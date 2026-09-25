namespace DocHub.Domain.Entities;

public sealed class Folder
{
    public int Id { get; set; }

    public int? ParentFolderId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public int CreatedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
