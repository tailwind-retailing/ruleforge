using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuleForge.Core.Loader;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Proves the catch-all route resolves bindings live, and that POST
/// /admin/refresh picks up a NEW endpoint without restarting the host —
/// the property the tax team needs for publish→serve loops on a long-
/// lived Render instance.
/// </summary>
public class DynamicRoutingTests
{
    [Fact]
    public async Task New_endpoint_becomes_reachable_after_refresh()
    {
        // 1. Stand up a writable copy of the fixtures pack so we can mutate
        //    _endpoint-bindings.json mid-flight.
        var sandbox = Path.Combine(Path.GetTempPath(), "ruleforge-dyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var src = LocateFixturesDir();
            foreach (var f in Directory.EnumerateFiles(src))
                File.Copy(f, Path.Combine(sandbox, Path.GetFileName(f)));

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                // Replace the IRuleSource registered by Program.cs with one
                // pointing at our writable sandbox. ConfigureTestServices
                // runs after Program.cs's own service registration, so the
                // last writer wins.
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IRuleSource>();
                    services.AddSingleton<IRuleSource>(_ => new LocalFileRuleSource(sandbox));
                });
            });
            using var client = factory.CreateClient();

            // 2. Sanity: existing endpoint resolves dynamically through the
            //    catch-all (we're not relying on a boot-time MapPost).
            var existingResp = await client.PostAsync("/v1/tax/pnr",
                new StringContent("{\"orig\":\"LHR\",\"taxCode\":\"GB1\",\"pax\":[{\"id\":\"p1\",\"ageCategory\":\"ADT\"}]}",
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, existingResp.StatusCode);

            // 3. NEW endpoint — not yet bound — must 404.
            var newPath = "/v1/tax/pnr-mirror";
            var unboundResp = await client.PostAsync(newPath,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, unboundResp.StatusCode);

            // 4. Add a binding for the new path (reuse the existing rule).
            var bindingsFile = Path.Combine(sandbox, "_endpoint-bindings.json");
            var bindings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(bindingsFile))!;
            bindings[$"POST {newPath}"] = "rule-pnr-taxes@1";
            File.WriteAllText(bindingsFile, JsonSerializer.Serialize(bindings));

            // Confirm the on-disk write actually reflects the new key — guards
            // against the sandbox being a different dir from the host's view.
            var verifyOnDisk = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(bindingsFile))!;
            Assert.Contains($"POST {newPath}", verifyOnDisk.Keys);

            // Sanity: bindings BEFORE refresh shouldn't include the new path —
            // the source still holds the dict from construction.
            var preBindings = await client.GetAsync("/admin/bindings");
            var preBody = await preBindings.Content.ReadFromJsonAsync<JsonElement>();
            Assert.DoesNotContain(preBody.GetProperty("bindings").EnumerateArray(),
                b => b.GetProperty("endpoint").GetString() == newPath);

            // 5. /admin/refresh should drop caches and re-read bindings.
            var refreshResp = await client.PostAsync("/admin/refresh", content: null);
            Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
            var refreshBody = await refreshResp.Content.ReadFromJsonAsync<JsonElement>();
            var refreshedEndpoints = refreshBody.GetProperty("bindings").EnumerateArray()
                .Select(b => b.GetProperty("endpoint").GetString()).ToList();
            Assert.Contains(newPath, refreshedEndpoints);

            // 6. The new endpoint is now reachable — without a restart.
            var nowBoundResp = await client.PostAsync(newPath,
                new StringContent("{\"orig\":\"LHR\",\"taxCode\":\"GB1\",\"pax\":[{\"id\":\"p1\",\"ageCategory\":\"ADT\"}]}",
                    Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, nowBoundResp.StatusCode);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Catch_all_returns_404_for_unbound_path()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RULEFORGE_RULE_SOURCE"] = "local",
                ["RULEFORGE_FIXTURES_DIR"] = LocateFixturesDir(),
            }!));
        });
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/this/path/does/not/exist",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("/this/path/does/not/exist", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Health_route_takes_precedence_over_catch_all()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RULEFORGE_RULE_SOURCE"] = "local",
                ["RULEFORGE_FIXTURES_DIR"] = LocateFixturesDir(),
                // Even with an API key configured, /health stays open — and
                // the catch-all must never shadow it.
                ["RULEFORGE_API_KEY"] = "the-key",
            }!));
        });
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static string LocateFixturesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var c = Path.Combine(dir, "fixtures", "rules");
            if (Directory.Exists(c)) return c;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("fixtures/rules");
    }
}
