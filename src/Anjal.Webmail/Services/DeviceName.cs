namespace Anjal.Webmail.Services;

/// <summary>
/// A device as a person would name it (rc.13): "Windows, Chrome", and whether
/// it is a phone - read from the browser's User-Agent, for the list of
/// signed-in devices and the names of passkeys. A guess, never a decision:
/// nothing about security depends on it.
/// </summary>
public static class DeviceName
{
    /// <summary>"Windows, Chrome", "Android, Chrome", "iPhone, Safari" ...</summary>
    /// <param name="userAgent">The User-Agent header.</param>
    /// <returns>The name; "Unknown browser" when it cannot be told.</returns>
    public static string Of(string? userAgent)
    {
        string ua = userAgent ?? string.Empty;
        string system = ua.Contains("iPhone", StringComparison.Ordinal) ? "iPhone"
            : ua.Contains("iPad", StringComparison.Ordinal) ? "iPad"
            : ua.Contains("Android", StringComparison.Ordinal) ? "Android"
            : ua.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : ua.Contains("CrOS", StringComparison.Ordinal) ? "ChromeOS"
            : ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : ua.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : string.Empty;
        string browser = ua.Contains("Edg/", StringComparison.Ordinal) || ua.Contains("EdgA/", StringComparison.Ordinal) ? "Edge"
            : ua.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : ua.Contains("SamsungBrowser", StringComparison.Ordinal) ? "Samsung Internet"
            : ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS", StringComparison.Ordinal) ? "Firefox"
            : ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS", StringComparison.Ordinal) ? "Chrome"
            : ua.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : string.Empty;
        return (system, browser) switch
        {
            ("", "") => "Unknown browser",
            ("", _) => browser,
            (_, "") => system,
            _ => system + ", " + browser,
        };
    }

    /// <summary>True for a phone or tablet.</summary>
    /// <param name="userAgent">The User-Agent header.</param>
    /// <returns>Whether to draw a phone.</returns>
    public static bool IsPhone(string? userAgent)
    {
        string ua = userAgent ?? string.Empty;
        return ua.Contains("Mobile", StringComparison.Ordinal) || ua.Contains("Android", StringComparison.Ordinal)
            || ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal);
    }
}
