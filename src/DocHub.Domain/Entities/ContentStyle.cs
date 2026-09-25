namespace DocHub.Domain.Entities;

public sealed class ContentStyle
{
    public int Id { get; set; }

    /// <summary>Word-compatible style id, e.g. <c>Heading1</c>.</summary>
    public string StyleId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ContentStyleKind Kind { get; set; }

    public string? BasedOnStyleId { get; set; }

    public string PropertiesJson { get; set; } = "{}";

    public bool IsBuiltIn { get; set; }

    public bool IsActive { get; set; } = true;

    public byte[] RowVersion { get; set; } = [];
}
