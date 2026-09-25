using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocHub.Domain.Content;

/// <summary>The derived columns of a node's content (T09 rule 4).</summary>
public sealed record RenderedContent(string Html, string PlainText, byte[] ContentHash);

/// <summary>
/// Content JSON → HTML and plain text (T09 rule 4). Text is always HTML-encoded; catalog styles become CSS classes
/// (<c>ds-style-{styleId}</c>, see the stylesheet of the style catalog); direct formatting becomes inline CSS built only from
/// values that pass the schema's whitelists. The renderer also runs on content a support script stored without validation,
/// so it never trusts the input: unknown types and invalid attributes are skipped, never echoed.
/// </summary>
public static partial class ContentHtmlRenderer
{
    /// <summary>Nesting beyond this is not rendered (valid content is at most <see cref="ContentSchema.MaxDepth"/> deep).</summary>
    private const int MaxRenderDepth = 64;

    public static RenderedContent Render(string contentJson)
    {
        ArgumentNullException.ThrowIfNull(contentJson);
        var hash = CanonicalJson.Hash(contentJson);
        try
        {
            // Lone surrogates (only a script can store them) become U+FFFD so every string is readable.
            using var document = JsonDocument.Parse(ContentSchema.RepairLoneSurrogates(contentJson), new JsonDocumentOptions { MaxDepth = CanonicalJson.MaxDepth });
            var html = new StringBuilder();
            RenderNode(html, document.RootElement, 0);
            return new RenderedContent(html.ToString(), PlainText(document.RootElement), hash);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return new RenderedContent("", "", hash);
        }
    }

    private static void RenderNode(StringBuilder html, JsonElement node, int depth)
    {
        if (depth > MaxRenderDepth || node.ValueKind != JsonValueKind.Object || Type(node) is not { } type)
        {
            return;
        }

        var attrs = node.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        switch (type)
        {
            case "doc":
                Children(html, node, depth);
                break;
            case "paragraph":
                Block(html, "p", node, depth, Classes(StyleId(attrs)), ParagraphCss(attrs));
                break;
            case "heading":
                var level = Whole(attrs, "level", 1, 6) ?? 1;
                Block(html, $"h{level}", node, depth, Classes(StyleId(attrs) ?? $"Heading{level}"), ParagraphCss(attrs));
                break;
            case "bulletList":
            case "orderedList":
                var tag = type == "bulletList" ? "ul" : "ol";
                var list = new StringBuilder();
                if (Enum(attrs, "listStyle", ContentSchema.Nodes[type].Attributes["listStyle"]) is { } listStyle)
                {
                    list.Append("list-style-type:").Append(ListStyleCss(listStyle)).Append(';');
                }

                var extra = new StringBuilder();
                if (type == "orderedList" && Whole(attrs, "start", 1, 32767) is { } start && start != 1)
                {
                    extra.Append(" start=\"").Append(start.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                if (Whole(attrs, "level", 0, 8) is { } listLevel && listLevel != 0)
                {
                    extra.Append(" data-level=\"").Append(listLevel.ToString(CultureInfo.InvariantCulture)).Append('"');
                }

                Block(html, tag, node, depth, null, list.ToString(), extra.ToString());
                break;
            case "listItem":
                Block(html, "li", node, depth, null, "");
                break;
            case "table":
                Table(html, node, attrs, depth);
                break;
            case "tableRow":
                RenderRow(html, node, depth, "");
                break;
            case "tableCell":
            case "tableHeader":
                Cell(html, type == "tableHeader" ? "th" : "td", node, attrs, depth);
                break;
            case "hardBreak":
                html.Append("<br>");
                break;
            case "pageBreak":
                html.Append("<div class=\"ds-page-break\" style=\"break-after:page\"></div>");
                break;
            case "horizontalRule":
                html.Append("<hr>");
                break;
            case "text":
                Text(html, node);
                break;
        }
    }

    private static void Block(StringBuilder html, string tag, JsonElement node, int depth, string? classes, string css, string extraAttributes = "")
    {
        html.Append('<').Append(tag);
        classes = string.Join(' ', new[] { classes, DiffClass(node) }.Where(c => !string.IsNullOrEmpty(c)));
        if (classes.Length > 0)
        {
            html.Append(" class=\"").Append(classes).Append('"');
        }

        if (css.Length > 0)
        {
            html.Append(" style=\"").Append(css.TrimEnd(';')).Append('"');
        }

        html.Append(extraAttributes).Append('>');
        if (tag is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && !HasChildren(node))
        {
            html.Append("<br>"); // an empty paragraph keeps its line, as in Word and the editor
        }

        Children(html, node, depth);
        html.Append("</").Append(tag).Append('>');
    }

    /// <summary>Diff output only: <c>attrs.diff</c> ∈ inserted/deleted/changed → <c>ds-diff-{value}</c>.</summary>
    private static string? DiffClass(JsonElement node) =>
        node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("diff", out var diff)
            && diff.ValueKind == JsonValueKind.String && diff.GetString() is "inserted" or "deleted" or "changed"
            ? $"ds-diff-{diff.GetString()}"
            : null;

    private static bool HasChildren(JsonElement node) =>
        node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0;

    private static void Children(StringBuilder html, JsonElement node, int depth)
    {
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                RenderNode(html, child, depth + 1);
            }
        }
    }

