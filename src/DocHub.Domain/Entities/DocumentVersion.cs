namespace DocHub.Domain.Entities;

public sealed class DocumentVersion
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public VersionStatus Status { get; set; } = VersionStatus.Draft;

    public int? VersionNumber { get; set; }

    public int? BasedOnVersionId { get; set; }

    public DateTime CreatedAt { get; set; }

    public int CreatedByUserId { get; set; }

    public DateTime? SignedAt { get; set; }

    public byte[]? SignedContentHash { get; set; }

    public bool IsCurrent { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
