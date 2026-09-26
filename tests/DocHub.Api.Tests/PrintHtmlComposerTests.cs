using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Export;

namespace DocHub.Api.Tests;

/// <summary>T20: the print HTML composer (pure .NET) — structure, options and HTML-encoding of every field.</summary>
public sealed class PrintHtmlComposerTests
{
    private static readonly PrintHtmlComposer Composer = new(StyleProperties.DefaultFontFamilies);

    private static PrintDocument Document(bool draft = false, bool tampered = false, string title = "Manual") => new(
        title, "General / <b>Policies</b>", "v1", "Alice & Co", new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc), draft, tampered,
        draft ? null : new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        [new PrintSection("1", 1, "Intro <i>", "<p class=\"ds-style-Normal\">raw content</p>"), new PrintSection("1.1", 2, "Scope", "")],
        [new PrintSignature("Carol \"C\" Clark", new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), "<script>alert(1)</script>")],
        ".ds-style-Normal { font-size: 11pt; }</style><script>");

    [Fact]
    public void Every_field_is_encoded_and_only_content_html_is_raw()
    {
        var html = Composer.Compose(Document(title: "<img src=http://internal-host/x>"), new PdfExportOptions());

        Assert.Contains("&lt;img src=http://internal-host/x&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("General / &lt;b&gt;Policies&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Alice &amp; Co", html, StringComparison.Ordinal);
        Assert.Contains("Intro &lt;i&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Carol &quot;C&quot; Clark", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"ds-style-Normal\">raw content</p>", html, StringComparison.Ordinal);
        // The catalog CSS can't close its style element.
        Assert.DoesNotContain("</style><script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_are_numbered_headings_with_anchors_the_toc_links_to()
    {
        var html = Composer.Compose(Document(), new PdfExportOptions(), new Dictionary<string, int> { ["dh-s0"] = 3, ["dh-s1"] = 4 });

        Assert.Contains("<h1 id=\"dh-s0\" class=\"dh-section-title ds-style-Heading1\"><span class=\"dh-section-number\">1</span> Intro &lt;i&gt;</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"dh-s1\" class=\"dh-section-title ds-style-Heading2\"><span class=\"dh-section-number\">1.1</span> Scope</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"#dh-s0\">", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"dh-toc-page\">3</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"dh-toc-page\">4</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_leave_out_title_page_toc_and_signature_page()
    {
        var html = Composer.Compose(Document(), new PdfExportOptions(TitlePage: false, Toc: false, SignaturePage: false));

        Assert.DoesNotContain("dh-title-page\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dh-toc\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("dh-signature-page\"", html, StringComparison.Ordinal);
        Assert.Contains("dh-section-title", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafts_get_the_watermark_and_no_signature_page()
    {
        var draft = Composer.Compose(Document(draft: true), new PdfExportOptions());
        var signed = Composer.Compose(Document(), new PdfExportOptions());

        Assert.Contains("class=\"dh-watermark\"", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("dh-signature-page\"", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"dh-watermark\"", signed, StringComparison.Ordinal);
        Assert.Contains("dh-signature-page\"", signed, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_carries_the_tamper_banner_whatever_the_options_and_encodes_the_title()
    {
        var tampered = Document(tampered: true, title: "A <b> & B");
        var off = new PdfExportOptions(HeaderFooter: false);

        Assert.True(PrintHtmlComposer.HasHeaderFooter(tampered, off));
        Assert.Contains("modified after signing", PrintHtmlComposer.HeaderTemplate(tampered, off), StringComparison.Ordinal);
        Assert.DoesNotContain("A &lt;b&gt;", PrintHtmlComposer.HeaderTemplate(tampered, off), StringComparison.Ordinal);
        Assert.Contains("A &lt;b&gt; &amp; B", PrintHtmlComposer.HeaderTemplate(tampered, new PdfExportOptions()), StringComparison.Ordinal);
        Assert.False(PrintHtmlComposer.HasHeaderFooter(Document(), off));
        Assert.Contains("class=\"pageNumber\"", PrintHtmlComposer.FooterTemplate(new PdfExportOptions()), StringComparison.Ordinal);
        Assert.Contains("class=\"totalPages\"", PrintHtmlComposer.FooterTemplate(new PdfExportOptions()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://fonts.dochub.invalid/LiberationSans-Regular.ttf", "LiberationSans-Regular.ttf")]
    [InlineData("https://fonts.dochub.invalid/LiberationMono-BoldItalic.ttf", "LiberationMono-BoldItalic.ttf")]
    [InlineData("https://fonts.dochub.invalid/../appsettings.json", null)]
    [InlineData("https://fonts.dochub.invalid/LiberationSans-Regular.ttf?x=1", null)]
    [InlineData("https://fonts.dochub.invalid.evil/LiberationSans-Regular.ttf", null)]
    [InlineData("http://internal-host/LiberationSans-Regular.ttf", null)]
    public void Only_bundled_font_files_are_served(string url, string? file)
    {
        Assert.Equal(file is null ? null : Path.Combine(PrintFonts.Directory, file), PrintFonts.FileFor(url));
    }

    [Fact]
    public void Catalog_fonts_are_aliases_of_the_bundled_fonts()
    {
        var css = PrintFonts.FontFaces(["Aptos", "Times New Roman", "Courier New", "Bad\"Font"]);

        Assert.Contains("font-family: \"Aptos\"; src: url(\"https://fonts.dochub.invalid/LiberationSans-Regular.ttf\")", css, StringComparison.Ordinal);
        Assert.Contains("font-family: \"Times New Roman\"; src: url(\"https://fonts.dochub.invalid/LiberationSerif-Bold.ttf\"); font-weight: 700", css, StringComparison.Ordinal);
        Assert.Contains("font-family: \"Courier New\"; src: url(\"https://fonts.dochub.invalid/LiberationMono-Italic.ttf\"); font-weight: 400; font-style: italic", css, StringComparison.Ordinal);
        Assert.DoesNotContain("Bad", css, StringComparison.Ordinal);
        Assert.All(Directory.GetFiles(PrintFonts.Directory, "*.ttf"), f => Assert.NotNull(PrintFonts.FileFor(PrintFonts.BaseUrl + Path.GetFileName(f))));
        Assert.Equal(12, Directory.GetFiles(PrintFonts.Directory, "*.ttf").Length);
    }
}