    private static void Table(StringBuilder html, JsonElement node, JsonElement attrs, int depth)
    {
        var css = new StringBuilder("border-collapse:collapse;");
        if (Whole(attrs, "width", 0, ContentSchema.MaxTwips) is { } width)
        {
            // pct widths are in fiftieths of a percent (Word's w:tblW type="pct").
            css.Append("width:").Append(Enum(attrs, "widthType", ContentSchema.Nodes["table"].Attributes["widthType"]) == "pct"
                ? (Math.Min(width, 5000) / 50m).ToString("0.##", CultureInfo.InvariantCulture) + "%"
                : Points(width / 20m)).Append(';');
        }

        switch (Enum(attrs, "align", ContentSchema.Nodes["table"].Attributes["align"]))
        {
            case "center":
                css.Append("margin-left:auto;margin-right:auto;");
                break;
            case "right":
                css.Append("margin-left:auto;");
                break;
        }

        if (Enum(attrs, "layout", ContentSchema.Nodes["table"].Attributes["layout"]) == "fixed")
        {
            css.Append("table-layout:fixed;");
        }

        AppendBorders(css, attrs);
        html.Append("<table");
        var tableClasses = string.Join(' ', new[] { StyleId(attrs) is { } styleId ? Classes(styleId) : null, DiffClass(node) }.Where(c => !string.IsNullOrEmpty(c)));
        if (tableClasses.Length > 0)
        {
            html.Append(" class=\"").Append(tableClasses).Append('"');
        }

        html.Append(" style=\"").Append(css.ToString().TrimEnd(';')).Append('"');
        html.Append('>');
        if (attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("columnWidths", out var widths) && widths.ValueKind == JsonValueKind.Array)
        {
            html.Append("<colgroup>");
            foreach (var column in widths.EnumerateArray().Take(63))
            {
                html.Append(column.ValueKind == JsonValueKind.Number && column.TryGetInt32(out var w) && w is >= 0 and <= ContentSchema.MaxTwips
                    ? $"<col style=\"width:{Points(w / 20m)}\">"
                    : "<col>");
            }

            html.Append("</colgroup>");
        }

        html.Append("<tbody>");
        var insideCss = InsideBorderCss(attrs);
        if (node.TryGetProperty("content", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                RenderRow(html, row, depth + 1, insideCss);
            }
        }

        html.Append("</tbody></table>");
    }

