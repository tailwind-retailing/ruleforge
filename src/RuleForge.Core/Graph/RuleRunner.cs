using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.Core.Graph;

public sealed class RuleRunner
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = AeroJson.Options;

    /// <summary>
    /// Per-run options. Defaults are non-debug, no sub-rule resolution
    /// (a node with <c>subRuleCall</c> will error), and a wall-clock now.
    /// </summary>
    public sealed record Options(
        bool Debug = false,
        IRuleSource? SubRuleSource = null,
        IReferenceSetSource? ReferenceSetSource = null,
        Func<DateTimeOffset>? Clock = null);

    /// <summary>
    /// Synchronous wrapper for callers that don't need sub-rules. Throws
    /// if the rule contains a <c>subRuleCall</c> since resolution is async.
    /// </summary>
    public Envelope Run(Rule rule, JsonElement request, bool debug = false) =>
        RunAsync(rule, request, new Options(Debug: debug)).GetAwaiter().GetResult();

    public Task<Envelope> RunAsync(
        Rule rule,
        JsonElement request,
        Options? options = null,
        CancellationToken ct = default)
    {
        options ??= new Options();
        return RunInternalAsync(rule, request, options, parentCtx: null, ct);
    }

    private async Task<Envelope> RunInternalAsync(
        Rule rule,
        JsonElement request,
        Options options,
        IDictionary<string, JsonElement>? parentCtx,
        CancellationToken ct)
    {
        var version = rule.CurrentVersion > 0 ? rule.CurrentVersion : 1;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var totalSw = options.Debug ? Stopwatch.StartNew() : null;
        var trace = options.Debug ? new List<TraceEntry>() : null;

        ValidateGraph(rule);

        var nodes = rule.Nodes.ToDictionary(n => n.Id);
        var outgoing = rule.Edges.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.ToList());
        var incoming = rule.Edges.GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.ToList());

        var inputNode = rule.Nodes.SingleOrDefault(n => n.Data.Category == NodeCategory.Input)
            ?? throw new InvalidOperationException("rule has no input node");
        var outputNode = rule.Nodes.SingleOrDefault(n => n.Data.Category == NodeCategory.Output)
            ?? throw new InvalidOperationException("rule has no output node");

        var verdicts = new Dictionary<string, Verdict>();
        var nodeOutputs = new Dictionary<string, JsonElement>();
        var fired = new HashSet<string>();
        var activated = new HashSet<string> { inputNode.Id };

        // Each rule run owns its execution context. Sub-rules get a fresh one
        // (parentCtx is always null at sub-rule entry) â€” they can only see
        // what's wired through the call's inputMapping at request-build time.
        var ctx = new Dictionary<string, JsonElement>();

        var queue = new Queue<string>();
        queue.Enqueue(inputNode.Id);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (fired.Contains(nodeId)) continue;
            if (!activated.Contains(nodeId)) continue;

            // Wait for all activated upstream deps to fire so multi-input nodes
            // (logic, output assembly) see complete inputs.
            if (incoming.TryGetValue(nodeId, out var inEdges))
            {
                var pendingDeps = inEdges
                    .Where(e => activated.Contains(e.Source))
                    .Where(e => !fired.Contains(e.Source))
                    .ToList();
                if (pendingDeps.Count > 0)
                {
                    queue.Enqueue(nodeId);
                    continue;
                }
            }

            var node = nodes[nodeId];
            var nodeStarted = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            var ctxBefore = options.Debug ? new Dictionary<string, JsonElement>(ctx) : null;
            string? subRuleRunId = null;

            // Sub-rule call runs first when present, before the host node's logic.
            JsonElement? subRuleResult = null;
            if (node.Data.SubRuleCall is { } call)
            {
                if (options.SubRuleSource is null)
                {
                    var msg = $"node '{node.Id}' has a subRuleCall but no SubRuleSource was configured for the run";
                    trace?.Add(new TraceEntry(node.Id, IsoUtc(nodeStarted), sw.ElapsedMilliseconds, TraceOutcome.Error, Error: msg));
                    return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
                }

                var subResult = await InvokeSubRuleAsync(call, request, ctx, options, ct);
                subRuleRunId = subResult.RunId;

                switch (subResult.Status)
                {
                    case SubRuleStatus.Ok:
                        ApplyOutputMapping(call.OutputMapping, subResult.Envelope!, ctx, nodeOutputs, node.Id);
                        subRuleResult = subResult.Envelope!.Result;
                        break;
                    case SubRuleStatus.Default:
                        ApplyDefault(call, ctx, nodeOutputs, node.Id);
                        subRuleResult = call.DefaultValue;
                        break;
                    case SubRuleStatus.Skipped:
                        // skip = sub-rule emits nothing; node continues without writes.
                        break;
                    case SubRuleStatus.Failed:
                        trace?.Add(new TraceEntry(
                            node.Id,
                            IsoUtc(nodeStarted),
                            sw.ElapsedMilliseconds,
                            TraceOutcome.Error,
                            Error: subResult.Error,
                            SubRuleRunId: subRuleRunId,
                            CtxRead: ctxBefore));
                        return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
                }
            }

            Verdict verdict;
            JsonElement? nodeOutput = null;
            try
            {
                (verdict, nodeOutput) = await ExecuteNodeAsync(node, request, ctx, incoming, verdicts, nodeOutputs, subRuleResult, options, ct);
            }
            catch (Exception e)
            {
                trace?.Add(new TraceEntry(
                    node.Id,
                    IsoUtc(nodeStarted),
                    sw.ElapsedMilliseconds,
                    TraceOutcome.Error,
                    Error: e.Message,
                    SubRuleRunId: subRuleRunId));
                return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);
            }
            sw.Stop();

            verdicts[node.Id] = verdict;
            if (nodeOutput.HasValue) nodeOutputs[node.Id] = nodeOutput.Value;
            fired.Add(node.Id);

            if (trace is not null)
            {
                Dictionary<string, JsonElement>? ctxWritten = null;
                if (ctxBefore is not null)
                {
                    foreach (var kv in ctx)
                    {
                        if (!ctxBefore.TryGetValue(kv.Key, out var prev) || !JsonElementEquals(prev, kv.Value))
                            (ctxWritten ??= new())[kv.Key] = kv.Value;
                    }
                }
                trace.Add(new TraceEntry(
                    node.Id,
                    IsoUtc(nodeStarted),
                    sw.ElapsedMilliseconds,
                    ToOutcome(verdict),
                    Output: nodeOutput,
                    CtxRead: ctxBefore is null || ctxBefore.Count == 0 ? null : ctxBefore,
                    CtxWritten: ctxWritten,
                    SubRuleRunId: subRuleRunId));
            }

            if (verdict == Verdict.Error)
                return new Envelope(rule.Id, version, Decision.Error, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);

            if (outgoing.TryGetValue(node.Id, out var outs))
            {
                foreach (var edge in outs)
                {
                    if (!EdgeRouter.Matches(verdict, edge.Branch)) continue;
                    activated.Add(edge.Target);
                    queue.Enqueue(edge.Target);
                }
            }
        }

        if (!fired.Contains(outputNode.Id))
            return new Envelope(rule.Id, version, Decision.Skip, IsoUtc(startedAtUtc), null, trace, totalSw?.ElapsedMilliseconds);

        var result = AssembleResult(outputNode, incoming, nodeOutputs, ctx);
        return new Envelope(rule.Id, version, Decision.Apply, IsoUtc(startedAtUtc), result, trace, totalSw?.ElapsedMilliseconds);
    }

    // â”€â”€â”€ node execution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<(Verdict, JsonElement?)> ExecuteNodeAsync(
        RuleNode node,
        JsonElement request,
        IDictionary<string, JsonElement> ctx,
        Dictionary<string, List<RuleEdge>> incoming,
        Dictionary<string, Verdict> verdicts,
        Dictionary<string, JsonElement> nodeOutputs,
        JsonElement? subRuleResult,
        Options options,
        CancellationToken ct)
    {
        switch (node.Data.Category)
        {
            case NodeCategory.Input:
                return (Verdict.Pass, request);

            case NodeCategory.Output:
                return (Verdict.Pass, null);

            case NodeCategory.Filter:
                return (ExecuteFilter(node, request, ctx, options), null);

            case NodeCategory.Logic:
                return (ExecuteLogic(node, incoming, verdicts), null);

            case NodeCategory.Constant:
                return (Verdict.Pass, ReadConfigField(node, "value"));

            case NodeCategory.Product:
                return (Verdict.Pass, ReadProductOutput(node, subRuleResult, nodeOutputs, incoming, ctx));

            case NodeCategory.Mutator:
                return await ExecuteMutatorAsync(node, request, ctx, incoming, nodeOutputs, options, ct);

            case NodeCategory.Calc:
                return (Verdict.Pass, ExecuteCalc(node, request, ctx, incoming, nodeOutputs));

            case NodeCategory.RuleRef:
                // ruleRef is a wrapper around subRuleCall â€” the call ran upstream of
                // ExecuteNode; emit the sub-rule result as this node's output and pass.
                return (Verdict.Pass, subRuleResult);

            default:
                throw new NotSupportedException(
                    $"node category '{node.Data.Category}' is not implemented (node {node.Id})");
        }
    }

    private Verdict ExecuteFilter(RuleNode node, JsonElement request, IDictionary<string, JsonElement> ctx, Options options)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException(
                $"filter node '{node.Id}' has no config. " +
                "AERO admin must inline the structured filter config before publish.");

        var raw = node.Data.Config.Value;
        EnsureStructuredFilterShape(node.Id, raw);

        var ctxElement = ctx.Count == 0 ? (JsonElement?)null : DictToJsonElement(ctx);
        var fctx = new StringFilterEvaluator.Context(request, ctxElement);

        var kind = ClassifyFilter(node, raw);
        try
        {
            switch (kind)
            {
                case FilterKind.Number:
                {
                    var cfg = raw.Deserialize<NumberFilterConfig>(ConfigJsonOptions)
                              ?? throw new InvalidOperationException("filter config deserialised to null");
                    return NumberFilterEvaluator.Evaluate(cfg, fctx).Verdict;
                }
                case FilterKind.Date:
                {
                    var cfg = raw.Deserialize<DateFilterConfig>(ConfigJsonOptions)
                              ?? throw new InvalidOperationException("filter config deserialised to null");
                    return DateFilterEvaluator.Evaluate(cfg, fctx, options.Clock).Verdict;
                }
                case FilterKind.String:
                default:
                {
                    var cfg = raw.Deserialize<StringFilterConfig>(ConfigJsonOptions)
                              ?? throw new InvalidOperationException("filter config deserialised to null");
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
                if (opName is "gt" or "gte" or "lt" or "lte" or "between" or "not_between")
                    return FilterKind.Number;
            }
        }
        return FilterKind.String;
    }

    private static Verdict ExecuteLogic(
        RuleNode node,
        Dictionary<string, List<RuleEdge>> incoming,
        Dictionary<string, Verdict> verdicts)
    {
        var op = LogicEvaluator.Parse(node.Data.TemplateId, node.Data.Label);
        var inEdges = incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var inputs = inEdges
            .Select(e => e.Source)
            .Distinct()
            .Select(src => verdicts.TryGetValue(src, out var v) ? v : Verdict.Skip)
            .ToList();
        return LogicEvaluator.Apply(op, inputs);
    }

    // â”€â”€â”€ mutator â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<(Verdict, JsonElement?)> ExecuteMutatorAsync(
        RuleNode node,
        JsonElement request,
        IDictionary<string, JsonElement> ctx,
        Dictionary<string, List<RuleEdge>> incoming,
        Dictionary<string, JsonElement> nodeOutputs,
        Options options,
        CancellationToken ct)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException($"mutator node '{node.Id}' has no config");

        MutatorConfig cfg;
        try
        {
            cfg = node.Data.Config.Value.Deserialize<MutatorConfig>(ConfigJsonOptions)
                  ?? throw new InvalidOperationException("mutator config deserialised to null");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"mutator node '{node.Id}' config could not be parsed: {e.Message}", e);
        }

        if (string.IsNullOrEmpty(cfg.Target))
            throw new InvalidOperationException($"mutator node '{node.Id}' is missing 'target'");

        // Find the upstream object to mutate. A mutator with a single
        // upstream node uses that node's output. With zero upstream outputs
        // (e.g. mutator wired to a logic gate that has no output object) we
        // start from an empty object so 'set property' still works.
        var inEdges = incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var upstreamOutputs = inEdges.Select(e => e.Source).Distinct()
            .Where(s => nodeOutputs.ContainsKey(s))
            .Select(s => nodeOutputs[s])
            .ToList();
        if (upstreamOutputs.Count > 1)
            throw new InvalidOperationException(
                $"mutator node '{node.Id}' has {upstreamOutputs.Count} upstream outputs â€” exactly one required");

        var baseObj = upstreamOutputs.Count == 1 && upstreamOutputs[0].ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(upstreamOutputs[0].GetRawText())!.AsObject()
            : new JsonObject();

        // Compute the new value.
        JsonElement? newValue;
        if (cfg.Lookup is not null)
        {
            if (options.ReferenceSetSource is null)
                throw new InvalidOperationException(
                    $"mutator node '{node.Id}' uses lookup but no ReferenceSetSource was configured");

            var refSet = await options.ReferenceSetSource.GetByIdAsync(cfg.Lookup.ReferenceId, ct)
                         ?? throw new InvalidOperationException(
                             $"mutator node '{node.Id}': reference set '{cfg.Lookup.ReferenceId}' not found");

            newValue = LookupRefSet(refSet, cfg.Lookup, request, ctx);
        }
        else if (cfg.From is not null)
        {
            newValue = ResolveFromPath(cfg.From, request, ctx);
        }
        else if (cfg.Value.HasValue)
        {
            newValue = cfg.Value.Value;
        }
        else
        {
            throw new InvalidOperationException(
                $"mutator node '{node.Id}' must specify 'value', 'from' or 'lookup'");
        }

        if (!newValue.HasValue)
        {
            switch (cfg.OnMissing)
            {
                case OnLookupMissing.Leave:
                    break; // baseObj keeps whatever was there
                case OnLookupMissing.Clear:
                    baseObj[cfg.Target] = null;
                    break;
                case OnLookupMissing.Error:
                    return (Verdict.Error, null);
            }
        }
        else
        {
            baseObj[cfg.Target] = JsonNode.Parse(newValue.Value.GetRawText());
        }

        var output = JsonDocument.Parse(baseObj.ToJsonString()).RootElement;
        return (Verdict.Pass, output);
    }

    private static JsonElement? LookupRefSet(
        ReferenceSet refSet,
        LookupSpec spec,
        JsonElement request,
        IDictionary<string, JsonElement> ctx)
    {
        // Resolve each match value once.
        var match = new Dictionary<string, JsonElement?>(spec.MatchOn.Count);
        foreach (var kv in spec.MatchOn)
            match[kv.Key] = ResolveFromPath(kv.Value, request, ctx);

        foreach (var row in refSet.Rows)
        {
            var allMatch = true;
            foreach (var (col, expected) in match)
            {
                if (!row.TryGetValue(col, out var actual))
                {
                    allMatch = false;
                    break;
                }
                if (!expected.HasValue || !RefValueEquals(actual, expected.Value))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch && row.TryGetValue(spec.ValueColumn, out var v))
                return v;
        }
        return null;
    }

    /// <summary>
    /// Loose equality used for reference-set match: number-vs-number compares
    /// numerically, string-vs-string compares verbatim, anything else falls
    /// back to GetRawText() comparison.
    /// </summary>
    private static bool RefValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            return a.GetDouble() == b.GetDouble();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String)
            return a.GetString() == b.GetString();
        return a.GetRawText() == b.GetRawText();
    }

    /// <summary>
    /// Resolve a JSONPath against the request (default) or the execution
    /// context (when prefixed with <c>$ctx.</c>).
    /// </summary>
    private static JsonElement? ResolveFromPath(
        string path,
        JsonElement request,
        IDictionary<string, JsonElement> ctx)
    {
        if (path.StartsWith("$ctx."))
        {
            if (ctx.Count == 0) return null;
            var ctxEl = DictToJsonElement(ctx);
            return JsonPath.Resolve(ctxEl, path).FirstOrDefault();
        }
        return JsonPath.Resolve(request, path).FirstOrDefault();
    }

    // â”€â”€â”€ calc â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static JsonElement? ExecuteCalc(
        RuleNode node,
        JsonElement request,
        IDictionary<string, JsonElement> ctx,
        Dictionary<string, List<RuleEdge>> incoming,
        Dictionary<string, JsonElement> nodeOutputs)
    {
        if (node.Data.Config is null)
            throw new InvalidOperationException($"calc node '{node.Id}' has no config");

        CalcConfig cfg;
        try
        {
            cfg = node.Data.Config.Value.Deserialize<CalcConfig>(ConfigJsonOptions)
                  ?? throw new InvalidOperationException("calc config deserialised to null");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"calc node '{node.Id}' config could not be parsed: {e.Message}", e);
        }

        if (string.IsNullOrEmpty(cfg.Expression))
            throw new InvalidOperationException($"calc node '{node.Id}' is missing 'expression'");

        // Locate the (single) upstream output to feed in as variables.
        var inEdges = incoming.GetValueOrDefault(node.Id) ?? new List<RuleEdge>();
        var upstreamOutputs = inEdges.Select(e => e.Source).Distinct()
            .Where(s => nodeOutputs.ContainsKey(s))
            .Select(s => nodeOutputs[s])
            .ToList();
        if (upstreamOutputs.Count > 1)
            throw new InvalidOperationException(
                $"calc node '{node.Id}' has {upstreamOutputs.Count} upstream outputs â€” exactly one required");

        var upstream = upstreamOutputs.Count == 1 ? upstreamOutputs[0] : (JsonElement?)null;
        var computed = CalcEvaluator.Evaluate(cfg.Expression, upstream, ctx, request);

        // No target â†’ emit the raw computed value as this node's output.
        if (string.IsNullOrEmpty(cfg.Target)) return computed;

        // With a target â†’ replace the field on a copy of the upstream object.
        var baseObj = upstream is { ValueKind: JsonValueKind.Object } u
            ? JsonNode.Parse(u.GetRawText())!.AsObject()
            : new JsonObject();
        baseObj[cfg.Target] = computed.HasValue
            ? JsonNode.Parse(computed.Value.GetRawText())
            : null;
        return JsonDocument.Parse(baseObj.ToJsonString()).RootElement;
    }

    // â”€â”€â”€ product / constant â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static JsonElement? ReadConfigField(RuleNode node, string field)
    {
        if (node.Data.Config is null) return null;
        var cfg = node.Data.Config.Value;
        return cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty(field, out var v) ? v : null;
    }

    /// <summary>
    /// Product node output. Order of precedence:
    ///   1. <c>data.config.output</c> â€” explicit object literal
    ///   2. <c>data.config.outputSchema</c> â€” array of {key, value} pairs (template style)
    ///   3. The sub-rule result, if a subRuleCall provided one
    /// Unknown placeholders <c>${ctx.X}</c> in any string value are resolved
    /// from the run's execution context.
    /// </summary>
    private static JsonElement? ReadProductOutput(
        RuleNode node,
        JsonElement? subRuleResult,
        Dictionary<string, JsonElement> nodeOutputs,
        Dictionary<string, List<RuleEdge>> incoming,
        IDictionary<string, JsonElement> ctx)
    {
        if (node.Data.Config is { } cfg && cfg.ValueKind == JsonValueKind.Object)
        {
            if (cfg.TryGetProperty("output", out var direct))
                return ResolveCtxPlaceholders(direct, ctx);

            if (cfg.TryGetProperty("outputSchema", out var schema) && schema.ValueKind == JsonValueKind.Array)
                return ResolveCtxPlaceholders(SchemaArrayToObject(schema), ctx);
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

    /// <summary>
    /// Recursively walk a JsonElement and replace any string of the form
    /// <c>"${ctx.path}"</c> with the JSON value at that ctx path.
    /// </summary>
    private static JsonElement ResolveCtxPlaceholders(JsonElement input, IDictionary<string, JsonElement> ctx)
    {
        if (ctx.Count == 0) return input;
        if (!ContainsCtxPlaceholder(input)) return input;

        var node = JsonNode.Parse(input.GetRawText());
        var ctxElement = DictToJsonElement(ctx);
        Replace(node, ctxElement);
        return JsonDocument.Parse(node!.ToJsonString()).RootElement;

        static void Replace(JsonNode? n, JsonElement ctxEl)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        var child = obj[key];
                        if (child is JsonValue v && v.TryGetValue(out string? s) && IsCtxPlaceholder(s, out var path))
                        {
                            var resolved = JsonPath.Resolve(ctxEl, path).FirstOrDefault();
                            obj[key] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
                        }
                        else
                        {
                            Replace(child, ctxEl);
                        }
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var child = arr[i];
                        if (child is JsonValue v && v.TryGetValue(out string? s) && IsCtxPlaceholder(s, out var path))
                        {
                            var resolved = JsonPath.Resolve(ctxEl, path).FirstOrDefault();
                            arr[i] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
                        }
                        else
                        {
                            Replace(child, ctxEl);
                        }
                    }
                    break;
            }
        }
    }

    private static bool ContainsCtxPlaceholder(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return IsCtxPlaceholder(el.GetString(), out _);
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    if (ContainsCtxPlaceholder(p.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (ContainsCtxPlaceholder(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool IsCtxPlaceholder(string? s, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(s)) return false;
        if (!s.StartsWith("${ctx.") || !s.EndsWith("}")) return false;
        path = "$ctx." + s.Substring("${ctx.".Length, s.Length - "${ctx.".Length - 1);
        return true;
    }

    // â”€â”€â”€ output assembly â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// The output node's result. Resolution order:
    ///   1. Legacy literal: <c>output.config.result</c> (used by v1â€“v4)
    ///   2. Single upstream output â†’ use it as-is
    ///   3. Multiple upstream outputs â†’ shallow-merge all object outputs
    ///      (later wins on key conflict), with ctx placeholders resolved
    /// </summary>
    private static JsonElement? AssembleResult(
        RuleNode outputNode,
        Dictionary<string, List<RuleEdge>> incoming,
        Dictionary<string, JsonElement> nodeOutputs,
        IDictionary<string, JsonElement> ctx)
    {
        if (outputNode.Data.Config is { } cfg && cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("result", out var literal))
        {
            return ResolveCtxPlaceholders(literal, ctx);
        }

        var inEdges = incoming.GetValueOrDefault(outputNode.Id) ?? new List<RuleEdge>();
        var sources = inEdges.Select(e => e.Source).Distinct()
            .Where(s => nodeOutputs.ContainsKey(s))
            .Select(s => nodeOutputs[s])
            .ToList();

        if (sources.Count == 0) return null;
        if (sources.Count == 1) return ResolveCtxPlaceholders(sources[0], ctx);

        var merged = new JsonObject();
        foreach (var src in sources)
        {
            if (src.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in src.EnumerateObject())
                merged[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        var assembled = JsonDocument.Parse(merged.ToJsonString()).RootElement;
        return ResolveCtxPlaceholders(assembled, ctx);
    }

    // â”€â”€â”€ sub-rule call â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private enum SubRuleStatus { Ok, Default, Skipped, Failed }

    private sealed record SubRuleResult(SubRuleStatus Status, Envelope? Envelope, string? RunId, string? Error);

    private async Task<SubRuleResult> InvokeSubRuleAsync(
        SubRuleCall call,
        JsonElement parentRequest,
        IDictionary<string, JsonElement> parentCtx,
        Options options,
        CancellationToken ct)
    {
        try
        {
            var subRule = await options.SubRuleSource!.GetByIdAsync(
                call.RuleId,
                ResolveVersion(call.PinnedVersion),
                ct);

            if (subRule is null)
                throw new InvalidOperationException(
                    $"subRuleCall: rule '{call.RuleId}' (version {call.PinnedVersion.GetRawText()}) not found");

            var subRequest = BuildSubRequest(call.InputMapping, parentRequest, parentCtx);

            // Sub-rule runs with no parent ctx and shares debug/clock with the parent.
            var subEnvelope = await RunInternalAsync(
                subRule,
                subRequest,
                options with { },
                parentCtx: null,
                ct);

            var runId = $"srr-{call.RuleId}-{Guid.NewGuid():N}";

            return subEnvelope.Decision switch
            {
                Decision.Apply => new SubRuleResult(SubRuleStatus.Ok, subEnvelope, runId, null),
                // Skip and Error both mean "no result to map" for the host; the
                // call's onError mode chooses the behavior. With onError=default
                // the configured defaultValue is mapped instead.
                Decision.Skip  => HandleSubRuleError(call, runId,
                                    "sub-rule decided to skip (no result)"),
                Decision.Error => HandleSubRuleError(call, runId,
                                    subEnvelope.Trace?.LastOrDefault()?.Error
                                    ?? "sub-rule errored without a trace"),
                _ => HandleSubRuleError(call, runId, $"unknown sub-rule decision {subEnvelope.Decision}"),
            };
        }
        catch (Exception e)
        {
            return HandleSubRuleError(call, runId: null, e.Message);
        }
    }

    private static SubRuleResult HandleSubRuleError(SubRuleCall call, string? runId, string message)
    {
        return call.OnError switch
        {
            SubRuleErrorMode.Skip    => new SubRuleResult(SubRuleStatus.Skipped, null, runId, message),
            SubRuleErrorMode.Default => new SubRuleResult(SubRuleStatus.Default, null, runId, message),
            _                        => new SubRuleResult(SubRuleStatus.Failed, null, runId, message),
        };
    }

    private static int? ResolveVersion(JsonElement pinned) =>
        pinned.ValueKind == JsonValueKind.Number && pinned.TryGetInt32(out var v) ? v : null;

    /// <summary>
    /// Build the sub-rule's request from the parent. Each entry in
    /// <c>inputMapping</c> is <c>parentJsonPath â†’ subRuleInputKey</c>: we
    /// resolve the path against the parent (request first, falling back to
    /// <c>$ctx.</c>-prefixed paths against parent ctx) and assign the value
    /// to the named key in a new object.
    /// </summary>
    private static JsonElement BuildSubRequest(
        IReadOnlyDictionary<string, string> inputMapping,
        JsonElement parentRequest,
        IDictionary<string, JsonElement> parentCtx)
    {
        var obj = new JsonObject();
        foreach (var kv in inputMapping)
        {
            var key = kv.Key;
            var path = kv.Value;
            JsonElement? resolved;
            if (path.StartsWith("$ctx."))
            {
                var ctxEl = parentCtx.Count == 0 ? (JsonElement?)null : DictToJsonElement(parentCtx);
                resolved = ctxEl.HasValue ? JsonPath.Resolve(ctxEl, path).FirstOrDefault() : null;
            }
            else
            {
                resolved = JsonPath.Resolve(parentRequest, path).FirstOrDefault();
            }
            obj[key] = resolved.HasValue ? JsonNode.Parse(resolved.Value.GetRawText()) : null;
        }
        return JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    /// <summary>
    /// Apply <c>outputMapping</c>: each entry is <c>parentTarget â†’ subRulePath</c>.
    /// Targets prefixed with <c>ctx.</c> write into the parent's execution
    /// context; others write into the host node's output object.
    /// </summary>
    private static void ApplyOutputMapping(
        IReadOnlyDictionary<string, string> outputMapping,
        Envelope subEnvelope,
        IDictionary<string, JsonElement> ctx,
        Dictionary<string, JsonElement> nodeOutputs,
        string hostNodeId)
    {
        // Assemble a virtual root over the sub-rule envelope so paths like
        // "result.bonusPieces" resolve naturally.
        var envelopeRoot = JsonSerializer.SerializeToElement(subEnvelope, AeroJson.Options);

        JsonObject? hostOutputAccum = null;

        foreach (var kv in outputMapping)
        {
            var target = kv.Key;
            var path = kv.Value;
            // Allow shorthand: "result" or "result.X" without a leading $.
            var rooted = path.StartsWith("$.") || path.StartsWith("$")
                ? path
                : "$." + path;
            var resolved = JsonPath.Resolve(envelopeRoot, rooted).FirstOrDefault();
            if (!resolved.HasValue) continue;

            if (target.StartsWith("ctx."))
            {
                ctx[target.Substring(4)] = resolved.Value.Clone();
            }
            else
            {
                hostOutputAccum ??= new JsonObject();
                hostOutputAccum[target] = JsonNode.Parse(resolved.Value.GetRawText());
            }
        }

        if (hostOutputAccum is not null)
            nodeOutputs[hostNodeId] = JsonDocument.Parse(hostOutputAccum.ToJsonString()).RootElement;
    }

    private static void ApplyDefault(
        SubRuleCall call,
        IDictionary<string, JsonElement> ctx,
        Dictionary<string, JsonElement> nodeOutputs,
        string hostNodeId)
    {
        if (!call.DefaultValue.HasValue) return;
        // Treat defaultValue as if it were the sub-rule's result.
        var fakeEnvelope = new Envelope("(default)", 0, Decision.Apply, "", call.DefaultValue, null, null);
        ApplyOutputMapping(call.OutputMapping, fakeEnvelope, ctx, nodeOutputs, hostNodeId);
    }

    // â”€â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static JsonElement DictToJsonElement(IDictionary<string, JsonElement> dict)
    {
        var obj = new JsonObject();
        foreach (var kv in dict)
            obj[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());
        return JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    private static bool JsonElementEquals(JsonElement a, JsonElement b) =>
        a.GetRawText() == b.GetRawText();

    private static void EnsureStructuredFilterShape(string nodeId, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"filter node '{nodeId}' config is not an object");

        var hasSource = config.TryGetProperty("source", out _);
        var hasCompare = config.TryGetProperty("compare", out _);
        var hasArraySelector = config.TryGetProperty("arraySelector", out _);
        var hasOnMissing = config.TryGetProperty("onMissing", out _);

        if (!hasSource || !hasCompare || !hasArraySelector || !hasOnMissing)
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
        if (HasCycle(rule))
            throw new InvalidOperationException($"rule '{rule.Id}' contains a cycle");
        if (rule.Nodes.Count(n => n.Data.Category == NodeCategory.Input) != 1)
            throw new InvalidOperationException($"rule '{rule.Id}' must have exactly one input node");
        if (rule.Nodes.Count(n => n.Data.Category == NodeCategory.Output) != 1)
            throw new InvalidOperationException($"rule '{rule.Id}' must have exactly one output node");
    }

    private static bool HasCycle(Rule rule)
    {
        var graph = rule.Edges.GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());
        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        bool Visit(string id)
        {
            if (done.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (graph.TryGetValue(id, out var targets))
                foreach (var t in targets)
                    if (Visit(t)) return true;
            visiting.Remove(id);
            done.Add(id);
            return false;
        }
        return rule.Nodes.Any(n => Visit(n.Id));
    }

    private static string IsoUtc(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static TraceOutcome ToOutcome(Verdict v) => v switch
    {
        Verdict.Pass => TraceOutcome.Pass,
        Verdict.Fail => TraceOutcome.Fail,
        Verdict.Skip => TraceOutcome.Skip,
        _            => TraceOutcome.Error,
    };
}
