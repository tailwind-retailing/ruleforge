using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class IterationTests
{
    private static string FixturesDir()
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

    private static string RefsDir() => Path.GetFullPath(Path.Combine(FixturesDir(), "..", "refs"));

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static (Rule rule, RuleRunner runner, IReferenceSetSource refs) LoadFixture(string fileName)
    {
        var path = Path.Combine(FixturesDir(), fileName);
        var rule = JsonSerializer.Deserialize<Rule>(File.ReadAllText(path), AeroJson.Options)!;
        return (rule, new RuleRunner(), new LocalFileReferenceSetSource(RefsDir()));
    }

    // ─── v8 tax engine — single iterator + reference-lookup mutator ─────────

    [Fact]
    public async Task Tax_engine_emits_one_line_per_pax_with_correct_amounts()
    {
        var (rule, runner, refs) = LoadFixture("rule-pnr-taxes.v1.json");
        var req = Json("""
            {
              "pnr": "TAX001",
              "orig": "LHR",
              "taxCode": "GB1",
              "pax": [
                { "id": "p1", "ageCategory": "ADT" },
                { "id": "p2", "ageCategory": "CHD" },
                { "id": "p3", "ageCategory": "INF" }
              ]
            }
        """);

        var env = await runner.RunAsync(rule, req,
            new RuleRunner.Options(Debug: false, ReferenceSetSource: refs));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.NotNull(env.Result);
        var arr = env.Result!.Value;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(3, arr.GetArrayLength());

        var entries = arr.EnumerateArray().ToList();
        Assert.Equal("p1", entries[0].GetProperty("paxId").GetString());
        Assert.Equal(26d, entries[0].GetProperty("amount").GetDouble());
        Assert.Equal("p2", entries[1].GetProperty("paxId").GetString());
        Assert.Equal(13d, entries[1].GetProperty("amount").GetDouble());
        Assert.Equal("p3", entries[2].GetProperty("paxId").GetString());
        Assert.Equal(0d,  entries[2].GetProperty("amount").GetDouble());
    }

    [Fact]
    public async Task Empty_iteration_source_produces_empty_array()
    {
        var (rule, runner, refs) = LoadFixture("rule-pnr-taxes.v1.json");
        var req = Json("""{"pnr":"X","orig":"LHR","taxCode":"GB1","pax":[]}""");
        var env = await runner.RunAsync(rule, req, new RuleRunner.Options(ReferenceSetSource: refs));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(0, env.Result!.Value.GetArrayLength());
    }

    [Fact]
    public async Task Trace_includes_one_entry_per_iteration()
    {
        var (rule, runner, refs) = LoadFixture("rule-pnr-taxes.v1.json");
        var req = Json("""
            {"orig":"LHR","taxCode":"GB1","pax":[{"id":"p1","ageCategory":"ADT"},{"id":"p2","ageCategory":"ADT"}]}
        """);
        var env = await runner.RunAsync(rule, req,
            new RuleRunner.Options(Debug: true, ReferenceSetSource: refs));

        // Each iteration's body runs once per pax: n3-base, n4-paxid, n5-amount.
        var amountTraces = env.Trace!.Where(t => t.NodeId == "n5-amount").ToList();
        Assert.Equal(2, amountTraces.Count);
    }

    // ─── nested iteration: journey × segment × pax ──────────────────────────

    [Fact]
    public async Task Nested_iteration_three_deep_produces_correct_combinations()
    {
        var (rule, runner, refs) = LoadFixture("rule-seat-assignments.v1.json");
        var req = Json("""
            {
              "pnr": "S1",
              "journeys": [
                { "id": "j1", "segments": [{ "id": "s1", "cabin": "Y" }] },
                { "id": "j2", "segments": [{ "id": "s2", "cabin": "J" }, { "id": "s3", "cabin": "Y" }] }
              ],
              "pax": [
                { "id": "p1" }, { "id": "p2" }
              ]
            }
        """);

        var env = await runner.RunAsync(rule, req,
            new RuleRunner.Options(ReferenceSetSource: refs));

        Assert.Equal(Decision.Apply, env.Decision);
        // Three explicit merges → nested array shape: journey[segment[pax]].
        var byJourney = env.Result!.Value;
        Assert.Equal(2, byJourney.GetArrayLength());

        var j1 = byJourney[0];
        Assert.Equal(1, j1.GetArrayLength());                 // 1 segment in j1
        Assert.Equal(2, j1[0].GetArrayLength());              // 2 pax per segment

        var j2 = byJourney[1];
        Assert.Equal(2, j2.GetArrayLength());                 // 2 segments in j2
        var j2Cabins = j2.EnumerateArray().Select(seg => seg[0].GetProperty("class").GetString()).ToList();
        Assert.Contains("Business", j2Cabins);
        Assert.Contains("Economy", j2Cabins);

        // Spot-check nested variable resolution:
        var firstLeaf = j1[0][0];
        Assert.Equal("j1", firstLeaf.GetProperty("journeyId").GetString());
        Assert.Equal("s1", firstLeaf.GetProperty("segmentId").GetString());
        Assert.Equal("p1", firstLeaf.GetProperty("paxId").GetString());
        Assert.Equal("Economy", firstLeaf.GetProperty("class").GetString());
    }

    // ─── merge mode tests (no fixture — direct in-memory rules) ─────────────

    private static Rule BuildIterMergeRule(MergeMode mode, string? field = null)
    {
        var modeStr = mode.ToString().ToLowerInvariant();
        var fieldFrag = field is null ? "" : ",\"field\":\"" + field + "\"";
        var json =
            "{" +
            "\"id\":\"r-merge-test\",\"name\":\"merge test\"," +
            "\"endpoint\":\"/x\",\"method\":\"POST\",\"status\":\"published\",\"currentVersion\":1," +
            "\"inputSchema\":{},\"outputSchema\":{}," +
            "\"nodes\":[" +
              "{\"id\":\"i\",\"type\":\"input\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"in\",\"category\":\"input\"}}," +
              "{\"id\":\"fe\",\"type\":\"iterator\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"fe\",\"category\":\"iterator\",\"config\":{\"source\":\"$.values\",\"as\":\"v\"}}}," +
              "{\"id\":\"k\",\"type\":\"constant\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"k\",\"category\":\"constant\",\"config\":{\"value\":{\"x\":0}}}}," +
              "{\"id\":\"set\",\"type\":\"mutator\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"set\",\"category\":\"mutator\",\"config\":{\"target\":\"x\",\"from\":\"$v\"}}}," +
              "{\"id\":\"m\",\"type\":\"merge\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"m\",\"category\":\"merge\",\"config\":{\"mode\":\"" + modeStr + "\"" + fieldFrag + "}}}," +
              "{\"id\":\"o\",\"type\":\"output\",\"position\":{\"x\":0,\"y\":0},\"data\":{\"label\":\"out\",\"category\":\"output\"}}" +
            "]," +
            "\"edges\":[" +
              "{\"id\":\"e1\",\"source\":\"i\",\"target\":\"fe\",\"branch\":\"default\"}," +
              "{\"id\":\"e2\",\"source\":\"fe\",\"target\":\"k\",\"branch\":\"pass\"}," +
              "{\"id\":\"e3\",\"source\":\"k\",\"target\":\"set\",\"branch\":\"default\"}," +
              "{\"id\":\"e4\",\"source\":\"set\",\"target\":\"m\",\"branch\":\"default\"}," +
              "{\"id\":\"e5\",\"source\":\"m\",\"target\":\"o\",\"branch\":\"default\"}" +
            "]," +
            "\"updatedAt\":\"2026-04-27T00:00:00.000Z\"" +
            "}";
        return JsonSerializer.Deserialize<Rule>(json, AeroJson.Options)!;
    }

    [Theory]
    [InlineData(MergeMode.Count, null,  "[1,2,3,4,5]", 5)]
    [InlineData(MergeMode.Sum,  "$.x", "[1,2,3,4,5]", 15)]
    [InlineData(MergeMode.Min,  "$.x", "[3,1,4,1,5]", 1)]
    [InlineData(MergeMode.Max,  "$.x", "[3,1,4,1,5]", 5)]
    public async Task Merge_modes_aggregate_correctly(MergeMode mode, string? field, string valuesJson, double expected)
    {
        var rule = BuildIterMergeRule(mode, field);
        var req = Json("{\"values\":" + valuesJson + "}");
        var env = await new RuleRunner().RunAsync(rule, req);
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(expected, env.Result!.Value.GetDouble(), 0.0001);
    }

    [Fact]
    public async Task Merge_avg_handles_empty_source()
    {
        var rule = BuildIterMergeRule(MergeMode.Avg, "$.x");
        var env = await new RuleRunner().RunAsync(rule, Json("""{"values":[]}"""));
        Assert.Equal(Decision.Apply, env.Decision);
    }

    [Fact]
    public async Task Merge_collect_default_returns_array()
    {
        var rule = BuildIterMergeRule(MergeMode.Collect);
        var env = await new RuleRunner().RunAsync(rule, Json("""{"values":["a","b","c"]}"""));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(JsonValueKind.Array, env.Result!.Value.ValueKind);
        Assert.Equal(3, env.Result.Value.GetArrayLength());
    }

    // ─── iteration variables in calc ────────────────────────────────────────

    [Fact]
    public async Task Calc_inside_iterator_resolves_iteration_vars()
    {
        // foreach v in $.values: out = v * 2 + $vIndex.
        var json = """
        {
          "id":"r-calc-iter","name":"calc-iter","endpoint":"/c","method":"POST","status":"published",
          "currentVersion":1,"inputSchema":{},"outputSchema":{},
          "nodes":[
            {"id":"i","type":"input","position":{"x":0,"y":0},"data":{"label":"i","category":"input"}},
            {"id":"fe","type":"iterator","position":{"x":0,"y":0},
              "data":{"label":"fe","category":"iterator","config":{"source":"$.values","as":"v"}}},
            {"id":"k","type":"constant","position":{"x":0,"y":0},
              "data":{"label":"k","category":"constant","config":{"value":{"x":0}}}},
            {"id":"calc","type":"calc","position":{"x":0,"y":0},
              "data":{"label":"calc","category":"calc","config":{"target":"x","expression":"v * 2 + vIndex"}}},
            {"id":"m","type":"merge","position":{"x":0,"y":0},
              "data":{"label":"m","category":"merge","config":{"mode":"sum","field":"$.x"}}},
            {"id":"o","type":"output","position":{"x":0,"y":0},"data":{"label":"o","category":"output"}}
          ],
          "edges":[
            {"id":"e1","source":"i","target":"fe","branch":"default"},
            {"id":"e2","source":"fe","target":"k","branch":"pass"},
            {"id":"e3","source":"k","target":"calc","branch":"default"},
            {"id":"e4","source":"calc","target":"m","branch":"default"},
            {"id":"e5","source":"m","target":"o","branch":"default"}
          ],
          "updatedAt":"2026-04-27T00:00:00.000Z"
        }
        """;
        var rule = JsonSerializer.Deserialize<Rule>(json, AeroJson.Options)!;
        // values=[10,20,30] -> [10*2+0, 20*2+1, 30*2+2] = [20, 41, 62] -> sum = 123
        var env = await new RuleRunner().RunAsync(rule, Json("""{"values":[10,20,30]}"""));
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(123d, env.Result!.Value.GetDouble());
    }

    // ─── multi-row reference node ───────────────────────────────────────────

    [Fact]
    public async Task Reference_node_returns_all_matching_rows()
    {
        var json = """
        {
          "id":"r-ref","name":"ref","endpoint":"/r","method":"POST","status":"published",
          "currentVersion":1,"inputSchema":{},"outputSchema":{},
          "nodes":[
            {"id":"i","type":"input","position":{"x":0,"y":0},"data":{"label":"i","category":"input"}},
            {"id":"r","type":"reference","position":{"x":0,"y":0},
              "data":{"label":"r","category":"reference","config":{
                "referenceId":"ref-tax-rates",
                "matchOn":{"origin":"$.orig"}
              }}},
            {"id":"o","type":"output","position":{"x":0,"y":0},"data":{"label":"o","category":"output"}}
          ],
          "edges":[
            {"id":"e1","source":"i","target":"r","branch":"default"},
            {"id":"e2","source":"r","target":"o","branch":"default"}
          ],
          "updatedAt":"2026-04-27T00:00:00.000Z"
        }
        """;
        var rule = JsonSerializer.Deserialize<Rule>(json, AeroJson.Options)!;
        var refs = new LocalFileReferenceSetSource(RefsDir());
        var env = await new RuleRunner().RunAsync(rule, Json("""{"orig":"LHR"}"""),
            new RuleRunner.Options(ReferenceSetSource: refs));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(JsonValueKind.Array, env.Result!.Value.ValueKind);
        // 6 LHR rows in ref-tax-rates (3 ages × 2 codes)
        Assert.Equal(6, env.Result.Value.GetArrayLength());
    }
}
