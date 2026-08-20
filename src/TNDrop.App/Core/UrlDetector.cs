namespace TNDrop.Core;

public static class UrlDetector
{
    public static bool IsUrl(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var trimmed = text.Trim();

        // Check for whitespace (should be single line)
        if (trimmed.Any(char.IsWhiteSpace))
            return false;

        // Try to parse as absolute URI
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        // Check if scheme is http or https
        return uri.Scheme is "http" or "https";
    }

    public static string GetDomain(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        return uri.Host;
    }
}
