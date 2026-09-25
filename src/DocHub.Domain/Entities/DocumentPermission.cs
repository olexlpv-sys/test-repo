namespace DocHub.Domain.Entities;

public sealed class DocumentPermission
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public int UserId { get; set; }

    public DocumentRole Role { get; set; }

    /// <summary><c>null</c> = whole document; a node scope is only allowed for editors.</summary>
    public Guid? LogicalNodeId { get; set; }

    public DateTime GrantedAt { get; set; }

    public int GrantedByUserId { get; set; }
}