    /// <summary>Rows are rendered here (not via <see cref="RenderNode"/>) so cells get the table's inside borders.</summary>
    private static void RenderRow(StringBuilder html, JsonElement row, int depth, string insideCss)
    {
        if (depth > MaxRenderDepth || row.ValueKind != JsonValueKind.Object || Type(row) != "tableRow")
        {
            return;
        }

        var attrs = row.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var css = new StringBuilder();
        if (Twips(attrs, "height") is { } height)
        {
            // A table row's CSS height acts as a minimum (Word's atLeast); HTML can't enforce exact heights.
            css.Append("height:").Append(Points(height / 20m)).Append(';');
        }

        if (Flag(attrs, "cantSplit") == true)
        {
            css.Append("break-inside:avoid;");
        }

        html.Append("<tr");
        var rowClasses = string.Join(' ', new[] { Flag(attrs, "isHeader") == true ? "ds-header-row" : null, DiffClass(row) }.Where(c => c is not null));
        if (rowClasses.Length > 0)
        {
            html.Append(" class=\"").Append(rowClasses).Append('"');
        }

        if (css.Length > 0)
        {
            html.Append(" style=\"").Append(css.ToString().TrimEnd(';')).Append('"');
        }

        html.Append('>');
        if (row.TryGetProperty("content", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            foreach (var cell in cells.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object && Type(cell) is "tableCell" or "tableHeader")
                {
                    var cellAttrs = cell.TryGetProperty("attrs", out var ca) && ca.ValueKind == JsonValueKind.Object ? ca : default;
                    Cell(html, Type(cell) == "tableHeader" ? "th" : "td", cell, cellAttrs, depth + 1, insideCss);
                }
            }
        }

        html.Append("</tr>");
    }

