namespace DocHub.Domain.Entities;

public sealed class User
{
    /// <summary>Seeded identity of background jobs (e.g. the derived-content refresher); cannot sign in.</summary>
    public const int SystemUserId = 0;

    public int Id { get; set; }

    public string Login { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;
}
