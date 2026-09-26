using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using DocHub.Domain.Signing;
using DocHub.Infrastructure.Content;
using Microsoft.Data.SqlClient;

namespace DocHub.DataGen;

/// <summary>What was generated and how long it took.</summary>
public sealed record DataGenReport(
    int Users, int Folders, int Documents, int Versions, long Nodes, long Contents, long Grants, long Signatures, long Comments, long AuditRows, TimeSpan Elapsed);

/// <summary>
/// Bulk-loads production-like data (T19 §1) into a freshly deployed DocHub database with <see cref="SqlBulkCopy"/>. Bulk copies
/// don't fire triggers, so the audit triggers are bypassed; the generator writes what they would have maintained (one synthetic
/// audit row per entity, in time order, <c>VersionStamp</c>) plus the cached version hashes, and finally inserts the
/// reconciliation baseline, so the ledger reconciliation (T21) ignores the bulk load. Constraints are validated at the end (they stay trusted).
/// </summary>
public sealed class DataGenerator(string connectionString, DataGenOptions options, Action<string> log)
{
    public const string LoginPrefix = "load";
    public const string DbLogin = "dochub-datagen";
    private const int Templates = 48;
    private const byte Draft = 1;
    private const byte Signed = 2;
    private const byte EditorRole = 1;
    private const byte ApproverRole = 2;

