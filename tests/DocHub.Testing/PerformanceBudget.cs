using System.Globalization;

namespace DocHub.Testing;

/// <summary>
/// Single-request performance budgets of tagged tests: the NFR target × <c>DOCHUB_PERF_FACTOR</c>. The default 1.5 is the
/// current (small, shared) environment's profile (decisions log Q19, NFR-L4a); set 1.0 on a production-sized environment.
/// </summary>
public static class PerformanceBudget
{
    public static double Factor { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("DOCHUB_PERF_FACTOR"), NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor > 0 ? factor : 1.5;

    public static TimeSpan For(TimeSpan target) => target * Factor;
}
