using System.Text.Json;
using RuleForge.Core.Models;

namespace RuleForge.Core.Loader;

/// <summary>
/// Reads rule snapshots from on-disk JSON files. Slice 1's only IRuleSource impl;
/// DocumentForge-backed source comes in slice 2.
///
/// Layout: <c>{baseDir}/{ruleId}.v{version}.json</c>. The source also reads an
/// optional <c>{baseDir}/_endpoint-bindings.json</c> which maps
/// <c>"{METHOD} {endpoint}" â†’ "{ruleId}@{version}"</c>, mimicking
/// environments[*].ruleBindings without the env layer.
/// </summary>
public sealed class LocalFileRuleSource : IRuleSource
{
    private static readonly JsonSerializerOptions JsonOptions = AeroJson.Options;

    private readonly string _baseDir;
    private readonly Dictionary<string, string> _bindings;

    public LocalFileRuleSource(string baseDir)
    {
        _baseDir = baseDir;
        var bindingsFile = Path.Combine(_baseDir, "_endpoint-bindings.json");
        if (File.Exists(bindingsFile))
        {
            var json = File.ReadAllText(bindingsFile);
            _bindings = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                        ?? new Dictionary<string, string>();
        }
        else
        {
            _bindings = new Dictionary<string, string>();
        }
    }

    public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default)
    {
        var key = $"{method} {endpoint}";
        if (!_bindings.TryGetValue(key, out var binding))
            return Task.FromResult<Rule?>(null);

        var (ruleId, version) = ParseBinding(binding);
        return GetByIdAsync(ruleId, version, ct);
    }

    public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default)
    {
        // If a specific version is requested, load that file. Otherwise pick
        // the highest v{N}.json on disk for this ruleId.
        int? resolvedVersion = version;
        if (resolvedVersion is null)
        {
            var candidates = Directory.EnumerateFiles(_baseDir, $"{ruleId}.v*.json")
                .Select(f => TryExtractVersion(Path.GetFileName(f), ruleId))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();
            if (candidates.Count == 0) return Task.FromResult<Rule?>(null);
            resolvedVersion = candidates.Max();
        }

        var path = Path.Combine(_baseDir, $"{ruleId}.v{resolvedVersion}.json");
        if (!File.Exists(path)) return Task.FromResult<Rule?>(null);

        var text = File.ReadAllText(path);
        var rule = JsonSerializer.Deserialize<Rule>(text, JsonOptions)
                   ?? throw new InvalidOperationException($"rule {path} deserialised to null");
        return Task.FromResult<Rule?>(rule);
    }

    public Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default)
    {
        var bindings = new List<RuleBinding>(_bindings.Count);
        foreach (var (key, binding) in _bindings)
        {
            // key is "{METHOD} {endpoint}"
            var spaceIdx = key.IndexOf(' ');
            if (spaceIdx <= 0) continue;
            if (!Enum.TryParse<HttpMethodKind>(key[..spaceIdx], out var method)) continue;
            var endpoint = key[(spaceIdx + 1)..];
            var (ruleId, version) = ParseBinding(binding);
            bindings.Add(new RuleBinding(ruleId, version, endpoint, method));
        }
        return Task.FromResult<IReadOnlyList<RuleBinding>>(bindings);
    }

    private static int? TryExtractVersion(string fileName, string ruleId)
    {
        // {ruleId}.v{N}.json
        var prefix = $"{ruleId}.v";
        const string suffix = ".json";
        if (!fileName.StartsWith(prefix) || !fileName.EndsWith(suffix)) return null;
        var versionStr = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return int.TryParse(versionStr, out var v) ? v : null;
    }

    private static (string ruleId, int version) ParseBinding(string binding)
    {
        var at = binding.IndexOf('@');
        if (at < 0) throw new ArgumentException($"binding '{binding}' must be 'ruleId@version'");
        var id = binding[..at];
        var v = int.Parse(binding[(at + 1)..]);
        return (id, v);
    }
}
