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

        /// <summary>Who last actually moved this character's block (cleared when the block is back in baseline order).</summary>
        public DiffAuthor? MovedBy { get; set; }

        /// <summary>Who last moved another block across this one while it is out of order (weaker than an own move or a restructure).</summary>
        public DiffAuthor? DisplacedBy { get; set; }

        /// <summary>Who last restructured this character's block: split, merged or largely rewrote it.</summary>
        public DiffAuthor? PlacedBy { get; set; }
    }

    public static ContentDiff Diff(IContentDiffService diff, string baselineJson, IReadOnlyList<ContentStep> steps)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(steps);
        var baseline = Flatten(baselineJson);
        var index = 0;
        var state = baseline.Select(block => block.Select(p => new CharInfo(p.C, p.Marks, index++, null, null)).ToList()).ToList();
        var blockOf = baseline.SelectMany((block, b) => Enumerable.Repeat(b, block.Count)).ToArray();
        var deletedBy = new Dictionary<int, DiffAuthor>();

        foreach (var step in steps)
        {
            var moved = new List<List<CharInfo>>();
            state = Apply(state, Flatten(step.Json), step.Author, deletedBy, moved);
            MarkMoves(state, moved, step.Author, blockOf);
        }

        // Baseline characters that survive: who moved / restructured them (for deletes the final diff shows of moved text).
        var movedBy = new Dictionary<int, DiffAuthor>();
        foreach (var c in state.SelectMany(b => b))
        {
            // Deletes of text that went elsewhere belong to whoever moved it away (before any later split of it), else to whoever
            // restructured it; being pushed out of order by someone else's move is the weakest reason.
            if (c.BaselineIndex is { } i && (c.MovedBy ?? c.PlacedBy ?? c.DisplacedBy) is { } by)
            {
                movedBy[i] = by;
            }
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

                return block with { Ops = BlockOps(baseStart, baseBlock, finalBlock, deletedBy, movedBy, last) };
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
    private static List<DiffOp> BlockOps(
        int baseStart, List<(char C, string Marks)> baseBlock, List<CharInfo> finalBlock, Dictionary<int, DiffAuthor> deletedBy, Dictionary<int, DiffAuthor> movedBy,
        DiffAuthor? last)
    {
        // Character by character: (op, char, author — null when unknown, changes, reformatted by).
        var chars = new List<(string Op, char C, DiffAuthor? By, IReadOnlyList<string>? Changes, DiffAuthor? FormatBy)>(finalBlock.Count + baseBlock.Count);
        bool Here(CharInfo c) => baseStart >= 0 && c.BaselineIndex is { } i && i >= baseStart && i < baseStart + baseBlock.Count;
        var surviving = finalBlock.Where(Here).Select(c => c.BaselineIndex!.Value).ToHashSet();
        var end = baseStart + baseBlock.Count;
        var next = baseStart; // next baseline index not yet emitted
        void FlushDeletes(int until)
        {
            // One stretch of deleted baseline text. Characters carried elsewhere (text moved between blocks) take who moved or
            // restructured them — unless the stretch was mostly deleted outright: then the leftovers the token diff happened to
            // carry along belong to that deletion too, so one deleted passage has one author.
            var stretch = new List<(char C, DiffAuthor? Deleted, DiffAuthor? Moved)>();
            for (; baseStart >= 0 && next < until && next < end; next++)
            {
                if (!surviving.Contains(next))
                {
                    stretch.Add((baseBlock[next - baseStart].C, deletedBy.GetValueOrDefault(next), movedBy.GetValueOrDefault(next)));
                }
            }

            var explicitCount = stretch.Count(d => d.Deleted is not null);
            var dominant = explicitCount * 2 > stretch.Count ? stretch.Where(d => d.Deleted is not null).Select(d => d.Deleted).MaxBy(a => a!.EntryId) : null;
            foreach (var (c, deleted, moved) in stretch)
            {
                chars.Add(("delete", c, deleted ?? dominant ?? moved, null, null));
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
            // Carried text: the later of an own move and a restructure; being pushed out of order is the weakest reason (as for deletes).
            chars.Add(("insert", c.C, c.InsertedBy ?? Latest(c.MovedBy, c.PlacedBy) ?? c.DisplacedBy, null, formatBy));
        }

        FlushDeletes(end);

        // Unknown authors (characters the fold carried across blocks, e.g. spaces matched between rewritten paragraphs) take
        // the author of the nearest known character in the same run of changes, else the last change. Linear: one pass per
        // direction over each run.
        for (var runStart = 0; runStart < chars.Count;)
        {
            var runEnd = runStart;
            while (runEnd < chars.Count && chars[runEnd].Op == chars[runStart].Op)
            {
                runEnd++;
            }

            if (chars[runStart].Op != "equal")
            {
                var before = new (DiffAuthor? Author, int At)[runEnd - runStart];
                (DiffAuthor? Author, int At) seen = (null, -1);
                for (var i = runStart; i < runEnd; i++)
                {
                    seen = chars[i].By is { } known ? (known, i) : seen;
                    before[i - runStart] = seen;
                }

                seen = (null, -1);
                for (var i = runEnd - 1; i >= runStart; i--)
                {
                    seen = chars[i].By is { } known ? (known, i) : seen;
                    if (chars[i].By is null)
                    {
                        var (prev, prevAt) = before[i - runStart];
                        var author = prev is null ? seen.Author : seen.Author is null ? prev : i - prevAt <= seen.At - i ? prev : seen.Author;
                        chars[i] = chars[i] with { By = author ?? last };
                    }
                }
            }

            runStart = runEnd;
        }

        // Runs of the same op, author and changes; text collected with a builder (a run can be a whole long paragraph).
        var ops = new List<DiffOp>();
        var text = new StringBuilder();
        void Close()
        {
            if (text.Length > 0)
            {
                ops[^1] = ops[^1] with { Text = text.ToString() };
                text.Clear();
            }
        }

        foreach (var (op, c, by, changes, formatBy) in chars)
        {
            var current = op == "equal" ? null : by;
            if (ops.Count > 0 && ops[^1].Op == op && Equals(ops[^1].By, current) && Equals(ops[^1].FormatBy, formatBy)
                && (ops[^1].Changes is null ? changes is null : changes is not null && ops[^1].Changes!.SequenceEqual(changes, StringComparer.Ordinal)))
            {
                text.Append(c);
                continue;
            }

            Close();
            ops.Add(new DiffOp(op, "", changes, current, formatBy));
            text.Append(c);
        }

        Close();
        return ops;
    }

    /// <summary>
    /// One old → new step. Blocks are aligned first (identical text and marks carry over whole, so moving a long paragraph
    /// costs nothing); only changed stretches get the character-level token diff.
    /// </summary>
    private static List<List<CharInfo>> Apply(
        List<List<CharInfo>> olds, List<List<(char C, string Marks)>> news, DiffAuthor author, Dictionary<int, DiffAuthor> deletedBy,
        List<List<CharInfo>> moved)
    {
        static string Key(IEnumerable<(char C, string Marks)> chars)
        {
            var key = new StringBuilder();
            string? marks = null;
            foreach (var (c, m) in chars)
            {
                if (m != marks)
                {
                    key.Append('\u0002').Append(m).Append('\u0002');
                    marks = m;
                }

                key.Append(c);
            }

            return key.ToString();
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string Lines(IEnumerable<string> keys) => string.Join('\n', keys.Select(k => ids.TryGetValue(k, out var id) ? id : ids[k] = ids.Count));
        var oldKeys = olds.Select(b => Key(b.Select(c => (c.C, c.Marks)))).ToList();
        var newKeys = news.Select(Key).ToList();
        var result = new List<List<CharInfo>>();
        if (olds.Count == 0 || news.Count == 0)
        {
            return ApplyStretch(olds, news, author, deletedBy);
        }

        var diff = Differ.Instance.CreateDiffs(Lines(oldKeys), Lines(newKeys), false, false, DiffPlex.Chunkers.LineChunker.Instance);
        var stretches = diff.DiffBlocks.Select(block => (
                OldStart: block.DeleteStartA, OldCount: Math.Min(block.DeleteCountA, olds.Count - block.DeleteStartA),
                NewStart: block.InsertStartB, NewCount: Math.Min(block.InsertCountB, news.Count - block.InsertStartB)))
            .ToList();

        // Moved blocks: identical on both sides but outside the aligned (equal) ones — anywhere in the step, since a move
        // leaves one stretch and lands in another. They carry over whole; the rest of each stretch gets the token diff.
        var unused = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        foreach (var (oldStart, oldCount, _, _) in stretches)
        {
            for (var i = oldStart; i < oldStart + oldCount; i++)
            {
                (unused.TryGetValue(oldKeys[i], out var q) ? q : unused[oldKeys[i]] = new Queue<int>()).Enqueue(i);
            }
        }

        var movedTo = new Dictionary<int, int>(); // new index → old index
        foreach (var (_, _, newStart, newCount) in stretches)
        {
            for (var j = newStart; j < newStart + newCount; j++)
            {
                if (unused.TryGetValue(newKeys[j], out var q) && q.Count > 0)
                {
                    movedTo[j] = q.Dequeue();
                }
            }
        }

        var usedOld = movedTo.Values.ToHashSet();
        moved.AddRange(movedTo.Values.Select(i => olds[i]));

        var (a, b) = (0, 0);
        foreach (var (oldStart, oldCount, newStart, newCount) in stretches)
        {
            for (; a < oldStart; a++, b++)
            {
                result.Add(olds[a]); // identical block in place: carried over with its origins
            }

            var changed = ApplyStretch(
                Enumerable.Range(oldStart, oldCount).Where(i => !usedOld.Contains(i)).Select(i => olds[i]).ToList(),
                Enumerable.Range(newStart, newCount).Where(j => !movedTo.ContainsKey(j)).Select(j => news[j]).ToList(),
                author, deletedBy);
            var k = 0;
            for (var j = newStart; j < newStart + newCount; j++)
            {
                result.Add(movedTo.TryGetValue(j, out var i) ? olds[i] : k < changed.Count ? changed[k++] : []);
            }

            a = oldStart + oldCount;
            b = newStart + newCount;
        }

        for (; a < olds.Count && b < news.Count; a++, b++)
        {
            result.Add(olds[a]);
        }

        return result;
    }

    /// <summary>One changed stretch: characters are carried over (keeping their origin), inserted, deleted or reformatted.</summary>
    private static List<List<CharInfo>> ApplyStretch(
        List<List<CharInfo>> olds, List<List<(char C, string Marks)>> news, DiffAuthor author, Dictionary<int, DiffAuthor> deletedBy)
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

        // Restructured blocks — split (an old block feeds several new ones), merged (a new block draws from several old ones) or
        // largely rewritten (less than half carried) — are placed by this step's author: their carried characters are his.
        var sourceOf = new Dictionary<CharInfo, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < olds.Count; i++)
        {
            foreach (var c in olds[i])
            {
                sourceOf[c] = i;
            }
        }

        var sources = blocks.Select(b => b.Where(sourceOf.ContainsKey).Select(c => sourceOf[c]).Distinct().ToList()).ToList();
        var feeds = sources.SelectMany(x => x).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < blocks.Count; i++)
        {
            var carried = blocks[i].Where(c => sourceOf.ContainsKey(c)).ToList();
            var restructured = sources[i].Count > 1 || sources[i].Any(x => feeds[x] > 1) || (blocks[i].Count > 0 && carried.Count * 2 < blocks[i].Count);
            if (restructured)
            {
                foreach (var c in carried)
                {
                    c.PlacedBy = author;
                }
            }
        }

        return news.Count == 0 ? [] : blocks;
    }

    /// <summary>
    /// After a step: blocks this step actually moved are moved by its author (a newer move overrides an older one); text they
    /// now jump over is displaced by the author (the final diff may show either side of a swap as the moved one). Order is
    /// judged per run of text from one baseline block, so a merged block's parts are judged separately. Runs back in baseline
    /// order (no inversion with any other run) lose both marks — an undone move leaves nothing. Linear in the document size.
    /// </summary>
    private static void MarkMoves(List<List<CharInfo>> state, List<List<CharInfo>> moved, DiffAuthor author, int[] blockOf)
    {
        var movedSet = new HashSet<List<CharInfo>>(moved, ReferenceEqualityComparer.Instance);
        var runs = new List<(int Block, int Position, List<CharInfo> Chars)>();
        for (var b = 0; b < state.Count; b++)
        {
            List<CharInfo>? run = null;
            foreach (var c in state[b])
            {
                if (c.BaselineIndex is not { } i)
                {
                    continue;
                }

                if (run is null || blockOf[run[0].BaselineIndex!.Value] != blockOf[i])
                {
                    run = [];
                    runs.Add((b, i, run));
                }

                run.Add(c);
            }
        }

        // Out of order at all: prefix maximum / suffix minimum over the runs.
        var suffixMin = new int[runs.Count + 1];
        suffixMin[runs.Count] = int.MaxValue;
        for (var k = runs.Count - 1; k >= 0; k--)
        {
            suffixMin[k] = Math.Min(suffixMin[k + 1], runs[k].Position);
        }

        var outOfOrder = new bool[runs.Count];
        var prefixMax = int.MinValue;
        for (var k = 0; k < runs.Count; k++)
        {
            outOfOrder[k] = prefixMax > runs[k].Position || suffixMin[k + 1] < runs[k].Position;
            prefixMax = Math.Max(prefixMax, runs[k].Position);
        }

        // Inverted with a run moved in this step (in another block): the moved runs' prefix maximum before the run's block and
        // suffix minimum after it.
        bool Moved(int k) => movedSet.Contains(state[runs[k].Block]);
        var movedMinAfter = new int[runs.Count];
        for (int k = runs.Count - 1, min = int.MaxValue, blockMin = int.MaxValue; k >= 0; k--)
        {
            if (k == runs.Count - 1 || runs[k].Block != runs[k + 1].Block)
            {
                min = Math.Min(min, blockMin);
                blockMin = int.MaxValue;
            }

            movedMinAfter[k] = min;
            blockMin = Moved(k) ? Math.Min(blockMin, runs[k].Position) : blockMin;
        }

        for (int k = 0, max = int.MinValue, blockMax = int.MinValue; k < runs.Count; k++)
        {
            if (k > 0 && runs[k].Block != runs[k - 1].Block)
            {
                max = Math.Max(max, blockMax);
                blockMax = int.MinValue;
            }

            var (_, position, chars) = runs[k];
            if (!outOfOrder[k])
            {
                chars.ForEach(c => (c.MovedBy, c.DisplacedBy) = (null, null));
            }
            else
            {
                if (Moved(k))
                {
                    chars.ForEach(c => c.MovedBy = author);
                }

                if (max > position || movedMinAfter[k] < position)
                {
                    chars.ForEach(c => c.DisplacedBy = author);
                }
            }

            blockMax = Moved(k) ? Math.Max(blockMax, position) : blockMax;
        }
    }

    /// <summary>The more recent of two marks (audit entry ids grow with time).</summary>
    private static DiffAuthor? Latest(DiffAuthor? a, DiffAuthor? b) => a is null ? b : b is null ? a : a.EntryId >= b.EntryId ? a : b;

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
