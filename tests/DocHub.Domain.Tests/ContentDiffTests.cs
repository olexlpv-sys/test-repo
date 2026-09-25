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
