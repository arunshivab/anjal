using System.Globalization;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// One clock for every date and time the webmail shows (rc.11, DEF-088): each
/// person's own time zone and date format, labelled with the zone. "Now" is
/// always passed in, never read inside, so the result never depends on when it
/// runs (the lesson of DEF-090).
/// </summary>
public sealed class ZonedClock
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    // The person's language for the names of days and months (rc.15, item 41); English when none.
    private Lexicon? names;

    // "24" or "12" (owner, 8 Oct 2026); the 24-hour clock unless chosen otherwise.
    private string hours = Clocks.TwentyFour;

    private ZonedClock(string zoneId, TimeZoneInfo zone, string dateFormat)
    {
        this.ZoneId = zoneId;
        this.Zone = zone;
        this.DateFormat = dateFormat;
    }

    /// <summary>
    /// This clock, writing the names of days and months - Mon, Oct, Yesterday - in a language
    /// (rc.15, item 41). Numbers, the order of the date and the zone are unchanged.
    /// </summary>
    /// <param name="words">The person's words, or null for English.</param>
    /// <returns>The clock.</returns>
    public ZonedClock In(Lexicon? words) => new(this.ZoneId, this.Zone, this.DateFormat) { names = words, hours = this.hours };

    /// <summary>This clock, showing times on the 24-hour (14:30) or the 12-hour (2:30 pm) clock (owner, 8 Oct 2026).</summary>
    /// <param name="clock">"24" or "12"; anything else is the 24-hour clock.</param>
    /// <returns>The clock.</returns>
    public ZonedClock WithHours(string? clock) => new(this.ZoneId, this.Zone, this.DateFormat) { names = this.names, hours = clock == Clocks.Twelve ? Clocks.Twelve : Clocks.TwentyFour };

    /// <summary>"24" or "12".</summary>
    public string Hours => this.hours;


    /// <summary>The clock a new mailbox starts with: India time, dates as the language writes them.</summary>
    public static ZonedClock Default { get; } = For(MailboxPreferences.DefaultTimeZone, MailboxPreferences.DefaultDateFormat);

    /// <summary>The IANA time zone id, such as Asia/Kolkata.</summary>
    public string ZoneId { get; }

    /// <summary>The time zone itself.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>One of <see cref="MailboxPreferences.DateFormats"/>.</summary>
    public string DateFormat { get; }

    /// <summary>
    /// The clock for a time zone and date format. Anything unknown falls back to
    /// the defaults, so a bad setting can never stop a page from showing.
    /// </summary>
    /// <param name="timeZone">An IANA time zone id, or null.</param>
    /// <param name="dateFormat">One of <see cref="MailboxPreferences.DateFormats"/>, or null.</param>
    /// <returns>The clock.</returns>
    public static ZonedClock For(string? timeZone, string? dateFormat)
    {
        MailboxPreferences p = new MailboxPreferences { TimeZone = timeZone ?? string.Empty, DateFormat = dateFormat ?? string.Empty }.Normalized();
        return new ZonedClock(p.TimeZone, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZone), p.DateFormat);
    }

    /// <summary>
    /// Every time zone this server knows, as IANA ids, sorted by their offset now
    /// and labelled like "(UTC+05:30) Asia - Kolkata" (rc.11, item 36).
    /// </summary>
    /// <param name="now">The present moment (offsets change with summer time).</param>
    /// <returns>The zones to choose from.</returns>
    public static IReadOnlyList<(string Id, string Label)> Choices(DateTimeOffset now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var zones = new List<(TimeSpan Offset, string Id, string Label)>();
        IEnumerable<TimeZoneInfo> system = TimeZoneInfo.GetSystemTimeZones();
        // The default zone is always offered, whatever the server lists (DEF-091).
        if (TimeZoneInfo.TryFindSystemTimeZoneById(MailboxPreferences.DefaultTimeZone, out TimeZoneInfo? india))
        {
            system = system.Prepend(india);
        }
        foreach (TimeZoneInfo zone in system)
        {
            string id = zone.HasIanaId ? zone.Id : (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? iana) ? iana : string.Empty);
            id = Modern(id);
            if (zone.Id == MailboxPreferences.DefaultTimeZone || (zone.HasIanaId && zone.Id.Length > 0 && Modern(zone.Id) == MailboxPreferences.DefaultTimeZone))
            {
                id = MailboxPreferences.DefaultTimeZone;
            }
            if (id.Length == 0 || !id.Contains('/', StringComparison.Ordinal) || id.StartsWith("Etc/", StringComparison.Ordinal) || !seen.Add(id))
            {
                continue;
            }
            TimeSpan offset = zone.GetUtcOffset(now);
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            TimeSpan abs = offset.Duration();
            string label = "(UTC" + sign + abs.Hours.ToString("00", En) + ":" + abs.Minutes.ToString("00", En) + ") " + id.Replace("/", " - ", StringComparison.Ordinal).Replace('_', ' ');
            zones.Add((offset, id, label));
        }
        if (seen.Add("UTC"))
        {
            zones.Add((TimeSpan.Zero, "UTC", "(UTC+00:00) UTC"));
        }
        return zones.OrderBy(z => z.Offset).ThenBy(z => z.Id, StringComparer.Ordinal).Select(z => (z.Id, z.Label)).ToList();
    }

    /// <summary>
    /// The current IANA name for a zone that was renamed (DEF-091): Windows
    /// converts its zones to older names, such as Asia/Calcutta for India, so
    /// the list would miss Asia/Kolkata on a Windows server.
    /// </summary>
    /// <param name="id">An IANA id, old or current.</param>
    /// <returns>The current name; the same id when it was never renamed.</returns>
    public static string Modern(string id) => id switch
    {
        "Asia/Calcutta" => "Asia/Kolkata",
        "Asia/Katmandu" => "Asia/Kathmandu",
        "Asia/Saigon" => "Asia/Ho_Chi_Minh",
        "Asia/Rangoon" => "Asia/Yangon",
        "Asia/Dacca" => "Asia/Dhaka",
        "Asia/Thimbu" => "Asia/Thimphu",
        "Asia/Ujung_Pandang" => "Asia/Makassar",
        "Asia/Ulan_Bator" => "Asia/Ulaanbaatar",
        "Asia/Macao" => "Asia/Macau",
        "Europe/Kiev" => "Europe/Kyiv",
        "Atlantic/Faeroe" => "Atlantic/Faroe",
        "America/Godthab" => "America/Nuuk",
        "America/Buenos_Aires" => "America/Argentina/Buenos_Aires",
        "America/Indianapolis" => "America/Indiana/Indianapolis",
        "Pacific/Truk" => "Pacific/Chuuk",
        "Pacific/Ponape" => "Pacific/Pohnpei",
        "Pacific/Enderbury" => "Pacific/Kanton",
        _ => id,
    };

    /// <summary>The moment in this clock's time zone.</summary>
    /// <param name="at">Any moment.</param>
    /// <returns>The same moment, with this zone's offset.</returns>
    public DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, this.Zone);

    /// <summary>
    /// When a message arrived, as short as it can be while still clear, decided
    /// in this time zone: the time today, "Yesterday", the weekday this week,
    /// the day and month this year, the full date otherwise.
    /// </summary>
    /// <param name="at">The moment to show.</param>
    /// <param name="now">The present moment.</param>
    /// <returns>For example 09:42, Yesterday, Tue, 29 Sep or 29 Sep 2025.</returns>
    public string List(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime local = this.Local(at).DateTime;
        DateTime today = this.Local(now).Date;
        if (local.Date == today)
        {
            return this.Time(at);
        }
        if (local.Date == today.AddDays(-1))
        {
            return this.Name("Yesterday");
        }
        if (local.Date < today && local.Date > today.AddDays(-7))
        {
            return this.Name(local.ToString("ddd", En));
        }
        if (local.Year == today.Year && local.Date < today)
        {
            return this.DateFormat switch
            {
                "dd/mm/yyyy" => local.ToString("dd/MM", En),
                "yyyy-mm-dd" => local.ToString("MM-dd", En),
                _ => local.ToString("%d", En) + " " + this.Name(local.ToString("MMM", En)),
            };
        }
        return this.Date(at);
    }

    /// <summary>
    /// When a message arrived, for a list already grouped by day (rc.15, the
    /// boards Laptop and PhoneList): the day group says "Yesterday", so the
    /// row gives the time, as it does for today; otherwise as <see cref="List"/>.
    /// </summary>
    /// <param name="at">The moment to show.</param>
    /// <param name="now">The present moment.</param>
    /// <returns>For example 09:42, 18:20 (yesterday), Tue, or 29 Sep.</returns>
    public string ListInDayGroup(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime local = this.Local(at).DateTime;
        return local.Date == this.Local(now).Date.AddDays(-1) ? this.Time(at) : this.List(at, now);
    }

    /// <summary>
    /// The day group a message belongs to in a list (rc.11, item 50), decided
    /// in this zone: Today, Yesterday, Earlier this week (by the person's own
    /// week start), Earlier this month, then the month and year.
    /// </summary>
    /// <param name="at">When the message arrived.</param>
    /// <param name="now">The present moment.</param>
    /// <param name="weekStart">monday or sunday.</param>
    /// <returns>The group's English label, looked up in the word list for display.</returns>
    public string DayGroup(DateTimeOffset at, DateTimeOffset now, string? weekStart)
    {
        DateTime day = this.Local(at).Date;
        DateTime today = this.Local(now).Date;
        if (day >= today)
        {
            return "Today";
        }
        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }
        DayOfWeek first = string.Equals(weekStart, "sunday", StringComparison.Ordinal) ? DayOfWeek.Sunday : DayOfWeek.Monday;
        DateTime weekBegan = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));
        if (day >= weekBegan)
        {
            return "Earlier this week";
        }
        if (day.Year == today.Year && day.Month == today.Month)
        {
            return "Earlier this month";
        }
        return day.ToString("MMMM yyyy", En);
    }

    /// <summary>The date alone, in the person's date format.</summary>
    /// <param name="at">The moment.</param>
    /// <returns>For example 2 Oct 2026, 02/10/2026 or 2026-10-02.</returns>
    public string Date(DateTimeOffset at)
    {
        DateTimeOffset local = this.Local(at);
        return this.DateFormat switch
        {
            "dd/mm/yyyy" => local.ToString("dd/MM/yyyy", En),
            "yyyy-mm-dd" => local.ToString("yyyy-MM-dd", En),
            _ => local.ToString("%d", En) + " " + this.Name(local.ToString("MMM", En)) + " " + local.ToString("yyyy", En),
        };
    }

    /// <summary>The time alone, on the person's clock: 14:30, or 2:30 pm.</summary>
    /// <param name="at">The moment.</param>
    /// <returns>The time.</returns>
    public string Time(DateTimeOffset at)
    {
        DateTimeOffset local = this.Local(at);
        return this.hours == Clocks.Twelve
            ? local.ToString("h:mm", En) + " " + this.Name(local.Hour < 12 ? "am" : "pm")
            : local.ToString("HH:mm", En);
    }

    /// <summary>
    /// The day a message came, written under its time in a list (owner, 8 Oct 2026): Today,
    /// Yesterday, the weekday within the last week, the day and month this year, the full date
    /// before that - decided in this time zone.
    /// </summary>
    /// <param name="at">The moment.</param>
    /// <param name="now">The present moment.</param>
    /// <returns>For example Today, Yesterday, Tue, 29 Sep or 29 Sep 2025.</returns>
    public string ListDay(DateTimeOffset at, DateTimeOffset now)
    {
        DateTime local = this.Local(at).DateTime;
        DateTime today = this.Local(now).Date;
        if (local.Date >= today)
        {
            return this.Name("Today");
        }
        return local.Date == today.AddDays(-1) ? this.Name("Yesterday") : this.List(at, now);
    }

    // A day's or month's name in the person's language.
    private string Name(string english) => this.names is null ? english : this.names[english];

    /// <summary>The weekday, date, time and zone: what the envelope and the status bar show.</summary>
    /// <param name="at">The moment.</param>
    /// <returns>For example Thu 2 Oct 2026, 09:42 IST.</returns>
    public string Full(DateTimeOffset at)
    {
        DateTimeOffset local = this.Local(at);
        return this.Name(local.ToString("ddd", En)) + " " + this.Date(at) + ", " + this.Time(at) + " " + this.ZoneName(at);
    }

    /// <summary>The date written out, with the time and zone: for the line above a quoted reply.</summary>
    /// <param name="at">The moment.</param>
    /// <returns>For example 2 October 2026 at 09:42 IST.</returns>
    public string Written(DateTimeOffset at)
    {
        DateTimeOffset local = this.Local(at);
        return local.ToString("d MMMM yyyy", En) + " at " + this.Time(at) + " " + this.ZoneName(at);
    }

    /// <summary>
    /// The zone's short name at that moment: IST, GMT or BST, EST or EDT and a
    /// few others well known; otherwise the offset, such as UTC+04:00.
    /// </summary>
    /// <param name="at">The moment (summer time depends on it).</param>
    /// <returns>The label.</returns>
    public string ZoneName(DateTimeOffset at)
    {
        bool summer = this.Zone.IsDaylightSavingTime(at);
        switch (this.ZoneId)
        {
            case "Asia/Kolkata":
            case "Asia/Calcutta":
                return "IST";
            case "Europe/London":
                return summer ? "BST" : "GMT";
            case "America/New_York":
                return summer ? "EDT" : "EST";
            case "Asia/Singapore":
                return "SGT";
            case "Asia/Dubai":
                return "GST";
            case "Etc/UTC":
            case "UTC":
                return "UTC";
            default:
                break;
        }
        System.TimeSpan offset = this.Zone.GetUtcOffset(at);
        if (offset == System.TimeSpan.Zero)
        {
            return "UTC";
        }
        string sign = offset < System.TimeSpan.Zero ? "-" : "+";
        System.TimeSpan abs = offset.Duration();
        return "UTC" + sign + abs.Hours.ToString("00", En) + ":" + abs.Minutes.ToString("00", En);
    }
}
