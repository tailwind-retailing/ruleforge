using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for an <c>api</c> node — generic outbound HTTP call.
/// <para>
/// <c>Url</c> and header values may be literals or raw JSONPaths
/// (<c>$.endpointUrl</c>, <c>$ctx.apiToken</c>); the runtime resolves them
/// at evaluation time. <c>Body</c>, when present, supports
/// <c>${...}</c> placeholders inside string values.
/// </para>
/// <para>
/// <c>TimeoutMs</c> is mandatory — there is no default. API nodes are
/// cold-path by definition (network latency dominates) and must declare
/// their per-call deadline explicitly.
/// </para>
/// </summary>
public sealed record ApiConfig(
    string Url,
    string Method,
    int TimeoutMs,
    IReadOnlyDictionary<string, string>? Headers = null,
    JsonElement? Body = null,
    string? ResponseMap = null);
