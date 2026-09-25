using System.Text.Json;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Content;

namespace DocHub.Api.Tests;

/// <summary>Unit tests of the style property schema and the generated stylesheet (docs/content-format.md §2).</summary>
public sealed class StylePropertiesTests
{
    private static readonly StyleProperties Rules = new();

    [Theory]
    [InlineData(ContentStyleKind.Paragraph, """{"fontFamily":"Aptos","fontSize":22,"color":"#000000","spacingAfter":160,"lineSpacing":259,"lineRule":"auto","keepWithNext":true,"contextualSpacing":true}""")]
    [InlineData(ContentStyleKind.Character, """{"bold":true,"italic":false,"underline":"double","highlight":"#FFFF00","smallCaps":true}""")]
    [InlineData(ContentStyleKind.Table, """{"borders":{"top":{"style":"single","size":4,"color":"#000000"},"insideV":{"style":"none"}},"shading":"#EEEEEE"}""")]
    public void Valid_properties_pass(ContentStyleKind kind, string json) =>
        Assert.Empty(Rules.Validate(JsonDocument.Parse(json).RootElement, kind));

    [Theory]
    [InlineData("""{"fontSize":1}""", "properties.fontSize")]
    [InlineData("""{"fontSize":22.5}""", "properties.fontSize")]
    [InlineData("""{"color":"#12345"}""", "properties.color")]
    [InlineData("{\"color\":\"#FFFFFF\\n\"}", "properties.color")]
    [InlineData("{\"borders\":{\"top\":{\"style\":\"single\",\"color\":\"#000000\\n\"}}}", "properties.borders.top.color")]
    [InlineData("""{"fontFamily":"Arial; } body { display:none"}""", "properties.fontFamily")]
    [InlineData("""{"spacingBefore":31681}""", "properties.spacingBefore")]
    [InlineData("""{"lineRule":"double"}""", "properties.lineRule")]
    [InlineData("""{"bold":"yes"}""", "properties.bold")]
    [InlineData("""{"borders":{"diagonal":{"style":"single"}}}""", "properties.borders.diagonal")]
    [InlineData("""{"borders":{"top":{"size":4}}}""", "properties.borders.top.style")]
    [InlineData("""{"borders":{"top":{"style":"wavy"}}}""", "properties.borders.top.style")]
    [InlineData("""{"script":"alert(1)"}""", "properties.script")]
    public void Invalid_properties_are_reported_by_path(string json, string path) =>
        Assert.Contains(path, Rules.Validate(JsonDocument.Parse(json).RootElement, ContentStyleKind.Paragraph).Keys);

    [Fact]
    public void Character_styles_reject_paragraph_properties() =>
        Assert.Contains("properties.indentLeft", Rules.Validate(JsonDocument.Parse("""{"indentLeft":720}""").RootElement, ContentStyleKind.Character).Keys);

    [Fact]
    public void Properties_must_be_an_object() =>
        Assert.Contains("properties", Rules.Validate(JsonDocument.Parse("[]").RootElement, ContentStyleKind.Paragraph).Keys);

    [Fact]
    public void Css_merges_the_based_on_chain_and_converts_word_units()
    {
        var css = StyleProperties.ToCss(
        [
            Style("Normal", ContentStyleKind.Paragraph, null, """{"fontFamily":"Aptos","fontSize":22,"lineSpacing":259,"lineRule":"auto","spacingAfter":160}"""),
            Style("Heading1", ContentStyleKind.Paragraph, "Normal", """{"fontSize":40,"color":"#0F4761","spacingBefore":360,"keepWithNext":true,"hanging":360}"""),
            Style("Strong", ContentStyleKind.Character, null, """{"bold":true,"underline":true,"strike":true}"""),
        ]);

        var heading = Rule(css, "Heading1");
        Assert.Contains("font-family: \"Aptos\"", heading, StringComparison.Ordinal);
        Assert.Contains("font-size: 20pt", heading, StringComparison.Ordinal);
        Assert.Contains("color: #0F4761", heading, StringComparison.Ordinal);
        Assert.Contains("margin-top: 18pt", heading, StringComparison.Ordinal);
        Assert.Contains("margin-bottom: 8pt", heading, StringComparison.Ordinal);
        Assert.Contains("line-height: 1.079", heading, StringComparison.Ordinal);
        Assert.Contains("text-indent: -18pt", heading, StringComparison.Ordinal);
        Assert.Contains("break-after: avoid", heading, StringComparison.Ordinal);
        Assert.Contains("font-size: 11pt", Rule(css, "Normal"), StringComparison.Ordinal);
        Assert.Contains("text-decoration: underline line-through", Rule(css, "Strong"), StringComparison.Ordinal);
    }

    [Fact]
    public void Css_of_table_styles_has_outer_and_inside_borders()
    {
        var css = StyleProperties.ToCss([Style("TableGrid", ContentStyleKind.Table, null,
            """{"borders":{"top":{"style":"single","size":4,"color":"#000000"},"insideH":{"style":"dotted","size":8,"color":"#FF0000"}}}""")]);

        Assert.Contains(".ds-style-TableGrid { border-top: 0.5pt solid #000000; }", css, StringComparison.Ordinal);
        Assert.Contains(".ds-style-TableGrid td, .ds-style-TableGrid th { border-top: 1pt dotted #FF0000; border-bottom: 1pt dotted #FF0000; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Css_survives_a_based_on_cycle()
    {
        var css = StyleProperties.ToCss([Style("A", ContentStyleKind.Paragraph, "B", """{"bold":true}"""), Style("B", ContentStyleKind.Paragraph, "A", """{"italic":true}""")]);

        Assert.Contains("font-style: italic", Rule(css, "A"), StringComparison.Ordinal);
    }

    [Fact]
    public void Css_resolves_base_styles_case_insensitively()
    {
        var css = StyleProperties.ToCss([Style("Heading1", ContentStyleKind.Paragraph, null, """{"fontSize":40}"""), Style("Probe", ContentStyleKind.Paragraph, "heading1", """{"bold":true}""")]);

        Assert.Contains("font-size: 20pt", Rule(css, "Probe"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("not json")]
    public void Css_ignores_properties_that_are_not_an_object(string json)
    {
        var css = StyleProperties.ToCss([Style("Base", ContentStyleKind.Paragraph, null, json), Style("Child", ContentStyleKind.Paragraph, "Base", """{"bold":true}""")]);

        Assert.Contains("font-weight: 700", Rule(css, "Child"), StringComparison.Ordinal);
        Assert.Contains(".ds-style-Base {", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_line_spacing_is_in_points() =>
        Assert.Contains("line-height: 12pt", Rule(StyleProperties.ToCss([Style("X", ContentStyleKind.Paragraph, null, """{"lineSpacing":240,"lineRule":"exact"}""")]), "X"), StringComparison.Ordinal);

    private static ContentStyle Style(string id, ContentStyleKind kind, string? basedOn, string json) =>
        new() { StyleId = id, Name = id, Kind = kind, BasedOnStyleId = basedOn, PropertiesJson = json };

    private static string Rule(string css, string styleId)
    {
        var start = css.IndexOf($".ds-style-{styleId} {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no rule for {styleId} in\n{css}");
        return css[start..css.IndexOf('}', start)];
    }
}
