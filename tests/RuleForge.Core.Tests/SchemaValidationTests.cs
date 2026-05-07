using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RuleForge.Core.Loader;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Unit tests for the SchemaValidator helper plus integration tests that
/// exercise the API host's pre-evaluation gate. The bag-policy fixture
/// declares a real inputSchema requiring cabin/orig/dest/bagPieces — used
/// here to drive both happy-path (200) and 400 paths.
/// </summary>
public class SchemaValidationTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // ─── unit tests on SchemaValidator ─────────────────────────────────────

    [Fact]
    public void Empty_schema_validates_any_payload()
    {
        Assert.Null(SchemaValidator.Validate(Json("{}"), Json("""{"any":"thing"}""")));
        Assert.Null(SchemaValidator.Validate(Json("{}"), Json("[]")));
        Assert.Null(SchemaValidator.Validate(Json("{}"), Json("42")));
    }

    [Fact]
    public void Null_or_non_object_schema_passes_through()
    {
        Assert.Null(SchemaValidator.Validate(Json("null"), Json("{}")));
        Assert.Null(SchemaValidator.Validate(Json("\"hello\""), Json("{}")));
        Assert.Null(SchemaValidator.Validate(Json("[]"), Json("{}")));
    }

    [Fact]
    public void Required_field_missing_yields_error_with_path()
    {
        var schema = Json("""
            {
              "type": "object",
              "required": ["name"],
              "properties": { "name": { "type": "string" } }
            }
            """);
        var err = SchemaValidator.Validate(schema, Json("{}"));
        Assert.NotNull(err);
        Assert.Contains("name", err);
    }

    [Fact]
    public void Type_mismatch_yields_error()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": { "amount": { "type": "number" } }
            }
            """);
        var err = SchemaValidator.Validate(schema, Json("""{"amount":"not-a-number"}"""));
        Assert.NotNull(err);
    }

    [Fact]
    public void Valid_payload_returns_null()
    {
        var schema = Json("""
            {
              "type": "object",
              "required": ["amount"],
              "properties": { "amount": { "type": "number" } }
            }
            """);
        Assert.Null(SchemaValidator.Validate(schema, Json("""{"amount":42}""")));
    }

    [Fact]
    public void Nested_required_field_missing_is_caught()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": {
                "pax": {
                  "type": "object",
                  "required": ["id"],
                  "properties": { "id": { "type": "string" } }
                }
              }
            }
            """);
        var err = SchemaValidator.Validate(schema, Json("""{"pax":{}}"""));
        Assert.NotNull(err);
    }

    // ─── API host integration ──────────────────────────────────────────────

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
    public async Task Api_returns_400_when_required_field_missing()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        // bag-policy requires cabin/orig/dest/bagPieces. Omit `dest`.
        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", bagPieces = 3 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("schema_validation_failed", body);
    }

    [Fact]
    public async Task Api_returns_400_when_type_mismatched()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        // bagPieces should be integer, not string.
        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", dest = "DXB", bagPieces = "three" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("schema_validation_failed", body);
    }

    [Fact]
    public async Task Api_passes_when_payload_is_valid()
    {
        using var factory = Factory(FixturesDir());
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/v1/ancillary/bag-policy",
            new { cabin = "Y", orig = "LHR", dest = "DXB", bagPieces = 3, markup = 0.0 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
