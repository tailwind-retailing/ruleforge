using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Coverage for the /admin/* surface — bindings listing and cache refresh.
/// Auth-bypass / require behaviour is exercised in <see cref="ApiAuthTests"/>;
/// these tests focus on shape + side effects.
/// </summary>
public class AdminEndpointTests
{
    private static WebApplicationFactory<Program> Factory(string fixturesDir, string? apiKey = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["RULEFORGE_RULE_SOURCE"] = "local",
                    ["RULEFORGE_FIXTURES_DIR"] = fixturesDir,
                };
                if (apiKey is not null) dict["RULEFORGE_API_KEY"] = apiKey;
                cfg.AddInMemoryCollection(dict!);
            });
        });
    }

    private static string FixturesDir()
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

    [Fact]
    public async Task Bindings_lists_registered_endpoints_and_cache_stats()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/admin/bindings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("bindings", out var bindings));
        Assert.True(body.TryGetProperty("registeredAtBoot", out var registered));
        Assert.True(body.TryGetProperty("cache", out _));

        // The local-fixtures pack ships several rules; both lists should match.
        Assert.Equal(JsonValueKind.Array, bindings.ValueKind);
        Assert.Equal(JsonValueKind.Array, registered.ValueKind);
        Assert.True(bindings.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Refresh_returns_200_with_timestamp_and_note()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/admin/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.TryGetProperty("refreshedAt", out _));
        Assert.True(body.TryGetProperty("note", out var note));
        Assert.Contains("redeploy", note.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_requires_api_key_when_configured()
    {
        using var factory = Factory(FixturesDir(), apiKey: "the-key");
        using var client = factory.CreateClient();

        var unauthed = await client.PostAsync("/admin/refresh", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthed.StatusCode);

        client.DefaultRequestHeaders.Add("X-AERO-Key", "the-key");
        var authed = await client.PostAsync("/admin/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
    }
}
