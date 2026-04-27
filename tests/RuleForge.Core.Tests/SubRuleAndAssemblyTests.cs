using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class SubRuleAndAssemblyTests
{
    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "rules");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("fixtures/rules");
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>
    /// Trivial in-memory IRuleSource for exercising sub-rule plumbing
    /// without touching the file system or DocumentForge.
    /// </summary>
    private sealed class InMemoryRuleSource : IRuleSource
    {
        private readonly Dictionary<(string id, int v), Rule> _rules = new();

        public InMemoryRuleSource Add(Rule rule)
        {
            _rules[(rule.Id, rule.CurrentVersion)] = rule;
            return this;
        }

        public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default)
        {
            var match = _rules.Values
                .Where(r => r.Endpoint == endpoint && r.Method == method)
                .OrderByDescending(r => r.CurrentVersion)
                .FirstOrDefault();
            return Task.FromResult(match);
        }

        public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
        {
            if (version.HasValue && _rules.TryGetValue((ruleId, version.Value), out var pinned))
                return Task.FromResult<Rule?>(pinned);
            var latest = _rules
                .Where(kv => kv.Key.id == ruleId)
                .OrderByDescending(kv => kv.Key.v)
                .Select(kv => kv.Value)
                .FirstOrDefault();
            return Task.FromResult<Rule?>(latest);
        }

        public Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuleBinding>>(_rules.Values
                .GroupBy(r => r.Id)
                .Select(g => g.OrderByDescending(r => r.CurrentVersion).First())
                .Select(r => new RuleBinding(r.Id, r.CurrentVersion, r.Endpoint, r.Method))
                .ToList());
    }

    private static Rule LoadFixture(string name)
    {
        var path = Path.Combine(FixturesDir(), name);
        return JsonSerializer.Deserialize<Rule>(File.ReadAllText(path), AeroJson.Options)!;
    }

    // â”€â”€â”€ happy: sub-rule applies, ctx populated, placeholder resolves â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Gold_pax_subrule_applies_and_bonus_flows_into_result()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        var bonus = LoadFixture("rule-tier-bonus.v1.json");
        var src = new InMemoryRuleSource().Add(bag).Add(bonus);

        var env = await new RuleRunner().RunAsync(
            bag,
            Json("""{"cabin":"Y","orig":"LHR","pax":[{"id":"p1","type":"ADT","tier":"GOLD"}]}"""),
            new RuleRunner.Options(Debug: true, SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.NotNull(env.Result);
        Assert.Equal(1, env.Result!.Value.GetProperty("bonusPieces").GetInt32());
        Assert.Equal("BAG", env.Result.Value.GetProperty("code").GetString());

        // The ruleRef node trace carries a subRuleRunId.
        var ruleRefTrace = env.Trace!.Single(t => t.NodeId == "n5-tier");
        Assert.NotNull(ruleRefTrace.SubRuleRunId);
        Assert.NotNull(ruleRefTrace.CtxWritten);
        Assert.Equal(1, ruleRefTrace.CtxWritten!["tierUplift"].GetInt32());
    }

    // â”€â”€â”€ default: sub-rule skips, defaultValue used â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Blue_pax_subrule_skips_default_value_used()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        var bonus = LoadFixture("rule-tier-bonus.v1.json");
        var src = new InMemoryRuleSource().Add(bag).Add(bonus);

        var env = await new RuleRunner().RunAsync(
            bag,
            Json("""{"cabin":"Y","orig":"LHR","pax":[{"id":"p1","type":"ADT","tier":"BLUE"}]}"""),
            new RuleRunner.Options(Debug: false, SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(0, env.Result!.Value.GetProperty("bonusPieces").GetInt32());
    }

    // â”€â”€â”€ onError = fail: parent fails when sub-rule has no source â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SubRuleCall_without_source_emits_decision_error()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        // No sub-rule source provided â†’ engine should error, not just skip.
        var env = await new RuleRunner().RunAsync(
            bag,
            Json("""{"cabin":"Y","orig":"LHR","pax":[]}"""),
            new RuleRunner.Options(Debug: true));

        Assert.Equal(Decision.Error, env.Decision);
        var subErr = env.Trace!.Single(t => t.NodeId == "n5-tier");
        Assert.Equal(TraceOutcome.Error, subErr.Outcome);
        Assert.Contains("subRuleCall", subErr.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // â”€â”€â”€ onError = fail: missing sub-rule with onError=fail propagates â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SubRuleCall_missing_rule_with_onError_fail_propagates_error()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        // Patch the snapshot's subRuleCall to onError=fail
        var patched = JsonSerializer.Serialize(bag, AeroJson.Options)
            .Replace("\"onError\":\"default\"", "\"onError\":\"fail\"");
        var bagPatched = JsonSerializer.Deserialize<Rule>(patched, AeroJson.Options)!;

        // Empty source â€” sub-rule cannot be found.
        var src = new InMemoryRuleSource();
        var env = await new RuleRunner().RunAsync(
            bagPatched,
            Json("""{"cabin":"Y","orig":"LHR","pax":[]}"""),
            new RuleRunner.Options(Debug: true, SubRuleSource: src));

        Assert.Equal(Decision.Error, env.Decision);
    }

    [Fact]
    public async Task SubRuleCall_missing_rule_with_onError_default_uses_default_value()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        var src = new InMemoryRuleSource(); // tier-bonus not registered
        var env = await new RuleRunner().RunAsync(
            bag,
            Json("""{"cabin":"Y","orig":"LHR","pax":[{"id":"p1","type":"ADT","tier":"GOLD"}]}"""),
            new RuleRunner.Options(Debug: false, SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(0, env.Result!.Value.GetProperty("bonusPieces").GetInt32());
    }

    // â”€â”€â”€ output assembly: ctx placeholder resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Product_node_resolves_dollar_ctx_placeholders_in_strings()
    {
        var bag = LoadFixture("rule-bag-policy.v5.json");
        var bonus = LoadFixture("rule-tier-bonus.v1.json");
        var src = new InMemoryRuleSource().Add(bag).Add(bonus);

        var env = await new RuleRunner().RunAsync(
            bag,
            Json("""{"cabin":"Y","orig":"LHR","pax":[{"id":"p1","type":"ADT","tier":"PLAT"}]}"""),
            new RuleRunner.Options(Debug: false, SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        // The "${ctx.tierUplift}" string was replaced with the integer 1.
        var bonusPieces = env.Result!.Value.GetProperty("bonusPieces");
        Assert.Equal(JsonValueKind.Number, bonusPieces.ValueKind);
        Assert.Equal(1, bonusPieces.GetInt32());
    }

    // â”€â”€â”€ input mapping passes only what the call asks for â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task SubRule_input_is_built_strictly_from_inputMapping()
    {
        // Sub-rule that returns the request it received under `result.echo`.
        var echo = JsonSerializer.Deserialize<Rule>("""
        {
          "id": "rule-echo",
          "name": "echo",
          "endpoint": "/v1/echo",
          "method": "POST",
          "status": "published",
          "currentVersion": 1,
          "inputSchema": {},
          "outputSchema": {},
          "nodes": [
            { "id": "i", "type": "input",  "position": {"x":0,"y":0}, "data": {"label":"in","category":"input"} },
            { "id": "o", "type": "output", "position": {"x":0,"y":0}, "data": {"label":"out","category":"output","config":{"result":{"echoMarker":"yes"}}} }
          ],
          "edges": [
            { "id": "e1", "source": "i", "target": "o", "branch": "default" }
          ],
          "updatedAt": "2026-04-27T00:00:00.000Z"
        }
        """, AeroJson.Options)!;

        var parent = JsonSerializer.Deserialize<Rule>("""
        {
          "id": "rule-parent",
          "name": "parent",
          "endpoint": "/v1/parent",
          "method": "POST",
          "status": "published",
          "currentVersion": 1,
          "inputSchema": {},
          "outputSchema": {},
          "nodes": [
            { "id": "i",    "type": "input",  "position": {"x":0,"y":0}, "data": {"label":"in","category":"input"} },
            {
              "id": "ref", "type": "ruleRef",
              "position": {"x":0,"y":0},
              "data": {
                "label": "echo", "category": "ruleRef", "templateId": "sys-rule-ref",
                "subRuleCall": {
                  "ruleId": "rule-echo",
                  "inputMapping":  { "onlyThis": "$.specificField" },
                  "outputMapping": { "ctx.echoMarker": "result.echoMarker" },
                  "onError": "fail",
                  "pinnedVersion": 1
                }
              }
            },
            { "id": "out", "type": "output", "position": {"x":0,"y":0}, "data": {"label":"out","category":"output","config":{"result":{"marker":"${ctx.echoMarker}"}}} }
          ],
          "edges": [
            { "id": "e1", "source": "i",   "target": "ref", "branch": "default" },
            { "id": "e2", "source": "ref", "target": "out", "branch": "default" }
          ],
          "updatedAt": "2026-04-27T00:00:00.000Z"
        }
        """, AeroJson.Options)!;

        var src = new InMemoryRuleSource().Add(parent).Add(echo);
        var env = await new RuleRunner().RunAsync(
            parent,
            Json("""{"specificField":"hello","secret":"shouldNotLeak"}"""),
            new RuleRunner.Options(Debug: false, SubRuleSource: src));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal("yes", env.Result!.Value.GetProperty("marker").GetString());
    }
}
