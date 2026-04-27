using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record NumberFilterConfig(
    NumberFilterSource Source,
    NumberFilterCompare Compare,
    ArraySelector ArraySelector,
    OnMissing OnMissing);

public sealed record NumberFilterSource(
    SourceKind Kind,
    string? Path = null,
    double? Literal = null);

public sealed record NumberFilterCompare(
    NumberFilterOperator Operator,
    double? Value = null,
    IReadOnlyList<double>? Values = null,
    double? Min = null,
    double? Max = null,
    bool? MinInclusive = null,
    bool? MaxInclusive = null,
    Rounding? Round = null);

[JsonConverter(typeof(JsonStringEnumConverter<NumberFilterOperator>))]
public enum NumberFilterOperator
{
    [JsonStringEnumMemberName("equals")] Equals,
    [JsonStringEnumMemberName("not_equals")] NotEquals,
    [JsonStringEnumMemberName("gt")] Gt,
    [JsonStringEnumMemberName("gte")] Gte,
    [JsonStringEnumMemberName("lt")] Lt,
    [JsonStringEnumMemberName("lte")] Lte,
    [JsonStringEnumMemberName("between")] Between,
    [JsonStringEnumMemberName("not_between")] NotBetween,
    [JsonStringEnumMemberName("in")] In,
    [JsonStringEnumMemberName("not_in")] NotIn,
    [JsonStringEnumMemberName("is_null")] IsNull,
}

[JsonConverter(typeof(JsonStringEnumConverter<Rounding>))]
public enum Rounding
{
    [JsonStringEnumMemberName("floor")] Floor,
    [JsonStringEnumMemberName("ceil")] Ceil,
    [JsonStringEnumMemberName("round")] Round,
}
