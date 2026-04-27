using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleForge.Core.Models;

public sealed record Rule(
    string Id,
    string Name,
    string Endpoint,
    HttpMethodKind Method,
    RuleStatus Status,
    int CurrentVersion,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    IReadOnlyList<RuleNode> Nodes,
    IReadOnlyList<RuleEdge> Edges,
    string UpdatedAt,
    JsonElement? ContextSchema = null,
    string? ProjectId = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? Category = null,
    string? UpdatedBy = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HttpMethodKind { GET, POST }

[JsonConverter(typeof(JsonStringEnumConverter<RuleStatus>))]
public enum RuleStatus
{
    [JsonStringEnumMemberName("draft")] Draft,
    [JsonStringEnumMemberName("review")] Review,
    [JsonStringEnumMemberName("published")] Published,
}
