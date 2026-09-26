using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DocHub.LoadTests;

/// <summary>The NFR-L3 traffic class a request belongs to.</summary>
public enum LoadCategory
{
    Reader,
    Editor,
    Other,
}

/// <summary>One API request of the load run (status 0: no response — network error, timeout, or an error in the flow itself).</summary>
public readonly record struct LoadSample(TimeSpan At, string Endpoint, double Milliseconds, int Status, LoadCategory Category = LoadCategory.Other);

/// <summary>Per-endpoint latency percentiles of a load run.</summary>
public sealed record EndpointStats(string Endpoint, int Requests, int Failures, int ServerErrors, double P50, double P95, double P99);

/// <summary>Records every request of the run (independently of NBomber's own statistics) for the per-endpoint report and drift checks.</summary>
public sealed class LoadRecorder
{
    private readonly ConcurrentQueue<LoadSample> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _inFlight;

    public IReadOnlyCollection<LoadSample> Samples => _samples;

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Requests started and not recorded yet: the run waits for them, so a hung request is never lost.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    public void Start() => Interlocked.Increment(ref _inFlight);

    public void Add(string endpoint, double milliseconds, int status, LoadCategory category = LoadCategory.Other) =>
        _samples.Enqueue(new LoadSample(_clock.Elapsed, endpoint, milliseconds, status, category));

    /// <summary>Records a request started with <see cref="Start"/>.</summary>
    public void Complete(string endpoint, double milliseconds, int status, LoadCategory category)
    {
        Add(endpoint, milliseconds, status, category);
        Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>Waits until every started request is recorded (at most <paramref name="limit"/>); false if some never finished.</summary>
    public async Task<bool> DrainAsync(TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (InFlight > 0 && watch.Elapsed < limit)
        {
            await Task.Delay(100);
        }

        return InFlight == 0;
    }

    /// <summary>Share of the requests per traffic class (NFR-L3: 70 / 20 / 10 %).</summary>
    public IReadOnlyDictionary<LoadCategory, double> Mix()
    {
        var total = Math.Max(1, _samples.Count);
        return Enum.GetValues<LoadCategory>().ToDictionary(c => c, c => 100.0 * _samples.Count(s => s.Category == c) / total);
    }

    public static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile / 100 * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    public IReadOnlyList<EndpointStats> Stats(Func<LoadSample, bool>? filter = null) =>
        _samples.Where(filter ?? (_ => true))
            .GroupBy(s => s.Endpoint)
            .Select(g =>
            {
                var sorted = g.Select(s => s.Milliseconds).Order().ToList();
                return new EndpointStats(g.Key, sorted.Count, g.Count(s => s.Status is < 200 or >= 400), g.Count(s => s.Status >= 500),
                    Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99));
            })
            .OrderBy(s => s.Endpoint, StringComparer.Ordinal)
            .ToList();

    public static string Csv(IEnumerable<EndpointStats> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var csv = new StringBuilder("endpoint,requests,failures,server_errors,p50_ms,p95_ms,p99_ms\n");
        foreach (var s in stats)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{s.Endpoint},{s.Requests},{s.Failures},{s.ServerErrors},{s.P50:0},{s.P95:0},{s.P99:0}\n");
        }

        return csv.ToString();
    }
}
