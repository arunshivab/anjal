using System.Collections.Concurrent;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// Who gets a changed organisation default (DES-11 D7, owner 10 Oct 2026): the colour, the clock,
/// Trash days and Junk days. The administrator answers "who should get it?" when changing one.
/// </summary>
public static class WhoGets
{
    /// <summary>Only people joining from today: everyone already here keeps what they have now.</summary>
    public const string Joining = "joining";

    /// <summary>Everyone who has not chosen their own (the usual way).</summary>
    public const string NotChosen = "followers";

    /// <summary>Everyone, own choices included. Never offered where it would loosen a setting.</summary>
    public const string Everyone = "everyone";

    /// <summary>The answer read from a form; anything else is "everyone who has not chosen their own".</summary>
    /// <param name="value">The posted value.</param>
    /// <returns>One of the three.</returns>
    public static string Read(string? value) => value is Joining or Everyone ? value : NotChosen;

    /// <summary>The answer in words, for the activity log.</summary>
    /// <param name="who">One of the three.</param>
    /// <returns>Words.</returns>
    public static string Words(string who) => who switch
    {
        Joining => "only people joining from today",
        Everyone => "everyone",
        _ => "everyone who has not chosen their own",
    };
}

/// <summary>Organisation defaults reaching people (DES-11 D7).</summary>
public sealed partial class MailboxService
{
    // People whose colour an administrator changed; their next page carries it (the colour rides
    // in the sign-in cookie, so it is re-issued once - Program's middleware reads this).
    private static readonly ConcurrentDictionary<Guid, string> ThemesToRefresh = new();

    // The same for a language an administrator gave (owner, 10 Oct 2026).
    private static readonly ConcurrentDictionary<Guid, string> LanguagesToRefresh = new();

    /// <summary>A language an administrator gave this person since their cookie was issued, taken once.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="language">The language to put in their cookie.</param>
    /// <returns>True when there is one.</returns>
    public static bool TakeLanguageRefresh(Guid personId, out string language)
    {
        if (LanguagesToRefresh.TryRemove(personId, out string? l))
        {
            language = l;
            return true;
        }
        language = string.Empty;
        return false;
    }

    /// <summary>A colour an administrator gave this person since their cookie was issued, taken once.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="theme">The theme to put in their cookie.</param>
    /// <returns>True when there is one.</returns>
    public static bool TakeThemeRefresh(Guid personId, out string theme)
    {
        if (ThemesToRefresh.TryRemove(personId, out string? t))
        {
            theme = t;
            return true;
        }
        theme = string.Empty;
        return false;
    }

