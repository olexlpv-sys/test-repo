using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.Chunkers;
using DocHub.Domain.Content;

namespace DocHub.Infrastructure.Content.Diff;

/// <summary>Who made a change (track changes, T11 §2a); absent in a plain two-state diff.</summary>
public sealed record DiffAuthor(long EntryId, int? UserId, string? DisplayName, string Source, string? Ticket, DateTime ChangedAt);

/// <summary>
/// One run of a text block diff: <c>equal</c>, <c>insert</c>, <c>delete</c> or <c>format</c> (same text, other formatting).
/// In track changes, <c>by</c> made the change; <c>formatBy</c> reformatted text another author inserted within the range.
/// </summary>
public sealed record DiffOp(string Op, string Text, IReadOnlyList<string>? Changes = null, DiffAuthor? By = null, DiffAuthor? FormatBy = null);

/// <summary>A table cell of a diff: its runs (paragraphs joined by line breaks) and its blocks.</summary>
public sealed record DiffCell(string Status, IReadOnlyList<DiffOp> Ops, IReadOnlyList<DiffBlock> Blocks);

/// <summary>
/// A block of a diff: <c>status</c> ∈ equal, changed, inserted, deleted. Text blocks carry <c>ops</c>, tables <c>rows</c>,
/// other containers (lists, list items) <c>children</c>; <c>changes</c> lists block formatting changes (style, alignment…).
/// </summary>
public sealed record DiffBlock(
    string Type, string Status, IReadOnlyList<string>? Changes = null, IReadOnlyList<DiffOp>? Ops = null, IReadOnlyList<DiffBlock>? Children = null,
    IReadOnlyList<IReadOnlyList<DiffCell>>? Rows = null);

public sealed record DiffStats(int Inserted, int Deleted);

/// <summary>A content diff: the block/ops structure, ready-to-render HTML (<c>ins</c>/<c>del</c> with the original styles) and word counts.</summary>
public sealed record ContentDiff(IReadOnlyList<DiffBlock> Blocks, string Html, DiffStats Stats);

/// <summary>The diff engine shared by history (T11) and version comparison (T12).</summary>
public interface IContentDiffService
{
    /// <summary>Diff of two Content Schema v1 documents (stored JSON; unreadable content counts as empty).</summary>
    ContentDiff Diff(string oldJson, string newJson);
}

/// <summary>
/// Tree diff of content JSON: children are aligned by LCS on their canonical JSON, unmatched neighbours of compatible types are
/// paired and diffed recursively; paired text blocks get a word-level diff (DiffPlex) in which equal words with other marks are
/// formatting changes, not delete + insert. The result is also built as a content document with diff marks, rendered by
/// <see cref="ContentHtmlRenderer"/>.
/// </summary>
public sealed partial class ContentDiffService : IContentDiffService
{
    private static readonly HashSet<string> TextBlocks = ["paragraph", "heading"];

