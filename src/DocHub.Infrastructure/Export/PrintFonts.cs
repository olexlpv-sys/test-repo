using System.Text;
using System.Text.RegularExpressions;

namespace DocHub.Infrastructure.Export;

/// <summary>
/// The fonts bundled with the application (Liberation, SIL OFL 1.1) and the aliases that map the catalog's font families
/// onto them, so a render never depends on the fonts installed on the machine. The print HTML loads them from
/// <see cref="BaseUrl"/>; the renderer answers those requests from <see cref="Directory"/> and aborts every other request.
/// </summary>
public static partial class PrintFonts
{
    /// <summary>A host that never resolves: requests to it are only ever answered by the renderer's interception.</summary>
    public const string BaseUrl = "https://fonts.dochub.invalid/";

    public const string DefaultFamily = "Liberation Sans";

    private static readonly string[] Faces = ["Regular", "Bold", "Italic", "BoldItalic"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cambria"] = "Serif",
        ["Times New Roman"] = "Serif",
        ["Georgia"] = "Serif",
        ["Courier New"] = "Mono",
    };

    /// <summary>The directory of the bundled font files (copied next to the assembly).</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Export", "Fonts");

    /// <summary>The bundled file a font URL names, or null for anything else (unknown names, paths, query strings).</summary>
    public static string? FileFor(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.StartsWith(BaseUrl, StringComparison.Ordinal))
        {
            return null;
        }

        var name = url[BaseUrl.Length..];
        return FileName().IsMatch(name) ? Path.Combine(Directory, name) : null;
    }

    /// <summary>
    /// <c>@font-face</c> rules: the bundled families under their own names plus every configured catalog font as an alias
    /// (serif fonts to Liberation Serif, Courier New to Liberation Mono, everything else to Liberation Sans).
    /// </summary>
    public static string FontFaces(IEnumerable<string> catalogFamilies)
    {
        ArgumentNullException.ThrowIfNull(catalogFamilies);
        var families = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Liberation Sans"] = "Sans",
            ["Liberation Serif"] = "Serif",
            ["Liberation Mono"] = "Mono",
        };
        foreach (var family in catalogFamilies.Where(f => SafeFamily().IsMatch(f)))
        {
            families.TryAdd(family, Aliases.GetValueOrDefault(family, "Sans"));
        }

        var css = new StringBuilder();
        foreach (var (family, target) in families.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (var face in Faces)
            {
                css.Append("@font-face { font-family: \"").Append(family).Append("\"; src: url(\"").Append(BaseUrl)
                    .Append("Liberation").Append(target).Append('-').Append(face).Append(".ttf\"); font-weight: ")
                    .Append(face.StartsWith("Bold", StringComparison.Ordinal) ? "700" : "400").Append("; font-style: ")
                    .Append(face.EndsWith("Italic", StringComparison.Ordinal) ? "italic" : "normal").Append("; }\n");
            }
        }

        return css.ToString();
    }

    [GeneratedRegex(@"^Liberation(Sans|Serif|Mono)-(Regular|Bold|Italic|BoldItalic)\.ttf$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"^[A-Za-z0-9 ]{1,64}$")]
    private static partial Regex SafeFamily();
}
