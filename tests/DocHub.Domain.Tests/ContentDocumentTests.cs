using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Domain.Content;

namespace DocHub.Domain.Tests;

/// <summary>T09: validation, canonicalization and rendering of node content (DocHub Content Schema v1).</summary>
public sealed class ContentDocumentTests
{
    public static readonly string[] CatalogStyles =
        ["Normal", "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6", "Title", "Subtitle", "Quote", "ListParagraph", "Caption", "TableGrid", "Strong", "Emphasis"];

    private static readonly HashSet<string> Fonts = ["Aptos", "Aptos Display", "Calibri", "Cambria", "Arial", "Times New Roman", "Georgia", "Verdana", "Segoe UI", "Courier New"];

    private static readonly ContentDocument Schema = new(CatalogStyles, Fonts);

    public static TheoryData<string> Fixtures() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ContentFixtures"), "*.json").Select(Path.GetFileNameWithoutExtension).Order()!);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixtures_are_valid_and_canonical(string fixture)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fixture, ".json")));
        Assert.Empty(Schema.Validate(document.RootElement));
        var canonical = Schema.Canonicalize(document.RootElement);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(document.RootElement.GetRawText()), JsonNode.Parse(canonical)), canonical);
        using var again = JsonDocument.Parse(canonical);
        Assert.Equal(canonical, Schema.Canonicalize(again.RootElement));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixtures_render_like_the_approved_snapshots(string fixture)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath(fixture, ".json")));
        var html = ContentHtmlRenderer.Render(Schema.Canonicalize(document.RootElement)).Html;
        var snapshot = Path.Combine(SourceFixtures(), fixture + ".html");
        if (Environment.GetEnvironmentVariable("UPDATE_CONTENT_SNAPSHOTS") == "1")
        {
            File.WriteAllText(snapshot, html + "\n");
        }

        Assert.True(File.Exists(snapshot), $"No approved snapshot {snapshot}; run with UPDATE_CONTENT_SNAPSHOTS=1 and review it.");
        Assert.Equal(File.ReadAllText(snapshot).TrimEnd('\n'), html);
    }

    [Theory]
    [InlineData("""{"type":"doc","content":[{"type":"video"}]}""", "content[0].type")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textStyle","attrs":{"color":"red; background:url(https://x)"}}]}]}]}""", "content[0].content[0].marks[0].attrs.color")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"javascript:alert(1)"}}]}]}]}""", "content[0].content[0].marks[0].attrs.href")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textStyle","attrs":{"fontSize":1000}}]}]}]}""", "content[0].content[0].marks[0].attrs.fontSize")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"styleId":"NoSuchStyle"}}]}""", "content[0].attrs.styleId")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textStyle","attrs":{"fontFamily":"Comic Sans MS"}}]}]}]}""", "content[0].content[0].marks[0].attrs.fontFamily")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"indentLeft":40000}}]}""", "content[0].attrs.indentLeft")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"spacingBefore":1.5}}]}""", "content[0].attrs.spacingBefore")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"onclick":"x"}}]}""", "content[0].attrs.onclick")]
    [InlineData("""{"type":"doc","content":[{"type":"table","content":[{"type":"paragraph"}]}]}""", "content[0].content[0].type")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"paragraph"}]}]}""", "content[0].content[0].type")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":""}]}]}""", "content[0].content[0].text")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"bold"},{"type":"bold"}]}]}]}""", "content[0].content[0].marks[1].type")]
    [InlineData("""{"type":"doc","content":[{"type":"heading","content":[]}]}""", "content[0].attrs.level")]
    [InlineData("""{"type":"doc","content":[{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"shading":"#12345"}}]}]}]}""", "content[0].content[0].content[0].attrs.shading")]
    [InlineData("""{"type":"doc","content":[{"type":"table","attrs":{"borders":{"top":{"style":"wavy"}}}}]}""", "content[0].attrs.borders.top.style")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"wordExt":"not an object"}}]}""", "content[0].attrs.wordExt")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link"}]}]}]}""", "content[0].content[0].marks[0].attrs.href")]
    [InlineData("""{"type":"paragraph"}""", "")]
    [InlineData("""{"type":"doc","content":[{"type":"bogus","type":"paragraph"}]}""", "content[0].type")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"wordExt":{"a":1,"a":2}}}]}""", "content[0].attrs.wordExt.a")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"\ud800"}]}]}""", "content[0].content[0].text")]
    public void Invalid_content_is_reported_with_its_json_path(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var errors = Schema.Validate(document.RootElement);
        Assert.Contains(path, errors.Keys);
    }

    [Fact]
    public void Nesting_deeper_than_20_levels_is_rejected()
    {
        var node = new JsonObject { ["type"] = "paragraph" };
        for (var i = 0; i < 12; i++)
        {
            node = new JsonObject
            {
                ["type"] = "bulletList",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(node) }),
            };
        }

        using var document = JsonDocument.Parse(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(node) }.ToJsonString());
        Assert.Contains(Schema.Validate(document.RootElement).Values, v => v.Any(m => m.Contains("deeper than 20", StringComparison.Ordinal)));
    }

    [Fact]
    public void Wordext_objects_up_to_16_kb_are_accepted_and_kept()
    {
        var small = """{"type":"doc","content":[{"type":"paragraph","attrs":{"wordExt":{"w:rPr":{"w:kern":""" + "\"" + new string('x', 100) + "\"" + """}}},"content":[{"type":"text","text":"x"}]}]}""";
        var large = """{"type":"doc","content":[{"type":"paragraph","attrs":{"wordExt":{"blob":""" + "\"" + new string('x', 17 * 1024) + "\"" + """}},"content":[{"type":"text","text":"x"}]}]}""";
        using var ok = JsonDocument.Parse(small);
        using var tooLarge = JsonDocument.Parse(large);
        Assert.Empty(Schema.Validate(ok.RootElement));
        Assert.Contains("w:kern", Schema.Canonicalize(ok.RootElement), StringComparison.Ordinal);
        Assert.Contains("content[0].attrs.wordExt", Schema.Validate(tooLarge.RootElement).Keys);
    }

    [Fact]
    public void Canonicalization_sorts_keys_drops_defaults_merges_runs_and_trims_trailing_empty_paragraphs()
    {
        const string input = """
            {"content":[
              {"type":"paragraph","attrs":{"styleId":"normal","align":null,"keepWithNext":false},"content":[
                {"text":"Hello ","type":"text","marks":[{"type":"italic"},{"type":"bold"}]},
                {"type":"text","text":"world","marks":[{"type":"bold"},{"type":"italic"}]},
                {"type":"text","text":"!"}]},
              {"type":"orderedList","attrs":{"start":1,"level":0,"listStyle":"decimal"},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[]}]}]},
              {"type":"paragraph","attrs":{"align":"left"}},
              {"type":"paragraph"}
            ],"type":"doc"}
            """;
        using var document = JsonDocument.Parse(input);
        Assert.Empty(Schema.Validate(document.RootElement));
        Assert.Equal(
            """{"content":[{"attrs":{"styleId":"Normal"},"content":[{"marks":[{"type":"bold"},{"type":"italic"}],"text":"Hello world","type":"text"},{"text":"!","type":"text"}],"type":"paragraph"},{"attrs":{"listStyle":"decimal"},"content":[{"content":[{"type":"paragraph"}],"type":"listItem"}],"type":"orderedList"}],"type":"doc"}""",
            Schema.Canonicalize(document.RootElement));
    }

    [Fact]
    public void Empty_content_is_the_empty_document()
    {
        using var document = JsonDocument.Parse("""{"type":"doc","content":[{"type":"paragraph"}]}""");
        Assert.Equal(ContentSchema.EmptyDocument, Schema.Canonicalize(document.RootElement));
    }

    [Fact]
    public void Plain_text_separates_blocks_by_newlines_and_table_cells_by_tabs()
    {
        const string table = """
            {"type":"doc","content":[{"type":"table","content":[
              {"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"a"}]}]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"b"}]}]}]},
              {"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"c"}]}]},{"type":"tableCell","content":[{"type":"paragraph","content":[{"type":"text","text":"d"}]}]}]}]}]}
            """;
        Assert.Equal("a\tb\nc\td", ContentHtmlRenderer.Render(table).PlainText);

        using var headings = JsonDocument.Parse(File.ReadAllText(FixturePath("headings", ".json")));
        Assert.Equal("1 Scope\n1.1 Purpose\n1.1.1 Background\nBody text under the headings.", ContentHtmlRenderer.PlainText(headings.RootElement));
    }

    [Fact]
    public void Text_is_always_escaped_and_unsafe_values_from_scripts_are_never_rendered()
    {
        // Script-stored (unvalidated) content: the renderer checks every value itself.
        const string forged = """
            {"type":"doc","content":[
              {"type":"paragraph","attrs":{"styleId":"x\" onclick=\"alert(1)","align":"center;color:red"},"content":[
                {"type":"text","text":"<script>alert(1)</script>","marks":[
                  {"type":"link","attrs":{"href":"javascript:alert(1)"}},
                  {"type":"textStyle","attrs":{"fontFamily":"x\";background:url(//evil)","color":"red;x:y","fontSize":"12pt"}}]}]},
              {"type":"iframe","attrs":{"src":"https://evil"}},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"shading":"url(x)","colspan":"2\" onmouseover=\"x"},"content":[]}]}]}]}
            """;
        var html = ContentHtmlRenderer.Render(forged).Html;
        Assert.Equal("""<p>&lt;script&gt;alert(1)&lt;/script&gt;</p><table style="border-collapse:collapse"><tbody><tr><td></td></tr></tbody></table>""", html);
    }

    [Fact]
    public void Script_stored_lone_surrogates_render_as_replacement_characters_and_hash()
    {
        const string json = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a\ud800b \ud83d\ude00 \\ud800"}]}]}""";
        var rendered = ContentHtmlRenderer.Render(json);
        Assert.Equal("<p>a\uFFFDb &#128512; \\ud800</p>", rendered.Html);
        Assert.Equal("a\uFFFDb \U0001F600 \\ud800", rendered.PlainText);
        Assert.Equal(CanonicalJson.Hash(json), rendered.ContentHash);
        Assert.NotEqual(CanonicalJson.Hash(json.Replace("a", "c", StringComparison.Ordinal)), rendered.ContentHash);
    }

    [Fact]
    public void Unparseable_or_absurdly_deep_script_content_renders_empty()
    {
        var deep = string.Concat(Enumerable.Repeat("[", 300)) + string.Concat(Enumerable.Repeat("]", 300));
        Assert.Equal("", ContentHtmlRenderer.Render(deep).Html);
        Assert.Equal("", ContentHtmlRenderer.Render("[1,2]").Html);
    }

    [Fact]
    public void Used_styles_include_implied_heading_styles_in_catalog_spelling()
    {
        using var document = JsonDocument.Parse(
            """{"type":"doc","content":[{"type":"heading","attrs":{"level":2}},{"type":"heading","attrs":{"level":1,"styleId":"title"}},{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"charStyle","attrs":{"styleId":"STRONG"}}]}]}]}""");
        Assert.Equal(["Heading2", "Strong", "Title"], Schema.UsedStyles(document.RootElement).Order(StringComparer.Ordinal));
    }

    private static string FixturePath(string fixture, string extension) => Path.Combine(AppContext.BaseDirectory, "ContentFixtures", fixture + extension);

    private static string SourceFixtures([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "ContentFixtures");
}
