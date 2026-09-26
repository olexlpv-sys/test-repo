using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocHub.Domain.Content;

namespace DocHub.DataGen;

/// <summary>A node content: canonical JSON with its derived columns and the styles it uses, exactly as the API stores it.</summary>
public sealed record ContentTemplate(string Json, string Html, string PlainText, byte[] Hash, IReadOnlyList<string> Styles);

/// <summary>
/// Node contents of about <see cref="TargetBytes"/> each, built from the Content Schema fixtures (headings, runs, lists, tables)
/// plus generated paragraphs, validated and canonicalized with the catalog like an API save (T09).
/// </summary>
public static class ContentTemplates
{
    public const int TargetBytes = 2048;

    private static readonly string[] Words =
    [
        "agreement", "party", "obligation", "service", "term", "notice", "scope", "liability", "payment", "delivery", "quality",
        "review", "approval", "record", "process", "requirement", "control", "audit", "risk", "change", "document", "section",
    ];

    public static IReadOnlyList<ContentTemplate> Build(int count, int seed, IReadOnlyCollection<string> styleIds, IEnumerable<string> fontFamilies)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var schema = new ContentDocument(styleIds, fontFamilies.ToHashSet(StringComparer.Ordinal));
        var fixtures = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ContentFixtures"), "*.json").Order(StringComparer.Ordinal)
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!["content"]!.AsArray())
            .ToList();
        if (fixtures.Count == 0)
        {
            throw new InvalidOperationException("No content fixtures found next to the generator.");
        }

        var random = new Random(seed);
        var templates = new List<ContentTemplate>(count);
        for (var i = 0; i < count; i++)
        {
            var content = new JsonArray();
            foreach (var block in fixtures[i % fixtures.Count])
            {
                content.Add(block!.DeepClone());
            }

            var doc = new JsonObject { ["type"] = "doc", ["content"] = content };
            while (Encoding.UTF8.GetByteCount(doc.ToJsonString()) < TargetBytes)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "paragraph",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Sentence(random) }),
                });
            }

            using var parsed = JsonDocument.Parse(doc.ToJsonString());
            var errors = schema.Validate(parsed.RootElement);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Generated content is not valid: {string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(' ', e.Value)}"))}");
            }

            var canonical = schema.Canonicalize(parsed.RootElement);
            var rendered = ContentHtmlRenderer.Render(canonical);
            using var canonicalDoc = JsonDocument.Parse(canonical);
            templates.Add(new ContentTemplate(canonical, rendered.Html, rendered.PlainText, rendered.ContentHash,
                [.. schema.UsedStyles(canonicalDoc.RootElement).Order(StringComparer.Ordinal)]));
        }

        return templates;
    }

    private static string Sentence(Random random)
    {
        var words = Enumerable.Range(0, random.Next(12, 24)).Select(_ => Words[random.Next(Words.Length)]).ToArray();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(' ', words) + ".";
    }
}
