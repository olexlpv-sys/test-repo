using System.Text;
using System.Text.Json.Nodes;
using DiffPlex;

namespace DocHub.Infrastructure.Content.Diff;

/// <summary>A content state after one change, and who made it.</summary>
public sealed record ContentStep(string Json, DiffAuthor Author);

/// <summary>
/// Track changes (T11 §2a): the diff from a baseline to the last of a series of states, each insert/delete/format run
/// attributed to the change that made it. Consecutive states are folded character by character: a character keeps its origin
/// (a baseline position, or the change that inserted it) and the change that last reformatted it; deleting baseline text
/// records who deleted it. Text inserted and deleted again within the series leaves no trace.
/// </summary>
public static class AttributedDiff
{
    private const char BlockSeparator = '\u0001';

    private sealed class CharInfo(char c, string marks, int? baselineIndex, DiffAuthor? insertedBy, DiffAuthor? formatBy)
    {
        public char C { get; } = c;

        public string Marks { get; set; } = marks;

        public int? BaselineIndex { get; } = baselineIndex;

        public DiffAuthor? InsertedBy { get; } = insertedBy;

        public DiffAuthor? FormatBy { get; set; } = formatBy;
    }

    public static ContentDiff Diff(IContentDiffService diff, string baselineJson, IReadOnlyList<ContentStep> steps)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(steps);
        var baseline = Flatten(baselineJson);
        var index = 0;
        var state = baseline.Select(block => block.Select(p => new CharInfo(p.C, p.Marks, index++, null, null)).ToList()).ToList();
        var deletedBy = new Dictionary<int, DiffAuthor>();

        foreach (var step in steps)
        {
            state = Apply(state, Flatten(step.Json), step.Author, deletedBy);
        }

        var finalJson = steps.Count == 0 ? baselineJson : steps[^1].Json;
        var result = diff.Diff(baselineJson, finalJson);
        var last = steps.Count == 0 ? null : steps[^1].Author;

        // Walk the structured diff in document order with cursors over the baseline and final text blocks.
        var baselineBlocks = new Queue<int>();
        var baselineChars = baseline.SelectMany(b => b).ToList();
        var baselineLengths = new Dictionary<int, int>();
        var offset = 0;
        foreach (var block in baseline)
        {
            baselineBlocks.Enqueue(offset);
            baselineLengths[offset] = block.Count;
            offset += block.Count;
        }

        var finalBlocks = new Queue<List<CharInfo>>(state);
        IReadOnlyList<DiffBlock> Walk(IReadOnlyList<DiffBlock> blocks) => blocks.Select(block =>
        {
            if (block.Ops is { } ops)
            {
                var consumesOld = block.Status != "inserted";
                var consumesNew = block.Status != "deleted";
                var baseStart = consumesOld && baselineBlocks.Count > 0 ? baselineBlocks.Dequeue() : -1;
                var baseBlock = baseStart >= 0 ? baselineChars.GetRange(baseStart, baselineLengths[baseStart]) : [];
                var finalBlock = consumesNew && finalBlocks.Count > 0 ? finalBlocks.Dequeue() : [];
                if (block.Status == "equal")
                {
                    return block;
                }

                return block with { Ops = BlockOps(baseStart, baseBlock, finalBlock, deletedBy, last) };
            }

            return block with
            {
                Children = block.Children is null ? null : Walk(block.Children),
                Rows = block.Rows?.Select(row => (IReadOnlyList<DiffCell>)row.Select(cell =>
                {
                    var cellBlocks = Walk(cell.Blocks);
                    return cell with { Blocks = cellBlocks, Ops = CellOps(cellBlocks) };
                }).ToList()).ToList(),
            };
        }).ToList();

