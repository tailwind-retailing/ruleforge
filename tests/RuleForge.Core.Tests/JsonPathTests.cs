using System.Text.Json;
using RuleForge.Core.Evaluators;
using Xunit;

namespace RuleForge.Core.Tests;

public class JsonPathTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Root_returns_input()
    {
        var root = Json("""{"a":1}""");
        var r = JsonPath.Resolve(root, "$");
        Assert.Single(r);
    }

    [Fact]
    public void Tolerates_missing_dollar_and_leading_dot()
    {
        var root = Json("""{"a":{"b":7}}""");
        Assert.Equal("7", JsonPath.Resolve(root, "$.a.b")[0]!.Value.GetRawText());
        Assert.Equal("7", JsonPath.Resolve(root, ".a.b")[0]!.Value.GetRawText());
        Assert.Equal("7", JsonPath.Resolve(root, "a.b")[0]!.Value.GetRawText());
    }

    [Fact]
    public void Bracket_property_and_quoted_property()
    {
        var root = Json("""{"a":{"b":7}}""");
        Assert.Equal("7", JsonPath.Resolve(root, "$['a']['b']")[0]!.Value.GetRawText());
        Assert.Equal("7", JsonPath.Resolve(root, "$[\"a\"][\"b\"]")[0]!.Value.GetRawText());
    }

    [Fact]
    public void Indexed_array_access()
    {
        var root = Json("""{"xs":["a","b","c"]}""");
        Assert.Equal("\"b\"", JsonPath.Resolve(root, "$.xs[1]")[0]!.Value.GetRawText());
    }

    [Fact]
    public void Wildcard_expands_arrays()
    {
        var root = Json("""{"pax":[{"tier":"GOLD"},{"tier":"BLUE"}]}""");
        var tiers = JsonPath.Resolve(root, "$.pax[*].tier")
            .Select(e => e!.Value.GetString())
            .ToArray();
        Assert.Equal(new[] { "GOLD", "BLUE" }, tiers);
    }

    [Fact]
    public void Missing_property_yields_empty()
    {
        var root = Json("""{"a":1}""");
        Assert.Empty(JsonPath.Resolve(root, "$.b"));
    }

    [Fact]
    public void Null_property_is_kept_as_json_null_not_undefined()
    {
        var root = Json("""{"a":null}""");
        var r = JsonPath.Resolve(root, "$.a");
        Assert.Single(r);
        Assert.Equal(JsonValueKind.Null, r[0]!.Value.ValueKind);
    }

    [Fact]
    public void Wildcard_keeps_null_and_drops_missing_subpath()
    {
        var root = Json("""{"pax":[{"tier":null},{"name":"x"},{"tier":"GOLD"}]}""");
        var tiers = JsonPath.Resolve(root, "$.pax[*].tier");
        // pax[0].tier is null â†’ kept; pax[1].tier is missing â†’ dropped; pax[2].tier="GOLD"
        Assert.Equal(2, tiers.Count);
        Assert.Equal(JsonValueKind.Null, tiers[0]!.Value.ValueKind);
        Assert.Equal("GOLD", tiers[1]!.Value.GetString());
    }

    [Fact]
    public void Dollar_ctx_prefix_is_stripped()
    {
        var root = Json("""{"foo":42}""");
        Assert.Equal("42", JsonPath.Resolve(root, "$ctx.foo")[0]!.Value.GetRawText());
    }
}
