using System.Collections.Concurrent;
using DocHub.Infrastructure.Export;
using DocHub.Testing.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.Azurite;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>
/// The API with the PDF export worker on: real Chromium (Playwright), real blob storage (an Azurite container per factory). The
/// renderer is wrapped by <see cref="RecordingRenderer"/>, which records every render (and its blocked requests) and can be told
/// to fail the next render.
/// </summary>
public sealed class ExportApiFactory(SqlServerContainerFixture server) : DocHubApiFactory(server)
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    /// <summary>The Chromium binary: <c>PLAYWRIGHT_CHROMIUM</c>, else the pre-installed one, else Playwright's own download.</summary>
    public static string? ChromiumPath =>
        Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM") is { Length: > 0 } path ? path
        : File.Exists("/opt/pw-browsers/chromium") ? "/opt/pw-browsers/chromium"
        : null;

    public RecordingRenderer Renderer => Services.GetRequiredService<RecordingRenderer>();

    public override async ValueTask InitializeAsync()
    {
        await Task.WhenAll(base.InitializeAsync().AsTask(), _azurite.StartAsync()).ConfigureAwait(false);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Export:Worker:Enabled", "true");
        builder.UseSetting("Export:Worker:PollInterval", "00:00:00.200");
        builder.UseSetting("Export:Storage:ConnectionString", _azurite.GetConnectionString());
        builder.UseSetting("Export:Renderer:ChromiumExecutablePath", ChromiumPath ?? "");
        // Test runs may be root inside a container, where Chromium can't create its sandbox.
        builder.UseSetting("Export:Renderer:Sandbox", "false");
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ChromiumPdfRenderer>();
        services.AddSingleton<RecordingRenderer>();
        services.Replace(ServiceDescriptor.Singleton<IPdfRenderer>(sp => sp.GetRequiredService<RecordingRenderer>()));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _azurite.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Records renders; <see cref="FailNext"/> makes the next render throw (as a Chromium crash would).</summary>
public sealed class RecordingRenderer(ChromiumPdfRenderer inner) : IPdfRenderer
{
    public ConcurrentQueue<(PrintDocument Document, PdfRenderResult Result)> Renders { get; } = new();

    private volatile bool _failNext;

    public bool FailNext { get => _failNext; set => _failNext = value; }

    public async Task<PdfRenderResult> RenderAsync(PrintDocument document, PdfExportOptions exportOptions, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (_failNext)
        {
            _failNext = false;
            throw new Microsoft.Playwright.PlaywrightException("Target page, context or browser has been closed (simulated crash)");
        }

        var result = await inner.RenderAsync(document, exportOptions, progress, cancellationToken).ConfigureAwait(false);
        Renders.Enqueue((document, result));
        return result;
    }
}
