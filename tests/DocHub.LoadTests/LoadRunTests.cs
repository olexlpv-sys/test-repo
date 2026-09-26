using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocHub.Testing;
using DocHub.Testing.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace DocHub.LoadTests;

/// <summary>
/// A load profile (T19 §3, NFR-L4a): the small-environment profile (2 concurrent users, p99 ≤ 10 s) by default; <c>production</c>
/// (20 req/s for 30 min, p99 ≤ 3 s), <c>burst</c> (40 req/s for 1 min, 0 % 5xx) and <c>soak</c> (20 req/s for 1 h, p95 drift ≤ 10 %)
/// through <c>DOCHUB_LOAD_PROFILE</c>; <c>DOCHUB_LOAD_DURATION</c> (seconds) shortens any of them.
/// </summary>
public sealed record LoadProfile(string Name, TimeSpan Duration, double RequestsPerSecond, int ConcurrentUsers, double MaxP99Milliseconds, bool ErrorsAllowed)
{
    public static LoadProfile FromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable("DOCHUB_LOAD_PROFILE") ?? "small";
        var profile = name switch
        {
            "production" => new LoadProfile(name, TimeSpan.FromMinutes(30), 20, 0, 3000, false),
            "burst" => new LoadProfile(name, TimeSpan.FromMinutes(1), 40, 0, double.MaxValue, false),
            "soak" => new LoadProfile(name, TimeSpan.FromHours(1), 20, 0, 3000, false),
            _ => new LoadProfile("small", TimeSpan.FromSeconds(60), 0, 2, 10_000, false),
        };
        return int.TryParse(Environment.GetEnvironmentVariable("DOCHUB_LOAD_DURATION"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? profile with { Duration = TimeSpan.FromSeconds(seconds) }
            : profile;
    }

    /// <summary>A request rate becomes a flow rate (a flow makes <see cref="LoadFlows.RequestsPerFlow"/> requests on average).</summary>
    public LoadSimulation Simulation() => ConcurrentUsers > 0
        ? NBomber.CSharp.Simulation.KeepConstant(ConcurrentUsers, Duration)
        : NBomber.CSharp.Simulation.Inject((int)Math.Round(RequestsPerSecond / LoadFlows.RequestsPerFlow * 10), TimeSpan.FromSeconds(10), Duration);
}

/// <summary>The in-process target: a generated database (<c>DOCHUB_LOAD_SCALE</c>, default 0.001 = 10 documents).</summary>
public sealed class LoadRunDatabase(SqlServerContainerFixture server) : GeneratedDatabase(server)
{
    /// <summary>Load runs are opt-in (<c>DOCHUB_LOAD=1</c> or a <c>DOCHUB_LOAD_PROFILE</c>): they take minutes.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("DOCHUB_LOAD") == "1" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCHUB_LOAD_PROFILE"));

    protected override double DefaultScale => 0.001;

