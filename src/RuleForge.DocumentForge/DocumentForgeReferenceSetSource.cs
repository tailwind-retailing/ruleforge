using System.Collections.Concurrent;
using System.Text.Json;
using RuleForge.Core.Loader;

namespace RuleForge.DocumentForge;

/// <summary>
/// Reads reference-set rows from DocumentForge's <c>referencesetversions</c>
/// collection, latest version per id (matching the rules-collection convention).
/// Caches indefinitely â€” each version is immutable.
/// </summary>
public sealed class DocumentForgeReferenceSetSource : IReferenceSetSource
{
    private readonly DfClient _client;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, ReferenceSet> _cache = new();
    private DateTimeOffset _lastRefreshedAt = DateTimeOffset.UtcNow;

    public DateTimeOffset LastRefreshedAt => _lastRefreshedAt;
    public int CachedRefSetCount => _cache.Count;
    public string CollectionPrefix => _prefix;

    /// <summary>
    /// Construct a reference-set source backed by DocumentForge.
    /// </summary>
    /// <param name="client">DF HTTP client.</param>
    /// <param name="collectionPrefix">
    /// Optional namespacing prefix prepended to every collection name. Empty
    /// (default) → uses <c>referencesets</c>, <c>referencesetversions</c>.
    /// </param>
    public DocumentForgeReferenceSetSource(DfClient client, string? collectionPrefix = null)
    {
        _client = client;
        _prefix = collectionPrefix ?? string.Empty;
    }

    private string ReferenceSets => _prefix + "referencesets";
    private string ReferenceSetVersions => _prefix + "referencesetversions";

    public Task RefreshAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        _lastRefreshedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public async Task<ReferenceSet?> GetByIdAsync(string referenceId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(referenceId, out var cached)) return cached;

        // Load the rule-style metadata first so we know the latest version.
        var headerSql = $"SELECT id, name, currentVersion FROM {ReferenceSets} WHERE id = '{Escape(referenceId)}'";
        var header = (await _client.QueryAsync<RefSetHeader>(headerSql, ct)).Documents.FirstOrDefault();
        if (header is null) return null;
        var version = header.CurrentVersion > 0 ? header.CurrentVersion : 1;

        var dataSql = $"SELECT * FROM {ReferenceSetVersions} WHERE refId = '{Escape(referenceId)}' AND version = {version}";
        var rv = (await _client.QueryAsync<RefSetVersion>(dataSql, ct)).Documents.FirstOrDefault();
        if (rv is null) return null;

        var rows = rv.Rows.Select(r => (IReadOnlyDictionary<string, JsonElement>)r).ToList();
        var refSet = new ReferenceSet(referenceId, header.Name, rv.Columns, rows, version);
        _cache[referenceId] = refSet;
        return refSet;
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private sealed class RefSetHeader
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int CurrentVersion { get; set; }
    }

    private sealed class RefSetVersion
    {
        public string RefId { get; set; } = string.Empty;
        public int Version { get; set; }
        public List<string> Columns { get; set; } = new();
        public List<Dictionary<string, JsonElement>> Rows { get; set; } = new();
    }
}
