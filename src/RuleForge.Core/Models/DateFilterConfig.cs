using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record DateFilterConfig(
    DateFilterSource Source,
    DateFilterCompare Compare,
    ArraySelector ArraySelector,
    OnMissing OnMissing);

public sealed record DateFilterSource(
    SourceKind Kind,
    string? Path = null,
    string? Literal = null);

public sealed record DateFilterCompare(
    DateFilterOperator Operator,
    DateGranularity Granularity = DateGranularity.Datetime,
    string? Value = null,
    string? From = null,
    string? To = null,
    int? Amount = null,
    DateUnit? Unit = null,
    string? Timezone = null,
    bool? FromInclusive = null,
    bool? ToInclusive = null);

[JsonConverter(typeof(JsonStringEnumConverter<DateFilterOperator>))]
public enum DateFilterOperator
{
    [JsonStringEnumMemberName("equals")] Equals,
    [JsonStringEnumMemberName("not_equals")] NotEquals,
    [JsonStringEnumMemberName("before")] Before,
    [JsonStringEnumMemberName("after")] After,
    [JsonStringEnumMemberName("between")] Between,
    [JsonStringEnumMemberName("not_between")] NotBetween,
    [JsonStringEnumMemberName("within_last")] WithinLast,
    [JsonStringEnumMemberName("within_next")] WithinNext,
    [JsonStringEnumMemberName("is_null")] IsNull,
}

[JsonConverter(typeof(JsonStringEnumConverter<DateGranularity>))]
public enum DateGranularity
{
    [JsonStringEnumMemberName("datetime")] Datetime,
    [JsonStringEnumMemberName("date")] Date,
    [JsonStringEnumMemberName("time")] Time,
}

[JsonConverter(typeof(JsonStringEnumConverter<DateUnit>))]
public enum DateUnit
{
    [JsonStringEnumMemberName("minutes")] Minutes,
    [JsonStringEnumMemberName("hours")] Hours,
    [JsonStringEnumMemberName("days")] Days,
    [JsonStringEnumMemberName("weeks")] Weeks,
    [JsonStringEnumMemberName("months")] Months,
}
