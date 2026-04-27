using System.Text.Json;
using RuleForge.Core.Evaluators;
using Xunit;

namespace RuleForge.Core.Tests;

public class CalcEvaluatorTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static IDictionary<string, JsonElement> Ctx(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in pairs) d[k] = Json(v);
        return d;
    }

    [Fact]
    public void Arithmetic_against_upstream_field()
    {
        // upstream.fee * (1 + markup) where markup comes from request
        var upstream = Json("""{"fee":100,"currency":"AED"}""");
        var ctx = Ctx();
        var request = Json("""{"markup":0.15}""");

        var result = CalcEvaluator.Evaluate("fee * (1 + markup)", upstream, ctx, request);

        Assert.Equal(JsonValueKind.Number, result!.Value.ValueKind);
        Assert.Equal(115d, result.Value.GetDouble(), 0.0001);
    }

    [Fact]
    public void Upstream_field_shadows_ctx_when_names_collide()
    {
        var upstream = Json("""{"x":10}""");
        var ctx = Ctx(("x", "999"));
        var request = Json("""{}""");

        var result = CalcEvaluator.Evaluate("x + 1", upstream, ctx, request);
        Assert.Equal(11, result!.Value.GetInt64());
    }

    [Fact]
    public void Ctx_shadows_request_when_upstream_does_not_have_key()
    {
        var upstream = Json("""{}""");
        var ctx = Ctx(("tier", "\"GOLD\""));
        var request = Json("""{"tier":"BLUE"}""");

        var result = CalcEvaluator.Evaluate("tier", upstream, ctx, request);
        Assert.Equal("GOLD", result!.Value.GetString());
    }

    [Fact]
    public void Boolean_result_returns_json_bool()
    {
        var result = CalcEvaluator.Evaluate("a > b",
            Json("""{"a":5,"b":3}"""), Ctx(), Json("""{}"""));
        Assert.Equal(JsonValueKind.True, result!.Value.ValueKind);
    }

    [Fact]
    public void Conditional_expression()
    {
        var result = CalcEvaluator.Evaluate("if(pieces > 2, 450, 0)",
            Json("""{"pieces":3}"""), Ctx(), Json("""{}"""));
        Assert.Equal(450, result!.Value.GetInt64());
    }

    [Fact]
    public void Division_is_floating_point()
    {
        var result = CalcEvaluator.Evaluate("10 / 4",
            null, Ctx(), Json("""{}"""));
        // NCalc 5 treats `/` as floating-point division: 10/4 = 2.5.
        Assert.Equal(2.5d, result!.Value.GetDouble(), 0.0001);
    }

    [Fact]
    public void String_input_resolves_as_string()
    {
        var result = CalcEvaluator.Evaluate("'fare-' + cabin",
            Json("""{"cabin":"Y"}"""), Ctx(), Json("""{}"""));
        Assert.Equal("fare-Y", result!.Value.GetString());
    }

    [Fact]
    public void Bad_expression_raises_invalid_operation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CalcEvaluator.Evaluate("bad ((( syntax", null, Ctx(), Json("""{}""")));
    }

    [Fact]
    public void Unknown_variable_propagates_via_evaluator()
    {
        // NCalc throws when a parameter has no value and isn't resolved.
        Assert.Throws<InvalidOperationException>(() =>
            CalcEvaluator.Evaluate("missing + 1", null, Ctx(), Json("""{}""")));
    }
}
