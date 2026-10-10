using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

public class Rc11WordsTests
{
    private static Words WithTamil() => new(new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["ta"] = new Dictionary<string, string> { ["Inbox"] = "\u0B87\u0BA9\u0BCD\u0BAA\u0BBE\u0B95\u0BCD\u0BB8\u0BCD", ["Sent"] = string.Empty },
    });

    [Fact]
    public void AMissingOrEmptyWord_ShowsInEnglish_NeverABlank()
    {
        Lexicon ta = WithTamil().For("ta");
        Assert.Equal("\u0B87\u0BA9\u0BCD\u0BAA\u0BBE\u0B95\u0BCD\u0BB8\u0BCD", ta["Inbox"]);
        Assert.Equal("Sent", ta["Sent"]);
        Assert.Equal("Reply all", ta["Reply all"]);
    }

    [Fact]
    public void ALanguageNotSwitchedOn_ShowsEnglish()
    {
        Words words = WithTamil();
        Assert.Equal("en", words.For("ml").Language);
        Assert.Equal("Inbox", words.For("ml")["Inbox"]);
        Assert.Equal("en", words.For(null).Language);
    }

    [Fact]
    public void TheCookiesLanguage_IsUsedOnlyWhenSwitchedOn()
    {
        var identity = new System.Security.Claims.ClaimsIdentity("AnjalWebmail");
        identity.AddClaim(new System.Security.Claims.Claim(WebmailAuthService.LanguageClaim, "ta"));
        var user = new System.Security.Claims.ClaimsPrincipal(identity);
        Assert.Equal("ta", WebmailAuthService.LanguageOf(user, WithTamil()));
        Assert.Equal("en", WebmailAuthService.LanguageOf(user, new Words(new Dictionary<string, IReadOnlyDictionary<string, string>>())));
        Assert.Equal("en", WebmailAuthService.LanguageOf(null, WithTamil()));
    }

    [Fact]
    public void FolderNames_StandardOnesInTheLanguage_INBOXAsInbox_YourOwnExactlyAsNamed()
    {
        Lexicon ta = WithTamil().For("ta");
        Assert.Equal("\u0B87\u0BA9\u0BCD\u0BAA\u0BBE\u0B95\u0BCD\u0BB8\u0BCD", ta.Folder("INBOX"));
        Lexicon en = Words.Load().For("en");
        Assert.Equal("Inbox", en.Folder("INBOX"));
        Assert.Equal("Trash", en.Folder("Trash"));
        Assert.Equal("Audit 2026", en.Folder("Audit 2026"));
        Assert.Equal("inbox  archive", en.Folder("inbox  archive"));
    }

    [Fact]
    public void LanguageNames_AreWrittenInTheirOwnScripts()
    {
        Assert.Equal("English", Words.NativeName("en"));
        Assert.Equal("\u0BA4\u0BAE\u0BBF\u0BB4\u0BCD", Words.NativeName("ta"));
        Assert.Equal("\u0A97\u0AC1\u0A9C\u0AB0\u0ABE\u0AA4\u0AC0", Words.NativeName("gu"));
    }

    [Fact]
    public void TheBuiltInWords_HoldTheDesignsWords_AndOnlyEnglishIsSwitchedOnToday()
    {
        Words words = Words.Load();
        Assert.Equal("en", words.Enabled[0]);
        Lexicon en = words.For("en");
        Assert.Equal("Compose", en["Compose"]);
        Assert.Equal("Reply all", en["Reply all"]);
        Assert.Equal("Verified sender", en["Verified sender"]);
        Assert.Single(words.Enabled);
    }
}
