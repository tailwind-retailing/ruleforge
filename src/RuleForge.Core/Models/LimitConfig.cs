namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>limit</c> node — takes the first <c>Count</c>
/// items of the upstream array, optionally after skipping <c>Offset</c>.
/// Pairs naturally with <c>sort</c> for "cheapest 3 fares" patterns.
/// </summary>
public sealed record LimitConfig(
    int Count,
    int? Offset = null);
