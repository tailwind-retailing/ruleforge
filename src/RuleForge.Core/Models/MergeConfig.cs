using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

/// <summary>
/// Merge node config. A merge closes the innermost open iteration scope:
/// it collects every iteration's upstream output and reduces them via
/// <see cref="Mode"/>. <see cref="Field"/> is required for the numeric
/// modes (<c>sum/avg/min/max</c>) and selects which property of each upstream
/// object to aggregate.
/// </summary>
public sealed record MergeConfig(
    MergeMode Mode = MergeMode.Collect,
    string? Field = null);

[JsonConverter(typeof(JsonStringEnumConverter<MergeMode>))]
public enum MergeMode
{
    /// <summary>Default — array of all upstream outputs in iteration order.</summary>
    [JsonStringEnumMemberName("collect")] Collect,
    /// <summary>Number of iterations whose upstream produced output.</summary>
    [JsonStringEnumMemberName("count")] Count,
    /// <summary>Sum of <c>Field</c> across iterations.</summary>
    [JsonStringEnumMemberName("sum")] Sum,
    /// <summary>Mean of <c>Field</c> (returns 0 on empty).</summary>
    [JsonStringEnumMemberName("avg")] Avg,
    /// <summary>Minimum <c>Field</c> across iterations.</summary>
    [JsonStringEnumMemberName("min")] Min,
    /// <summary>Maximum <c>Field</c> across iterations.</summary>
    [JsonStringEnumMemberName("max")] Max,
    /// <summary>First iteration's upstream output (in source-array order).</summary>
    [JsonStringEnumMemberName("first")] First,
    /// <summary>Last iteration's upstream output.</summary>
    [JsonStringEnumMemberName("last")] Last,
}
