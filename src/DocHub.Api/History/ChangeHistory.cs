using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;
using DocHub.Api.Documents;
using DocHub.Domain.Content;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Content.Diff;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocHub.Api.History;

/// <summary>One row of <c>audit.ChangeLog</c> (T03) as read by the history.</summary>
public sealed class ChangeLogRow
{
    public long Id { get; set; }

    public DateTime ChangedAt { get; set; }

    public string TableName { get; set; } = "";

    public string Operation { get; set; } = "";

    public int EntityId { get; set; }

    public int? DocumentId { get; set; }

    public int? DocumentVersionId { get; set; }

    public Guid? LogicalNodeId { get; set; }

    public string? OldValues { get; set; }

    public string? NewValues { get; set; }

    public string? ChangedColumns { get; set; }

    public int? UserId { get; set; }

    public string Source { get; set; } = "";

    public string DbLogin { get; set; } = "";

    public string? CorrelationId { get; set; }

    public string? OperationContext { get; set; }

    public string? Ticket { get; set; }

    public string? Reason { get; set; }

    public IReadOnlySet<string> Columns => (ChangedColumns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private Dictionary<string, string?>? _old;
    private Dictionary<string, string?>? _new;

    public string? Old(string column) => (_old ??= Parse(OldValues)).GetValueOrDefault(column);

    public string? New(string column) => (_new ??= Parse(NewValues)).GetValueOrDefault(column);

    /// <summary>A row image (JSON object) as column → text (JSON text for non-strings); unreadable → empty.</summary>
    private static Dictionary<string, string?> Parse(string? json)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (json is null)
        {
            return values;
        }

        try
        {
            using var document = JsonDocument.Parse(ContentSchema.RepairLoneSurrogates(json), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => property.Value.GetString(),
                        _ => property.Value.GetRawText(),
                    };
                }
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }
}

public sealed record FieldChange(string Field, string? Old, string? New);

/// <summary>A history entry (T11): audit rows of one request and entity, merged.</summary>
public sealed record HistoryEntry(
    long Id, DateTime ChangedAt, int? VersionId, string? VersionLabel, string Kind, Guid? LogicalNodeId, UserRef? User, string Source, string? DbLogin,
    string? Ticket, string? Reason, string Summary, IReadOnlyList<FieldChange> Changes, bool HasContentDiff, bool AfterSigning);

/// <summary>Reads <c>audit.ChangeLog</c> and turns rows into history entries (grouping, kinds, summaries).</summary>
public sealed partial class ChangeHistory(DocHubDbContext db, IContentDiffService diff)
{
    public const string CopyContext = "CopyVersion";

    private const string Columns =
        "[Id], [ChangedAt], [TableName], [Operation], [EntityId], [DocumentId], [DocumentVersionId], [LogicalNodeId], [OldValues], [NewValues], " +
        "[ChangedColumns], [UserId], [Source], [DbLogin], [CorrelationId], [OperationContext], [Ticket], [Reason]";

