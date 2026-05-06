using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using RuleForge.DocumentForge;

// AERO Engine CLI â€” slice 2.
//
// Verbs:
//   run     â€” execute a rule against a request (default)
//   publish â€” push a local rule snapshot to DocumentForge as a new
//             ruleversion, then bind the active env to it
//
// If the first arg starts with "--" the verb defaults to `run`, preserving
// the slice-1 invocation form.

if (args.Length == 0) { PrintTopLevelHelp(); return 1; }

string verb;
string[] rest;
if (args[0].StartsWith("--"))
{
    verb = "run";
    rest = args;
}
else
{
    verb = args[0];
    rest = args[1..];
}

return verb switch
{
    "run"     => await RunVerb(rest),
    "publish" => await PublishVerb(rest),
    "mirror"  => await MirrorVerb(rest),
    "bench"   => await BenchVerb(rest),
    "schemas" => SchemasVerb(rest),
    "-h" or "--help" => PrintTopLevelHelpAndOk(),
    _ => Unknown(verb),
};

static int PrintTopLevelHelpAndOk() { PrintTopLevelHelp(); return 0; }

static int Unknown(string verb)
{
    Console.Error.WriteLine($"unknown verb: {verb}");
    PrintTopLevelHelp();
    return 1;
}

static void PrintTopLevelHelp()
{
    Console.Error.WriteLine("""
        RuleForge.Cli â€” runtime + admin

        Verbs:
          run      Execute a rule against a request (default if no verb given)
          publish  Push a local rule snapshot to DocumentForge
          mirror   Copy collections from one DocumentForge instance to another
          bench    Benchmark the engine against a rule source
          schemas  Export JSON Schemas for every config record (for UI builders)

        Use `aero <verb> --help` for verb-specific options.
        """);
}

// â”€â”€â”€ run â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
static async Task<int> RunVerb(string[] argv)
{
    var opts = ParseRunOpts(argv);
    if (opts is null) { PrintRunHelp(); return 1; }

    var requestJson = ReadInline(opts.RequestArg);
    JsonElement request;
    try
    {
        using var doc = JsonDocument.Parse(requestJson);
        request = doc.RootElement.Clone();
    }
    catch (JsonException e)
    {
        Console.Error.WriteLine($"--request value is not valid JSON: {e.Message}");
        return 2;
    }

    var indented = new JsonSerializerOptions(AeroJson.Options) { WriteIndented = true };

    if (opts.HttpBaseUrl is null)
    {
        var (source, refSource) = opts.UseDf
            ? BuildDfSources(opts)
            : BuildLocalSources(opts);

        var rule = await source.GetByEndpointAsync(opts.Endpoint, HttpMethodKind.POST);
        if (rule is null)
        {
            Console.Error.WriteLine($"no rule bound to POST {opts.Endpoint}");
            return 3;
        }

        Console.WriteLine($"â”â” in-process run: {rule.Name} (v{rule.CurrentVersion}) â”â”");
        Console.WriteLine($"endpoint : POST {rule.Endpoint}");
        Console.WriteLine($"source   : {(opts.UseDf ? "DocumentForge" : "local file")}");
        Console.WriteLine($"debug    : {opts.Debug}");
        Console.WriteLine("request  :");
        Console.WriteLine(JsonSerializer.Serialize(request, indented));
        Console.WriteLine();

        var envelope = await new RuleRunner().RunAsync(
            rule,
            request,
            new RuleRunner.Options(
                Debug: opts.Debug,
                SubRuleSource: source,
                ReferenceSetSource: refSource));
        Console.WriteLine("envelope :");
        Console.WriteLine(JsonSerializer.Serialize(envelope, indented));
        return envelope.Decision == Decision.Error ? 4 : 0;
    }

    var url = $"{opts.HttpBaseUrl.TrimEnd('/')}{opts.Endpoint}{(opts.Debug ? "?debug=true" : "")}";
    Console.WriteLine($"â”â” HTTP POST {url} â”â”");
    Console.WriteLine("request  :");
    Console.WriteLine(JsonSerializer.Serialize(request, indented));
    Console.WriteLine();

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var resp = await client.PostAsJsonAsync(url, request);
    var body = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"status   : {(int)resp.StatusCode} {resp.StatusCode}");
    Console.WriteLine("envelope :");
    try
    {
        using var parsed = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(parsed.RootElement, indented));
    }
    catch
    {
        Console.WriteLine(body);
    }
    return resp.IsSuccessStatusCode ? 0 : 5;
}

