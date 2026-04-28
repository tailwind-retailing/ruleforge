using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RuleForge.Core;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;

namespace RuleForge.DocumentForge;

/// <summary>
/// IRuleSource backed by DocumentForge. Resolves rules in three steps:
///   1. rule lookup by (endpoint, method) â†’ ruleId
///   2. environments[name].ruleBindings[ruleId] â†’ version
///   3. ruleversions filtered by (ruleId, version) â†’ snapshot
///
/// Rule snapshots are cached indefinitely (immutable). Env bindings are cached
/// for <see cref="EnvBindingsTtl"/> to keep up with publish-bind changes.
/// </summary>
public sealed class DocumentForgeRuleSource : IRuleSource
{
    public static readonly TimeSpan EnvBindingsTtl = TimeSpan.FromSeconds(30);

    private readonly DfClient _client;
    private readonly string _envName;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<(string ruleId, int version), Rule> _versionCache = new();
    private (DateTimeOffset loadedAt, IReadOnlyDictionary<string, int> bindings)? _envCache;
    private readonly SemaphoreSlim _envLock = new(1, 1);
    private DateTimeOffset _lastRefreshedAt = DateTimeOffset.UtcNow;

    /// <summary>When the in-memory caches were last fully invalidated.</summary>
    public DateTimeOffset LastRefreshedAt => _lastRefreshedAt;

    /// <summary>Number of cached <c>(ruleId, version)</c> snapshots.</summary>
    public int CachedSnapshotCount => _versionCache.Count;

    /// <summary>Active collection-name prefix (empty string when unset).</summary>
    public string CollectionPrefix => _prefix;

    /// <summary>
    /// Construct a rule source backed by DocumentForge.
    /// </summary>
    /// <param name="client">DF HTTP client.</param>
    /// <param name="envName">Environment whose <c>ruleBindings</c> to read at boot.</param>
    /// <param name="collectionPrefix">
    /// Optional namespacing prefix prepended to every collection name. Empty
    /// (default) → uses <c>rules</c>, <c>ruleversions</c>, <c>environments</c>.
    /// Set to e.g. <c>"aerotoys.tax."</c> to read from <c>aerotoys.tax.rules</c>,
    /// <c>aerotoys.tax.ruleversions</c>, <c>aerotoys.tax.environments</c> —
    /// lets multiple RuleForge instances share one DocumentForge cleanly.
    /// </param>
    public DocumentForgeRuleSource(DfClient client, string envName, string? collectionPrefix = null)
    {
        _client = client;
        _envName = envName;
        _prefix = collectionPrefix ?? string.Empty;
    }

