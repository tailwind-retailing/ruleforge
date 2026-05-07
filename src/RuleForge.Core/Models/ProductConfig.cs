using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>product</c> node — defines the rule's output
/// shape (the rule's "product"). Domain-agnostic: in airline merchandising
/// it's a product/service offer (a bag, fare, ancillary); in a validation
/// rule it might be a warning envelope; in an error path it's an error
/// shape. Whatever the rule emits, that's its product.
/// <para>
/// Two ways to author the shape:
/// <list type="bullet">
/// <item><c>Output</c>: a literal JSON shape (object, array, scalar) with
/// embedded <c>${...}</c> placeholders that resolve from upstream / ctx /
/// request / iteration frames at evaluation time.</item>
/// <item><c>OutputSchema</c>: an array of <c>{ key, value }</c> fields that
/// assemble into an object — same placeholder semantics on each value.
/// Convenient when authoring an object via a flat list (e.g. in editor UI).</item>
/// </list>
/// </para>
/// <para>
/// If neither field is set, the node falls through to the upstream sub-rule
/// result (when present) so a <c>ruleRef → product</c> chain works without
/// extra config.
/// </para>
/// </summary>
public sealed record ProductConfig(
    JsonElement? Output = null,
    IReadOnlyList<ProductSchemaField>? OutputSchema = null);

public sealed record ProductSchemaField(string Key, JsonElement Value);
