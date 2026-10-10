namespace Anjal.Store;

/// <summary>
/// A person's own settings for how the webmail looks and tells time (rc.11):
/// time zone, language, density, layout, the folded rail, date format, the
/// first day of the week, messages per page and the new-mail sound. They are saved on their own, never by a general
/// mailbox save, so no other change can reset them.
/// </summary>
public sealed class MailboxPreferences
{
    /// <summary>The time zone a new mailbox starts with.</summary>
    public const string DefaultTimeZone = "Asia/Kolkata";

    /// <summary>The language a new mailbox starts with.</summary>
    public const string DefaultLanguage = "en";

    /// <summary>The density a new mailbox starts with.</summary>
    public const string DefaultDensity = "comfortable";

    /// <summary>The layout a new mailbox starts with: three panes.</summary>
    public const string DefaultLayout = "three";

    /// <summary>The date format a new mailbox starts with: as its language writes dates.</summary>
    public const string DefaultDateFormat = "language";

    /// <summary>The first day of the week a new mailbox starts with.</summary>
    public const string DefaultWeekStart = "monday";

    private static readonly string[] LanguageList = { "en", "ta", "ml", "hi", "mr", "gu" };
    private static readonly string[] DensityList = { "comfortable", "compact" };
    private static readonly string[] LayoutList = { "three", "focus", "list" };
    private static readonly string[] DateFormatList = { "language", "d-mmm-yyyy", "dd/mm/yyyy", "yyyy-mm-dd" };
    private static readonly string[] WeekStartList = { "monday", "sunday" };

    private static readonly int[] PageSizeList = { 10, 20, 30, 50, 100 };

    /// <summary>Messages per page for a new mailbox: 50, as before rc.11.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The page sizes a person may choose (owner, item 14).</summary>
    public static IReadOnlyList<int> PageSizes => PageSizeList;

    /// <summary>The six languages, English first: Tamil, Malayalam, Hindi, Marathi, Gujarati.</summary>
    public static IReadOnlyList<string> Languages => LanguageList;

    /// <summary>Comfortable or compact.</summary>
    public static IReadOnlyList<string> Densities => DensityList;

    /// <summary>Three panes, focus (the letter and its envelope), or list only.</summary>
    public static IReadOnlyList<string> Layouts => LayoutList;

    /// <summary>As the language writes dates, or one fixed form.</summary>
    public static IReadOnlyList<string> DateFormats => DateFormatList;

    /// <summary>Monday or Sunday.</summary>
    public static IReadOnlyList<string> WeekStarts => WeekStartList;

    /// <summary>IANA time zone, such as Asia/Kolkata. Every date and time is shown in it.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>One of <see cref="Languages"/>.</summary>
    public string Language { get; set; } = DefaultLanguage;

    /// <summary>One of <see cref="Densities"/>.</summary>
    public string Density { get; set; } = DefaultDensity;

    /// <summary>One of <see cref="Layouts"/>.</summary>
    public string Layout { get; set; } = DefaultLayout;

    /// <summary>True when the rail shows icons only.</summary>
    public bool RailFolded { get; set; }

    /// <summary>
    /// True once the person has folded or opened the rail themselves; until
    /// then the rail shows icons only on screens up to laptop width.
    /// </summary>
    public bool RailChosen { get; set; }

    /// <summary>One of <see cref="DateFormats"/>.</summary>
    public string DateFormat { get; set; } = DefaultDateFormat;

    /// <summary>One of <see cref="WeekStarts"/>.</summary>
    public string WeekStart { get; set; } = DefaultWeekStart;

    /// <summary>One of <see cref="PageSizes"/>.</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>True to play a sound when new mail arrives; on by default (D-99).</summary>
    public bool NewMailSound { get; set; } = true;

    /// <summary>True once the welcome screen has been answered (D-105): it is never shown again.</summary>
    public bool WelcomeDone { get; set; }

    /// <summary>The preferences stored on a mailbox.</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <returns>A copy the caller may change.</returns>
    public static MailboxPreferences Of(MailboxRow mailbox)
    {
        System.ArgumentNullException.ThrowIfNull(mailbox);
        return new MailboxPreferences
        {
            TimeZone = mailbox.TimeZone,
            Language = mailbox.Language,
            Density = mailbox.Density,
            Layout = mailbox.Layout,
            RailFolded = mailbox.RailFolded,
            RailChosen = mailbox.RailChosen,
            DateFormat = mailbox.DateFormat,
            WeekStart = mailbox.WeekStart,
            PageSize = mailbox.PageSize,
            NewMailSound = mailbox.NewMailSound,
            WelcomeDone = mailbox.WelcomeDone,
        };
    }

    /// <summary>True when <paramref name="timeZone"/> names a time zone this server knows.</summary>
    /// <param name="timeZone">An IANA time zone id.</param>
    /// <returns>Whether the time zone can be used.</returns>
    public static bool IsKnownTimeZone(string? timeZone) =>
        !string.IsNullOrWhiteSpace(timeZone) && System.TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out _);

    /// <summary>
    /// A copy in which every value is one that exists: known values kept
    /// (lower-cased, trimmed), anything else replaced by its default.
    /// </summary>
    /// <returns>The checked preferences.</returns>
    public MailboxPreferences Normalized() => new()
    {
        TimeZone = IsKnownTimeZone(this.TimeZone) ? this.TimeZone.Trim() : DefaultTimeZone,
        Language = Pick(this.Language, LanguageList, DefaultLanguage),
        Density = Pick(this.Density, DensityList, DefaultDensity),
        Layout = Pick(this.Layout, LayoutList, DefaultLayout),
        RailFolded = this.RailFolded,
        RailChosen = this.RailChosen,
        DateFormat = Pick(this.DateFormat, DateFormatList, DefaultDateFormat),
        WeekStart = Pick(this.WeekStart, WeekStartList, DefaultWeekStart),
        PageSize = System.Array.IndexOf(PageSizeList, this.PageSize) >= 0 ? this.PageSize : DefaultPageSize,
        NewMailSound = this.NewMailSound,
        WelcomeDone = this.WelcomeDone,
    };

    private static string Pick(string? value, string[] allowed, string fallback)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return System.Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }
}
