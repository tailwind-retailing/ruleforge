namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>sort</c> node — sorts the upstream array by a key.
/// <para>
/// <c>SortKey</c> is a JSONPath relative to each array element, or
/// <c>"self"</c>/<c>"$"</c>/null to sort by the whole element. Direction
/// defaults to <c>asc</c>; null handling defaults to <c>last</c>.
/// </para>
/// <para>
/// Sort is stable. Mixed-type comparison (e.g. numbers vs strings)
/// falls back to raw-text ordering — deterministic but not semantically
/// meaningful, so callers should ensure homogeneous key types.
/// </para>
/// </summary>
public sealed record SortConfig(
    string? SortKey = null,
    string? Direction = null,   // "asc" (default) | "desc"
    string? Nulls = null);      // "first" | "last" (default) | "error"
