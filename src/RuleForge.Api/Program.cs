using System.Text.Json;
using RuleForge.Api;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using RuleForge.DocumentForge;

var builder = WebApplication.CreateBuilder(args);

var ruleSourceKind = (builder.Configuration["RULEFORGE_RULE_SOURCE"]
                      ?? Environment.GetEnvironmentVariable("RULEFORGE_RULE_SOURCE")
                      ?? "local").ToLowerInvariant();

if (ruleSourceKind == "df")
{
    var baseUrl = builder.Configuration["RULEFORGE_DF_BASE_URL"]
                  ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_BASE_URL")
                  ?? "https://documentforge.onrender.com";
    var apiKey = builder.Configuration["RULEFORGE_DF_API_KEY"]
                 ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_API_KEY")
                 ?? throw new InvalidOperationException("RULEFORGE_DF_API_KEY is required when RULEFORGE_RULE_SOURCE=df");
    var envName = builder.Configuration["RULEFORGE_ENV"]
                  ?? Environment.GetEnvironmentVariable("RULEFORGE_ENV")
                  ?? "staging";
    // Optional collection-name namespacing — lets multiple RuleForge instances
    // share one DocumentForge cleanly. Empty (default) keeps the current
    // behavior. Common pattern: "aerotoys.tax." or "aerotoys.offer.".
    var prefix = builder.Configuration["RULEFORGE_COLLECTION_PREFIX"]
                 ?? Environment.GetEnvironmentVariable("RULEFORGE_COLLECTION_PREFIX")
                 ?? "";
    builder.Services.AddSingleton(_ => new DfClient(new HttpClient(), baseUrl, apiKey));
    builder.Services.AddSingleton<IRuleSource>(sp =>
        new DocumentForgeRuleSource(sp.GetRequiredService<DfClient>(), envName, prefix));
    builder.Services.AddSingleton<IReferenceSetSource>(sp =>
        new DocumentForgeReferenceSetSource(sp.GetRequiredService<DfClient>(), prefix));
}
else
{
    var fixturesDir = builder.Configuration["RULEFORGE_FIXTURES_DIR"]
                      ?? Environment.GetEnvironmentVariable("RULEFORGE_FIXTURES_DIR")
                      ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "rules");
    var refsDir = builder.Configuration["RULEFORGE_REFS_DIR"]
                  ?? Environment.GetEnvironmentVariable("RULEFORGE_REFS_DIR")
                  ?? Path.Combine(Path.GetFullPath(Path.Combine(fixturesDir, "..")), "refs");
    builder.Services.AddSingleton<IRuleSource>(_ => new LocalFileRuleSource(Path.GetFullPath(fixturesDir)));
    builder.Services.AddSingleton<IReferenceSetSource>(_ => new LocalFileReferenceSetSource(Path.GetFullPath(refsDir)));
}

builder.Services.AddSingleton<RuleRunner>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = AeroJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = AeroJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.PropertyNameCaseInsensitive = AeroJson.Options.PropertyNameCaseInsensitive;
});

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// ─── admin ──────────────────────────────────────────────────────────────────
//
// Two ops surfaces, both gated by ApiKeyMiddleware:
//
//   GET  /admin/bindings  — list live bindings + cache stats
//   POST /admin/refresh   — flush source caches (after a publish, etc.)
//
// Routing is dynamic (see catch-all below) so /admin/refresh now picks up
// BOTH new versions AND new endpoints — no redeploy required for either.
// Explicit /health and /admin/* routes have higher precedence than the
// catch-all and so always win.

app.MapGet("/admin/bindings", async (IRuleSource source, IServiceProvider sp) =>
{
    var live = await source.ListBindingsAsync();
    return Results.Json(new
    {
        bindings = live,
        bindingCount = live.Count,
        cache = ReadCacheStats(source, sp.GetService<IReferenceSetSource>()),
        routing = "dynamic",
    });
});

