using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// /ready probes the rule source so orchestrators can distinguish
/// "process alive but rule source down" from "process alive and serving
/// traffic". /health stays as fast liveness; both bypass auth.
/// </summary>
public class ReadinessProbeTests
{
    private static string FixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "fixtures", "rules");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("fixtures/rules");
    }

    private static WebApplicationFactory<Program> Factory(
        string fixturesDir,
        string? expectedKey = null,
        Action<IServiceCollection>? servicesOverride = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["RULEFORGE_RULE_SOURCE"] = "local",
                    ["RULEFORGE_FIXTURES_DIR"] = fixturesDir,
                };
                if (expectedKey is not null) dict["RULEFORGE_API_KEY"] = expectedKey;
                cfg.AddInMemoryCollection(dict!);
            });
            if (servicesOverride is not null) b.ConfigureTestServices(servicesOverride);
        });

    [Fact]
    public async Task Ready_returns_200_when_source_works()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", body);
        Assert.Contains("\"ruleSource\":\"ok\"", body);
    }

    [Fact]
    public async Task Ready_bypasses_auth_when_key_required()
    {
        using var factory = Factory(FixturesDir(), expectedKey: "the-key");
        using var client = factory.CreateClient();

        // No X-AERO-Key header — readiness probes shouldn't need auth.
        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Health_still_bypasses_auth()
    {
        using var factory = Factory(FixturesDir(), expectedKey: "the-key");
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_503_when_source_throws()
    {
        var source = new ProgrammableRuleSource();
        using var factory = Factory(FixturesDir(), servicesOverride: services =>
        {
            services.RemoveAll<IRuleSource>();
            services.AddSingleton<IRuleSource>(source);
        });
        using var client = factory.CreateClient();    // boot enumeration succeeds (default mode)
        source.Mode = SourceMode.Throw;                // now flip to failure mode

        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":false", body);
        Assert.Contains("\"ruleSource\":\"error\"", body);
    }

    [Fact]
    public async Task Ready_returns_503_when_source_times_out()
    {
        var source = new ProgrammableRuleSource();
        using var factory = Factory(FixturesDir(), servicesOverride: services =>
        {
            services.RemoveAll<IRuleSource>();
            services.AddSingleton<IRuleSource>(source);
        });
        using var client = factory.CreateClient();    // boot enumeration succeeds
        source.Mode = SourceMode.Hang;                 // probe will hit the 2s timeout

        var resp = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ruleSource\":\"timeout\"", body);
    }

    private enum SourceMode { Ok, Throw, Hang }

    private sealed class ProgrammableRuleSource : IRuleSource
    {
        public SourceMode Mode { get; set; } = SourceMode.Ok;

        public Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default) =>
            Task.FromResult<Rule?>(null);
        public Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default) =>
            Task.FromResult<Rule?>(null);

        public async Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default)
        {
            switch (Mode)
            {
                case SourceMode.Throw:
                    throw new InvalidOperationException("simulated source failure");
                case SourceMode.Hang:
                    // Sleep longer than the 2s probe budget so the linked CTS fires.
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    break;
            }
            return Array.Empty<RuleBinding>();
        }
    }
}

internal static class ServiceCollectionRemoveExt
{
    public static void RemoveAll<T>(this IServiceCollection services) where T : class
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }
}
