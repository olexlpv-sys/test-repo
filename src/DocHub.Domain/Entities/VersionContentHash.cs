namespace DocHub.Domain.Entities;

/// <summary>Cached canonical content hash of a version, valid while ContentChangeLogId equals the version stamp's (T07).</summary>
public sealed class VersionContentHash
{
    public int DocumentVersionId { get; set; }

    public long ContentChangeLogId { get; set; }

    public byte[] ContentHash { get; set; } = new byte[32];

    public DateTime ComputedAt { get; set; }
}
