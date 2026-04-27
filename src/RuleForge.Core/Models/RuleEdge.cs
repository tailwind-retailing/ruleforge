using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record RuleEdge(
    string Id,
    string Source,
    string Target,
    EdgeBranch? Branch = null,
    string? SourceHandle = null,
    string? TargetHandle = null,
    string? Label = null);

[JsonConverter(typeof(JsonStringEnumConverter<EdgeBranch>))]
public enum EdgeBranch
{
    [JsonStringEnumMemberName("pass")] Pass,
    [JsonStringEnumMemberName("fail")] Fail,
    [JsonStringEnumMemberName("default")] Default,
}
