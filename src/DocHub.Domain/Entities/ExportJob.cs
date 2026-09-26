namespace DocHub.Domain.Entities;

/// <summary>One PDF export request of one user (T20, FR-E5/E7) — cache hits get their own row too.</summary>
public sealed class ExportJob
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public int DocumentVersionId { get; set; }

    /// <summary><c>null</c> = the whole document; else the root of the exported subtree.</summary>
    public Guid? LogicalNodeId { get; set; }

    public string OptionsJson { get; set; } = "{}";

    public ExportStatus Status { get; set; }

    /// <summary>0–100, advanced by the worker per rendering stage.</summary>
    public byte Progress { get; set; }

    public int RequestedByUserId { get; set; }

    public DateTime RequestedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? Error { get; set; }

    public string? BlobPath { get; set; }

    public string? FileName { get; set; }

    public long? FileSize { get; set; }

    /// <summary>SHA-256 hex of everything the PDF depends on; equal keys = identical files.</summary>
    public string CacheKey { get; set; } = string.Empty;

    public byte[] RowVersion { get; set; } = [];
}
