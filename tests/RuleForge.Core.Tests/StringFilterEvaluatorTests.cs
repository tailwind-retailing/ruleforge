using System.Text.Json;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class StringFilterEvaluatorTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static StringFilterEvaluator.Context CtxFor(string requestJson, string? ctxJson = null) =>
        new(Json(requestJson), ctxJson is null ? null : Json(ctxJson));

    private static StringFilterConfig Cfg(
        StringFilterSource source,
        StringFilterCompare compare,
        ArraySelector selector = ArraySelector.First,
        OnMissing onMissing = OnMissing.Fail) =>
        new(source, compare, selector, onMissing);

    // â”€â”€â”€ source â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Literal_source_resolves_to_the_literal()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "hello"),
            new StringFilterCompare(StringFilterOperator.Equals, "hello"));
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}"""));
        Assert.Equal(Verdict.Pass, r.Verdict);
        Assert.Equal(new[] { "hello" }, r.ResolvedValues);
    }

    [Fact]
    public void Request_path_resolution()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.cabin"),
            new StringFilterCompare(StringFilterOperator.Equals, "Y"));
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{"cabin":"Y"}"""));
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    [Fact]
    public void Context_path_resolution()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Context, Path: "$.tier"),
            new StringFilterCompare(StringFilterOperator.Equals, "GOLD"));
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}""", """{"tier":"GOLD"}"""));
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    // â”€â”€â”€ operators â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData("equals", "Y", "Y", true)]
    [InlineData("equals", "Y", "J", false)]
    [InlineData("not_equals", "Y", "J", true)]
    [InlineData("starts_with", "GOLDEN", "GOLD", true)]
    [InlineData("ends_with", "PLATINUM", "NUM", true)]
    [InlineData("contains", "PLATINUM", "TIN", true)]
    [InlineData("not_contains", "PLATINUM", "TIN", false)]
    public void Comparison_operators(string op, string lhs, string rhs, bool expectedPass)
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: lhs),
            new StringFilterCompare(StringEnumFromName(op), rhs));
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}"""));
        Assert.Equal(expectedPass ? Verdict.Pass : Verdict.Fail, r.Verdict);
    }

    [Fact]
    public void In_and_not_in()
    {
        var inCfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "GOLD"),
            new StringFilterCompare(StringFilterOperator.In, Values: new[] { "GOLD", "PLAT" }));
        Assert.Equal(Verdict.Pass, StringFilterEvaluator.Evaluate(inCfg, CtxFor("""{}""")).Verdict);

        var notInCfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "BLUE"),
            new StringFilterCompare(StringFilterOperator.NotIn, Values: new[] { "GOLD", "PLAT" }));
        Assert.Equal(Verdict.Pass, StringFilterEvaluator.Evaluate(notInCfg, CtxFor("""{}""")).Verdict);
    }

    [Fact]
    public void Regex_pattern_lives_in_compare_value()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "user@example.com"),
            new StringFilterCompare(StringFilterOperator.Regex, Value: "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$"));
        Assert.Equal(Verdict.Pass, StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}""")).Verdict);
    }

    [Fact]
    public void Regex_invalid_pattern_returns_false_not_throw()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "anything"),
            new StringFilterCompare(StringFilterOperator.Regex, Value: "([unclosed"));
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}"""));
        Assert.Equal(Verdict.Fail, r.Verdict);
    }

    [Fact]
    public void Is_null_and_is_empty_short_circuit_normalisation()
    {
        var nullCfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.maybe"),
            new StringFilterCompare(StringFilterOperator.IsNull));
        var r1 = StringFilterEvaluator.Evaluate(nullCfg, CtxFor("""{"maybe":null}"""));
        Assert.Equal(Verdict.Pass, r1.Verdict);

        var emptyCfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: ""),
            new StringFilterCompare(StringFilterOperator.IsEmpty));
        Assert.Equal(Verdict.Pass, StringFilterEvaluator.Evaluate(emptyCfg, CtxFor("""{}""")).Verdict);
    }

    [Fact]
    public void Case_insensitive_and_trim_are_applied_to_both_sides()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Literal, Literal: "  gold  "),
            new StringFilterCompare(StringFilterOperator.Equals, " GOLD ", CaseInsensitive: true, Trim: true));
        Assert.Equal(Verdict.Pass, StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}""")).Verdict);
    }

    // â”€â”€â”€ array selectors â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Selector_any_passes_when_one_matches()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.pax[*].tier"),
            new StringFilterCompare(StringFilterOperator.In, Values: new[] { "GOLD", "PLAT" }),
            ArraySelector.Any);
        var r = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"BLUE"},{"tier":"GOLD"}]}"""));
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    [Fact]
    public void Selector_all_fails_if_any_misses()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.pax[*].tier"),
            new StringFilterCompare(StringFilterOperator.In, Values: new[] { "GOLD" }),
            ArraySelector.All);
        var r = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"GOLD"},{"tier":"BLUE"}]}"""));
        Assert.Equal(Verdict.Fail, r.Verdict);
    }

    [Fact]
    public void Selector_none_passes_when_no_matches()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.pax[*].tier"),
            new StringFilterCompare(StringFilterOperator.Equals, "PLAT"),
            ArraySelector.None);
        var r = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"GOLD"},{"tier":"BLUE"}]}"""));
        Assert.Equal(Verdict.Pass, r.Verdict);
    }

    [Fact]
    public void Selector_first_only_inspects_index_zero()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.pax[*].tier"),
            new StringFilterCompare(StringFilterOperator.Equals, "GOLD"),
            ArraySelector.First);
        var pass = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"GOLD"},{"tier":"BLUE"}]}"""));
        var fail = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"BLUE"},{"tier":"GOLD"}]}"""));
        Assert.Equal(Verdict.Pass, pass.Verdict);
        Assert.Equal(Verdict.Fail, fail.Verdict);
    }

    [Fact]
    public void Selector_only_passes_when_exactly_one_matches()
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.pax[*].tier"),
            new StringFilterCompare(StringFilterOperator.Equals, "GOLD"),
            ArraySelector.Only);
        var one = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"GOLD"},{"tier":"BLUE"}]}"""));
        var two = StringFilterEvaluator.Evaluate(cfg,
            CtxFor("""{"pax":[{"tier":"GOLD"},{"tier":"GOLD"}]}"""));
        Assert.Equal(Verdict.Pass, one.Verdict);
        Assert.Equal(Verdict.Fail, two.Verdict);
    }

    // â”€â”€â”€ onMissing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(OnMissing.Fail, Verdict.Fail)]
    [InlineData(OnMissing.Pass, Verdict.Pass)]
    [InlineData(OnMissing.Skip, Verdict.Skip)]
    public void OnMissing_branch_when_no_values_resolved(OnMissing om, Verdict expected)
    {
        var cfg = Cfg(
            new StringFilterSource(SourceKind.Request, Path: "$.notHere"),
            new StringFilterCompare(StringFilterOperator.Equals, "x"),
            onMissing: om);
        var r = StringFilterEvaluator.Evaluate(cfg, CtxFor("""{}"""));
        Assert.Equal(expected, r.Verdict);
    }

    // â”€â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static StringFilterOperator StringEnumFromName(string s) => s switch
    {
        "equals" => StringFilterOperator.Equals,
        "not_equals" => StringFilterOperator.NotEquals,
        "starts_with" => StringFilterOperator.StartsWith,
        "ends_with" => StringFilterOperator.EndsWith,
        "contains" => StringFilterOperator.Contains,
        "not_contains" => StringFilterOperator.NotContains,
        _ => throw new ArgumentException(s),
    };
}