    private static void Cell(StringBuilder html, string tag, JsonElement node, JsonElement attrs, int depth, string insideCss = "")
    {
        if (depth > MaxRenderDepth)
        {
            return;
        }

        var css = new StringBuilder(insideCss);
        if (Twips(attrs, "width") is { } width)
        {
            css.Append("width:").Append(Points(width / 20m)).Append(';');
        }

        if (Enum(attrs, "verticalAlign", ContentSchema.Nodes["tableCell"].Attributes["verticalAlign"]) is { } verticalAlign)
        {
            css.Append("vertical-align:").Append(verticalAlign == "center" ? "middle" : verticalAlign).Append(';');
        }

        if (ColorValue(attrs, "shading") is { } shading)
        {
            css.Append("background-color:").Append(shading).Append(';');
        }

        AppendBorders(css, attrs);
        var extra = new StringBuilder();
        if (Whole(attrs, "colspan", 1, 63) is { } colspan && colspan > 1)
        {
            extra.Append(" colspan=\"").Append(colspan.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        if (Whole(attrs, "rowspan", 1, 1000) is { } rowspan && rowspan > 1)
        {
            extra.Append(" rowspan=\"").Append(rowspan.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        Block(html, tag, node, depth, null, css.ToString(), extra.ToString());
    }

    private static void AppendBorders(StringBuilder css, JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object || !attrs.TryGetProperty("borders", out var borders) || borders.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var side in new[] { "top", "right", "bottom", "left" })
        {
            if (borders.TryGetProperty(side, out var border) && BorderCss(border) is { } value)
            {
                css.Append("border-").Append(side).Append(':').Append(value).Append(';');
            }
        }
    }

    /// <summary>The table's insideH/insideV borders as cell CSS (Word draws them between cells).</summary>
    private static string InsideBorderCss(JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object || !attrs.TryGetProperty("borders", out var borders) || borders.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var css = new StringBuilder();
        if (borders.TryGetProperty("insideH", out var h) && BorderCss(h) is { } horizontal)
        {
            css.Append("border-top:").Append(horizontal).Append(";border-bottom:").Append(horizontal).Append(';');
        }

        if (borders.TryGetProperty("insideV", out var v) && BorderCss(v) is { } vertical)
        {
            css.Append("border-left:").Append(vertical).Append(";border-right:").Append(vertical).Append(';');
        }

        return css.ToString();
    }

    private static string? BorderCss(JsonElement border)
    {
        if (border.ValueKind != JsonValueKind.Object || !border.TryGetProperty("style", out var s) || s.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var style = s.GetString() switch
        {
            "none" => "none",
            "double" => "double",
            "dotted" => "dotted",
            "dashed" => "dashed",
            "single" or "thick" => "solid",
            _ => null,
        };
        if (style is null or "none")
        {
            return style;
        }

        var eighths = border.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out var n) && n is >= 0 and <= 96 ? n : 4;
        var color = border.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String && ContentSchema.IsColor(c.GetString()!) ? c.GetString() : "#000000";
        return $"{Points(eighths / 8m)} {style} {color}";
    }

    private static string ParagraphCss(JsonElement attrs)
    {
        if (attrs.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var rules = ContentSchema.Nodes["paragraph"].Attributes;
        var css = new StringBuilder();
        if (Enum(attrs, "align", rules["align"]) is { } align)
        {
            css.Append("text-align:").Append(align).Append(';');
        }

        void Length(string attribute, string property)
        {
            if (Twips(attrs, attribute) is { } twips)
            {
                css.Append(property).Append(':').Append(Points(twips / 20m)).Append(';');
            }
        }

        Length("spacingBefore", "margin-top");
        Length("spacingAfter", "margin-bottom");
        Length("indentLeft", "margin-left");
        Length("indentRight", "margin-right");
        if (Twips(attrs, "hanging") is { } hanging)
        {
            css.Append("text-indent:").Append(Points(-hanging / 20m)).Append(';');
        }
        else
        {
            Length("indentFirstLine", "text-indent");
        }

        if (Whole(attrs, "lineSpacing", 1, ContentSchema.MaxTwips) is { } lineSpacing)
        {
            css.Append("line-height:").Append(Enum(attrs, "lineRule", rules["lineRule"]) is "exact" or "atLeast"
                ? Points(lineSpacing / 20m)
                : (lineSpacing / 240m).ToString("0.###", CultureInfo.InvariantCulture)).Append(';');
        }

        if (Flag(attrs, "keepWithNext") == true)
        {
            css.Append("break-after:avoid;");
        }

        if (Flag(attrs, "pageBreakBefore") == true)
        {
            css.Append("break-before:page;");
        }

        return css.ToString();
    }

    private static void Text(StringBuilder html, JsonElement node)
    {
        if (!node.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var close = new Stack<string>();
        if (node.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
        {
            // Marks nest in a fixed order so equal formatting always renders equally.
            foreach (var mark in marks.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object && Type(m) is not null).OrderBy(m => Type(m), StringComparer.Ordinal))
            {
                var attrs = mark.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                if (OpenMark(Type(mark)!, attrs) is { } open)
                {
                    html.Append(open.Open);
                    close.Push(open.Close);
                }
            }
        }

        html.Append(WebUtility.HtmlEncode(text.GetString()));
        while (close.Count > 0)
        {
            html.Append(close.Pop());
        }
    }

    private static (string Open, string Close)? OpenMark(string type, JsonElement attrs)
    {
        switch (type)
        {
            case "bold":
                return ("<strong>", "</strong>");
            case "italic":
                return ("<em>", "</em>");
            case "strike":
                return ("<s>", "</s>");
            case "subscript":
                return ("<sub>", "</sub>");
            case "superscript":
                return ("<sup>", "</sup>");
            case "smallCaps":
                return ("<span style=\"font-variant:small-caps\">", "</span>");
            case "allCaps":
                return ("<span style=\"text-transform:uppercase\">", "</span>");
            case "underline":
                var style = Enum(attrs, "style", ContentSchema.Marks["underline"].Attributes["style"]);
                return style is "double" or "dotted" ? ($"<u style=\"text-decoration-style:{style}\">", "</u>") : ("<u>", "</u>");
            case "textStyle":
                var css = new StringBuilder();
                if (attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("fontFamily", out var font) && font.ValueKind == JsonValueKind.String
                    && SafeFont().IsMatch(font.GetString()!))
                {
                    css.Append("font-family:&quot;").Append(font.GetString()).Append("&quot;;");
                }

                if (Whole(attrs, "fontSize", 2, 400) is { } size)
                {
                    css.Append("font-size:").Append(Points(size / 2m)).Append(';');
                }

                if (ColorValue(attrs, "color") is { } color)
                {
                    css.Append("color:").Append(color).Append(';');
                }

                return css.Length == 0 ? null : ($"<span style=\"{css.ToString().TrimEnd(';')}\">", "</span>");
            case "highlight":
                return ColorValue(attrs, "color") is { } highlight
                    ? ($"<mark style=\"background-color:{highlight}\">", "</mark>")
                    : ("<mark>", "</mark>");
            case "link":
                return attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String
                    && ContentSchema.IsSafeHref(href.GetString()!)
                    ? ($"<a href=\"{WebUtility.HtmlEncode(href.GetString())}\" rel=\"noopener noreferrer\">", "</a>")
                    : null;
            case "charStyle":
                return StyleId(attrs) is { } styleId ? ($"<span class=\"{Classes(styleId)}\">", "</span>") : null;
            // Diff output only (T11/T12, never valid in stored content): the change is shown on top of the formatting.
            case "diffInsert":
                return ("<ins class=\"ds-diff-insert\">", "</ins>");
            case "diffDelete":
                return ("<del class=\"ds-diff-delete\">", "</del>");
            case "diffFormat":
                return ("<span class=\"ds-diff-format\">", "</span>");
            default:
                return null;
        }
    }

    /// <summary>Plain text (T09 rule 4): a newline between blocks and table rows, a tab between table cells.</summary>
    public static string PlainText(JsonElement root)
    {
        var lines = new List<string>();
        Blocks(root, lines, 0);
        return string.Join('\n', lines);
    }

    private static void Blocks(JsonElement node, List<string> lines, int depth)
    {
        if (depth > MaxRenderDepth || node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in content.EnumerateArray())
        {
            switch (child.ValueKind == JsonValueKind.Object ? Type(child) : null)
            {
                case "paragraph" or "heading":
                    lines.Add(Inline(child, depth + 1));
                    break;
                case "bulletList" or "orderedList" or "listItem":
                    Blocks(child, lines, depth + 1);
                    break;
                case "table":
                    if (child.TryGetProperty("content", out var rows) && rows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in rows.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object && Type(r) == "tableRow"))
                        {
                            var cells = row.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                                ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Type(x) is "tableCell" or "tableHeader").Select(x => CellText(x, depth + 3))
                                : [];
                            lines.Add(string.Join('\t', cells));
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>A cell's blocks on one line (separated by spaces), so rows stay one line each.</summary>
    private static string CellText(JsonElement cell, int depth)
    {
        var lines = new List<string>();
        Blocks(cell, lines, depth);
        return string.Join(' ', lines.Where(l => l.Length > 0)).Replace('\n', ' ');
    }

    private static string Inline(JsonElement block, int depth)
    {
        var text = new StringBuilder();
        if (depth <= MaxRenderDepth && block.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var inline in content.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                switch (Type(inline))
                {
                    case "text" when inline.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String:
                        text.Append(t.GetString());
                        break;
                    case "hardBreak":
                        text.Append('\n');
                        break;
                }
            }
        }

        return text.ToString();
    }

    private static string? Type(JsonElement node) =>
        node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static string Classes(string? styleId) => styleId is null ? "" : $"ds-style-{styleId}";

    private static string? StyleId(JsonElement attrs) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("styleId", out var style) && style.ValueKind == JsonValueKind.String
            && SafeStyleId().IsMatch(style.GetString()!) ? style.GetString() : null;

    private static int? Whole(JsonElement attrs, string name, int min, int max) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var n) && n >= min && n <= max ? n : null;

    private static int? Twips(JsonElement attrs, string name) => Whole(attrs, name, 0, ContentSchema.MaxTwips);

    private static string? Enum(JsonElement attrs, string name, AttributeRule rule) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && rule.Values!.Contains(value.GetString()!, StringComparer.Ordinal) ? value.GetString() : null;

    private static bool? Flag(JsonElement attrs, string name) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    private static string? ColorValue(JsonElement attrs, string name) =>
        attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && ContentSchema.IsColor(value.GetString()!) ? value.GetString() : null;

    private static string ListStyleCss(string listStyle) => listStyle switch
    {
        "bullet" => "disc",
        "lowerLetter" => "lower-alpha",
        "upperLetter" => "upper-alpha",
        "lowerRoman" => "lower-roman",
        "upperRoman" => "upper-roman",
        _ => listStyle, // circle, square, decimal
    };

    private static string Points(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "pt";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,49}\z")]
    private static partial Regex SafeStyleId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 \-]{0,63}\z")]
    private static partial Regex SafeFont();
}
