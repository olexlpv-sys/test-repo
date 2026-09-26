using UglyToad.PdfPig;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>The text of each PDF page (words joined by single spaces), for assertions on rendered exports.</summary>
internal static class PdfText
{
    public static List<string> Pages(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))).ToList();
    }

    /// <summary>The letters of each page without spaces — for rotated text (the DRAFT watermark), which word extraction splits.</summary>
    public static List<string> Letters(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p => string.Concat(p.Letters.Select(l => l.Value))).ToList();
    }
}
