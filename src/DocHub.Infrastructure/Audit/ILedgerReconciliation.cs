namespace DocHub.Infrastructure.Audit;

/// <summary>Tamper-evidence checks (T21 §4): the module-integrity check (rule 6), then <c>audit.usp_ReconcileLedger</c> (rules 1–5).</summary>
public interface ILedgerReconciliation
{
    /// <summary>Runs both checks over ledger transactions committed from <paramref name="fromUtc"/> until now.</summary>
    Task<ReconciliationRun> RunAsync(DateTime fromUtc, CancellationToken cancellationToken);
}

/// <param name="NewFindings">Findings recorded by the reconciliation procedure in this run.</param>
/// <param name="ModuleProblems">Audit modules whose definition didn't match (each recorded once as a finding).</param>
public sealed record ReconciliationRun(DateTime FromUtc, DateTime ToUtc, int NewFindings, int ModuleProblems);
