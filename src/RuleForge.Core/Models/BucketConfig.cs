namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>bucket</c> node — sticky-hash deterministic
/// assignment to one of N named buckets, weighted by configured share.
/// <para>
/// Pairs naturally with shadow mode for real A/B testing: hash a stable
/// key (PNR, customerId) and route the same key to the same bucket every
/// time, across engine restarts. Hash is FNV-1a 32-bit — chosen for
/// stability across versions and platforms.
/// </para>
/// <para>
/// Output: the chosen bucket's <c>Name</c>, as a JSON string. Downstream
/// filter / logic / switch nodes route on it.
/// </para>
/// </summary>
public sealed record BucketConfig(
    string HashKey,
    IReadOnlyList<BucketSpec> Buckets);

public sealed record BucketSpec(string Name, int Weight);
