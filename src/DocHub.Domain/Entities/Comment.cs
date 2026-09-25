namespace DocHub.Domain.Entities;

public sealed class Comment
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public int DocumentVersionId { get; set; }

    /// <summary><c>null</c> = comment on the whole document.</summary>
    public Guid? LogicalNodeId { get; set; }

    public int? ParentCommentId { get; set; }

    public int AuthorUserId { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? EditedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? ResolvedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
