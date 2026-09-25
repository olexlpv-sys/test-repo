using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocHub.Domain.Entities;

namespace DocHub.Infrastructure.Content;

/// <summary>
/// The property schema of the Word-compatible style catalog (docs/content-format.md §2): validation of a style's
/// <c>PropertiesJson</c> and the stylesheet generated from the catalog. Units are Word's: font sizes in half-points,
/// spacing and indents in twips, border sizes in eighths of a point.
/// </summary>
public sealed partial class StyleProperties(IEnumerable<string> fontFamilies)
{
    public const int MaxTwips = 31680;

    private static readonly string[] Alignments = ["left", "center", "right", "justify"];
    private static readonly string[] LineRules = ["auto", "exact", "atLeast"];
    private static readonly string[] Underlines = ["single", "double", "dotted"];
    private static readonly string[] BorderStyles = ["none", "single", "double", "dotted", "dashed", "thick"];
    private static readonly string[] BorderSides = ["top", "left", "bottom", "right", "insideH", "insideV"];

    private static readonly HashSet<string> RunFlags = ["bold", "italic", "strike", "smallCaps", "allCaps"];
    private static readonly HashSet<string> ParagraphFlags = ["keepWithNext", "pageBreakBefore", "contextualSpacing"];
    private static readonly HashSet<string> Twips = ["spacingBefore", "spacingAfter", "indentLeft", "indentRight", "indentFirstLine", "hanging"];
    private static readonly HashSet<string> RunProperties = [.. RunFlags, "underline", "fontFamily", "fontSize", "color", "highlight"];
    private static readonly HashSet<string> ParagraphProperties = [.. ParagraphFlags, .. Twips, "align", "lineSpacing", "lineRule", "shading", "borders"];

    /// <summary>The fonts offered to authors (and the only ones a style may use).</summary>
    public static readonly string[] DefaultFontFamilies =
        ["Aptos", "Aptos Display", "Calibri", "Cambria", "Arial", "Times New Roman", "Georgia", "Verdana", "Segoe UI", "Courier New"];

    private readonly HashSet<string> _fonts = new(fontFamilies, StringComparer.Ordinal);

    public StyleProperties()
        : this(DefaultFontFamilies)
    {
    }

