using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record StringFilterConfig(
    StringFilterSource Source,
    StringFilterCompare Compare,
    ArraySelector ArraySelector,
    OnMissing OnMissing,
    string? ReferenceId = null,
    string? ReferenceColumn = null);

public sealed record StringFilterSource(
    SourceKind Kind,
    string? Path = null,
    string? Literal = null);

public sealed record StringFilterCompare(
    StringFilterOperator Operator,
    string? Value = null,
    IReadOnlyList<string>? Values = null,
    bool? CaseInsensitive = null,
    bool? Trim = null);

[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind
{
    [JsonStringEnumMemberName("request")] Request,
    [JsonStringEnumMemberName("context")] Context,
    [JsonStringEnumMemberName("literal")] Literal,
}

[JsonConverter(typeof(JsonStringEnumConverter<StringFilterOperator>))]
public enum StringFilterOperator
{
    [JsonStringEnumMemberName("equals")] Equals,
    [JsonStringEnumMemberName("not_equals")] NotEquals,
    [JsonStringEnumMemberName("starts_with")] StartsWith,
    [JsonStringEnumMemberName("ends_with")] EndsWith,
    [JsonStringEnumMemberName("contains")] Contains,
    [JsonStringEnumMemberName("not_contains")] NotContains,
    [JsonStringEnumMemberName("in")] In,
    [JsonStringEnumMemberName("not_in")] NotIn,
    [JsonStringEnumMemberName("regex")] Regex,
    [JsonStringEnumMemberName("is_null")] IsNull,
    [JsonStringEnumMemberName("is_empty")] IsEmpty,
}

[JsonConverter(typeof(JsonStringEnumConverter<ArraySelector>))]
public enum ArraySelector
{
    [JsonStringEnumMemberName("any")] Any,
    [JsonStringEnumMemberName("all")] All,
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("first")] First,
    [JsonStringEnumMemberName("only")] Only,
}

[JsonConverter(typeof(JsonStringEnumConverter<OnMissing>))]
public enum OnMissing
{
    [JsonStringEnumMemberName("fail")] Fail,
    [JsonStringEnumMemberName("pass")] Pass,
    [JsonStringEnumMemberName("skip")] Skip,
}
