using System.Diagnostics;
using System.Text.Json;
using RuleForge.Core;
using RuleForge.Core.Graph;
using RuleForge.Core.Loader;
using RuleForge.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace RuleForge.Core.Tests;

/// <summary>
/// Performance regression gate. The thresholds here are intentionally
/// generous (~50× the README's reported steady-state latency) so the
/// suite is robust to CI machine variance — they catch real regressions
/// (10×+ slowdowns) without flaking on slow runners.
/// <para>
/// Reference figures from <c>README.md</c> (single laptop, NVMe):
/// </para>
/// <code>
///   Warm steady-state, 1 worker:    p50=0.07 ms, p95=0.09 ms, p99=0.14 ms
///   Warm steady-state, 16 workers:  p50=0.13 ms, p95=0.23 ms, p99=1.45 ms
/// </code>
/// </summary>
[Trait("Category", "Performance")]
public class PerfRegressionTests
{
    private readonly ITestOutputHelper _output;
    public PerfRegressionTests(ITestOutputHelper output) => _output = output;

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

    private static (Rule Rule, JsonElement Request, IReferenceSetSource Refs) Setup()
    {
        var rule = JsonSerializer.Deserialize<Rule>(
            File.ReadAllText(Path.Combine(FixturesDir(), "rule-bag-policy.v7.json")),
            AeroJson.Options)!;
        var request = JsonDocument.Parse("""
            { "cabin": "Y", "orig": "LHR", "dest": "DXB", "bagPieces": 3, "markup": 0.15, "route": "LHR-DXB" }
            """).RootElement;

        var refsDir = Path.Combine(Path.GetDirectoryName(FixturesDir())!, "refs");
        var refs = new LocalFileReferenceSetSource(refsDir);
        return (rule, request, refs);
    }

    private static (double p50, double p95, double p99, double mean) Stats(double[] samples)
    {
        Array.Sort(samples);
        return (
            samples[(int)(samples.Length * 0.50)],
            samples[(int)(samples.Length * 0.95)],
            samples[Math.Min((int)(samples.Length * 0.99), samples.Length - 1)],
            samples.Average());
    }

    [Fact]
    public async Task Warm_steady_state_p99_under_threshold()
    {
        var (rule, request, refs) = Setup();
        var runner = new RuleRunner();
        var options = new RuleRunner.Options(ReferenceSetSource: refs);

        // Warmup — JIT, refset cache, first allocations.
        for (var i = 0; i < 200; i++)
            await runner.RunAsync(rule, request, options);

        // Measure
        const int n = 1000;
        var samples = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            await runner.RunAsync(rule, request, options);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        var (p50, p95, p99, mean) = Stats(samples);
        _output.WriteLine($"warm steady-state (n={n}):");
        _output.WriteLine($"  p50  = {p50:F3} ms");
        _output.WriteLine($"  p95  = {p95:F3} ms");
        _output.WriteLine($"  p99  = {p99:F3} ms");
        _output.WriteLine($"  mean = {mean:F3} ms");

        // Generous thresholds — README claims p99=0.14ms; threshold is ~30× that.
        // CI runners are noisy; we want to catch 10×+ regressions, not be flaky.
        Assert.True(p50 < 2.0,  $"p50={p50:F3}ms exceeds 2ms threshold (expected sub-ms)");
        Assert.True(p95 < 5.0,  $"p95={p95:F3}ms exceeds 5ms threshold");
        Assert.True(p99 < 10.0, $"p99={p99:F3}ms exceeds 10ms threshold");
    }

    [Fact]
    public async Task Throughput_meets_minimum_floor()
    {
        var (rule, request, refs) = Setup();
        var runner = new RuleRunner();
        var options = new RuleRunner.Options(ReferenceSetSource: refs);

        // Warmup
        for (var i = 0; i < 200; i++)
            await runner.RunAsync(rule, request, options);

        const int n = 5000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            await runner.RunAsync(rule, request, options);
        sw.Stop();

        var rps = n / sw.Elapsed.TotalSeconds;
        _output.WriteLine($"throughput (single worker): {rps:F0} req/s over {n} iterations");

        // README claims ~14k req/s warm single worker. Floor at 1000 req/s
        // — catches any catastrophic regression without flaking on slow CI.
        Assert.True(rps > 1000, $"throughput {rps:F0} req/s below 1000 floor");
    }

    // NOTE: a concurrent (multi-worker) benchmark was tried but it interacted
    // badly with xUnit's default class-level test parallelism — the combined
    // load triggered intermittent process aborts. Run a multi-worker
    // benchmark via the CLI `bench` verb instead (`dotnet run --project
    // src/RuleForge.Cli -- bench --concurrency 8 --iterations 5000`), which
    // owns its process and isn't subject to test-runner parallelism.
}