    public ContentDiff Diff(string oldJson, string newJson)
    {
        var oldDoc = ParseDocument(oldJson);
        var newDoc = ParseDocument(newJson);
        var stats = new Counter();
        var (blocks, nodes) = DiffChildren(Children(oldDoc), Children(newDoc), stats);
        var document = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray([.. nodes]) };
        return new ContentDiff(blocks, ContentHtmlRenderer.Render(document.ToJsonString()).Html, new DiffStats(stats.Inserted, stats.Deleted));
    }

    /// <summary>
    /// A document from stored JSON: lone surrogates repaired, duplicate keys resolved (the last one wins — JSON a script
    /// stored may have them), anything unreadable → empty. The tree is built eagerly, so reading it never throws later.
    /// </summary>
    public static JsonObject ParseDocument(string? json)
    {
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                using var document = JsonDocument.Parse(ContentSchema.RepairLoneSurrogates(json), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
                if (ToNode(document.RootElement) is JsonObject root)
                {
                    return root;
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
            }
        }

        return new JsonObject { ["type"] = "doc", ["content"] = new JsonArray() };
    }

    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = ToNode(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ToNode(item));
                }

                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());
            case JsonValueKind.Number:
                // Kept as written (1e400 has no double), like the stored text.
                return JsonValue.Create(element.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private static List<JsonObject> Children(JsonObject node) =>
        node["content"] is JsonArray content ? content.OfType<JsonObject>().Where(c => TypeOf(c) is not null).ToList() : [];

    private static string? TypeOf(JsonObject node) =>
        node["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;

    private static string Key(JsonObject node) => node.ToJsonString();

    /// <summary>Aligns two child lists and diffs them: equal children kept, compatible neighbours paired, the rest inserted/deleted.</summary>
    private (List<DiffBlock> Blocks, List<JsonNode> Nodes) DiffChildren(List<JsonObject> olds, List<JsonObject> news, Counter stats)
    {
        var blocks = new List<DiffBlock>();
        var nodes = new List<JsonNode>();
        foreach (var (oldRange, newRange, equal) in Align(olds.Select(Key).ToList(), news.Select(Key).ToList()))
        {
            if (equal)
            {
                for (var i = 0; i < newRange.Count; i++)
                {
                    blocks.Add(Whole(news[newRange.Start + i], "equal", stats));
                    nodes.Add(news[newRange.Start + i].DeepClone());
                }

                continue;
            }

            // A changed stretch: pair similar compatible children (order kept), the remainder is deleted / inserted.
            foreach (var (o, n) in Pair(olds.GetRange(oldRange.Start, oldRange.Count), news.GetRange(newRange.Start, newRange.Count)))
            {
                if (o is not null && n is not null)
                {
                    var (block, node) = DiffPair(o, n, stats);
                    blocks.Add(block);
                    nodes.Add(node);
                }
                else if (o is not null)
                {
                    blocks.Add(Whole(o, "deleted", stats));
                    nodes.Add(Marked(o, "diffDelete", "deleted"));
                }
                else
                {
                    blocks.Add(Whole(n!, "inserted", stats));
                    nodes.Add(Marked(n!, "diffInsert", "inserted"));
                }
            }
        }

        return (blocks, nodes);
    }

    /// <summary>Minimum word similarity for two changed blocks to be shown as one edited block.</summary>
    private const double PairThreshold = 0.3;

    /// <summary>
    /// Order-preserving alignment of a changed stretch that maximizes the word similarity of paired blocks (unpaired ones
    /// are deleted before inserted). Very long stretches fall back to pairing by position.
    /// </summary>
    private static List<(JsonObject? Old, JsonObject? New)> Pair(List<JsonObject> olds, List<JsonObject> news)
    {
        var result = new List<(JsonObject?, JsonObject?)>();
        if ((long)olds.Count * news.Count > 250_000)
        {
            for (var i = 0; i < Math.Max(olds.Count, news.Count); i++)
            {
                var (o, n) = (i < olds.Count ? olds[i] : null, i < news.Count ? news[i] : null);
                if (o is not null && n is not null && Compatible(o, n))
                {
                    result.Add((o, n));
                }
                else
                {
                    if (o is not null)
                    {
                        result.Add((o, null));
                    }

                    if (n is not null)
                    {
                        result.Add((null, n));
                    }
                }
            }

            return result;
        }

        var oldWords = olds.Select(WordSet).ToList();
        var newWords = news.Select(WordSet).ToList();
        var score = new double[olds.Count + 1, news.Count + 1];
        for (var i = 1; i <= olds.Count; i++)
        {
            for (var j = 1; j <= news.Count; j++)
            {
                var best = Math.Max(score[i - 1, j], score[i, j - 1]);
                var similarity = Compatible(olds[i - 1], news[j - 1]) ? Similarity(oldWords[i - 1], newWords[j - 1]) : -1;
                score[i, j] = similarity >= PairThreshold ? Math.Max(best, score[i - 1, j - 1] + similarity) : best;
            }
        }

        var (a, b) = (olds.Count, news.Count);
        var reversed = new List<(JsonObject?, JsonObject?)>();
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && Compatible(olds[a - 1], news[b - 1]) && Similarity(oldWords[a - 1], newWords[b - 1]) is var sim && sim >= PairThreshold
                && Math.Abs(score[a, b] - (score[a - 1, b - 1] + sim)) < 1e-9)
            {
                reversed.Add((olds[--a], news[--b]));
            }
            else if (b > 0 && (a == 0 || Math.Abs(score[a, b] - score[a, b - 1]) < 1e-9))
            {
                reversed.Add((null, news[--b]));
            }
            else
            {
                reversed.Add((olds[--a], null));
            }
        }

        reversed.Reverse();
        // Within a run of unpaired blocks, deletions first.
        for (var i = 0; i < reversed.Count;)
        {
            if (reversed[i].Item1 is not null && reversed[i].Item2 is not null)
            {
                result.Add(reversed[i++]);
                continue;
            }

            var run = new List<(JsonObject?, JsonObject?)>();
            while (i < reversed.Count && (reversed[i].Item1 is null || reversed[i].Item2 is null))
            {
                run.Add(reversed[i++]);
            }

            result.AddRange(run.Where(r => r.Item1 is not null));
            result.AddRange(run.Where(r => r.Item2 is not null));
        }

        return result;
    }

    private static HashSet<string> WordSet(JsonObject node)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(JsonObject current)
        {
            if (TypeOf(current) == "text" && current["text"] is JsonValue t && t.TryGetValue<string>(out var text))
            {
                words.UnionWith(WordPattern().Matches(text).Select(m => m.Value));
            }

            foreach (var child in current["content"] as JsonArray ?? [])
            {
                if (child is JsonObject o)
                {
                    Walk(o);
                }
            }
        }

        Walk(node);
        return words;
    }

    /// <summary>Jaccard similarity of word sets; two blocks without words (rules, breaks, empty paragraphs) are alike.</summary>
    private static double Similarity(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 && b.Count == 0 ? 1 : (double)a.Count(b.Contains) / a.Union(b, StringComparer.OrdinalIgnoreCase).Count();

    private static bool Compatible(JsonObject a, JsonObject b)
    {
        var (ta, tb) = (TypeOf(a)!, TypeOf(b)!);
        return ta == tb || (TextBlocks.Contains(ta) && TextBlocks.Contains(tb)) || (ta is "bulletList" or "orderedList" && tb is "bulletList" or "orderedList")
            || (ta is "tableCell" or "tableHeader" && tb is "tableCell" or "tableHeader");
    }

    private (DiffBlock Block, JsonNode Node) DiffPair(JsonObject oldNode, JsonObject newNode, Counter stats)
    {
        var type = TypeOf(newNode)!;
        var changes = AttributeChanges(oldNode, newNode);
        var result = new JsonObject { ["type"] = type };
        if (newNode["attrs"] is JsonObject attrs)
        {
            result["attrs"] = attrs.DeepClone();
        }

        if (TextBlocks.Contains(type))
        {
            var (ops, inline) = InlineDiff.Diff(Inline(oldNode), Inline(newNode), stats);
            result["content"] = new JsonArray([.. inline]);
            var changed = changes.Count > 0 || ops.Any(op => op.Op != "equal");
            SetDiffAttr(result, changed ? "changed" : "equal");
            return (new DiffBlock(type, changed ? "changed" : "equal", changes.Count > 0 ? changes : null, ops), result);
        }

        if (type == "table")
        {
            var (rows, rowNodes) = DiffRows(Children(oldNode), Children(newNode), stats);
            result["content"] = new JsonArray([.. rowNodes]);
            var changed = changes.Count > 0 || rows.Any(r => r.Any(c => c.Status != "equal"));
            SetDiffAttr(result, changed ? "changed" : "equal");
            return (new DiffBlock(type, changed ? "changed" : "equal", changes.Count > 0 ? changes : null, Rows: rows), result);
        }

        var (children, childNodes) = DiffChildren(Children(oldNode), Children(newNode), stats);
        if (childNodes.Count > 0 || newNode["content"] is not null)
        {
            result["content"] = new JsonArray([.. childNodes]);
        }

        var status = changes.Count > 0 || children.Any(c => c.Status != "equal") ? "changed" : "equal";
        return (new DiffBlock(type, status, changes.Count > 0 ? changes : null, Children: children.Count > 0 ? children : null), result);
    }

    /// <summary>Rows aligned like other children; cells of paired rows are paired by position.</summary>
    private (List<IReadOnlyList<DiffCell>> Rows, List<JsonNode> Nodes) DiffRows(List<JsonObject> olds, List<JsonObject> news, Counter stats)
    {
        var rows = new List<IReadOnlyList<DiffCell>>();
        var nodes = new List<JsonNode>();
        void Row(JsonObject? oldRow, JsonObject? newRow)
        {
            var row = new JsonObject { ["type"] = "tableRow" };
            if ((newRow ?? oldRow)!["attrs"] is JsonObject attrs)
            {
                row["attrs"] = attrs.DeepClone();
            }

            var oldCells = oldRow is null ? [] : Children(oldRow);
            var newCells = newRow is null ? [] : Children(newRow);
            var cells = new List<DiffCell>();
            var cellNodes = new JsonArray();
            for (var i = 0; i < Math.Max(oldCells.Count, newCells.Count); i++)
            {
                var (o, n) = (i < oldCells.Count ? oldCells[i] : null, i < newCells.Count ? newCells[i] : null);
                if (o is not null && n is not null)
                {
                    var (blocks, content) = DiffChildren(Children(o), Children(n), stats);
                    var changed = AttributeChanges(o, n).Count > 0 || blocks.Any(b => b.Status != "equal");
                    cells.Add(new DiffCell(changed ? "changed" : "equal", CellOps(blocks), blocks));
                    var cell = new JsonObject { ["type"] = TypeOf(n) };
                    if (n["attrs"] is JsonObject cellAttrs)
                    {
                        cell["attrs"] = cellAttrs.DeepClone();
                    }

                    cell["content"] = new JsonArray([.. content]);
                    SetDiffAttr(cell, changed ? "changed" : "equal");
                    cellNodes.Add(cell);
                }
                else
                {
                    var whole = (o ?? n)!;
                    var status = o is null ? "inserted" : "deleted";
                    var blocks = Children(whole).Select(b => Whole(b, status, stats)).ToList();
                    cells.Add(new DiffCell(status, CellOps(blocks), blocks));
                    cellNodes.Add(Marked(whole, o is null ? "diffInsert" : "diffDelete", status));
                }
            }

            row["content"] = cellNodes;
            rows.Add(cells);
            nodes.Add(row);
        }

        foreach (var (oldRange, newRange, equal) in Align(olds.Select(Key).ToList(), news.Select(Key).ToList()))
        {
            if (equal)
            {
                for (var i = 0; i < newRange.Count; i++)
                {
                    Row(olds[oldRange.Start + i], news[newRange.Start + i]);
                }

                continue;
            }

            // Changed rows: pair by position (a cell edit keeps its row), extra rows are inserted/deleted.
            for (var i = 0; i < Math.Max(oldRange.Count, newRange.Count); i++)
            {
                Row(i < oldRange.Count ? olds[oldRange.Start + i] : null, i < newRange.Count ? news[newRange.Start + i] : null);
            }
        }

        return (rows, nodes);
    }

    private static List<DiffOp> CellOps(IReadOnlyList<DiffBlock> blocks)
    {
        var ops = new List<DiffOp>();
        void Collect(DiffBlock block)
        {
            if (block.Ops is { } blockOps)
            {
                if (ops.Count > 0)
                {
                    ops.Add(new DiffOp(block.Status == "inserted" ? "insert" : block.Status == "deleted" ? "delete" : "equal", "\n"));
                }

                ops.AddRange(blockOps);
            }

            foreach (var child in block.Children ?? [])
            {
                Collect(child);
            }
        }

        foreach (var block in blocks)
        {
            Collect(block);
        }

        return ops;
    }

    /// <summary>A block reported as a whole (equal, inserted or deleted).</summary>
    private static DiffBlock Whole(JsonObject node, string status, Counter stats)
    {
        var type = TypeOf(node)!;
        var op = status switch { "inserted" => "insert", "deleted" => "delete", _ => "equal" };
        if (TextBlocks.Contains(type))
        {
            var text = InlineDiff.Text(Inline(node));
            stats.Count(op, text);
            return new DiffBlock(type, status, Ops: text.Length == 0 ? [] : [new DiffOp(op, text)]);
        }

        if (type == "table")
        {
            return new DiffBlock(type, status, Rows: Children(node).Select(row => (IReadOnlyList<DiffCell>)Children(row).Select(cell =>
            {
                var blocks = Children(cell).Select(b => Whole(b, status, stats)).ToList();
                return new DiffCell(status, CellOps(blocks), blocks);
            }).ToList()).ToList());
        }

        var children = Children(node).Select(c => Whole(c, status, stats)).ToList();
        return new DiffBlock(type, status, Children: children.Count > 0 ? children : null);
    }

    /// <summary>A copy of an inserted/deleted subtree with every text run marked, and the block flagged for the renderer.</summary>
    private static JsonObject Marked(JsonObject node, string mark, string status)
    {
        var copy = (JsonObject)node.DeepClone();
        void Walk(JsonObject current)
        {
            if (TypeOf(current) == "text")
            {
                var marks = current["marks"] as JsonArray ?? [];
                marks.Add(new JsonObject { ["type"] = mark });
                current["marks"] = marks;
                return;
            }

            if (TypeOf(current) != "hardBreak")
            {
                SetDiffAttr(current, status);
            }

            foreach (var child in current["content"] as JsonArray ?? [])
            {
                if (child is JsonObject o)
                {
                    Walk(o);
                }
            }
        }

        Walk(copy);
        return copy;
    }

    private static void SetDiffAttr(JsonObject node, string status)
    {
        if (status == "equal")
        {
            return;
        }

        var attrs = node["attrs"] as JsonObject ?? [];
        attrs["diff"] = status;
        node["attrs"] = attrs;
    }

    private static List<InlineDiff.Piece> Inline(JsonObject block)
    {
        var pieces = new List<InlineDiff.Piece>();
        foreach (var child in block["content"] as JsonArray ?? [])
        {
            if (child is not JsonObject inline)
            {
                continue;
            }

            switch (TypeOf(inline))
            {
                case "text" when inline["text"] is JsonValue t && t.TryGetValue<string>(out var text):
                    pieces.Add(new InlineDiff.Piece(text, InlineDiff.MarksKey(inline["marks"] as JsonArray)));
                    break;
                case "hardBreak":
                    pieces.Add(new InlineDiff.Piece("\n", InlineDiff.BreakKey));
                    break;
            }
        }

        return pieces;
    }

    /// <summary>Block formatting changes: type (paragraph ↔ heading, list kind), style, alignment and every other attribute.</summary>
    private static List<string> AttributeChanges(JsonObject oldNode, JsonObject newNode)
    {
        var changes = new List<string>();
        var (oldType, newType) = (TypeOf(oldNode)!, TypeOf(newNode)!);
        if (oldType != newType)
        {
            changes.Add($"type {oldType}→{newType}");
        }

        var oldAttrs = oldNode["attrs"] as JsonObject ?? [];
        var newAttrs = newNode["attrs"] as JsonObject ?? [];
        foreach (var name in oldAttrs.Select(a => a.Key).Union(newAttrs.Select(a => a.Key)).Where(n => n != "diff").Order(StringComparer.Ordinal))
        {
            var (o, n) = (oldAttrs[name], newAttrs[name]);
            if (!JsonNode.DeepEquals(o, n))
            {
                changes.Add($"{name} {Describe(o)}→{Describe(n)}");
            }
        }

        return changes;
    }

    internal static string Describe(JsonNode? value) => value switch
    {
        null => "none",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => value.ToJsonString(),
    };

    /// <summary>Equal and changed stretches of two key lists (DiffPlex on one line per key).</summary>
    private static List<((int Start, int Count) Old, (int Start, int Count) New, bool Equal)> Align(List<string> oldKeys, List<string> newKeys)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string Lines(List<string> keys) => string.Join('\n', keys.Select(k => ids.TryGetValue(k, out var id) ? id : ids[k] = ids.Count));
        var diff = Differ.Instance.CreateDiffs(Lines(oldKeys), Lines(newKeys), false, false, LineChunker.Instance);
        var old = oldKeys.Count == 0 ? 0 : diff.PiecesOld.Count;
        var neu = newKeys.Count == 0 ? 0 : diff.PiecesNew.Count;
        var result = new List<((int, int), (int, int), bool)>();
        var (a, b) = (0, 0);
        foreach (var block in diff.DiffBlocks)
        {
            if (block.DeleteStartA > a)
            {
                result.Add(((a, block.DeleteStartA - a), (b, block.DeleteStartA - a), true));
            }

            // Empty lists produce one empty piece; clamp to the real counts.
            var deleteCount = Math.Min(block.DeleteCountA, oldKeys.Count - block.DeleteStartA);
            var insertCount = Math.Min(block.InsertCountB, newKeys.Count - block.InsertStartB);
            result.Add(((block.DeleteStartA, Math.Max(deleteCount, 0)), (block.InsertStartB, Math.Max(insertCount, 0)), false));
            a = block.DeleteStartA + block.DeleteCountA;
            b = block.InsertStartB + block.InsertCountB;
        }

        if (a < old)
        {
            result.Add(((a, old - a), (b, neu - b), true));
        }

        return result;
    }

    internal sealed class Counter
    {
        public int Inserted { get; private set; }

        public int Deleted { get; private set; }

        public void Count(string op, string text)
        {
            var words = WordPattern().Count(text);
            if (op == "insert")
            {
                Inserted += words;
            }
            else if (op == "delete")
            {
                Deleted += words;
            }
        }
    }

    [GeneratedRegex(@"\w+")]
    internal static partial Regex WordPattern();
}

