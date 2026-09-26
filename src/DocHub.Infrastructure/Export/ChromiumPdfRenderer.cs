using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Tokens;

namespace DocHub.Infrastructure.Export;

/// <summary>Configuration section <c>Export:Renderer</c>.</summary>
public sealed class PdfRendererOptions
{
    public const string Section = "Export:Renderer";

    /// <summary>The Chromium binary; null uses the browser installed by Playwright (<c>playwright install chromium</c>).</summary>
    public string? ChromiumExecutablePath { get; set; }

    /// <summary>Chromium's sandbox (NFR-5). Only turn it off where Chromium can't create one (e.g. a container running as root).</summary>
    public bool Sandbox { get; set; } = true;

    /// <summary>The limit for one render (all passes).</summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>A rendered PDF with its page count, the number of Chromium passes and every request the interception blocked.</summary>
public sealed record PdfRenderResult(byte[] Pdf, int PageCount, int Passes, IReadOnlyList<string> BlockedRequests);

public interface IPdfRenderer
{
    Task<PdfRenderResult> RenderAsync(PrintDocument document, PdfExportOptions exportOptions, IProgress<int>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Print HTML → PDF with headless Chromium through Playwright (T20 steps 3–4). JavaScript is off, every request except the bundled
/// fonts is aborted (and logged), each render gets a fresh browser context without cookies or credentials, and the TOC page numbers
/// come from the named destinations of a previous pass (read with PdfPig): pass 1 without numbers, pass 2 with them, and one more
/// pass if the numbers moved (e.g. the TOC grew by a page).
/// </summary>
public sealed partial class ChromiumPdfRenderer(IOptions<PdfRendererOptions> options, PrintHtmlComposer composer, ILogger<ChromiumPdfRenderer> logger)
    : IPdfRenderer, IAsyncDisposable
{
    private const int MaxPasses = 3;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<PdfRenderResult> RenderAsync(PrintDocument document, PdfExportOptions exportOptions, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(exportOptions);
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RenderTimeout);
        var browser = await BrowserAsync(timeout.Token);
        var blocked = new ConcurrentQueue<string>();
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
            Offline = false,
        });
        // A cancelled or timed-out render closes its context, which fails the pending Playwright call.
        await using var closeOnCancel = timeout.Token.Register(() => _ = context.CloseAsync());
        await context.RouteAsync("**/*", route => HandleAsync(route, blocked));
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout((float)settings.RenderTimeout.TotalMilliseconds);

        var toc = exportOptions.Toc && document.Sections.Count > 0;
        IReadOnlyDictionary<string, int>? numbers = null;
        byte[] pdf = [];
        var passes = 0;
        try
        {
            while (true)
            {
                passes++;
                pdf = await PassAsync(page, composer.Compose(document, exportOptions, numbers), document, exportOptions);
                progress?.Report(Math.Min(20 + (passes * 30), 90));
                if (!toc)
                {
                    break;
                }

                var found = Destinations(pdf);
                if ((numbers is not null && Same(numbers, found)) || passes == MaxPasses)
                {
                    break;
                }

                numbers = found;
            }
        }
        catch (PlaywrightException) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The PDF render took longer than {settings.RenderTimeout}.");
        }

        int pageCount;
        using (var parsed = PdfDocument.Open(pdf))
        {
            pageCount = parsed.NumberOfPages;
        }

        return new PdfRenderResult(pdf, pageCount, passes, [.. blocked]);
    }

    /// <summary>Page numbers of the named destinations the TOC links point to (anchor → 1-based page).</summary>
    public static Dictionary<string, int> Destinations(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            foreach (var annotation in page.GetAnnotations())
            {
                if (annotation.AnnotationDictionary.TryGet(NameToken.Dest, out var dest) && dest is NameToken or StringToken
                    && annotation.Action is AbstractGoToAction { Destination.PageNumber: var number })
                {
                    var name = dest is NameToken n ? n.Data : ((StringToken)dest).Data;
                    result.TryAdd(name, number);
                }
            }
        }

        return result;
    }

    private static bool Same(IReadOnlyDictionary<string, int> a, Dictionary<string, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static async Task<byte[]> PassAsync(IPage page, string html, PrintDocument document, PdfExportOptions exportOptions)
    {
        await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
        var headerFooter = PrintHtmlComposer.HasHeaderFooter(document, exportOptions);
        return await page.PdfAsync(new PagePdfOptions
        {
            Format = exportOptions.PageSize == PdfPageSize.Letter ? "Letter" : "A4",
            Margin = new Margin { Top = "2.5cm", Bottom = "2.5cm", Left = "2.5cm", Right = "2.5cm" },
            PrintBackground = true,
            DisplayHeaderFooter = headerFooter,
            HeaderTemplate = headerFooter ? PrintHtmlComposer.HeaderTemplate(document, exportOptions) : null,
            FooterTemplate = headerFooter ? PrintHtmlComposer.FooterTemplate(exportOptions) : null,
            Outline = true,
            Tagged = true,
        });
    }

    private async Task HandleAsync(IRoute route, ConcurrentQueue<string> blocked)
    {
        var url = route.Request.Url;
        if (PrintFonts.FileFor(url) is { } file && File.Exists(file))
        {
            await route.FulfillAsync(new RouteFulfillOptions { Path = file, ContentType = "font/ttf" });
            return;
        }

        blocked.Enqueue(url);
        LogBlocked(logger, url.Length > 300 ? url[..300] : url);
        await route.AbortAsync("blockedbyclient");
    }

    private async Task<IBrowser> BrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsConnected: true } ready)
        {
            return ready;
        }

        await _launch.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true } launched)
            {
                return launched;
            }

            if (_browser is not null)
            {
                // Crashed or closed: start a new one.
                await _browser.DisposeAsync();
            }

            _playwright ??= await Playwright.CreateAsync();
            var settings = options.Value;
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(settings.ChromiumExecutablePath) ? null : settings.ChromiumExecutablePath,
                ChromiumSandbox = settings.Sandbox,
                Args = ["--disable-dev-shm-usage", "--disable-extensions", "--disable-background-networking", "--no-first-run"],
            });
            return _browser;
        }
        finally
        {
            _launch.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        _launch.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDF render blocked a request to {Url}")]
    private static partial void LogBlocked(ILogger logger, string url);
}
