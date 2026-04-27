using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class RuleRunnerIntegrationTests
{
    private static string FixturesDir()
    {
        // Walk up from the test bin folder until we find the repo root marker.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "rules");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("could not locate fixtures/rules");
    }

    private static (Rule rule, RuleRunner runner) Load()
    {
        // Pin to v3 (no `within_next` clock dependency) so the integration
        // tests stay deterministic regardless of which fixture the local
        // bindings file currently points at.
        var path = Path.Combine(FixturesDir(), "rule-bag-policy.v3.json");
        var rule = JsonSerializer.Deserialize<Rule>(File.ReadAllText(path), AeroJson.Options);
        Assert.NotNull(rule);
        return (rule!, new RuleRunner());
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Happy_path_cabin_Y_orig_LHR_applies_with_hardcoded_result()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"Y","orig":"LHR","bagPieces":2,"pax":[{"id":"p1"}]}""");

        var env = runner.Run(rule, req, debug: true);

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal("rule-bag-policy", env.RuleId);
        Assert.Equal(3, env.RuleVersion);
        Assert.NotNull(env.Result);
        Assert.Equal("BAG", env.Result!.Value.GetProperty("code").GetString());
        Assert.Equal(2, env.Result.Value.GetProperty("pieces").GetInt32());
        // 6 nodes traced: input, cabin, orig, bagpieces, AND, output
        Assert.Equal(6, env.Trace!.Count);
        Assert.All(env.Trace, t => Assert.Equal(TraceOutcome.Pass, t.Outcome));
    }

    [Fact]
    public void Wrong_route_skips_with_null_result()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"Y","orig":"SYD","bagPieces":2,"pax":[{"id":"p1"}]}""");

        var env = runner.Run(rule, req, debug: true);

        Assert.Equal(Decision.Skip, env.Decision);
        Assert.Null(env.Result);
        var fired = env.Trace!.Select(t => t.NodeId).ToList();
        Assert.Contains("n2-cabin", fired);
        Assert.Contains("n3-orig", fired);
        Assert.Contains("n5-and", fired);
        Assert.DoesNotContain("n6-output", fired);

        var andTrace = env.Trace!.Single(t => t.NodeId == "n5-and");
        Assert.Equal(TraceOutcome.Fail, andTrace.Outcome);
    }

    [Fact]
    public void Bag_pieces_out_of_range_skips()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"Y","orig":"LHR","bagPieces":9,"pax":[]}""");
        var env = runner.Run(rule, req);
        Assert.Equal(Decision.Skip, env.Decision);
    }

    [Fact]
    public void Wrong_cabin_also_skips()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"J","orig":"LHR","bagPieces":2,"pax":[]}""");
        var env = runner.Run(rule, req);
        Assert.Equal(Decision.Skip, env.Decision);
    }

    [Fact]
    public void Production_mode_omits_trace()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"Y","orig":"LHR","bagPieces":2,"pax":[]}""");
        var env = runner.Run(rule, req, debug: false);
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Null(env.Trace);
    }

    [Fact]
    public void Missing_bag_pieces_passes_via_onMissing_pass()
    {
        var (rule, runner) = Load();
        var req = Json("""{"cabin":"Y","orig":"LHR","pax":[]}""");
        var env = runner.Run(rule, req);
        Assert.Equal(Decision.Apply, env.Decision); // bagPieces filter onMissing=pass
    }

    [Fact]
    public void Legacy_flat_filter_config_is_rejected()
    {
        // Synthetic rule whose filter uses the legacy {path, operator} shape.
        var legacyJson = """
        {
          "id": "rule-legacy",
          "name": "legacy",
          "endpoint": "/x",
          "method": "POST",
          "status": "published",
          "currentVersion": 1,
          "inputSchema": {},
          "outputSchema": {},
          "nodes": [
            { "id": "i", "type": "input",  "position": {"x":0,"y":0}, "data": {"label":"in", "category":"input"} },
            { "id": "f", "type": "filter", "position": {"x":0,"y":0}, "data": {"label":"f", "category":"filter", "config": {"path":"$.x","operator":"in","referenceId":"r"}} },
            { "id": "o", "type": "output", "position": {"x":0,"y":0}, "data": {"label":"out","category":"output"} }
          ],
          "edges": [
            { "id": "e1", "source": "i", "target": "f" },
            { "id": "e2", "source": "f", "target": "o", "branch": "pass" }
          ],
          "updatedAt": "2026-04-27T00:00:00.000Z"
        }
        """;
        var rule = JsonSerializer.Deserialize<Rule>(legacyJson, AeroJson.Options)!;
        var f = rule.Nodes.Single(n => n.Id == "f");
        // Diagnose first: ensure config actually deserialised
        Assert.True(f.Data.Config.HasValue, "filter node config should not be null after deserialisation");
        Assert.Equal(JsonValueKind.Object, f.Data.Config!.Value.ValueKind);
        Assert.False(f.Data.Config.Value.TryGetProperty("source", out _));

        var env = new RuleRunner().Run(rule, Json("""{"x":"a"}"""), debug: true);
        // Filter throws â†’ envelope.decision = error, message in trace
        Assert.Equal(Decision.Error, env.Decision);
        var fTrace = env.Trace!.Single(t => t.NodeId == "f");
        Assert.Equal(TraceOutcome.Error, fTrace.Outcome);
        Assert.Contains("legacy", fTrace.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cycle_is_detected_at_validation()
    {
        var cycleJson = """
        {
          "id": "rule-cycle",
          "name": "cycle",
          "endpoint": "/c",
          "method": "POST",
          "status": "published",
          "currentVersion": 1,
          "inputSchema": {},
          "outputSchema": {},
          "nodes": [
            { "id": "i", "type": "input",  "position": {"x":0,"y":0}, "data": {"label":"in","category":"input"} },
            { "id": "a", "type": "logic",  "position": {"x":0,"y":0}, "data": {"label":"AND","category":"logic","templateId":"sys-and"} },
            { "id": "b", "type": "logic",  "position": {"x":0,"y":0}, "data": {"label":"AND","category":"logic","templateId":"sys-and"} },
            { "id": "o", "type": "output", "position": {"x":0,"y":0}, "data": {"label":"out","category":"output"} }
          ],
          "edges": [
            { "id": "e1", "source": "i", "target": "a" },
            { "id": "e2", "source": "a", "target": "b" },
            { "id": "e3", "source": "b", "target": "a" },
            { "id": "e4", "source": "a", "target": "o", "branch": "pass" }
          ],
          "updatedAt": "2026-04-27T00:00:00.000Z"
        }
        """;
        var rule = JsonSerializer.Deserialize<Rule>(cycleJson, AeroJson.Options)!;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new RuleRunner().Run(rule, Json("""{}""")));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
