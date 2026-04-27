using System.Text.Json;

namespace RuleForge.Core;

public static class AeroJson
{
    /// <summary>Shared serializer options â€” camelCase, ignore-nulls, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