    /// <summary>
    /// A node's rows across all versions (index on LogicalNodeId, filtered by document too), plus the document's signing rows
    /// as context.
    /// </summary>
    public Task<List<ChangeLogRow>> NodeRowsAsync(int documentId, Guid logicalNodeId, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<ChangeLogRow>(
                $"""
                SELECT {Columns} FROM [audit].[ChangeLog] WHERE [LogicalNodeId] = @l AND [DocumentId] = @d
                UNION ALL
                SELECT {Columns} FROM [audit].[ChangeLog]
                WHERE [DocumentId] = @d AND [TableName] = N'app.DocumentVersion' AND [Operation] = 'U'
                  AND [ChangedColumns] LIKE N'%Status%' AND JSON_VALUE([NewValues], N'$.Status') = N'2'
                """,
                Parameter("@l", logicalNodeId), Parameter("@d", documentId))
            .ToListAsync(cancellationToken);

    /// <summary>A document's rows, optionally filtered (index on DocumentId, ChangedAt); the worker's export job progress updates are left out.</summary>
    public Task<List<ChangeLogRow>> DocumentRowsAsync(int documentId, int? versionId, DateTime? from, DateTime? to, int? userId, string? source, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<ChangeLogRow>(
                $"""
                SELECT {Columns} FROM [audit].[ChangeLog]
                WHERE [DocumentId] = @d AND [ChangedAt] >= @from AND [ChangedAt] < @to
                  AND (@v IS NULL OR [DocumentVersionId] = @v) AND (@u IS NULL OR [UserId] = @u) AND (@s IS NULL OR [Source] = @s)
                  AND NOT ([TableName] = N'app.ExportJob' AND [Operation] <> 'I' AND [Source] = N'App')
                """,
                Parameter("@d", documentId), Parameter("@from", from ?? DateTime.MinValue), Parameter("@to", to ?? DateTime.MaxValue),
                Parameter("@v", versionId), Parameter("@u", userId), Parameter("@s", source))
            .ToListAsync(cancellationToken);

    /// <summary>One row by id.</summary>
    public Task<ChangeLogRow?> RowAsync(long id, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<ChangeLogRow>($"SELECT {Columns} FROM [audit].[ChangeLog] WHERE [Id] = @id", Parameter("@id", id)).SingleOrDefaultAsync(cancellationToken)!;

    /// <summary>The latest row of a table for a node in a version at or before an entry.</summary>
    public Task<ChangeLogRow?> LatestAsync(string table, Guid logicalNodeId, int versionId, long atOrBefore, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<ChangeLogRow>(
                $"""
                SELECT TOP (1) {Columns} FROM [audit].[ChangeLog]
                WHERE [LogicalNodeId] = @l AND [TableName] = @t AND [Id] <= @id
                  AND ([DocumentVersionId] = @v OR TRY_CONVERT(INT, JSON_VALUE([OldValues], N'$.DocumentVersionId')) = @v)
                ORDER BY [Id] DESC
                """,
                Parameter("@l", logicalNodeId), Parameter("@t", table), Parameter("@id", atOrBefore), Parameter("@v", versionId))
            .FirstOrDefaultAsync(cancellationToken)!;

    internal static Microsoft.Data.SqlClient.SqlParameter Parameter(string name, object? value) =>
        value is DateTime
            ? new(name, System.Data.SqlDbType.DateTime2) { Value = value } // ChangedAt is datetime2(7); DateTime.MinValue must fit
            : new(name, value ?? DBNull.Value);

    /// <summary>Rows → entries, newest first (T11 rules 1–3, 5).</summary>
    public async Task<List<HistoryEntry>> EntriesAsync(IReadOnlyCollection<ChangeLogRow> rows, int documentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = Group(rows);
        var userIds = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new UserRef(u.Id, u.DisplayName), cancellationToken);
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken);
        var numbers = versions.Where(v => v.VersionNumber != null).ToDictionary(v => v.Id, v => v.VersionNumber!.Value);
        var labels = versions.ToDictionary(v => v.Id, v => DocumentViews.Label(v, numbers));
        var ids = rows.Select(r => r.Id).ToList();
        var tampered = ids.Count == 0
            ? []
            : (await db.Database.SqlQueryRaw<long>(
                    "SELECT [ChangeLogId] AS [Value] FROM [audit].[vSignedVersionTampering] WHERE [DocumentId] = @d", Parameter("@d", documentId))
                .ToListAsync(cancellationToken)).ToHashSet();

        var entries = groups.Select(g =>
            {
                var primary = Primary(g);
                var (kind, changes) = Describe(g);
                var user = g.First().UserId is { } u ? users.GetValueOrDefault(u) : null;
                var script = g.Any(r => r.Source == "Script");
                var contentRow = g.FirstOrDefault(r => r.TableName == "app.NodeContent" && r.Operation == "U" && r.Columns.Contains("ContentJson"));
                var summary = kind switch
                {
                    "ContentChanged" when contentRow is not null => ContentSummary(contentRow),
                    _ => Summary(kind, changes),
                };
                var versionId = primary.DocumentVersionId ?? (int.TryParse(primary.Old("DocumentVersionId"), out var old) ? old : null);
                return new HistoryEntry(
                    primary.Id, g.Max(r => r.ChangedAt), versionId, versionId is { } v ? labels.GetValueOrDefault(v) : null, kind, g.Select(r => r.LogicalNodeId).FirstOrDefault(l => l != null),
                    user, script ? "Script" : "App", script ? g.First().DbLogin : null, g.Select(r => r.Ticket).FirstOrDefault(t => t != null),
                    g.Select(r => r.Reason).FirstOrDefault(t => t != null), summary, changes, contentRow is not null, g.Any(r => tampered.Contains(r.Id)));
            })
            // Bookkeeping without a visible change (the current-version flag moving to a new draft) is no history entry.
            .Where(e => !(e.Kind == "VersionChanged" && e.Changes.Count == 0))
            .OrderByDescending(e => e.ChangedAt).ThenByDescending(e => e.Id)
            .ToList();
        return await WithParentTitlesAsync(entries, cancellationToken);
    }

    /// <summary>T11 rule 3: moves show the old and new parent by title (also of parents deleted since), not by internal ids.</summary>
    private async Task<List<HistoryEntry>> WithParentTitlesAsync(List<HistoryEntry> entries, CancellationToken ct)
    {
        var parentIds = entries.Where(e => e.Kind == "NodeMoved")
            .SelectMany(e => e.Changes.Where(c => c.Field == "parentNodeId").SelectMany(c => new[] { c.Old, c.New }))
            .Select(id => int.TryParse(id, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
        if (parentIds.Count == 0)
        {
            return entries;
        }

        var titles = await db.DocumentNodes.AsNoTracking().Where(n => parentIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Title, ct);
        foreach (var missing in parentIds.Where(id => !titles.ContainsKey(id)))
        {
            var row = (await db.Database.SqlQueryRaw<ChangeLogRow>(
                    "SELECT TOP (1) " + Columns + " FROM [audit].[ChangeLog] WHERE [TableName] = N'app.DocumentNode' AND [EntityId] = @id ORDER BY [Id] DESC",
                    Parameter("@id", missing)).ToListAsync(ct)).FirstOrDefault();
            if ((row?.Operation == "D" ? row.Old("Title") : row?.New("Title")) is { } title)
            {
                titles[missing] = title;
            }
        }

        string? Title(string? id) => int.TryParse(id, out var n) ? titles.GetValueOrDefault(n, "(unknown section)") : null;
        return entries.Select(e =>
        {
            if (e.Kind != "NodeMoved")
            {
                return e;
            }

            var parent = e.Changes.FirstOrDefault(c => c.Field == "parentNodeId");
            var changes = e.Changes.Where(c => c.Field is not ("parentNodeId" or "sortOrder")).ToList();
            if (parent is null)
            {
                return e with { Changes = changes, Summary = "Reordered among its siblings" };
            }

            var (from, to) = (Title(parent.Old), Title(parent.New));
            changes.Insert(0, new FieldChange("parent", from, to));
            return e with { Changes = changes, Summary = $"Moved from \"{from ?? "top level"}\" to \"{to ?? "top level"}\"" };
        }).ToList();
    }

    /// <summary>Rows of one request (CorrelationId) and one entity (node, else table + id) form one entry; script rows stand alone.</summary>
    public static List<List<ChangeLogRow>> Group(IEnumerable<ChangeLogRow> rows) =>
        rows.OrderBy(r => r.Id)
            .GroupBy(r => (
                Request: r.CorrelationId ?? $"row:{r.Id}",
                Entity: r.LogicalNodeId is { } l && r.TableName is "app.DocumentNode" or "app.NodeContent" ? $"node:{l}:{r.DocumentVersionId}" : $"{r.TableName}:{r.EntityId}"))
            .Select(g => g.ToList())
            .ToList();

    /// <summary>The row whose id identifies the entry: the content row if there is one (its diff/content), else the last row.</summary>
    public static ChangeLogRow Primary(IReadOnlyList<ChangeLogRow> group) =>
        group.LastOrDefault(r => r.TableName == "app.NodeContent") ?? group[^1];

    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "ContentJson", "ContentHtml", "PlainText", "ContentHash", "ModifiedAt", "ModifiedByUserId", "CreatedAt", "CreatedByUserId", "RowVersion", "DerivedStale",
        "DocumentVersionId", "LogicalNodeId", "SchemaVersion", "SignedContentHash", "ContentChangeLogId",
    };

    /// <summary>The kind of an entry (from table, operation and changed columns) and its field changes.</summary>
    public static (string Kind, List<FieldChange> Changes) Describe(IReadOnlyList<ChangeLogRow> group)
    {
        var changes = group.Where(r => r.Operation == "U")
            // The API moves the current-version flag as bookkeeping (new draft, discard); a script doing it is a change.
            .SelectMany(r => r.Columns.Where(c => !Hidden.Contains(c) && !(c == "IsCurrent" && r.Source == "App")).Select(c => new FieldChange(Field(c), Value(c, r.Old(c)), Value(c, r.New(c)))))
            .ToList();
        // The copy into a new draft collapses per node; its version row stays a VersionCreated entry.
        if (group.Any(r => r.OperationContext == CopyContext && r.TableName is "app.DocumentNode" or "app.NodeContent"))
        {
            return ("CopiedToNewDraft", changes);
        }

        var node = group.FirstOrDefault(r => r.TableName == "app.DocumentNode");
        if (node is not null)
        {
            if (node.Operation == "I")
            {
                return ("NodeCreated", [new FieldChange("title", null, node.New("Title"))]);
            }

            if (node.Operation == "D")
            {
                return ("NodeDeleted", [new FieldChange("title", node.Old("Title"), null)]);
            }

            var columns = node.Columns;
            var kind = columns.Contains("ParentNodeId") || columns.Contains("SortOrder") ? "NodeMoved"
                : columns.Contains("Title") ? "NodeRenamed"
                : columns.Contains("NodeTypeId") ? "NodeTypeChanged"
                : group.Any(r => r.TableName == "app.NodeContent" && r.Columns.Contains("ContentJson")) ? "ContentChanged"
                : "NodeChanged";
            return (kind, changes);
        }

        var row = group[^1];
        return (row.TableName, row.Operation) switch
        {
            ("app.NodeContent", _) => ("ContentChanged", changes),
            ("app.DocumentVersion", "I") => ("VersionCreated", changes),
            ("app.DocumentVersion", "U") when row.Columns.Contains("Status") && row.New("Status") == "2" => ("VersionSigned", changes),
            ("app.DocumentVersion", "U") when row.Columns.Contains("Status") && row.New("Status") == "3" => ("VersionDiscarded", changes),
            ("app.DocumentVersion", _) => ("VersionChanged", changes),
            ("app.DocumentPermission", "I") => ("PermissionGranted", [new FieldChange("role", null, Role(row.New("Role"))), new FieldChange("userId", null, row.New("UserId"))]),
            ("app.DocumentPermission", "D") => ("PermissionRevoked", [new FieldChange("role", Role(row.Old("Role")), null), new FieldChange("userId", row.Old("UserId"), null)]),
            ("app.VersionSignature", "I") => ("SignatureAdded", changes),
            ("app.VersionSignature", "U") when row.Columns.Contains("WithdrawnAt") => ("SignatureWithdrawn", changes),
            ("app.ExportJob", "I") => ("PdfExportRequested", []),
            ("app.Comment", "I") => ("CommentAdded", changes),
            ("app.Comment", _) => ("CommentChanged", changes),
            ("app.Document", "I") => ("DocumentCreated", [new FieldChange("title", null, row.New("Title"))]),
            ("app.Document", "U") when row.Columns.Contains("DeletedAt") => (row.New("DeletedAt") is null ? "DocumentRestored" : "DocumentDeleted", changes),
            ("app.Document", "U") when row.Columns.Contains("FolderId") => ("DocumentMoved", changes),
            ("app.Document", "U") when row.Columns.Contains("Title") => ("DocumentRenamed", changes),
            ("app.Document", _) => ("DocumentChanged", changes),
            _ => ("Changed", changes),
        };
    }

    /// <summary>
    /// A column value as recorded in the audit JSON; timestamp columns (<c>…At</c>, stored in UTC as <c>datetime2</c>) are
    /// marked UTC like every other timestamp of the API.
    /// </summary>
    private static string? Value(string column, string? value) =>
        value is not null && column.EndsWith("At", StringComparison.Ordinal) && UnmarkedTimestamp().IsMatch(value) ? value + "Z" : value;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$")]
    private static partial Regex UnmarkedTimestamp();

    private static string? Role(string? role) => role switch { "1" => "Editor", "2" => "Approver", _ => role };

    private static string Field(string column) => column.Length == 0 ? column : char.ToLowerInvariant(column[0]) + column[1..];

    private string ContentSummary(ChangeLogRow row)
    {
        var stats = diff.Diff(row.Old("ContentJson") ?? ContentSchema.EmptyDocument, row.New("ContentJson") ?? ContentSchema.EmptyDocument).Stats;
        return stats.Inserted == 0 && stats.Deleted == 0
            ? "Formatting changed"
            : string.Create(CultureInfo.InvariantCulture, $"Content changed (+{stats.Inserted} / −{stats.Deleted} words)");
    }

    private static string Summary(string kind, List<FieldChange> changes) => kind switch
    {
        "NodeCreated" => $"Section \"{changes.FirstOrDefault()?.New}\" added",
        "NodeDeleted" => $"Section \"{changes.FirstOrDefault()?.Old}\" deleted",
        "NodeRenamed" => $"Renamed \"{changes.FirstOrDefault(c => c.Field == "title")?.Old}\" → \"{changes.FirstOrDefault(c => c.Field == "title")?.New}\"",
        "NodeMoved" => "Section moved",
        "NodeTypeChanged" => "Section type changed",
        "CopiedToNewDraft" => "Copied to a new draft",
        "VersionSigned" => "Version signed",
        "VersionCreated" => "Version created",
        "VersionDiscarded" => "Draft discarded",
        "PermissionGranted" => $"{changes.FirstOrDefault()?.New} role granted",
        "PermissionRevoked" => $"{changes.FirstOrDefault()?.Old} role revoked",
        "SignatureAdded" => "Signed",
        "SignatureWithdrawn" => "Signature withdrawn",
        "CommentAdded" => "Comment added",
        "PdfExportRequested" => "PDF export requested",
        "DocumentCreated" => "Document created",
        "DocumentDeleted" => "Document deleted",
        "DocumentRestored" => "Document restored",
        "DocumentMoved" => "Document moved",
        "DocumentRenamed" => "Document renamed",
        _ => kind,
    };
}