// â”€â”€â”€ publish â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
static async Task<int> PublishVerb(string[] argv)
{
    var opts = ParsePublishOpts(argv);
    if (opts is null) { PrintPublishHelp(); return 1; }

    var rulePath = Path.GetFullPath(opts.RuleFile);
    if (!File.Exists(rulePath))
    {
        Console.Error.WriteLine($"rule file not found: {rulePath}");
        return 2;
    }

    var snapshotJson = await File.ReadAllTextAsync(rulePath);
    var snapshot = JsonSerializer.Deserialize<Rule>(snapshotJson, AeroJson.Options)
                   ?? throw new InvalidOperationException("rule file did not parse as Rule");

    var apiKey = opts.ApiKey
                 ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_API_KEY")
                 ?? throw new InvalidOperationException("--api-key (or RULEFORGE_DF_API_KEY) required");
    var baseUrl = opts.BaseUrl ?? "https://documentforge.onrender.com";

    var df = new DfClient(new HttpClient(), baseUrl, apiKey);
    var prefix = opts.Prefix ?? Environment.GetEnvironmentVariable("RULEFORGE_COLLECTION_PREFIX") ?? "";
    var rulesCol = prefix + "rules";
    var rvCol = prefix + "ruleversions";
    var envCol = prefix + "environments";

    Console.WriteLine($"â”â” publish to DocumentForge â”â”");
    Console.WriteLine($"  base url : {baseUrl}");
    Console.WriteLine($"  rule     : {snapshot.Id}@{snapshot.CurrentVersion}");
    Console.WriteLine($"  endpoint : {snapshot.Method} {snapshot.Endpoint}");
    Console.WriteLine($"  env      : {opts.EnvName}");

    // 1. Insert ruleversion. If a version with the same id exists already, replace it.
    var rvId = $"rv-{snapshot.Id}-{snapshot.CurrentVersion}";
    var rvDoc = new
    {
        id = rvId,
        ruleId = snapshot.Id,
        version = snapshot.CurrentVersion,
        snapshot,
        publishedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        publishedBy = "aero-engine-cli",
        bundleId = (string?)null,
    };

    var existingRv = await df.GetByFieldAsync<JsonElement?>(rvCol, "id", rvId);
    if (TryExtractDfId(existingRv, out var prevDfId))
    {
        await df.ReplaceAsync(rvCol, prevDfId, rvDoc);
        Console.WriteLine($"  ruleversion {rvId} replaced (df _id {prevDfId})");
    }
    else
    {
        var dfId = await df.InsertAsync(rvCol, rvDoc);
        Console.WriteLine($"  ruleversion {rvId} inserted (df _id {dfId})");
    }

    // 2. Bump rules.currentVersion (best-effort â€” replace the matching doc).
    var ruleHeader = await df.GetByFieldAsync<JsonElement?>(rulesCol, "id", snapshot.Id);
    if (TryExtractDfId(ruleHeader, out var ruleDfId))
    {
        var bumped = JsonSerializer.Deserialize<Dictionary<string, object?>>(ruleHeader!.Value.GetRawText())!;
        bumped["currentVersion"] = snapshot.CurrentVersion;
        bumped["status"] = "published";
        bumped["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        bumped.Remove("_id");
        await df.ReplaceAsync(rulesCol, ruleDfId, bumped);
        Console.WriteLine($"  rules[{snapshot.Id}].currentVersion â†’ {snapshot.CurrentVersion}");
    }
    else
    {
        Console.WriteLine($"  rules[{snapshot.Id}] not found â€” skipping currentVersion bump");
    }

    // 3. Bind the env. We bypass the by/name lookup because the live DF
    //    instance has a stale unique index; SQL by id (convention env-{name})
    //    with a full-scan fallback is more reliable.
    var env = await FindEnvironmentAsync(df, opts.EnvName, prefix);
    if (TryExtractDfId(env, out var envDfId))
    {
        var envDoc = JsonSerializer.Deserialize<Dictionary<string, object?>>(env!.Value.GetRawText())!;
        var bindings = (envDoc.TryGetValue("ruleBindings", out var rb) && rb is JsonElement je && je.ValueKind == JsonValueKind.Object)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(je.GetRawText()) ?? new()
            : new Dictionary<string, int>();
        bindings[snapshot.Id] = snapshot.CurrentVersion;
        envDoc["ruleBindings"] = bindings;
        envDoc.Remove("_id");
        await df.ReplaceAsync(envCol, envDfId, envDoc);
        Console.WriteLine($"  environments[{opts.EnvName}].ruleBindings[{snapshot.Id}] = {snapshot.CurrentVersion}");
    }
    else
    {
        Console.Error.WriteLine($"  environment '{opts.EnvName}' not found");
        return 6;
    }

    Console.WriteLine("â”â” done â”â”");
    return 0;
}

// â”€â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
static (IRuleSource, IReferenceSetSource) BuildDfSources(RunOpts opts)
{
    var apiKey = opts.DfApiKey
                 ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_API_KEY")
                 ?? throw new InvalidOperationException("--df-api-key (or RULEFORGE_DF_API_KEY) required");
    var baseUrl = opts.DfBaseUrl ?? "https://documentforge.onrender.com";
    var env = opts.Env ?? "staging";
    var df = new DfClient(new HttpClient(), baseUrl, apiKey);
    var prefix = opts.Prefix ?? Environment.GetEnvironmentVariable("RULEFORGE_COLLECTION_PREFIX") ?? "";
    return (new DocumentForgeRuleSource(df, env, prefix), new DocumentForgeReferenceSetSource(df, prefix));
}

static (IRuleSource, IReferenceSetSource) BuildLocalSources(RunOpts opts)
{
    var rules = Path.GetFullPath(opts.FixturesDir);
    var refsDir = Path.GetFullPath(Path.Combine(rules, "..", "refs"));
    return (new LocalFileRuleSource(rules), new LocalFileReferenceSetSource(refsDir));
}

static string ReadInline(string arg) => arg.StartsWith("@") ? File.ReadAllText(arg[1..]) : arg;

static async Task<JsonElement?> FindEnvironmentAsync(DfClient df, string envName, string prefix = "")
{
    var coll = prefix + "environments";
    var byId = await df.QueryAsync<JsonElement>(
        $"SELECT * FROM {coll} WHERE id = 'env-{envName.Replace("'", "''")}'");
    if (byId.Documents.Count > 0) return byId.Documents[0];

    var all = await df.QueryAsync<JsonElement>($"SELECT * FROM {coll}");
    foreach (var doc in all.Documents)
    {
        if (doc.ValueKind != JsonValueKind.Object) continue;
        if (doc.TryGetProperty("name", out var n) &&
            string.Equals(n.GetString(), envName, StringComparison.OrdinalIgnoreCase))
        {
            return doc;
        }
    }
    return null;
}

static bool TryExtractDfId(JsonElement? maybe, out string id)
{
    id = string.Empty;
    if (maybe is null || maybe.Value.ValueKind != JsonValueKind.Object) return false;
    if (!maybe.Value.TryGetProperty("_id", out var idEl)) return false;
    id = idEl.GetString() ?? string.Empty;
    return id.Length > 0;
}

static RunOpts? ParseRunOpts(string[] argv)
{
    string? endpoint = null, request = null, http = null, env = null, dfKey = null, dfBase = null, prefix = null;
    var debug = false; var useDf = false;
    var fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "rules");

    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--endpoint":   endpoint = argv[++i]; break;
            case "--request":    request  = argv[++i]; break;
            case "--http":       http     = argv[++i]; break;
            case "--debug":      debug    = true; break;
            case "--fixtures":   fixtures = argv[++i]; break;
            case "--df":         useDf    = true; break;
            case "--env":        env      = argv[++i]; break;
            case "--df-api-key": dfKey    = argv[++i]; break;
            case "--df-base":    dfBase   = argv[++i]; break;
            case "--prefix":     prefix   = argv[++i]; break;
            case "-h":
            case "--help":       return null;
            default:
                Console.Error.WriteLine($"unknown arg: {argv[i]}");
                return null;
        }
    }
    if (endpoint is null || request is null) return null;
    return new RunOpts(endpoint, request, http, debug, fixtures, useDf, env, dfKey, dfBase, prefix);
}

