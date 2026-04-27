using System.Text.Json;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class NumberFilterEvaluatorTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static StringFilterEvaluator.Context Ctx(string requestJson, string? ctxJson = null) =>
        new(Json(requestJson), ctxJson is null ? null : Json(ctxJson));

    private static NumberFilterConfig Cfg(
        NumberFilterSource source,
        NumberFilterCompare compare,
        ArraySelector selector = ArraySelector.First,
        OnMissing onMissing = OnMissing.Fail) =>
        new(source, compare, selector, onMissing);

    [Theory]
    [InlineData(NumberFilterOperator.Equals, 5d, 5d, true)]
    [InlineData(NumberFilterOperator.Equals, 5d, 6d, false)]
    [InlineData(NumberFilterOperator.NotEquals, 5d, 6d, true)]
    [InlineData(NumberFilterOperator.Gt, 6d, 5d, true)]
    [InlineData(NumberFilterOperator.Gte, 5d, 5d, true)]
    [InlineData(NumberFilterOperator.Lt, 4d, 5d, true)]
    [InlineData(NumberFilterOperator.Lte, 5d, 5d, true)]
    public void Comparison_operators(NumberFilterOperator op, double lhs, double rhs, bool expected)
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: lhs),
            new NumberFilterCompare(op, Value: rhs));
        var r = NumberFilterEvaluator.Evaluate(cfg, Ctx("""{}"""));
        Assert.Equal(expected ? Verdict.Pass : Verdict.Fail, r.Verdict);
    }

    [Fact]
    public void Between_inclusive_default()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 5),
            new NumberFilterCompare(NumberFilterOperator.Between, Min: 1, Max: 5));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void Between_can_exclude_max()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 5),
            new NumberFilterCompare(NumberFilterOperator.Between, Min: 1, Max: 5, MaxInclusive: false));
        Assert.Equal(Verdict.Fail, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void Not_between_inverts()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 10),
            new NumberFilterCompare(NumberFilterOperator.NotBetween, Min: 1, Max: 5));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void In_and_not_in()
    {
        var inCfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 3),
            new NumberFilterCompare(NumberFilterOperator.In, Values: new[] { 1d, 2d, 3d }));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(inCfg, Ctx("""{}""")).Verdict);

        var notInCfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 99),
            new NumberFilterCompare(NumberFilterOperator.NotIn, Values: new[] { 1d, 2d, 3d }));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(notInCfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void Coerces_string_numeric_and_boolean()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Request, Path: "$.x"),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 23));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{"x":"23"}""")).Verdict);
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{"x":" 23 "}""")).Verdict);

        var boolCfg = Cfg(
            new NumberFilterSource(SourceKind.Request, Path: "$.b"),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 1));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(boolCfg, Ctx("""{"b":true}""")).Verdict);
    }

    [Fact]
    public void Round_floor_ceil_round()
    {
        var floorCfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 4.7),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 4, Round: Rounding.Floor));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(floorCfg, Ctx("""{}""")).Verdict);

        var ceilCfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 4.2),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 5, Round: Rounding.Ceil));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(ceilCfg, Ctx("""{}""")).Verdict);

        var roundCfg = Cfg(
            new NumberFilterSource(SourceKind.Literal, Literal: 4.5),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 5, Round: Rounding.Round));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(roundCfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void Is_null_short_circuits_normalisation()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Request, Path: "$.maybe"),
            new NumberFilterCompare(NumberFilterOperator.IsNull));
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{"maybe":null}""")).Verdict);
    }

    [Theory]
    [InlineData(OnMissing.Fail, Verdict.Fail)]
    [InlineData(OnMissing.Pass, Verdict.Pass)]
    [InlineData(OnMissing.Skip, Verdict.Skip)]
    public void OnMissing_when_path_yields_nothing(OnMissing om, Verdict expected)
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Request, Path: "$.notHere"),
            new NumberFilterCompare(NumberFilterOperator.Equals, Value: 1),
            onMissing: om);
        Assert.Equal(expected, NumberFilterEvaluator.Evaluate(cfg, Ctx("""{}""")).Verdict);
    }

    [Fact]
    public void Wildcard_with_any_selector()
    {
        var cfg = Cfg(
            new NumberFilterSource(SourceKind.Request, Path: "$.bags[*].weightKg"),
            new NumberFilterCompare(NumberFilterOperator.Gt, Value: 30),
            ArraySelector.Any);
        Assert.Equal(Verdict.Pass, NumberFilterEvaluator.Evaluate(cfg,
            Ctx("""{"bags":[{"weightKg":12},{"weightKg":35}]}""")).Verdict);
        Assert.Equal(Verdict.Fail, NumberFilterEvaluator.Evaluate(cfg,
            Ctx("""{"bags":[{"weightKg":12},{"weightKg":15}]}""")).Verdict);
    }
}
