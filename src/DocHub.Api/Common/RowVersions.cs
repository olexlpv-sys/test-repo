using DocHub.Domain.Errors;

namespace DocHub.Api.Common;

/// <summary>The <c>rowVersion</c> query parameter of DELETE requests (base64, as in the DTOs).</summary>
internal static class RowVersions
{
    public static byte[] Parse(string? rowVersion)
    {
        try
        {
            return string.IsNullOrEmpty(rowVersion) ? throw new FormatException() : Convert.FromBase64String(rowVersion);
        }
        catch (FormatException)
        {
            throw DomainException.Validation("The rowVersion query parameter (base64) is required.");
        }
    }
}
