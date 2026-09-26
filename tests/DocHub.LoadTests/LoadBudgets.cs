using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DocHub.LoadTests;

/// <summary>An NFR-L5 budget: the p95 at load of an operation.</summary>
public sealed record LoadBudget(string Operation, double P95Milliseconds, double MeasuredP95, int Samples)
{
    public bool Met => Samples > 0 && MeasuredP95 <= P95Milliseconds;
}

/// <summary>
/// NFR-L5: the internal budgets the run is compared with. API operations come from the recorded requests; the two stored
/// procedures are timed on their own by <see cref="ProcedureProbe"/> during the run.
/// </summary>
public static class LoadBudgets
{
    public const string LargeOpen = "open document + tree (2 000 nodes)";
    public const string LargeHistory = "node history (2 000 nodes)";
    public const string LargeCompare = "compare (2 000 nodes)";
    public const string LargeNewDraft = "new draft (2 000 nodes)";

    /// <summary>
    /// Recorded endpoint → NFR-L5 budget (p95, ms). The document-size budgets come from the probe on the largest documents
    /// (<see cref="LoadFlows.LargeDocumentAsync"/>), the others from the traffic mix.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Operation, double Budget)> Endpoints = new Dictionary<string, (string, double)>(StringComparer.Ordinal)
    {
        ["list documents"] = ("document list page", 300),
        ["node content"] = ("node content read", 300),
        ["autosave"] = ("autosave", 300),
        [LargeOpen] = ("open document (header + tree, 2 000 nodes)", 800),
        [LargeHistory] = ("history page (2 000 nodes)", 1500),
        [LargeCompare] = ("compare (2 000 nodes)", 1500),
        [LargeNewDraft] = ("new draft (deep copy, 2 000 nodes)", 2500),
    };

    public const double CheckPermission = 20;
    public const double ListDocuments = 300;

    public static IReadOnlyList<LoadBudget> Evaluate(LoadRecorder recorder, ProcedureProbe probe)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(probe);
        var budgets = Endpoints.Select(e =>
        {
            var sorted = recorder.Samples.Where(s => s.Endpoint == e.Key && s.Status is >= 200 and < 400).Select(s => s.Milliseconds).Order().ToList();
            return new LoadBudget($"{e.Value.Operation} [{e.Key}]", e.Value.Budget, LoadRecorder.Percentile(sorted, 95), sorted.Count);
        }).ToList();
        budgets.Add(new LoadBudget("permission check [usp_CheckPermission]", CheckPermission, probe.P95("usp_CheckPermission"), probe.Count("usp_CheckPermission")));
        budgets.Add(new LoadBudget("document list page [usp_ListDocuments]", ListDocuments, probe.P95("usp_ListDocuments"), probe.Count("usp_ListDocuments")));
        return budgets;
    }

    public static string Csv(IEnumerable<LoadBudget> budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        var csv = new StringBuilder("operation,budget_p95_ms,measured_p95_ms,samples,met\n");
        foreach (var b in budgets)
        {
            csv.Append(CultureInfo.InvariantCulture, $"\"{b.Operation}\",{b.P95Milliseconds:0},{b.MeasuredP95:0.0},{b.Samples},{(b.Met ? "yes" : "no")}\n");
        }

        return csv.ToString();
    }
}

/// <summary>
/// Times <c>usp_CheckPermission</c> and <c>usp_ListDocuments</c> directly against the database while the load runs (one call of
/// each per second, with generated users and documents): their NFR-L5 budgets are server-side, below the API.
/// </summary>
public sealed class ProcedureProbe(string connectionString, LoadCatalog catalog)
{
    private readonly System.Collections.Concurrent.ConcurrentBag<(string Procedure, double Milliseconds)> _samples = [];

    public int Count(string procedure) => _samples.Count(s => s.Procedure == procedure);

    public double P95(string procedure) =>
        LoadRecorder.Percentile(_samples.Where(s => s.Procedure == procedure).Select(s => s.Milliseconds).Order().ToList(), 95);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var random = new Random(19);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var document = catalog.Documents[random.Next(catalog.Documents.Count)];
                var reader = catalog.Readers[random.Next(catalog.Readers.Count)];
                await TimeAsync(connection, "usp_CheckPermission", "EXEC app.usp_CheckPermission @DocumentId = @d, @UserId = @u, @Action = 'View';",
                    [new("@d", document.Id), new("@u", reader)], cancellationToken);
                await TimeAsync(connection, "usp_ListDocuments", "EXEC app.usp_ListDocuments @FolderId = @f, @UserId = @u, @Search = N'document', @PageSize = 20;",
                    [new("@f", document.FolderId), new("@u", reader)], cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TimeAsync(SqlConnection connection, string procedure, string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var watch = Stopwatch.StartNew();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(cancellationToken));
        }

        _samples.Add((procedure, watch.Elapsed.TotalMilliseconds));
    }
}

/// <summary>
/// The API's memory during the run (soak: "no memory growth trend in the API"), sampled through <c>GET /api/admin/runtime</c>:
/// each call answers for one API instance (behind a load balancer, a random one), so every instance gets its own trend.
/// </summary>
public sealed class MemorySampler(Func<CancellationToken, Task<(string Instance, long Bytes)>> source)
{
    private readonly List<(TimeSpan At, string Instance, long Bytes)> _samples = [];

    public IReadOnlyList<(TimeSpan At, string Instance, long Bytes)> Samples => _samples;

    /// <summary>Samples the managed heap of the answering instance (admin call).</summary>
    public static MemorySampler ForApi(HttpClient http) => new(async ct =>
    {
        ArgumentNullException.ThrowIfNull(http);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/admin/runtime", UriKind.Relative));
        request.Headers.Add("X-User-Id", LoadFlows.AdminUserId.ToString(CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var sample = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (sample.GetProperty("instance").GetString()!, sample.GetProperty("managedHeapBytes").GetInt64());
    });

    public async Task RunAsync(LoadRecorder recorder, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (instance, bytes) = await source(cancellationToken);
                _samples.Add((recorder.Elapsed, instance, bytes));
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The largest least-squares growth of any instance over the window after <paramref name="from"/>, as a fraction of its mean.</summary>
    public double GrowthAfter(TimeSpan from) =>
        _samples.Where(s => s.At >= from).GroupBy(s => s.Instance).Select(g => Growth([.. g.Select(s => (s.At.TotalSeconds, (double)s.Bytes))])).DefaultIfEmpty(0).Max();

    /// <summary>Instances with enough samples after <paramref name="from"/> for a trend.</summary>
    public int InstancesWithTrend(TimeSpan from) => _samples.Where(s => s.At >= from).GroupBy(s => s.Instance).Count(g => g.Count() >= 3);

    private static double Growth(List<(double X, double Y)> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        var (meanX, meanY) = (points.Average(p => p.X), points.Average(p => p.Y));
        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return slope * (points[^1].X - points[0].X) / meanY;
    }

    public string Csv()
    {
        var csv = new StringBuilder("elapsed_s,instance,managed_heap_bytes\n");
        foreach (var (at, instance, bytes) in _samples)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{at.TotalSeconds:0},{instance},{bytes}\n");
        }

        return csv.ToString();
    }
}
