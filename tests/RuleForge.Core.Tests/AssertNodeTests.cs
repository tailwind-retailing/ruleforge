using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Assert node — fails the rule with a structured error if condition is
/// falsy, otherwise passes the upstream value through unchanged.
/// </summary>
public class AssertNodeTests
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

    private static RuleNode AssertNode(string id, string configJson) =>
        new(id, "assert", new(0, 0), new(id, NodeCategory.Assert, Config: Json(configJson)));

    private static RuleNode ConstantNode(string id, string configJson) =>
        new(id, "constant", new(0, 0), new(id, NodeCategory.Constant, Config: Json(configJson)));

    // ─── pass-through ──────────────────────────────────────────────────────

    [Fact]
    public async Task Truthy_condition_passes_upstream_through()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":{"amount":100,"currency":"USD"}}"""),
            AssertNode("a", """{"condition":"amount > 0"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(100, env.Result!.Value.GetProperty("amount").GetInt32());
        Assert.Equal("USD", env.Result.Value.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Literal_true_condition_passes()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":42}"""),
            AssertNode("a", """{"condition":"true"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(42, env.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Condition_referencing_request_resolves()
    {
        var rule = BuildLinearRule(
            AssertNode("a", """{"condition":"amount > 0"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("""{"amount":150}"""));
        Assert.Equal(Decision.Apply, env.Decision);
    }

    [Fact]
    public async Task Numeric_zero_is_falsy()
    {
        var rule = BuildLinearRule(
            AssertNode("a", """{"condition":"0"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    [Fact]
    public async Task Numeric_nonzero_is_truthy()
    {
        var rule = BuildLinearRule(
            AssertNode("a", """{"condition":"1"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(Decision.Apply, env.Decision);
    }

    // ─── failure paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task Falsy_condition_yields_error_with_default_code()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":{"amount":-5}}"""),
            AssertNode("a", """{"condition":"amount > 0"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("ASSERT_FAILED", err);
        Assert.Contains("amount > 0", err);
    }

    [Fact]
    public async Task Custom_errorCode_and_message_appear_in_trace()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":{"amount":-5}}"""),
            AssertNode("a", """
                {
                  "condition": "amount > 0",
                  "errorCode": "INVALID_AMOUNT",
                  "errorMessage": "Amount must be positive (got negative)"
                }
                """));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("INVALID_AMOUNT", err);
        Assert.Contains("Amount must be positive", err);
    }

    [Fact]
    public async Task Missing_condition_yields_error()
    {
        var rule = BuildLinearRule(
            AssertNode("a", """{"errorCode":"X"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("missing condition", err);
    }

    [Fact]
    public async Task Bad_expression_yields_error()
    {
        var rule = BuildLinearRule(
            AssertNode("a", """{"condition":"((((bad syntax"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
    }
}
