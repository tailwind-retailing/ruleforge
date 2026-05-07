using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// When <c>Options.RedactTraceErrors</c> is set, the trace returned to the
/// caller carries stable error codes instead of raw exception messages.
/// Intent: production callers see actionable codes; raw details (parse
/// positions, file paths, internal IDs) stay in server-side logs.
/// </summary>
public class TraceRedactionTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static Rule BuildLinearRule(params RuleNode[] middle)
    {
        var nodes = new List<RuleNode>();
        nodes.Add(new RuleNode("i", "input", new(0, 0), new("in", NodeCategory.Input)));
        nodes.AddRange(middle);
        nodes.Add(new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output)));
        var edges = new List<RuleEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new RuleEdge($"e{i}", nodes[i].Id, nodes[i + 1].Id, EdgeBranch.Default));
        return new Rule(
            "rule-test", "test", "/x", HttpMethodKind.POST,
            RuleStatus.Published, 1,
            JsonDocument.Parse("{}").RootElement,
            JsonDocument.Parse("{}").RootElement,
            nodes, edges, "2026-04-27T00:00:00.000Z");
    }

    private static string GetErrorFromTrace(Envelope env) =>
        env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;

    [Fact]
    public async Task Redaction_off_returns_raw_message()
    {
        var rule = BuildLinearRule(
            new RuleNode("a", "assert", new(0, 0), new("a", NodeCategory.Assert,
                Config: Json("""{"condition":"false","errorCode":"FAIL"}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true, RedactTraceErrors: false));

        var msg = GetErrorFromTrace(env);
        Assert.Contains("FAIL", msg);
        Assert.Contains("assert 'a'", msg);   // raw includes node id + custom code
    }

    [Fact]
    public async Task Redaction_on_returns_stable_code_for_assert_failure()
    {
        var rule = BuildLinearRule(
            new RuleNode("a", "assert", new(0, 0), new("a", NodeCategory.Assert,
                Config: Json("""{"condition":"false"}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true, RedactTraceErrors: true));

        Assert.Equal("ASSERT_FAILED", GetErrorFromTrace(env));
    }

    [Fact]
    public async Task Redaction_on_returns_stable_code_for_calc_eval_error()
    {
        // Bad NCalc syntax → "calc expression '...' failed to evaluate: ..."
        // → CALC_EVAL_ERROR. Avoids the timeout-via-stack-overflow path
        // tested separately in CalcEvaluatorTests.
        var rule = BuildLinearRule(
            new RuleNode("c", "calc", new(0, 0), new("c", NodeCategory.Calc,
                Config: Json("""{"expression":"((((bad syntax"}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true, RedactTraceErrors: true));

        Assert.Equal("CALC_EVAL_ERROR", GetErrorFromTrace(env));
    }

    [Fact]
    public async Task Redaction_on_returns_stable_code_for_subrule_cycle()
    {
        // Build a self-referential rule using a sub-rule call.
        var refNode = new RuleNode("c", "ruleRef", new(0, 0), new("call", NodeCategory.RuleRef,
            SubRuleCall: new SubRuleCall(
                RuleId: "rule-test",   // self-reference
                InputMapping: new Dictionary<string, string>(),
                OutputMapping: new Dictionary<string, string>(),
                OnError: SubRuleErrorMode.Fail,
                DefaultValue: null,
                PinnedVersion: Json("\"latest\""))));
        var rule = BuildLinearRule(refNode);

        var src = new TinyRuleSource(rule);
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true, SubRuleSource: src, RedactTraceErrors: true));

        Assert.Equal("SUBRULE_CYCLE", GetErrorFromTrace(env));
    }

    [Fact]
    public async Task Redaction_on_returns_stable_code_for_distinct_bad_config()
    {
        var rule = BuildLinearRule(
            new RuleNode("k", "constant", new(0, 0), new("k", NodeCategory.Constant,
                Config: Json("""{"value":[1,2,3]}"""))),
            new RuleNode("d", "distinct", new(0, 0), new("d", NodeCategory.Distinct,
                Config: Json("""{"keep":"weird"}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true, RedactTraceErrors: true));

        Assert.Equal("DISTINCT_ERROR", GetErrorFromTrace(env));
    }

    [Fact]
    public async Task Redaction_off_preserves_existing_test_assertions()
    {
        // Sanity: the default (RedactTraceErrors: false) keeps all existing
        // test message-assertions working. This test mirrors a snippet of
        // SortLimitDistinctTests with explicit redaction off and verifies
        // we still get the literal "not a JSON array" substring.
        var rule = BuildLinearRule(
            new RuleNode("k", "constant", new(0, 0), new("k", NodeCategory.Constant,
                Config: Json("""{"value":42}"""))),
            new RuleNode("s", "sort", new(0, 0), new("s", NodeCategory.Sort,
                Config: Json("""{}"""))));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true /* RedactTraceErrors: false default */));

        var msg = GetErrorFromTrace(env);
        Assert.Contains("not a JSON array", msg);
    }

    private sealed class TinyRuleSource : RuleForge.Core.Loader.IRuleSource
    {
        private readonly Rule _rule;
        public TinyRuleSource(Rule rule) { _rule = rule; }
        public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default) =>
            Task.FromResult<Rule?>(_rule);
        public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default) =>
            Task.FromResult<Rule?>(_rule);
        public Task<IReadOnlyList<RuleForge.Core.Loader.RuleBinding>> ListBindingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuleForge.Core.Loader.RuleBinding>>(
                new[] { new RuleForge.Core.Loader.RuleBinding(_rule.Id, _rule.CurrentVersion, _rule.Endpoint, _rule.Method) });
    }
}
