namespace DocHub.Api.Auth;

/// <summary><c>Auth</c> configuration section (ADR-06).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary><c>Test</c>: seeded users selected with the <c>X-User-Id</c> header (the only mode until Entra ID is added).</summary>
    public string Mode { get; set; } = AuthModes.Test;

    /// <summary>Explicit opt-in to run test mode in the Production environment (demo environments only).</summary>
    public bool AllowTestModeInProduction { get; set; }
}

public static class AuthModes
{
    public const string Test = "Test";
}
