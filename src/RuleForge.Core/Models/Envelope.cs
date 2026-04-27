using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record Envelope(
    string RuleId,
    int RuleVersion,
    Decision Decision,
    string EvaluatedAt,
    JsonElement? Result,
    IReadOnlyList<TraceEntry>? Trace = null,
    long? DurationMs = null);

[JsonConverter(typeof(JsonStringEnumConverter<Decision>))]
public enum Decision
{
    [JsonStringEnumMemberName("apply")] Apply,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("error")] Error,
}

public sealed record TraceEntry(
    string NodeId,
    string StartedAt,
    long DurationMs,
    TraceOutcome Outcome,
    JsonElement? Input = null,
    JsonElement? Output = null,
    IReadOnlyDictionary<string, JsonElement>? CtxRead = null,
    IReadOnlyDictionary<string, JsonElement>? CtxWritten = null,
    string? SubRuleRunId = null,
    string? Error = null);

[JsonConverter(typeof(JsonStringEnumConverter<TraceOutcome>))]
public enum TraceOutcome
{
    [JsonStringEnumMemberName("pass")] Pass,
    [JsonStringEnumMemberName("fail")] Fail,
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("error")] Error,
}
