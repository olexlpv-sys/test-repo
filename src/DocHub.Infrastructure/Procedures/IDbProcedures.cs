namespace DocHub.Infrastructure.Procedures;

/// <summary>
/// Typed access to the database's stored procedures (ADR-09): one method per procedure, always parameterized, executed on
/// the DbContext connection so the session context (audit) applies. Plain CRUD never goes through here (FR-D1).
/// </summary>
public interface IDbProcedures
{
    /// <summary><c>app.usp_Ping</c> — establishes the pattern: a row from the session context plus a second result set.</summary>
    Task<PingResult> PingAsync(CancellationToken cancellationToken);
}

public sealed record PingResult(int? UserId, string? CorrelationId, string Source, DateTime ServerTimeUtc);
