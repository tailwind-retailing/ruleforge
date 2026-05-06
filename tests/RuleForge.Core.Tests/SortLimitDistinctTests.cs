using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Sort, limit, and distinct nodes — array transforms that read a single
/// upstream JSON array, transform, and emit. Tests are grouped here
/// because they share the same upstream-array contract and helpers.
/// </summary>
public class SortLimitDistinctTests
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

    private static RuleNode SortNode(string id, string configJson) =>
        new(id, "sort", new(0, 0), new(id, NodeCategory.Sort, Config: Json(configJson)));

    private static RuleNode LimitNode(string id, string configJson) =>
        new(id, "limit", new(0, 0), new(id, NodeCategory.Limit, Config: Json(configJson)));

    private static RuleNode DistinctNode(string id, string configJson) =>
        new(id, "distinct", new(0, 0), new(id, NodeCategory.Distinct, Config: Json(configJson)));

    private static int[] AsIntArr(JsonElement el) =>
        el.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private static string[] AsStringArr(JsonElement el) =>
        el.EnumerateArray().Select(x => x.GetString()!).ToArray();

    // ─── sort ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_numbers_ascending_by_default()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[3,1,4,1,5,9,2,6]}"""),
            SortNode("s", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 1, 1, 2, 3, 4, 5, 6, 9 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Sort_numbers_descending()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[3,1,4,1,5,9,2,6]}"""),
            SortNode("s", """{"direction":"desc"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 9, 6, 5, 4, 3, 2, 1, 1 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Sort_strings_ascending()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":["banana","apple","cherry"]}"""),
            SortNode("s", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { "apple", "banana", "cherry" }, AsStringArr(env.Result!.Value));
    }

    [Fact]
    public async Task Sort_by_key_path()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"price":300},{"price":100},{"price":200}]}"""),
            SortNode("s", """{"sortKey":"$.price"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var prices = env.Result!.Value.EnumerateArray()
            .Select(x => x.GetProperty("price").GetInt32()).ToArray();
        Assert.Equal(new[] { 100, 200, 300 }, prices);
    }

    [Fact]
    public async Task Sort_empty_array_is_empty()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[]}"""),
            SortNode("s", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Empty(env.Result!.Value.EnumerateArray());
    }

    [Fact]
    public async Task Sort_nulls_last_by_default()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"price":2},{"price":null},{"price":1}]}"""),
            SortNode("s", """{"sortKey":"$.price"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, arr[2].GetProperty("price").ValueKind);
        Assert.Equal(1, arr[0].GetProperty("price").GetInt32());
        Assert.Equal(2, arr[1].GetProperty("price").GetInt32());
    }

    [Fact]
    public async Task Sort_nulls_first()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"price":2},{"price":null},{"price":1}]}"""),
            SortNode("s", """{"sortKey":"$.price","nulls":"first"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, arr[0].GetProperty("price").ValueKind);
    }

    [Fact]
    public async Task Sort_bad_direction_yields_error()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2]}"""),
            SortNode("s", """{"direction":"weird"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    [Fact]
    public async Task Sort_non_array_upstream_yields_error()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":42}"""),
            SortNode("s", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("not a JSON array", err);
    }

    // ─── limit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Limit_takes_first_N_when_count_lt_length()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2,3,4,5,6,7]}"""),
            LimitNode("l", """{"count":3}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 1, 2, 3 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Limit_returns_all_when_count_gte_length()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2,3]}"""),
            LimitNode("l", """{"count":10}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 1, 2, 3 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Limit_zero_returns_empty()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2,3]}"""),
            LimitNode("l", """{"count":0}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Empty(env.Result!.Value.EnumerateArray());
    }

    [Fact]
    public async Task Limit_with_offset_skips_then_takes()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[10,20,30,40,50]}"""),
            LimitNode("l", """{"count":2,"offset":2}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 30, 40 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Limit_negative_count_yields_error()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2,3]}"""),
            LimitNode("l", """{"count":-1}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
    }

    // ─── distinct ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Distinct_whole_element_dedups_numbers()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[1,2,2,3,1,4]}"""),
            DistinctNode("d", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 1, 2, 3, 4 }, AsIntArr(env.Result!.Value));
    }

    [Fact]
    public async Task Distinct_by_key_dedups_objects()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"id":"a","v":1},{"id":"b","v":2},{"id":"a","v":3}]}"""),
            DistinctNode("d", """{"key":"$.id"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var ids = env.Result!.Value.EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public async Task Distinct_keep_first_retains_first_occurrence()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"id":"a","v":1},{"id":"a","v":2}]}"""),
            DistinctNode("d", """{"key":"$.id","keep":"first"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().ToArray();
        Assert.Single(arr);
        Assert.Equal(1, arr[0].GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Distinct_keep_last_retains_last_occurrence()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"id":"a","v":1},{"id":"a","v":2}]}"""),
            DistinctNode("d", """{"key":"$.id","keep":"last"}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var arr = env.Result!.Value.EnumerateArray().ToArray();
        Assert.Single(arr);
        Assert.Equal(2, arr[0].GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Distinct_empty_array_returns_empty()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[]}"""),
            DistinctNode("d", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Empty(env.Result!.Value.EnumerateArray());
    }

    // ─── chained ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_then_limit_yields_top_n()
    {
        // Cheapest 3 fares pattern.
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[{"price":300},{"price":100},{"price":200},{"price":50},{"price":400}]}"""),
            SortNode("s", """{"sortKey":"$.price"}"""),
            LimitNode("l", """{"count":3}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        var prices = env.Result!.Value.EnumerateArray()
            .Select(x => x.GetProperty("price").GetInt32()).ToArray();
        Assert.Equal(new[] { 50, 100, 200 }, prices);
    }

    [Fact]
    public async Task Distinct_then_sort_dedups_then_orders()
    {
        var rule = BuildLinearRule(
            ConstantNode("k", """{"value":[3,1,2,1,3,2]}"""),
            DistinctNode("d", """{}"""),
            SortNode("s", """{}"""));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"));
        Assert.Equal(new[] { 1, 2, 3 }, AsIntArr(env.Result!.Value));
    }
}
