namespace Anjal.Webmail.Services;

/// <summary>
/// The words for pages seen before signing in (rc.13): the language chosen
/// with the chips on the sign-in page, kept in a cookie; once signed in, the
/// person's own language.
/// </summary>
public static class AuthWords
{
    /// <summary>The cookie holding the language chosen before signing in.</summary>
    public const string Cookie = "anjal.lang";

    /// <summary>The words for a request.</summary>
    /// <param name="http">The request, or null.</param>
    /// <param name="words">The words built into the program.</param>
    /// <returns>The lexicon to show the page in.</returns>
    public static Lexicon For(Microsoft.AspNetCore.Http.HttpContext? http, Words words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (http?.User.Identity?.IsAuthenticated == true)
        {
            return words.For(WebmailAuthService.LanguageOf(http.User, words));
        }
        string? chosen = http?.Request.Cookies[Cookie];
        return words.For(words.IsEnabled(chosen) ? chosen : "en");
    }
}
