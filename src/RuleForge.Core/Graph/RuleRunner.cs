using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.Core.Graph;

/// <summary>
/// Walks a rule's DAG and produces an envelope. Frame-aware: iterator nodes
/// fan out the downstream sub-graph N times with a new <see cref="IterationFrame"/>
/// pushed onto the runner's stack; merge nodes close the innermost open scope
/// and reduce per their <see cref="MergeMode"/>.
/// </summary>
public sealed class RuleRunner
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = AeroJson.Options;

    public sealed record Options(
        bool Debug = false,
        IRuleSource? SubRuleSource = null,
        IReferenceSetSource? ReferenceSetSource = null,
        Func<DateTimeOffset>? Clock = null,
        HttpClient? HttpClient = null);

    public Envelope Run(Rule rule, JsonElement request, bool debug = false) =>
        RunAsync(rule, request, new Options(Debug: debug)).GetAwaiter().GetResult();

    public Task<Envelope> RunAsync(
        Rule rule,
        JsonElement request,
        Options? options = null,
        CancellationToken ct = default)
    {
        options ??= new Options();
        return RunInternalAsync(rule, request, options, ct);
    }

    // ─── core run ──────────────────────────────────────────────────────────

    private async Task<Envelope> RunInternalAsync(
        Rule rule,
        JsonElement request,
        Options options,
        CancellationToken ct)
    {
        var version = rule.CurrentVersion > 0 ? rule.CurrentVersion : 1;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var totalSw = options.Debug ? Stopwatch.StartNew() : null;
        var trace = options.Debug ? new List<TraceEntry>() : null;

        ValidateGraph(rule);
        var graph = AnalyzeGraph(rule);

        var inputNode = rule.Nodes.Single(n => n.Data.Category == NodeCategory.Input);
        var outputNode = rule.Nodes.Single(n => n.Data.Category == NodeCategory.Output);

        var run = new RunState(graph, request, options, trace);
        run.Activate(inputNode.Id, FrameStack.Empty);

        while (run.Queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (nodeId, frames) = run.Queue.Dequeue();
            var key = run.Key(nodeId, frames);
            if (run.Fired.Contains(key)) continue;
            if (!run.Activated.Contains(key)) continue;

            var node = graph.Nodes[nodeId];

            // Wait for all in-this-scope upstream deps to fire before we run.
            if (!IsReady(node, frames, graph, run))
            {
                run.Queue.Enqueue((nodeId, frames));
                continue;
            }

            var nodeStarted = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            var ctxBefore = options.Debug ? new Dictionary<string, JsonElement>(run.Ctx) : null;
            string? subRuleRunId = null;
            JsonElement? subRuleResult = null;

            // Sub-rule call (with optional forEach) fires before the host node's logic.
            if (node.Data.SubRuleCall is { } call)
            {
                if (options.SubRuleSource is null)
                {
                    var msg = $"node '{node.Id}' has a subRuleCall but no SubRuleSource was configured for the run";
                    trace?.Add(new TraceEntry(node.Id, IsoUtc(nodeStarted), sw.ElapsedMilliseconds, TraceOutcome.Error, Error: msg));
                    return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
                }

                var subResult = await InvokeSubRuleAsync(call, request, run.Ctx, frames, options, ct);
                subRuleRunId = subResult.RunId;

                switch (subResult.Status)
                {
                    case SubRuleStatus.Ok:
                        ApplyOutputMapping(call.OutputMapping, subResult.Envelope!, run.Ctx, run.NodeOutputs, run.Key(node.Id, frames), frames);
                        subRuleResult = subResult.Envelope!.Result;
                        break;
                    case SubRuleStatus.OkArray:
                        // forEach path — accumulated array. Map already done inside Invoke.
                        subRuleResult = subResult.AccumulatedArray;
                        if (subRuleResult.HasValue)
                            run.NodeOutputs[run.Key(node.Id, frames)] = subRuleResult.Value;
                        break;
                    case SubRuleStatus.Default:
                        ApplyDefault(call, run.Ctx, run.NodeOutputs, run.Key(node.Id, frames), frames);
                        subRuleResult = call.DefaultValue;
                        break;
                    case SubRuleStatus.Skipped:
                        break;
                    case SubRuleStatus.Failed:
                        trace?.Add(new TraceEntry(
                            node.Id, IsoUtc(nodeStarted), sw.ElapsedMilliseconds, TraceOutcome.Error,
                            Error: subResult.Error, SubRuleRunId: subRuleRunId, CtxRead: ctxBefore));
                        return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
                }
            }

            // Execute the node itself
            Verdict verdict;
            JsonElement? nodeOutput;
            try
            {
                (verdict, nodeOutput) = await ExecuteNodeAsync(node, frames, subRuleResult, graph, run, options, ct);
            }
            catch (Exception e)
            {
                trace?.Add(new TraceEntry(
                    node.Id, IsoUtc(nodeStarted), sw.ElapsedMilliseconds, TraceOutcome.Error,
                    Error: e.Message, SubRuleRunId: subRuleRunId));
                return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
            }
            sw.Stop();

            run.Verdicts[key] = verdict;
            if (nodeOutput.HasValue) run.NodeOutputs[key] = nodeOutput.Value;
            run.Fired.Add(key);

            if (trace is not null)
            {
                Dictionary<string, JsonElement>? ctxWritten = null;
                if (ctxBefore is not null)
                {
                    foreach (var kv in run.Ctx)
                    {
                        if (!ctxBefore.TryGetValue(kv.Key, out var prev) || !JsonElementEquals(prev, kv.Value))
                            (ctxWritten ??= new())[kv.Key] = kv.Value;
                    }
                }
                trace.Add(new TraceEntry(
                    node.Id, IsoUtc(nodeStarted), sw.ElapsedMilliseconds, ToOutcome(verdict),
                    Output: nodeOutput,
                    CtxRead: ctxBefore is null || ctxBefore.Count == 0 ? null : ctxBefore,
                    CtxWritten: ctxWritten,
                    SubRuleRunId: subRuleRunId));
            }

            if (verdict == Verdict.Error)
                return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);

            // Route outgoing edges. Iterator nodes fan out at deeper frames;
            // when an outgoing edge targets a merge, the merge runs at the
            // popped frame stack (one level above this node).
            if (node.Data.Category == NodeCategory.Iterator)
            {
                FanOutIterator(node, frames, verdict, graph, run);
            }
            else
            {
                if (graph.Outgoing.TryGetValue(node.Id, out var outs))
                {
                    foreach (var edge in outs)
                    {
                        if (!EdgeRouter.Matches(verdict, edge.Branch)) continue;
                        var targetIsMerge = graph.Nodes[edge.Target].Data.Category == NodeCategory.Merge;
                        var targetFrames = targetIsMerge ? frames.Pop() : frames;
                        run.Activate(edge.Target, targetFrames);
                    }
                }
            }
        }

        // Output node assembly
        var outputKey = run.Key(outputNode.Id, FrameStack.Empty);
        if (!run.Fired.Contains(outputKey))
            return new Envelope(rule.Id, version, Decision.Skip, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);

        var result = AssembleResult(outputNode, graph, run);
        return new Envelope(rule.Id, version, Decision.Apply, IsoUtc(startedAtUtc), result, trace, totalSw?.ElapsedMilliseconds);
    }

    // ─── readiness ─────────────────────────────────────────────────────────

    private static bool IsReady(RuleNode node, FrameStack frames, GraphInfo graph, RunState run)
    {
        if (!graph.Incoming.TryGetValue(node.Id, out var inEdges)) return true;

        foreach (var edge in inEdges)
        {
            var src = graph.Nodes[edge.Source];

            // The iterator that opened this scope sits one frame above us. It
            // doesn't produce a typed output; its work was the fan-out. Skip
            // the readiness check for it.
            if (src.Data.Category == NodeCategory.Iterator)
            {
                continue;
            }

            // Merge node looks at upstream across the closed scope: not a
            // single (src, frames) but the union of (src, frames+[*]) for the
            // closed iterator's iterations.
            if (node.Data.Category == NodeCategory.Merge)
            {
                if (!run.AllInnerIterationsCompleteFor(node.Id, frames, graph)) return false;
                continue;
            }

            // Standard: upstream must have fired at the same frames. If the
            // edge wasn't activated for this frames (verdict didn't match
            // branch), this dep isn't pending — skip.
            var upKey = run.Key(edge.Source, frames);
            if (!run.Activated.Contains(upKey)) continue;
            if (!run.Fired.Contains(upKey)) return false;
        }
        return true;
    }

    // ─── iterator fan-out ──────────────────────────────────────────────────

    private void FanOutIterator(RuleNode node, FrameStack frames, Verdict verdict, GraphInfo graph, RunState run)
    {
        if (verdict != Verdict.Pass) return;

        var cfg = ParseConfig<IteratorConfig>(node, "iterator");
        if (string.IsNullOrEmpty(cfg.Source)) throw new InvalidOperationException($"iterator '{node.Id}' missing source");
        if (string.IsNullOrEmpty(cfg.As)) throw new InvalidOperationException($"iterator '{node.Id}' missing as");

        var arr = JsonPath.Resolve(run.Request, cfg.Source, frames.ToList())
                          .Where(e => e.HasValue && e.Value.ValueKind == JsonValueKind.Array)
                          .Select(e => e!.Value)
                          .FirstOrDefault();

        if (arr.ValueKind != JsonValueKind.Array)
        {
            // Source resolves to a single value, not an array. Treat as 1-item iteration.
            var singleResolved = JsonPath.Resolve(run.Request, cfg.Source, frames.ToList()).FirstOrDefault();
            if (!singleResolved.HasValue)
            {
                run.RecordIteratorCount(node.Id, frames, 0);
                return;
            }
            run.RecordIteratorCount(node.Id, frames, 1);
            var f = new IterationFrame(cfg.As, singleResolved.Value, 0, 1);
            var fStack = frames.Push(f);
            if (graph.Outgoing.TryGetValue(node.Id, out var outs0))
                foreach (var edge in outs0)
                    if (EdgeRouter.Matches(verdict, edge.Branch))
                        run.Activate(edge.Target, fStack);
            return;
        }

        var items = arr.EnumerateArray().ToList();
        run.RecordIteratorCount(node.Id, frames, items.Count);

        if (items.Count == 0)
        {
            // Empty source: no body iterations, but the closing merge still
            // needs to fire so the rule produces a sensible empty-array / 0 /
            // first-of-empty result.
            if (graph.IteratorClosingMerge.TryGetValue(node.Id, out var mergeId))
                run.Activate(mergeId, frames);
            return;
        }

        if (graph.Outgoing.TryGetValue(node.Id, out var outs))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var f = new IterationFrame(cfg.As, items[i], i, items.Count);
                var fStack = frames.Push(f);
                foreach (var edge in outs)
                {
                    if (!EdgeRouter.Matches(verdict, edge.Branch)) continue;
                    run.Activate(edge.Target, fStack);
                }
            }
        }
    }

    // ─── node execution ────────────────────────────────────────────────────

    private async Task<(Verdict, JsonElement?)> ExecuteNodeAsync(
        RuleNode node,
        FrameStack frames,
        JsonElement? subRuleResult,
        GraphInfo graph,
        RunState run,
        Options options,
        CancellationToken ct)
    {
        switch (node.Data.Category)
        {
            case NodeCategory.Input:
                return (Verdict.Pass, run.Request);

            case NodeCategory.Output:
                return (Verdict.Pass, null);

            case NodeCategory.Filter:
                return (ExecuteFilter(node, frames, run, options), null);

            case NodeCategory.Logic:
                return (ExecuteLogic(node, frames, graph, run), null);

            case NodeCategory.Constant:
                return (Verdict.Pass, ReadConfigField(node, "value"));

            case NodeCategory.Product:
                return (Verdict.Pass, ReadProductOutput(node, frames, subRuleResult, run));

            case NodeCategory.Mutator:
                return await ExecuteMutatorAsync(node, frames, graph, run, options, ct);

            case NodeCategory.Calc:
                return (Verdict.Pass, ExecuteCalc(node, frames, graph, run));

            case NodeCategory.Iterator:
                // Iterator emits a Pass to drive routing — fan-out happens in caller.
                return (Verdict.Pass, null);

            case NodeCategory.Merge:
                return (Verdict.Pass, ExecuteMerge(node, frames, graph, run));

            case NodeCategory.Reference:
                return await ExecuteReferenceAsync(node, frames, run, options, ct);

            case NodeCategory.RuleRef:
                return (Verdict.Pass, subRuleResult);

            case NodeCategory.Api:
                return await ExecuteApiAsync(node, frames, run, options, ct);

            case NodeCategory.Bucket:
                return (Verdict.Pass, ExecuteBucket(node, frames, run));

            case NodeCategory.Assert:
                return (Verdict.Pass, ExecuteAssert(node, frames, graph, run));

            default:
                throw new NotSupportedException(
                    $"node category '{node.Data.Category}' is not implemented (node {node.Id})");
        }
    }

    // ─── filter ────────────────────────────────────────────────────────────

    private Verdict ExecuteFilter(RuleNode node, FrameStack frames, RunState run, Options options)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException(
                $"filter node '{node.Id}' has no config. AERO admin must inline the structured filter config before publish.");
        var raw = node.Data.Config.Value;
        EnsureStructuredFilterShape(node.Id, raw);

        // For frame-aware paths inside the filter source, we resolve the path
        // ourselves (with the runner's frame stack) and feed a literal-source
        // shape into the underlying evaluator.
        var fctx = BuildFilterContext(run, frames);
        var kind = ClassifyFilter(node, raw);
        try
        {
            switch (kind)
            {
                case FilterKind.Number:
                {
                    var cfg = raw.Deserialize<NumberFilterConfig>(ConfigJsonOptions)!;
                    cfg = ResolveSourceForFrames(cfg, run, frames);
                    return NumberFilterEvaluator.Evaluate(cfg, fctx).Verdict;
                }
                case FilterKind.Date:
                {
                    var cfg = raw.Deserialize<DateFilterConfig>(ConfigJsonOptions)!;
                    cfg = ResolveSourceForFrames(cfg, run, frames);
                    return DateFilterEvaluator.Evaluate(cfg, fctx, options.Clock).Verdict;
                }
                default:
                {
                    var cfg = raw.Deserialize<StringFilterConfig>(ConfigJsonOptions)!;
                    cfg = ResolveSourceForFrames(cfg, run, frames);
                    return StringFilterEvaluator.Evaluate(cfg, fctx).Verdict;
                }
            }
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"filter node '{node.Id}' config ({kind}) could not be parsed: {e.Message}", e);
        }
    }

    private StringFilterEvaluator.Context BuildFilterContext(RunState run, FrameStack frames)
    {
        // The filter evaluators don't know about iteration frames natively.
        // We pre-resolve any frame-rooted path (`$pax.tier`) by rewriting the
        // source to `literal` with the resolved value before handing off.
        // Context (`$ctx`) is unchanged.
        var ctxEl = run.Ctx.Count == 0 ? (JsonElement?)null : DictToJsonElement(run.Ctx);
        return new StringFilterEvaluator.Context(run.Request, ctxEl);
    }

    private StringFilterConfig ResolveSourceForFrames(StringFilterConfig cfg, RunState run, FrameStack frames)
    {
        if (cfg.Source.Kind != SourceKind.Request || string.IsNullOrEmpty(cfg.Source.Path)) return cfg;
        if (frames.Count == 0) return cfg;
        if (!cfg.Source.Path!.StartsWith('$') || cfg.Source.Path.Length < 2) return cfg;
        if (cfg.Source.Path[1] is '.' or '[') return cfg;

        // Path starts with $<name> — pre-resolve against the frame stack.
        var resolved = JsonPath.Resolve(run.Request, cfg.Source.Path, frames.ToList()).FirstOrDefault();
        if (!resolved.HasValue) return cfg;
        // Substitute the source: replace the frame-rooted path with a one-shot
        // request literal at a synthetic key.
        return cfg with { Source = new StringFilterSource(SourceKind.Literal, Literal: ToScalarString(resolved.Value)) };
    }

    private NumberFilterConfig ResolveSourceForFrames(NumberFilterConfig cfg, RunState run, FrameStack frames)
    {
        if (cfg.Source.Kind != SourceKind.Request || string.IsNullOrEmpty(cfg.Source.Path)) return cfg;
        if (frames.Count == 0) return cfg;
        if (!cfg.Source.Path!.StartsWith('$') || cfg.Source.Path.Length < 2) return cfg;
        if (cfg.Source.Path[1] is '.' or '[') return cfg;

        var resolved = JsonPath.Resolve(run.Request, cfg.Source.Path, frames.ToList()).FirstOrDefault();
        if (!resolved.HasValue) return cfg;
        if (resolved.Value.ValueKind == JsonValueKind.Number && resolved.Value.TryGetDouble(out var d))
            return cfg with { Source = new NumberFilterSource(SourceKind.Literal, Literal: d) };
        if (resolved.Value.ValueKind == JsonValueKind.String &&
            double.TryParse(resolved.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
            return cfg with { Source = new NumberFilterSource(SourceKind.Literal, Literal: ds) };
        return cfg;
    }

    private DateFilterConfig ResolveSourceForFrames(DateFilterConfig cfg, RunState run, FrameStack frames)
    {
        if (cfg.Source.Kind != SourceKind.Request || string.IsNullOrEmpty(cfg.Source.Path)) return cfg;
        if (frames.Count == 0) return cfg;
        if (!cfg.Source.Path!.StartsWith('$') || cfg.Source.Path.Length < 2) return cfg;
        if (cfg.Source.Path[1] is '.' or '[') return cfg;

        var resolved = JsonPath.Resolve(run.Request, cfg.Source.Path, frames.ToList()).FirstOrDefault();
        if (!resolved.HasValue) return cfg;
        if (resolved.Value.ValueKind == JsonValueKind.String)
            return cfg with { Source = new DateFilterSource(SourceKind.Literal, Literal: resolved.Value.GetString()) };
        return cfg;
    }

    private static string? ToScalarString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private enum FilterKind { String, Number, Date }

    private static FilterKind ClassifyFilter(RuleNode node, JsonElement config)
    {
        var t = (node.Data.TemplateId ?? string.Empty).ToLowerInvariant();
        if (t.Contains("filter-date") || t.Contains("date")) return FilterKind.Date;
        if (t.Contains("filter-num") || t.Contains("number")) return FilterKind.Number;
        if (t.Contains("filter-str") || t.Contains("string")) return FilterKind.String;

        if (config.TryGetProperty("compare", out var cmp) && cmp.ValueKind == JsonValueKind.Object)
        {
            if (cmp.TryGetProperty("granularity", out _)) return FilterKind.Date;
            if (cmp.TryGetProperty("unit", out _)) return FilterKind.Date;
            if (cmp.TryGetProperty("operator", out var op) && op.ValueKind == JsonValueKind.String)
            {
                var opName = op.GetString() ?? string.Empty;
                if (opName is "before" or "after" or "within_last" or "within_next") return FilterKind.Date;
                if (opName is "gt" or "gte" or "lt" or "lte" or "between" or "not_between") return FilterKind.Number;
            }
        }
        return FilterKind.String;
    }

    // ─── logic ─────────────────────────────────────────────────────────────

    private static Verdict ExecuteLogic(RuleNode node, FrameStack frames, GraphInfo graph, RunState run)
    {
        var op = LogicEvaluator.Parse(node.Data.TemplateId, node.Data.Label);
        var inEdges = graph.Incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var inputs = inEdges
            .Select(e => e.Source)
            .Distinct()
            .Select(src => run.Verdicts.TryGetValue(run.Key(src, frames), out var v) ? v : Verdict.Skip)
            .ToList();
        return LogicEvaluator.Apply(op, inputs);
    }

    // ─── product / constant ────────────────────────────────────────────────

    private static JsonElement? ReadConfigField(RuleNode node, string field)
    {
        if (node.Data.Config is null) return null;
        var cfg = node.Data.Config.Value;
        return cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty(field, out var v) ? v : null;
    }

    private static JsonElement? ReadProductOutput(RuleNode node, FrameStack frames, JsonElement? subRuleResult, RunState run)
    {
        if (node.Data.Config is { } cfg && cfg.ValueKind == JsonValueKind.Object)
        {
            if (cfg.TryGetProperty("output", out var direct))
                return ResolveCtxPlaceholders(direct, run.Ctx, run.Request, frames);
            if (cfg.TryGetProperty("outputSchema", out var schema) && schema.ValueKind == JsonValueKind.Array)
                return ResolveCtxPlaceholders(SchemaArrayToObject(schema), run.Ctx, run.Request, frames);
        }
        return subRuleResult;
    }

    private static JsonElement SchemaArrayToObject(JsonElement schemaArray)
    {
        var obj = new JsonObject();
        foreach (var field in schemaArray.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object) continue;
            if (!field.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
            var key = keyEl.GetString()!;
            if (field.TryGetProperty("value", out var v))
                obj[key] = JsonNode.Parse(v.GetRawText());
        }
        return JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    private static JsonElement ResolveCtxPlaceholders(
        JsonElement input,
        IDictionary<string, JsonElement> ctx,
        JsonElement request,
        FrameStack frames)
    {
        if (!ContainsPlaceholder(input)) return input;
        var node = JsonNode.Parse(input.GetRawText());
        Replace(node);
        return JsonDocument.Parse(node!.ToJsonString()).RootElement;

        void Replace(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        var child = obj[key];
                        if (child is JsonValue v && v.TryGetValue(out string? s) && IsPlaceholder(s, out var path, out var rooted))
                        {
                            var resolved = ResolvePath(path, rooted);
                            obj[key] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
                        }
                        else
                        {
                            Replace(child);
                        }
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is JsonValue v && v.TryGetValue(out string? s) && IsPlaceholder(s, out var path, out var rooted))
                        {
                            var resolved = ResolvePath(path, rooted);
                            arr[i] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
                        }
                        else
                        {
                            Replace(child);
                        }
                    }
                    break;
            }
        }

        JsonElement? ResolvePath(string path, PlaceholderRoot rooted) => rooted switch
        {
            PlaceholderRoot.Ctx     => JsonPath.Resolve(DictToJsonElement(ctx), "$" + path).FirstOrDefault(),
            PlaceholderRoot.Frame   => JsonPath.Resolve(request, path, frames.ToList()).FirstOrDefault(),
            PlaceholderRoot.Request => JsonPath.Resolve(request, "$" + path).FirstOrDefault(),
            _                       => null,
        };
    }

    private enum PlaceholderRoot { Ctx, Frame, Request }

    private static bool ContainsPlaceholder(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => IsPlaceholder(el.GetString(), out _, out _),
            JsonValueKind.Object => el.EnumerateObject().Any(p => ContainsPlaceholder(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Any(ContainsPlaceholder),
            _ => false,
        };
    }

    private static bool IsPlaceholder(string? s, out string path, out PlaceholderRoot root)
    {
        path = string.Empty; root = PlaceholderRoot.Ctx;
        if (string.IsNullOrEmpty(s)) return false;
        if (!s.StartsWith("${") || !s.EndsWith("}")) return false;
        var inner = s.Substring(2, s.Length - 3);

        if (inner.StartsWith("ctx.")) { path = "." + inner.Substring("ctx.".Length); root = PlaceholderRoot.Ctx; return true; }
        if (inner.StartsWith("$"))
        {
            // ${$pax.id} — frame-rooted
            path = inner;
            root = PlaceholderRoot.Frame;
            return true;
        }
        // Plain "${field.x}" — treated as request-rooted
        path = "." + inner;
        root = PlaceholderRoot.Request;
        return true;
    }

    // ─── mutator ───────────────────────────────────────────────────────────

    private async Task<(Verdict, JsonElement?)> ExecuteMutatorAsync(
        RuleNode node, FrameStack frames, GraphInfo graph, RunState run, Options options, CancellationToken ct)
    {
        if (node.Data.Config is null) throw new InvalidOperationException($"mutator '{node.Id}' has no config");
        var cfg = ParseConfig<MutatorConfig>(node, "mutator");
        if (string.IsNullOrEmpty(cfg.Target))
            throw new InvalidOperationException($"mutator '{node.Id}' missing target");

        // Find single upstream output at THIS frame.
        var inEdges = graph.Incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var upstream = inEdges
            .Select(e => e.Source).Distinct()
            .Where(s => run.NodeOutputs.ContainsKey(run.Key(s, frames)))
            .Select(s => run.NodeOutputs[run.Key(s, frames)])
            .ToList();
        if (upstream.Count > 1)
            throw new InvalidOperationException($"mutator '{node.Id}' has {upstream.Count} upstream outputs — exactly one required");

        var baseObj = upstream.Count == 1 && upstream[0].ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(upstream[0].GetRawText())!.AsObject()
            : new JsonObject();

        JsonElement? newValue;
        if (cfg.Lookup is not null)
        {
            if (options.ReferenceSetSource is null)
                throw new InvalidOperationException($"mutator '{node.Id}' uses lookup but no ReferenceSetSource configured");

            var refSet = await options.ReferenceSetSource.GetByIdAsync(cfg.Lookup.ReferenceId, ct)
                         ?? throw new InvalidOperationException($"mutator '{node.Id}': reference set '{cfg.Lookup.ReferenceId}' not found");
            newValue = LookupRefSet(refSet, cfg.Lookup, run.Request, run.Ctx, frames);
        }
        else if (cfg.From is not null)
        {
            newValue = ResolveFromPath(cfg.From, run.Request, run.Ctx, frames);
        }
        else if (cfg.Value.HasValue)
        {
            newValue = cfg.Value.Value;
        }
        else
        {
            throw new InvalidOperationException($"mutator '{node.Id}' must specify value, from, or lookup");
        }

        if (!newValue.HasValue)
        {
            switch (cfg.OnMissing)
            {
                case OnLookupMissing.Leave: break;
                case OnLookupMissing.Clear: baseObj[cfg.Target] = null; break;
                case OnLookupMissing.Error: return (Verdict.Error, null);
            }
        }
        else
        {
            baseObj[cfg.Target] = JsonNode.Parse(newValue.Value.GetRawText());
        }
        return (Verdict.Pass, JsonDocument.Parse(baseObj.ToJsonString()).RootElement);
    }

    private static JsonElement? LookupRefSet(
        ReferenceSet refSet, LookupSpec spec,
        JsonElement request, IDictionary<string, JsonElement> ctx, FrameStack frames)
    {
        var match = new Dictionary<string, JsonElement?>(spec.MatchOn.Count);
        foreach (var kv in spec.MatchOn)
            match[kv.Key] = ResolveFromPath(kv.Value, request, ctx, frames);

        foreach (var row in refSet.Rows)
        {
            var allMatch = true;
            foreach (var (col, expected) in match)
            {
                if (!row.TryGetValue(col, out var actual)) { allMatch = false; break; }
                if (!expected.HasValue || !RefValueEquals(actual, expected.Value)) { allMatch = false; break; }
            }
            if (allMatch && row.TryGetValue(spec.ValueColumn, out var v)) return v;
        }
        return null;
    }

    private static bool RefValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String) return a.GetString() == b.GetString();
        return a.GetRawText() == b.GetRawText();
    }

    private static JsonElement? ResolveFromPath(
        string path, JsonElement request, IDictionary<string, JsonElement> ctx, FrameStack frames)
    {
        if (path.StartsWith("$ctx."))
        {
            if (ctx.Count == 0) return null;
            return JsonPath.Resolve(DictToJsonElement(ctx), path).FirstOrDefault();
        }
        return JsonPath.Resolve(request, path, frames.ToList()).FirstOrDefault();
    }

    // ─── calc ──────────────────────────────────────────────────────────────

    private static JsonElement? ExecuteCalc(RuleNode node, FrameStack frames, GraphInfo graph, RunState run)
    {
        if (node.Data.Config is null) throw new InvalidOperationException($"calc '{node.Id}' has no config");
        var cfg = ParseConfig<CalcConfig>(node, "calc");
        if (string.IsNullOrEmpty(cfg.Expression)) throw new InvalidOperationException($"calc '{node.Id}' missing expression");

        var inEdges = graph.Incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var upstream = inEdges.Select(e => e.Source).Distinct()
            .Where(s => run.NodeOutputs.ContainsKey(run.Key(s, frames)))
            .Select(s => run.NodeOutputs[run.Key(s, frames)])
            .ToList();
        if (upstream.Count > 1) throw new InvalidOperationException($"calc '{node.Id}' has {upstream.Count} upstream outputs — exactly one required");

        var upstreamEl = upstream.Count == 1 ? upstream[0] : (JsonElement?)null;

        // Calc evaluator already supports upstream + ctx + request namespaces.
        // Frame-rooted variables ($pax) are resolved here as a custom variable
        // resolver: if the upstream/ctx/request lookup misses, try the frame stack.
        var computed = CalcEvaluator.Evaluate(cfg.Expression, upstreamEl, run.Ctx, run.Request, frames.ToList());

        if (string.IsNullOrEmpty(cfg.Target)) return computed;

        var baseObj = upstreamEl is { ValueKind: JsonValueKind.Object } u
            ? JsonNode.Parse(u.GetRawText())!.AsObject()
            : new JsonObject();
        baseObj[cfg.Target] = computed.HasValue ? JsonNode.Parse(computed.Value.GetRawText()) : null;
        return JsonDocument.Parse(baseObj.ToJsonString()).RootElement;
    }

    // ─── reference (multi-row lookup) ──────────────────────────────────────

    private async Task<(Verdict, JsonElement?)> ExecuteReferenceAsync(
        RuleNode node, FrameStack frames, RunState run, Options options, CancellationToken ct)
    {
        if (node.Data.Config is null) throw new InvalidOperationException($"reference '{node.Id}' has no config");
        if (options.ReferenceSetSource is null)
            throw new InvalidOperationException($"reference '{node.Id}' has no ReferenceSetSource configured");

        var cfg = ParseConfig<ReferenceConfig>(node, "reference");
        if (string.IsNullOrEmpty(cfg.ReferenceId))
            throw new InvalidOperationException($"reference '{node.Id}' missing referenceId");

        var refSet = await options.ReferenceSetSource.GetByIdAsync(cfg.ReferenceId, ct)
                     ?? throw new InvalidOperationException($"reference '{node.Id}': set '{cfg.ReferenceId}' not found");

        var match = new Dictionary<string, JsonElement?>();
        if (cfg.MatchOn is not null)
        {
            foreach (var kv in cfg.MatchOn)
                match[kv.Key] = ResolveFromPath(kv.Value, run.Request, run.Ctx, frames);
        }

        var rows = new JsonArray();
        foreach (var row in refSet.Rows)
        {
            var allMatch = true;
            foreach (var (col, expected) in match)
            {
                if (!row.TryGetValue(col, out var actual)) { allMatch = false; break; }
                if (!expected.HasValue || !RefValueEquals(actual, expected.Value)) { allMatch = false; break; }
            }
            if (!allMatch) continue;
            var rowObj = new JsonObject();
            foreach (var (k, v) in row) rowObj[k] = JsonNode.Parse(v.GetRawText());
            rows.Add(rowObj);
        }

        return (Verdict.Pass, JsonDocument.Parse(rows.ToJsonString()).RootElement);
    }

    // ─── api (outbound HTTP) ───────────────────────────────────────────────

    private static async Task<(Verdict, JsonElement?)> ExecuteApiAsync(
        RuleNode node, FrameStack frames, RunState run, Options options, CancellationToken ct)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException($"api '{node.Id}' has no config");
        if (options.HttpClient is null)
            throw new InvalidOperationException(
                $"api '{node.Id}': no HttpClient on Options. The host must inject one.");

        var cfg = ParseConfig<ApiConfig>(node, "api");
        if (string.IsNullOrEmpty(cfg.Url))
            throw new InvalidOperationException($"api '{node.Id}' missing url");
        if (string.IsNullOrEmpty(cfg.Method))
            throw new InvalidOperationException($"api '{node.Id}' missing method");
        if (cfg.TimeoutMs <= 0)
            throw new InvalidOperationException(
                $"api '{node.Id}' timeoutMs must be > 0 (got {cfg.TimeoutMs})");

        var url = ResolveStringField(cfg.Url, run, frames)
                  ?? throw new InvalidOperationException($"api '{node.Id}': url resolved to null");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"api '{node.Id}': url '{url}' is not a valid absolute URI");

        using var req = new HttpRequestMessage(new HttpMethod(cfg.Method.ToUpperInvariant()), uri);

        string contentType = "application/json";
        if (cfg.Headers is not null)
        {
            foreach (var (name, raw) in cfg.Headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = ResolveStringField(raw, run, frames) ?? contentType;
                    continue;   // applied to Content below if a body is present
                }
                var v = ResolveStringField(raw, run, frames);
                if (v is null) continue;
                req.Headers.TryAddWithoutValidation(name, v);
            }
        }

        if (cfg.Body.HasValue)
        {
            var resolvedBody = ResolveCtxPlaceholders(cfg.Body.Value, run.Ctx, run.Request, frames);
            req.Content = new StringContent(resolvedBody.GetRawText(), Encoding.UTF8, contentType);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(cfg.TimeoutMs);

        HttpResponseMessage resp;
        try
        {
            resp = await options.HttpClient.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"api '{node.Id}': request to {uri} timed out after {cfg.TimeoutMs}ms");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"api '{node.Id}': {req.Method} {uri} returned {(int)resp.StatusCode} {resp.ReasonPhrase}");

            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrEmpty(raw))
                return (Verdict.Pass, JsonDocument.Parse("null").RootElement);

            JsonElement body;
            try
            {
                body = JsonDocument.Parse(raw).RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new InvalidOperationException(
                    $"api '{node.Id}': response body is not valid JSON: {e.Message}", e);
            }

            if (string.IsNullOrEmpty(cfg.ResponseMap))
                return (Verdict.Pass, body);

            var mapped = JsonPath.Resolve(body, cfg.ResponseMap).FirstOrDefault();
            return (Verdict.Pass, mapped);
        }
    }

    /// <summary>
    /// Resolve a string config field. Values starting with <c>$</c> are JSONPaths
    /// (against request, <c>$ctx.x</c>, or open iteration frames); other values
    /// are literals. Non-string resolved values are stringified.
    /// </summary>
    private static string? ResolveStringField(string raw, RunState run, FrameStack frames)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (raw[0] != '$') return raw;
        var resolved = ResolveFromPath(raw, run.Request, run.Ctx, frames);
        if (!resolved.HasValue) return null;
        return resolved.Value.ValueKind switch
        {
            JsonValueKind.String => resolved.Value.GetString(),
            JsonValueKind.Number => resolved.Value.ToString(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            JsonValueKind.Null   => null,
            _                    => resolved.Value.GetRawText(),
        };
    }

    // ─── assert (invariant guard) ──────────────────────────────────────────

    private static JsonElement? ExecuteAssert(
        RuleNode node, FrameStack frames, GraphInfo graph, RunState run)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException($"assert '{node.Id}' has no config");
        var cfg = ParseConfig<AssertConfig>(node, "assert");
        if (string.IsNullOrEmpty(cfg.Condition))
            throw new InvalidOperationException($"assert '{node.Id}' missing condition");

        var inEdges = graph.Incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var upstream = inEdges.Select(e => e.Source).Distinct()
            .Where(s => run.NodeOutputs.ContainsKey(run.Key(s, frames)))
            .Select(s => run.NodeOutputs[run.Key(s, frames)])
            .ToList();
        var upstreamEl = upstream.Count == 1 ? upstream[0] : (JsonElement?)null;

        var result = CalcEvaluator.Evaluate(cfg.Condition, upstreamEl, run.Ctx, run.Request, frames.ToList());
        if (IsTruthy(result))
            return upstreamEl;   // pass-through on success

        var code = string.IsNullOrEmpty(cfg.ErrorCode) ? "ASSERT_FAILED" : cfg.ErrorCode;
        var msg = string.IsNullOrEmpty(cfg.ErrorMessage) ? cfg.Condition : cfg.ErrorMessage;
        throw new InvalidOperationException($"assert '{node.Id}' [{code}]: {msg}");
    }

    private static bool IsTruthy(JsonElement? el)
    {
        if (!el.HasValue) return false;
        return el.Value.ValueKind switch
        {
            JsonValueKind.True      => true,
            JsonValueKind.False     => false,
            JsonValueKind.Number    => el.Value.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String    => !string.IsNullOrEmpty(el.Value.GetString()),
            JsonValueKind.Null      => false,
            JsonValueKind.Undefined => false,
            _                       => true,    // objects / arrays are truthy
        };
    }

    // ─── bucket (deterministic A/B sticky-hash) ────────────────────────────

    private static JsonElement? ExecuteBucket(RuleNode node, FrameStack frames, RunState run)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException($"bucket '{node.Id}' has no config");

        var cfg = ParseConfig<BucketConfig>(node, "bucket");
        if (string.IsNullOrEmpty(cfg.HashKey))
            throw new InvalidOperationException($"bucket '{node.Id}' missing hashKey");
        if (cfg.Buckets is null || cfg.Buckets.Count == 0)
            throw new InvalidOperationException($"bucket '{node.Id}' has no buckets");

        var keyEl = ResolveFromPath(cfg.HashKey, run.Request, run.Ctx, frames);
        if (!keyEl.HasValue)
            throw new InvalidOperationException(
                $"bucket '{node.Id}': hashKey '{cfg.HashKey}' resolved to no value");

        var keyStr = keyEl.Value.ValueKind switch
        {
            JsonValueKind.String => keyEl.Value.GetString() ?? "",
            JsonValueKind.Null   => "",
            _                    => keyEl.Value.GetRawText(),
        };

        long totalWeight = 0;
        foreach (var b in cfg.Buckets)
        {
            if (b.Weight < 0)
                throw new InvalidOperationException(
                    $"bucket '{node.Id}': bucket '{b.Name}' has negative weight {b.Weight}");
            totalWeight += b.Weight;
        }
        if (totalWeight <= 0)
            throw new InvalidOperationException(
                $"bucket '{node.Id}': total weight must be > 0");

        var hash = Fnv1a32(keyStr);
        var pick = (long)(hash % (uint)totalWeight);
        long cumulative = 0;
        foreach (var b in cfg.Buckets)
        {
            if (b.Weight <= 0) continue;
            cumulative += b.Weight;
            if (pick < cumulative)
                return JsonDocument.Parse(JsonSerializer.Serialize(b.Name)).RootElement;
        }
        // Unreachable under the bounds above.
        throw new InvalidOperationException(
            $"bucket '{node.Id}': bucket selection failed (logic error)");
    }

    /// <summary>
    /// FNV-1a 32-bit hash over UTF-8 bytes. Stable across versions and
    /// platforms — bucket assignment must be reproducible for the same key
    /// across engine restarts and deployments.
    /// </summary>
    private static uint Fnv1a32(string s)
    {
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        uint h = offset;
        var bytes = Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            h ^= b;
            h *= prime;
        }
        return h;
    }

    // ─── merge ─────────────────────────────────────────────────────────────

    private static JsonElement? ExecuteMerge(RuleNode node, FrameStack frames, GraphInfo graph, RunState run)
    {
        var cfg = node.Data.Config is null
            ? new MergeConfig(MergeMode.Collect)
            : ParseConfig<MergeConfig>(node, "merge");

        // Find which iterator this merge closes (innermost open at merge's location).
        var iterId = graph.MergeClosesIterator.GetValueOrDefault(node.Id);
        if (iterId is null) return JsonDocument.Parse("[]").RootElement;

        var iterCount = run.GetIteratorCount(iterId, frames);
        if (iterCount == 0) return cfg.Mode == MergeMode.Collect
            ? JsonDocument.Parse("[]").RootElement
            : (JsonElement?)null;

        // Collect upstream outputs at frames + [each iter frame] for each iteration.
        var inEdges = graph.Incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var iterName = (graph.Nodes[iterId].Data.Config?.Deserialize<IteratorConfig>(ConfigJsonOptions))?.As ?? "iter";
        var values = new List<JsonElement>();
        for (var i = 0; i < iterCount; i++)
        {
            var iterFrame = new IterationFrame(iterName, default, i, iterCount);
            var childFrames = frames.PushIndexOnly(iterFrame);
            foreach (var src in inEdges.Select(e => e.Source).Distinct())
            {
                if (run.NodeOutputs.TryGetValue(run.Key(src, childFrames), out var val))
                    values.Add(val);
            }
        }

        return cfg.Mode switch
        {
            MergeMode.Collect => MakeArray(values),
            MergeMode.Count   => MakeNumber(values.Count),
            MergeMode.Sum     => MakeNumber(values.Sum(v => GetField(v, cfg.Field))),
            MergeMode.Avg     => MakeNumber(values.Count == 0 ? 0 : values.Average(v => GetField(v, cfg.Field))),
            MergeMode.Min     => values.Count == 0 ? MakeNumber(0) : MakeNumber(values.Min(v => GetField(v, cfg.Field))),
            MergeMode.Max     => values.Count == 0 ? MakeNumber(0) : MakeNumber(values.Max(v => GetField(v, cfg.Field))),
            MergeMode.First   => values.FirstOrDefault(),
            MergeMode.Last    => values.LastOrDefault(),
            _                 => MakeArray(values),
        };
    }

    private static double GetField(JsonElement v, string? field)
    {
        if (string.IsNullOrEmpty(field) || field == "$" || field == "$.")
        {
            return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        }
        var resolved = JsonPath.Resolve(v, field).FirstOrDefault();
        if (!resolved.HasValue) return 0;
        return resolved.Value.ValueKind == JsonValueKind.Number ? resolved.Value.GetDouble() : 0;
    }

    private static JsonElement MakeArray(IEnumerable<JsonElement> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(JsonNode.Parse(v.GetRawText()));
        return JsonDocument.Parse(arr.ToJsonString()).RootElement;
    }

    private static JsonElement MakeNumber(double d) =>
        JsonDocument.Parse(d.ToString("R", CultureInfo.InvariantCulture)).RootElement;

    // ─── output assembly ───────────────────────────────────────────────────

    private static JsonElement? AssembleResult(RuleNode outputNode, GraphInfo graph, RunState run)
    {
        if (outputNode.Data.Config is { ValueKind: JsonValueKind.Object } cfg &&
            cfg.TryGetProperty("result", out var literal))
        {
            return ResolveCtxPlaceholders(literal, run.Ctx, run.Request, FrameStack.Empty);
        }

        var inEdges = graph.Incoming.GetValueOrDefault(outputNode.Id) ?? new List<RuleEdge>();

        // Collect upstream outputs at the output node's level (which is always
        // empty frame stack — nothing iterates past the merge that closes it).
        var sources = inEdges.Select(e => e.Source).Distinct()
            .Where(s => run.NodeOutputs.ContainsKey(run.Key(s, FrameStack.Empty)))
            .Select(s => run.NodeOutputs[run.Key(s, FrameStack.Empty)])
            .ToList();

        if (sources.Count == 0) return null;
        if (sources.Count == 1) return ResolveCtxPlaceholders(sources[0], run.Ctx, run.Request, FrameStack.Empty);

        var merged = new JsonObject();
        foreach (var src in sources)
        {
            if (src.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in src.EnumerateObject())
                merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        return ResolveCtxPlaceholders(JsonDocument.Parse(merged.ToJsonString()).RootElement, run.Ctx, run.Request, FrameStack.Empty);
    }

    // ─── sub-rule call ─────────────────────────────────────────────────────

    private enum SubRuleStatus { Ok, OkArray, Default, Skipped, Failed }

    private sealed record SubRuleResult(SubRuleStatus Status, Envelope? Envelope, string? RunId, string? Error, JsonElement? AccumulatedArray = null);

    private async Task<SubRuleResult> InvokeSubRuleAsync(
        SubRuleCall call, JsonElement parentRequest, IDictionary<string, JsonElement> parentCtx,
        FrameStack frames, Options options, CancellationToken ct)
    {
        try
        {
            var subRule = await options.SubRuleSource!.GetByIdAsync(call.RuleId, ResolveVersion(call.PinnedVersion), ct);
            if (subRule is null)
                throw new InvalidOperationException($"subRuleCall: rule '{call.RuleId}' not found");

            // No forEach — single invocation path
            if (string.IsNullOrEmpty(call.ForEach))
            {
                var subRequest = BuildSubRequest(call.InputMapping, parentRequest, parentCtx, frames, asName: null, item: null, index: 0, count: 1);
                var subEnvelope = await RunInternalAsync(subRule, subRequest, options with { }, ct);
                var runId = $"srr-{call.RuleId}-{Guid.NewGuid():N}";
                return subEnvelope.Decision switch
                {
                    Decision.Apply => new SubRuleResult(SubRuleStatus.Ok, subEnvelope, runId, null),
                    Decision.Skip  => HandleSubRuleError(call, runId, "sub-rule decided to skip"),
                    Decision.Error => HandleSubRuleError(call, runId, subEnvelope.Trace?.LastOrDefault()?.Error ?? "sub-rule errored"),
                    _              => HandleSubRuleError(call, runId, $"unknown decision {subEnvelope.Decision}"),
                };
            }

            // forEach path — fan out per element
            var arrEl = JsonPath.Resolve(parentRequest, call.ForEach, frames.ToList()).FirstOrDefault();
            var items = arrEl.HasValue && arrEl.Value.ValueKind == JsonValueKind.Array
                ? arrEl.Value.EnumerateArray().ToList()
                : new List<JsonElement>();
            var asName = call.As ?? "item";

            var accumulator = new JsonArray();
            for (var i = 0; i < items.Count; i++)
            {
                var subReq = BuildSubRequest(call.InputMapping, parentRequest, parentCtx, frames, asName, items[i], i, items.Count);
                var subEnv = await RunInternalAsync(subRule, subReq, options with { }, ct);
                if (subEnv.Decision == Decision.Apply)
                {
                    // Build per-iteration mapped object
                    var perIter = MapToObject(call.OutputMapping, subEnv);
                    if (perIter is not null) accumulator.Add(JsonNode.Parse(perIter.Value.GetRawText()));
                }
                else if (call.OnError == SubRuleErrorMode.Default && call.DefaultValue.HasValue)
                {
                    var fakeEnv = new Envelope("(default)", 0, Decision.Apply, "", call.DefaultValue, null, null);
                    var perIter = MapToObject(call.OutputMapping, fakeEnv);
                    if (perIter is not null) accumulator.Add(JsonNode.Parse(perIter.Value.GetRawText()));
                }
                else if (call.OnError == SubRuleErrorMode.Fail)
                {
                    return HandleSubRuleError(call, null, $"sub-rule iter {i} failed");
                }
                // Skip onError — just don't append
            }
            var arr = JsonDocument.Parse(accumulator.ToJsonString()).RootElement;
            return new SubRuleResult(SubRuleStatus.OkArray, null, $"srr-{call.RuleId}-{Guid.NewGuid():N}", null, arr);
        }
        catch (Exception e)
        {
            return HandleSubRuleError(call, null, e.Message);
        }
    }

    private static JsonElement? MapToObject(IReadOnlyDictionary<string, string> outputMapping, Envelope env)
    {
        var envelopeRoot = JsonSerializer.SerializeToElement(env, AeroJson.Options);
        var obj = new JsonObject();
        var any = false;
        foreach (var kv in outputMapping)
        {
            if (kv.Key.StartsWith("ctx.")) continue; // ctx writes don't fit the per-iter array model
            var rooted = kv.Value.StartsWith("$.") || kv.Value.StartsWith("$") ? kv.Value : "$." + kv.Value;
            var resolved = JsonPath.Resolve(envelopeRoot, rooted).FirstOrDefault();
            if (resolved.HasValue) { obj[kv.Key] = JsonNode.Parse(resolved.Value.GetRawText()); any = true; }
        }
        return any ? JsonDocument.Parse(obj.ToJsonString()).RootElement : (JsonElement?)null;
    }

    private static SubRuleResult HandleSubRuleError(SubRuleCall call, string? runId, string message) =>
        call.OnError switch
        {
            SubRuleErrorMode.Skip    => new SubRuleResult(SubRuleStatus.Skipped, null, runId, message),
            SubRuleErrorMode.Default => new SubRuleResult(SubRuleStatus.Default, null, runId, message),
            _                        => new SubRuleResult(SubRuleStatus.Failed, null, runId, message),
        };

    private static int? ResolveVersion(JsonElement pinned) =>
        pinned.ValueKind == JsonValueKind.Number && pinned.TryGetInt32(out var v) ? v : null;

    private static JsonElement BuildSubRequest(
        IReadOnlyDictionary<string, string> inputMapping,
        JsonElement parentRequest, IDictionary<string, JsonElement> parentCtx,
        FrameStack frames, string? asName, JsonElement? item, int index, int count)
    {
        var obj = new JsonObject();
        foreach (var kv in inputMapping)
        {
            var key = kv.Key; var path = kv.Value;
            JsonElement? resolved;

            if (asName is not null && item.HasValue && (path == "$" + asName))
                resolved = item;
            else if (asName is not null && path == "$index")
                resolved = JsonDocument.Parse(index.ToString()).RootElement;
            else if (asName is not null && path == "$count")
                resolved = JsonDocument.Parse(count.ToString()).RootElement;
            else if (path.StartsWith("$ctx."))
                resolved = parentCtx.Count == 0 ? null : JsonPath.Resolve(DictToJsonElement(parentCtx), path).FirstOrDefault();
            else
            {
                // Build a one-shot frames list that includes the current iteration if any
                var liveFrames = frames.ToList();
                if (asName is not null && item.HasValue)
                    liveFrames = liveFrames.Append(new IterationFrame(asName, item.Value, index, count)).ToList();
                resolved = JsonPath.Resolve(parentRequest, path, liveFrames).FirstOrDefault();
            }
            obj[key] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
        }
        return JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    private static void ApplyOutputMapping(
        IReadOnlyDictionary<string, string> outputMapping, Envelope subEnvelope,
        IDictionary<string, JsonElement> ctx, Dictionary<string, JsonElement> nodeOutputs,
        string hostKey, FrameStack frames)
    {
        var envelopeRoot = JsonSerializer.SerializeToElement(subEnvelope, AeroJson.Options);
        JsonObject? hostAccum = null;
        foreach (var kv in outputMapping)
        {
            var target = kv.Key; var path = kv.Value;
            var rooted = path.StartsWith("$.") || path.StartsWith("$") ? path : "$." + path;
            var resolved = JsonPath.Resolve(envelopeRoot, rooted).FirstOrDefault();
            if (!resolved.HasValue) continue;

            if (target.StartsWith("ctx."))
                ctx[target.Substring(4)] = resolved.Value.Clone();
            else
            {
                hostAccum ??= new JsonObject();
                hostAccum[target] = JsonNode.Parse(resolved.Value.GetRawText());
            }
        }
        if (hostAccum is not null) nodeOutputs[hostKey] = JsonDocument.Parse(hostAccum.ToJsonString()).RootElement;
    }

    private static void ApplyDefault(SubRuleCall call, IDictionary<string, JsonElement> ctx, Dictionary<string, JsonElement> nodeOutputs, string hostKey, FrameStack frames)
    {
        if (!call.DefaultValue.HasValue) return;
        var fakeEnv = new Envelope("(default)", 0, Decision.Apply, "", call.DefaultValue, null, null);
        ApplyOutputMapping(call.OutputMapping, fakeEnv, ctx, nodeOutputs, hostKey, frames);
    }

    // ─── graph analysis ────────────────────────────────────────────────────

    private sealed record GraphInfo(
        IReadOnlyDictionary<string, RuleNode> Nodes,
        IReadOnlyDictionary<string, List<RuleEdge>> Outgoing,
        IReadOnlyDictionary<string, List<RuleEdge>> Incoming,
        IReadOnlyDictionary<string, string> MergeClosesIterator,
        IReadOnlyDictionary<string, string> IteratorClosingMerge);

    private static GraphInfo AnalyzeGraph(Rule rule)
    {
        var nodes = rule.Nodes.ToDictionary(n => n.Id);
        var outgoing = rule.Edges.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToList());
        var incoming = rule.Edges.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.ToList());

        // DFS from the input node, maintaining a stack of open iterators.
        var input = rule.Nodes.Single(n => n.Data.Category == NodeCategory.Input);
        var mergeMap = new Dictionary<string, string>();
        var visited = new HashSet<string>();
        var stack = new Stack<string>();
        DFS(input.Id);
        // Reverse mapping for iterator → its closing merge (used to handle
        // empty iterations: the iterator fires zero bodies but still needs to
        // "close" via the merge, which then emits an empty array / 0 / etc.).
        var iterClose = mergeMap.ToDictionary(kv => kv.Value, kv => kv.Key);
        return new GraphInfo(nodes, outgoing, incoming, mergeMap, iterClose);

        void DFS(string nodeId)
        {
            if (visited.Contains(nodeId)) return;
            visited.Add(nodeId);
            var node = nodes[nodeId];

            if (node.Data.Category == NodeCategory.Iterator)
            {
                stack.Push(nodeId);
                if (outgoing.TryGetValue(nodeId, out var outs))
                    foreach (var e in outs) DFS(e.Target);
                // Pop happens implicitly: when we hit the merge, we record + pop
            }
            else if (node.Data.Category == NodeCategory.Merge)
            {
                if (stack.Count > 0)
                {
                    mergeMap[nodeId] = stack.Pop();
                }
                if (outgoing.TryGetValue(nodeId, out var outs))
                    foreach (var e in outs) DFS(e.Target);
            }
            else
            {
                if (outgoing.TryGetValue(nodeId, out var outs))
                    foreach (var e in outs) DFS(e.Target);
            }
        }
    }

    // ─── run state ─────────────────────────────────────────────────────────

    private sealed class RunState
    {
        public GraphInfo Graph { get; }
        public JsonElement Request { get; }
        public Options Options { get; }
        public List<TraceEntry>? Trace { get; }
        public Dictionary<string, JsonElement> Ctx { get; } = new();

        public Queue<(string nodeId, FrameStack frames)> Queue { get; } = new();
        public HashSet<string> Activated { get; } = new();
        public HashSet<string> Fired { get; } = new();
        public Dictionary<string, JsonElement> NodeOutputs { get; } = new();
        public Dictionary<string, Verdict> Verdicts { get; } = new();
        public Dictionary<string, int> IteratorCounts { get; } = new();

        public RunState(GraphInfo graph, JsonElement request, Options options, List<TraceEntry>? trace)
        {
            Graph = graph; Request = request; Options = options; Trace = trace;
        }

        public string Key(string nodeId, FrameStack frames) => $"{nodeId}|{frames.Key}";

        public void Activate(string nodeId, FrameStack frames)
        {
            var k = Key(nodeId, frames);
            if (Activated.Add(k)) Queue.Enqueue((nodeId, frames));
        }

        public void RecordIteratorCount(string iterId, FrameStack frames, int count)
        {
            IteratorCounts[Key(iterId, frames)] = count;
        }

        public int GetIteratorCount(string iterId, FrameStack frames) =>
            IteratorCounts.TryGetValue(Key(iterId, frames), out var c) ? c : 0;

        public bool AllInnerIterationsCompleteFor(string mergeId, FrameStack frames, GraphInfo graph)
        {
            if (!graph.MergeClosesIterator.TryGetValue(mergeId, out var iterId)) return true;
            var expected = GetIteratorCount(iterId, frames);
            if (expected == 0)
            {
                // Iterator may not have fired yet at this frame stack
                return Fired.Contains(Key(iterId, frames));
            }

            // Each upstream of the merge must have fired at frames + [iter-frame i] for every i
            var inEdges = graph.Incoming.GetValueOrDefault(mergeId) ?? new List<RuleEdge>();
            var iterName = (graph.Nodes[iterId].Data.Config?.Deserialize<IteratorConfig>(ConfigJsonOptions))?.As ?? "iter";
            for (var i = 0; i < expected; i++)
            {
                var iterFrame = new IterationFrame(iterName, default, i, expected);
                var childFrames = frames.PushIndexOnly(iterFrame);
                foreach (var src in inEdges.Select(e => e.Source).Distinct())
                {
                    if (graph.Nodes[src].Data.Category == NodeCategory.Iterator) continue;
                    var srcKey = Key(src, childFrames);
                    if (!Activated.Contains(srcKey)) continue;
                    if (!Fired.Contains(srcKey)) return false;
                }
            }
            return true;
        }
    }

    // ─── frame stack ───────────────────────────────────────────────────────

    private sealed class FrameStack
    {
        public static readonly FrameStack Empty = new(ImmutableList<IterationFrame>.Empty);

        private readonly ImmutableList<IterationFrame> _frames;
        public string Key { get; }

        public FrameStack(ImmutableList<IterationFrame> frames)
        {
            _frames = frames;
            Key = string.Join("|", frames.Select(f => $"{f.Name}={f.Index}"));
        }

        public int Count => _frames.Count;
        public IReadOnlyList<IterationFrame> ToList() => _frames;

        public FrameStack Push(IterationFrame f) => new(_frames.Add(f));

        /// <summary>
        /// Variant of Push that uses only (name, index) for identity — the item
        /// itself is irrelevant for keying. Used by the merge to look up siblings.
        /// </summary>
        public FrameStack PushIndexOnly(IterationFrame f) => new(_frames.Add(f));

        public FrameStack Pop() =>
            _frames.Count == 0 ? Empty : new(_frames.RemoveAt(_frames.Count - 1));
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private static T ParseConfig<T>(RuleNode node, string label) where T : class
    {
        try
        {
            return node.Data.Config!.Value.Deserialize<T>(ConfigJsonOptions)!;
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{label} '{node.Id}' config parse failed: {e.Message}", e);
        }
    }

    private static JsonElement DictToJsonElement(IDictionary<string, JsonElement> dict)
    {
        var obj = new JsonObject();
        foreach (var kv in dict) obj[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        return JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    private static bool JsonElementEquals(JsonElement a, JsonElement b) => a.GetRawText() == b.GetRawText();

    private static void EnsureStructuredFilterShape(string nodeId, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"filter node '{nodeId}' config is not an object");
        if (!config.TryGetProperty("source", out _) ||
            !config.TryGetProperty("compare", out _) ||
            !config.TryGetProperty("arraySelector", out _) ||
            !config.TryGetProperty("onMissing", out _))
            throw new InvalidOperationException(
                $"filter node '{nodeId}' uses a legacy/flat config shape. " +
                "Engine requires the structured shape with source/compare/arraySelector/onMissing.");
    }

    private static void ValidateGraph(Rule rule)
    {
        var nodes = rule.Nodes.ToDictionary(n => n.Id);
        foreach (var e in rule.Edges)
        {
            if (!nodes.ContainsKey(e.Source))
                throw new InvalidOperationException($"edge {e.Id} has unknown source '{e.Source}'");
            if (!nodes.ContainsKey(e.Target))
                throw new InvalidOperationException($"edge {e.Id} has unknown target '{e.Target}'");
        }
        if (HasCycle(rule)) throw new InvalidOperationException($"rule '{rule.Id}' contains a cycle");
        if (rule.Nodes.Count(n => n.Data.Category == NodeCategory.Input) != 1)
            throw new InvalidOperationException($"rule '{rule.Id}' must have exactly one input node");
        if (rule.Nodes.Count(n => n.Data.Category == NodeCategory.Output) != 1)
            throw new InvalidOperationException($"rule '{rule.Id}' must have exactly one output node");
    }

    private static bool HasCycle(Rule rule)
    {
        var graph = rule.Edges.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        bool Visit(string id)
        {
            if (done.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (graph.TryGetValue(id, out var ts)) foreach (var t in ts) if (Visit(t)) return true;
            visiting.Remove(id); done.Add(id);
            return false;
        }
        return rule.Nodes.Any(n => Visit(n.Id));
    }

    private static string IsoUtc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static TraceOutcome ToOutcome(Verdict v) => v switch
    {
        Verdict.Pass => TraceOutcome.Pass,
        Verdict.Fail => TraceOutcome.Fail,
        Verdict.Skip => TraceOutcome.Skip,
        _            => TraceOutcome.Error,
    };
}

// Reference-node config — siblings of MutatorConfig.LookupSpec.
public sealed record ReferenceConfig(
    string ReferenceId,
    IReadOnlyDictionary<string, string>? MatchOn = null);
