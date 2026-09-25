using DocHub.Api.Auth;
using DocHub.Api.Common;
using DocHub.Api.Errors;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Audit;
using DocHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocHub.Api.Endpoints;

/// <summary>Admin → Audit (T21 §4): run ledger reconciliation now, list its findings.</summary>
internal sealed class AuditEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/audit")
            .RequireAuthorization(AuthPolicies.Admin)
            .WithTags("Admin");

        group.MapPost("/reconcile", async Task<Ok<ReconciliationRunResponse>> (
                HttpDbSessionContext session, ILedgerReconciliation reconciliation, IOptions<Audit.ReconciliationOptions> options,
                TimeProvider time, CancellationToken ct) =>
            {
                session.OperationContext = Audit.LedgerReconciliationService.OperationContext;
                var run = await reconciliation.RunAsync(time.GetUtcNow().UtcDateTime - options.Value.Window, ct);
                return TypedResults.Ok(new ReconciliationRunResponse(run.FromUtc, run.ToUtc, run.NewFindings, run.ModuleProblems));
            })
            .WithName("ReconcileLedger")
            .WithSummary("Runs ledger reconciliation and the module-integrity check over the configured window (default: last 48 h).");

        group.MapGet("/findings", async Task<Ok<PagedResult<FindingResponse>>> ([AsParameters] PageRequest paging, DocHubDbContext db, CancellationToken ct) =>
            {
                var query = db.ReconciliationFindings.AsNoTracking();
                var total = await query.CountAsync(ct);
                var items = await query
                    .OrderByDescending(f => f.Id)
                    .Skip(paging.Skip)
                    .Take(paging.PageSize)
                    .Select(f => new FindingResponse(f.Id, f.Kind, f.TableName, f.EntityId, f.DocumentId, f.DocumentVersionId, f.LedgerTransactionId,
                        f.TransactionCommitTime, f.Principal, f.Detail, f.DetectedAt, f.AfterSigning))
                    .ToListAsync(ct);
                return TypedResults.Ok(new PagedResult<FindingResponse>(items, paging.Page, paging.PageSize, total));
            })
            .WithValidation<PageRequest>()
            .WithName("ListAuditFindings")
            .WithSummary("Tamper-evidence findings, newest first.");
    }

    public sealed record ReconciliationRunResponse(DateTime FromUtc, DateTime ToUtc, int NewFindings, int ModuleProblems);

    /// <param name="AfterSigning">Committed at or after the version's ledger signing transaction (flags a Signed version, FR-H5).</param>
    public sealed record FindingResponse(
        long Id,
        string Kind,
        string? TableName,
        int? EntityId,
        int? DocumentId,
        int? DocumentVersionId,
        long? LedgerTransactionId,
        DateTime? TransactionCommitTimeUtc,
        string? Principal,
        string? Detail,
        DateTime DetectedAtUtc,
        bool AfterSigning);
}
