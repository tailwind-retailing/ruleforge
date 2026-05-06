using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Constant nodes emit a literal JSON value. Implementation is in
/// <c>RuleRunner.cs</c> case <c>NodeCategory.Constant</c> at line 322
/// (one-liner: read the <c>value</c> field from <c>node.Data.Config</c>).
/// These tests document the supported value shapes and the empty-config
/// fall-through behavior.
/// </summary>
public class ConstantNodeTests
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

    private static RuleNode ConstantNode(string id, string configJson) =>
        new(id, "constant", new(0, 0), new(id, NodeCategory.Constant, Config: Json(configJson)));

    [Fact]
    public async Task Constant_emits_literal_number()
    {
        var rule = BuildLinearRule(ConstantNode("k", """{"value":42}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(42, env.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Constant_emits_literal_string()
    {
        var rule = BuildLinearRule(ConstantNode("k", """{"value":"AED"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal("AED", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Constant_emits_literal_boolean()
    {
        var rule = BuildLinearRule(ConstantNode("k", """{"value":true}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.True(env.Result!.Value.GetBoolean());
    }

    [Fact]
    public async Task Constant_emits_literal_object()
    {
        var rule = BuildLinearRule(ConstantNode("k", """{"value":{"taxRate":0.15,"currency":"USD"}}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(0.15, env.Result!.Value.GetProperty("taxRate").GetDouble());
        Assert.Equal("USD", env.Result.Value.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Constant_emits_literal_array()
    {
        var rule = BuildLinearRule(ConstantNode("k", """{"value":[1,2,3]}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, arr);
    }

    [Fact]
    public async Task Constant_with_null_config_falls_through_to_null_output()
    {
        // No config at all — current implementation reads `value` field via
        // ReadConfigField and returns null when Config is missing. The rule
        // still applies; downstream consumers see no upstream object.
        var k = new RuleNode("k", "constant", new(0, 0), new("k", NodeCategory.Constant));
        var rule = BuildLinearRule(k);
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.True(env.Result is null || env.Result.Value.ValueKind == JsonValueKind.Null);
    }
}
