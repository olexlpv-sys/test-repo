namespace DocHub.Domain.Entities;

public sealed class VersionSignature
{
    public int Id { get; set; }

    public int DocumentVersionId { get; set; }

    public int UserId { get; set; }

    public DateTime SignedAt { get; set; }

    public byte[] ContentHash { get; set; } = new byte[32];

    public DateTime? WithdrawnAt { get; set; }

    public string? Comment { get; set; }
}
