namespace DocHub.Api.Audit;

/// <summary><c>Audit:Reconciliation</c> configuration (T21 §4, scheduling).</summary>
public sealed class ReconciliationOptions
{
    public const string SectionName = "Audit:Reconciliation";

    /// <summary>Runs the nightly reconciliation (the admin endpoint works either way).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time of day (UTC) of the nightly run.</summary>
    public TimeOnly RunAtUtc { get; set; } = new(2, 0);

    /// <summary>Reconciled window, ending now. Longer than a day so consecutive runs overlap (reconciliation is idempotent).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromHours(48);
}
