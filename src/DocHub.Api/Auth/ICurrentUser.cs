namespace DocHub.Api.Auth;

/// <summary>The caller of the current request — the only way endpoints learn who is calling (ADR-06).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The user id; throws when the request is not authenticated.</summary>
    int UserId { get; }

    bool IsAdmin { get; }
}
