namespace DocHub.Domain.Entities;

public sealed class NodeContent
{
    public const string EmptyContentJson = """{"type":"doc","content":[]}""";

    public int NodeId { get; set; }

    public int DocumentVersionId { get; set; }

    public Guid LogicalNodeId { get; set; }

    public byte SchemaVersion { get; set; } = 1;

    /// <summary>Source of truth (docs/content-format.md); the other content columns are derived from it.</summary>
    public string ContentJson { get; set; } = EmptyContentJson;

    public string ContentHtml { get; set; } = string.Empty;

    public string PlainText { get; set; } = string.Empty;

    public byte[] ContentHash { get; set; } = new byte[32];

    public bool DerivedStale { get; set; }

    public DateTime ModifiedAt { get; set; }

    public int ModifiedByUserId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
