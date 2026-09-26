using System.Runtime.CompilerServices;
using DocHub.Infrastructure.Content.Diff;

namespace DocHub.Domain.Tests;

/// <summary>T11 §3: the content diff engine (block alignment, word diff, formatting changes, HTML).</summary>
public sealed class ContentDiffTests
{
    private static readonly ContentDiffService Service = new();

    private static string Doc(params string[] blocks) => $$"""{"type":"doc","content":[{{string.Join(',', blocks)}}]}""";

    private static string P(string text, string marks = "", string attrs = "") =>
        $$"""{"type":"paragraph"{{(attrs.Length > 0 ? $",\"attrs\":{attrs}" : "")}},"content":[{"type":"text","text":"{{text}}"{{(marks.Length > 0 ? $",\"marks\":{marks}" : "")}}}]}""";

    [Fact]
    public void A_changed_word_is_a_delete_and_an_insert_inside_the_paragraph()
    {
        var diff = Service.Diff(Doc(P("The old text")), Doc(P("The new text")));
        var block = Assert.Single(diff.Blocks);
        Assert.Equal("changed", block.Status);
        Assert.Equal([("equal", "The "), ("delete", "old"), ("insert", "new"), ("equal", " text")], block.Ops!.Select(o => (o.Op, o.Text)));
        Assert.Equal(new DiffStats(1, 1), diff.Stats);
        Assert.Equal("""<p class="ds-diff-changed">The <del class="ds-diff-delete">old</del><ins class="ds-diff-insert">new</ins> text</p>""", diff.Html);
    }

