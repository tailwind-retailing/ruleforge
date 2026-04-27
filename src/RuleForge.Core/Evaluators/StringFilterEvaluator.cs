using System.Text.Json;
using System.Text.RegularExpressions;
using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Pure port of src/shared/evaluators/string-filter.ts. No I/O. Never throws.
/// Errors during evaluation collapse to <see cref="Verdict.Error"/> with a message.
/// </summary>
public static class StringFilterEvaluator
{
    public sealed record Result(
        Verdict Verdict,
        IReadOnlyList<string?> ResolvedValues,
        IReadOnlyList<bool>? PerElement = null,
        string? Reason = null,
        string? Error = null);

    public sealed record Context(JsonElement? Request, JsonElement? Ctx);

    public static Result Evaluate(StringFilterConfig config, Context context)
    {
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

            var perElement = raw.Select(v => CompareOne(v, config.Compare)).ToArray();
            var ok = Reduce(perElement, config.ArraySelector);
            var matched = perElement.Count(b => b);

            return new(
                ok ? Verdict.Pass : Verdict.Fail,
                raw,
                perElement,
                Reason: $"{matched}/{perElement.Length} elements matched ({SelectorName(config.ArraySelector)})");
        }
        catch (Exception e)
        {
            return new(Verdict.Error, Array.Empty<string?>(), Error: e.Message);
        }
    }

    // â”€â”€â”€ source resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static List<string?> ResolveSource(StringFilterConfig cfg, Context ctx)
    {
        var src = cfg.Source;
        if (src.Kind == SourceKind.Literal)
            return new List<string?> { src.Literal ?? string.Empty };

        if (string.IsNullOrEmpty(src.Path))
            return new List<string?>();

        var root = src.Kind == SourceKind.Context ? ctx.Ctx : ctx.Request;
        var matches = JsonPath.Resolve(root, src.Path!);

        var result = new List<string?>(matches.Count);
        foreach (var v in matches)
            result.Add(Stringy(v));
        return result;
    }

    /// <summary>
    /// Project a JsonElement to a string-or-null-or-missing.
    /// Returns null for explicit JSON null; null-marker (via not-included) for
    /// objects/arrays. Mirrors the TS <c>stringy()</c> helper.
    /// </summary>
    private static string? Stringy(JsonElement? maybe)
    {
        if (maybe is null) return null; // shouldn't happen â€” filtered upstream
        var v = maybe.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null: return null;
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.Number: return v.GetRawText();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            // Objects/arrays aren't string-filterable â€” TS returns undefined,
            // we return null and rely on CompareOne to treat it the same as
            // the explicit-null path (i.e. fail unless is_null/is_empty).
            default: return null;
        }
    }

    // â”€â”€â”€ comparison â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static bool CompareOne(string? raw, StringFilterCompare c)
    {
        var op = c.Operator;

        // null/empty checks short-circuit before normalization
        if (op == StringFilterOperator.IsNull) return raw is null;
        if (op == StringFilterOperator.IsEmpty) return raw is null || raw.Length == 0;

        if (raw is null) return false;

        var trim = c.Trim ?? false;
        var ci = c.CaseInsensitive ?? false;

        var lhs = raw;
        if (trim) lhs = lhs.Trim();
        if (ci) lhs = lhs.ToLowerInvariant();

        string Norm(string s)
        {
            var o = s;
            if (trim) o = o.Trim();
            if (ci) o = o.ToLowerInvariant();
            return o;
        }

        switch (op)
        {
            case StringFilterOperator.Equals:      return lhs == Norm(c.Value ?? string.Empty);
            case StringFilterOperator.NotEquals:   return lhs != Norm(c.Value ?? string.Empty);
            case StringFilterOperator.StartsWith:  return lhs.StartsWith(Norm(c.Value ?? string.Empty), StringComparison.Ordinal);
            case StringFilterOperator.EndsWith:    return lhs.EndsWith(Norm(c.Value ?? string.Empty), StringComparison.Ordinal);
            case StringFilterOperator.Contains:    return lhs.Contains(Norm(c.Value ?? string.Empty), StringComparison.Ordinal);
            case StringFilterOperator.NotContains: return !lhs.Contains(Norm(c.Value ?? string.Empty), StringComparison.Ordinal);
            case StringFilterOperator.In:
                return (c.Values ?? Array.Empty<string>()).Select(Norm).Contains(lhs);
            case StringFilterOperator.NotIn:
                return !(c.Values ?? Array.Empty<string>()).Select(Norm).Contains(lhs);
            case StringFilterOperator.Regex:
                try
                {
                    var opts = ci ? RegexOptions.IgnoreCase : RegexOptions.None;
                    return Regex.IsMatch(lhs, c.Value ?? string.Empty, opts);
                }
                catch
                {
                    return false;
                }
            default: return false;
        }
    }

    // â”€â”€â”€ array reduction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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
