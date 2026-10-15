using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A change Lajna proposes, made only when the person presses Yes (design review [8c]).</summary>
/// <param name="What">What changes, in words.</param>
/// <param name="From">The value now.</param>
/// <param name="To">The value after.</param>
/// <param name="Action">Where Yes posts.</param>
/// <param name="Fields">The form's fields.</param>
/// <param name="Change">Where "Change it" goes, to choose another value by hand.</param>
public sealed record LajnaConsent(string What, string From, string To, string Action, IReadOnlyList<KeyValuePair<string, string>> Fields, string Change);

/// <summary>A draft Lajna wrote (design review [8f]); Lajna never sends it.</summary>
/// <param name="To">The address.</param>
/// <param name="ToName">Who that is.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The text.</param>
/// <param name="Cover">Who covered last time, from the person's own mail, or null.</param>
/// <param name="Style">normal, formal, short or ta.</param>
public sealed record LajnaDraft(string To, string ToName, string Subject, string Body, LajnaCover? Cover, string Style)
{
    /// <summary>Compose, filled in with the draft.</summary>
    public string ComposeLink => "/compose?to=" + Uri.EscapeDataString(this.To) + "&subject=" + Uri.EscapeDataString(this.Subject) + "&body=" + Uri.EscapeDataString(this.Body);
}

/// <summary>One answer from Lajna.</summary>
public sealed class LajnaAnswer
{
    /// <summary>help, consent, mails, draft, story, unsure.</summary>
    public string Kind { get; init; } = "unsure";

    /// <summary>The answer's words, in the person's language.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The help articles it rests on (cited as chips).</summary>
    public IReadOnlyList<HelpArticle> Cites { get; init; } = Array.Empty<HelpArticle>();

    /// <summary>A change to agree to.</summary>
    public LajnaConsent? Consent { get; init; }

    /// <summary>Messages found.</summary>
    public IReadOnlyList<LajnaMail> Mails { get; init; } = Array.Empty<LajnaMail>();

    /// <summary>Another person the name could mean, offered in one line.</summary>
    public (string Address, string Name)? Other { get; init; }

    /// <summary>The address the found mail is from.</summary>
    public string? MailsFrom { get; init; }

    /// <summary>A draft.</summary>
    public LajnaDraft? Draft { get; init; }

    /// <summary>What happened to a message.</summary>
    public LajnaStory? Story { get; init; }

    /// <summary>The question, to ask again with a chip.</summary>
    public string Question { get; init; } = string.Empty;
}

/// <summary>
/// rc.18 (design review section 11 [8c]-[8g], owner 11-12 Oct 2026: "Lajna should run within
/// Anjal ... no external connection"): Lajna, Anjal's helper. It runs inside the webmail, reads only
/// the help guide and the asking person's own mail and settings (and, for an administrator, their
/// organisation's applications), sends nothing anywhere, keeps nothing asked, and never sends,
/// deletes or changes anything without the person pressing Yes. It works from the words of the
/// question - finding mail, writing a draft, explaining a delivery, proposing a setting, or answering
/// from the guide - and says when it is not sure.
/// </summary>
public sealed partial class Lajna
{
    private readonly MailboxService mail;
    private readonly HelpGuide guide;
    private readonly Words words;

