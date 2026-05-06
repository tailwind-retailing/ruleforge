using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// switch — multi-way branch emitting a chosen case name as string.
/// groupBy — partitions an upstream array into a map of key → items.
/// </summary>
public class SwitchAndGroupByTests
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

    private static RuleNode SwitchNode(string id, string configJson) =>
        new(id, "switch", new(0, 0), new(id, NodeCategory.Switch, Config: Json(configJson)));

    private static RuleNode GroupByNode(string id, string configJson) =>
        new(id, "groupBy", new(0, 0), new(id, NodeCategory.GroupBy, Config: Json(configJson)));

    // ─── switch ────────────────────────────────────────────────────────────

    private static string SwitchConfigJson => """
        {
          "input": "$.paxType",
          "cases": [
            { "match": "ADT", "name": "adult" },
            { "match": "CHD", "name": "child" },
            { "match": "INF", "name": "infant" }
          ],
          "default": "unknown"
        }
        """;

    [Fact]
    public async Task Switch_first_case_matches()
    {
        var rule = BuildLinearRule(SwitchNode("s", SwitchConfigJson));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"paxType":"ADT"}"""));
        Assert.Equal("adult", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Switch_middle_case_matches()
    {
        var rule = BuildLinearRule(SwitchNode("s", SwitchConfigJson));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"paxType":"CHD"}"""));
        Assert.Equal("child", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Switch_last_case_matches()
    {
        var rule = BuildLinearRule(SwitchNode("s", SwitchConfigJson));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"paxType":"INF"}"""));
        Assert.Equal("infant", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Switch_default_used_when_no_case_matches()
    {
        var rule = BuildLinearRule(SwitchNode("s", SwitchConfigJson));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"paxType":"OTHER"}"""));
        Assert.Equal("unknown", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Switch_no_match_no_default_yields_error()
    {
        var rule = BuildLinearRule(SwitchNode("s", """
            { "input": "$.x",
              "cases": [{ "match": "a", "name": "got-a" }] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"x":"b"}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("no case matched", err);
    }

    [Fact]
    public async Task Switch_numeric_match_works()
    {
        var rule = BuildLinearRule(SwitchNode("s", """
            { "input": "$.tier",
              "cases": [
                { "match": 1, "name": "gold" },
                { "match": 2, "name": "silver" }
              ] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"tier":2}"""));
        Assert.Equal("silver", env.Result!.Value.GetString());
    }

    [Fact]
    public async Task Switch_missing_input_yields_error()
    {
        var rule = BuildLinearRule(SwitchNode("s", """
            { "cases": [{ "match": "a", "name": "x" }] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    [Fact]
    public async Task Switch_empty_cases_yields_error()
    {
        var rule = BuildLinearRule(SwitchNode("s", """
            { "input": "$.x", "cases": [], "default": "z" }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"x":"a"}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    // ─── groupBy ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GroupBy_partitions_by_key()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """
                {"value":[
                  {"id":"p1","type":"ADT"},
                  {"id":"p2","type":"CHD"},
                  {"id":"p3","type":"ADT"},
                  {"id":"p4","type":"INF"}
                ]}
                """),
            GroupByNode("g", """{"groupKey":"$.type"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var groups = env.Result!.Value;

        Assert.Equal(2, groups.GetProperty("ADT").GetArrayLength());
        Assert.Equal(1, groups.GetProperty("CHD").GetArrayLength());
        Assert.Equal(1, groups.GetProperty("INF").GetArrayLength());
        Assert.Equal("p1", groups.GetProperty("ADT")[0].GetProperty("id").GetString());
        Assert.Equal("p3", groups.GetProperty("ADT")[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GroupBy_single_group()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"k":"x","v":1},{"k":"x","v":2}]}"""),
            GroupByNode("g", """{"groupKey":"$.k"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var groups = env.Result!.Value;
        Assert.Equal(2, groups.GetProperty("x").GetArrayLength());
    }

    [Fact]
    public async Task GroupBy_empty_array_yields_empty_object()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[]}"""),
            GroupByNode("g", """{"groupKey":"$.k"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(JsonValueKind.Object, env.Result!.Value.ValueKind);
        Assert.Empty(env.Result.Value.EnumerateObject());
    }

    [Fact]
    public async Task GroupBy_missing_groupKey_yields_error()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"id":1}]}"""),
            GroupByNode("g", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    [Fact]
    public async Task GroupBy_preserves_first_seen_group_order()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """
                {"value":[
                  {"k":"second","v":1},
                  {"k":"first","v":2},
                  {"k":"second","v":3},
                  {"k":"third","v":4}
                ]}
                """),
            GroupByNode("g", """{"groupKey":"$.k"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var keys = env.Result!.Value.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "second", "first", "third" }, keys);
    }
}
