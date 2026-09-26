using System.Globalization;
using System.Net;
using System.Text;

namespace DocHub.Infrastructure.Export;

/// <summary>
/// Composes the print HTML of a document version (T20 step 2): title page, table of contents, numbered sections, signature
/// page, the DRAFT watermark and the print stylesheet. Every field is HTML-encoded; only the T09-rendered content HTML and the
/// generated style catalog CSS are inserted as they are. The page numbers of the table of contents come from a previous render
/// (<see cref="Compose"/> with <c>pageNumbers</c>); without them the entries keep the same layout with an empty number.
/// </summary>
public sealed class PrintHtmlComposer(IEnumerable<string> catalogFontFamilies)
{
    private readonly string _fontFaces = PrintFonts.FontFaces(catalogFontFamilies);

    /// <summary>The anchor (<c>id</c>) of the section at <paramref name="index"/>; the TOC links to it.</summary>
    public static string Anchor(int index) => string.Create(CultureInfo.InvariantCulture, $"dh-s{index}");

    public string Compose(PrintDocument document, PdfExportOptions options, IReadOnlyDictionary<string, int>? pageNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        var html = new StringBuilder(4096 + document.Sections.Sum(s => s.ContentHtml.Length + s.Title.Length + 160));
        html.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(E(document.Title)).Append("</title>\n")
            .Append("<style>\n").Append(_fontFaces).Append("</style>\n")
            .Append("<style>\n").Append(RawCss(document.StyleCss)).Append("</style>\n")
            .Append("<style>\n").Append(PrintCss).Append("</style>\n")
            .Append("</head>\n<body class=\"dh-pdf\">\n");
        if (document.IsDraft)
        {
            // A fixed element repeats on every printed page.
            html.Append("<div class=\"dh-watermark\" aria-hidden=\"true\">DRAFT</div>\n");
        }

        if (options.TitlePage)
        {
            AppendTitlePage(html, document);
        }

        if (options.Toc && document.Sections.Count > 0)
        {
            AppendToc(html, document, pageNumbers);
        }

        html.Append("<main>\n");
        for (var i = 0; i < document.Sections.Count; i++)
        {
            var section = document.Sections[i];
            var level = Math.Clamp(section.Level, 1, 6);
            html.Append("<section class=\"dh-section\">")
                .Append("<h").Append(level).Append(" id=\"").Append(Anchor(i)).Append("\" class=\"dh-section-title ds-style-Heading").Append(level).Append("\">")
                .Append("<span class=\"dh-section-number\">").Append(E(section.Number)).Append("</span> ").Append(E(section.Title))
                .Append("</h").Append(level).Append(">\n")
                .Append("<div class=\"dh-section-text\">").Append(section.ContentHtml).Append("</div></section>\n");
        }

        html.Append("</main>\n");
        if (options.SignaturePage && !document.IsDraft && document.Signatures.Count > 0)
        {
            AppendSignatures(html, document);
        }

        return html.Append("</body></html>\n").ToString();
    }

    /// <summary>
    /// Chromium's header template: the tampering banner (FR-E4, on every page whatever the options) and, with the header/footer
    /// option, the document title and version label.
    /// </summary>
    public static string HeaderTemplate(PrintDocument document, PdfExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        var html = new StringBuilder("<div style=\"").Append(TemplateStyle).Append("\">");
        if (document.ModifiedAfterSigning)
        {
            html.Append("<div style=\"background:#FDECEA;color:#8A1C1C;border:1px solid #E0A5A5;padding:2px 6px;margin-bottom:4px;font-weight:700;\">")
                .Append("Warning: this signed version was modified after signing.</div>");
        }

        if (options.HeaderFooter)
        {
            html.Append("<div style=\"display:flex;justify-content:space-between;gap:12px;\"><span>").Append(E(document.Title))
                .Append("</span><span>").Append(E(document.VersionLabel)).Append("</span></div>");
        }

        return html.Append("</div>").ToString();
    }

    /// <summary>Chromium's footer template: "Page X of Y" with the header/footer option, else empty.</summary>
    public static string FooterTemplate(PdfExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.HeaderFooter
            ? $"<div style=\"{TemplateStyle}text-align:center;\">Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></div>"
            // Chromium prints its own default footer for an empty template.
            : "<span></span>";
    }

    /// <summary>Whether the render needs Chromium's header/footer area (the option, or the tampering banner).</summary>
    public static bool HasHeaderFooter(PrintDocument document, PdfExportOptions options) =>
        options.HeaderFooter || document.ModifiedAfterSigning;

    private const string TemplateStyle =
        "font-family:'Liberation Sans',sans-serif;font-size:8pt;color:#444;width:100%;padding:0 2.5cm;box-sizing:border-box;";

