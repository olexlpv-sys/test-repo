namespace DocHub.Infrastructure.Audit;

/// <summary>Expected hashes of the audit modules (rule 6): the generated <see cref="AuditModuleHashes"/>; replaceable in tests.</summary>
public interface IAuditModuleHashes
{
    IReadOnlyDictionary<string, string> Hashes { get; }
}

internal sealed class GeneratedAuditModuleHashes : IAuditModuleHashes
{
    public IReadOnlyDictionary<string, string> Hashes => AuditModuleHashes.Expected;
}