static PublishOpts? ParsePublishOpts(string[] argv)
{
    string? rule = null, env = null, key = null, baseUrl = null, prefix = null;
    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--rule":     rule = argv[++i]; break;
            case "--env":      env  = argv[++i]; break;
            case "--api-key":  key  = argv[++i]; break;
            case "--df-base":  baseUrl = argv[++i]; break;
            case "--prefix":   prefix = argv[++i]; break;
            case "-h":
            case "--help":     return null;
            default:
                Console.Error.WriteLine($"unknown arg: {argv[i]}");
                return null;
        }
    }
    if (rule is null) return null;
    return new PublishOpts(rule, env ?? "staging", key, baseUrl, prefix);
}

static void PrintRunHelp()
{
    Console.Error.WriteLine("""
        run â€” execute a rule against a request

        Common:
          --endpoint <slug>       Endpoint, e.g. /v1/ancillary/bag-policy   (required)
          --request <json|@file>  Inline JSON or @path/to/file.json         (required)
          --debug                 Include the trace in the response

        Source selection:
          (default)               Local fixtures (--fixtures DIR to override)
          --df                    DocumentForge live source
          --env NAME              DF environment to read bindings from (default: staging)
          --df-api-key KEY        DF API key (or env RULEFORGE_DF_API_KEY)
          --df-base URL           DF base URL (default: https://documentforge.onrender.com)

        Or talk to a running engine via HTTP:
          --http <baseUrl>        Skip in-process execution, POST to this engine
        """);
}

