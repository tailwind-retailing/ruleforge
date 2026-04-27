using System.Text.Json;
using RuleForge.Core.Evaluators;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class DateFilterEvaluatorTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static StringFilterEvaluator.Context Ctx(string requestJson) =>
        new(Json(requestJson), null);

    private static DateFilterConfig Cfg(
        DateFilterSource source,
        DateFilterCompare compare,
        ArraySelector selector = ArraySelector.First,
        OnMissing onMissing = OnMissing.Fail) =>
        new(source, compare, selector, onMissing);

    // Pinned clock for relative-window tests: 2026-04-27T12:00:00Z.
    private static readonly Func<DateTimeOffset> PinnedClock =
        () => new DateTimeOffset(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);

    // â”€â”€â”€ basic operators â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Equals_at_datetime_granularity_compares_full_instant()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T14:00:00Z"),
            new DateFilterCompare(DateFilterOperator.Equals, Value: "2026-04-27T14:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Equals_at_date_granularity_strips_time()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T14:00:00Z"),
            new DateFilterCompare(DateFilterOperator.Equals, DateGranularity.Date, Value: "2026-04-27T08:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Before_and_after()
    {
        var before = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T10:00:00Z"),
            new DateFilterCompare(DateFilterOperator.Before, Value: "2026-04-27T11:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(before, Ctx("""{}"""), PinnedClock).Verdict);

        var after = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T11:00:00Z"),
            new DateFilterCompare(DateFilterOperator.After, Value: "2026-04-27T10:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(after, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Between_inclusive_default_then_excluded_endpoint()
    {
        var inclusive = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T10:00:00Z"),
            new DateFilterCompare(DateFilterOperator.Between,
                From: "2026-04-27T10:00:00Z", To: "2026-04-27T12:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(inclusive, Ctx("""{}"""), PinnedClock).Verdict);

        var fromExcluded = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T10:00:00Z"),
            new DateFilterCompare(DateFilterOperator.Between,
                From: "2026-04-27T10:00:00Z", To: "2026-04-27T12:00:00Z",
                FromInclusive: false));
        Assert.Equal(Verdict.Fail, DateFilterEvaluator.Evaluate(fromExcluded, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Not_between_inverts()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-05-10T00:00:00Z"),
            new DateFilterCompare(DateFilterOperator.NotBetween,
                From: "2026-04-27T00:00:00Z", To: "2026-05-01T00:00:00Z"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock).Verdict);
    }

    // â”€â”€â”€ relative windows â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Within_last_5_days_includes_today_and_4_days_ago()
    {
        // pinned now = 2026-04-27T12:00:00Z; window = [2026-04-22T12:00, now]
        var inWindow = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-25T12:00:00Z"),
            new DateFilterCompare(DateFilterOperator.WithinLast,
                Amount: 5, Unit: DateUnit.Days));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(inWindow, Ctx("""{}"""), PinnedClock).Verdict);

        var outside = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-20T12:00:00Z"),
            new DateFilterCompare(DateFilterOperator.WithinLast,
                Amount: 5, Unit: DateUnit.Days));
        Assert.Equal(Verdict.Fail, DateFilterEvaluator.Evaluate(outside, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Within_next_3_hours_inclusive_at_now()
    {
        var atNow = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T12:00:00Z"),
            new DateFilterCompare(DateFilterOperator.WithinNext,
                Amount: 3, Unit: DateUnit.Hours));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(atNow, Ctx("""{}"""), PinnedClock).Verdict);

        var fourHrs = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T16:00:00Z"),
            new DateFilterCompare(DateFilterOperator.WithinNext,
                Amount: 3, Unit: DateUnit.Hours));
        Assert.Equal(Verdict.Fail, DateFilterEvaluator.Evaluate(fourHrs, Ctx("""{}"""), PinnedClock).Verdict);
    }

    // â”€â”€â”€ timezones â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Naive_string_pinned_to_configured_timezone()
    {
        // 2026-04-27T14:00 in Asia/Dubai (UTC+04:00) = 10:00Z
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "2026-04-27T14:00:00"),
            new DateFilterCompare(DateFilterOperator.Equals,
                Value: "2026-04-27T10:00:00Z",
                Timezone: "Asia/Dubai"));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock).Verdict);
    }

    // â”€â”€â”€ source kinds â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Path_resolution_with_array_wildcard()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Request, Path: "$.segments[*].dep"),
            new DateFilterCompare(DateFilterOperator.After, Value: "2026-04-26T00:00:00Z"),
            ArraySelector.All);
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg,
            Ctx("""{"segments":[{"dep":"2026-04-27T08:00:00Z"},{"dep":"2026-04-28T08:00:00Z"}]}"""),
            PinnedClock).Verdict);
    }

    // â”€â”€â”€ on-missing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData(OnMissing.Fail, Verdict.Fail)]
    [InlineData(OnMissing.Pass, Verdict.Pass)]
    [InlineData(OnMissing.Skip, Verdict.Skip)]
    public void OnMissing_when_path_yields_nothing(OnMissing om, Verdict expected)
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Request, Path: "$.notHere"),
            new DateFilterCompare(DateFilterOperator.Equals, Value: "2026-04-27T00:00:00Z"),
            onMissing: om);
        Assert.Equal(expected, DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Is_null_short_circuits()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Request, Path: "$.maybe"),
            new DateFilterCompare(DateFilterOperator.IsNull));
        Assert.Equal(Verdict.Pass, DateFilterEvaluator.Evaluate(cfg, Ctx("""{"maybe":null}"""), PinnedClock).Verdict);
    }

    [Fact]
    public void Garbage_input_returns_fail_not_throw()
    {
        var cfg = Cfg(
            new DateFilterSource(SourceKind.Literal, Literal: "not-a-date"),
            new DateFilterCompare(DateFilterOperator.Equals, Value: "2026-04-27T00:00:00Z"));
        var r = DateFilterEvaluator.Evaluate(cfg, Ctx("""{}"""), PinnedClock);
        Assert.Equal(Verdict.Fail, r.Verdict);
    }
}
