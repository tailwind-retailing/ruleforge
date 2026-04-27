using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

/// <summary>
/// Mutator node config. A mutator takes its single upstream node's output
/// (an object), overrides one field, and re-emits the modified object.
///
/// Two flavors share this shape:
///
///   set property â€” provide either <c>Value</c> (literal) or <c>From</c>
///                  (JSONPath, supports <c>$ctx.</c>) to compute the new value.
///   lookup &amp; replace â€” provide <c>Lookup</c> with a referenceId + matchOn
///                  + valueColumn. The engine fetches the referenced row and
///                  uses the named column as the new value.
/// </summary>
public sealed record MutatorConfig(
    string Target,
    System.Text.Json.JsonElement? Value = null,
    string? From = null,
    LookupSpec? Lookup = null,
    OnLookupMissing OnMissing = OnLookupMissing.Leave);

public sealed record LookupSpec(
    string ReferenceId,
    string ValueColumn,
    IReadOnlyDictionary<string, string> MatchOn);

[JsonConverter(typeof(JsonStringEnumConverter<OnLookupMissing>))]
public enum OnLookupMissing
{
    /// <summary>Leave the upstream object's existing value unchanged.</summary>
    [JsonStringEnumMemberName("leave")] Leave,
    /// <summary>Clear the field (set to JSON null).</summary>
    [JsonStringEnumMemberName("clear")] Clear,
    /// <summary>Mutator emits a verdict of <c>error</c> when no row matches.</summary>
    [JsonStringEnumMemberName("error")] Error,
}