static void PrintPublishHelp()
{
    Console.Error.WriteLine("""
        publish â€” push a rule snapshot to DocumentForge

          --rule <path>   Local rule JSON file (the snapshot)         (required)
          --env <name>    Environment to bind to (default: staging)
          --api-key <k>   DF API key (or env RULEFORGE_DF_API_KEY)
          --df-base <url> DF base URL (default: https://documentforge.onrender.com)

        Effects:
          1. ruleversions: insert (or replace) the snapshot doc.
          2. rules:        bump currentVersion + status=published.
          3. environments: add/update ruleBindings[ruleId] = version.
        """);
}

// â”€â”€â”€ mirror â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
static async Task<int> MirrorVerb(string[] argv)
{
    var opts = ParseMirrorOpts(argv);
    if (opts is null) { PrintMirrorHelp(); return 1; }

    var from = new DfClient(new HttpClient(), opts.FromUrl,
        opts.FromKey ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_API_KEY") ?? "");
    var to   = new DfClient(new HttpClient(), opts.ToUrl, opts.ToKey ?? "");

    Console.WriteLine($"â”â” mirror â”â”");
    Console.WriteLine($"  from : {opts.FromUrl}");
    Console.WriteLine($"  to   : {opts.ToUrl}");
    Console.WriteLine();

    foreach (var collection in opts.Collections)
    {
        var docs = await from.QueryAsync<JsonElement>($"SELECT * FROM {collection}");
        // Idempotent: delete any pre-existing target docs that share a logical
        // id with the source. Avoids duplicate rows on re-runs.
        var existingByLogicalId = new Dictionary<string, string>();
        try
        {
            var existing = await to.QueryAsync<JsonElement>($"SELECT * FROM {collection}");
            foreach (var doc in existing.Documents)
            {
                if (doc.ValueKind != JsonValueKind.Object) continue;
                if (!doc.TryGetProperty("_id", out var dfId)) continue;
                if (!doc.TryGetProperty("id", out var lid)) continue;
                existingByLogicalId[lid.GetString() ?? ""] = dfId.GetString() ?? "";
            }
        }
        catch { /* collection may not exist yet â€” fine */ }

        var inserted = 0; var replaced = 0;
        foreach (var doc in docs.Documents)
        {
            using var stripped = JsonDocument.Parse(doc.GetRawText());
            var withoutId = StripDfId(stripped.RootElement);
            string? logicalId = null;
            if (withoutId.ValueKind == JsonValueKind.Object &&
                withoutId.TryGetProperty("id", out var lid))
                logicalId = lid.GetString();

            try
            {
                if (logicalId is not null && existingByLogicalId.TryGetValue(logicalId, out var dfId))
                {
                    await to.DeleteAsync(collection, dfId);
                    await to.InsertAsync(collection, withoutId);
                    replaced++;
                }
                else
                {
                    await to.InsertAsync(collection, withoutId);
                    inserted++;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  ! {collection}: {e.Message}");
            }
        }
        Console.WriteLine($"  {collection,-22}  read {docs.Count,5}  inserted {inserted,5}  replaced {replaced,5}");
    }
    Console.WriteLine("â”â” done â”â”");
    return 0;
}

static JsonElement StripDfId(JsonElement el)
{
    if (el.ValueKind != JsonValueKind.Object) return el;
    var obj = new JsonObject();
    foreach (var prop in el.EnumerateObject())
    {
        if (prop.Name == "_id") continue;
        obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
    }
    return JsonDocument.Parse(obj.ToJsonString()).RootElement;
}

static MirrorOpts? ParseMirrorOpts(string[] argv)
{
    string? from = null, to = null, fromKey = null, toKey = null, cols = null;
    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--from":      from = argv[++i]; break;
            case "--to":        to   = argv[++i]; break;
            case "--from-key":  fromKey = argv[++i]; break;
            case "--to-key":    toKey   = argv[++i]; break;
            case "--collections": cols = argv[++i]; break;
            case "-h":
            case "--help": return null;
            default:
                Console.Error.WriteLine($"unknown arg: {argv[i]}");
                return null;
        }
    }
    if (from is null || to is null) return null;
    var defaultCols = new[]
    {
        "rules", "ruleversions", "environments",
        "referencesets", "referencesetversions",
        "nodetemplates", "scenarios", "connections",
    };
    return new MirrorOpts(from, fromKey, to, toKey,
        cols?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? defaultCols);
}

