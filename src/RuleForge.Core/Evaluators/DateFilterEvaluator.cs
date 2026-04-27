using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Pure port of src/shared/evaluators/date-filter.ts.
///
/// Three concerns:
///   1. Granularity â€” `datetime` compares full instants, `date` collapses to
///      Y-M-D, `time` collapses to seconds-of-day.
///   2. Timezones â€” IANA names (e.g. "Asia/Dubai"). ISO strings with offset
///      are trusted as-is; naive strings are interpreted in the configured
///      timezone (or UTC when absent).
///   3. Relative windows â€” `within_last 5 days` = [now - 5d, now]; `now` is
///      taken from the supplied clock so tests can pin it.
///
/// Port note vs the TS reference: when reducing to a comparable scalar at
/// `date`/`time` granularity the TS code reads `getFullYear()` etc. which
/// uses the runtime host's local zone. C# reads in the configured timezone
/// (UTC when absent) so behavior is deterministic across hosts.
/// </summary>
public static class DateFilterEvaluator
{
    public sealed record Result(
        Verdict Verdict,
        IReadOnlyList<string?> ResolvedValues,
        IReadOnlyList<bool>? PerElement = null,
        string? Reason = null,
        string? Error = null);

    public static Result Evaluate(
        DateFilterConfig config,
        StringFilterEvaluator.Context context,
        Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.UtcNow;
        try
        {
            var raw = ResolveSource(config, context);

            if (raw.Count == 0)
            {
                return config.OnMissing switch
                {
                    OnMissing.Pass => new(Verdict.Pass, raw, Reason: "no values, onMissing=pass"),
                    OnMissing.Skip => new(Verdict.Skip, raw, Reason: "no values, onMissing=skip"),
                    _              => new(Verdict.Fail, raw, Reason: "no values, onMissing=fail"),
                };
            }

            var tz = config.Compare.Timezone;
            var granularity = config.Compare.Granularity;
            var perElement = new bool[raw.Count];
            var display = new List<string?>(raw.Count);

            for (var i = 0; i < raw.Count; i++)
            {
                var r = raw[i];
                var parsed = r is null ? null : ParseDate(r, tz, clock);
                display.Add(parsed is null ? r : FormatForDisplay(parsed.Value, granularity, tz));
                perElement[i] = CompareOne(parsed, config.Compare, granularity, tz, clock);
            }
            var ok = Reduce(perElement, config.ArraySelector);
            var matched = perElement.Count(b => b);
            return new(
                ok ? Verdict.Pass : Verdict.Fail,
                display,
                perElement,
                Reason: $"{matched}/{perElement.Length} elements matched ({SelectorName(config.ArraySelector)})");
        }
        catch (Exception e)
        {
            return new(Verdict.Error, Array.Empty<string?>(), Error: e.Message);
        }
    }

    // â”€â”€â”€ source â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static List<string?> ResolveSource(DateFilterConfig cfg, StringFilterEvaluator.Context ctx)
    {
        var src = cfg.Source;
        if (src.Kind == SourceKind.Literal)
            return new List<string?> { src.Literal ?? string.Empty };
        if (string.IsNullOrEmpty(src.Path))
            return new List<string?>();

        var root = src.Kind == SourceKind.Context ? ctx.Ctx : ctx.Request;
        var matches = JsonPath.Resolve(root, src.Path!);
        return matches.Select(Stringy).ToList();
    }

    private static string? Stringy(JsonElement? maybe)
    {
        if (maybe is null) return null;
        var v = maybe.Value;
        return v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }

