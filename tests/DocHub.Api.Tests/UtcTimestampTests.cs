using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocHub.Api.Common;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>
/// Every timestamp the API returns is explicitly UTC (ends with <c>Z</c>) — whether it was read through EF, raw SQL or a stored
/// procedure — so browsers show the right local time.
/// </summary>
public sealed partial class UtcTimestampTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private readonly DocumentArrange _arrange = new(factory);

    // Real JSON string values only: the audit log's oldValues/newValues carry the audit record verbatim (escaped JSON text).
    [GeneratedRegex("(?<!\\\\)\"(\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?)([^\"]*)\"")]
    private static partial Regex Timestamp();

    private async Task<string> RawAsync(int user, string path)
    {
        using var client = factory.CreateClientFor(user);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {body}");
        return body;
    }

    [Fact]
    public async Task Timestamps_from_ef_raw_sql_and_stored_procedures_end_with_z()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        var (documentId, version, _) = await _arrange.SignedAsync();
        await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Post, $"/api/versions/{version}/comments", new { body = "Looks fine." }, HttpStatusCode.Created);
        var range = $"from={Uri.EscapeDataString(before.ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("o", CultureInfo.InvariantCulture))}";

        string[] paths =
        [
            $"/api/documents/{documentId}", // EF: createdAt, versions' createdAt/signedAt
            $"/api/documents/{documentId}/history", // raw SQL over audit.ChangeLog: changedAt
            $"/api/versions/{version}/signatures", // signedAt
            $"/api/versions/{version}/comments", // createdAt
            $"/api/documents/{documentId}/permissions", // grantedAt
            "/api/folders/1/documents?PageSize=100", // stored procedure usp_ListDocuments (ADO): modifiedAt
            $"/api/admin/audit?{range}", // raw SQL, from/to given in UTC with Z
        ];
        foreach (var path in paths)
        {
            var body = await RawAsync(path.StartsWith("/api/admin", StringComparison.Ordinal) ? TestUsers.Admin : TestUsers.Alice, path);
            var stamps = Timestamp().Matches(body);
            Assert.True(stamps.Count > 0, $"{path} returned no timestamps: {body}");
            Assert.All(stamps, m => Assert.True(m.Groups[2].Value == "Z", $"{path}: {m.Value} is not marked UTC"));
        }

        // The value is the stored UTC time, not shifted.
        var created = DateTime.Parse(
            (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/documents/{documentId}", null, HttpStatusCode.OK)).GetProperty("createdAt").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.InRange(created, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task The_audit_range_filter_is_unchanged_for_utc_query_values()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        var (documentId, _) = await _arrange.CreateAsync();
        string Range(DateTime from, DateTime to) =>
            $"/api/admin/audit?from={Uri.EscapeDataString(from.ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("o", CultureInfo.InvariantCulture))}&PageSize=100";

        var inRange = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, Range(before, DateTime.UtcNow.AddMinutes(1)), null, HttpStatusCode.OK);
        var earlier = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, Range(before.AddDays(-2), before.AddDays(-1)), null, HttpStatusCode.OK);

        Assert.Contains(inRange.GetProperty("items").EnumerateArray(), r => r.TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == documentId);
        Assert.DoesNotContain(earlier.GetProperty("items").EnumerateArray(), r => r.TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == documentId);
    }

    [Fact]
    public async Task The_audit_range_allows_31_local_days_across_a_daylight_saving_change()
    {
        // 1–31 October in Berlin: local midnight to local midnight is 31 days and one hour.
        var from = new DateTime(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc);
        string Path(TimeSpan span) =>
            $"/api/admin/audit?from={Uri.EscapeDataString(from.ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString((from + span).ToString("o", CultureInfo.InvariantCulture))}";

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, Path(TimeSpan.FromDays(31) + TimeSpan.FromHours(1) - TimeSpan.FromMilliseconds(1)), null, HttpStatusCode.OK);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, Path(TimeSpan.FromDays(31) + TimeSpan.FromHours(2)), null, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Changes_since_a_date_take_the_start_of_the_callers_local_day_as_a_utc_instant()
    {
        var (_, draft) = await _arrange.CreateAsync();
        var node = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Post, $"/api/versions/{draft}/nodes", new { nodeTypeId = 1, title = "Scope" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var logical = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}", null, HttpStatusCode.OK)).GetProperty("logicalNodeId").GetGuid();
        var edited = DateTime.UtcNow;
        var rowVersion = (await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/nodes/{node}/content", null, HttpStatusCode.OK)).GetProperty("rowVersion").GetString();
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Put, $"/api/nodes/{node}/content",
            new { contentJson = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"edited"}]}]}"""), rowVersion }, HttpStatusCode.OK);

        async Task<int> ChangesSince(string since)
        {
            var summary = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, $"/api/versions/{draft}/change-summary?since={Uri.EscapeDataString(since)}", null, HttpStatusCode.OK);
            return summary.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("logicalNodeId").GetGuid() == logical).Select(n => n.GetProperty("changeCount").GetInt32()).SingleOrDefault();
        }

        // The web sends local midnight as an instant (e.g. 26 Sep in Berlin = 2026-09-25T22:00:00.000Z).
        Assert.True(await ChangesSince($"d:{edited.AddMinutes(-90):yyyy-MM-ddTHH:mm:ss.fff}Z") >= 1);
        Assert.Equal(0, await ChangesSince($"d:{DateTime.UtcNow.AddMinutes(90):yyyy-MM-ddTHH:mm:ss.fff}Z"));
        // A plain date still means that UTC day.
        await ChangesSince($"d:{edited:yyyy-MM-dd}");
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified, "\"2026-09-26T10:15:00Z\"")]
    [InlineData(DateTimeKind.Utc, "\"2026-09-26T10:15:00Z\"")]
    public void The_converter_marks_unspecified_and_utc_values_as_utc(DateTimeKind kind, string expected)
    {
        var options = new JsonSerializerOptions { Converters = { new UtcDateTimeConverter() } };

        Assert.Equal(expected, JsonSerializer.Serialize(new DateTime(2026, 9, 26, 10, 15, 0, kind), options));
        Assert.Equal($"[{expected},null]", JsonSerializer.Serialize(new DateTime?[] { new DateTime(2026, 9, 26, 10, 15, 0, kind), null }, options));
        var local = new DateTime(2026, 9, 26, 10, 15, 0, DateTimeKind.Local);
        Assert.Equal($"\"{local.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z\"", JsonSerializer.Serialize(local, options));
    }
}
