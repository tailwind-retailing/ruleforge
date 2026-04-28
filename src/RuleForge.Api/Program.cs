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

// Auto-register every bound endpoint from the rule source.
var bindings = await app.Services.GetRequiredService<IRuleSource>().ListBindingsAsync();
foreach (var b in bindings)
{
    var endpointPath = b.Endpoint;
    var route = b.Method switch
    {
        HttpMethodKind.GET  => app.MapGet(endpointPath, (HttpContext http, IRuleSource source, RuleRunner runner) =>
            Dispatch(http, source, runner, endpointPath)),
        HttpMethodKind.POST => app.MapPost(endpointPath, (HttpContext http, IRuleSource source, RuleRunner runner) =>
            Dispatch(http, source, runner, endpointPath)),
        _ => throw new NotSupportedException($"unsupported method {b.Method} for {endpointPath}"),
    };
    app.Logger.LogInformation("Bound {Method} {Endpoint} â†’ {RuleId}@{Version}",
        b.Method, endpointPath, b.RuleId, b.Version);
}

// ─── admin ──────────────────────────────────────────────────────────────────
//
// Two ops surfaces, both gated by ApiKeyMiddleware:
//
//   GET  /admin/bindings  — list the auto-registered endpoints + cache stats
//   POST /admin/refresh   — flush the source caches (after a publish, etc.)
//
// Note: NEW endpoints still require a redeploy because routes are registered
// at boot. /admin/refresh handles version changes within existing endpoints.

app.MapGet("/admin/bindings", async (IRuleSource source, IServiceProvider sp) =>
{
    var live = await source.ListBindingsAsync();
    return Results.Json(new
    {
        bindings = live,
        registeredAtBoot = bindings,
        cache = ReadCacheStats(source, sp.GetService<IReferenceSetSource>()),
    });
});

app.MapPost("/admin/refresh", async (IRuleSource source, IServiceProvider sp, CancellationToken ct) =>
{
    var refSrc = sp.GetService<IReferenceSetSource>();
    var refreshedAt = DateTimeOffset.UtcNow;
    await source.RefreshAsync(ct);
    if (refSrc is not null) await refSrc.RefreshAsync(ct);
    return Results.Json(new
    {
        ok = true,
        refreshedAt,
        note = "Source caches dropped. Existing endpoint routes unchanged — adding " +
               "a NEW endpoint requires a redeploy.",
    });
});

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

static async Task<IResult> Dispatch(HttpContext http, IRuleSource source, RuleRunner runner, string endpoint)
{
    var rule = await source.GetByEndpointAsync(endpoint, HttpMethodKind.POST);
    if (rule is null)
        return Results.NotFound(new { error = $"no rule bound to POST {endpoint}" });

    using var doc = await JsonDocument.ParseAsync(http.Request.Body);
    var debug = http.Request.Query.ContainsKey("debug")
                || (http.Request.Headers.TryGetValue("X-Debug", out var v)
                    && v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

    var refSource = http.RequestServices.GetService<IReferenceSetSource>();
    var envelope = await runner.RunAsync(
        rule,
        doc.RootElement.Clone(),
        new RuleRunner.Options(
            Debug: debug,
            SubRuleSource: source,
            ReferenceSetSource: refSource),
        http.RequestAborted);
    return Results.Json(envelope);
}

public partial class Program { } // for WebApplicationFactory in tests
