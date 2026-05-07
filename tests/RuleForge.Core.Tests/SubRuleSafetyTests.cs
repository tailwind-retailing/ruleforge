using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Sub-rule depth limit + inter-rule cycle detection. The pre-existing
/// HasCycle() check at validate time only catches loops within a single
/// rule's DAG — A → forEach → B → A across sub-rule boundaries was
/// undetected and would stack-overflow the engine on the hot path. These
/// tests exercise the new <c>Options.MaxSubRuleDepth</c> +
/// <c>Options.SubRuleCallStack</c> guard rails in
/// <c>RuleRunner.InvokeSubRuleAsync</c>.
/// </summary>
public class SubRuleSafetyTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private sealed class InMemoryRuleSource : IRuleSource
    {
        private readonly Dictionary<(string id, int v), Rule> _rules = new();
        public InMemoryRuleSource Add(Rule rule) { _rules[(rule.Id, rule.CurrentVersion)] = rule; return this; }
        public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default) =>
            Task.FromResult(_rules.Values.FirstOrDefault(r => r.Endpoint == endpoint && r.Method == method));
        public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
        {
            var latest = _rules.Where(kv => kv.Key.id == ruleId)
                .OrderByDescending(kv => kv.Key.v).Select(kv => kv.Value).FirstOrDefault();
            return Task.FromResult<Rule?>(latest);
        }
        public Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuleBinding>>(_rules.Values
                .Select(r => new RuleBinding(r.Id, r.CurrentVersion, r.Endpoint, r.Method)).ToList());
    }

    /// <summary>Build a rule that does nothing but call another rule via ruleRef.</summary>
    private static Rule MakeCallerRule(string id, string callsId)
    {
        var input  = new RuleNode("i", "input",  new(0, 0), new("in",  NodeCategory.Input));
        var refNode = new RuleNode("c", "ruleRef", new(0, 0), new("call", NodeCategory.RuleRef,
            SubRuleCall: new SubRuleCall(
                RuleId: callsId,
                InputMapping: new Dictionary<string, string>(),
                OutputMapping: new Dictionary<string, string>(),
                OnError: SubRuleErrorMode.Fail,
                DefaultValue: null,
                PinnedVersion: Json("\"latest\""))));
        var output = new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output));
        var edges = new List<RuleEdge>
        {
            new("e1", "i", "c", EdgeBranch.Default),
            new("e2", "c", "o", EdgeBranch.Default),
        };
        return new Rule(id, id, "/" + id, HttpMethodKind.POST, RuleStatus.Published, 1,
            Json("{}"), Json("{}"), new[] { input, refNode, output }, edges, "2026-04-27T00:00:00.000Z");
    }

    /// <summary>Leaf rule that just emits a constant — terminates a chain.</summary>
    private static Rule MakeLeafRule(string id)
    {
        var input  = new RuleNode("i", "input", new(0, 0), new("in", NodeCategory.Input));
        var k      = new RuleNode("k", "constant", new(0, 0), new("k", NodeCategory.Constant,
            Config: Json("""{"value":42}""")));
        var output = new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output));
        var edges = new List<RuleEdge>
        {
            new("e1", "i", "k", EdgeBranch.Default),
            new("e2", "k", "o", EdgeBranch.Default),
        };
        return new Rule(id, id, "/" + id, HttpMethodKind.POST, RuleStatus.Published, 1,
            Json("{}"), Json("{}"), new[] { input, k, output }, edges, "2026-04-27T00:00:00.000Z");
    }

    [Fact]
    public async Task Self_referential_rule_yields_cycle_error()
    {
        var ruleA = MakeCallerRule("rule-a", "rule-a");
        var src = new InMemoryRuleSource().Add(ruleA);

        var env = await new RuleRunner().RunAsync(ruleA, Json("{}"),
            new RuleRunner.Options(SubRuleSource: src, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("cycle detected", err);
    }

    [Fact]
    public async Task A_calls_B_calls_A_yields_cycle_error()
    {
        var ruleA = MakeCallerRule("rule-a", "rule-b");
        var ruleB = MakeCallerRule("rule-b", "rule-a");
        var src = new InMemoryRuleSource().Add(ruleA).Add(ruleB);

        var env = await new RuleRunner().RunAsync(ruleA, Json("{}"),
            new RuleRunner.Options(SubRuleSource: src, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("cycle detected", err);
        Assert.Contains("rule-a", err);
    }

    [Fact]
    public async Task Linear_chain_under_default_depth_succeeds()
    {
        // 5-level chain — well under default 16. Verifies depth tracking
        // doesn't fire false positives on legitimate fan-out.
        var rules = new[]
        {
            MakeCallerRule("r1", "r2"),
            MakeCallerRule("r2", "r3"),
            MakeCallerRule("r3", "r4"),
            MakeCallerRule("r4", "r5"),
            MakeLeafRule("r5"),
        };
        var src = new InMemoryRuleSource();
        foreach (var r in rules) src.Add(r);

        var env = await new RuleRunner().RunAsync(rules[0], Json("{}"),
            new RuleRunner.Options(SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(42, env.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Depth_limit_exceeded_yields_error()
    {
        // 4-level chain with MaxSubRuleDepth=2 — should fail at the 3rd nested call.
        var rules = new[]
        {
            MakeCallerRule("rd-a", "rd-b"),
            MakeCallerRule("rd-b", "rd-c"),
            MakeCallerRule("rd-c", "rd-d"),
            MakeLeafRule("rd-d"),
        };
        var src = new InMemoryRuleSource();
        foreach (var r in rules) src.Add(r);

        var env = await new RuleRunner().RunAsync(rules[0], Json("{}"),
            new RuleRunner.Options(SubRuleSource: src, MaxSubRuleDepth: 2, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("depth limit exceeded", err);
        Assert.Contains("(2)", err);
    }

    [Fact]
    public async Task Three_rule_cycle_A_B_C_A_detected()
    {
        var rA = MakeCallerRule("c-a", "c-b");
        var rB = MakeCallerRule("c-b", "c-c");
        var rC = MakeCallerRule("c-c", "c-a");
        var src = new InMemoryRuleSource().Add(rA).Add(rB).Add(rC);

        var env = await new RuleRunner().RunAsync(rA, Json("{}"),
            new RuleRunner.Options(SubRuleSource: src, Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("cycle detected", err);
    }
}