static void PrintMirrorHelp()
{
    Console.Error.WriteLine("""
        mirror â€” copy collections from one DocumentForge instance to another

          --from <baseUrl>      Source DF base URL                           (required)
          --to   <baseUrl>      Target DF base URL                           (required)
          --from-key <k>        Source bearer token (or RULEFORGE_DF_API_KEY)
          --to-key   <k>        Target bearer token
          --collections <list>  Comma-separated list (default: aero-relevant set)

        Strips the source DF '_id' from each document and re-POSTs into the
        target. Idempotency is the caller's problem â€” point at a fresh local
        DF for clean mirrors.
        """);
}

// â”€â”€â”€ bench â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
static async Task<int> BenchVerb(string[] argv)
{
    var opts = ParseBenchOpts(argv);
    if (opts is null) { PrintBenchHelp(); return 1; }

    Func<(IRuleSource, IReferenceSetSource)> makeSources = opts.UseDf
        ? () => BuildDfSourcesForBench(opts)
        : () => ((IRuleSource)new LocalFileRuleSource(Path.GetFullPath(opts.FixturesDir)),
                 (IReferenceSetSource)new LocalFileReferenceSetSource(Path.GetFullPath(Path.Combine(opts.FixturesDir, "..", "refs"))));

    var (warmRuleSource, warmRefSource) = makeSources();
    var rule = await warmRuleSource.GetByEndpointAsync(opts.Endpoint, HttpMethodKind.POST)
               ?? throw new InvalidOperationException($"no rule bound to POST {opts.Endpoint}");

    var requestJson = ReadInline(opts.RequestArg);
    JsonElement request;
    using (var doc = JsonDocument.Parse(requestJson))
        request = doc.RootElement.Clone();

    var runner = new RuleRunner();

    Console.WriteLine($"â”â” bench â”â”");
    Console.WriteLine($"  rule         : {rule.Name} (v{rule.CurrentVersion})");
    Console.WriteLine($"  source       : {(opts.UseDf ? "DocumentForge" : "local file")}");
    Console.WriteLine($"  endpoint     : POST {opts.Endpoint}");
    Console.WriteLine($"  iterations   : {opts.Iterations}");
    Console.WriteLine($"  warmup       : {opts.WarmupIterations}");
    Console.WriteLine($"  concurrency  : {opts.Concurrency}");
    Console.WriteLine($"  cold         : {opts.Cold}  (fresh source per request)");
    Console.WriteLine();

    // Sanity run on the warm source so we know the rule is well-formed.
    var sample = await runner.RunAsync(rule, request,
        new RuleRunner.Options(false, warmRuleSource, warmRefSource));
    Console.WriteLine($"  sample run   : decision={sample.Decision}");
    Console.WriteLine();

    // Helper that picks sources per call (cold) or reuses the warmed pair.
    async Task RunOnce()
    {
        IRuleSource s; IReferenceSetSource r; Rule ruleForCall;
        if (opts.Cold)
        {
            (s, r) = makeSources();
            ruleForCall = await s.GetByEndpointAsync(opts.Endpoint, HttpMethodKind.POST)
                          ?? throw new InvalidOperationException("rule disappeared");
        }
        else
        {
            s = warmRuleSource; r = warmRefSource; ruleForCall = rule;
        }
        await runner.RunAsync(ruleForCall, request, new RuleRunner.Options(false, s, r));
    }

    // Warmup
    for (var i = 0; i < opts.WarmupIterations; i++)
        await RunOnce();

    // Hot loop
    var samples = new long[opts.Iterations];
    var totalSw = System.Diagnostics.Stopwatch.StartNew();

    if (opts.Concurrency <= 1)
    {
        var sw = new System.Diagnostics.Stopwatch();
        for (var i = 0; i < opts.Iterations; i++)
        {
            sw.Restart();
            await RunOnce();
            sw.Stop();
            samples[i] = sw.ElapsedTicks;
        }
    }
    else
    {
        var iters = opts.Iterations;
        var c = opts.Concurrency;
        var counter = 0;
        var tasks = Enumerable.Range(0, c).Select(_ => Task.Run(async () =>
        {
            var sw = new System.Diagnostics.Stopwatch();
            while (true)
            {
                var idx = Interlocked.Increment(ref counter) - 1;
                if (idx >= iters) return;
                sw.Restart();
                await RunOnce();
                sw.Stop();
                samples[idx] = sw.ElapsedTicks;
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    totalSw.Stop();

    Array.Sort(samples);
    var ticksPerMs = (double)System.Diagnostics.Stopwatch.Frequency / 1000.0;
    double Ms(long t) => t / ticksPerMs;

    var p50 = samples[samples.Length / 2];
    var p95 = samples[(int)(samples.Length * 0.95)];
    var p99 = samples[(int)(samples.Length * 0.99)];
    var max = samples[^1];
    var mean = samples.Average();
    var totalMs = totalSw.ElapsedMilliseconds;
    var qps = opts.Iterations / (totalMs / 1000.0);

    Console.WriteLine($"  total wall   : {totalMs} ms  ({qps:F0} req/s effective)");
    Console.WriteLine($"  per request  :");
    Console.WriteLine($"    p50        : {Ms(p50):F2} ms");
    Console.WriteLine($"    p95        : {Ms(p95):F2} ms");
    Console.WriteLine($"    p99        : {Ms(p99):F2} ms");
    Console.WriteLine($"    max        : {Ms(max):F2} ms");
    Console.WriteLine($"    mean       : {Ms((long)mean):F2} ms");
    return 0;
}

static (IRuleSource, IReferenceSetSource) BuildDfSourcesForBench(BenchOpts opts)
{
    var apiKey = opts.DfApiKey
                 ?? Environment.GetEnvironmentVariable("RULEFORGE_DF_API_KEY")
                 ?? "";
    var baseUrl = opts.DfBaseUrl ?? "https://documentforge.onrender.com";
    var env = opts.Env ?? "staging";
    var df = new DfClient(new HttpClient(), baseUrl, apiKey);
    var prefix = opts.Prefix ?? Environment.GetEnvironmentVariable("RULEFORGE_COLLECTION_PREFIX") ?? "";
    return (new DocumentForgeRuleSource(df, env, prefix), new DocumentForgeReferenceSetSource(df, prefix));
}

static BenchOpts? ParseBenchOpts(string[] argv)
{
    string? endpoint = null, request = null, env = null, dfKey = null, dfBase = null, prefix = null;
    var iters = 1000; var warmup = 100; var conc = 1; var useDf = false; var cold = false;
    var fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "rules");
    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--endpoint":     endpoint = argv[++i]; break;
            case "--request":      request  = argv[++i]; break;
            case "--iterations":   iters    = int.Parse(argv[++i]); break;
            case "--warmup":       warmup   = int.Parse(argv[++i]); break;
            case "--concurrency":  conc     = int.Parse(argv[++i]); break;
            case "--df":           useDf    = true; break;
            case "--env":          env      = argv[++i]; break;
            case "--df-api-key":   dfKey    = argv[++i]; break;
            case "--df-base":      dfBase   = argv[++i]; break;
            case "--fixtures":     fixtures = argv[++i]; break;
            case "--cold":         cold     = true; break;
            case "--prefix":       prefix   = argv[++i]; break;
            case "-h":
            case "--help":         return null;
            default:
                Console.Error.WriteLine($"unknown arg: {argv[i]}");
                return null;
        }
    }
    if (endpoint is null || request is null) return null;
    return new BenchOpts(endpoint, request, iters, warmup, conc, useDf, env, dfKey, dfBase, fixtures, cold, prefix);
}

static void PrintBenchHelp()
{
    Console.Error.WriteLine("""
        bench â€” run N iterations and report p50/p95/p99 latency

          --endpoint <slug>      Endpoint, e.g. /v1/ancillary/bag-policy   (required)
          --request <json|@file> Inline JSON or @path/to/file.json         (required)
          --iterations N         Hot-loop iterations (default: 1000)
          --warmup N             Warmup iterations before timing (default: 100)
          --concurrency N        Parallel workers (default: 1)

        Source selection (same as `run`):
          (default) local fixtures   --df / --env / --df-api-key / --df-base for DF
        """);
}

// ─── schemas ────────────────────────────────────────────────────────────────
//
// Emits one JSON Schema file per configurable record in the engine — the
// canonical contract every UI builder (tax engine, offer engine, AERO admin)
// can codegen TypeScript types or render forms against.

static int SchemasVerb(string[] argv)
{
    var opts = ParseSchemasOpts(argv);
    if (opts is null) { PrintSchemasHelp(); return 1; }

    Directory.CreateDirectory(opts.OutDir);

    // Mirror the engine's runtime JSON options so emitted schemas reflect the
    // actual on-the-wire shape (camelCase, enum names from JsonStringEnumMemberName,
    // null-omission). JsonSchemaExporter requires a TypeInfoResolver; use the
    // default reflection-based one.
    var jsonOpts = new JsonSerializerOptions(AeroJson.Options)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    var exports = new (string FileName, Type Type, string Title)[]
    {
        ("rule.schema.json",                 typeof(RuleForge.Core.Models.Rule),                "Rule (top-level)"),
        ("envelope.schema.json",             typeof(RuleForge.Core.Models.Envelope),            "Envelope (engine response)"),
        ("string-filter-config.schema.json", typeof(RuleForge.Core.Models.StringFilterConfig),  "String filter config"),
        ("number-filter-config.schema.json", typeof(RuleForge.Core.Models.NumberFilterConfig),  "Number filter config"),
        ("date-filter-config.schema.json",   typeof(RuleForge.Core.Models.DateFilterConfig),    "Date filter config"),
        ("mutator-config.schema.json",       typeof(RuleForge.Core.Models.MutatorConfig),       "Mutator config"),
        ("calc-config.schema.json",          typeof(RuleForge.Core.Models.CalcConfig),          "Calc config"),
        ("iterator-config.schema.json",      typeof(RuleForge.Core.Models.IteratorConfig),      "Iterator config"),
        ("merge-config.schema.json",         typeof(RuleForge.Core.Models.MergeConfig),         "Merge config"),
        ("reference-config.schema.json",     typeof(RuleForge.Core.Graph.ReferenceConfig),      "Reference (multi-row lookup) config"),
        ("api-config.schema.json",           typeof(RuleForge.Core.Models.ApiConfig),           "API (outbound HTTP) node config"),
        ("sub-rule-call.schema.json",        typeof(RuleForge.Core.Models.SubRuleCall),         "Sub-rule call (with optional forEach)"),
    };

    Console.WriteLine($"━━ schemas → {Path.GetFullPath(opts.OutDir)} ━━");
    foreach (var (file, type, title) in exports)
    {
        var schema = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(jsonOpts, type);
        // Stamp a $id and a title onto each schema so consumers can identify it.
        if (schema is JsonObject obj)
        {
            obj["$id"] = $"https://ruleforge-docs.onrender.com/schemas/{file}";
            obj["title"] = title;
        }
        var path = Path.Combine(opts.OutDir, file);
        File.WriteAllText(path,
            schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  {title,-46}  {file}");
    }
    Console.WriteLine($"━━ wrote {exports.Length} schemas ━━");
    return 0;
}

static SchemasOpts? ParseSchemasOpts(string[] argv)
{
    string? outDir = null;
    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--out": outDir = argv[++i]; break;
            case "-h":
            case "--help": return null;
            default:
                Console.Error.WriteLine($"unknown arg: {argv[i]}");
                return null;
        }
    }
    if (outDir is null) return null;
    return new SchemasOpts(outDir);
}

static void PrintSchemasHelp()
{
    Console.Error.WriteLine("""
        schemas — export typed JSON Schemas for every engine config record

          --out <dir>   Output directory (required). One file per type:
                          rule.schema.json
                          envelope.schema.json
                          string-filter-config.schema.json
                          number-filter-config.schema.json
                          date-filter-config.schema.json
                          mutator-config.schema.json
                          calc-config.schema.json
                          iterator-config.schema.json
                          merge-config.schema.json
                          reference-config.schema.json
                          sub-rule-call.schema.json

        Schemas are generated from the live C# types via
        System.Text.Json.Schema.JsonSchemaExporter — they are the contract
        the engine actually validates against. Re-run after model changes
        and commit the output.
        """);
}

internal sealed record RunOpts(
    string Endpoint, string RequestArg, string? HttpBaseUrl, bool Debug,
    string FixturesDir, bool UseDf, string? Env, string? DfApiKey, string? DfBaseUrl,
    string? Prefix = null);
internal sealed record PublishOpts(string RuleFile, string EnvName, string? ApiKey, string? BaseUrl,
    string? Prefix = null);
internal sealed record MirrorOpts(
    string FromUrl, string? FromKey, string ToUrl, string? ToKey,
    IReadOnlyList<string> Collections);
internal sealed record BenchOpts(
    string Endpoint, string RequestArg, int Iterations, int WarmupIterations,
    int Concurrency, bool UseDf, string? Env, string? DfApiKey, string? DfBaseUrl,
    string FixturesDir, bool Cold, string? Prefix = null);
internal sealed record SchemasOpts(string OutDir);