/// <summary>Word-level diff of the runs of one text block, with formatting changes of equal words reported separately.</summary>
internal static partial class InlineDiff
{
    /// <summary>A run: text with the key of its marks (canonical JSON of the sorted marks; <see cref="BreakKey"/> for a line break).</summary>
    public sealed record Piece(string Text, string Marks);

    public const string BreakKey = "\u0001br";

    public static string Text(IEnumerable<Piece> pieces) => string.Concat(pieces.Select(p => p.Text));

    public static string MarksKey(JsonArray? marks) =>
        marks is null || marks.Count == 0
            ? ""
            : new JsonArray([.. marks.OfType<JsonObject>().OrderBy(m => m["type"]?.ToJsonString(), StringComparer.Ordinal).Select(m => m.DeepClone())]).ToJsonString();

    /// <summary>Words, whitespace runs and single other characters — the units of the diff.</summary>
    public static string[] Tokenize(string text) => TokenPattern().Matches(text).Select(m => m.Value).ToArray();

    private sealed class TokenChunker : DiffPlex.IChunker
    {
        public static readonly TokenChunker Instance = new();

        public IReadOnlyList<string> Chunk(string text) => Tokenize(text);
    }

    public static (List<DiffOp> Ops, List<JsonNode> Inline) Diff(List<Piece> olds, List<Piece> news, ContentDiffService.Counter stats)
    {
        var oldText = Text(olds);
        var newText = Text(news);
        var oldMarks = CharMarks(olds);
        var newMarks = CharMarks(news);
        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);
        var oldStarts = Starts(oldTokens);
        var newStarts = Starts(newTokens);

