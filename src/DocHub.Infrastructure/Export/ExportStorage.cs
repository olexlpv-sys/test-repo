using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace DocHub.Infrastructure.Export;

/// <summary>Configuration section <c>Export:Storage</c>.</summary>
public sealed class ExportStorageOptions
{
    public const string Section = "Export:Storage";

    /// <summary>An Azure Storage connection string (<c>UseDevelopmentStorage=true</c> is Azurite on its default ports).</summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";

    public string Container { get; set; } = "exports";
}

/// <summary>The exported files (T20 "Storage"): <c>{documentId}/{versionId}/{cacheKey}.pdf</c>; draft files expire.</summary>
public interface IExportStorage
{
    /// <summary>Whether a file exists and has not expired.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>Stores a file (replacing one with the same path); <paramref name="expiresAtUtc"/> null keeps it.</summary>
    Task UploadAsync(string path, byte[] content, DateTime? expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>The file's content, or null when it does not exist or has expired.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes the expired files; returns how many.</summary>
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken);
}

public static class ExportPaths
{
    public static string Blob(int documentId, int versionId, string cacheKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{documentId}/{versionId}/{cacheKey}.pdf");
}

/// <summary>Azure Blob Storage (Azurite locally and in tests). Expiry is blob metadata, enforced on read and by the cleanup.</summary>
public sealed class BlobExportStorage(IOptions<ExportStorageOptions> options, TimeProvider clock) : IExportStorage
{
    private const string ExpiresKey = "expiresat";
    private readonly Lazy<BlobContainerClient> _container = new(() => new BlobContainerClient(options.Value.ConnectionString, options.Value.Container));
    private volatile bool _created;

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        try
        {
            var properties = await container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: cancellationToken);
            return !Expired(properties.Value.Metadata);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return false;
        }
    }

    public async Task UploadAsync(string path, byte[] content, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var container = await ContainerAsync(cancellationToken);
        var metadata = new Dictionary<string, string>();
        if (expiresAtUtc is { } expires)
        {
            metadata[ExpiresKey] = expires.ToString("O", CultureInfo.InvariantCulture);
        }

        await container.GetBlobClient(path).UploadAsync(
            BinaryData.FromBytes(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }, Metadata = metadata },
            cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        try
        {
            var download = await container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: cancellationToken);
            if (Expired(download.Value.Details.Metadata))
            {
                await download.Value.Content.DisposeAsync();
                return null;
            }

            return download.Value.Content;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var container = await ContainerAsync(cancellationToken);
        var deleted = 0;
        await foreach (var blob in container.GetBlobsAsync(new GetBlobsOptions { Traits = BlobTraits.Metadata }, cancellationToken))
        {
            if (Expired(blob.Metadata))
            {
                await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
                deleted++;
            }
        }

        return deleted;
    }

    private bool Expired(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(ExpiresKey, out var value)
        && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
        && expires.ToUniversalTime() <= clock.GetUtcNow().UtcDateTime;

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken cancellationToken)
    {
        var container = _container.Value;
        if (!_created)
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _created = true;
        }

        return container;
    }
}
