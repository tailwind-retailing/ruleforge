using System.Globalization;
using System.Text.Json;
using NCalc;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Evaluates a calc node's expression and produces a JsonElement. Variables
/// are resolved from a stacked namespace: upstream fields shadow ctx keys
/// shadow request fields. Numeric results round-trip back to JSON numbers;
/// booleans to JSON booleans; strings to JSON strings.
/// </summary>
public static class CalcEvaluator
{
    public static JsonElement? Evaluate(
        string expression,
        JsonElement? upstream,
        IDictionary<string, JsonElement> ctx,
        JsonElement request)
    {
        // Default options: case-sensitive operators / functions. We do our own
        // case-insensitive variable resolution against the JSON inputs below.
        var expr = new Expression(expression);

        expr.EvaluateParameter += (name, args) =>
        {
            if (TryResolveVariable(name, upstream, ctx, request, out var resolved))
                args.Result = resolved;
        };

        object? result;
        try
        {
            result = expr.Evaluate();
        }
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
        out object? value)
    {
        // Upstream fields take priority.
        if (upstream is { ValueKind: JsonValueKind.Object } u &&
            TryReadProperty(u, name, out value))
            return true;

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
