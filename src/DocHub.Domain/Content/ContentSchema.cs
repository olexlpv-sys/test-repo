using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocHub.Domain.Content;

/// <summary>Kinds of attribute values of DocHub Content Schema v1.</summary>
public enum AttributeKind
{
    Boolean,
    WholeNumber,
    Enum,
    Color,
    StyleId,
    FontFamily,
    Href,
    Borders,
    WholeNumberArray,
    Opaque,
}

/// <summary>An allowed attribute: its kind, range or values, and the default that canonicalization removes.</summary>
public sealed record AttributeRule(AttributeKind Kind, int Min = 0, int Max = 0, IReadOnlyList<string>? Values = null, object? Default = null);

/// <summary>An allowed node or mark type with its attributes and (for nodes) allowed children.</summary>
public sealed record TypeRule(IReadOnlyDictionary<string, AttributeRule> Attributes, IReadOnlyList<string>? Children = null, bool Inline = false);

/// <summary>
/// DocHub Content Schema v1 (docs/content-format.md §2): TipTap/ProseMirror JSON whose attributes mirror WordprocessingML.
/// Units are Word's: twips for spacing/indents/widths, half-points for font sizes, eighths of a point for borders.
/// </summary>
public static partial class ContentSchema
{
    public const int Version = 1;
    public const int MaxDepth = 20;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxOpaqueBytes = 16 * 1024;
    public const int MaxTwips = 31680;
    /// <summary>Empty content in canonical form (keys sorted).</summary>
    public const string EmptyDocument = """{"content":[],"type":"doc"}""";

    private static readonly string[] Blocks = ["paragraph", "heading", "bulletList", "orderedList", "table", "horizontalRule", "pageBreak"];
    private static readonly string[] CellBlocks = ["paragraph", "heading", "bulletList", "orderedList"];
    private static readonly string[] Inlines = ["text", "hardBreak"];

    private static AttributeRule Twips(int min = 0) => new(AttributeKind.WholeNumber, min, MaxTwips);

    private static readonly Dictionary<string, AttributeRule> ParagraphAttributes = new(StringComparer.Ordinal)
    {
        ["styleId"] = new(AttributeKind.StyleId),
        ["align"] = new(AttributeKind.Enum, Values: ["left", "center", "right", "justify"]),
        ["indentLeft"] = Twips(),
        ["indentRight"] = Twips(),
        ["indentFirstLine"] = Twips(),
        ["hanging"] = Twips(),
        ["spacingBefore"] = Twips(),
        ["spacingAfter"] = Twips(),
        ["lineSpacing"] = new(AttributeKind.WholeNumber, 1, MaxTwips),
        ["lineRule"] = new(AttributeKind.Enum, Values: ["auto", "exact", "atLeast"]),
        ["keepWithNext"] = new(AttributeKind.Boolean, Default: false),
        ["pageBreakBefore"] = new(AttributeKind.Boolean, Default: false),
        ["wordExt"] = new(AttributeKind.Opaque),
    };

    private static readonly Dictionary<string, AttributeRule> ListAttributes = new(StringComparer.Ordinal)
    {
        ["listStyle"] = new(AttributeKind.Enum, Values: ["bullet", "circle", "square", "decimal", "lowerLetter", "upperLetter", "lowerRoman", "upperRoman"]),
        ["start"] = new(AttributeKind.WholeNumber, 1, 32767, Default: 1L),
        ["level"] = new(AttributeKind.WholeNumber, 0, 8, Default: 0L),
        ["wordExt"] = new(AttributeKind.Opaque),
    };

    private static readonly Dictionary<string, AttributeRule> CellAttributes = new(StringComparer.Ordinal)
    {
        ["colspan"] = new(AttributeKind.WholeNumber, 1, 63, Default: 1L),
        ["rowspan"] = new(AttributeKind.WholeNumber, 1, 1000, Default: 1L),
        ["width"] = Twips(),
        ["verticalAlign"] = new(AttributeKind.Enum, Values: ["top", "center", "bottom"]),
        ["shading"] = new(AttributeKind.Color),
        ["borders"] = new(AttributeKind.Borders),
        ["wordExt"] = new(AttributeKind.Opaque),
    };

