using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>switch</c> node — multi-way branch on a single
/// resolved value. Resolves <c>Input</c> once, walks <c>Cases</c> in order,
/// emits the matched case's <c>Name</c> as a JSON string. If no case
/// matches, falls back to <c>Default</c>; missing default with no match
/// is an error.
/// <para>
/// v1 routing pattern: switch emits the chosen case name; downstream
/// <c>filter</c>/<c>logic</c> nodes route on it. Native N-way case edges
/// are a future extension that would let the runner skip the downstream
/// filter chain entirely.
/// </para>
/// <para>
/// Match comparison is type-aware: numbers as doubles, strings ordinal,
/// booleans by kind, others by raw-text equality.
/// </para>
/// </summary>
public sealed record SwitchConfig(
    string Input,
    IReadOnlyList<SwitchCase> Cases,
    string? Default = null);

public sealed record SwitchCase(
    JsonElement Match,
    string Name);