        var segments = new List<(string Op, string Text, string Marks, IReadOnlyList<string>? Changes)>();
        void Equal(int oldToken, int newToken)
        {
            // Per character: same marks → equal, other marks → a formatting change (text unchanged).
            var (o, n) = (oldStarts[oldToken], newStarts[newToken]);
            for (var i = 0; i < newTokens[newToken].Length; i++)
            {
                var (om, nm) = (oldMarks[o + i], newMarks[n + i]);
                var c = newText[n + i].ToString();
                segments.Add(om == nm ? ("equal", c, nm, null) : ("format", c, nm, FormatChanges(om, nm)));
            }
        }

        void Range(string op, string text, List<string> marks, int start, int length)
        {
            for (var i = 0; i < length; i++)
            {
                segments.Add((op, text[start + i].ToString(), marks[start + i], null));
            }
        }

        var diff = Differ.Instance.CreateDiffs(oldText, newText, false, false, TokenChunker.Instance);
        var (a, b) = (0, 0);
        foreach (var block in diff.DiffBlocks)
        {
            for (; a < block.DeleteStartA; a++, b++)
            {
                Equal(a, b);
            }

            for (var i = 0; i < block.DeleteCountA && block.DeleteStartA + i < oldTokens.Length; i++)
            {
                var t = block.DeleteStartA + i;
                Range("delete", oldText, oldMarks, oldStarts[t], oldTokens[t].Length);
            }

            for (var i = 0; i < block.InsertCountB && block.InsertStartB + i < newTokens.Length; i++)
            {
                var t = block.InsertStartB + i;
                Range("insert", newText, newMarks, newStarts[t], newTokens[t].Length);
            }

            a = block.DeleteStartA + block.DeleteCountA;
            b = block.InsertStartB + block.InsertCountB;
        }

