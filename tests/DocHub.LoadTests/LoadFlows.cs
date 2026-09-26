using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocHub.LoadTests;

/// <summary>
/// The NFR-L3 traffic mix (T19 §2) as user flows: 70 % readers, 20 % editors, 10 % other (comments, sign/withdraw, admin reads,
/// PDF export ≈ 1 % of all traffic). Users come from the generated population; every request goes through the recorder.
/// </summary>
public sealed class LoadFlows(HttpClient http, LoadCatalog catalog, LoadRecorder recorder, bool exports, TimeSpan autosaveInterval)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _editing = new();

    public const int AdminUserId = 1;

    /// <summary>Requests of one iteration on average (reader 8, editor 13, other ≈ 2): converts a request rate into a flow rate.</summary>
    public const double RequestsPerFlow = 8.4;

    public async Task RunOneAsync(Random random, CancellationToken cancellationToken)
    {
        var roll = random.Next(100);
        var document = catalog.Documents[random.Next(catalog.Documents.Count)];
        if (roll < 70)
        {
            await ReaderAsync(random, document, cancellationToken);
        }
        else if (roll < 90)
        {
            await EditorAsync(random, catalog.Documents.Where(d => d.HasDraft).ElementAtOrDefault(random.Next(catalog.Documents.Count(d => d.HasDraft))) ?? document, cancellationToken);
        }
        else
        {
            await OtherAsync(random, roll - 90, document, cancellationToken);
        }
    }

    /// <summary>List the folder (title filter) → open the document → its tree → read 5 nodes.</summary>
    private async Task ReaderAsync(Random random, LoadDocument document, CancellationToken cancellationToken)
    {
        var user = catalog.Readers[random.Next(catalog.Readers.Count)];
        await SendAsync("list documents", HttpMethod.Get, user, $"/api/folders/{document.FolderId}/documents?Search=document&PageSize=20", null, cancellationToken);
        await SendAsync("open document", HttpMethod.Get, user, $"/api/documents/{document.Id}", null, cancellationToken);
        await SendAsync("tree", HttpMethod.Get, user, $"/api/versions/{document.CurrentVersionId}/tree", null, cancellationToken);
        foreach (var (nodeId, _) in document.Nodes.OrderBy(_ => random.Next()).Take(5))
        {
            await SendAsync("node content", HttpMethod.Get, user, $"/api/nodes/{nodeId}/content", null, cancellationToken);
        }
    }

    /// <summary>Open the draft → autosave a node 10 times (every 3 s) → its history → compare with the latest signed version.</summary>
    private async Task EditorAsync(Random random, LoadDocument document, CancellationToken cancellationToken)
    {
        var user = document.OwnerId;
        // Two editors never save the same node at once (that is a real conflict, not load).
        var (nodeId, logical) = document.Nodes.OrderBy(_ => random.Next()).FirstOrDefault(n => _editing.TryAdd(n.NodeId, true));
        if (nodeId == 0)
        {
            return;
        }

        try
        {
            await EditAsync(user, document, nodeId, logical, cancellationToken);
        }
        finally
        {
            _editing.TryRemove(nodeId, out _);
        }
    }

    private async Task EditAsync(int user, LoadDocument document, int nodeId, Guid logical, CancellationToken cancellationToken)
    {
        await SendAsync("open document", HttpMethod.Get, user, $"/api/documents/{document.Id}", null, cancellationToken);
        var content = await SendAsync("node content", HttpMethod.Get, user, $"/api/nodes/{nodeId}/content", null, cancellationToken);
        if (content is { } current && document.HasDraft)
        {
            var json = JsonNode.Parse(current.GetProperty("contentJson").GetRawText())!.AsObject();
            var rowVersion = current.GetProperty("rowVersion").GetString();
            var blocks = json["content"]!.AsArray();
            var marker = new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Autosave 0" }) };
            blocks.Add(marker);
            for (var i = 1; i <= 10; i++)
            {
                marker["content"]![0]!["text"] = string.Create(CultureInfo.InvariantCulture, $"Autosave {i} {Guid.NewGuid():N}");
                var saved = await SendAsync("autosave", HttpMethod.Put, user, $"/api/nodes/{nodeId}/content", new { contentJson = json, rowVersion }, cancellationToken);
                if (saved is not { } result)
                {
                    break;
                }

                rowVersion = result.GetProperty("rowVersion").GetString();
                await Task.Delay(autosaveInterval, cancellationToken);
            }
        }

        await SendAsync("node history", HttpMethod.Get, user, $"/api/documents/{document.Id}/nodes/{logical}/history?PageSize=20", null, cancellationToken);
        if (document.HasDraft && document.LatestSignedVersionId is not null)
        {
            await SendAsync("compare", HttpMethod.Get, user, $"/api/documents/{document.Id}/compare?base=latestSigned&target=draft", null, cancellationToken);
        }
    }

    /// <summary>0–3 comment, 4–6 sign and withdraw, 7–8 admin audit read, 9 PDF export (1 % of all flows).</summary>
    private async Task OtherAsync(Random random, int kind, LoadDocument document, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case <= 3:
                var author = document.Editors.Length > 0 ? document.Editors[random.Next(document.Editors.Length)] : document.OwnerId;
                await SendAsync("comment", HttpMethod.Post, author, $"/api/versions/{document.CurrentVersionId}/comments", new { body = "Load test comment." }, cancellationToken);
                break;
            case <= 6 when document.HasDraft && document.Approvers.Length > 0:
                // Sign and withdraw at once, so the draft is never finalized by the load test.
                var approver = document.Approvers[random.Next(document.Approvers.Length)];
                if (await SendAsync("sign", HttpMethod.Post, approver, $"/api/versions/{document.CurrentVersionId}/signatures", new { comment = "Load test" }, cancellationToken) is not null)
                {
                    await SendAsync("withdraw", HttpMethod.Delete, approver, $"/api/versions/{document.CurrentVersionId}/signatures/mine", null, cancellationToken);
                }

                break;
            case <= 8:
                var to = DateTime.UtcNow;
                await SendAsync("admin audit", HttpMethod.Get, AdminUserId,
                    $"/api/admin/audit?from={Uri.EscapeDataString(to.AddDays(-1).ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("o", CultureInfo.InvariantCulture))}&PageSize=50",
                    null, cancellationToken);
                break;
            default:
                if (exports && document.LatestSignedVersionId is { } signed)
                {
                    await ExportAsync(catalog.Readers[random.Next(catalog.Readers.Count)], signed, cancellationToken);
                }
                else
                {
                    await SendAsync("open document", HttpMethod.Get, document.OwnerId, $"/api/documents/{document.Id}", null, cancellationToken);
                }

                break;
        }
    }

    private async Task ExportAsync(int user, int versionId, CancellationToken cancellationToken)
    {
        var job = await SendAsync("export start", HttpMethod.Post, user, $"/api/versions/{versionId}/exports/pdf", new { }, cancellationToken);
        if (job is not { } started)
        {
            return;
        }

        var id = started.GetProperty("jobId").GetInt32();
        var status = started.GetProperty("status").GetString();
        var watch = Stopwatch.StartNew();
        while (status is "Queued" or "Running" && watch.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            status = (await SendAsync("export status", HttpMethod.Get, user, $"/api/exports/{id}", null, cancellationToken))?.GetProperty("status").GetString();
        }

        if (status == "Succeeded")
        {
            await SendAsync("export download", HttpMethod.Get, user, $"/api/exports/{id}/file", null, cancellationToken, json: false);
        }
    }

    /// <summary>One request as <paramref name="user"/>, timed and recorded; the JSON body of a success, else null.</summary>
    private async Task<JsonElement?> SendAsync(string endpoint, HttpMethod method, int user, string path, object? body, CancellationToken cancellationToken, bool json = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-User-Id", user.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var watch = Stopwatch.StartNew();
        int status;
        JsonElement? result = null;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode && json && bytes.Length > 0)
            {
                result = JsonDocument.Parse(bytes).RootElement.Clone();
            }
            else if (response.IsSuccessStatusCode)
            {
                result = default(JsonElement);
            }
        }
        catch (HttpRequestException)
        {
            status = 0;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = 0; // timeout
        }

        recorder.Add(endpoint, watch.Elapsed.TotalMilliseconds, status);
        return result;
    }
}
