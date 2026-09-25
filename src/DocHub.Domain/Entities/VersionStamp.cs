namespace DocHub.Domain.Entities;

/// <summary>Per-version change stamp maintained by the database triggers (read-only for the application).</summary>
public sealed class VersionStamp
{
    public int DocumentVersionId { get; set; }

    public long LastChangeLogId { get; set; }

    public DateTime? TamperedAt { get; set; }

    /// <summary>ChangeLog id of the latest node/content change (keys the content-hash cache).</summary>
    public long? ContentChangeLogId { get; set; }

    public DateTime? LastChangedAt { get; set; }
}
