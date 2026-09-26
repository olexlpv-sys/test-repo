using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Recorded endpoint → budget (p95, ms).</summary>
    public static readonly IReadOnlyDictionary<string, (string Operation, double Budget)> Endpoints = new Dictionary<string, (string, double)>(StringComparer.Ordinal)
    {
        ["list documents"] = ("document list page", 300),
        ["open document"] = ("open document (header)", 800),
        ["tree"] = ("open document (tree)", 800),
        ["node content"] = ("node content read", 300),
        ["autosave"] = ("autosave", 300),
        ["node history"] = ("history page", 1500),
        ["compare"] = ("compare", 1500),
        ["new draft"] = ("new draft (deep copy)", 2500),
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
/// The API's memory during the run (soak: "no memory growth trend"): the managed heap of the in-process API, or the working set
/// of the external API process given by <c>DOCHUB_LOAD_API_PROCESS</c> (its process id, on this machine).
/// </summary>
public sealed class MemorySampler(Func<long>? source)
{
    private readonly List<(TimeSpan At, long Bytes)> _samples = [];

    public bool Available => source is not null;

    public IReadOnlyList<(TimeSpan At, long Bytes)> Samples => _samples;

    public static MemorySampler FromEnvironment(bool inProcess)
    {
        if (inProcess)
        {
            // The heap size after the last GC: stable enough for a trend, without forcing collections into the measured run.
            return new MemorySampler(() => GC.GetGCMemoryInfo().HeapSizeBytes);
        }

        return int.TryParse(Environment.GetEnvironmentVariable("DOCHUB_LOAD_API_PROCESS"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? new MemorySampler(() =>
            {
                using var process = Process.GetProcessById(pid);
                return process.WorkingSet64;
            })
            : new MemorySampler(null);
    }

    public async Task RunAsync(LoadRecorder recorder, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (source is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _samples.Add((recorder.Elapsed, source()));
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The least-squares growth over the window after <paramref name="from"/>, as a fraction of the mean.</summary>
    public double GrowthAfter(TimeSpan from)
    {
        var points = _samples.Where(s => s.At >= from).Select(s => (X: s.At.TotalSeconds, Y: (double)s.Bytes)).ToList();
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
        var csv = new StringBuilder("elapsed_s,bytes\n");
        foreach (var (at, bytes) in _samples)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{at.TotalSeconds:0},{bytes}\n");
        }

        return csv.ToString();
    }
}
