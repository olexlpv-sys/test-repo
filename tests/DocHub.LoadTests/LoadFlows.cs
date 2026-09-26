using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocHub.LoadTests;

/// <summary>
/// The NFR-L3 traffic mix (T19 §2) as user flows. The mix is a share of the <b>requests</b> — 70 % readers, 20 % editors, 10 % other
/// (comments, sign/withdraw, admin reads, new draft, PDF export ≈ 1 % of all traffic) — so each flow class is picked with a weight of
/// its share divided by its requests per flow. Users come from the generated population; every request goes through the recorder.
/// </summary>
public sealed class LoadFlows(HttpClient http, LoadCatalog catalog, LoadRecorder recorder, bool exports, TimeSpan autosaveInterval, TimeSpan requestTimeout)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _editing = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _drafting = new();

    public const int AdminUserId = 1;

    /// <summary>Reader: list, open, tree, 5 × node content.</summary>
    public const int ReaderRequests = 8;

    /// <summary>Editor: open, content, 10 autosaves, add + delete a node (tree edit), history, compare.</summary>
    public const int EditorRequests = 16;

    /// <summary>The other flows per 100: comment 40 (1 request), sign + withdraw 20 (2), admin audit 20 (1), new draft + discard 15 (2), PDF export 5 (≈ 4).</summary>
    public const double OtherRequests = ((40 * 1) + (20 * 2) + (20 * 1) + (15 * 2) + (5 * 4)) / 100.0;

    private const double ReaderWeight = 0.70 / ReaderRequests;
    private const double EditorWeight = 0.20 / EditorRequests;
    private const double OtherWeight = 0.10 / OtherRequests;

    /// <summary>Requests of one flow on average: converts a request rate into a flow rate.</summary>
    public const double RequestsPerFlow = 1 / (ReaderWeight + EditorWeight + OtherWeight);

    public async Task RunOneAsync(Random random, CancellationToken cancellationToken)
    {
        var roll = random.NextDouble() * (ReaderWeight + EditorWeight + OtherWeight);
        var document = catalog.Documents[random.Next(catalog.Documents.Count)];
        if (roll < ReaderWeight)
        {
            await ReaderAsync(random, document, cancellationToken);
        }
        else if (roll < ReaderWeight + EditorWeight)
        {
            var drafts = catalog.Documents.Where(d => d.HasDraft).ToList();
            await EditorAsync(random, drafts.Count > 0 ? drafts[random.Next(drafts.Count)] : document, cancellationToken);
        }
        else
        {
            await OtherAsync(random, random.Next(100), document, cancellationToken);
        }
    }

    /// <summary>List the folder (title filter) → open the document → its tree → read 5 nodes.</summary>
    private async Task ReaderAsync(Random random, LoadDocument document, CancellationToken cancellationToken)
    {
        const LoadCategory C = LoadCategory.Reader;
        var user = catalog.Readers[random.Next(catalog.Readers.Count)];
        await SendAsync(C, "list documents", HttpMethod.Get, user, $"/api/folders/{document.FolderId}/documents?Search=document&PageSize=20", null, cancellationToken);
        await SendAsync(C, "open document", HttpMethod.Get, user, $"/api/documents/{document.Id}", null, cancellationToken);
        await SendAsync(C, "tree", HttpMethod.Get, user, $"/api/versions/{document.CurrentVersionId}/tree", null, cancellationToken);
        foreach (var (nodeId, _) in document.Nodes.OrderBy(_ => random.Next()).Take(5))
        {
            await SendAsync(C, "node content", HttpMethod.Get, user, $"/api/nodes/{nodeId}/content", null, cancellationToken);
        }
    }

    /// <summary>Open the draft → autosave a node 10 times (every 3 s) → add and delete a node → its history → compare with the latest signed version.</summary>
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
        const LoadCategory C = LoadCategory.Editor;
        await SendAsync(C, "open document", HttpMethod.Get, user, $"/api/documents/{document.Id}", null, cancellationToken);
        var content = await SendAsync(C, "node content", HttpMethod.Get, user, $"/api/nodes/{nodeId}/content", null, cancellationToken);
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
                var saved = await SendAsync(C, "autosave", HttpMethod.Put, user, $"/api/nodes/{nodeId}/content", new { contentJson = json, rowVersion }, cancellationToken);
                if (saved is not { } result)
                {
                    break;
                }

                rowVersion = result.GetProperty("rowVersion").GetString();
                await Task.Delay(autosaveInterval, cancellationToken);
            }

            // A tree edit: a new clause at the end of the tree, removed again (keeps the tree size stable).
            if (await SendAsync(C, "add node", HttpMethod.Post, user, $"/api/versions/{document.CurrentVersionId}/nodes", new { nodeTypeId = 4, title = "Load test clause" }, cancellationToken) is { } added)
            {
                var id = added.GetProperty("id").GetInt32();
                var version = Uri.EscapeDataString(added.GetProperty("rowVersion").GetString()!);
                await SendAsync(C, "delete node", HttpMethod.Delete, user, $"/api/nodes/{id}?rowVersion={version}", null, cancellationToken);
            }
        }

        await SendAsync(C, "node history", HttpMethod.Get, user, $"/api/documents/{document.Id}/nodes/{logical}/history?PageSize=20", null, cancellationToken);
        if (document.HasDraft && document.LatestSignedVersionId is not null)
        {
            await SendAsync(C, "compare", HttpMethod.Get, user, $"/api/documents/{document.Id}/compare?base=latestSigned&target=draft", null, cancellationToken);
        }
    }

    /// <summary>Per 100 other flows: 0–39 comment, 40–59 sign and withdraw, 60–79 admin audit read, 80–94 new draft and discard, 95–99 PDF export.</summary>
    private async Task OtherAsync(Random random, int kind, LoadDocument document, CancellationToken cancellationToken)
    {
        const LoadCategory C = LoadCategory.Other;
        switch (kind)
        {
            case < 40:
                await CommentAsync(random, document, cancellationToken);
                break;
            case < 60 when document.HasDraft && document.Approvers.Length > 0:
                // Sign and withdraw at once, so the draft is never finalized by the load test.
                var approver = document.Approvers[random.Next(document.Approvers.Length)];
                if (await SendAsync(C, "sign", HttpMethod.Post, approver, $"/api/versions/{document.CurrentVersionId}/signatures", new { comment = "Load test" }, cancellationToken) is not null)
                {
                    await SendAsync(C, "withdraw", HttpMethod.Delete, approver, $"/api/versions/{document.CurrentVersionId}/signatures/mine", null, cancellationToken);
                }

                break;
            case < 60:
                await CommentAsync(random, document, cancellationToken);
                break;
            case < 80:
                var to = DateTime.UtcNow;
                await SendAsync(C, "admin audit", HttpMethod.Get, AdminUserId,
                    $"/api/admin/audit?from={Uri.EscapeDataString(to.AddDays(-1).ToString("o", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("o", CultureInfo.InvariantCulture))}&PageSize=50",
                    null, cancellationToken);
                break;
            case < 95:
                await NewDraftAsync(random, document, cancellationToken);
                break;
            default:
                if (exports && document.LatestSignedVersionId is { } signed)
                {
                    await ExportAsync(catalog.Readers[random.Next(catalog.Readers.Count)], signed, cancellationToken);
                }
                else
                {
                    await SendAsync(C, "open document", HttpMethod.Get, document.OwnerId, $"/api/documents/{document.Id}", null, cancellationToken);
                }

                break;
        }
    }

    private async Task CommentAsync(Random random, LoadDocument document, CancellationToken cancellationToken)
    {
        var author = document.Editors.Length > 0 ? document.Editors[random.Next(document.Editors.Length)] : document.OwnerId;
        await SendAsync(LoadCategory.Other, "comment", HttpMethod.Post, author, $"/api/versions/{document.CurrentVersionId}/comments", new { body = "Load test comment." }, cancellationToken);
    }

    /// <summary>
    /// New draft (the deep copy, NFR-L5 2.5 s) of a document whose current version is signed, discarded at once so the document
    /// returns to its generated state; one flow per document at a time (a second draft is a real conflict, not load).
    /// </summary>
    private async Task NewDraftAsync(Random random, LoadDocument fallback, CancellationToken cancellationToken)
    {
        var candidates = catalog.Documents.Where(d => !d.HasDraft && d.LatestSignedVersionId is not null).ToList();
        var document = candidates.OrderBy(_ => random.Next()).Take(20).FirstOrDefault(d => _drafting.TryAdd(d.Id, true));
        if (document is null)
        {
            await CommentAsync(random, fallback, cancellationToken);
            return;
        }

        try
        {
            if (await SendAsync(LoadCategory.Other, "new draft", HttpMethod.Post, document.OwnerId, $"/api/documents/{document.Id}/drafts", null, cancellationToken) is { } draft)
            {
                var version = Uri.EscapeDataString(draft.GetProperty("rowVersion").GetString()!);
                await SendAsync(LoadCategory.Other, "discard draft", HttpMethod.Delete, document.OwnerId, $"/api/versions/{draft.GetProperty("id").GetInt32()}?rowVersion={version}", null, cancellationToken);
            }
        }
        finally
        {
            _drafting.TryRemove(document.Id, out _);
        }
    }

    private async Task ExportAsync(int user, int versionId, CancellationToken cancellationToken)
    {
        var job = await SendAsync(LoadCategory.Other, "export start", HttpMethod.Post, user, $"/api/versions/{versionId}/exports/pdf", new { }, cancellationToken);
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
            status = (await SendAsync(LoadCategory.Other, "export status", HttpMethod.Get, user, $"/api/exports/{id}", null, cancellationToken))?.GetProperty("status").GetString();
        }

        if (status == "Succeeded")
        {
            await SendAsync(LoadCategory.Other, "export download", HttpMethod.Get, user, $"/api/exports/{id}/file", null, cancellationToken, json: false);
        }
    }

    /// <summary>
    /// One request as <paramref name="user"/>, timed and recorded; the JSON body of a success, else null. No request starts after
    /// the run ended, but a started one is never cancelled by the end of the run: it completes (or times out after the request
    /// timeout, a failure) and is recorded, so a hung request can't go missing.
    /// </summary>
    private async Task<JsonElement?> SendAsync(
        LoadCategory category, string endpoint, HttpMethod method, int user, string path, object? body, CancellationToken cancellationToken, bool json = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-User-Id", user.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var timeout = new CancellationTokenSource(requestTimeout);
        recorder.Start();
        var watch = Stopwatch.StartNew();
        var status = 0;
        JsonElement? result = null;
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
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
#pragma warning disable CA1031 // Every failure (network, timeout, unreadable body) is a failed request of the run.
        catch (Exception)
#pragma warning restore CA1031
        {
            status = 0;
            result = null;
        }
        finally
        {
            recorder.Complete(endpoint, watch.Elapsed.TotalMilliseconds, status, category);
        }

        return result;
    }
}