    private static readonly Dictionary<string, AttributeRule> None = new(StringComparer.Ordinal);

    /// <summary>Node types with their attributes and allowed children.</summary>
    public static readonly IReadOnlyDictionary<string, TypeRule> Nodes = new Dictionary<string, TypeRule>(StringComparer.Ordinal)
    {
        ["doc"] = new(None, Blocks),
        ["paragraph"] = new(ParagraphAttributes, Inlines),
        ["heading"] = new(new Dictionary<string, AttributeRule>(ParagraphAttributes, StringComparer.Ordinal) { ["level"] = new(AttributeKind.WholeNumber, 1, 6) }, Inlines),
        ["bulletList"] = new(ListAttributes, ["listItem"]),
        ["orderedList"] = new(ListAttributes, ["listItem"]),
        ["listItem"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal) { ["wordExt"] = new(AttributeKind.Opaque) }, CellBlocks),
        ["table"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal)
        {
            ["styleId"] = new(AttributeKind.StyleId),
            ["width"] = new(AttributeKind.WholeNumber, 0, MaxTwips),
            ["widthType"] = new(AttributeKind.Enum, Values: ["dxa", "pct"]),
            ["align"] = new(AttributeKind.Enum, Values: ["left", "center", "right"]),
            ["borders"] = new(AttributeKind.Borders),
            ["columnWidths"] = new(AttributeKind.WholeNumberArray, 0, MaxTwips),
            ["layout"] = new(AttributeKind.Enum, Values: ["fixed", "auto"]),
            ["wordExt"] = new(AttributeKind.Opaque),
        }, ["tableRow"]),
        ["tableRow"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal)
        {
            ["height"] = Twips(),
            ["heightRule"] = new(AttributeKind.Enum, Values: ["auto", "atLeast", "exact"]),
            ["isHeader"] = new(AttributeKind.Boolean, Default: false),
            ["cantSplit"] = new(AttributeKind.Boolean, Default: false),
            ["wordExt"] = new(AttributeKind.Opaque),
        }, ["tableCell", "tableHeader"]),
        ["tableCell"] = new(CellAttributes, CellBlocks),
        ["tableHeader"] = new(CellAttributes, CellBlocks),
        ["hardBreak"] = new(None, Inline: true),
        ["pageBreak"] = new(None),
        ["horizontalRule"] = new(None),
        ["text"] = new(None, Inline: true),
    };

    /// <summary>Mark types (character formatting, <c>w:rPr</c>).</summary>
    public static readonly IReadOnlyDictionary<string, TypeRule> Marks = new Dictionary<string, TypeRule>(StringComparer.Ordinal)
    {
        ["bold"] = new(None),
        ["italic"] = new(None),
        ["underline"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal) { ["style"] = new(AttributeKind.Enum, Values: ["single", "double", "dotted"], Default: "single") }),
        ["strike"] = new(None),
        ["subscript"] = new(None),
        ["superscript"] = new(None),
        ["textStyle"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal)
        {
            ["fontFamily"] = new(AttributeKind.FontFamily),
            ["fontSize"] = new(AttributeKind.WholeNumber, 2, 400),
            ["color"] = new(AttributeKind.Color),
        }),
        ["highlight"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal) { ["color"] = new(AttributeKind.Color) }),
        ["smallCaps"] = new(None),
        ["allCaps"] = new(None),
        ["link"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal) { ["href"] = new(AttributeKind.Href) }),
        ["charStyle"] = new(new Dictionary<string, AttributeRule>(StringComparer.Ordinal) { ["styleId"] = new(AttributeKind.StyleId) }),
    };

    public static readonly IReadOnlyList<string> BorderSides = ["top", "left", "bottom", "right", "insideH", "insideV"];
    public static readonly IReadOnlyList<string> BorderStyles = ["none", "single", "double", "dotted", "dashed", "thick"];

    public static bool IsColor(string value) => ColorPattern().IsMatch(value);

    /// <summary>
    /// Stored JSON a support script may have written, made readable: lone surrogates — raw UTF-16 code units or escaped
    /// (<c>"\ud800"</c>, which <c>ISJSON</c> accepts) — become U+FFFD; everything else is kept as is. Used before rendering
    /// or returning stored content, never before hashing it.
    /// </summary>
    public static string RepairLoneSurrogates(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StringBuilder? repaired = null;
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < json.Length && char.IsLowSurrogate(json[i + 1]))
                {
                    repaired?.Append(c).Append(json[i + 1]);
                    i++;
                    continue;
                }

                repaired ??= new StringBuilder(json, 0, i, json.Length);
                repaired.Append('\uFFFD');
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
            }
            else if (c == '\\' && inString && i + 1 < json.Length)
            {
                // An escape: \uXXXX (possibly one half of a surrogate pair) or a two-character escape.
                if (json[i + 1] == 'u' && EscapedUnit(json, i) is { } unit && char.IsSurrogate(unit))
                {
                    if (char.IsHighSurrogate(unit) && EscapedUnit(json, i + 6) is { } low && char.IsLowSurrogate(low))
                    {
                        repaired?.Append(json, i, 12);
                        i += 11;
                        continue;
                    }

                    repaired ??= new StringBuilder(json, 0, i, json.Length);
                    repaired.Append("\\uFFFD");
                    i += 5;
                    continue;
                }

                repaired?.Append(c).Append(json[i + 1]);
                i++;
                continue;
            }

            repaired?.Append(c);
        }

        return repaired?.ToString() ?? json;
    }

    /// <summary>The code unit of a <c>\uXXXX</c> escape at <paramref name="index"/>, if there is one.</summary>
    private static char? EscapedUnit(string json, int index) =>
        index + 5 < json.Length && json[index] == '\\' && json[index + 1] == 'u'
            && ushort.TryParse(json.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit)
            ? (char)unit
            : null;

    /// <summary>http, https and mailto only; no whitespace or control characters.</summary>
    public static bool IsSafeHref(string value) =>
        HrefPattern().IsMatch(value) && !value.Any(char.IsControl);

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"^(https?://[^\s""'<>]+|mailto:[^\s""'<>]+)\z", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    /// <summary>The machine-readable schema (for <c>GET /api/content-schema</c>).</summary>
    public static object Describe(IEnumerable<string> fontFamilies) => new
    {
        version = Version,
        maxDepth = MaxDepth,
        maxBytes = MaxBytes,
        units = new { spacing = "twips", fontSize = "half-points", borderSize = "eighths of a point", pctWidth = "fiftieths of a percent" },
        fontFamilies = fontFamilies.ToList(),
        borderSides = BorderSides,
        borderStyles = BorderStyles,
        nodes = Nodes.ToDictionary(n => n.Key, n => new { attributes = n.Value.Attributes.ToDictionary(a => a.Key, a => DescribeAttribute(a.Value)), children = n.Value.Children }),
        marks = Marks.ToDictionary(m => m.Key, m => new { attributes = m.Value.Attributes.ToDictionary(a => a.Key, a => DescribeAttribute(a.Value)) }),
    };

    private static object DescribeAttribute(AttributeRule rule) => new
    {
        kind = rule.Kind.ToString(),
        min = rule.Kind is AttributeKind.WholeNumber or AttributeKind.WholeNumberArray ? rule.Min : (int?)null,
        max = rule.Kind is AttributeKind.WholeNumber or AttributeKind.WholeNumberArray ? rule.Max : (int?)null,
        values = rule.Values,
        @default = rule.Default,
    };
}
