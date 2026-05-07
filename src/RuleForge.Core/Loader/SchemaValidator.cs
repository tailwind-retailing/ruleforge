using System.Text.Json;
using Json.Schema;

namespace RuleForge.Core.Loader;

/// <summary>
/// JSON Schema validation for rule input / output payloads. Wraps
/// <see cref="JsonSchema"/> to provide a single-call API that returns null on
/// success or a short, actionable description of the first violation on
/// failure.
/// <para>
/// Empty schemas (<c>{}</c> or any schema with no constraints) always
/// validate — the rule simply hasn't declared an input contract.
/// </para>
/// </summary>
public static class SchemaValidator
{
    /// <summary>
    /// Validates <paramref name="payload"/> against <paramref name="schema"/>.
    /// Returns null when valid; otherwise a string of the form
    /// <c>"path: message"</c> describing the first violation found.
    /// </summary>
    public static string? Validate(JsonElement schema, JsonElement payload)
    {
        // Trivial cases: missing / non-object / empty schemas have no constraints.
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (!schema.EnumerateObject().Any()) return null;

        JsonSchema parsed;
        try
        {
            parsed = JsonSchema.FromText(schema.GetRawText());
        }
        catch (Exception e)
        {
            // A malformed schema is an authoring error, not a validation error.
            // Surface it but distinct from "request didn't match".
            return $"$: schema is malformed ({e.Message})";
        }

        var results = parsed.Evaluate(payload, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (results.IsValid) return null;

        return FirstError(results) ?? "schema validation failed";
    }

    /// <summary>
    /// Walks the evaluation result tree depth-first looking for the first
    /// node that has an actual error message. Some results have errors at
    /// the root, others nest them in details.
    /// </summary>
    private static string? FirstError(EvaluationResults results)
    {
        if (results.Errors is { Count: > 0 } errors)
        {
            var path = results.InstanceLocation.ToString() is { Length: > 0 } p ? p : "$";
            var msg = errors.Values.FirstOrDefault() ?? "violation";
            return $"{path}: {msg}";
        }
        if (results.Details is not null)
        {
            foreach (var detail in results.Details)
            {
                var sub = FirstError(detail);
                if (sub is not null) return sub;
            }
        }
        return null;
    }
}
