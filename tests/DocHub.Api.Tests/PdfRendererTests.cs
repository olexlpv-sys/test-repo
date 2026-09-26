using System.Runtime.Versioning;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Domain.Content;
using DocHub.Infrastructure.Content;
using DocHub.Infrastructure.Export;
using DocHub.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace DocHub.Api.Tests;

/// <summary>
/// T20: the Chromium renderer on its own — the two-pass table of contents, the request interception (the render makes no network
/// request) and visual regression of the T09 content fixtures against approved page snapshots (<c>UPDATE_PDF_SNAPSHOTS=1</c>
/// writes new snapshots into <c>tests/DocHub.Api.Tests/PdfSnapshots</c>; review and commit them).
/// </summary>
public sealed class PdfRendererTests(PdfRendererTests.Chromium chromium) : IClassFixture<PdfRendererTests.Chromium>
{
    public sealed class Chromium : IAsyncDisposable
    {
        public ChromiumPdfRenderer Renderer { get; } = new(
            Options.Create(new PdfRendererOptions { ChromiumExecutablePath = ExportApiFactory.ChromiumPath, Sandbox = false }),
            new PrintHtmlComposer(StyleProperties.DefaultFontFamilies),
            NullLogger<ChromiumPdfRenderer>.Instance);

        public ValueTask DisposeAsync() => Renderer.DisposeAsync();
    }

    private static readonly DateTime Generated = new(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);

    private static PrintDocument Document(IReadOnlyList<PrintSection> sections, bool draft = false, string? css = null) =>
        new("Fixture document", "General", draft ? "Draft" : "v1", "Alice Anderson", Generated, draft, false, draft ? null : Generated,
            sections, [new PrintSignature("Carol Clark", Generated, "Approved.")], css ?? StyleProperties.ToCss(SeedStyles()));

