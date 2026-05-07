using System.Globalization;
using System.Text.Json;
using NCalc;
using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Evaluates a calc node's expression and produces a JsonElement. Variables
/// are resolved from a stacked namespace: upstream fields shadow ctx keys
/// shadow request fields. Numeric results round-trip back to JSON numbers;
/// booleans to JSON booleans; strings to JSON strings.
/// </summary>
public static class CalcEvaluator
{
    /// <summary>
    /// Default per-call deadline for calc expressions. NCalcSync has no native
    /// cancellation, so we race <c>Evaluate()</c> against this on a Task. The
    /// deadline guards against runaway expressions like <c>pow(pow(pow(...)))</c>
    /// that an authoring mistake could plant on the hot path.
    /// <para>
    /// 5 seconds is far longer than any sane expression should take (sub-ms
    /// is the warm-state target) but provides headroom against thread-pool
    /// scheduling delay under heavy parallel load.
    /// </para>
    /// </summary>
    public const int DefaultTimeoutMs = 5000;

    public static JsonElement? Evaluate(
        string expression,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request) =>
        Evaluate(expression, upstream, ctx, request, frames: null);

    public static JsonElement? Evaluate(
        string expression,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request,
        IReadOnlyList<IterationFrame>? frames,
        int timeoutMs = DefaultTimeoutMs)
    {
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be > 0");

        // Default options: case-sensitive operators / functions. We do our own
        // case-insensitive variable resolution against the JSON inputs below.
        var expr = new Expression(expression);

        expr.EvaluateParameter += (name, args) =>
        {
            if (TryResolveVariable(name, upstream, ctx, request, frames, out var resolved))
                args.Result = resolved;
        };

        // Race expr.Evaluate() against the deadline. NCalcSync evaluation is
        // CPU-bound and uncancellable; if the wait times out we fail fast. The
        // background task may continue spinning until NCalc finishes — that's
        // a known leak pending NCalc cancellation support, but the request
        // returns promptly.
        object? result;
        try
        {
            var task = Task.Run(() => expr.Evaluate());
            if (!task.Wait(timeoutMs))
            {
                throw new InvalidOperationException(
                    $"calc expression '{expression}' timed out after {timeoutMs}ms " +
                    "(likely a runaway / deeply-nested expression)");
            }
            result = task.GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"calc expression '{expression}' failed to evaluate: {e.Message}", e);
        }

        return CoerceToJson(result);
    }

    private static bool TryResolveVariable(
        string name,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request,
        IReadOnlyList<IterationFrame>? frames,
        out object? value)
    {
        // Upstream fields take priority.
        if (upstream is { ValueKind: JsonValueKind.Object } u &&
            TryReadProperty(u, name, out value))
            return true;

        // Iteration frames (innermost first): bare name → frame.Item if it's a
        // primitive; <name>Index / <name>Count → integer.
        if (frames is { Count: > 0 })
        {
            for (var i = frames.Count - 1; i >= 0; i--)
            {
                var f = frames[i];
                if (string.Equals(name, f.Name, StringComparison.Ordinal))
                {
                    if (TryUnwrap(f.Item, out value)) return true;
                    break;
                }
                if (string.Equals(name, f.Name + "Index", StringComparison.Ordinal))
                {
                    value = (long)f.Index; return true;
                }
                if (string.Equals(name, f.Name + "Count", StringComparison.Ordinal))
                {
                    value = (long)f.Count; return true;
                }
            }
        }

        // Then ctx.
        if (ctx.TryGetValue(name, out var ctxEl) && TryUnwrap(ctxEl, out value))
            return true;

        // Then request top-level.
        if (request.ValueKind == JsonValueKind.Object &&
            TryReadProperty(request, name, out value))
            return true;

        value = null;
        return false;
    }

    private static bool TryReadProperty(JsonElement obj, string name, out object? value)
    {
        // Case-insensitive scan to match NCalc's IgnoreCase.
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.NameEquals(name) ||
                string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return TryUnwrap(prop.Value, out value);
            }
        }
        value = null;
        return false;
    }

    private static bool TryUnwrap(JsonElement el, out object? value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var i)) { value = i; return true; }
                if (el.TryGetDouble(out var d)) { value = d; return true; }
                break;
            case JsonValueKind.String:
                value = el.GetString();
                return true;
            case JsonValueKind.True:  value = true;  return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Null:  value = null;  return true;
        }
        value = null;
        return false;
    }

    private static JsonElement? CoerceToJson(object? result) => result switch
    {
        null    => null,
        bool b  => JsonDocument.Parse(b ? "true" : "false").RootElement,
        int  i  => JsonDocument.Parse(i.ToString(CultureInfo.InvariantCulture)).RootElement,
        long l  => JsonDocument.Parse(l.ToString(CultureInfo.InvariantCulture)).RootElement,
        double d when double.IsFinite(d) => JsonDocument.Parse(FormatDouble(d)).RootElement,
        decimal dec => JsonDocument.Parse(dec.ToString(CultureInfo.InvariantCulture)).RootElement,
        string s => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement,
        _        => JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement,
    };

    private static string FormatDouble(double d)
    {
        // Prefer integer form when the value is exact, to keep JSON tidy.
        if (d == Math.Truncate(d) && d >= long.MinValue && d <= long.MaxValue)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
