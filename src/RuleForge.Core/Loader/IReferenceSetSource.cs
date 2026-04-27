using RuleForge.Core.Models;

namespace RuleForge.Core.Loader;

public interface IReferenceSetSource
{
    /// <summary>
    /// Fetch a reference set by id. Returns null when no such reference set
    /// exists. Implementations should cache aggressively â€” reference sets
    /// are immutable per version.
    /// </summary>
    Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default);
}

public sealed record ReferenceSet(
    string Id,
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> Rows,
    int CurrentVersion);
