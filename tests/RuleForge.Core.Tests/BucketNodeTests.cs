using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Bucket node — deterministic sticky-hash A/B assignment. The same key
/// must always route to the same bucket; the distribution must respect
/// configured weights within tolerance over enough samples.
/// </summary>
public class BucketNodeTests
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

    private static RuleNode BucketNode(string id, string configJson) =>
        new(id, "bucket", new(0, 0), new(id, NodeCategory.Bucket, Config: Json(configJson)));

    // ─── determinism ───────────────────────────────────────────────────────

    [Fact]
    public async Task Same_key_always_routes_to_same_bucket()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.pnr",
              "buckets": [{"name":"a","weight":50},{"name":"b","weight":50}] }
            """));
        var runner = new RuleRunner();
        var env1 = await runner.RunAsync(rule, Json("""{"pnr":"ABC123"}"""));
        var env2 = await runner.RunAsync(rule, Json("""{"pnr":"ABC123"}"""));
        var env3 = await runner.RunAsync(rule, Json("""{"pnr":"ABC123"}"""));
        var picked = env1.Result!.Value.GetString();
        Assert.Equal(picked, env2.Result!.Value.GetString());
        Assert.Equal(picked, env3.Result!.Value.GetString());
    }

    [Fact]
    public async Task Single_bucket_always_picked()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.pnr",
              "buckets": [{"name":"only","weight":100}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"pnr":"X"}"""));
        Assert.Equal("only", env.Result!.Value.GetString());
    }

    // ─── distribution ──────────────────────────────────────────────────────

    [Fact]
    public async Task Even_split_distribution_within_tolerance()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":50},{"name":"b","weight":50}] }
            """));
        const int n = 2000;
        var runner = new RuleRunner();
        int aCount = 0, bCount = 0;
        for (var i = 0; i < n; i++)
        {
            var env = await runner.RunAsync(rule, Json($$"""{"id":"key-{{i}}"}"""));
            if (env.Result!.Value.GetString() == "a") aCount++; else bCount++;
        }
        // Expect ~50/50 within ±10% (FNV-1a is not cryptographic; tolerance loose).
        Assert.InRange(aCount, n * 4 / 10, n * 6 / 10);
        Assert.InRange(bCount, n * 4 / 10, n * 6 / 10);
    }

    [Fact]
    public async Task Weighted_distribution_skews_correctly()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":90},{"name":"b","weight":10}] }
            """));
        const int n = 2000;
        var runner = new RuleRunner();
        int aCount = 0, bCount = 0;
        for (var i = 0; i < n; i++)
        {
            var env = await runner.RunAsync(rule, Json($$"""{"id":"k-{{i}}"}"""));
            if (env.Result!.Value.GetString() == "a") aCount++; else bCount++;
        }
        // Expect ~90/10 within ±5% absolute on the 'a' count.
        Assert.InRange(aCount, n * 85 / 100, n * 95 / 100);
        Assert.InRange(bCount, n * 5 / 100, n * 15 / 100);
    }

    // ─── input variants ────────────────────────────────────────────────────

    [Fact]
    public async Task Numeric_hashKey_value_works()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":50},{"name":"b","weight":50}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"id":12345}"""));
        var picked = env.Result!.Value.GetString();
        Assert.True(picked == "a" || picked == "b");
    }

    [Fact]
    public async Task Zero_weight_bucket_never_picked()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":100},{"name":"never","weight":0}] }
            """));
        var runner = new RuleRunner();
        for (var i = 0; i < 200; i++)
        {
            var env = await runner.RunAsync(rule, Json($$"""{"id":"k-{{i}}"}"""));
            Assert.Equal("a", env.Result!.Value.GetString());
        }
    }

    // ─── error paths (use Debug:true to read the trace) ────────────────────

    [Fact]
    public async Task Missing_hashKey_yields_error()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "buckets": [{"name":"a","weight":50}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("hashKey", err);
    }

    [Fact]
    public async Task Empty_buckets_yields_error()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id", "buckets": [] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"id":"x"}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("buckets", err);
    }

    [Fact]
    public async Task Zero_total_weight_yields_error()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":0},{"name":"b","weight":0}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"id":"x"}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("weight", err);
    }

    [Fact]
    public async Task Negative_weight_yields_error()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.id",
              "buckets": [{"name":"a","weight":50},{"name":"b","weight":-1}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("""{"id":"x"}"""),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("negative weight", err);
    }

    [Fact]
    public async Task Missing_hashKey_value_in_request_yields_error()
    {
        var rule = BuildLinearRule(BucketNode("b", """
            { "hashKey": "$.pnr",
              "buckets": [{"name":"a","weight":50},{"name":"b","weight":50}] }
            """));
        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));
        Assert.Equal(Decision.Error, env.Decision);
        var err = env.Trace!.First(t => t.Outcome == TraceOutcome.Error).Error!;
        Assert.Contains("resolved to no value", err);
    }
}
