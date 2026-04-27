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
    builder.Services.AddSingleton(_ => new DfClient(new HttpClient(), baseUrl, apiKey));
    builder.Services.AddSingleton<IRuleSource>(sp => new DocumentForgeRuleSource(sp.GetRequiredService<DfClient>(), envName));
    builder.Services.AddSingleton<IReferenceSetSource>(sp => new DocumentForgeReferenceSetSource(sp.GetRequiredService<DfClient>()));
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

// List the bindings at /admin/bindings for ops sanity.
app.MapGet("/admin/bindings", (IRuleSource source) =>
    Results.Json(source.ListBindingsAsync().GetAwaiter().GetResult()));

await app.RunAsync();

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