        for (; a < oldTokens.Length && b < newTokens.Length; a++, b++)
        {
            Equal(a, b);
        }

        // Merge characters into runs.
        var ops = new List<DiffOp>();
        var inline = new List<JsonNode>();
        foreach (var run in Merge(segments))
        {
            stats.Count(run.Op, run.Text);
            if (ops.Count > 0 && ops[^1].Op == run.Op && SameChanges(ops[^1].Changes, run.Changes))
            {
                ops[^1] = ops[^1] with { Text = ops[^1].Text + run.Text };
            }
            else
            {
                ops.Add(new DiffOp(run.Op, run.Text, run.Changes));
            }

            inline.AddRange(Nodes(run));
        }

        return (ops, inline);
    }

    private static IEnumerable<(string Op, string Text, string Marks, IReadOnlyList<string>? Changes)> Merge(
        List<(string Op, string Text, string Marks, IReadOnlyList<string>? Changes)> segments)
    {
        var builder = new StringBuilder();
        (string Op, string Text, string Marks, IReadOnlyList<string>? Changes)? current = null;
        foreach (var s in segments)
        {
            if (current is { } c && c.Op == s.Op && c.Marks == s.Marks && SameChanges(c.Changes, s.Changes))
            {
                builder.Append(s.Text);
                continue;
            }

            if (current is { } done)
            {
                yield return done with { Text = builder.ToString() };
            }

            builder.Clear().Append(s.Text);
            current = s;
        }

        if (current is { } last)
        {
            yield return last with { Text = builder.ToString() };
        }
    }

    private static bool SameChanges(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>Content nodes of a run: text with its own marks plus the diff mark; line breaks as hard breaks.</summary>
    private static IEnumerable<JsonNode> Nodes((string Op, string Text, string Marks, IReadOnlyList<string>? Changes) run)
    {
        var diffMark = run.Op switch { "insert" => "diffInsert", "delete" => "diffDelete", "format" => "diffFormat", _ => null };
        if (run.Marks == BreakKey)
        {
            foreach (var _ in run.Text)
            {
                if (run.Op == "delete")
                {
                    yield return new JsonObject { ["type"] = "text", ["text"] = "↵", ["marks"] = new JsonArray(new JsonObject { ["type"] = "diffDelete" }) };
                }
                else
                {
                    yield return new JsonObject { ["type"] = "hardBreak" };
                }
            }

            yield break;
        }

        var marks = run.Marks.Length == 0 ? [] : (JsonArray)JsonNode.Parse(run.Marks)!;
        if (diffMark is not null)
        {
            marks.Add(new JsonObject { ["type"] = diffMark });
        }

        var node = new JsonObject { ["type"] = "text", ["text"] = run.Text };
        if (marks.Count > 0)
        {
            node["marks"] = marks;
        }

        yield return node;
    }

    private static List<string> CharMarks(List<Piece> pieces)
    {
        var marks = new List<string>();
        foreach (var piece in pieces)
        {
            marks.AddRange(Enumerable.Repeat(piece.Marks, piece.Text.Length));
        }

        return marks;
    }

    private static int[] Starts(string[] tokens)
    {
        var starts = new int[tokens.Length];
        for (int i = 0, position = 0; i < tokens.Length; position += tokens[i].Length, i++)
        {
            starts[i] = position;
        }

        return starts;
    }

    /// <summary>Human-readable formatting changes between two mark sets ("bold added", "fontSize 11pt→14pt").</summary>
    public static List<string> FormatChanges(string oldKey, string newKey)
    {
        static Dictionary<string, JsonObject?> Parse(string key) =>
            key.Length == 0 || key == BreakKey
                ? []
                : ((JsonArray)JsonNode.Parse(key)!).OfType<JsonObject>()
                    .GroupBy(m => ContentDiffService.Describe(m["type"]))
                    .ToDictionary(g => g.Key, g => g.First()["attrs"] as JsonObject, StringComparer.Ordinal);

        var (olds, news) = (Parse(oldKey), Parse(newKey));
        var changes = new List<string>();
        foreach (var type in olds.Keys.Union(news.Keys).Order(StringComparer.Ordinal))
        {
            var (hadOld, hasNew) = (olds.TryGetValue(type, out var o), news.TryGetValue(type, out var n));
            if (!hadOld)
            {
                changes.Add(n is null || n.Count == 0 ? $"{type} added" : $"{type} added ({string.Join(", ", n.Select(a => $"{a.Key} {Value(a.Key, a.Value)}"))})");
            }
            else if (!hasNew)
            {
                changes.Add($"{type} removed");
            }
            else
            {
                var oldAttrs = o ?? [];
                var newAttrs = n ?? [];
                foreach (var name in oldAttrs.Select(a => a.Key).Union(newAttrs.Select(a => a.Key)).Order(StringComparer.Ordinal))
                {
                    if (!JsonNode.DeepEquals(oldAttrs[name], newAttrs[name]))
                    {
                        changes.Add($"{name} {Value(name, oldAttrs[name])}→{Value(name, newAttrs[name])}");
                    }
                }
            }
        }

        return changes;
    }

    private static string Value(string name, JsonNode? value) =>
        name == "fontSize" && value is JsonValue v && v.TryGetValue<int>(out var halfPoints)
            ? (halfPoints / 2m).ToString("0.#", CultureInfo.InvariantCulture) + "pt"
            : ContentDiffService.Describe(value);

    // A surrogate pair (emoji, rare CJK) is one unit, never split.
    [GeneratedRegex(@"\w+|\s+|[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\w\s]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
