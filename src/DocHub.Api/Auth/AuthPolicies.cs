namespace DocHub.Api.Auth;

/// <summary>Named authorization policies (the fallback policy requires an authenticated user).</summary>
public static class AuthPolicies
{
    /// <summary>Administrators (<c>User.IsAdmin</c>); others get <c>403 forbidden</c>.</summary>
    public const string Admin = "Admin";
}
