namespace RuleForge.Api;

/// <summary>
/// Minimal X-AERO-Key shared-secret check. Configure with the
/// <c>RULEFORGE_API_KEY</c> env var (or <c>RULEFORGE_API_KEY</c>
/// configuration entry). When unset, every request is allowed â€” useful for
/// local dev. When set, only requests whose <c>X-AERO-Key</c> header (or
/// <c>Authorization: Bearer ...</c>) matches are accepted.
///
/// Bypass paths: <c>/health</c> stays open so monitoring + load balancers
/// don't need to ship the key.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-AERO-Key";

    private static readonly HashSet<string> BypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
    };

    private readonly RequestDelegate _next;
    private readonly string? _expected;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _expected = config["RULEFORGE_API_KEY"]
                    ?? Environment.GetEnvironmentVariable("RULEFORGE_API_KEY");
    }

    public async Task InvokeAsync(HttpContext http)
    {
        if (string.IsNullOrEmpty(_expected) || BypassPaths.Contains(http.Request.Path))
        {
            await _next(http);
            return;
        }

        if (TryReadKey(http.Request, out var supplied) &&
            FixedTimeEquals(supplied, _expected))
        {
            await _next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers["WWW-Authenticate"] = $"AeroKey realm=\"aero-engine\"";
        await http.Response.WriteAsJsonAsync(new
        {
            error = "missing or invalid X-AERO-Key",
        });
    }

    private static bool TryReadKey(HttpRequest req, out string supplied)
    {
        if (req.Headers.TryGetValue(HeaderName, out var fromHeader) &&
            fromHeader.Count > 0 && !string.IsNullOrEmpty(fromHeader[0]))
        {
            supplied = fromHeader[0]!;
            return true;
        }
        if (req.Headers.TryGetValue("Authorization", out var auth) && auth.Count > 0)
        {
            const string prefix = "Bearer ";
            var v = auth[0] ?? string.Empty;
            if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                supplied = v.Substring(prefix.Length).Trim();
                return supplied.Length > 0;
            }
        }
        supplied = string.Empty;
        return false;
    }

    /// <summary>Constant-time string comparison to deter timing attacks.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