    private static void AppendTitlePage(StringBuilder html, PrintDocument document)
    {
        html.Append("<section class=\"dh-title-page\">\n<div class=\"dh-doc-title ds-style-Title\">").Append(E(document.Title)).Append("</div>\n<dl class=\"dh-doc-meta\">");
        Meta(html, "Folder", document.FolderPath);
        Meta(html, "Version", document.VersionLabel);
        Meta(html, "Owner", document.Owner);
        if (document.SignedAtUtc is { } signedAt)
        {
            Meta(html, "Signed", Utc(signedAt));
        }

        Meta(html, "Date", Utc(document.GeneratedAtUtc));
        html.Append("</dl>\n</section>\n");
    }

    private static void Meta(StringBuilder html, string term, string value) =>
        html.Append("<dt>").Append(term).Append("</dt><dd>").Append(E(value)).Append("</dd>");

    private static void AppendToc(StringBuilder html, PrintDocument document, IReadOnlyDictionary<string, int>? pageNumbers)
    {
        html.Append("<nav class=\"dh-toc\" aria-label=\"Contents\">\n<div class=\"dh-toc-heading\">Contents</div>\n<ol class=\"dh-toc-list\">\n");
        for (var i = 0; i < document.Sections.Count; i++)
        {
            var section = document.Sections[i];
            var anchor = Anchor(i);
            var page = pageNumbers is not null && pageNumbers.TryGetValue(anchor, out var p) ? p.ToString(CultureInfo.InvariantCulture) : "";
            html.Append("<li class=\"dh-toc-entry\" style=\"padding-left:").Append((Math.Clamp(section.Level, 1, 12) - 1) * 14).Append("pt\">")
                .Append("<a href=\"#").Append(anchor).Append("\"><span class=\"dh-toc-number\">").Append(E(section.Number)).Append("</span>")
                .Append("<span class=\"dh-toc-title\">").Append(E(section.Title)).Append("</span><span class=\"dh-toc-leader\"></span>")
                .Append("<span class=\"dh-toc-page\">").Append(page).Append("</span></a></li>\n");
        }

        html.Append("</ol>\n</nav>\n");
    }

    private static void AppendSignatures(StringBuilder html, PrintDocument document)
    {
        html.Append("<section class=\"dh-signature-page\">\n<div class=\"dh-signature-heading ds-style-Heading1\">Signatures</div>\n")
            .Append("<table class=\"dh-signatures\"><thead><tr><th>Approver</th><th>Signed at</th><th>Note</th></tr></thead><tbody>\n");
        foreach (var signature in document.Signatures)
        {
            html.Append("<tr><td>").Append(E(signature.Approver)).Append("</td><td>").Append(E(Utc(signature.SignedAtUtc)))
                .Append("</td><td>").Append(E(signature.Note ?? "")).Append("</td></tr>\n");
        }

        html.Append("</tbody></table>\n</section>\n");
    }

    private static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>The catalog CSS is generated from validated values; this only keeps it from ever closing its style element.</summary>
    private static string RawCss(string css) => css.Replace("</", "<\\/", StringComparison.Ordinal);

    private const string PrintCss = """
        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body.dh-pdf { margin: 0; font-family: "Aptos", "Liberation Sans", sans-serif; font-size: 11pt; line-height: 1.35; color: #000; }
        .dh-watermark { position: fixed; top: 45%; left: 0; right: 0; text-align: center; transform: rotate(-45deg); font-size: 120pt;
          font-weight: 700; color: rgba(190, 30, 30, 0.13); z-index: 1000; pointer-events: none; }
        .dh-title-page { break-after: page; padding-top: 30%; }
        .dh-doc-title { font-size: 28pt; margin-bottom: 24pt; }
        .dh-doc-meta { display: grid; grid-template-columns: max-content 1fr; gap: 4pt 16pt; font-size: 11pt; }
        .dh-doc-meta dt { color: #555; }
        .dh-doc-meta dd { margin: 0; }
        .dh-toc { break-after: page; }
        .dh-toc-heading { font-size: 18pt; margin-bottom: 12pt; }
        .dh-toc-list { list-style: none; margin: 0; padding: 0; }
        .dh-toc-entry { margin: 0 0 3pt; break-inside: avoid; }
        .dh-toc-entry a { display: flex; align-items: baseline; color: inherit; text-decoration: none; }
        .dh-toc-number { min-width: 3.2em; padding-right: 6pt; }
        .dh-toc-leader { flex: 1; border-bottom: 1px dotted #888; margin: 0 4pt; min-width: 12pt; }
        .dh-toc-page { min-width: 3em; text-align: right; }
        .dh-section-title { break-after: avoid; }
        .dh-section-number { margin-right: 6px; }
        .dh-section-text p { margin: 0 0 8pt; }
        .dh-section-text table { border-collapse: collapse; width: 100%; margin: 6pt 0; }
        .dh-section-text td, .dh-section-text th { border: 1px solid #bfc5cc; padding: 3pt 5pt; vertical-align: top; }
        .dh-section-text tr { break-inside: avoid; }
        .dh-signature-page { break-before: page; }
        .dh-signatures { border-collapse: collapse; width: 100%; }
        .dh-signatures th, .dh-signatures td { border: 1px solid #bfc5cc; padding: 4pt 6pt; text-align: left; vertical-align: top; }
        """;
}
