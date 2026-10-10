using AngleSharp.Html.Parser;

namespace Librus.Portal;

/// <summary>Extracts the Laravel <c>_token</c> hidden input from the portal login page (verified 2026-10).</summary>
public static class CsrfTokenExtractor
{
    public const string FieldName = "_token";

    public static string Extract(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var input = document.QuerySelector($"form#login input[name='{FieldName}']")
            ?? document.QuerySelector($"input[name='{FieldName}']");
        var value = input?.GetAttribute("value");
        if (string.IsNullOrEmpty(value))
        {
            // Fallback: the page also carries <meta name="csrf-token">.
            value = document.QuerySelector("meta[name='csrf-token']")?.GetAttribute("content");
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new LibrusException(LibrusErrorCodes.PortalLoginPageInvalid,
                "Portal login page no longer contains the expected CSRF token.");
        }

        return value;
    }
}