    /// <summary>Validation errors keyed by JSON path (<c>properties.fontSize</c>); empty when valid.</summary>
    public Dictionary<string, string[]> Validate(JsonElement properties, ContentStyleKind kind)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string path, string message)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            list.Add(message);
        }

        if (properties.ValueKind != JsonValueKind.Object)
        {
            Add("properties", "Must be a JSON object.");
            return Finish(errors);
        }

        foreach (var property in properties.EnumerateObject())
        {
            var path = $"properties.{property.Name}";
            var value = property.Value;
            if (!RunProperties.Contains(property.Name) && !ParagraphProperties.Contains(property.Name))
            {
                Add(path, "Unknown style property.");
                continue;
            }

            if (kind == ContentStyleKind.Character && ParagraphProperties.Contains(property.Name))
            {
                Add(path, "Character styles support character formatting only.");
                continue;
            }

            switch (property.Name)
            {
                case var name when RunFlags.Contains(name) || ParagraphFlags.Contains(name):
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        Add(path, "Must be true or false.");
                    }

                    break;
                case "underline":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) && !IsOneOf(value, Underlines))
                    {
                        Add(path, $"Must be true, false or one of: {string.Join(", ", Underlines)}.");
                    }

                    break;
                case "fontFamily":
                    if (value.ValueKind != JsonValueKind.String || !_fonts.Contains(value.GetString()!))
                    {
                        Add(path, $"Must be one of the configured fonts: {string.Join(", ", _fonts.Order(StringComparer.Ordinal))}.");
                    }

                    break;
                case "fontSize":
                    CheckInteger(value, 2, 400, path, "half-points", Add);
                    break;
                case "color" or "highlight" or "shading":
                    CheckColor(value, path, Add);
                    break;
                case "align":
                    if (!IsOneOf(value, Alignments))
                    {
                        Add(path, $"Must be one of: {string.Join(", ", Alignments)}.");
                    }

                    break;
                case "lineRule":
                    if (!IsOneOf(value, LineRules))
                    {
                        Add(path, $"Must be one of: {string.Join(", ", LineRules)}.");
                    }

                    break;
                case "lineSpacing":
                    CheckInteger(value, 1, MaxTwips, path, "240ths of a line (auto) or twips", Add);
                    break;
                case "borders":
                    CheckBorders(value, path, Add);
                    break;
                default:
                    CheckInteger(value, 0, MaxTwips, path, "twips", Add);
                    break;
            }
        }

        return Finish(errors);
    }

    /// <summary>
    /// The stylesheet of the catalog: one rule per style (<c>.ds-style-Heading1</c>) with the properties of its
    /// <c>BasedOn</c> chain merged in (CSS classes don't inherit from each other). Inactive styles are included — existing
    /// content may still use them.
    /// </summary>
    public static string ToCss(IReadOnlyCollection<ContentStyle> styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        var byId = styles.ToDictionary(s => s.StyleId, StringComparer.Ordinal);
        var css = new StringBuilder("/* Generated from the DocHub style catalog (app.ContentStyle). */\n");
        foreach (var style in styles.OrderBy(s => s.StyleId, StringComparer.Ordinal))
        {
            var merged = Resolve(style, byId);
            var selector = $".ds-style-{style.StyleId}";
            css.Append(selector).Append(" {\n");
            foreach (var declaration in Declarations(merged))
            {
                css.Append("  ").Append(declaration).Append(";\n");
            }

            css.Append("}\n");
            if (style.Kind == ContentStyleKind.Table && merged.TryGetValue("borders", out var borders) && borders.ValueKind == JsonValueKind.Object)
            {
                AppendTableBorders(css, selector, borders);
            }
        }

        return css.ToString();
    }

    private static Dictionary<string, JsonElement> Resolve(ContentStyle style, Dictionary<string, ContentStyle> byId)
    {
        var chain = new List<ContentStyle>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = style; current is not null && seen.Add(current.StyleId);
             current = current.BasedOnStyleId is { } basedOn && byId.TryGetValue(basedOn, out var parent) ? parent : null)
        {
            chain.Add(current);
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in Enumerable.Reverse(chain))
        {
            using var document = JsonDocument.Parse(item.PropertiesJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Character styles based on paragraph styles take only character formatting.
                if (style.Kind == ContentStyleKind.Character && !RunProperties.Contains(property.Name))
                {
                    continue;
                }

                merged[property.Name] = property.Value.Clone();
            }
        }

        return merged;
    }

    private static IEnumerable<string> Declarations(Dictionary<string, JsonElement> p)
    {
        if (Text(p, "fontFamily") is { } font && SafeFont().IsMatch(font))
        {
            yield return $"font-family: \"{font}\"";
        }

        if (Number(p, "fontSize") is { } size)
        {
            yield return $"font-size: {Points(size / 2m)}";
        }

        if (Color(p, "color") is { } color)
        {
            yield return $"color: {color}";
        }

        if (Color(p, "highlight") is { } highlight)
        {
            yield return $"background-color: {highlight}";
        }

        if (Color(p, "shading") is { } shading)
        {
            yield return $"background-color: {shading}";
        }

        if (Flag(p, "bold") is { } bold)
        {
            yield return $"font-weight: {(bold ? "700" : "400")}";
        }

        if (Flag(p, "italic") is { } italic)
        {
            yield return $"font-style: {(italic ? "italic" : "normal")}";
        }

        var underline = p.TryGetValue("underline", out var u)
            ? u.ValueKind switch { JsonValueKind.True => "single", JsonValueKind.String => u.GetString(), _ => null }
            : null;
        var strike = Flag(p, "strike") == true;
        if (underline is not null || strike)
        {
            var lines = string.Join(' ', new[] { underline is not null ? "underline" : null, strike ? "line-through" : null }.OfType<string>());
            var style = underline switch { "double" => " double", "dotted" => " dotted", _ => "" };
            yield return $"text-decoration: {lines}{style}";
        }

        if (Flag(p, "smallCaps") == true)
        {
            yield return "font-variant: small-caps";
        }

        if (Flag(p, "allCaps") == true)
        {
            yield return "text-transform: uppercase";
        }

        if (Text(p, "align") is { } align && Alignments.Contains(align))
        {
            yield return $"text-align: {align}";
        }

        if (Number(p, "spacingBefore") is { } before)
        {
            yield return $"margin-top: {Points(before / 20m)}";
        }

        if (Number(p, "spacingAfter") is { } after)
        {
            yield return $"margin-bottom: {Points(after / 20m)}";
        }

        if (Number(p, "indentLeft") is { } left)
        {
            yield return $"margin-left: {Points(left / 20m)}";
        }

        if (Number(p, "indentRight") is { } right)
        {
            yield return $"margin-right: {Points(right / 20m)}";
        }

        if (Number(p, "hanging") is { } hanging)
        {
            yield return $"text-indent: {Points(-hanging / 20m)}";
        }
        else if (Number(p, "indentFirstLine") is { } firstLine)
        {
            yield return $"text-indent: {Points(firstLine / 20m)}";
        }

        if (Number(p, "lineSpacing") is { } lineSpacing)
        {
            yield return Text(p, "lineRule") is "exact" or "atLeast"
                ? $"line-height: {Points(lineSpacing / 20m)}"
                : $"line-height: {(lineSpacing / 240m).ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        if (Flag(p, "keepWithNext") == true)
        {
            yield return "break-after: avoid";
        }

        if (Flag(p, "pageBreakBefore") == true)
        {
            yield return "break-before: page";
        }
    }

    private static void AppendTableBorders(StringBuilder css, string selector, JsonElement borders)
    {
        css.Append(selector).Append(" { border-collapse: collapse; }\n");
        var outer = new List<string>();
        var cell = new List<string>();
        foreach (var side in BorderSides)
        {
            if (!borders.TryGetProperty(side, out var border) || Border(border) is not { } value)
            {
                continue;
            }

            switch (side)
            {
                case "insideH":
                    cell.Add($"border-top: {value}");
                    cell.Add($"border-bottom: {value}");
                    break;
                case "insideV":
                    cell.Add($"border-left: {value}");
                    cell.Add($"border-right: {value}");
                    break;
                default:
                    outer.Add($"border-{side}: {value}");
                    break;
            }
        }

        if (outer.Count > 0)
        {
            css.Append(selector).Append(" { ").Append(string.Join("; ", outer)).Append("; }\n");
        }

        if (cell.Count > 0)
        {
            css.Append(selector).Append(" td, ").Append(selector).Append(" th { ").Append(string.Join("; ", cell)).Append("; }\n");
        }
    }

    private static string? Border(JsonElement border)
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
        if (style is null)
        {
            return null;
        }

        if (style == "none")
        {
            return "none";
        }

        var eighths = border.TryGetProperty("size", out var size) && size.TryGetInt32(out var n) ? n : 4;
        var color = border.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String && ColorPattern().IsMatch(c.GetString()!) ? c.GetString() : "#000000";
        return $"{Points(eighths / 8m)} {style} {color}";
    }

    private static string Points(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "pt";

    private static string? Text(Dictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(Dictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static bool? Flag(Dictionary<string, JsonElement> p, string name) =>
        p.TryGetValue(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static string? Color(Dictionary<string, JsonElement> p, string name) =>
        Text(p, name) is { } color && ColorPattern().IsMatch(color) ? color : null;

    private static bool IsOneOf(JsonElement value, string[] allowed) =>
        value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString(), StringComparer.Ordinal);

    private static void CheckInteger(JsonElement value, int min, int max, string path, string unit, Action<string, string> add)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < min || n > max)
        {
            add(path, $"Must be an integer from {min} to {max} ({unit}).");
        }
    }

    private static void CheckColor(JsonElement value, string path, Action<string, string> add)
    {
        if (value.ValueKind != JsonValueKind.String || !ColorPattern().IsMatch(value.GetString()!))
        {
            add(path, "Must be a color #RRGGBB.");
        }
    }

    private static void CheckBorders(JsonElement value, string path, Action<string, string> add)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            add(path, "Must be an object with top, left, bottom, right, insideH and/or insideV.");
            return;
        }

        foreach (var side in value.EnumerateObject())
        {
            var sidePath = $"{path}.{side.Name}";
            if (!BorderSides.Contains(side.Name, StringComparer.Ordinal))
            {
                add(sidePath, "Unknown border side.");
                continue;
            }

            if (side.Value.ValueKind != JsonValueKind.Object)
            {
                add(sidePath, "Must be an object { style, size, color }.");
                continue;
            }

            foreach (var attribute in side.Value.EnumerateObject())
            {
                var attributePath = $"{sidePath}.{attribute.Name}";
                switch (attribute.Name)
                {
                    case "style":
                        if (!IsOneOf(attribute.Value, BorderStyles))
                        {
                            add(attributePath, $"Must be one of: {string.Join(", ", BorderStyles)}.");
                        }

                        break;
                    case "size":
                        CheckInteger(attribute.Value, 0, 96, attributePath, "eighths of a point", add);
                        break;
                    case "color":
                        CheckColor(attribute.Value, attributePath, add);
                        break;
                    default:
                        add(attributePath, "Unknown border attribute.");
                        break;
                }
            }

            if (!side.Value.TryGetProperty("style", out _))
            {
                add($"{sidePath}.style", "Required.");
            }
        }
    }

    private static Dictionary<string, string[]> Finish(Dictionary<string, List<string>> errors) =>
        errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    // Defence in depth for CSS output (fonts are also validated against the configured list).
    [GeneratedRegex("^[A-Za-z0-9 \\-]{1,64}$")]
    private static partial Regex SafeFont();
}
