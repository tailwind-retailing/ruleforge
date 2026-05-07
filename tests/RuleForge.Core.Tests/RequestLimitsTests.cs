using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Defense-in-depth limits on the request hot path: malformed JSON is now
/// 400 (was silently substituted {}), JsonDocumentOptions.MaxDepth=32
/// blocks billion-laughs payloads, and Kestrel.MaxRequestBodySize=5MB
/// caps oversize POSTs.
/// </summary>
public class RequestLimitsTests
{
    private static WebApplicationFactory<Program> Factory(string fixturesDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RULEFORGE_RULE_SOURCE"] = "local",
                ["RULEFORGE_FIXTURES_DIR"] = fixturesDir,
            }!));
        });

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
    public async Task Malformed_json_returns_400_not_silent_empty_object()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        var content = new StringContent("{not-json", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/ancillary/bag-policy", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid_json", body);
    }

    [Fact]
    public async Task Deeply_nested_json_beyond_max_depth_returns_400()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        // 50 levels of nesting — exceeds MaxDepth=32.
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) sb.Append("{\"a\":");
        sb.Append("0");
        for (var i = 0; i < 50; i++) sb.Append('}');

        var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/ancillary/bag-policy", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Modest_nesting_within_limit_passes_through()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        // 10 levels — comfortably under MaxDepth=32. Just verifying that the
        // depth cap isn't accidentally too tight for realistic payloads.
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++) sb.Append("{\"a\":");
        sb.Append("0");
        for (var i = 0; i < 10; i++) sb.Append('}');

        var content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/v1/ancillary/bag-policy", content);

        // Whatever the rule decides (apply / skip / error) — we only care
        // that JSON parsing succeeded and the body wasn't 400.
        Assert.NotEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
