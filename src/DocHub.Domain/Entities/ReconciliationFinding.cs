namespace DocHub.Domain.Entities;

/// <summary>
/// A tamper-evidence finding of ledger reconciliation or the module-integrity check (T21 §4). Append-only in the database;
/// the application only reads it (findings are written by <c>audit</c> procedures).
/// </summary>
public sealed class ReconciliationFinding
{
    public long Id { get; set; }

    /// <summary><c>TriggerBypass</c>, <c>ForgedAuditRow</c>, <c>StampTampering</c>, <c>LedgerIntegrity</c>, <c>SchemaTampering</c> or <c>ModuleIntegrity</c>.</summary>
    public string Kind { get; set; } = "";

    public string? TableName { get; set; }

    public int? EntityId { get; set; }

    public int? DocumentId { get; set; }

    public int? DocumentVersionId { get; set; }

    public long? LedgerTransactionId { get; set; }

    public DateTime? TransactionCommitTime { get; set; }

    public string? Principal { get; set; }

    public string? Detail { get; set; }

    public DateTime DetectedAt { get; set; }

    /// <summary>The finding's transaction committed at or after the version's ledger signing transaction (FR-H5).</summary>
    public bool AfterSigning { get; set; }
}
