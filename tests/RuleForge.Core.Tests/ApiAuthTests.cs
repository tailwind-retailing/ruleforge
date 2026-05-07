using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RuleForge.Core.Tests;

public class ApiAuthTests
{
    private static WebApplicationFactory<Program> Factory(string? expectedKey, string fixturesDir)
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
                if (expectedKey is not null) dict["RULEFORGE_API_KEY"] = expectedKey;
                cfg.AddInMemoryCollection(dict!);
            });
        });
    }

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

    [Fact]
    public async Task Health_open_when_key_required()
    {
        using var factory = Factory("the-key", FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Endpoint_blocked_without_key()
    {
        using var factory = Factory("the-key", FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", bagPieces = 3, route = "LHR-DXB" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("AeroKey", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Endpoint_accepts_X_AERO_Key_header()
    {
        using var factory = Factory("the-key", FixturesDir());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AERO-Key", "the-key");

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", dest = "DXB", bagPieces = 3, route = "LHR-DXB", markup = 0.0 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Endpoint_accepts_Authorization_Bearer()
    {
        using var factory = Factory("the-key", FixturesDir());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "the-key");

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", dest = "DXB", bagPieces = 3, route = "LHR-DXB", markup = 0.0 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Endpoint_rejects_wrong_key()
    {
        using var factory = Factory("the-key", FixturesDir());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AERO-Key", "WRONG");

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", bagPieces = 3, route = "LHR-DXB" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task When_no_key_configured_all_requests_allowed()
    {
        using var factory = Factory(null, FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", dest = "DXB", bagPieces = 3, route = "LHR-DXB", markup = 0.0 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
