namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>distinct</c> node — removes duplicate items from
/// the upstream array. <c>Key</c> is a JSONPath relative to each array
/// element, or <c>"self"</c>/<c>"$"</c>/null to dedup by whole-element
/// equality. <c>Keep</c> selects which occurrence to retain when duplicates
/// are found (preserving relative order otherwise).
/// </summary>
public sealed record DistinctConfig(
    string? Key = null,
    string? Keep = null);   // "first" (default) | "last"
