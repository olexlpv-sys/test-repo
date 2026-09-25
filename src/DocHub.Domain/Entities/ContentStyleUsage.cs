namespace DocHub.Domain.Entities;

/// <summary>Which node content uses which style (derived from ContentJson, maintained by the API).</summary>
public sealed class ContentStyleUsage
{
    public string StyleId { get; set; } = string.Empty;

    public int NodeId { get; set; }
}
