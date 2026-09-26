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
/// (20 req/s for 30 min, p99 ≤ 3 s), <c>burst</c> (40 req/s for 1 min, 0 % 5xx) and <c>soak</c> (20 req/s for 1 h, p95 drift ≤ 10 %,
/// no memory growth) through <c>DOCHUB_LOAD_PROFILE</c>; <c>DOCHUB_LOAD_DURATION</c> (seconds) shortens any of them.
/// </summary>
public sealed record LoadProfile(string Name, TimeSpan Duration, double RequestsPerSecond, int ConcurrentUsers, double MaxP99Milliseconds, bool ErrorsAllowed)
{
    /// <summary>A request that takes longer is abandoned and counts as a failure (NFR-L4: no request timeouts).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Soak: the first minute (cold caches, JIT) is outside the drift and memory windows.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromMinutes(1);

    /// <summary>Soak: the drift compares two 10-minute windows, which must not overlap (after the warm-up).</summary>
    public static readonly TimeSpan MinimumSoak = TimeSpan.FromMinutes(30);

    /// <summary>The NFR-L2 volume the full profiles are meant for.</summary>
    public const int FullScaleDocuments = 10_000;

    /// <summary>Profiles measured against the production targets: budgets, the request mix and the data volume are enforced.</summary>
    public bool IsFull => Name is "production" or "soak" or "burst";

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
        var ct = TestContext.Current.CancellationToken;
        using var http = target is null ? data.CreateClient() : new HttpClient { BaseAddress = new Uri(target) };
        http.Timeout = Timeout.InfiniteTimeSpan; // each request has LoadProfile.RequestTimeout
        var catalog = await LoadCatalog.ReadAsync(connection, ct);
        Assert.NotEmpty(catalog.Documents);

        // The full profiles only mean something on the NFR-L2 volume; a smaller data set is a rehearsal and says so.
        var rehearsal = profile.IsFull && catalog.Documents.Count < LoadProfile.FullScaleDocuments;
        Assert.False(rehearsal && Environment.GetEnvironmentVariable("DOCHUB_LOAD_ALLOW_SMALL_DATA") != "1",
            $"Profile {profile.Name} needs the NFR-L2 volume ({LoadProfile.FullScaleDocuments} documents, generator scale 1.0); the database has {catalog.Documents.Count}. " +
            "Set DOCHUB_LOAD_ALLOW_SMALL_DATA=1 for a rehearsal on less data.");
        Assert.False(profile.Name == "soak" && profile.Duration < LoadProfile.MinimumSoak,
            $"The soak needs at least {LoadProfile.MinimumSoak.TotalMinutes} min (two separate 10-minute windows after the warm-up); DOCHUB_LOAD_DURATION is {profile.Duration}.");
        var memory = MemorySampler.FromEnvironment(inProcess: target is null);
        Assert.False(profile.Name == "soak" && !memory.Available,
            "The soak checks the API's memory: run it in process, or set DOCHUB_LOAD_API_PROCESS to the process id of the API under test.");

        var recorder = new LoadRecorder();
        var flows = new LoadFlows(http, catalog, recorder, exports: target is not null, autosaveInterval: TimeSpan.FromSeconds(3), LoadProfile.RequestTimeout);
        var reports = Environment.GetEnvironmentVariable("DOCHUB_LOAD_REPORTS") ?? Path.Combine(AppContext.BaseDirectory, "load-reports");
        Directory.CreateDirectory(reports);
        var findingsBefore = await FindingCountAsync(connection);

        // Ledger reconciliation half way through (T21 acceptance in T19): its duration, its findings, and the requests around it.
        var reconciliation = Task.Run(async () =>
        {
            await Task.Delay(profile.Duration / 2);
            var watch = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/admin/audit/reconcile", UriKind.Relative));
            request.Headers.Add("X-User-Id", LoadFlows.AdminUserId.ToString(CultureInfo.InvariantCulture));
            using var admin = target is null ? data.CreateClient() : new HttpClient { BaseAddress = new Uri(target) };
            admin.Timeout = TimeSpan.FromMinutes(15);
            using var response = await admin.SendAsync(request);
            var run = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (Status: (int)response.StatusCode, Elapsed: watch.Elapsed, Started: recorder.Elapsed - watch.Elapsed,
                Findings: response.IsSuccessStatusCode ? run.GetProperty("newFindings").GetInt32() + run.GetProperty("moduleProblems").GetInt32() : -1);
        });
        using var sampling = new CancellationTokenSource();
        var probe = new ProcedureProbe(connection, catalog);
        var probing = probe.RunAsync(sampling.Token);
        var memorySampling = memory.RunAsync(recorder, TimeSpan.FromSeconds(30), sampling.Token);

