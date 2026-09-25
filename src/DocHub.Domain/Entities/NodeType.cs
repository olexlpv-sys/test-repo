namespace DocHub.Domain.Entities;

public sealed class NodeType
{
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public byte[] RowVersion { get; set; } = [];
}