    [Fact]
    public async Task Toc_page_numbers_are_the_pages_of_the_headings_even_across_several_toc_pages()
    {
        var sections = Enumerable.Range(1, 150)
            .Select(i => new PrintSection($"{i}", 1, $"Topic{i}", i % 10 == 0 ? string.Concat(Enumerable.Repeat("<p>Long text paragraph.</p>", 60)) : "<p>Short.</p>"))
            .ToList();

        var result = await chromium.Renderer.RenderAsync(Document(sections), new PdfExportOptions(), null, TestContext.Current.CancellationToken);

        Assert.InRange(result.Passes, 2, 3);
        var destinations = ChromiumPdfRenderer.Destinations(result.Pdf);
        var pages = PdfText.Pages(result.Pdf);
        var toc = string.Join(' ', pages.Take(pages.FindIndex(p => p.Contains("Topic1 ", StringComparison.Ordinal) && !p.Contains("Contents", StringComparison.Ordinal) && p.Contains("Short.", StringComparison.Ordinal))));
        Assert.True(pages.Count(p => p.Contains("Topic", StringComparison.Ordinal) && !p.Contains("Short.", StringComparison.Ordinal) && !p.Contains("Long text", StringComparison.Ordinal)) >= 3, "the TOC spans several pages");
        for (var i = 0; i < sections.Count; i++)
        {
            var page = destinations[PrintHtmlComposer.Anchor(i)];
            Assert.Contains($"{i + 1} Topic{i + 1} {page} ", toc + " ", StringComparison.Ordinal);
            Assert.Contains($"{i + 1} Topic{i + 1}", pages[page - 1], StringComparison.Ordinal);
        }

        Assert.All(pages.Select((text, index) => (text, index)), p => Assert.Contains($"Page {p.index + 1} of {pages.Count}", p.text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_request_except_the_bundled_fonts_is_blocked_and_logged()
    {
        // Content HTML never contains resources (T09); this bypasses the content renderer to prove the interception.
        var hostile = """<p>before</p><img src="http://internal-host/x.png"><link rel="stylesheet" href="https://internal-host/a.css"><iframe src="http://169.254.169.254/latest"></iframe><p style="background:url(http://internal-host/bg.png)">after</p>""";

        var result = await chromium.Renderer.RenderAsync(Document([new PrintSection("1", 1, "Hostile", hostile)]), new PdfExportOptions(), null, TestContext.Current.CancellationToken);

        Assert.Contains(result.BlockedRequests, u => u.StartsWith("http://internal-host/x.png", StringComparison.Ordinal));
        Assert.Contains(result.BlockedRequests, u => u.StartsWith("https://internal-host/a.css", StringComparison.Ordinal));
        Assert.Contains(result.BlockedRequests, u => u.StartsWith("http://169.254.169.254/", StringComparison.Ordinal));
        Assert.Contains("after", string.Join(' ', PdfText.Pages(result.Pdf)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_normal_render_makes_no_request_and_embeds_the_bundled_fonts()
    {
        var content = ContentHtmlRenderer.Render(Fixture("mixed-runs")).Html;

        var result = await chromium.Renderer.RenderAsync(Document([new PrintSection("1", 1, "Runs", content)]), new PdfExportOptions(), null, TestContext.Current.CancellationToken);

        Assert.Empty(result.BlockedRequests);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(result.Pdf);
        var fonts = pdf.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName ?? "").Distinct().ToList();
        Assert.Contains(fonts, f => f.Contains("LiberationSans", StringComparison.Ordinal));
    }

    public static TheoryData<string> Fixtures => ["headings", "mixed-runs", "nested-lists", "paragraph-formatting", "table"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    public async Task Content_fixtures_match_the_approved_page_snapshots(string fixture)
    {
        var content = ContentHtmlRenderer.Render(Fixture(fixture)).Html;
        var options = new PdfExportOptions(TitlePage: false, Toc: false, HeaderFooter: false, SignaturePage: false);

        var result = await chromium.Renderer.RenderAsync(Document([new PrintSection("1", 1, fixture, content)]), options, null, TestContext.Current.CancellationToken);

        using var actual = Conversion.ToImage(result.Pdf, 0, options: new RenderOptions(Dpi: 72));
        var snapshot = Path.Combine(Repository.Root, "tests", "DocHub.Api.Tests", "PdfSnapshots", $"{fixture}.png");
        if (Environment.GetEnvironmentVariable("UPDATE_PDF_SNAPSHOTS") == "1" || !File.Exists(snapshot))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            await using (var file = File.Create(snapshot))
            {
                actual.Encode(file, SKEncodedImageFormat.Png, 100);
            }

            Assert.True(Environment.GetEnvironmentVariable("UPDATE_PDF_SNAPSHOTS") == "1", $"No approved snapshot yet: wrote {snapshot}; review and commit it.");
            return;
        }

        using var approved = SKBitmap.Decode(snapshot);
        Assert.Equal((approved.Width, approved.Height), (actual.Width, actual.Height));
        var different = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var (a, b) = (actual.GetPixel(x, y), approved.GetPixel(x, y));
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 96)
                {
                    different++;
                }
            }
        }

        // Anti-aliasing may differ slightly between machines; layout changes move far more pixels.
        var share = different / (double)(actual.Width * actual.Height);
        Assert.True(share <= 0.005, $"{fixture}: {share:P2} of the pixels differ from the approved snapshot.");
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ContentFixtures", $"{name}.json"));

    /// <summary>The seeded style catalog (Seed.ContentStyles.sql), so the snapshots show the real Word-like styles.</summary>
    private static List<Domain.Entities.ContentStyle> SeedStyles()
    {
        var seed = File.ReadAllText(Path.Combine(Repository.DatabaseProject, "Scripts", "PostDeployment", "Seed.ContentStyles.sql"));
        return System.Text.RegularExpressions.Regex.Matches(seed, @"\('(?<id>\w+)',\s*N'(?<name>[^']*)',\s*(?<kind>\d),\s*(?:'(?<based>\w+)'|NULL),\s*N'(?<props>(?:[^']|'')*)'\)")
            .Select(m => new Domain.Entities.ContentStyle
            {
                StyleId = m.Groups["id"].Value,
                Name = m.Groups["name"].Value,
                Kind = (Domain.Entities.ContentStyleKind)int.Parse(m.Groups["kind"].Value, System.Globalization.CultureInfo.InvariantCulture),
                BasedOnStyleId = m.Groups["based"].Success ? m.Groups["based"].Value : null,
                PropertiesJson = m.Groups["props"].Value.Replace("''", "'", StringComparison.Ordinal),
            })
            .ToList();
    }
}