        var scenario = Scenario.Create("nfr-l3-mix", async context =>
            {
                try
                {
                    await flows.RunOneAsync(Random.Shared, context.ScenarioCancellationToken);
                    return Response.Ok();
                }
                catch (OperationCanceledException) when (context.ScenarioCancellationToken.IsCancellationRequested)
                {
                    // The run ended during the flow: no new request starts; the ones in flight are still recorded.
                    return Response.Ok();
                }
#pragma warning disable CA1031 // A broken flow is a failure of the run, recorded like a failed request.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    recorder.Add("flow error", 0, 0);
                    return Response.Fail(message: e.Message);
                }
            })
            .WithoutWarmUp()
            .WithLoadSimulations(profile.Simulation());
        var nbomber = NBomberRunner.RegisterScenarios(scenario)
            .WithTestSuite("DocHub")
            .WithTestName($"T19 {profile.Name}")
            .WithReportFolder(reports)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv, ReportFormat.Md)
            .Run();
        var drained = await recorder.DrainAsync(LoadProfile.RequestTimeout + TimeSpan.FromSeconds(10));
        await sampling.CancelAsync();
        await Task.WhenAll(probing, memorySampling);
        var reconciled = await reconciliation;
        var findingsAfter = await FindingCountAsync(connection);

        var stats = recorder.Stats();
        var budgets = LoadBudgets.Evaluate(recorder, probe);
        var mix = recorder.Mix();
        await File.WriteAllTextAsync(Path.Combine(reports, "endpoints.csv"), LoadRecorder.Csv(stats), ct);
        await File.WriteAllTextAsync(Path.Combine(reports, "budgets.csv"), LoadBudgets.Csv(budgets), ct);
        await File.WriteAllTextAsync(Path.Combine(reports, "memory.csv"), memory.Csv(), ct);
        await File.WriteAllTextAsync(Path.Combine(reports, "top-queries.csv"), await TopQueriesAsync(connection), ct);
        var failedFlows = nbomber.ScenarioStats.Sum(s => s.Fail.Request.Count);
        var summary = new StringBuilder(
            $"Profile {profile.Name}{(rehearsal ? " (REHEARSAL: less than the NFR-L2 volume)" : "")}, {profile.Duration}, {catalog.Documents.Count} documents, {recorder.Samples.Count} requests, failed flows {failedFlows}.\n");
        summary.Append(CultureInfo.InvariantCulture, $"Mix: readers {mix[LoadCategory.Reader]:0.0} %, editors {mix[LoadCategory.Editor]:0.0} %, other {mix[LoadCategory.Other]:0.0} % (NFR-L3: 70 / 20 / 10).\n");
        summary.Append(CultureInfo.InvariantCulture, $"Reconciliation {reconciled.Elapsed.TotalSeconds:0.0} s, {reconciled.Findings} new findings, {findingsAfter - findingsBefore} finding rows added, {findingsAfter} in total.\n");
        summary.Append(LoadRecorder.Csv(stats)).Append(LoadBudgets.Csv(budgets));
        TestContext.Current.SendDiagnosticMessage(summary.ToString());
        TestContext.Current.AddAttachment("load-summary", summary.ToString());

        // Nothing is lost: every started request was recorded, and no flow failed outside a request.
        Assert.True(drained, $"{recorder.InFlight} requests never finished.");
        Assert.Equal(0, failedFlows);
        Assert.NotEmpty(stats);
        Assert.All(stats, s =>
        {
            Assert.True(s.ServerErrors == 0, $"{s.Endpoint}: {s.ServerErrors} server errors");
            Assert.True(profile.ErrorsAllowed || s.Failures == 0, $"{s.Endpoint}: {s.Failures} failed requests");
            Assert.True(s.P99 <= profile.MaxP99Milliseconds, $"{s.Endpoint}: p99 {s.P99:0} ms > {profile.MaxP99Milliseconds:0} ms");
        });
        Assert.Equal((200, 0), (reconciled.Status, reconciled.Findings));
        // No finding at all — also none recorded outside this run's reconciliation (e.g. by the deployed API's scheduled run).
        Assert.Equal(0, findingsAfter);
        Assert.True(reconciled.Elapsed < TimeSpan.FromMinutes(10), $"Reconciliation took {reconciled.Elapsed}.");
        // The requests that ran while the reconciliation did stay within the limit too.
        var during = recorder.Stats(s => s.At >= reconciled.Started && s.At <= reconciled.Started + reconciled.Elapsed);
        Assert.All(during, s => Assert.True(s.P99 <= profile.MaxP99Milliseconds, $"{s.Endpoint} during reconciliation: p99 {s.P99:0} ms"));

        if (profile.IsFull)
        {
            // NFR-L3 is a share of the requests; NFR-L5 budgets hold at this load (the small profile reports both).
            Assert.InRange(mix[LoadCategory.Reader], 65, 75);
            Assert.InRange(mix[LoadCategory.Editor], 15, 25);
            Assert.InRange(mix[LoadCategory.Other], 6, 14);
            if (profile.Name != "burst")
            {
                Assert.All(budgets, b => Assert.True(b.Met, $"NFR-L5 {b.Operation}: p95 {b.MeasuredP95:0.0} ms > {b.P95Milliseconds:0} ms ({b.Samples} samples)"));
            }
        }

        if (profile.Name == "soak")
        {
            // p95 of the last 10 minutes at most 10 % above the first 10 minutes after the warm-up; no memory growth trend.
            var firstWindow = (From: LoadProfile.WarmUp, To: LoadProfile.WarmUp + TimeSpan.FromMinutes(10));
            var first = recorder.Samples.Where(s => s.At >= firstWindow.From && s.At < firstWindow.To).Select(s => s.Milliseconds).Order().ToList();
            var last = recorder.Samples.Where(s => s.At > profile.Duration - TimeSpan.FromMinutes(10)).Select(s => s.Milliseconds).Order().ToList();
            Assert.True(LoadRecorder.Percentile(last, 95) <= LoadRecorder.Percentile(first, 95) * 1.1,
                $"p95 drifted by more than 10 %: {LoadRecorder.Percentile(first, 95):0} ms → {LoadRecorder.Percentile(last, 95):0} ms.");
            var growth = memory.GrowthAfter(LoadProfile.WarmUp);
            Assert.True(growth <= 0.10, $"API memory grew by {growth:P0} over the soak (trend of memory.csv).");
        }
    }

    private static async Task<int> FindingCountAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM [audit].[ReconciliationFinding];", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The 10 statements with the highest average elapsed time since the run started (the report's tuning list).</summary>
    internal static async Task<string> TopQueriesAsync(string connectionString, int count = 10)
    {
        const string Sql = """
            SELECT TOP (@count) s.execution_count, s.total_elapsed_time / s.execution_count / 1000.0 AS avg_ms, s.max_elapsed_time / 1000.0 AS max_ms,
                   REPLACE(REPLACE(SUBSTRING(t.text, s.statement_start_offset / 2 + 1, 400), CHAR(13), ' '), CHAR(10), ' ') AS statement
            FROM sys.dm_exec_query_stats AS s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) AS t
            -- sql_text's dbid is NULL for ad hoc and parameterized statements (all of EF Core): the plan's dbid is set for every one.
            CROSS APPLY (SELECT CONVERT(INT, a.value) AS dbid FROM sys.dm_exec_plan_attributes(s.plan_handle) AS a WHERE a.attribute = 'dbid') AS p
            WHERE p.dbid = DB_ID() AND s.execution_count > 1
            ORDER BY avg_ms DESC;
            """;
        var csv = new StringBuilder("executions,avg_ms,max_ms,statement\n");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.AddWithValue("@count", count);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            csv.Append(CultureInfo.InvariantCulture, $"{reader.GetInt64(0)},{reader.GetDecimal(1):0.0},{reader.GetDecimal(2):0.0},\"{reader.GetString(3).Replace("\"", "\"\"", StringComparison.Ordinal).Trim()}\"\n");
        }

        return csv.ToString();
    }
}
