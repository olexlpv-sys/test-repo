namespace DocHub.Database.Tests;

/// <summary>
/// Tagged performance tests run alone, after the parallel tests, so their timings don't compete with other test classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceTestGroup
{
    public const string Name = "Performance";
}
