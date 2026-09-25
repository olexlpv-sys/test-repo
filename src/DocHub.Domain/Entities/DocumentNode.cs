namespace DocHub.Domain.Entities;

public sealed class DocumentNode
{
    public int Id { get; set; }

    public int DocumentVersionId { get; set; }

    /// <summary>Stable across versions (ADR-03).</summary>
    public Guid LogicalNodeId { get; set; }

    public int? ParentNodeId { get; set; }

    public int NodeTypeId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public int CreatedByUserId { get; set; }

    public DateTime ModifiedAt { get; set; }

    public int ModifiedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
