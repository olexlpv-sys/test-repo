namespace DocHub.Domain.Entities;

public sealed class Document
{
    public int Id { get; set; }

    public int FolderId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int OwnerUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public int? DeletedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public bool IsDeleted => DeletedAt is not null;
}
