using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class ApiNodeTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnSend { get; set; }
            = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}", Encoding.UTF8, "application/json") });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => OnSend(request, ct);
    }

    private static Rule BuildLinearRule(params RuleNode[] middle)
    {
        var nodes = new List<RuleNode>();
        nodes.Add(new RuleNode("i", "input", new(0, 0), new("in", NodeCategory.Input)));
        nodes.AddRange(middle);
        nodes.Add(new RuleNode("o", "output", new(0, 0), new("out", NodeCategory.Output)));
        var edges = new List<RuleEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
            edges.Add(new RuleEdge($"e{i}", nodes[i].Id, nodes[i + 1].Id, EdgeBranch.Default));
        return new Rule(
            "rule-test", "test", "/x", HttpMethodKind.POST,
            RuleStatus.Published, 1,
            JsonDocument.Parse("{}").RootElement,
            JsonDocument.Parse("{}").RootElement,
            nodes, edges, "2026-04-27T00:00:00.000Z");
    }

    private static RuleNode ApiNode(string id, string configJson) =>
        new(id, "api", new(0, 0), new(id, NodeCategory.Api, Config: Json(configJson)));

    // ─── happy paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task Api_get_with_literal_url_returns_full_body()
    {
        var handler = new StubHandler
        {
            OnSend = (req, _) =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Equal("https://api.example.com/v1/x", req.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"price":42,"currency":"USD"}""", Encoding.UTF8, "application/json"),
                });
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal(42, env.Result!.Value.GetProperty("price").GetInt32());
        Assert.Equal("USD", env.Result.Value.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Api_post_serializes_body_with_application_json_content_type()
    {
        string? capturedBody = null;
        string? capturedContentType = null;
        var handler = new StubHandler
        {
            OnSend = async (req, ct) =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                capturedContentType = req.Content.Headers.ContentType?.MediaType;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
                };
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/orders","method":"POST","timeoutMs":5000,"body":{"qty":3,"sku":"BAG"}}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http));

        Assert.Equal(Decision.Apply, env.Decision);
        Assert.Equal("application/json", capturedContentType);
        var parsedBody = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal(3, parsedBody.GetProperty("qty").GetInt32());
        Assert.Equal("BAG", parsedBody.GetProperty("sku").GetString());
    }

    [Fact]
    public async Task Url_resolved_from_jsonpath_against_request()
    {
        Uri? capturedUri = null;
        var handler = new StubHandler
        {
            OnSend = (req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"$.endpoint","method":"GET","timeoutMs":5000}"""));

        await new RuleRunner().RunAsync(rule,
            Json("""{"endpoint":"https://resolved.example.com/v1/y"}"""),
            new RuleRunner.Options(HttpClient: http));

        Assert.Equal("https://resolved.example.com/v1/y", capturedUri!.ToString());
    }

    [Fact]
    public async Task Header_resolved_from_jsonpath_and_literal_both_supported()
    {
        string? authHeader = null;
        string? userAgentHeader = null;
        var handler = new StubHandler
        {
            OnSend = (req, _) =>
            {
                authHeader = req.Headers.Authorization?.ToString();
                userAgentHeader = string.Join(" ", req.Headers.UserAgent.Select(p => p.Product?.ToString() ?? ""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a", """
            {
              "url": "https://api.example.com/v1/x",
              "method": "GET",
              "timeoutMs": 5000,
              "headers": {
                "Authorization": "$.token",
                "User-Agent": "ruleforge/1.0"
              }
            }
            """));

        await new RuleRunner().RunAsync(rule,
            Json("""{"token":"Bearer abc123"}"""),
            new RuleRunner.Options(HttpClient: http));

        Assert.Equal("Bearer abc123", authHeader);
        Assert.Contains("ruleforge/1.0", userAgentHeader!);
    }

    [Fact]
    public async Task ResponseMap_extracts_subset_of_response_body()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"price":99,"meta":{"src":"x"}},"count":1}""",
                    Encoding.UTF8, "application/json"),
            }),
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000,"responseMap":"$.data.price"}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http));

        Assert.Equal(99, env.Result!.Value.GetInt32());
    }

    [Fact]
    public async Task Body_placeholder_substitution_resolves_against_request()
    {
        string? capturedBody = null;
        var handler = new StubHandler
        {
            OnSend = async (req, ct) =>
            {
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a", """
            {
              "url": "https://api.example.com/v1/x",
              "method": "POST",
              "timeoutMs": 5000,
              "body": { "amount": "${$.amount}", "currency": "USD" }
            }
            """));

        await new RuleRunner().RunAsync(rule,
            Json("""{"amount":150}"""),
            new RuleRunner.Options(HttpClient: http));

        var parsed = JsonDocument.Parse(capturedBody!).RootElement;
        Assert.Equal(150, parsed.GetProperty("amount").GetInt32());
        Assert.Equal("USD", parsed.GetProperty("currency").GetString());
    }

    // ─── error paths ───────────────────────────────────────────────────────
    //
    // The runner catches per-node exceptions and turns them into Decision.Error
    // with the message recorded in the trace (RuleRunner.cs:130-140). So error
    // tests run with Debug=true and assert on the trace's error message.

    private static string AssertErrorAndGetMessage(Envelope env)
    {
        Assert.Equal(Decision.Error, env.Decision);
        Assert.NotNull(env.Trace);
        var errorEntry = env.Trace!.FirstOrDefault(t => t.Outcome == TraceOutcome.Error);
        Assert.NotNull(errorEntry);
        Assert.NotNull(errorEntry!.Error);
        return errorEntry.Error!;
    }

    [Fact]
    public async Task Non_2xx_response_yields_error_with_status_code_in_trace()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream", Encoding.UTF8, "text/plain"),
                ReasonPhrase = "Bad Gateway",
            }),
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("502", msg);
    }

    [Fact]
    public async Task Timeout_yields_error_with_timeoutMs_in_trace()
    {
        var handler = new StubHandler
        {
            OnSend = async (_, ct) =>
            {
                // Sleep longer than the configured timeout so the linked CTS fires.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/slow","method":"GET","timeoutMs":50}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("timed out", msg);
        Assert.Contains("50ms", msg);
    }

    [Fact]
    public async Task Malformed_response_json_yields_error()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("not-json{{{", Encoding.UTF8, "application/json") }),
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("not valid JSON", msg);
    }

    [Fact]
    public async Task Missing_HttpClient_on_options_yields_error()
    {
        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("HttpClient", msg);
    }

    [Fact]
    public async Task TimeoutMs_zero_yields_error()
    {
        using var http = new HttpClient(new StubHandler());
        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":0}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("timeoutMs", msg);
    }

    [Fact]
    public async Task Invalid_url_yields_error()
    {
        using var http = new HttpClient(new StubHandler());
        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"not-a-real-url","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http, Debug: true));

        var msg = AssertErrorAndGetMessage(env);
        Assert.Contains("not a valid absolute URI", msg);
    }

    [Fact]
    public async Task Empty_response_body_yields_json_null_value()
    {
        var handler = new StubHandler
        {
            OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("", Encoding.UTF8, "application/json") }),
        };
        using var http = new HttpClient(handler);

        var rule = BuildLinearRule(ApiNode("a",
            """{"url":"https://api.example.com/v1/x","method":"GET","timeoutMs":5000}"""));

        var env = await new RuleRunner().RunAsync(rule, Json("{}"),
            new RuleRunner.Options(HttpClient: http));

        // Empty body → JSON null; rule still applies. The runner may surface
        // this as a missing Result (envelope.Result == null) or as a Result
        // whose ValueKind is Null — either is acceptable.
        Assert.Equal(Decision.Apply, env.Decision);
        Assert.True(env.Result is null || env.Result.Value.ValueKind == JsonValueKind.Null);
    }
}