    /// <summary>
    /// True when giving everyone the new value would loosen the setting: for Trash and Junk, keeping
    /// mail longer than now. A colour or a clock is never looser or stricter.
    /// </summary>
    /// <param name="setting">"theme", "clock", "trash" or "junk".</param>
    /// <param name="before">The value before.</param>
    /// <param name="after">The new value.</param>
    /// <returns>True when "everyone" may not be chosen.</returns>
    public static bool WouldLoosen(string setting, string before, string after)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting is "trash" or "junk"
            && int.TryParse(before, System.Globalization.CultureInfo.InvariantCulture, out int b)
            && int.TryParse(after, System.Globalization.CultureInfo.InvariantCulture, out int a)
            && a > b;
    }

    /// <summary>
    /// Give a changed organisation default to the people the administrator chose. Call it after the
    /// new default is saved; nothing happens when the value did not change.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="setting">"theme" (a colour such as "rose"), "language" (a code such as "ta"), "clock" ("24" or "12"), "trash" or "junk" (days).</param>
    /// <param name="before">The organisation's value before the change.</param>
    /// <param name="after">The new value.</param>
    /// <param name="who">One of <see cref="WhoGets"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many people's own setting changed, or -1 when "everyone" would loosen it (nothing changed).</returns>
    public async Task<int> ApplyDefaultChangeAsync(TenantRow tenant, string setting, string before, string after, string who, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(who);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return 0;
        }
        if (who == WhoGets.Everyone && WouldLoosen(setting, before, after))
        {
            return -1;
        }
        int changed = 0;
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            MailSettings s = await this.GetMailSettingsAsync(m.Id, ct).ConfigureAwait(false);
            bool settingsChanged = false;
            switch (setting)
            {
                case "theme":
                    // The person's own light or dark stays theirs; only the colour moves.
                    bool takes = who == WhoGets.Everyone || (who == WhoGets.NotChosen && !s.ThemeChosen);
                    if (takes)
                    {
                        string mode = MailboxRow.NormalizeTheme(m.Theme).EndsWith("-dark", StringComparison.Ordinal) ? "dark" : "light";
                        string theme = MailboxRow.NormalizeTheme(after + "-" + mode);
                        if (!string.Equals(MailboxRow.NormalizeTheme(m.Theme), theme, StringComparison.Ordinal))
                        {
                            m.Theme = theme;
                            m.PasswordPbkdf2 = string.Empty;
                            await this.store.UpsertMailboxAsync(m, ct).ConfigureAwait(false);
                            ThemesToRefresh[m.Id] = theme;
                            changed++;
                        }
                        if (s.ThemeChosen)
                        {
                            s.ThemeChosen = false;
                            settingsChanged = true;
                        }
                    }
                    break;
                case "language":
                    bool reaches = who == WhoGets.Everyone || (who == WhoGets.NotChosen && !s.LanguageChosen);
                    if (reaches)
                    {
                        MailboxPreferences prefs = MailboxPreferences.Of(m);
                        if (!string.Equals(prefs.Language, after, StringComparison.Ordinal))
                        {
                            prefs.Language = after;
                            await this.store.SetMailboxPreferencesAsync(m.Id, prefs.Normalized(), ct).ConfigureAwait(false);
                            LanguagesToRefresh[m.Id] = after;
                            changed++;
                        }
                        if (s.LanguageChosen)
                        {
                            s.LanguageChosen = false;
                            settingsChanged = true;
                        }
                    }
                    break;
                case "clock":
                    if (who == WhoGets.Joining && s.Clock.Length == 0)
                    {
                        s.Clock = before;
                        settingsChanged = true;
                        changed++;
                    }
                    else if (who == WhoGets.Everyone && s.Clock.Length > 0)
                    {
                        s.Clock = string.Empty;
                        settingsChanged = true;
                        changed++;
                    }
                    break;
                case "trash":
                    if (who == WhoGets.Joining && !s.TrashChosen)
                    {
                        s.TrashChosen = true;
                        s.TrashDays = int.Parse(before, System.Globalization.CultureInfo.InvariantCulture);
                        settingsChanged = true;
                        changed++;
                    }
                    else if (who == WhoGets.Everyone && s.TrashChosen)
                    {
                        s.TrashChosen = false;
                        settingsChanged = true;
                        changed++;
                    }
                    break;
                case "junk":
                    if (who == WhoGets.Joining && s.JunkKept == 0)
                    {
                        s.JunkKept = int.Parse(before, System.Globalization.CultureInfo.InvariantCulture);
                        settingsChanged = true;
                        changed++;
                    }
                    else if (who != WhoGets.Joining && s.JunkKept > 0)
                    {
                        // Junk has no choice of one's own: an earlier setting kept for someone gives
                        // way to "everyone"; "who has not chosen" leaves it.
                        if (who == WhoGets.Everyone)
                        {
                            s.JunkKept = 0;
                            settingsChanged = true;
                            changed++;
                        }
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(setting), setting, "Not an organisation default.");
            }
            if (settingsChanged)
            {
                await this.WriteDocumentAsync(m.Id, MailSettingsKind, s, ct).ConfigureAwait(false);
            }
        }
        return changed;
    }
}