    // â”€â”€â”€ compare â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static bool CompareOne(
        DateTimeOffset? raw,
        DateFilterCompare c,
        DateGranularity gran,
        string? tz,
        Func<DateTimeOffset> clock)
    {
        if (c.Operator == DateFilterOperator.IsNull) return raw is null;
        if (raw is null) return false;

        var lhs = ToComparable(raw.Value, gran, tz);

        switch (c.Operator)
        {
            case DateFilterOperator.Equals:
            {
                var v = ParseRequired(c.Value, tz, clock);
                return v.HasValue && lhs == ToComparable(v.Value, gran, tz);
            }
            case DateFilterOperator.NotEquals:
            {
                var v = ParseRequired(c.Value, tz, clock);
                return v.HasValue && lhs != ToComparable(v.Value, gran, tz);
            }
            case DateFilterOperator.Before:
            {
                var v = ParseRequired(c.Value, tz, clock);
                return v.HasValue && lhs < ToComparable(v.Value, gran, tz);
            }
            case DateFilterOperator.After:
            {
                var v = ParseRequired(c.Value, tz, clock);
                return v.HasValue && lhs > ToComparable(v.Value, gran, tz);
            }
            case DateFilterOperator.Between:
            case DateFilterOperator.NotBetween:
            {
                var from = ParseRequired(c.From, tz, clock);
                var to = ParseRequired(c.To, tz, clock);
                if (from is null || to is null) return false;
                var fromN = ToComparable(from.Value, gran, tz);
                var toN   = ToComparable(to.Value, gran, tz);
                var fromOk = (c.FromInclusive == false) ? lhs > fromN : lhs >= fromN;
                var toOk   = (c.ToInclusive   == false) ? lhs < toN   : lhs <= toN;
                var inside = fromOk && toOk;
                return c.Operator == DateFilterOperator.Between ? inside : !inside;
            }
            case DateFilterOperator.WithinLast:
            {
                if (c.Amount is null || c.Unit is null) return false;
                var now = clock();
                var past = AddRelative(now, -c.Amount.Value, c.Unit.Value);
                return lhs >= ToComparable(past, gran, tz) && lhs <= ToComparable(now, gran, tz);
            }
            case DateFilterOperator.WithinNext:
            {
                if (c.Amount is null || c.Unit is null) return false;
                var now = clock();
                var future = AddRelative(now, c.Amount.Value, c.Unit.Value);
                return lhs >= ToComparable(now, gran, tz) && lhs <= ToComparable(future, gran, tz);
            }
            default: return false;
        }
    }

    // â”€â”€â”€ parsing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static readonly Regex IsoWithOffset =
        new(@"T.*([+-]\d{2}:?\d{2}|Z)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsoStartsWithDateT =
        new(@"^\d{4}-\d{2}-\d{2}T", RegexOptions.Compiled);
    private static readonly Regex DateOnly =
        new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
    private static readonly Regex TimeOnly =
        new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$", RegexOptions.Compiled);
    private static readonly Regex NaiveDateTime =
        new(@"^\d{4}-\d{2}-\d{2}[ T]\d{1,2}:\d{2}", RegexOptions.Compiled);

    private static DateTimeOffset? ParseDate(string input, string? tz, Func<DateTimeOffset> clock)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var trimmed = input.Trim();

        // ISO with explicit offset/Z â€” trust standard parsing.
        if (IsoWithOffset.IsMatch(trimmed) ||
            (IsoStartsWithDateT.IsMatch(trimmed) && trimmed.IndexOfAny(new[] { '+', '-', 'Z', 'z' }, 10) >= 0))
        {
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var iso))
                return iso;
            return null;
        }

        // Date-only â€” pin to midnight in tz.
        if (DateOnly.IsMatch(trimmed))
            return ParseInTz(trimmed + "T00:00:00", tz);

        // Time-only â€” pin to today in tz.
        if (TimeOnly.IsMatch(trimmed))
        {
            var today = TodayDateString(tz, clock);
            var padded = trimmed.Length == 4 || trimmed.Length == 5
                ? (trimmed.Length == 4 ? "0" + trimmed : trimmed) + ":00"
                : trimmed;
            if (padded.Length < 8) padded = padded.PadLeft(8, '0');
            return ParseInTz($"{today}T{padded}", tz);
        }

        // Naive datetime â€” apply tz.
        if (NaiveDateTime.IsMatch(trimmed))
            return ParseInTz(trimmed.Replace(' ', 'T'), tz);

        // Last resort â€” try .NET's parser as the TS reference does.
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var fallback))
            return fallback;
        return null;
    }

    private static DateTimeOffset? ParseRequired(string? input, string? tz, Func<DateTimeOffset> clock) =>
        string.IsNullOrEmpty(input) ? null : ParseDate(input!, tz, clock);

    private static DateTimeOffset? ParseInTz(string naiveIso, string? tzId)
    {
        if (!DateTime.TryParse(naiveIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
            return null;

        var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        if (string.IsNullOrEmpty(tzId))
        {
            // Treat as UTC when no timezone is configured â€” predictable across hosts.
            return new DateTimeOffset(unspecified, TimeSpan.Zero);
        }
        var tz = ResolveTz(tzId);
        if (tz is null) return null;
        try
        {
            var asUtc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
            return new DateTimeOffset(asUtc, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static string TodayDateString(string? tzId, Func<DateTimeOffset> clock)
    {
        var now = clock();
        if (string.IsNullOrEmpty(tzId)) return now.UtcDateTime.ToString("yyyy-MM-dd");
        var tz = ResolveTz(tzId);
        if (tz is null) return now.UtcDateTime.ToString("yyyy-MM-dd");
        var local = TimeZoneInfo.ConvertTime(now, tz);
        return local.ToString("yyyy-MM-dd");
    }

    private static TimeZoneInfo? ResolveTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }

    // â”€â”€â”€ comparable scalar â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static long ToComparable(DateTimeOffset d, DateGranularity gran, string? tzId)
    {
        DateTime local;
        if (string.IsNullOrEmpty(tzId))
        {
            local = d.UtcDateTime;
        }
        else
        {
            var tz = ResolveTz(tzId);
            local = tz is null ? d.UtcDateTime : TimeZoneInfo.ConvertTime(d, tz).DateTime;
        }

        return gran switch
        {
            DateGranularity.Datetime =>
                d.UtcTicks / TimeSpan.TicksPerMillisecond,
            DateGranularity.Date =>
                local.Year * 10000L + local.Month * 100L + local.Day,
            DateGranularity.Time =>
                local.Hour * 3600L + local.Minute * 60L + local.Second,
            _ => d.UtcTicks / TimeSpan.TicksPerMillisecond,
        };
    }

    private static string FormatForDisplay(DateTimeOffset d, DateGranularity gran, string? tzId)
    {
        var iso = d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return gran switch
        {
            DateGranularity.Date => iso[..10],
            DateGranularity.Time => iso.Substring(11, 8),
            _ => iso,
        };
    }

    // â”€â”€â”€ relative arithmetic â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static DateTimeOffset AddRelative(DateTimeOffset d, int amount, DateUnit unit) => unit switch
    {
        DateUnit.Minutes => d.AddMinutes(amount),
        DateUnit.Hours   => d.AddHours(amount),
        DateUnit.Days    => d.AddDays(amount),
        DateUnit.Weeks   => d.AddDays(amount * 7),
        DateUnit.Months  => d.AddMonths(amount),
        _ => d,
    };

    // â”€â”€â”€ reduction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static bool Reduce(bool[] per, ArraySelector selector)
    {
        if (per.Length == 0) return false;
        return selector switch
        {
            ArraySelector.Any   => per.Any(b => b),
            ArraySelector.All   => per.All(b => b),
            ArraySelector.None  => !per.Any(b => b),
            ArraySelector.First => per[0],
            ArraySelector.Only  => per.Count(b => b) == 1,
            _ => false,
        };
    }

    private static string SelectorName(ArraySelector s) => s switch
    {
        ArraySelector.Any => "any",
        ArraySelector.All => "all",
        ArraySelector.None => "none",
        ArraySelector.First => "first",
        ArraySelector.Only => "only",
        _ => s.ToString().ToLowerInvariant(),
    };
}