    public override async ValueTask InitializeAsync()
    {
        // A remote target brings its own data; a skipped run needs none.
        if (Enabled && Environment.GetEnvironmentVariable("DOCHUB_LOAD_TARGET") is null)
        {
            await base.InitializeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Logging:LogLevel:Default", "Error");
    }
}

/// <summary>
/// The NFR-L3 load run (T19 §2–3): the traffic mix against the API — in process on a generated container database, or against a
/// deployed stack with <c>DOCHUB_LOAD_TARGET</c> (base URL) and <c>DOCHUB_LOAD_CONNECTION</c> (its database, generated with
/// tools/DocHub.DataGen). Reports: NBomber HTML/CSV plus <c>endpoints.csv</c> (p50/p95/p99 per endpoint) and <c>top-queries.csv</c>
/// in <c>DOCHUB_LOAD_REPORTS</c> (default <c>load-reports</c> next to the test binaries). Opt in with <c>DOCHUB_LOAD=1</c>.
/// </summary>
[Trait("Category", TestCategories.Load)]
public sealed class LoadRunTests(LoadRunDatabase data) : IClassFixture<LoadRunDatabase>
{
    [Fact]
    public async Task The_traffic_mix_meets_the_profile_thresholds()
    {
        Assert.SkipUnless(LoadRunDatabase.Enabled, "Load runs are opt-in: set DOCHUB_LOAD=1 or DOCHUB_LOAD_PROFILE.");
        var profile = LoadProfile.FromEnvironment();
        var target = Environment.GetEnvironmentVariable("DOCHUB_LOAD_TARGET");
        var connection = target is null ? data.AdminConnectionString : Environment.GetEnvironmentVariable("DOCHUB_LOAD_CONNECTION")
            ?? throw new InvalidOperationException("DOCHUB_LOAD_TARGET needs DOCHUB_LOAD_CONNECTION (the target's database).");
        using var http = target is null ? data.CreateClient() : new HttpClient { BaseAddress = new Uri(target), Timeout = TimeSpan.FromSeconds(30) };
        var catalog = await LoadCatalog.ReadAsync(connection, TestContext.Current.CancellationToken);
        Assert.NotEmpty(catalog.Documents);
        var recorder = new LoadRecorder();
        var flows = new LoadFlows(http, catalog, recorder, exports: target is not null, autosaveInterval: TimeSpan.FromSeconds(3));
        var reports = Environment.GetEnvironmentVariable("DOCHUB_LOAD_REPORTS") ?? Path.Combine(AppContext.BaseDirectory, "load-reports");
        Directory.CreateDirectory(reports);

        // Ledger reconciliation half way through (T21 acceptance in T19): its duration, its findings, and the requests around it.
        var reconciliation = Task.Run(async () =>
        {
            await Task.Delay(profile.Duration / 2);
            var watch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/admin/audit/reconcile", UriKind.Relative));
            request.Headers.Add("X-User-Id", LoadFlows.AdminUserId.ToString(CultureInfo.InvariantCulture));
            using var admin = target is null ? data.CreateClient() : new HttpClient { BaseAddress = new Uri(target), Timeout = TimeSpan.FromMinutes(15) };
            using var response = await admin.SendAsync(request);
            var run = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (Status: (int)response.StatusCode, Elapsed: watch.Elapsed, Started: recorder.Elapsed - watch.Elapsed,
                Findings: response.IsSuccessStatusCode ? run.GetProperty("newFindings").GetInt32() + run.GetProperty("moduleProblems").GetInt32() : -1);
        });

        var scenario = Scenario.Create("nfr-l3-mix", async context =>
            {
                await flows.RunOneAsync(Random.Shared, context.ScenarioCancellationToken);
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(profile.Simulation());
        NBomberRunner.RegisterScenarios(scenario)
            .WithTestSuite("DocHub")
            .WithTestName($"T19 {profile.Name}")
            .WithReportFolder(reports)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv, ReportFormat.Md)
            .Run();
        var reconciled = await reconciliation;

        var stats = recorder.Stats();
        await File.WriteAllTextAsync(Path.Combine(reports, "endpoints.csv"), LoadRecorder.Csv(stats), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(reports, "top-queries.csv"), await TopQueriesAsync(connection), TestContext.Current.CancellationToken);
        var summary = new StringBuilder($"Profile {profile.Name}, {profile.Duration}, {recorder.Samples.Count} requests; reconciliation {reconciled.Elapsed.TotalSeconds:0.0} s, {reconciled.Findings} findings.\n");
        summary.Append(LoadRecorder.Csv(stats));
        TestContext.Current.SendDiagnosticMessage(summary.ToString());
        TestContext.Current.AddAttachment("load-summary", summary.ToString());

        Assert.NotEmpty(stats);
        Assert.All(stats, s =>
        {
            Assert.True(s.ServerErrors == 0, $"{s.Endpoint}: {s.ServerErrors} server errors");
            Assert.True(profile.ErrorsAllowed || s.Failures == 0, $"{s.Endpoint}: {s.Failures} failed requests");
            Assert.True(s.P99 <= profile.MaxP99Milliseconds, $"{s.Endpoint}: p99 {s.P99:0} ms > {profile.MaxP99Milliseconds:0} ms");
        });
        Assert.Equal((200, 0), (reconciled.Status, reconciled.Findings));
        Assert.True(reconciled.Elapsed < TimeSpan.FromMinutes(10), $"Reconciliation took {reconciled.Elapsed}.");
        // The requests that ran while the reconciliation did stay within the limit too.
        var during = recorder.Stats(s => s.At >= reconciled.Started && s.At <= reconciled.Started + reconciled.Elapsed);
        Assert.All(during, s => Assert.True(s.P99 <= profile.MaxP99Milliseconds, $"{s.Endpoint} during reconciliation: p99 {s.P99:0} ms"));

        if (profile.Name == "soak")
        {
            // p95 of the last 10 minutes at most 10 % above the first 10 minutes.
            var first = recorder.Samples.Where(s => s.At < TimeSpan.FromMinutes(10)).Select(s => s.Milliseconds).Order().ToList();
            var last = recorder.Samples.Where(s => s.At > profile.Duration - TimeSpan.FromMinutes(10)).Select(s => s.Milliseconds).Order().ToList();
            Assert.True(LoadRecorder.Percentile(last, 95) <= LoadRecorder.Percentile(first, 95) * 1.1, "p95 drifted by more than 10 %.");
        }
    }

    /// <summary>The 10 statements with the highest average elapsed time since the run started (the report's tuning list).</summary>
    private static async Task<string> TopQueriesAsync(string connectionString)
    {
        const string Sql = """
            SELECT TOP (10) s.execution_count, s.total_elapsed_time / s.execution_count / 1000.0 AS avg_ms, s.max_elapsed_time / 1000.0 AS max_ms,
                   REPLACE(REPLACE(SUBSTRING(t.text, s.statement_start_offset / 2 + 1, 400), CHAR(13), ' '), CHAR(10), ' ') AS statement
            FROM sys.dm_exec_query_stats AS s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS t
            WHERE t.dbid = DB_ID() AND s.execution_count > 1
            ORDER BY avg_ms DESC;
            """;
        var csv = new StringBuilder("executions,avg_ms,max_ms,statement\n");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            csv.Append(CultureInfo.InvariantCulture, $"{reader.GetInt64(0)},{reader.GetDecimal(1):0.0},{reader.GetDecimal(2):0.0},\"{reader.GetString(3).Replace("\"", "\"\"", StringComparison.Ordinal).Trim()}\"\n");
        }

        return csv.ToString();
    }
}
