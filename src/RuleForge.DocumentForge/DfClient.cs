using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleForge.Core;

namespace RuleForge.DocumentForge;

/// <summary>
/// Thin DocumentForge HTTP client. Read + write surface used by AERO Engine.
///
/// Conventions observed against the live instance:
///   - Bearer token auth (Authorization: Bearer ...)
///   - GET /collections/{c}/by/{field}/{value}  â†’ { document: {...} }
///   - POST /query { sql }                      â†’ { documents: [...], count, ... }
///   - POST /collections/{c} {...}              â†’ { success, id, collection }   (id = DF _id)
///   - PUT  /collections/{c}/{_id} {...}        â†’ 200
///   - DELETE /collections/{c}/{_id}            â†’ 200
/// </summary>
public sealed class DfClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = AeroJson.Options;

    public DfClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public sealed record QueryResult<T>(IReadOnlyList<T> Documents, int Count, string? Plan, double ExecutionTimeMs);

    /// <summary>
    /// Run a SQL query. Returns the parsed `documents` array and the plan/timing
    /// info that DF returns so callers can log it.
    /// </summary>
    public async Task<QueryResult<T>> QueryAsync<T>(string sql, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("query", new { sql }, JsonOpts, ct);
        await EnsureSuccess(resp, "POST /query");
        var raw = await resp.Content.ReadAsStringAsync(ct);
        var body = JsonNode.Parse(raw)?.AsObject()
                   ?? throw new InvalidOperationException("DF /query returned non-object body: " + Truncate(raw, 200));

        var docs = body["documents"]?.AsArray() ?? new JsonArray();
        var parsed = new List<T>(docs.Count);
        foreach (var node in docs)
        {
            if (node is null) continue;
            // Re-serialize with our options so STJ can deserialize into T even if
            // JsonNode caching in body lost the original options context.
            var t = JsonSerializer.Deserialize<T>(node.ToJsonString(), JsonOpts);
            if (t is not null) parsed.Add(t);
        }

        return new QueryResult<T>(
            parsed,
            body["count"]?.GetValue<int>() ?? parsed.Count,
            body["plan"]?.GetValue<string>(),
            body["executionTimeMs"]?.GetValue<double>() ?? 0d);
    }

    /// <summary>
    /// Single-document fetch by an indexed field. Returns null on 404.
    /// The DF response shape is <c>{ document: {...} }</c>; we unwrap.
    /// </summary>
    public async Task<T?> GetByFieldAsync<T>(string collection, string field, string value, CancellationToken ct = default)
    {
        var url = $"collections/{collection}/by/{field}/{Uri.EscapeDataString(value)}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        await EnsureSuccess(resp, $"GET {url}");

        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct);
        var doc = body?["document"];
        return doc is null ? default : doc.Deserialize<T>(JsonOpts);
    }

    /// <summary>
    /// Insert a document. Returns the DF-assigned <c>_id</c> (hex string).
    /// </summary>
    public async Task<string> InsertAsync<T>(string collection, T doc, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"collections/{collection}", doc, JsonOpts, ct);
        await EnsureSuccess(resp, $"POST /collections/{collection}");
        var body = await resp.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct)
                   ?? throw new InvalidOperationException("DF insert returned empty body");
        return body["id"]?.GetValue<string>()
               ?? throw new InvalidOperationException("DF insert response had no id");
    }

    /// <summary>Replace a document by DF _id.</summary>
    public async Task ReplaceAsync<T>(string collection, string dfId, T doc, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync($"collections/{collection}/{dfId}", doc, JsonOpts, ct);
        await EnsureSuccess(resp, $"PUT /collections/{collection}/{dfId}");
    }

    /// <summary>Delete a document by DF _id. Idempotent over 404.</summary>
    public async Task DeleteAsync(string collection, string dfId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"collections/{collection}/{dfId}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        await EnsureSuccess(resp, $"DELETE /collections/{collection}/{dfId}");
    }

    private static async Task EnsureSuccess(HttpResponseMessage resp, string label)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"DocumentForge call failed: {label} â†’ {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "â€¦";
}
