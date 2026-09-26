using DocHub.Api.Exports;
using DocHub.Infrastructure.Export;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DocHub.Api.Endpoints;

/// <summary>
/// PDF export (T20, FR-E1…E7): asynchronous jobs rendered by <see cref="PdfExportWorker"/>. Every request creates an audited job of
/// its caller (a cache hit is Succeeded at once); status and file are visible only to the requester or an admin who can still
/// view the document.
/// </summary>
internal sealed class ExportEndpoints : IEndpointModule
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/versions/{versionId:int}/exports/pdf", async Task<Accepted<ExportJobStatus>> (
                int versionId, CreatePdfExport? request, ExportService exports, CancellationToken ct) =>
            {
                request ??= new CreatePdfExport();
                var options = new PdfExportOptions(
                    request.PageSize ?? PdfPageSize.A4, request.TitlePage ?? true, request.Toc ?? true, request.HeaderFooter ?? true, request.SignaturePage ?? true);
                var job = await exports.RequestAsync(versionId, request.LogicalNodeId, options, ct);
                return TypedResults.Accepted($"/api/exports/{job.JobId}", job);
            })
            .WithTags("Exports")
            .WithName("CreatePdfExport")
            .WithSummary("Starts a PDF export of a version (or of a node with its subtree); returns the caller's job — at once Succeeded when the file is cached.");

        endpoints.MapGet("/api/exports/{jobId:int}", async Task<Ok<ExportJobStatus>> (int jobId, ExportService exports, CancellationToken ct) =>
                TypedResults.Ok(await exports.StatusAsync(jobId, ct)))
            .WithTags("Exports")
            .WithName("GetExport")
            .WithSummary("Status and progress (0–100) of an export job of the caller.");

        endpoints.MapGet("/api/exports/{jobId:int}/file", async Task<FileStreamHttpResult> (int jobId, ExportService exports, CancellationToken ct) =>
            {
                var (content, fileName) = await exports.FileAsync(jobId, ct);
                return TypedResults.File(content, "application/pdf", fileName);
            })
            .WithTags("Exports")
            .Produces<Stream>(StatusCodes.Status200OK, "application/pdf")
            .WithName("DownloadExport")
            .WithSummary("The PDF of a succeeded export job (attachment); 409 export-not-ready before, 404 once a draft's file has expired.");
    }
}

/// <summary>Options of a PDF export; omitted values default to A4 with every part included.</summary>
public sealed record CreatePdfExport(
    Guid? LogicalNodeId = null, PdfPageSize? PageSize = null, bool? TitlePage = null, bool? Toc = null, bool? HeaderFooter = null, bool? SignaturePage = null);
