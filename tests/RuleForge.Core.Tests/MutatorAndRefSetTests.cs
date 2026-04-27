using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class MutatorAndRefSetTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private sealed class InMemoryRefSetSource : IReferenceSetSource
    {
        private readonly Dictionary<string, ReferenceSet> _sets = new();
        public InMemoryRefSetSource Add(ReferenceSet rs) { _sets[rs.Id] = rs; return this; }
        public Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default) =>
            Task.FromResult(_sets.GetValueOrDefault(referenceId));
    }

    private static Rule BuildLinearRule(params RuleNode[] middle)
    {
        var nodes = new List<RuleNode>();
        nodes.Add(new RuleNode("i", "input", new(0,0), new("in", NodeCategory.Input)));
        nodes.AddRange(middle);
        nodes.Add(new RuleNode("o", "output", new(0,0), new("out", NodeCategory.Output)));

        var edges = new List<RuleEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new RuleEdge($"e{i}", nodes[i].Id, nodes[i+1].Id, EdgeBranch.Default));

        return new Rule(
            "rule-test", "test", "/x", HttpMethodKind.POST,
            RuleStatus.Published, 1,
            JsonDocument.Parse("{}").RootElement,
            JsonDocument.Parse("{}").RootElement,
            nodes, edges, "2026-04-27T00:00:00.000Z");
    }

    // â”€â”€â”€ set-property â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Set_property_with_literal_value_overrides_field()
    {
        var product = new RuleNode("p", "product", new(0,0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"fee":0,"currency":"AED"}}""")));
        var setter = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""{"target":"fee","value":42}""")));

        var rule = BuildLinearRule(product, setter);
        var env = await new RuleRunner().RunAsync(rule, Json("""{}"""));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(42, env.Result!.Value.GetProperty("fee").GetInt32());
        Assert.Equal("AED", env.Result.Value.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Set_property_with_from_path_reads_request()
    {
        var product = new RuleNode("p", "product", new(0,0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"pieces":0}}""")));
        var setter = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""{"target":"pieces","from":"$.bagPieces"}""")));

        var rule = BuildLinearRule(product, setter);
        var env = await new RuleRunner().RunAsync(rule, Json("""{"bagPieces":3}"""));

        Assert.Equal(3, env.Result!.Value.GetProperty("pieces").GetInt32());
    }

    [Fact]
    public async Task Mutator_with_no_upstream_object_starts_from_empty()
    {
        // No product upstream â€” mutator's input node output is an object (the request),
        // but the mutator has no incoming-from-product. baseObj should still work.
        var setter = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""{"target":"hello","value":"world"}""")));

        var rule = BuildLinearRule(setter);
        var env = await new RuleRunner().RunAsync(rule, Json("""{}"""));

        Assert.Equal("world", env.Result!.Value.GetProperty("hello").GetString());
    }

    // â”€â”€â”€ lookup-replace â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task Lookup_replace_pulls_value_from_ref_set()
    {
        var refSet = new ReferenceSet(
            "ref-fees", "fees",
            new[] { "route", "fee" },
            new[]
            {
                new Dictionary<string, JsonElement>
                {
                    ["route"] = Json("\"LHR-DXB\""),
                    ["fee"]   = Json("450"),
                } as IReadOnlyDictionary<string, JsonElement>,
                new Dictionary<string, JsonElement>
                {
                    ["route"] = Json("\"SYD-DXB\""),
                    ["fee"]   = Json("600"),
                } as IReadOnlyDictionary<string, JsonElement>,
            },
            1);

        var product = new RuleNode("p", "product", new(0,0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"fee":0,"code":"BAG"}}""")));
        var lookup = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""
            {
              "target": "fee",
              "lookup": {
                "referenceId": "ref-fees",
                "valueColumn": "fee",
                "matchOn": { "route": "$.route" }
              },
              "onMissing": "leave"
            }
            """)));

        var rule = BuildLinearRule(product, lookup);
        var env = await new RuleRunner().RunAsync(
            rule,
            Json("""{"route":"LHR-DXB"}"""),
            new RuleRunner.Options(ReferenceSetSource: new InMemoryRefSetSource().Add(refSet)));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(450, env.Result!.Value.GetProperty("fee").GetInt32());
        Assert.Equal("BAG", env.Result.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Lookup_replace_no_match_with_onMissing_leave_keeps_original()
    {
        var refSet = new ReferenceSet(
            "ref-fees", "fees",
            new[] { "route", "fee" },
            new[]
            {
                new Dictionary<string, JsonElement>
                {
                    ["route"] = Json("\"LHR-DXB\""),
                    ["fee"]   = Json("450"),
                } as IReadOnlyDictionary<string, JsonElement>,
            },
            1);

        var product = new RuleNode("p", "product", new(0,0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"fee":99,"code":"BAG"}}""")));
        var lookup = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""
            {
              "target": "fee",
              "lookup": {
                "referenceId": "ref-fees",
                "valueColumn": "fee",
                "matchOn": { "route": "$.route" }
              },
              "onMissing": "leave"
            }
            """)));

        var rule = BuildLinearRule(product, lookup);
        var env = await new RuleRunner().RunAsync(
            rule,
            Json("""{"route":"DOH-DXB"}"""),
            new RuleRunner.Options(ReferenceSetSource: new InMemoryRefSetSource().Add(refSet)));

        Assert.Equal(99, env.Result!.Value.GetProperty("fee").GetInt32());
    }

    [Fact]
    public async Task Lookup_replace_no_match_with_onMissing_error_emits_decision_error()
    {
        var refSet = new ReferenceSet("ref-empty", "empty",
            new[] { "k", "v" }, Array.Empty<IReadOnlyDictionary<string, JsonElement>>(), 1);

        var product = new RuleNode("p", "product", new(0,0), new("p", NodeCategory.Product,
            Config: Json("""{"output":{"v":1}}""")));
        var lookup = new RuleNode("m", "mutator", new(0,0), new("m", NodeCategory.Mutator,
            Config: Json("""
            {
              "target": "v",
              "lookup": {
                "referenceId": "ref-empty",
                "valueColumn": "v",
                "matchOn": { "k": "$.k" }
              },
              "onMissing": "error"
            }
            """)));

        var rule = BuildLinearRule(product, lookup);
        var env = await new RuleRunner().RunAsync(
            rule,
            Json("""{"k":"any"}"""),
            new RuleRunner.Options(ReferenceSetSource: new InMemoryRefSetSource().Add(refSet)));

        Assert.Equal(Decision.Error, env.Decision);
    }

    // â”€â”€â”€ routing: ListBindingsAsync â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task LocalFileRuleSource_lists_all_bindings()
    {
        var dir = FindFixturesDir();
        var src = new LocalFileRuleSource(dir);
        var bindings = await src.ListBindingsAsync();
        Assert.Contains(bindings, b => b.Endpoint == "/v1/ancillary/bag-policy" && b.Method == HttpMethodKind.POST);
        Assert.Contains(bindings, b => b.Endpoint == "/v1/ancillary/tier-bonus" && b.Method == HttpMethodKind.POST);
    }

    private static string FindFixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var c = Path.Combine(dir, "fixtures", "rules");
            if (Directory.Exists(c)) return c;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("fixtures/rules");
    }
}