    private string Rules => _prefix + "rules";
    private string RuleVersions => _prefix + "ruleversions";
    private string Environments => _prefix + "environments";

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _envLock.WaitAsync(ct);
        try
        {
            _versionCache.Clear();
            _envCache = null;
            _lastRefreshedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _envLock.Release();
        }
    }

    public async Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default)
    {
        var ruleId = await ResolveRuleIdAsync(endpoint, method, ct);
        if (ruleId is null) return null;

        var bindings = await GetBindingsAsync(ct);
        if (!bindings.TryGetValue(ruleId, out var version) || version <= 0)
            return null; // not bound to this environment

        return await GetByIdAsync(ruleId, version, ct);
    }

    public async Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
    {
        var resolved = version;
        if (resolved is null)
        {
            // Read the rule header to learn currentVersion.
            var header = await _client.QueryAsync<RuleHeader>(
                $"SELECT id, endpoint, method, currentVersion FROM {Rules} WHERE id = '{Escape(ruleId)}'", ct);
            var first = header.Documents.FirstOrDefault();
            if (first?.CurrentVersion is null or <= 0) return null;
            resolved = first.CurrentVersion;
        }

        var key = (ruleId, resolved.Value);
        if (_versionCache.TryGetValue(key, out var cached)) return cached;

        var snapshot = await LoadSnapshotAsync(ruleId, resolved.Value, ct);
        if (snapshot is not null) _versionCache[key] = snapshot;
        return snapshot;
    }

    public async Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default)
    {
        var bindings = await GetBindingsAsync(ct);
        if (bindings.Count == 0) return Array.Empty<RuleBinding>();

        // Per-id query: dfdb SQL doesn't support `IN(...)` so we fan out one
        // query per bound rule. Boot-time cost only (N is small in practice).
        var result = new List<RuleBinding>(bindings.Count);
        foreach (var (ruleId, version) in bindings)
        {
            if (version <= 0) continue;
            var sql = $"SELECT id, endpoint, method, currentVersion FROM {Rules} WHERE id = '{Escape(ruleId)}'";
            var query = await _client.QueryAsync<RuleHeader>(sql, ct);
            var header = query.Documents.FirstOrDefault();
            if (header is null) continue;
            if (!Enum.TryParse<HttpMethodKind>(header.Method, ignoreCase: true, out var method)) continue;
            result.Add(new RuleBinding(header.Id, version, header.Endpoint, method));
        }
        return result;
    }

    private async Task<string?> ResolveRuleIdAsync(string endpoint, HttpMethodKind method, CancellationToken ct)
    {
        var sql = "SELECT id, endpoint, method FROM " + Rules +
                  " WHERE endpoint = '" + Escape(endpoint) +
                  "' AND method = '" + method + "'";
        var result = await _client.QueryAsync<RuleHeader>(sql, ct);
        var match = result.Documents.FirstOrDefault();
        return match?.Id;
    }

    private async Task<IReadOnlyDictionary<string, int>> GetBindingsAsync(CancellationToken ct)
    {
        var snapshot = _envCache;
        if (snapshot is { } v && DateTimeOffset.UtcNow - v.loadedAt < EnvBindingsTtl)
            return v.bindings;

        await _envLock.WaitAsync(ct);
        try
        {
            snapshot = _envCache;
            if (snapshot is { } v2 && DateTimeOffset.UtcNow - v2.loadedAt < EnvBindingsTtl)
                return v2.bindings;

            // The DF GET /by/name lookup uses idx_environments_name which can
            // go stale after a failed unique-index PUT. We deliberately route
            // by id (convention: "env-{name}") and fall back to a full scan +
            // client-side filter so we keep working even if both indexes are
            // unhealthy.
            var byIdSql = $"SELECT * FROM {Environments} WHERE id = 'env-{Escape(_envName)}'";
            var envQuery = await _client.QueryAsync<EnvironmentDoc>(byIdSql, ct);
            var env = envQuery.Documents.FirstOrDefault();
            if (env is null)
            {
                var allEnvs = await _client.QueryAsync<EnvironmentDoc>($"SELECT * FROM {Environments}", ct);
                env = allEnvs.Documents.FirstOrDefault(e =>
                    string.Equals(e.Name, _envName, StringComparison.OrdinalIgnoreCase));
            }
            var bindings = (IReadOnlyDictionary<string, int>)(env?.RuleBindings ?? new Dictionary<string, int>());
            _envCache = (DateTimeOffset.UtcNow, bindings);
            return bindings;
        }
        finally
        {
            _envLock.Release();
        }
    }

    private async Task<Rule?> LoadSnapshotAsync(string ruleId, int version, CancellationToken ct)
    {
        var sql = $"SELECT * FROM {RuleVersions} WHERE ruleId = '{Escape(ruleId)}' AND version = {version}";
        var result = await _client.QueryAsync<RuleVersionDoc>(sql, ct);
        var rv = result.Documents.FirstOrDefault();
        return rv?.Snapshot;
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private sealed record RuleHeader(string Id, string Endpoint, string Method, int? CurrentVersion = null);

    private sealed class EnvironmentDoc
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, int> RuleBindings { get; set; } = new();
    }

    private sealed class RuleVersionDoc
    {
        public string RuleId { get; set; } = string.Empty;
        public int Version { get; set; }
        public Rule? Snapshot { get; set; }
    }
}