app.MapPost("/admin/refresh", async (IRuleSource source, IServiceProvider sp, ILoggerFactory lf, CancellationToken ct) =>
{
    var refSrc = sp.GetService<IReferenceSetSource>();
    var refreshedAt = DateTimeOffset.UtcNow;
    await source.RefreshAsync(ct);
    if (refSrc is not null) await refSrc.RefreshAsync(ct);
    var live = await source.ListBindingsAsync();
    var log = lf.CreateLogger("Refresh");
    foreach (var b in live)
        log.LogInformation("Rebound {Method} {Endpoint} -> {RuleId}@{Version}",
            b.Method, b.Endpoint, b.RuleId, b.Version);
    log.LogInformation("Refresh complete. {Count} binding(s) live.", live.Count);
    return Results.Json(new
    {
        ok = true,
        refreshedAt,
        bindingCount = live.Count,
        bindings = live,
        note = "Source caches dropped and bindings re-enumerated. New endpoints AND new versions are live immediately — no redeploy required.",
    });
});

// ─── boot enumeration (for log/observability only) ──────────────────────────
//
// Routing itself is dynamic — see the catch-all below. We still walk the
// bindings at boot so the deploy log shows what's wired, and so anything
// missing in DF (env doc, ruleversions row) blows up loudly at startup
// rather than on the first request.
var bootBindings = await app.Services.GetRequiredService<IRuleSource>().ListBindingsAsync();
foreach (var b in bootBindings)
{
    app.Logger.LogInformation("Bound {Method} {Endpoint} -> {RuleId}@{Version}",
        b.Method, b.Endpoint, b.RuleId, b.Version);
}
app.Logger.LogInformation(
    "Discovered {Count} binding(s) at boot. Routing is dynamic — POST /admin/refresh to pick up changes.",
    bootBindings.Count);

// ─── dynamic catch-all ──────────────────────────────────────────────────────
//
// Every request not matched by an explicit route above resolves live via
// IRuleSource.GetByEndpointAsync(path, method). That means publishing a
// brand-new endpoint and POSTing /admin/refresh is enough — no redeploy.
// The DF source already caches snapshots indefinitely (immutable per
// version) and env bindings for 30s, so the steady-state cost is a
// dictionary lookup.
app.MapMethods("/{**path}", new[] { "GET", "POST" },
    async (HttpContext http, IRuleSource source, RuleRunner runner) =>
        await Dispatch(http, source, runner));

await app.RunAsync();

static object ReadCacheStats(IRuleSource ruleSrc, IReferenceSetSource? refSrc)
{
    var rules = ruleSrc as RuleForge.DocumentForge.DocumentForgeRuleSource;
    var refs  = refSrc  as RuleForge.DocumentForge.DocumentForgeReferenceSetSource;
    return new
    {
        ruleSnapshots = rules?.CachedSnapshotCount,
        rulesLastRefreshedAt = rules?.LastRefreshedAt,
        referenceSets = refs?.CachedRefSetCount,
        refsLastRefreshedAt = refs?.LastRefreshedAt,
    };
}

static async Task<IResult> Dispatch(HttpContext http, IRuleSource source, RuleRunner runner)
{
    var endpoint = http.Request.Path.Value ?? "/";
    var method = string.Equals(http.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)
        ? HttpMethodKind.GET
        : HttpMethodKind.POST;

    var rule = await source.GetByEndpointAsync(endpoint, method);
    if (rule is null)
        return Results.NotFound(new { error = $"no rule bound to {http.Request.Method} {endpoint}" });

    // Body is optional — GETs typically have none, and POSTs may legitimately
    // ship empty bodies for parameterless rules. Default to {} when absent.
    JsonElement payload;
    if (http.Request.ContentLength is > 0
        || (http.Request.ContentLength is null && http.Request.Body.CanRead && http.Request.Method == "POST"))
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            payload = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var fallback = JsonDocument.Parse("{}");
            payload = fallback.RootElement.Clone();
        }
    }
    else
    {
        using var fallback = JsonDocument.Parse("{}");
        payload = fallback.RootElement.Clone();
    }

    var debug = http.Request.Query.ContainsKey("debug")
                || (http.Request.Headers.TryGetValue("X-Debug", out var v)
                    && v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

    var refSource = http.RequestServices.GetService<IReferenceSetSource>();
    var envelope = await runner.RunAsync(
        rule,
        payload,
        new RuleRunner.Options(
            Debug: debug,
            SubRuleSource: source,
            ReferenceSetSource: refSource),
        http.RequestAborted);
    return Results.Json(envelope);
}

public partial class Program { } // for WebApplicationFactory in tests
