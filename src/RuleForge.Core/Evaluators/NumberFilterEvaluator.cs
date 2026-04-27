using System.Globalization;
using System.Text.Json;
using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Pure port of src/shared/evaluators/number-filter.ts. Coerces strings,
/// numbers and booleans; treats objects/arrays as missing.
/// </summary>
public static class NumberFilterEvaluator
{
    public sealed record Result(
        Verdict Verdict,
        IReadOnlyList<string?> ResolvedValues,
        IReadOnlyList<bool>? PerElement = null,
        string? Reason = null,
        string? Error = null);

    public static Result Evaluate(NumberFilterConfig config, StringFilterEvaluator.Context context)
    {
        try
        {
            var raw = ResolveSource(config, context);

            if (raw.Count == 0)
            {
                return config.OnMissing switch
                {
                    OnMissing.Pass => new(Verdict.Pass, Array.Empty<string?>(), Reason: "no values, onMissing=pass"),
                    OnMissing.Skip => new(Verdict.Skip, Array.Empty<string?>(), Reason: "no values, onMissing=skip"),
                    _              => new(Verdict.Fail, Array.Empty<string?>(), Reason: "no values, onMissing=fail"),
                };
            }

            var perElement = raw.Select(v => CompareOne(v, config.Compare)).ToArray();
            var ok = Reduce(perElement, config.ArraySelector);
            var matched = perElement.Count(b => b);
            return new(
                ok ? Verdict.Pass : Verdict.Fail,
                raw.Select(v => v?.ToString(CultureInfo.InvariantCulture)).ToArray(),
                perElement,
                Reason: $"{matched}/{perElement.Length} elements matched ({SelectorName(config.ArraySelector)})");
        }
        catch (Exception e)
        {
            return new(Verdict.Error, Array.Empty<string?>(), Error: e.Message);
        }
    }

    private static List<double?> ResolveSource(NumberFilterConfig cfg, StringFilterEvaluator.Context ctx)
    {
        var src = cfg.Source;
        if (src.Kind == SourceKind.Literal)
            return new List<double?> { src.Literal ?? 0d };

        if (string.IsNullOrEmpty(src.Path))
            return new List<double?>();

        var root = src.Kind == SourceKind.Context ? ctx.Ctx : ctx.Request;
        var matches = JsonPath.Resolve(root, src.Path!);
        return matches.Select(Coerce).ToList();
    }

    /// <summary>
    /// Mirrors TS coerce(): number â†’ number, string-numeric â†’ number,
    /// boolean â†’ 0/1, JSON null â†’ null, anything else â†’ null (missing).
    /// </summary>
    private static double? Coerce(JsonElement? maybe)
    {
        if (maybe is null) return null;
        var v = maybe.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                if (v.TryGetDouble(out var n) && !double.IsNaN(n) && !double.IsInfinity(n))
                    return n;
                return null;
            case JsonValueKind.String:
                var s = v.GetString();
                if (s is null) return null;
                if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                    return parsed;
                return null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    private static bool CompareOne(double? raw, NumberFilterCompare c)
    {
        if (c.Operator == NumberFilterOperator.IsNull) return raw is null;
        if (raw is null) return false;

        var lhs = raw.Value;
        if (c.Round == Rounding.Floor) lhs = Math.Floor(lhs);
        else if (c.Round == Rounding.Ceil) lhs = Math.Ceiling(lhs);
        else if (c.Round == Rounding.Round) lhs = Math.Round(lhs, MidpointRounding.AwayFromZero);

        var target = c.Value ?? 0d;

        switch (c.Operator)
        {
            case NumberFilterOperator.Equals:    return lhs == target;
            case NumberFilterOperator.NotEquals: return lhs != target;
            case NumberFilterOperator.Gt:        return lhs >  target;
            case NumberFilterOperator.Gte:       return lhs >= target;
            case NumberFilterOperator.Lt:        return lhs <  target;
            case NumberFilterOperator.Lte:       return lhs <= target;
            case NumberFilterOperator.Between:
            case NumberFilterOperator.NotBetween:
            {
                var min = c.Min ?? double.NegativeInfinity;
                var max = c.Max ?? double.PositiveInfinity;
                var minOk = (c.MinInclusive == false) ? lhs > min : lhs >= min;
                var maxOk = (c.MaxInclusive == false) ? lhs < max : lhs <= max;
                var inside = minOk && maxOk;
                return c.Operator == NumberFilterOperator.Between ? inside : !inside;
            }
            case NumberFilterOperator.In:
                return (c.Values ?? Array.Empty<double>()).Contains(lhs);
            case NumberFilterOperator.NotIn:
                return !(c.Values ?? Array.Empty<double>()).Contains(lhs);
            default: return false;
        }
    }

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
