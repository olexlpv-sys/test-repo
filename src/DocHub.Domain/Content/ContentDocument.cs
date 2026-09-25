using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocHub.Domain.Content;

/// <summary>
/// Validation and canonicalization of node content (T09 rules 2–3) against <see cref="ContentSchema"/>.
/// Style ids and fonts come from the catalog (<c>app.ContentStyle</c>, active or inactive) and the configured font list.
/// </summary>
public sealed class ContentDocument(IEnumerable<string> styleIds, IReadOnlySet<string> fontFamilies)
{
    // Style ids compare case-insensitively (database collation); canonical content uses the catalog's spelling, which is
    // also the stylesheet's CSS class.
    private readonly Dictionary<string, string> _styles = styleIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);

    /// <summary>Errors keyed by JSON path (e.g. <c>content[0].content[1].marks[0].attrs.color</c>); empty when valid.</summary>
    public Dictionary<string, string[]> Validate(JsonElement root)
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

        // Duplicate keys and strings .NET can't read (lone surrogates) first: the schema checks below read one value per key.
        WellFormed(root, "", Add);
        if (errors.Count > 0)
        {
            return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "doc")
        {
            Add("", "The content must be a document: { \"type\": \"doc\", \"content\": [ … ] }.");
        }
        else
        {
            ValidateNode(root, "", 0, Add);
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    private static void WellFormed(JsonElement element, string path, Action<string, string> add)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var property in element.EnumerateObject())
                {
                    string name;
                    try
                    {
                        name = property.Name;
                    }
                    catch (InvalidOperationException)
                    {
                        add(path, $"Property {index} has a name with invalid characters (a lone surrogate).");
                        continue;
                    }
                    finally
                    {
                        index++;
                    }

                    if (!names.Add(name))
                    {
                        add(Join(path, name), "Duplicate property.");
                        continue;
                    }

                    WellFormed(property.Value, Join(path, name), add);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WellFormed(item, $"{path}[{i++}]", add);
                }

                break;
            case JsonValueKind.String:
                try
                {
                    _ = element.GetString();
                }
                catch (InvalidOperationException)
                {
                    add(path, "The text contains invalid characters (a lone surrogate).");
                }

                break;
        }
    }

    private void ValidateNode(JsonElement node, string path, int depth, Action<string, string> add)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            add(path, "A node must be an object.");
            return;
        }

        if (depth > ContentSchema.MaxDepth)
        {
            add(path, $"Nesting is deeper than {ContentSchema.MaxDepth} levels.");
            return;
        }

        if (!node.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !ContentSchema.Nodes.TryGetValue(typeElement.GetString()!, out var rule))
        {
            add(Join(path, "type"), "Unknown node type.");
            return;
        }

        var type = typeElement.GetString()!;
        foreach (var property in node.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    break;
                case "attrs":
                    ValidateAttributes(property.Value, rule.Attributes, Join(path, "attrs"), add);
                    break;
                case "content" when rule.Children is not null:
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        add(Join(path, "content"), "Must be an array.");
                        break;
                    }

                    var index = 0;
                    foreach (var child in property.Value.EnumerateArray())
                    {
                        var childPath = $"{Join(path, "content")}[{index++}]";
                        if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("type", out var childType) && childType.ValueKind == JsonValueKind.String
                            && ContentSchema.Nodes.ContainsKey(childType.GetString()!) && !rule.Children.Contains(childType.GetString()!))
                        {
                            add(Join(childPath, "type"), $"A {type} can't contain a {childType.GetString()}.");
                            continue;
                        }

                        ValidateNode(child, childPath, depth + 1, add);
                    }

                    break;
                case "text" when type == "text":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length == 0)
                    {
                        add(Join(path, "text"), "Text must be a non-empty string.");
                    }

                    break;
                case "marks" when type == "text":
                    ValidateMarks(property.Value, Join(path, "marks"), add);
                    break;
                default:
                    add(Join(path, property.Name), $"Unknown property of a {type} node.");
                    break;
            }
        }

        if (type == "text" && !node.TryGetProperty("text", out _))
        {
            add(Join(path, "text"), "Text is required.");
        }

        if (type == "heading" && !(node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number))
        {
            add(Join(path, "attrs.level"), "A heading needs a level from 1 to 6.");
        }
    }

    private void ValidateMarks(JsonElement marks, string path, Action<string, string> add)
    {
        if (marks.ValueKind != JsonValueKind.Array)
        {
            add(path, "Must be an array.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var mark in marks.EnumerateArray())
        {
            var markPath = $"{path}[{index++}]";
            if (mark.ValueKind != JsonValueKind.Object || !mark.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !ContentSchema.Marks.TryGetValue(type.GetString()!, out var rule))
            {
                add(Join(markPath, "type"), "Unknown mark type.");
                continue;
            }

            if (!seen.Add(type.GetString()!))
            {
                add(Join(markPath, "type"), "The same mark is applied twice.");
            }

            if (type.GetString() is "link" or "charStyle" && !(mark.TryGetProperty("attrs", out var markAttrs) && markAttrs.ValueKind == JsonValueKind.Object
                    && markAttrs.TryGetProperty(type.GetString() == "link" ? "href" : "styleId", out var required) && required.ValueKind == JsonValueKind.String))
            {
                add(Join(markPath, type.GetString() == "link" ? "attrs.href" : "attrs.styleId"), "Required.");
            }

            foreach (var property in mark.EnumerateObject())
            {
                if (property.Name == "attrs")
                {
                    ValidateAttributes(property.Value, rule.Attributes, Join(markPath, "attrs"), add);
                }
                else if (property.Name != "type")
                {
                    add(Join(markPath, property.Name), "Unknown property of a mark.");
                }
            }
        }
    }

    private void ValidateAttributes(JsonElement attrs, IReadOnlyDictionary<string, AttributeRule> rules, string path, Action<string, string> add)
    {
        if (attrs.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (attrs.ValueKind != JsonValueKind.Object)
        {
            add(path, "Attributes must be an object.");
            return;
        }

        foreach (var attribute in attrs.EnumerateObject())
        {
            var attributePath = Join(path, attribute.Name);
            if (!rules.TryGetValue(attribute.Name, out var rule))
            {
                add(attributePath, "Unknown attribute.");
            }
            else if (attribute.Value.ValueKind != JsonValueKind.Null && CheckValue(attribute.Value, rule, attributePath, add) is { } error)
            {
                add(attributePath, error);
            }
        }
    }

    /// <summary>Checks one value; returns an error message (or reports nested errors itself and returns null).</summary>
    private string? CheckValue(JsonElement value, AttributeRule rule, string path, Action<string, string> add)
    {
        switch (rule.Kind)
        {
            case AttributeKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Must be true or false.";
            case AttributeKind.WholeNumber:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= rule.Min && n <= rule.Max
                    ? null
                    : $"Must be an integer from {rule.Min} to {rule.Max}.";
            case AttributeKind.Enum:
                return value.ValueKind == JsonValueKind.String && rule.Values!.Contains(value.GetString()!, StringComparer.Ordinal)
                    ? null
                    : $"Must be one of: {string.Join(", ", rule.Values!)}.";
            case AttributeKind.Color:
                return value.ValueKind == JsonValueKind.String && ContentSchema.IsColor(value.GetString()!) ? null : "Must be a color #RRGGBB.";
            case AttributeKind.StyleId:
                return value.ValueKind == JsonValueKind.String && _styles.ContainsKey(value.GetString()!) ? null : "Unknown style (not in the style catalog).";
            case AttributeKind.FontFamily:
                return value.ValueKind == JsonValueKind.String && fontFamilies.Contains(value.GetString()!) ? null : "Not one of the configured fonts.";
            case AttributeKind.Href:
                return value.ValueKind == JsonValueKind.String && ContentSchema.IsSafeHref(value.GetString()!) ? null : "Only http, https and mailto links are allowed.";
            case AttributeKind.WholeNumberArray:
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 63)
                {
                    return "Must be an array of at most 63 integers.";
                }

                return value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var w) && w >= rule.Min && w <= rule.Max)
                    ? null
                    : $"Every value must be an integer from {rule.Min} to {rule.Max}.";
            case AttributeKind.Opaque:
                return value.ValueKind == JsonValueKind.Object && System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) <= ContentSchema.MaxOpaqueBytes
                    ? null
                    : $"Must be an object of at most {ContentSchema.MaxOpaqueBytes / 1024} KB.";
            case AttributeKind.Borders:
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return "Must be an object with top, left, bottom, right, insideH and/or insideV.";
                }

                foreach (var side in value.EnumerateObject())
                {
                    var sidePath = Join(path, side.Name);
                    if (!ContentSchema.BorderSides.Contains(side.Name, StringComparer.Ordinal))
                    {
                        add(sidePath, "Unknown border side.");
                    }
                    else if (side.Value.ValueKind != JsonValueKind.Object)
                    {
                        add(sidePath, "Must be an object { style, size, color }.");
                    }
                    else
                    {
                        foreach (var part in side.Value.EnumerateObject())
                        {
                            var error = part.Name switch
                            {
                                "style" => part.Value.ValueKind == JsonValueKind.String && ContentSchema.BorderStyles.Contains(part.Value.GetString()!, StringComparer.Ordinal)
                                    ? null : $"Must be one of: {string.Join(", ", ContentSchema.BorderStyles)}.",
                                "size" => part.Value.ValueKind == JsonValueKind.Number && part.Value.TryGetInt32(out var size) && size is >= 0 and <= 96
                                    ? null : "Must be an integer from 0 to 96 (eighths of a point).",
                                "color" => part.Value.ValueKind == JsonValueKind.String && ContentSchema.IsColor(part.Value.GetString()!) ? null : "Must be a color #RRGGBB.",
                                _ => "Unknown border attribute.",
                            };
                            if (error is not null)
                            {
                                add(Join(sidePath, part.Name), error);
                            }
                        }
                    }
                }

                return null;
            default:
                return "Unsupported attribute.";
        }
    }

    /// <summary>
    /// The canonical form (T09 rule 3): nulls and default-valued attributes removed, empty attrs/marks/content removed
    /// (a document keeps its content array), marks ordered by type, adjacent text nodes with equal marks merged, empty
    /// trailing paragraphs removed, keys sorted. Call on validated content only.
    /// </summary>
    public string Canonicalize(JsonElement root)
    {
        var node = CanonicalNode(JsonNode.Parse(root.GetRawText())!.AsObject(), isDocument: true);
        return CanonicalJson.Serialize(node.ToJsonString());
    }

    /// <summary>
    /// Catalog style ids referenced by content (explicit ones and the implied HeadingN of headings without one) — for
    /// ContentStyleUsage. Ids that aren't in the catalog are left out.
    /// </summary>
    public IReadOnlySet<string> UsedStyles(JsonElement root)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Use(string styleId)
        {
            if (_styles.TryGetValue(styleId, out var canonical))
            {
                used.Add(canonical);
            }
        }

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("styleId", out var style) && style.ValueKind == JsonValueKind.String)
                {
                    Use(style.GetString()!);
                }
                else if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "heading"
                    && attrs.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var l))
                {
                    Use($"Heading{l}");
                }
            }

            if (node.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
            {
                foreach (var mark in marks.EnumerateArray())
                {
                    if (mark.ValueKind == JsonValueKind.Object && mark.TryGetProperty("attrs", out var markAttrs) && markAttrs.ValueKind == JsonValueKind.Object
                        && markAttrs.TryGetProperty("styleId", out var charStyle) && charStyle.ValueKind == JsonValueKind.String)
                    {
                        Use(charStyle.GetString()!);
                    }
                }
            }

            if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in content.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }

        Walk(root);
        return used;
    }

    private JsonObject CanonicalNode(JsonObject node, bool isDocument = false)
    {
        var type = node["type"]!.GetValue<string>();
        var rule = ContentSchema.Nodes[type];
        var result = new JsonObject { ["type"] = type };

        if (CanonicalAttributes(node["attrs"] as JsonObject, rule.Attributes) is { } attrs)
        {
            result["attrs"] = attrs;
        }

        if (type == "text")
        {
            result["text"] = node["text"]!.GetValue<string>();
            if (CanonicalMarks(node["marks"] as JsonArray) is { } marks)
            {
                result["marks"] = marks;
            }

            return result;
        }

        var children = new List<JsonObject>();
        if (node["content"] is JsonArray content)
        {
            foreach (var child in content.OfType<JsonObject>())
            {
                var canonical = CanonicalNode(child);
                // Adjacent text nodes with equal marks are one run.
                if (canonical["type"]!.GetValue<string>() == "text" && children.Count > 0 && children[^1]["type"]!.GetValue<string>() == "text"
                    && JsonNode.DeepEquals(children[^1]["marks"], canonical["marks"]))
                {
                    children[^1]["text"] = children[^1]["text"]!.GetValue<string>() + canonical["text"]!.GetValue<string>();
                    continue;
                }

                children.Add(canonical);
            }
        }

        if (isDocument)
        {
            while (children.Count > 0 && children[^1]["type"]!.GetValue<string>() == "paragraph" && children[^1]["content"] is null)
            {
                children.RemoveAt(children.Count - 1);
            }

            result["content"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray());
        }
        else if (children.Count > 0)
        {
            result["content"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray());
        }

        return result;
    }

    private JsonArray? CanonicalMarks(JsonArray? marks)
    {
        if (marks is null)
        {
            return null;
        }

        var canonical = marks.OfType<JsonObject>()
            .Select(m =>
            {
                var type = m["type"]!.GetValue<string>();
                var result = new JsonObject { ["type"] = type };
                if (CanonicalAttributes(m["attrs"] as JsonObject, ContentSchema.Marks[type].Attributes) is { } attrs)
                {
                    result["attrs"] = attrs;
                }

                return result;
            })
            .OrderBy(m => m["type"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(m => (JsonNode)m)
            .ToArray();
        return canonical.Length == 0 ? null : new JsonArray(canonical);
    }

    private JsonObject? CanonicalAttributes(JsonObject? attrs, IReadOnlyDictionary<string, AttributeRule> rules)
    {
        if (attrs is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (name, value) in attrs)
        {
            if (value is null || !rules.TryGetValue(name, out var rule) || IsDefault(value, rule))
            {
                continue;
            }

            result[name] = rule.Kind switch
            {
                // Whole numbers in one spelling ("-0" → 0); style ids in the catalog's spelling.
                AttributeKind.WholeNumber => JsonValue.Create(value.GetValue<long>()),
                AttributeKind.WholeNumberArray => new JsonArray(value.AsArray().Select(v => (JsonNode?)JsonValue.Create(v!.GetValue<long>())).ToArray()),
                AttributeKind.StyleId when _styles.TryGetValue(value.GetValue<string>(), out var styleId) => JsonValue.Create(styleId),
                _ => value.DeepClone(),
            };
        }

        return result.Count == 0 ? null : result;
    }

    private static bool IsDefault(JsonNode value, AttributeRule rule) => rule.Default switch
    {
        bool b => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False && value.GetValue<bool>() == b,
        long l => value.GetValueKind() == JsonValueKind.Number && value.AsValue().TryGetValue<long>(out var n) && n == l,
        string s => value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == s,
        _ => false,
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
