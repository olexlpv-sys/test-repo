namespace DocHub.Infrastructure.Export;

public enum PdfPageSize
{
    A4,
    Letter,
}

/// <summary>The options of a PDF export (FR-E3); they are part of the cache key.</summary>
public sealed record PdfExportOptions(
    PdfPageSize PageSize = PdfPageSize.A4, bool TitlePage = true, bool Toc = true, bool HeaderFooter = true, bool SignaturePage = true);

/// <summary>A section of the print document: a node with its tree number, depth (1 = top level) and T09-rendered content HTML.</summary>
public sealed record PrintSection(string Number, int Level, string Title, string ContentHtml);

/// <summary>A valid signature of a signed version (signature page).</summary>
public sealed record PrintSignature(string Approver, DateTime SignedAtUtc, string? Note);

/// <summary>Everything the composer writes. All strings except <see cref="PrintSection.ContentHtml"/> and <see cref="StyleCss"/> are plain text.</summary>
public sealed record PrintDocument(
    string Title,
    string FolderPath,
    string VersionLabel,
    string Owner,
    DateTime GeneratedAtUtc,
    bool IsDraft,
    bool ModifiedAfterSigning,
    DateTime? SignedAtUtc,
    IReadOnlyList<PrintSection> Sections,
    IReadOnlyList<PrintSignature> Signatures,
    string StyleCss);