        return result with { Blocks = Walk(result.Blocks) };
    }

    /// <summary>
    /// The ops of one text block from the fold itself: final characters that come from this baseline block are equal (or a
    /// format change), characters inserted within the series are inserts by their author, baseline characters that no longer
    /// survive here are deletes by whoever deleted them — placed before the next surviving character, deletions first.
    /// </summary>
    private static List<DiffOp> BlockOps(int baseStart, List<(char C, string Marks)> baseBlock, List<CharInfo> finalBlock, Dictionary<int, DiffAuthor> deletedBy, DiffAuthor? last)
    {
        // Character by character: (op, char, author — null when unknown, changes, reformatted by).
        var chars = new List<(string Op, char C, DiffAuthor? By, IReadOnlyList<string>? Changes, DiffAuthor? FormatBy)>(finalBlock.Count + baseBlock.Count);
        bool Here(CharInfo c) => baseStart >= 0 && c.BaselineIndex is { } i && i >= baseStart && i < baseStart + baseBlock.Count;
        var surviving = finalBlock.Where(Here).Select(c => c.BaselineIndex!.Value).ToHashSet();
        var end = baseStart + baseBlock.Count;
        var next = baseStart; // next baseline index not yet emitted
        void FlushDeletes(int until)
        {
            for (; baseStart >= 0 && next < until && next < end; next++)
            {
                if (!surviving.Contains(next))
                {
                    // Deleted here but carried elsewhere (text moved between blocks): the author comes from its neighbours.
                    chars.Add(("delete", baseBlock[next - baseStart].C, deletedBy.GetValueOrDefault(next), null, null));
                }
            }
        }

        // The next surviving baseline index after each position (linear, precomputed).
        var nextSurvivor = new int[finalBlock.Count + 1];
        nextSurvivor[finalBlock.Count] = end;
        for (var i = finalBlock.Count - 1; i >= 0; i--)
        {
            nextSurvivor[i] = Here(finalBlock[i]) ? finalBlock[i].BaselineIndex!.Value : nextSurvivor[i + 1];
        }

        for (var i = 0; i < finalBlock.Count; i++)
        {
            var c = finalBlock[i];
            if (Here(c))
            {
                FlushDeletes(c.BaselineIndex!.Value);
                next = Math.Max(next, c.BaselineIndex!.Value + 1);
                var baseMarks = baseBlock[c.BaselineIndex!.Value - baseStart].Marks;
                chars.Add(baseMarks == c.Marks ? ("equal", c.C, null, null, null) : ("format", c.C, c.FormatBy, InlineDiff.FormatChanges(baseMarks, c.Marks), null));
                continue;
            }

            // An insert: the deletions it replaces come first (up to the next surviving baseline character).
            FlushDeletes(nextSurvivor[i + 1]);
            var formatBy = c.InsertedBy is not null && c.FormatBy is not null && !Equals(c.FormatBy, c.InsertedBy) ? c.FormatBy : null;
            chars.Add(("insert", c.C, c.InsertedBy, null, formatBy));
        }

        FlushDeletes(end);

        // Unknown authors (characters the fold carried across blocks, e.g. spaces matched between rewritten paragraphs) take
        // the author of the nearest changed neighbour of the same kind, else the last change.
        for (var i = 0; i < chars.Count; i++)
        {
            if (chars[i].Op == "equal" || chars[i].By is not null)
            {
                continue;
            }

            DiffAuthor? found = null;
            for (var d = 1; found is null && (i - d >= 0 || i + d < chars.Count); d++)
            {
                if (i - d >= 0 && chars[i - d].Op == chars[i].Op && chars[i - d].By is { } before)
                {
                    found = before;
                }
                else if (i + d < chars.Count && chars[i + d].Op == chars[i].Op && chars[i + d].By is { } after)
                {
                    found = after;
                }
                else if ((i - d < 0 || chars[i - d].Op != chars[i].Op) && (i + d >= chars.Count || chars[i + d].Op != chars[i].Op))
                {
                    break; // left the run of this kind on both sides
                }
            }

            chars[i] = chars[i] with { By = found ?? last };
        }

        var ops = new List<DiffOp>();
        foreach (var (op, c, by, changes, formatBy) in chars)
        {
            if (ops.Count > 0 && ops[^1].Op == op && Equals(ops[^1].By, by) && Equals(ops[^1].FormatBy, formatBy)
                && (ops[^1].Changes is null ? changes is null : changes is not null && ops[^1].Changes!.SequenceEqual(changes, StringComparer.Ordinal)))
            {
                ops[^1] = ops[^1] with { Text = ops[^1].Text + c };
            }
            else
            {
                ops.Add(new DiffOp(op, c.ToString(), changes, op == "equal" ? null : by, formatBy));
            }
        }

        return ops;
    }

    /// <summary>One old → new step: characters are carried over (keeping their origin), inserted, deleted or reformatted.</summary>
    private static List<List<CharInfo>> Apply(List<List<CharInfo>> olds, List<List<(char C, string Marks)>> news, DiffAuthor author, Dictionary<int, DiffAuthor> deletedBy)
    {
        var oldChars = Join(olds, c => c.C);
        var newChars = Join(news, p => p.C);
        var oldFlat = olds.SelectMany((b, i) => i == 0 ? b : b.Prepend(null!)).ToList();
        var newFlat = news.SelectMany((b, i) => i == 0 ? b.Select(p => ((char, string)?)p) : b.Select(p => ((char, string)?)p).Prepend(null)).ToList();
        var oldTokens = InlineDiff.Tokenize(oldChars);
        var newTokens = InlineDiff.Tokenize(newChars);
        var diff = Differ.Instance.CreateDiffs(oldChars, newChars, false, false, TokenChunker.Instance);

        var result = new List<CharInfo?>(newFlat.Count);
        var (oldPos, newPos, a, t) = (0, 0, 0, 0);
        void Carry(int oldTokenCount)
        {
            for (var k = 0; k < oldTokenCount; k++, a++, t++)
            {
                for (var i = 0; i < oldTokens[a].Length; i++, oldPos++, newPos++)
                {
                    var (o, n) = (oldFlat[oldPos], newFlat[newPos]);
                    if (o is null || n is null)
                    {
                        result.Add(null);
                        continue;
                    }

                    if (o.Marks != n.Value.Item2)
                    {
                        o.Marks = n.Value.Item2;
                        o.FormatBy = author;
                    }

                    result.Add(o);
                }
            }
        }

        foreach (var block in diff.DiffBlocks)
        {
            Carry(block.DeleteStartA - a);
            for (var k = 0; k < block.DeleteCountA && a < oldTokens.Length; k++, a++)
            {
                for (var i = 0; i < oldTokens[a].Length; i++, oldPos++)
                {
                    if (oldFlat[oldPos] is { BaselineIndex: { } index })
                    {
                        deletedBy[index] = author;
                    }
                }
            }

            for (var k = 0; k < block.InsertCountB && t < newTokens.Length; k++, t++)
            {
                for (var i = 0; i < newTokens[t].Length; i++, newPos++)
                {
                    result.Add(newFlat[newPos] is { } n ? new CharInfo(n.Item1, n.Item2, null, author, null) : null);
                }
            }
        }

        Carry(Math.Min(oldTokens.Length - a, newTokens.Length - t));

        // Back to blocks (separators are the nulls).
        var blocks = new List<List<CharInfo>> { new() };
        foreach (var c in result)
        {
            if (c is null)
            {
                blocks.Add([]);
            }
            else
            {
                blocks[^1].Add(c);
            }
        }

        return news.Count == 0 ? [] : blocks;
    }

    private static string Join<T>(IEnumerable<List<T>> blocks, Func<T, char> selector) =>
        string.Join(BlockSeparator, blocks.Select(b => new string(b.Select(selector).ToArray())));

    /// <summary>The text blocks (paragraphs, headings — also in lists and tables) of a document in document order, as characters with marks.</summary>
    private static List<List<(char C, string Marks)>> Flatten(string json)
    {
        var blocks = new List<List<(char, string)>>();
        void Walk(JsonObject node)
        {
            var type = node["type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (type is "paragraph" or "heading")
            {
                var chars = new List<(char, string)>();
                foreach (var child in node["content"] as JsonArray ?? [])
                {
                    if (child is not JsonObject inline)
                    {
                        continue;
                    }

                    var inlineType = inline["type"] is JsonValue iv && iv.TryGetValue<string>(out var it) ? it : null;
                    if (inlineType == "text" && inline["text"] is JsonValue tv && tv.TryGetValue<string>(out var text))
                    {
                        var marks = InlineDiff.MarksKey(inline["marks"] as JsonArray);
                        chars.AddRange(text.Select(c => (c, marks)));
                    }
                    else if (inlineType == "hardBreak")
                    {
                        chars.Add(('\n', InlineDiff.BreakKey));
                    }
                }

                blocks.Add(chars);
                return;
            }

            foreach (var child in node["content"] as JsonArray ?? [])
            {
                if (child is JsonObject o && o["type"] is JsonValue)
                {
                    Walk(o);
                }
            }
        }

        Walk(ContentDiffService.ParseDocument(json));
        return blocks;
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
                    ops.Add(new DiffOp("equal", "\n"));
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

    private sealed class TokenChunker : IChunker
    {
        public static readonly TokenChunker Instance = new();

        public IReadOnlyList<string> Chunk(string text) => InlineDiff.Tokenize(text);
    }
}