    /// <summary>Make Lajna.</summary>
    /// <param name="mail">The mailbox service.</param>
    /// <param name="guide">The help guide.</param>
    /// <param name="words">The word lists.</param>
    public Lajna(MailboxService mail, HelpGuide guide, Words words)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(words);
        this.mail = mail;
        this.guide = guide;
        this.words = words;
    }

    /// <summary>Answer a question.</summary>
    /// <param name="user">Who asks.</param>
    /// <param name="question">The question.</param>
    /// <param name="page">The page they are on (for "back").</param>
    /// <param name="style">For a draft: normal, formal, short or ta.</param>
    /// <param name="L">The person's words.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public async Task<LajnaAnswer> AskAsync(ClaimsPrincipal user, string question, string page, string? style, Lexicon L, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(L);
        string q = Regex.Replace(question.Trim(), @"\s+", " ");
        if (q.Length == 0 || WebmailAuthService.PersonIdOf(user) is not Guid person)
        {
            return new LajnaAnswer { Kind = "unsure", Text = L["Ask me how to do something in Anjal, to find mail, or to write a draft."], Question = q };
        }
        string back = LocalPath.Safe(page);

        if (FindRegex().Match(q) is { Success: true } find)
        {
            return await this.FindAsync(person, q, find, L, ct).ConfigureAwait(false);
        }
        // rc.20 (owner, 14 Oct 2026): "Priya's last 5 mails", "find arunshiva@redifmail's mails".
        if (FindOwnerRegex().Match(q) is { Success: true } owned)
        {
            return await this.FindAsync(person, q, owned, L, ct).ConfigureAwait(false);
        }
        if (DraftRegex().Match(q) is { Success: true } draft)
        {
            return await this.DraftAsync(user, person, q, draft.Groups["who"].Value, draft.Groups["what"].Value, style, L, ct).ConfigureAwait(false);
        }
        if (StoryRegex().Match(q) is { Success: true } story)
        {
            return await this.StoryAsync(person, q, story.Groups["who"].Value, L, ct).ConfigureAwait(false);
        }
        if (this.Setting(user, q, back, L) is LajnaConsent consent)
        {
            return new LajnaAnswer
            {
                Kind = "consent",
                Text = L["Lajna will change this, if you agree."],
                Consent = consent,
                Cites = this.guide.Search(consent.What, 1).Select(x => x.Article).ToList(),
                Question = q,
            };
        }
        return await this.HelpAsync(person, q, L, ct).ConfigureAwait(false);
    }

    // ---- finding mail [8g] ----

    private async Task<LajnaAnswer> FindAsync(Guid person, string q, Match m, Lexicon L, CancellationToken ct)
    {
        int count = int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 20) : 5;
        bool files = m.Groups["files"].Success;
        IReadOnlyList<(string Address, string Name)> who = await this.mail.WhoIsAsync(person, m.Groups["who"].Value, ct).ConfigureAwait(false);
        if (who.Count == 0)
        {
            return new LajnaAnswer { Kind = "unsure", Text = L["I could not find anyone called {name} in your contacts or your organisation. Try their address."].Replace("{name}", m.Groups["who"].Value.Trim(), StringComparison.Ordinal), Question = q };
        }
        IReadOnlyList<LajnaMail> found = await this.mail.MailFromAsync(person, who[0].Address, count, files, ct).ConfigureAwait(false);
        string text = found.Count == 0
            ? L["No mail from {name} in your folders."].Replace("{name}", who[0].Name, StringComparison.Ordinal)
            : (found.Count == 1 ? L["The last message from {name}."] : L["The last {n} messages from {name}."].Replace("{n}", found.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                .Replace("{name}", who[0].Name, StringComparison.Ordinal);
        return new LajnaAnswer { Kind = "mails", Text = text, Mails = found, MailsFrom = who[0].Address, Other = who.Count > 1 ? who[1] : null, Question = q };
    }

    // ---- drafts [8f] ----

    private async Task<LajnaAnswer> DraftAsync(ClaimsPrincipal user, Guid person, string q, string whoText, string what, string? style, Lexicon L, CancellationToken ct)
    {
        IReadOnlyList<(string Address, string Name)> who = await this.mail.WhoIsAsync(person, whoText, ct).ConfigureAwait(false);
        // rc.20 (owner, 14 Oct 2026: "write a mail to HR asking for leave on Friday"): someone Lajna
        // cannot find still gets the draft, with the To line left for the person to fill in.
        bool unknown = who.Count == 0;
        if (unknown)
        {
            who = new[] { (string.Empty, whoText.Trim()) };
        }
        MailboxRow? me = (await this.mail.GetContextAsync(person, ct).ConfigureAwait(false))?.Mailbox;
        string myName = me is null || me.DisplayName.Length == 0 ? user.Identity?.Name ?? string.Empty : me.DisplayName;
        (_, string signature) = await this.mail.GetSignatureAsync(person, ct).ConfigureAwait(false);
        bool leave = LeaveRegex().IsMatch(what);
        LajnaCover? cover = leave ? await this.mail.WhoCoveredAsync(person, ct).ConfigureAwait(false) : null;
        string s = style is "formal" or "short" or "ta" ? style : (Regex.IsMatch(q, @"\p{IsTamil}") ? "ta" : "normal");
        LajnaDraft draft = LajnaLetters.Write(who[0].Address, who[0].Name, what, leave, cover, myName, signature, s);
        string text = unknown
            ? L["I do not know {name}'s address, so the To line is empty. Here is a draft: open it in Compose, add the address, and send it yourself."].Replace("{name}", whoText.Trim(), StringComparison.Ordinal)
            : cover is null
            ? L["Here is a draft. Read it, then open it in Compose to send it yourself."]
            : (cover.Copied ? L["Here is a draft. Your mail of {date} copied {name}; check that they will cover again."] : L["Here is a draft. Your mail of {date} says {name} covered last time."])
                .Replace("{date}", WebmailAuthService.ClockOf(user).Date(cover.At), StringComparison.Ordinal)
                .Replace("{name}", cover.Name, StringComparison.Ordinal);
        return new LajnaAnswer { Kind = "draft", Text = text, Draft = draft, Question = q, Other = who.Count > 1 ? who[1] : null };
    }

    // ---- a delivery explained [8d] ----

    private async Task<LajnaAnswer> StoryAsync(Guid person, string q, string whoText, Lexicon L, CancellationToken ct)
    {
        IReadOnlyList<(string Address, string Name)> who = await this.mail.WhoIsAsync(person, whoText, ct).ConfigureAwait(false);
        if (who.Count == 0)
        {
            return new LajnaAnswer { Kind = "unsure", Text = L["I could not find anyone called {name} in your contacts or your organisation. Try their address."].Replace("{name}", whoText.Trim(), StringComparison.Ordinal), Question = q };
        }
        LajnaStory? story = await this.mail.OwnStoryAsync(person, who[0].Address, ct).ConfigureAwait(false);
        if (story is null && await this.mail.IsOrgAdminAsync(person, ct).ConfigureAwait(false)
            && (await this.mail.GetContextAsync(person, ct).ConfigureAwait(false)) is { } context)
        {
            story = await this.mail.AppStoryAsync(context.Tenant, who[0].Address, ct).ConfigureAwait(false);
        }
        if (story is null)
        {
            return new LajnaAnswer { Kind = "unsure", Text = L["You sent nothing to {name} in the last 30 days."].Replace("{name}", who[0].Name, StringComparison.Ordinal), Question = q };
        }
        string text = story.Status switch
        {
            "Delivered" => L["It was delivered: {server} accepted it."],
            "Inside" => L["It went to someone in your organisation and was filed at once."],
            "Delayed" => L["It is still being tried: {server} has not taken it yet."],
            "Queued" => L["It is waiting to be sent."],
            "Bounced" => L["{server} refused it for good."],
            "Suppressed" => L["It was not sent: the address is on your organisation's do-not-send list."],
            _ => L["Anjal gave up on it."],
        };
        string server = story.Attempts.Count > 0 && story.Attempts[^1].Host.Length > 0 ? story.Attempts[^1].Host : L["the receiving server"];
        return new LajnaAnswer { Kind = "story", Text = text.Replace("{server}", server, StringComparison.Ordinal), Story = story, Question = q, Cites = this.guide.Search("delivery delayed bounced", 1).Select(x => x.Article).ToList() };
    }

    // ---- settings, with consent [8c] ----

    private LajnaConsent? Setting(ClaimsPrincipal user, string q, string back, Lexicon L)
    {
        string t = q.ToLowerInvariant();
        string theme = WebmailAuthService.ThemeOf(user);
        int dash = theme.LastIndexOf('-');
        string colour = dash > 0 ? theme[..dash] : "anjal";
        string mode = dash > 0 ? theme[(dash + 1)..] : "light";
        if (Regex.IsMatch(t, @"\b(dark)\b") && Regex.IsMatch(t, @"\b(mode|theme|colou?rs?|switch|turn|make|use|set)\b"))
        {
            return mode == "dark" ? null : new LajnaConsent(L["Light or dark"], L["Light"], L["Dark"], "/settings/theme", Fields(("theme", colour + "-dark")), "/settings/appearance");
        }
        if (Regex.IsMatch(t, @"\b(light)\b") && Regex.IsMatch(t, @"\b(mode|theme|colou?rs?|switch|turn|make|use|set)\b"))
        {
            return mode == "light" ? null : new LajnaConsent(L["Light or dark"], L["Dark"], L["Light"], "/settings/theme", Fields(("theme", colour + "-light")), "/settings/appearance");
        }
        // rc.20: size on screen - one step smaller or larger than now, 100, 90 or 80 percent.
        if (Regex.IsMatch(t, @"\b(smaller|bigger|larger|zoom|text size|font size|size on screen|too big|too large|too small)\b"))
        {
            int now = WebmailAuthService.ZoomOf(user);
            bool down = Regex.IsMatch(t, @"\b(smaller|too big|too large|zoom out|reduce|less)\b");
            int[] steps = MailboxPreferences.Zooms.OrderByDescending(z => z).ToArray();
            int i = Array.IndexOf(steps, now);
            int next = down ? steps[Math.Min(steps.Length - 1, i + 1)] : steps[Math.Max(0, i - 1)];
            return next == now ? null : new LajnaConsent(L["Size on screen"], Percent(now, L), Percent(next, L), "/settings/zoom", Fields(("zoom", next.ToString(CultureInfo.InvariantCulture)), ("back", back)), "/settings/appearance");
        }
        if (Regex.IsMatch(t, @"\b(sound|chime)\b"))
        {
            bool off = Regex.IsMatch(t, @"\b(off|stop|mute|silence|no|disable|quiet)\b");
            return new LajnaConsent(L["New mail sound"], off ? L["On"] : L["Off"], off ? L["Off"] : L["On"], "/settings/sound", Fields(("on", off ? "0" : "1"), ("back", back)), "/settings/mail");
        }
        if (Regex.Match(t, @"\b(?<n>\d{2,3})\s*(?:messages|mails|emails|rows)?\s*(?:per|a|on each|in a)\s*page\b") is { Success: true } size
            && int.TryParse(size.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pageSize)
            && pageSize is 20 or 25 or 50 or 100 or 200)
        {
            return new LajnaConsent(L["Messages per page"], "–", pageSize.ToString(CultureInfo.InvariantCulture), "/settings/pagesize", Fields(("size", pageSize.ToString(CultureInfo.InvariantCulture)), ("back", back)), "/settings/mail");
        }
        if (Regex.IsMatch(t, @"\b(open|start|land)\b.*\b(on|in|with)\b.*\binbox\b"))
        {
            return new LajnaConsent(L["Open Anjal on"], L["Today"], L["Inbox"], "/settings/openon", Fields(("where", "inbox")), "/settings/mail");
        }
        if (Regex.IsMatch(t, @"\b(open|start|land)\b.*\b(on|in|with)\b.*\btoday\b"))
        {
            return new LajnaConsent(L["Open Anjal on"], L["Inbox"], L["Today"], "/settings/openon", Fields(("where", "today")), "/settings/mail");
        }
        if (Regex.Match(t, @"\blanguage\b.*\b(?:to|into|in)\s+(?<lang>\p{L}+)|\b(?:switch|change)\b.*\b(?:to|into)\s+(?<lang>tamil|hindi|malayalam|marathi|gujarati|english|தமிழ்|हिन्दी|हिंदी|മലയാളം|मराठी|ગુજરાતી)\b") is { Success: true } lang
            && LanguageCode(lang.Groups["lang"].Value) is string code && code != L.Language)
        {
            if (!this.words.IsEnabled(code))
            {
                return null;
            }
            return new LajnaConsent(L["Language"], Words.NativeName(L.Language), Words.NativeName(code), "/settings/language", Fields(("language", code)), "/settings/language");
        }
        if (Regex.IsMatch(t, @"\b(fold|hide|collapse)\b.*\b(rail|folders|sidebar|menu)\b|\bicons only\b"))
        {
            return new LajnaConsent(L["The rail"], L["Show folder names"], L["Show icons only"], "/settings/rail", Fields(("folded", "1"), ("back", back)), "/settings/appearance");
        }
        return null;

        static string Percent(int z, Lexicon L) => z == MailboxPreferences.DefaultZoom ? L["Normal"] : L["{n}%"].Replace("{n}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        static IReadOnlyList<KeyValuePair<string, string>> Fields(params (string Name, string Value)[] f) => f.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();
    }

    private static string? LanguageCode(string name) => name.ToLowerInvariant() switch
    {
        "tamil" or "தமிழ்" => "ta",
        "hindi" or "हिन्दी" or "हिंदी" => "hi",
        "malayalam" or "മലയാളം" => "ml",
        "marathi" or "मराठी" => "mr",
        "gujarati" or "ગુજરાતી" => "gu",
        "english" => "en",
        _ => null,
    };

    // ---- the guide, or not sure ----

    private async Task<LajnaAnswer> HelpAsync(Guid person, string q, Lexicon L, CancellationToken ct)
    {
        IReadOnlyList<(HelpArticle Article, double Score)> found = this.guide.Search(q, 3);
        if (found.Count > 0 && found[0].Score >= 1.0)
        {
            return new LajnaAnswer { Kind = "help", Text = found[0].Article.Summary, Cites = found.Where(f => f.Score >= found[0].Score * 0.6).Select(f => f.Article).ToList(), Question = q };
        }
        IReadOnlyList<string> admins = await this.mail.AdministratorNamesAsync(person, ct).ConfigureAwait(false);
        string text = admins.Count == 0
            ? L["I am not sure. These articles may help; your administrator can help with anything else."]
            : L["I am not sure. These articles may help, or ask your administrator: {names}."].Replace("{names}", string.Join(", ", admins.Take(2)), StringComparison.Ordinal);
        return new LajnaAnswer { Kind = "unsure", Text = text, Cites = found.Select(f => f.Article).ToList(), Question = q };
    }

    [GeneratedRegex(@"^(?:please\s+)?(?:get|show|find|open|list|give)?\s*(?:me\s+)?(?:the\s+|my\s+)?(?:last|latest|recent|newest)?\s*(?<n>\d{1,2})?\s*(?:mails?|messages?|e-?mails?|letters?)\s+(?<files>with\s+(?:attachments?|files?)\s+)?(?:from|by|sent by|of)\s+(?<who>[^?]+?)\s*\??$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FindRegex();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:get|show|find|open|list|give)\s+)?(?:me\s+)?(?<who>[^?]+?)['’]s?\s+(?:last|latest|recent|newest)?\s*(?<n>\d{1,2})?\s*(?:mails?|messages?|e-?mails?|letters?)(?<files>\s+with\s+(?:attachments?|files?))?\s*\??$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FindOwnerRegex();

    [GeneratedRegex(@"^(?:please\s+)?(?:write|draft|compose)\s+(?:a\s+|an\s+)?(?:(?:short|formal|quick)\s+)?(?:mail|e-?mail|message|letter|note)?\s*to\s+(?<who>.+?)\s+(?<what>(?:asking|about|for|regarding|requesting|saying|to ask|to say|to tell|telling|to request|to inform)\b.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DraftRegex();

    [GeneratedRegex(@"(?:why|where|what happened|did|has|was|is)\b.*\b(?:mail|message|e-?mail|letter)\b.*?\bto\s+(?<who>[\p{L}\p{N}.@+_-]+(?:\s+\p{Lu}[\p{L}.-]+)?)\b.*?(?:arrive|deliver|reach|bounce|go|got|get|sent|fail|receive)|(?:why|where|what happened)\b.*\bto\s+(?<who>[\w.+-]+@[\w.-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoryRegex();

    [GeneratedRegex(@"\b(leave|vacation|holiday|time off|day off|days off)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaveRegex();

}
