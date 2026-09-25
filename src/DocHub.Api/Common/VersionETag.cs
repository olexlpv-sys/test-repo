using System.Globalization;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace DocHub.Api.Common;

/// <summary>
/// NFR-L9 revalidation of version reads: ETag = the version stamp (<c>VersionStamp.LastChangeLogId</c>; every change,
/// script edits included, advances it), <c>private, no-cache</c>, varying by user.
/// </summary>
public static class VersionETag
{
    /// <summary>The version's stamp (0 before its first change).</summary>
    public static async Task<long> StampAsync(DocHubDbContext db, int versionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.VersionStamps.AsNoTracking().Where(s => s.DocumentVersionId == versionId).Select(s => (long?)s.LastChangeLogId).SingleOrDefaultAsync(cancellationToken) ?? 0;
    }

    /// <summary>Sets the caching headers; true when the client's copy is current (answer 304).</summary>
    public static bool IsNotModified(HttpContext http, long stamp) => IsNotModified(http, stamp.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// ETag of node contents: the stamp plus the rows' <c>rowVersion</c>s, which the derived-content refresher advances
    /// without an audited change (so without a new stamp) — the returned <c>rowVersion</c> must never be served stale.
    /// </summary>
    public static bool IsNotModified(HttpContext http, long stamp, IEnumerable<(int NodeId, byte[] RowVersion)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var digest = System.Security.Cryptography.SHA256.HashData(
            rows.SelectMany(r => BitConverter.GetBytes(r.NodeId).Concat(r.RowVersion)).ToArray());
        return IsNotModified(http, $"{stamp.ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexString(digest, 0, 8)}");
    }

    private static bool IsNotModified(HttpContext http, string tag)
    {
        ArgumentNullException.ThrowIfNull(http);
        var etag = new EntityTagHeaderValue($"\"{tag}\"");
        http.Response.Headers.ETag = etag.ToString();
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.Vary = "X-User-Id";
        return http.Request.GetTypedHeaders().IfNoneMatch.Any(candidate => candidate.Compare(etag, useStrongComparison: false));
    }
}
