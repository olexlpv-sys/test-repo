namespace DocHub.DataGen;

/// <summary>
/// What to generate. <see cref="Scale"/> 1.0 is the NFR-L2 volume (50 500 users, 100 folders, 10 000 documents with 5 versions of
/// ~300 nodes, 10 grants per document, 20 comments per version); 0.1 is the nightly CI size.
/// </summary>
public sealed record DataGenOptions
{
    public double Scale { get; init; } = 0.1;

    /// <summary>The same seed on the same (fresh) database gives the same data.</summary>
    public int Seed { get; init; } = 42;

    public int VersionsPerDocument { get; init; } = 5;

    public int GrantsPerDocument { get; init; } = 10;

    public int CommentsPerVersion { get; init; } = 20;

    /// <summary>Average nodes per document (log-normal, 1–<see cref="MaxNodes"/>; every 500th document has the maximum).</summary>
    public int AverageNodes { get; init; } = 300;

    public int MaxNodes { get; init; } = 2000;

    public int MaxDepth { get; init; } = 15;

    /// <summary>Chunks loaded at the same time (each keeps about one SQL Server core busy).</summary>
    public int Parallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>Documents per bulk-load transaction.</summary>
    public int ChunkDocuments { get; init; } = 10;

    /// <summary>Creation dates are spread over this many days before now (history queries need a spread).</summary>
    public int HistoryDays { get; init; } = 730;

    public int Users => Math.Max(20, (int)Math.Round(50_500 * Scale));

    /// <summary>Owners, editors and approvers (the other users are readers).</summary>
    public int Editors => Math.Max(GrantsPerDocument + 2, (int)Math.Round(500 * Scale));

    public int Folders => Math.Max(3, (int)Math.Round(100 * Scale));

    public int Documents => Math.Max(1, (int)Math.Round(10_000 * Scale));
}
