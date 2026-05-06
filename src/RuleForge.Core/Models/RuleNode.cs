using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record RuleNode(
    string Id,
    string? Type,
    NodePosition Position,
    NodeData Data);

public sealed record NodePosition(double X, double Y);

public sealed record NodeData(
    string Label,
    NodeCategory Category,
    string? Description = null,
    string? TemplateId = null,
    JsonElement? Config = null,
    string? ConnectionId = null,
    SubRuleCall? SubRuleCall = null,
    IReadOnlyList<string>? ReadsContext = null,
    IReadOnlyList<string>? WritesContext = null);

[JsonConverter(typeof(JsonStringEnumConverter<NodeCategory>))]
public enum NodeCategory
{
    [JsonStringEnumMemberName("input")] Input,
    [JsonStringEnumMemberName("output")] Output,
    [JsonStringEnumMemberName("logic")] Logic,
    [JsonStringEnumMemberName("filter")] Filter,
    [JsonStringEnumMemberName("product")] Product,
    [JsonStringEnumMemberName("sql")] Sql,
    [JsonStringEnumMemberName("api")] Api,
    [JsonStringEnumMemberName("reference")] Reference,
    [JsonStringEnumMemberName("ruleRef")] RuleRef,
    [JsonStringEnumMemberName("calc")] Calc,
    [JsonStringEnumMemberName("constant")] Constant,
    [JsonStringEnumMemberName("mutator")] Mutator,
    [JsonStringEnumMemberName("iterator")] Iterator,
    [JsonStringEnumMemberName("merge")] Merge,
    [JsonStringEnumMemberName("bucket")] Bucket,
    [JsonStringEnumMemberName("assert")] Assert,
}

public sealed record SubRuleCall(
    string RuleId,
    IReadOnlyDictionary<string, string> InputMapping,
    IReadOnlyDictionary<string, string> OutputMapping,
    SubRuleErrorMode OnError,
    JsonElement? DefaultValue,
    JsonElement PinnedVersion,
    string? ForEach = null,
    string? As = null);

[JsonConverter(typeof(JsonStringEnumConverter<SubRuleErrorMode>))]
public enum SubRuleErrorMode
{
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("fail")] Fail,
    [JsonStringEnumMemberName("default")] Default,
}
