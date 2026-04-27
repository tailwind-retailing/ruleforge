using System.Collections.Concurrent;
using System.Text.Json;

namespace RuleForge.Core.Loader;

/// <summary>
/// Reads reference sets from on-disk JSON files at <c>{baseDir}/{refId}.json</c>.
/// File shape: <c>{"id": "...", "name": "...", "columns": [...], "rows": [...]}</c>.
/// </summary>
public sealed class LocalFileReferenceSetSource : IReferenceSetSource
{
    private static readonly JsonSerializerOptions JsonOptions = AeroJson.Options;

    private readonly string _baseDir;
    private readonly ConcurrentDictionary<string, ReferenceSet> _cache = new();

    public LocalFileReferenceSetSource(string baseDir) { _baseDir = baseDir; }

    public Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(referenceId, out var cached)) return Task.FromResult<ReferenceSet?>(cached);

        var path = Path.Combine(_baseDir, $"{referenceId}.json");
        if (!File.Exists(path)) return Task.FromResult<ReferenceSet?>(null);

        var text = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<RawRefSet>(text, JsonOptions)
                  ?? throw new InvalidOperationException($"reference set {path} parsed to null");

        var rows = doc.Rows.Select(r => (IReadOnlyDictionary<string, JsonElement>)r).ToList();
        var refSet = new ReferenceSet(doc.Id, doc.Name, doc.Columns, rows, doc.CurrentVersion);
        _cache[referenceId] = refSet;
        return Task.FromResult<ReferenceSet?>(refSet);
    }

    private sealed class RawRefSet
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, JsonElement>> Rows { get; set; } = new();
        public int CurrentVersion { get; set; } = 1;
    }
}