    private readonly DateTime _now = DateTime.UtcNow.AddMinutes(-5) is var n ? new DateTime(n.Ticks - (n.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc) : default;
    private IReadOnlyList<ContentTemplate> _templates = [];
    private Dictionary<string, byte[]> _hashes = [];
    private int[] _editors = [];
    private int[] _readers = [];
    private int[] _folders = [];
    private long _nodes;
    private long _contents;
    private long _grants;
    private long _signatures;
    private long _comments;
    private long _audit;
    private readonly Dictionary<string, TimeSpan> _phases = [];

    /// <summary>Time per load phase, summed over the parallel loaders (for tuning).</summary>
    private void Time(string phase, Stopwatch watch)
    {
        lock (_phases)
        {
            _phases[phase] = _phases.GetValueOrDefault(phase) + watch.Elapsed;
        }

        watch.Restart();
    }

    // Next ids (explicit, KeepIdentity), taken from the database so seed rows are kept.
    private int _nextUser;
    private int _nextFolder;
    private int _nextDocument;
    private int _nextVersion;
    private int _nextNode;
    private int _nextGrant;
    private int _nextSignature;
    private int _nextComment;
    private long _nextChangeLog;
    private readonly HashSet<string> _unusedIdentities = new(StringComparer.Ordinal);

    public async Task<DataGenReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureFreshAsync(connection, cancellationToken);

        var styles = new List<string>();
        await using (var command = new SqlCommand("SELECT [StyleId] FROM [app].[ContentStyle]", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                styles.Add(reader.GetString(0));
            }
        }

        _templates = ContentTemplates.Build(Templates, options.Seed, styles, StyleProperties.DefaultFontFamilies);
        _hashes = _templates.ToDictionary(t => t.Json, t => t.Hash, StringComparer.Ordinal);
        await ReadNextIdsAsync(connection, cancellationToken);
        log($"Scale {options.Scale.ToString(CultureInfo.InvariantCulture)}: {options.Users} users, {options.Folders} folders, {options.Documents} documents × {options.VersionsPerDocument} versions.");

        await LoadUsersAndFoldersAsync(connection, cancellationToken);
        log($"Users and folders loaded ({watch.Elapsed:mm\\:ss}).");
        await ReserveIdentitiesAsync(connection, cancellationToken);

        // Chunks are built one after another (one random stream and one id sequence: the same seed gives the same data) and
        // loaded in parallel — a bulk-load transaction keeps about one SQL Server core busy. Chunks never read back what others
        // load (audit rows, stamps and hashes are computed here), so parallel loads don't block each other.
        var random = new Random(options.Seed);
        var versions = 0;
        var loaded = 0;
        var chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(options.Parallelism * 2) { SingleWriter = true });
        var loaders = Enumerable.Range(0, options.Parallelism).Select(_ => Task.Run(async () =>
        {
            await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken))
            {
                var audit = await LoadWithRetryAsync(chunk, cancellationToken);
                Interlocked.Add(ref _audit, audit);
                var done = Interlocked.Add(ref loaded, chunk.Count);
                if (done % (options.ChunkDocuments * 20) < chunk.Count || done == options.Documents)
                {
                    log($"{done} / {options.Documents} documents loaded ({watch.Elapsed:mm\\:ss}).");
                }
            }
        }, cancellationToken)).ToList();
        try
        {
            for (var first = 0; first < options.Documents; first += options.ChunkDocuments)
            {
                var count = Math.Min(options.ChunkDocuments, options.Documents - first);
                var chunk = new Chunk(new ChunkTables(), _nextDocument, count);
                for (var i = 0; i < count; i++)
                {
                    versions += AddDocument(chunk.Tables, random, first + i);
                }

                Seal(chunk.Tables);
                await chunks.Writer.WriteAsync(chunk, cancellationToken);
            }
        }
        finally
        {
            chunks.Writer.Complete();
        }

        await Task.WhenAll(loaders);
        await FinishAsync(connection, cancellationToken);
        log($"Done in {watch.Elapsed:mm\\:ss}. Phases: {string.Join(", ", _phases.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value.TotalSeconds:0.0} s"))}.");
        return new DataGenReport(options.Users, options.Folders, options.Documents, versions, _nodes, _contents, _grants, _signatures, _comments, _audit, watch.Elapsed);
    }

    /// <summary>The bulk load must come before the first reconciliation baseline (T21): refuse a database that already has one.</summary>
    private static async Task EnsureFreshAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            $"SELECT (SELECT COUNT(*) FROM [audit].[ReconciliationBaseline]), (SELECT COUNT(*) FROM [app].[User] WHERE [Login] LIKE N'{LoginPrefix}[0-9]%')",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt32(0) > 0 || reader.GetInt32(1) > 0)
        {
            throw new InvalidOperationException(
                "The database already has a reconciliation baseline or generated data. Generate into a freshly deployed database (the bulk load must precede the first baseline).");
        }
    }

    private async Task ReadNextIdsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[User];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[Folder];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[Document];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[DocumentVersion];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[DocumentNode];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[DocumentPermission];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[VersionSignature];
            SELECT ISNULL(MAX([Id]), 0) FROM [app].[Comment];
            """;
        var values = new List<int>();
        await using var command = new SqlCommand(Sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            await reader.ReadAsync(cancellationToken);
            values.Add(reader.GetInt32(0) + 1);
        }
        while (await reader.NextResultAsync(cancellationToken));

        (_nextUser, _nextFolder, _nextDocument, _nextVersion, _nextNode, _nextGrant, _nextSignature, _nextComment) =
            (values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
        await reader.CloseAsync();
        await using var log = new SqlCommand("SELECT ISNULL(MAX([Id]), 0) FROM [audit].[ChangeLog]", connection);
        _nextChangeLog = (long)(await log.ExecuteScalarAsync(cancellationToken))! + 1;
    }

    private async Task LoadUsersAndFoldersAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var users = Table("app.User", ("Id", typeof(int)), ("Login", typeof(string)), ("DisplayName", typeof(string)), ("Email", typeof(string)), ("IsAdmin", typeof(bool)), ("IsActive", typeof(bool)));
        var ids = new int[options.Users];
        for (var i = 0; i < options.Users; i++)
        {
            ids[i] = _nextUser++;
            var login = $"{LoginPrefix}{i + 1:D6}";
            users.Rows.Add(ids[i], login, $"{(i < options.Editors ? "Editor" : "Reader")} {i + 1:D6}", $"{login}@dochub.test", false, true);
        }

        _editors = ids[..options.Editors];
        _readers = ids[options.Editors..];

        var folders = Table("app.Folder", ("Id", typeof(int)), ("ParentFolderId", typeof(int)), ("Name", typeof(string)), ("SortOrder", typeof(int)), ("CreatedAt", typeof(DateTime)), ("CreatedByUserId", typeof(int)));
        var roots = Math.Max(1, options.Folders / 10);
        _folders = new int[options.Folders];
        for (var i = 0; i < options.Folders; i++)
        {
            _folders[i] = _nextFolder++;
            object parent = i < roots ? DBNull.Value : _folders[i % roots];
            folders.Rows.Add(_folders[i], parent, $"Load folder {i + 1:D3}", (i + 1) * 1024, _now.AddDays(-options.HistoryDays - 1), 1);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await CopyAsync(connection, transaction, users, cancellationToken);
        await CopyAsync(connection, transaction, folders, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>One transaction per chunk: documents with their versions, trees, contents, grants, signatures, comments, audit rows and stamps.</summary>
    private sealed record Chunk(ChunkTables Tables, int FirstDocument, int Count);

    /// <summary>Parallel chunks can deadlock on shared indexes; the victim's transaction is rolled back whole, so it is loaded again.</summary>
    private async Task<int> LoadWithRetryAsync(Chunk chunk, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A fresh session per attempt: an interrupted bulk copy leaves session state behind (IDENTITY_INSERT).
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            try
            {
                return await LoadChunkAsync(connection, chunk, cancellationToken);
            }
            catch (SqlException e) when (e.Number == 1205 && attempt < 5)
            {
                SqlConnection.ClearPool(connection);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * Random.Shared.Next(1, 4)), cancellationToken);
            }
        }
    }

    /// <summary>One transaction per chunk, all tables in foreign-key order. Returns the audit rows.</summary>
    private async Task<int> LoadChunkAsync(SqlConnection connection, Chunk chunk, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in chunk.Tables.InOrder)
        {
            await CopyAsync(connection, transaction, table, cancellationToken);
            Time(table.TableName, watch);
        }

        await transaction.CommitAsync(cancellationToken);
        Time("commit", watch);
        return chunk.Tables.ChangeLog.Rows.Count;
    }

    /// <summary>
    /// What the triggers would have written for the chunk: one audit row per entity in time order (explicit ids from one
    /// sequence; copies into a new version share a correlation id like the API's deep copy), each version's stamp, and the
    /// stamp the cached version hash is valid for.
    /// </summary>
    private void Seal(ChunkTables t)
    {
        foreach (var r in t.Audit.OrderBy(a => a.At).ThenBy(a => a.Priority).ThenBy(a => a.EntityId))
        {
            var id = _nextChangeLog++;
            t.ChangeLog.Rows.Add(id, r.At, r.Table, "I", r.EntityId, r.DocumentId, r.VersionId.HasValue ? r.VersionId.Value : DBNull.Value,
                r.Logical.HasValue ? r.Logical.Value : DBNull.Value, r.NewValues, r.UserId, "App", DbLogin,
                r.Correlation is null ? DBNull.Value : r.Correlation, r.Context);
            if (r.VersionId is { } version && r.Table is "app.DocumentVersion" or "app.DocumentNode" or "app.NodeContent" or "app.VersionSignature")
            {
                var stamp = t.Stamps.GetValueOrDefault(version);
                var content = r.Table is "app.DocumentNode" or "app.NodeContent" ? id : stamp.Content;
                t.Stamps[version] = (id, content, r.At > stamp.At ? r.At : stamp.At);
            }
        }

        foreach (var (version, stamp) in t.Stamps)
        {
            t.VersionStamps.Rows.Add(version, stamp.Last, stamp.Content, stamp.At);
            t.VersionHashRows[version]["ContentChangeLogId"] = stamp.Content;
        }
    }

    private readonly record struct AuditRecord(
        DateTime At, int Priority, string Table, int EntityId, int DocumentId, int? VersionId, Guid? Logical, string NewValues, int UserId, string? Correlation, string Context);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The audit JSON of a row, like the triggers' FOR JSON (nulls left out, timestamps without a zone).</summary>
    private static string Json(object values) => System.Text.Json.JsonSerializer.Serialize(values, JsonOptions);

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string? Stamp(DateTime? value) => value is { } v ? Stamp(v) : null;

    private sealed record ShapeNode(Guid Logical, int Parent, int Depth, int TypeId, string Title, int Template, int IntroducedIn, bool HasContent);

    private int AddDocument(ChunkTables t, Random random, int index)
    {
        var documentId = _nextDocument++;
        var owner = _editors[random.Next(_editors.Length)];
        var created = _now.AddDays(-random.Next(30, options.HistoryDays)).AddSeconds(-random.Next(86_400));
        var deleted = index % 100 == 99;
        var title = $"Load document {index + 1:D5} {Word(random)}";
        var folder = _folders[index % _folders.Length];
        t.Documents.Rows.Add(documentId, folder, title, owner, created, deleted ? _now.AddDays(-1) : DBNull.Value, deleted ? owner : DBNull.Value);
        t.Audit.Add(new(created, 0, "app.Document", documentId, documentId, null, null, Json(new { Title = title, FolderId = folder, OwnerUserId = owner }), owner, null, "DataGen"));

        // Grants: approvers and editors from the editor population, never the owner; two editors scoped to a chapter.
        var people = _editors.Where(e => e != owner).OrderBy(_ => random.Next()).Take(options.GrantsPerDocument).ToArray();
        var approvers = people.Take(3).ToArray();
        var shape = Shape(random, index);
        var chapters = shape.Where(s => s.Parent < 0).Select(s => s.Logical).ToArray();
        for (var g = 0; g < people.Length; g++)
        {
            var role = g < approvers.Length ? ApproverRole : EditorRole;
            object scope = role == EditorRole && g >= people.Length - 2 && chapters.Length > 0 ? chapters[random.Next(chapters.Length)] : DBNull.Value;
            var grantId = _nextGrant++;
            t.Grants.Rows.Add(grantId, documentId, people[g], role, scope, created.AddMinutes(g + 1), owner);
            t.Audit.Add(new(created.AddMinutes(g + 1), 2, "app.DocumentPermission", grantId, documentId, null, scope as Guid?,
                Json(new { UserId = people[g], Role = role, LogicalNodeId = scope as Guid? }), owner, null, "DataGen"));
            _grants++;
        }

        // Versions: v1…v(n-1) signed; the last one is a draft for 70 % of the documents, else signed too. The last is current.
        var versionCount = options.VersionsPerDocument;
        var span = (_now.AddDays(-1) - created) / versionCount;
        var lastIsDraft = random.NextDouble() < 0.7;
        int? previous = null;
        var number = 0;
        for (var k = 1; k <= versionCount; k++)
        {
            var versionId = _nextVersion++;
            var versionCreated = created + (span * (k - 1)) + TimeSpan.FromMinutes(5);
            var signed = k < versionCount || !lastIsDraft;
            var signedAt = versionCreated + (span * 0.5);
            var nodes = AddTree(t, shape, documentId, versionId, k, versionCreated, owner, people);
            var hash = VersionTreeHash.Compute(nodes, json => _hashes[json]);
            t.Versions.Rows.Add(versionId, documentId, signed ? Signed : Draft, signed ? ++number : DBNull.Value, previous.HasValue ? previous.Value : DBNull.Value,
                versionCreated, owner, signed ? signedAt : DBNull.Value, signed ? hash : DBNull.Value, k == versionCount);
            t.Audit.Add(new(versionCreated, 1, "app.DocumentVersion", versionId, documentId, versionId, null,
                Json(new { Status = signed ? Signed : Draft, VersionNumber = signed ? number : (int?)null, BasedOnVersionId = previous, IsCurrent = k == versionCount, SignedAt = signed ? Stamp(signedAt) : null }),
                owner, null, "DataGen"));
            t.VersionHashRows[versionId] = t.VersionHashes.Rows.Add(versionId, 0L, hash, versionCreated);
            if (signed)
            {
                foreach (var approver in approvers)
                {
                    var signatureId = _nextSignature++;
                    var at = signedAt.AddMinutes(-random.Next(1, 600));
                    var note = random.NextDouble() < 0.3 ? "Reviewed and approved." : null;
                    t.Signatures.Rows.Add(signatureId, versionId, approver, at, hash, DBNull.Value, note is null ? DBNull.Value : note);
                    t.Audit.Add(new(at, 5, "app.VersionSignature", signatureId, documentId, versionId, null, Json(new { UserId = approver, SignedAt = Stamp(at), Comment = note }), approver, null, "DataGen"));
                    _signatures++;
                }
            }

            AddComments(t, random, documentId, versionId, versionCreated, span, nodes, owner, people);
            previous = versionId;
        }

        return versionCount;
    }

    /// <summary>A document's node shape (shared by its versions through the logical ids): depth-first, depth ≤ MaxDepth.</summary>
    private List<ShapeNode> Shape(Random random, int index)
    {
        var count = NodeCount(random, index);
        var extras = Math.Max(0, (int)Math.Round(count * 0.01));
        var nodes = new List<ShapeNode>(count + (extras * options.VersionsPerDocument));
        for (var i = 0; i < count + (extras * (options.VersionsPerDocument - 1)); i++)
        {
            var introducedIn = i < count ? 1 : 2 + ((i - count) / Math.Max(1, extras));
            var parent = -1;
            if (i > 0 && random.NextDouble() > 0.06)
            {
                // Mostly below the previous node or one of its ancestors (a realistic outline), never deeper than allowed.
                parent = i - 1;
                while (parent >= 0 && (random.NextDouble() < 0.35 || nodes[parent].Depth >= options.MaxDepth - 1))
                {
                    parent = nodes[parent].Parent;
                }
            }

            var depth = parent < 0 ? 1 : nodes[parent].Depth + 1;
            var type = depth switch { 1 => i == count - 1 ? 5 : 1, 2 => 2, 3 => 3, _ => 4 };
            var title = type switch { 1 => "Chapter", 2 => "Section", 3 => "Subsection", 5 => "Appendix", _ => "Clause" } + $" {Word(random)} {i + 1}";
            nodes.Add(new ShapeNode(NewGuid(random), parent, depth, type, title, random.Next(_templates.Count), introducedIn, random.Next(10) != 0));
        }

        return nodes;
    }

    private int NodeCount(Random random, int index)
    {
        if (index % 500 == 499)
        {
            return options.MaxNodes;
        }

        // Log-normal around the average (σ = 0.7), 1…MaxNodes.
        const double Sigma = 0.7;
        var z = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var value = Math.Exp(Math.Log(options.AverageNodes) - (Sigma * Sigma / 2) + (Sigma * z));
        return (int)Math.Clamp(Math.Round(value), 1, options.MaxNodes);
    }

    /// <summary>Version k of the tree: the nodes introduced up to k, new node ids, contents that changed in some versions.</summary>
    private List<TreeHashNode> AddTree(ChunkTables t, List<ShapeNode> shape, int documentId, int versionId, int k, DateTime created, int owner, int[] people)
    {
        // Versions after the first are copies of the previous one (the API's deep copy: one correlation id).
        var copy = k > 1 ? $"datagen-copy-{versionId}" : null;
        var ids = new int[shape.Count];
        var siblings = new Dictionary<int, int>();
        var hashNodes = new List<TreeHashNode>(shape.Count);
        for (var i = 0; i < shape.Count; i++)
        {
            var node = shape[i];
            if (node.IntroducedIn > k)
            {
                continue;
            }

            ids[i] = _nextNode++;
            int? parentId = node.Parent < 0 ? null : ids[node.Parent];
            var order = siblings[node.Parent] = siblings.GetValueOrDefault(node.Parent) + 1;
            var sortOrder = order * 1024;
            var changes = Enumerable.Range(2, Math.Max(0, k - 1)).Count(j => Mix(i, j, versionId) % 20 == 0);
            var template = _templates[(node.Template + changes) % _templates.Count];
            var modifiedBy = changes > 0 ? people[Mix(i, k, 7) % people.Length] : owner;
            var modified = changes > 0 ? created.AddHours(1 + (Mix(i, k, 3) % 48)) : created;
            t.Nodes.Rows.Add(ids[i], versionId, node.Logical, parentId.HasValue ? parentId.Value : DBNull.Value, node.TypeId, node.Title, sortOrder, created, owner, modified, modifiedBy);
            t.Audit.Add(new(created, 3, "app.DocumentNode", ids[i], documentId, versionId, node.Logical,
                Json(new { Title = node.Title, ParentNodeId = parentId, NodeTypeId = node.TypeId, SortOrder = sortOrder }), owner, copy, copy is null ? "DataGen" : "CopyVersion"));
            _nodes++;
            if (node.HasContent)
            {
                t.Contents.Rows.Add(ids[i], versionId, node.Logical, (byte)1, template.Json, template.Html, template.PlainText, template.Hash, false, modified, modifiedBy);
                var copied = copy is not null && changes == 0;
                t.Audit.Add(new(modified, 4, "app.NodeContent", ids[i], documentId, versionId, node.Logical, Json(new { ContentJson = template.Json }), modifiedBy,
                    copied ? copy : null, copied ? "CopyVersion" : "DataGen"));
                foreach (var style in template.Styles)
                {
                    t.StyleUsage.Rows.Add(style, ids[i]);
                }

                _contents++;
            }

            hashNodes.Add(new TreeHashNode(ids[i], parentId, node.Logical, node.TypeId, node.Title, sortOrder, node.HasContent ? template.Json : null));
        }

        return hashNodes;
    }

    private void AddComments(ChunkTables t, Random random, int documentId, int versionId, DateTime created, TimeSpan span, List<TreeHashNode> nodes, int owner, int[] people)
    {
        var threads = new List<(int Id, object Logical)>();
        for (var c = 0; c < options.CommentsPerVersion; c++)
        {
            var author = c % 4 == 0 ? owner : people[random.Next(people.Length)];
            var at = created.AddMinutes(10 + (span.TotalMinutes * 0.4 * random.NextDouble()));
            if (threads.Count > 0 && random.NextDouble() < 0.25)
            {
                var parent = threads[random.Next(threads.Count)];
                var replyId = _nextComment++;
                var body = $"Reply: {Sentence(random)}";
                t.Comments.Rows.Add(replyId, documentId, versionId, parent.Logical, parent.Id, author, body, at, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
                t.Audit.Add(new(at, 6, "app.Comment", replyId, documentId, versionId, parent.Logical as Guid?, Json(new { Body = body, ParentCommentId = parent.Id }), author, null, "DataGen"));
            }
            else
            {
                object logical = nodes.Count > 0 && random.NextDouble() < 0.7 ? nodes[random.Next(nodes.Count)].LogicalNodeId : DBNull.Value;
                var resolved = random.NextDouble() < 0.2;
                var id = _nextComment++;
                var body = Sentence(random);
                t.Comments.Rows.Add(id, documentId, versionId, logical, DBNull.Value, author, body, at, DBNull.Value, DBNull.Value,
                    resolved ? at.AddHours(2) : DBNull.Value, resolved ? owner : DBNull.Value);
                t.Audit.Add(new(at, 6, "app.Comment", id, documentId, versionId, logical as Guid?, Json(new { Body = body }), author, null, "DataGen"));
                threads.Add((id, logical));
            }

            _comments++;
        }
    }

    private static readonly string[] IdentityTables =
        ["[app].[Document]", "[app].[DocumentVersion]", "[app].[DocumentNode]", "[app].[DocumentPermission]", "[app].[VersionSignature]", "[app].[Comment]", "[audit].[ChangeLog]"];

    /// <summary>
    /// Moves the identities of the chunk tables above every id the generator will use. An explicit id above an identity's
    /// current value updates it in the catalog and holds that lock to the end of the transaction, which would serialize the
    /// parallel chunks; below it, nothing is updated. <see cref="FinishAsync"/> sets them back to the largest id.
    /// </summary>
    private async Task ReserveIdentitiesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // An identity that never generated a value hands out the RESEED value itself next (not value + 1): FinishAsync must know.
        await using (var used = new SqlCommand(
            "SELECT QUOTENAME(OBJECT_SCHEMA_NAME([object_id])) + N'.' + QUOTENAME(OBJECT_NAME([object_id])) FROM sys.identity_columns WHERE [last_value] IS NULL",
            connection))
        await using (var reader = await used.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                _unusedIdentities.Add(reader.GetString(0));
            }
        }

        var sql = string.Concat(IdentityTables.Select(t => $"DBCC CHECKIDENT ('{t}', RESEED, {(t.Contains("ChangeLog", StringComparison.Ordinal) ? "4000000000000" : "2000000000")}) WITH NO_INFOMSGS;\n"));
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Identities continue after the largest ids; then the reconciliation baseline closes the bulk load.</summary>
    private async Task FinishAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var reseed = string.Concat(IdentityTables.Select(t =>
            $"SELECT @max = ISNULL(MAX([Id]), 0) + {(_unusedIdentities.Contains(t) ? 1 : 0)} FROM {t}; DBCC CHECKIDENT ('{t}', RESEED, @max) WITH NO_INFOMSGS;\n"));
        var sql = $"""
            DECLARE @max BIGINT;
            {reseed}
            DBCC CHECKIDENT ('[app].[User]') WITH NO_INFOMSGS;
            DBCC CHECKIDENT ('[app].[Folder]') WITH NO_INFOMSGS;
            ALTER TABLE [app].[User] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[Folder] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[Document] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[DocumentVersion] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[DocumentNode] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[NodeContent] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[ContentStyleUsage] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[DocumentPermission] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[VersionSignature] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[Comment] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[VersionContentHash] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [app].[VersionStamp] WITH CHECK CHECK CONSTRAINT ALL;
            ALTER TABLE [audit].[ChangeLog] WITH CHECK CHECK CONSTRAINT ALL;
            INSERT INTO [audit].[ReconciliationBaseline] ([Reason])
            VALUES (N'T19 data generator: bulk load (scale {options.Scale.ToString(CultureInfo.InvariantCulture)}, seed {options.Seed}) bypassed the audit triggers.');
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyAsync(SqlConnection connection, SqlTransaction transaction, DataTable table, CancellationToken cancellationToken)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        // No per-row constraint checks: parallel chunks would deadlock in the foreign-key checks. FinishAsync validates every
        // constraint of the loaded tables once, so they stay trusted and a generator bug still fails the run.
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, transaction)
        {
            DestinationTableName = string.Join('.', table.TableName.Split('.').Select(p => $"[{p}]")),
            BulkCopyTimeout = 0,
            // Below the lock-escalation threshold (5 000 locks per statement): a table lock would make the parallel chunks wait.
            BatchSize = 2000,
        };
        foreach (DataColumn column in table.Columns)
        {
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await copy.WriteToServerAsync(table, cancellationToken);
    }

    private static DataTable Table(string name, params (string Name, Type Type)[] columns)
    {
        var table = new DataTable(name) { Locale = CultureInfo.InvariantCulture };
        foreach (var (column, type) in columns)
        {
            table.Columns.Add(column, type);
        }

        return table;
    }

    private sealed class ChunkTables
    {
        public DataTable Documents { get; } = Table("app.Document", ("Id", typeof(int)), ("FolderId", typeof(int)), ("Title", typeof(string)), ("OwnerUserId", typeof(int)),
            ("CreatedAt", typeof(DateTime)), ("DeletedAt", typeof(DateTime)), ("DeletedByUserId", typeof(int)));

        public DataTable Versions { get; } = Table("app.DocumentVersion", ("Id", typeof(int)), ("DocumentId", typeof(int)), ("Status", typeof(byte)), ("VersionNumber", typeof(int)),
            ("BasedOnVersionId", typeof(int)), ("CreatedAt", typeof(DateTime)), ("CreatedByUserId", typeof(int)), ("SignedAt", typeof(DateTime)),
            ("SignedContentHash", typeof(byte[])), ("IsCurrent", typeof(bool)));

        public DataTable Nodes { get; } = Table("app.DocumentNode", ("Id", typeof(int)), ("DocumentVersionId", typeof(int)), ("LogicalNodeId", typeof(Guid)), ("ParentNodeId", typeof(int)),
            ("NodeTypeId", typeof(int)), ("Title", typeof(string)), ("SortOrder", typeof(int)), ("CreatedAt", typeof(DateTime)), ("CreatedByUserId", typeof(int)),
            ("ModifiedAt", typeof(DateTime)), ("ModifiedByUserId", typeof(int)));

        public DataTable Contents { get; } = Table("app.NodeContent", ("NodeId", typeof(int)), ("DocumentVersionId", typeof(int)), ("LogicalNodeId", typeof(Guid)),
            ("SchemaVersion", typeof(byte)), ("ContentJson", typeof(string)), ("ContentHtml", typeof(string)), ("PlainText", typeof(string)), ("ContentHash", typeof(byte[])),
            ("DerivedStale", typeof(bool)), ("ModifiedAt", typeof(DateTime)), ("ModifiedByUserId", typeof(int)));

        public DataTable StyleUsage { get; } = Table("app.ContentStyleUsage", ("StyleId", typeof(string)), ("NodeId", typeof(int)));

        public DataTable Grants { get; } = Table("app.DocumentPermission", ("Id", typeof(int)), ("DocumentId", typeof(int)), ("UserId", typeof(int)), ("Role", typeof(byte)),
            ("LogicalNodeId", typeof(Guid)), ("GrantedAt", typeof(DateTime)), ("GrantedByUserId", typeof(int)));

        public DataTable Signatures { get; } = Table("app.VersionSignature", ("Id", typeof(int)), ("DocumentVersionId", typeof(int)), ("UserId", typeof(int)), ("SignedAt", typeof(DateTime)),
            ("ContentHash", typeof(byte[])), ("WithdrawnAt", typeof(DateTime)), ("Comment", typeof(string)));

        public DataTable Comments { get; } = Table("app.Comment", ("Id", typeof(int)), ("DocumentId", typeof(int)), ("DocumentVersionId", typeof(int)), ("LogicalNodeId", typeof(Guid)),
            ("ParentCommentId", typeof(int)), ("AuthorUserId", typeof(int)), ("Body", typeof(string)), ("CreatedAt", typeof(DateTime)), ("EditedAt", typeof(DateTime)),
            ("DeletedAt", typeof(DateTime)), ("ResolvedAt", typeof(DateTime)), ("ResolvedByUserId", typeof(int)));

        public DataTable VersionHashes { get; } = Table("app.VersionContentHash", ("DocumentVersionId", typeof(int)), ("ContentChangeLogId", typeof(long)),
            ("ContentHash", typeof(byte[])), ("ComputedAt", typeof(DateTime)));

        public DataTable ChangeLog { get; } = Table("audit.ChangeLog", ("Id", typeof(long)), ("ChangedAt", typeof(DateTime)), ("TableName", typeof(string)), ("Operation", typeof(string)),
            ("EntityId", typeof(int)), ("DocumentId", typeof(int)), ("DocumentVersionId", typeof(int)), ("LogicalNodeId", typeof(Guid)), ("NewValues", typeof(string)),
            ("UserId", typeof(int)), ("Source", typeof(string)), ("DbLogin", typeof(string)), ("CorrelationId", typeof(string)), ("OperationContext", typeof(string)));

        public DataTable VersionStamps { get; } = Table("app.VersionStamp", ("DocumentVersionId", typeof(int)), ("LastChangeLogId", typeof(long)), ("ContentChangeLogId", typeof(long)),
            ("LastChangedAt", typeof(DateTime)));

        public List<AuditRecord> Audit { get; } = [];

        public Dictionary<int, (long Last, long Content, DateTime At)> Stamps { get; } = [];

        public Dictionary<int, DataRow> VersionHashRows { get; } = [];

        /// <summary>Foreign-key order.</summary>
        public IEnumerable<DataTable> InOrder => [Documents, Versions, Nodes, Contents, StyleUsage, Grants, Signatures, Comments, VersionHashes, VersionStamps, ChangeLog];
    }

    private static int Mix(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)((a * 73856093) ^ (b * 19349663) ^ (c * 83492791));
            h ^= h >> 13;
            h *= 0x5bd1e995;
            return (int)(h & 0x7fffffff);
        }
    }

    private static Guid NewGuid(Random random)
    {
        Span<byte> bytes = stackalloc byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static readonly string[] Vocabulary = ["scope", "terms", "quality", "safety", "supply", "service", "finance", "privacy", "security", "training", "records", "audit"];

    private static string Word(Random random) => Vocabulary[random.Next(Vocabulary.Length)];

    private static string Sentence(Random random) =>
        string.Join(' ', Enumerable.Range(0, random.Next(6, 16)).Select(_ => Word(random))) is var s ? char.ToUpperInvariant(s[0]) + s[1..] + "." : "";
}