    [Fact]
    public void Making_a_word_bold_is_a_formatting_change_not_delete_and_insert()
    {
        var diff = Service.Diff(
            Doc("""{"type":"paragraph","content":[{"type":"text","text":"Make Scope bold"}]}"""),
            Doc("""{"type":"paragraph","content":[{"type":"text","text":"Make "},{"type":"text","text":"Scope","marks":[{"type":"bold"}]},{"type":"text","text":" bold"}]}"""));
        var ops = Assert.Single(diff.Blocks).Ops!;
        Assert.Equal(["equal", "format", "equal"], ops.Select(o => o.Op));
        Assert.Equal("Scope", ops[1].Text);
        Assert.Equal(["bold added"], ops[1].Changes!);
        Assert.Equal(new DiffStats(0, 0), diff.Stats);
        Assert.Contains("""<strong><span class="ds-diff-format">Scope</span></strong>""", diff.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Font_size_and_paragraph_style_changes_are_described()
    {
        var diff = Service.Diff(
            Doc(P("Title", """[{"type":"textStyle","attrs":{"fontSize":22}}]""", """{"styleId":"Normal"}""")),
            Doc(P("Title", """[{"type":"textStyle","attrs":{"fontSize":28}}]""", """{"styleId":"Heading2"}""")));
        var block = Assert.Single(diff.Blocks);
        Assert.Equal(["styleId Normal→Heading2"], block.Changes!);
        Assert.Equal(["fontSize 11pt→14pt"], Assert.Single(block.Ops!).Changes!);
    }

    [Fact]
    public void A_paragraph_becoming_a_heading_is_one_changed_block()
    {
        var diff = Service.Diff(Doc(P("Scope")), Doc("""{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Scope"}]}"""));
        var block = Assert.Single(diff.Blocks);
        Assert.Equal(("heading", "changed"), (block.Type, block.Status));
        Assert.Equal(["type paragraph→heading", "level none→2"], block.Changes!);
    }

    [Fact]
    public void Only_the_changed_table_cell_is_marked()
    {
        var old = File.ReadAllText(Fixture("report.old.json"));
        var neu = File.ReadAllText(Fixture("report.new.json"));
        var table = Service.Diff(old, neu).Blocks.Single(b => b.Type == "table");
        var cells = table.Rows!.SelectMany((row, r) => row.Select((cell, c) => (r, c, cell.Status))).ToList();
        Assert.Equal([(0, 0, "equal"), (0, 1, "equal"), (1, 0, "equal"), (1, 1, "changed")], cells);
        Assert.Equal([("delete", "10"), ("insert", "12")], table.Rows![1][1].Ops.Where(o => o.Op != "equal").Select(o => (o.Op, o.Text)));
    }

    [Fact]
    public void Inserted_and_deleted_blocks_and_list_items_are_reported_whole()
    {
        var diff = Service.Diff(File.ReadAllText(Fixture("report.old.json")), File.ReadAllText(Fixture("report.new.json")));
        Assert.Equal(
            [("heading", "equal"), ("paragraph", "changed"), ("paragraph", "deleted"), ("bulletList", "changed"), ("table", "changed"), ("paragraph", "inserted")],
            diff.Blocks.Select(b => (b.Type, b.Status)));
        Assert.Equal(["equal", "inserted", "equal"], diff.Blocks[3].Children!.Select(c => c.Status));
    }

    [Fact]
    public void The_fixture_diff_renders_like_the_approved_snapshot_with_text_escaped()
    {
        var diff = Service.Diff(File.ReadAllText(Fixture("report.old.json")), File.ReadAllText(Fixture("report.new.json")));
        var snapshot = Path.Combine(SourceFixtures(), "report.diff.html");
        if (Environment.GetEnvironmentVariable("UPDATE_CONTENT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(snapshot, diff.Html + "\n");
        }

        Assert.DoesNotContain("<data>", diff.Html, StringComparison.Ordinal);
        Assert.Equal(File.ReadAllText(snapshot).TrimEnd('\n'), diff.Html);
    }

    [Fact]
    public void Characters_outside_the_basic_plane_are_never_split()
    {
        var diff = Service.Diff(Doc(P("Mood \U0001F600 ok")), Doc(P("Mood \U0001F601 ok")));
        var ops = Assert.Single(diff.Blocks).Ops!;
        Assert.Equal([("equal", "Mood "), ("delete", "\U0001F600"), ("insert", "\U0001F601"), ("equal", " ok")], ops.Select(o => (o.Op, o.Text)));
        Assert.DoesNotContain("\uFFFD", diff.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_stored_duplicate_keys_are_read_last_one_wins()
    {
        const string duplicate = """{"type":"doc","type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"old text here","text":"new text here"}]}]}""";
        var diff = Service.Diff(Doc(P("old text here")), duplicate);
        Assert.Equal([("delete", "old"), ("insert", "new")], Assert.Single(diff.Blocks).Ops!.Where(o => o.Op != "equal").Select(o => (o.Op, o.Text)));
    }

    [Fact]
    public void Unreadable_or_empty_states_diff_as_empty_documents()
    {
        Assert.Equal(["inserted"], Service.Diff("not json", Doc(P("x"))).Blocks.Select(b => b.Status));
        Assert.Equal(["deleted"], Service.Diff(Doc(P("x")), "").Blocks.Select(b => b.Status));
        Assert.Empty(Service.Diff(Doc(), Doc()).Blocks);
        Assert.Equal(["equal"], Service.Diff(Doc(P("same")), Doc(P("same"))).Blocks.Select(b => b.Status));
    }

    [Fact]
    public void Line_breaks_and_repeated_words_diff_correctly()
    {
        var diff = Service.Diff(
            Doc("""{"type":"paragraph","content":[{"type":"text","text":"a a a"},{"type":"hardBreak"},{"type":"text","text":"b"}]}"""),
            Doc("""{"type":"paragraph","content":[{"type":"text","text":"a a"},{"type":"hardBreak"},{"type":"text","text":"b c"}]}"""));
        var ops = Assert.Single(diff.Blocks).Ops!;
        Assert.Equal("a a a\nb", string.Concat(ops.Where(o => o.Op != "insert").Select(o => o.Text)));
        Assert.Equal("a a\nb c", string.Concat(ops.Where(o => o.Op != "delete").Select(o => o.Text)));
        Assert.Contains("<br>", diff.Html, StringComparison.Ordinal);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "DiffFixtures", name);

    private static string SourceFixtures([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "DiffFixtures");
}

/// <summary>T11 §2a: attributed diff (track changes).</summary>
public sealed class AttributedDiffTests
{
    private static readonly ContentDiffService Service = new();
    private static readonly DiffAuthor Alice = new(1, 2, "Alice", "App", null, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    private static readonly DiffAuthor Bob = new(2, 3, "Bob", "App", null, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
    private static readonly DiffAuthor Script = new(3, null, null, "Script", "INC-7", new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));

    private static string Doc(params string[] paragraphs) =>
        $$"""{"type":"doc","content":[{{string.Join(',', paragraphs.Select(p => $$"""{"type":"paragraph","content":[{"type":"text","text":"{{p}}"}]}"""))}}]}""";

    [Fact]
    public void Each_run_is_attributed_and_text_added_then_removed_leaves_no_trace()
    {
        var baseline = Doc("The systm is fast.");
        var steps = new[]
        {
            new ContentStep(Doc("The systm is fast. It stores every change."), Alice),
            new ContentStep(Doc("The systm is fast. It stores every change. Temporary words."), Alice),
            new ContentStep(Doc("The systm is fast. It records every change."), Bob),
            new ContentStep(Doc("The system is fast. It records every change."), Script),
        };

        var ops = Assert.Single(AttributedDiff.Diff(Service, baseline, steps).Blocks).Ops!;
        var changes = ops.Where(o => o.Op != "equal").Select(o => (o.Op, o.Text, o.By?.DisplayName ?? o.By?.Source)).ToList();
        Assert.Contains(("delete", "systm", "Script"), changes);
        Assert.Contains(("insert", "system", "Script"), changes);
        Assert.Contains(("insert", "records", "Bob"), changes);
        Assert.Contains(changes, c => c.Op == "insert" && c.Item3 == "Alice" && c.Text.Contains("every change", StringComparison.Ordinal));
        Assert.DoesNotContain(ops, o => o.Text.Contains("Temporary", StringComparison.Ordinal) || o.Text.Contains("stores", StringComparison.Ordinal));
        Assert.Equal("INC-7", ops.First(o => o.By?.Source == "Script").By!.Ticket);
        Assert.All(ops.Where(o => o.Op == "equal"), o => Assert.Null(o.By));
    }

    [Fact]
    public void A_format_change_keeps_the_text_author_and_records_the_formatter()
    {
        var baseline = Doc("Intro.");
        var steps = new[]
        {
            new ContentStep(Doc("Intro. Scope matters."), Alice),
            new ContentStep("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Intro. "},{"type":"text","text":"Scope","marks":[{"type":"bold"}]},{"type":"text","text":" matters."}]}]}""", Bob),
        };

        var ops = Assert.Single(AttributedDiff.Diff(Service, baseline, steps).Blocks).Ops!;
        // Inserted within the range: shown as Alice's insert (with Bob's bold applied), not as Bob's text.
        Assert.All(ops.Where(o => o.Op == "insert"), o => Assert.Equal("Alice", o.By!.DisplayName));
        Assert.Contains("Scope", string.Concat(ops.Where(o => o.Op == "insert").Select(o => o.Text)), StringComparison.Ordinal);
        // ...and Bob is recorded as the one who reformatted it.
        var bolded = Assert.Single(ops, o => o.Op == "insert" && o.FormatBy is not null);
        Assert.Equal(("Scope", "Bob"), (bolded.Text, bolded.FormatBy!.DisplayName));

        // Bolding baseline text is Bob's format change.
        var formatted = AttributedDiff.Diff(Service, Doc("Scope matters."),
            [new ContentStep("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Scope","marks":[{"type":"bold"}]},{"type":"text","text":" matters."}]}]}""", Bob)]);
        var format = Assert.Single(Assert.Single(formatted.Blocks).Ops!, o => o.Op == "format");
        Assert.Equal(("Scope", "Bob"), (format.Text, format.By!.DisplayName));
    }

    [Fact]
    public void Rewritten_and_moved_paragraphs_keep_their_author_for_spaces_and_punctuation()
    {
        var baseline = Doc("Alpha beta gamma delta.", "Second paragraph stays here.", "Third one moves around.");
        var steps = new[]
        {
            new ContentStep(Doc("Third one moves around.", "Completely new wording now.", "Second paragraph stays here."), Alice),
            new ContentStep(Doc("Third one moves around.", "Completely new wording now.", "Second paragraph remains here."), Bob),
        };

        var ops = AttributedDiff.Diff(Service, baseline, steps).Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal").ToList();
        // Alice's move shows as delete + re-insert of the paragraph (hers); only Bob's word change is his.
        Assert.Equal(["delete stays", "insert remains"], ops.Where(o => o.By!.DisplayName == "Bob").Select(o => $"{o.Op} {o.Text.Trim()}"));
        Assert.All(ops.Where(o => o.By!.DisplayName != "Bob"), o => Assert.Equal("Alice", o.By!.DisplayName));
    }

    [Fact]
    public void A_long_new_paragraph_is_attributed_in_linear_time()
    {
        var words = string.Join(' ', Enumerable.Range(0, 8000).Select(i => $"word{i % 997}"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = AttributedDiff.Diff(Service, Doc("Start."), [new ContentStep(Doc("Start.", words), Alice)]);
        watch.Stop();
        Assert.Equal("inserted", diff.Blocks[1].Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void An_undone_move_does_not_claim_a_later_rewrite()
    {
        var baseline = Doc("Zeta intro.", "Alpha beta gamma delta.", "Second paragraph stays here.", "Omega end.");
        var steps = new[]
        {
            new ContentStep(Doc("Omega end.", "Zeta intro.", "Alpha beta gamma delta.", "Second paragraph stays here."), Alice),
            new ContentStep(baseline, Alice),
            new ContentStep(Doc("Zeta intro.", "Completely new wording now.", "Another fresh sentence, too.", "Omega end."), Bob),
        };

        var ops = AttributedDiff.Diff(Service, baseline, steps).Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal").ToList();
        Assert.NotEmpty(ops);
        Assert.All(ops, o => Assert.Equal("Bob", o.By!.DisplayName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_split_after_an_undone_move_is_the_splitters(bool carolEditsLater)
    {
        var carol = new DiffAuthor(4, 4, "Carol", "App", null, new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc));
        var baseline = Doc("Xray paragraph moves around.", "Yankee one two three. Four five six seven.", "Zulu tail paragraph here.");
        var split = Doc("Xray paragraph moves around.", "Yankee one two three.", "Four five six seven.", "Zulu tail paragraph here.");
        var steps = new List<ContentStep>
        {
            new(Doc("Yankee one two three. Four five six seven.", "Xray paragraph moves around.", "Zulu tail paragraph here."), Alice),
            new(baseline, Alice),
            new(split, Bob),
        };
        if (carolEditsLater)
        {
            steps.Add(new ContentStep(Doc("Xray paragraph moves around.", "Yankee one two three.", "Four five six seven.", "Zulu tail paragraph now."), carol));
        }

        var ops = AttributedDiff.Diff(Service, baseline, steps).Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal").ToList();
        Assert.All(ops.Where(o => !o.Text.Contains("here", StringComparison.Ordinal) && !o.Text.Contains("now", StringComparison.Ordinal)),
            o => Assert.Equal("Bob", o.By!.DisplayName));
        Assert.DoesNotContain(ops, o => o.By!.DisplayName == "Alice");
    }

    [Fact]
    public void A_later_real_move_beats_being_pushed_out_of_order_by_an_earlier_one()
    {
        var baseline = Doc("Xa one.", "Yb two.", "Zc three.", "Wd four.", "Ve five.");
        var steps = new[]
        {
            new ContentStep(Doc("Ve five.", "Xa one.", "Yb two.", "Zc three.", "Wd four."), Alice),
            new ContentStep(Doc("Ve five.", "Xa one.", "Zc three.", "Wd four.", "Yb two."), Bob),
        };

        var ops = AttributedDiff.Diff(Service, baseline, steps).Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal").ToList();
        Assert.All(ops.Where(o => o.Text.Contains("Yb", StringComparison.Ordinal)), o => Assert.Equal("Bob", o.By!.DisplayName));
        Assert.All(ops.Where(o => o.Text.Contains("Ve", StringComparison.Ordinal)), o => Assert.Equal("Alice", o.By!.DisplayName));
    }

    [Fact]
    public void Being_pushed_out_of_order_does_not_claim_a_later_split_or_merge()
    {
        var baseline = Doc("Xa one.", "Yb two.", "Zc three.", "Wd four.", "Ve five.");
        var pushed = Doc("Ve five.", "Xa one.", "Yb two.", "Zc three.", "Wd four.");
        foreach (var restructured in new[]
                 {
                     Doc("Ve five.", "Xa", "one.", "Yb two.", "Zc three.", "Wd four."),
                     Doc("Ve five.", "Xa one. Yb two.", "Zc three.", "Wd four."),
                 })
        {
            var ops = AttributedDiff.Diff(Service, baseline, [new ContentStep(pushed, Alice), new ContentStep(restructured, Bob)])
                .Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal").ToList();
            Assert.All(ops.Where(o => o.Text.Contains("Xa", StringComparison.Ordinal) || o.Text.Contains("one", StringComparison.Ordinal)),
                o => Assert.Equal("Bob", o.By!.DisplayName));
            Assert.All(ops.Where(o => o.Text.Contains("Ve", StringComparison.Ordinal)), o => Assert.Equal("Alice", o.By!.DisplayName));
        }
    }

    [Fact]
    public void Text_moved_away_and_then_merged_stays_with_the_mover()
    {
        var baseline = Doc("Xa one.", "Yb two.", "Zc three.", "Wd four.", "Ve five.");
        var steps = new[]
        {
            new ContentStep(Doc("Yb two.", "Zc three.", "Wd four.", "Ve five.", "Xa one."), Alice),
            new ContentStep(Doc("Yb two.", "Zc three.", "Wd four.", "Ve five. Xa one."), Bob),
        };

        var blocks = AttributedDiff.Diff(Service, baseline, steps).Blocks;
        var first = blocks.First(b => b.Ops is not null && b.Status != "equal");
        Assert.All(first.Ops!.Where(o => o.Op == "delete"), o => Assert.Equal("Alice", o.By!.DisplayName));
        Assert.Contains(first.Ops!, o => o.Op == "delete" && o.Text.Contains("Xa one.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("swap")]
    [InlineData("reverse")]
    public void Moving_many_paragraphs_at_once_is_attributed_in_linear_time(string kind)
    {
        var paragraphs = Enumerable.Range(0, 2000).Select(i => $"Paragraph {i} has about ten words of text in it.").ToArray();
        var changed = kind == "swap" ? [.. paragraphs[1000..], .. paragraphs[..1000]] : paragraphs.Reverse().ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = AttributedDiff.Diff(Service, Doc(paragraphs), [new ContentStep(Doc(changed), Alice)]);
        watch.Stop();
        Assert.All(diff.Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal"), o => Assert.Equal("Alice", o.By!.DisplayName));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Swapping_a_long_paragraph_is_attributed_in_linear_time()
    {
        var words = string.Join(' ', Enumerable.Range(0, 16000).Select(i => $"word{i % 997}"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var diff = AttributedDiff.Diff(Service, Doc("Small A.", words), [new ContentStep(Doc(words, "Small A."), Alice)]);
        watch.Stop();
        Assert.All(diff.Blocks.SelectMany(b => b.Ops ?? []).Where(o => o.Op != "equal"), o => Assert.Equal("Alice", o.By!.DisplayName));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Numbers_beyond_double_are_kept_as_written()
    {
        var diff = Service.Diff(Doc("x"), """{"type":"doc","content":[{"type":"heading","attrs":{"level":1e400},"content":[{"type":"text","text":"x"}]}]}""");
        Assert.NotEmpty(diff.Html);
    }

    [Fact]
    public void Deleted_and_inserted_paragraphs_and_tables_are_attributed()
    {
        var baseline = Doc("Keep.", "Remove me.");
        var steps = new[] { new ContentStep(Doc("Keep."), Bob), new ContentStep(Doc("Keep.", "Added by Alice."), Alice) };
        var blocks = AttributedDiff.Diff(Service, baseline, steps).Blocks;
        Assert.Equal(["equal", "deleted", "inserted"], blocks.Select(b => b.Status));
        Assert.Equal("Bob", Assert.Single(blocks[1].Ops!).By!.DisplayName);
        Assert.Equal("Alice", Assert.Single(blocks[2].Ops!).By!.DisplayName);
        Assert.DoesNotContain(AttributedDiff.Diff(Service, baseline, []).Blocks, b => b.Status != "equal");
    }
}
