using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocHub.Infrastructure.Audit;

/// <summary>
/// The normalization of module definitions for the integrity check (T21 §4, rule 6). Must stay identical to the one in
/// <c>database/tools/GenerateAuditTriggers.cs</c>, which produces <see cref="AuditModuleHashes"/> (a test compares them):
/// line endings → LF, trailing whitespace removed from every line and the end, the leading CREATE / ALTER /
/// CREATE OR ALTER keyword (after leading comments) → CREATE; then SHA-256 over UTF-16LE, upper-case hex.
/// </summary>
public static partial class ModuleDefinition
{
    public static string Hash(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var lines = definition.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd());
        var text = string.Join('\n', lines).TrimEnd();
        var start = LeadingTriviaLength(text);
        var keyword = LeadingKeyword().Match(text[start..]);
        if (keyword.Success)
        {
            text = string.Concat(text.AsSpan(0, start), "CREATE", text.AsSpan(start + keyword.Length));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text)));
    }

    private static int LeadingTriviaLength(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text.AsSpan(i).StartsWith("--"))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (text.AsSpan(i).StartsWith("/*"))
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    [GeneratedRegex(@"^(CREATE\s+OR\s+ALTER|ALTER|CREATE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingKeyword();
}
