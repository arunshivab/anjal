namespace Anjal.Webmail.Services;

/// <summary>
/// Where to send someone back to after a small form (folding the rail, say):
/// only a page on this site, never an address elsewhere, so a crafted link
/// cannot bounce a person to another website (an open redirect).
/// </summary>
public static class LocalPath
{
    /// <summary>The page to return to, or the inbox when the one given is not on this site.</summary>
    /// <param name="back">The path the form says it came from, such as /folder/INBOX?page=2.</param>
    /// <returns>A path on this site.</returns>
    public static string Safe(string? back)
    {
        const string Home = "/folder/INBOX";
        if (string.IsNullOrEmpty(back) || back[0] != '/' || back.StartsWith("//", StringComparison.Ordinal) || back.StartsWith("/\\", StringComparison.Ordinal))
        {
            return Home;
        }
        foreach (char c in back)
        {
            if (c == '\\' || char.IsControl(c))
            {
                return Home;
            }
        }
        return back;
    }
}
