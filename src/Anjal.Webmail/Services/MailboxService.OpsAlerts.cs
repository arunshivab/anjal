using System.Globalization;
using System.Text;
using System.Text.Json;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>What the operators were last told, kept so that alerts repeat and clear on time.</summary>
public sealed class OpsAlertState
{
    /// <summary>The day (yyyy-MM-dd, Anjal's own time zone) the last weekly summary went.</summary>
    public string LastSummaryDay { get; set; } = string.Empty;

    /// <summary>Each open alert by its key, with when it was last mailed.</summary>
    public Dictionary<string, OpenAlert> Open { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One alert the operators have been told about and that is not yet resolved.</summary>
public sealed class OpenAlert
{
    /// <summary>The alert in English, as it was mailed.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When it was first seen.</summary>
    public DateTimeOffset First { get; set; }

    /// <summary>When it was last mailed.</summary>
    public DateTimeOffset LastSent { get; set; }

    /// <summary>"warn" or "bad" when it was last mailed; a step up to "bad" is mailed at once.</summary>
    public string Level { get; set; } = "warn";
}

/// <summary>
/// DES-11 F4 (owner, 10 Oct 2026: "Agreed"): the service summary and the alerts go to the
/// operators by themselves, not only from "Send it now". The summary goes every Monday at 09:00
/// in Anjal's own time zone. An alert goes as soon as something reaches "Needs attention" (the
/// check runs every 15 minutes), again every 6 hours while it stays, and a "resolved" mail when
/// it clears. The "Needs attention" list is made here, once, for the Anjal console and for these
/// mails alike.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>How often an unresolved alert is mailed again.</summary>
    public static readonly TimeSpan AlertRepeat = TimeSpan.FromHours(6);

    /// <summary>The service record kind that keeps <see cref="OpsAlertState"/>.</summary>
    public const string OpsAlertsKind = "ops-alerts";

    /// <summary>
    /// The Anjal console's "Needs attention" list: every tile that is not well, suspended
    /// organisations, organisations near their storage or with records not right, sudden rises,
    /// and the restore drill. Days are the viewer's days (DES-11 F9: the drill's "today" was the
    /// UTC day here while the Service health page used the viewer's).
    /// </summary>
    /// <param name="clock">The viewer's clock.</param>
    /// <param name="tiles">The service health tiles.</param>
    /// <param name="orgs">The organisations.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lines, most important first as found.</returns>
    public async Task<IReadOnlyList<DashLine>> OpsAttentionAsync(ZonedClock clock, IReadOnlyList<HealthTile> tiles, IReadOnlyList<OrgSummary> orgs, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(orgs);
        var attention = new List<DashLine>();
        foreach (HealthTile t in tiles.Where(t => t.Level is "warn" or "bad"))
        {
            attention.Add(new DashLine(OpsIcon(t.Title), t.Text, t.Title, "/ops/health", t.Level, t.Args));
        }
        foreach (OrgSummary o in orgs.Where(o => o.Status == "suspended"))
        {
            attention.Add(new DashLine("home", "{org}: suspended", string.Empty, "/ops/orgs?sel=" + o.Tenant.Id, string.Empty, OrgArgs(o)));
        }
        foreach (OrgSummary o in orgs.Where(o => o.GivenBytes > 0 && Sizes.Percent(o.UsedBytes, o.GivenBytes) >= 80))
        {
            attention.Add(new DashLine("box", "{org}: near its storage", string.Empty, "/ops/orgs?sel=" + o.Tenant.Id, "warn", OrgArgs(o)));
        }
        // rc.15 (item 61): organisations whose records the daily check found wrong.
        foreach (OrgSummary o in orgs.Where(o => o.Tenant.Enabled))
        {
            if (await this.DnsCheckOfAsync(o.Tenant.Id, ct).ConfigureAwait(false) is DnsCheckRecord check)
            {
                List<string> wrong = check.Domains.SelectMany(d => CheckedRecords.Where(k => d.Value.TryGetValue(k, out string? s) && s is "missing" or "different").Select(k => d.Key + " " + k)).ToList();
                if (wrong.Count > 0)
                {
                    Dictionary<string, string> args = OrgArgs(o);
                    args["records"] = string.Join(", ", wrong.Take(4));
                    attention.Add(new DashLine("world-route", "{org}: records not right", "Found by the daily check: {records}", "/ops/orgs?sel=" + o.Tenant.Id, "warn", args));
                }
            }
        }
        // Sudden rises (DES-11 D8): received, sent out and bounced per organisation, refused for the
        // whole service - today over three times the usual day, and at least 20.
        foreach (SuddenRise rise in await this.SuddenRisesAsync(clock, now, ct).ConfigureAwait(false))
        {
            string org = rise.Tenant is Guid t && orgs.FirstOrDefault(o => o.Tenant.Id == t) is OrgSummary rose ? OrgName(rose) : rise.Tenant?.ToString() ?? string.Empty;
            string text = rise.Kind switch
            {
                "sent" => "{org}: {n} sent out today",
                "bounced" => "{org}: {n} bounced today",
                "refused" => "{n} refused by the mail server today",
                _ => "{org}: {n} received today",
            };
            attention.Add(new DashLine("trend-up", text, rise.Usual < 1 ? "Usually almost none a day" : "Usually about {u} a day", rise.Tenant is Guid id ? "/ops/orgs?sel=" + id : "/ops#v-security", rise.Kind is "bounced" or "refused" ? "bad" : "warn",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["org"] = org, ["n"] = rise.Today.ToString("N0", CultureInfo.InvariantCulture), ["u"] = Math.Round(rise.Usual).ToString("N0", CultureInfo.InvariantCulture) }));
        }
        // The restore drill, every six months, with a reminder two weeks before - in the viewer's days.
        DateTime today = clock.Local(now).Date;
        DateTime? due = NextDrillDue(await this.DrillsAsync(ct).ConfigureAwait(false));
        if (due is null)
        {
            attention.Add(new DashLine("version-history", "No restore drill recorded", "Record each drill on the Service health page", "/ops/health", "warn"));
        }
        else if (due.Value <= today.AddDays(14))
        {
            attention.Add(new DashLine("version-history", due.Value < today ? "Restore drill overdue since {date}" : "Restore drill due {date}", string.Empty, "/ops/health", due.Value < today ? "bad" : "warn",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["date"] = clock.Date(new DateTimeOffset(due.Value, TimeSpan.Zero)) }));
        }
        return attention;
    }

    /// <summary>
    /// One pass of the operators' mail: the weekly summary when it is due, and the alerts. Called
    /// by the background sender every 15 minutes. Returns how many mails went.
    /// </summary>
    /// <param name="maildirRoot">Where mail is kept, for the disk tile.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of mails sent.</returns>
    public async Task<int> OperatorMailAsync(string maildirRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        IReadOnlyList<string> operators = Operators.List();
        MailboxRow? from = await this.OperatorSenderAsync(operators, ct).ConfigureAwait(false);
        if (operators.Count == 0 || from is null)
        {
            return 0;
        }
        ZonedClock clock = AnjalClock();
        (IReadOnlyList<HealthTile> tiles, IReadOnlyList<(DateTimeOffset Hour, long Count)> hours) = await this.HealthAsync(maildirRoot, now, ct).ConfigureAwait(false);
        IReadOnlyList<OrgSummary> orgs = await this.ListOrganisationsAsync(ct).ConfigureAwait(false);
        OpsAlertState state = await this.ServiceRecordAsync<OpsAlertState>(OpsAlertsKind, ct).ConfigureAwait(false) ?? new OpsAlertState();
        int sent = 0;

        DateTimeOffset local = clock.Local(now);
        string day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (local.DayOfWeek == DayOfWeek.Monday && local.Hour >= 9 && state.LastSummaryDay != day)
        {
            string text = SummaryText(tiles, hours, orgs);
            foreach (string op in operators)
            {
                await this.SendSystemMailAsync(from, op, "Anjal service summary, week of " + clock.Date(now), text, ct).ConfigureAwait(false);
                sent++;
            }
            state.LastSummaryDay = day;
        }

        // Each thing is known by what it is - its line's words before the figures are filled in,
        // and where it opens - never by its figures: a disk going from 223.84 GB to 223.85 GB is
        // the same thing, not one resolved and another raised (found 10 Oct 2026 in the screen checks).
        var current = new Dictionary<string, (string Text, string Level)>(StringComparer.Ordinal);
        foreach (DashLine line in await this.OpsAttentionAsync(clock, tiles, orgs, now, ct).ConfigureAwait(false))
        {
            if (line.Level is "warn" or "bad")
            {
                string filled = HealthTile.Fill(line.Text, line.Args) + (line.Sub.Length > 0 ? " - " + HealthTile.Fill(line.Sub, line.Args) : string.Empty);
                current[AlertKey(line)] = (filled, line.Level);
            }
        }
        // Alerts kept before the keys changed (10 Oct 2026): taken over quietly - what is open now
        // counts as already mailed when they were, so the change itself sends nothing.
        List<OpenAlert> before = state.Open.Where(k => !k.Key.StartsWith(AlertKeyMark, StringComparison.Ordinal)).Select(k => k.Value).ToList();
        if (before.Count > 0)
        {
            foreach (string old in state.Open.Keys.Where(k => !k.StartsWith(AlertKeyMark, StringComparison.Ordinal)).ToList())
            {
                state.Open.Remove(old);
            }
            foreach ((string key, (string text, string level)) in current)
            {
                if (!state.Open.ContainsKey(key))
                {
                    state.Open[key] = new OpenAlert { Text = text, First = before.Min(b => b.First), LastSent = before.Max(b => b.LastSent), Level = level };
                }
            }
        }
        var raise = new List<string>();
        foreach ((string key, (string text, string level)) in current)
        {
            if (!state.Open.TryGetValue(key, out OpenAlert? open))
            {
                state.Open[key] = new OpenAlert { Text = text, First = now, LastSent = now, Level = level };
                raise.Add(text);
            }
            else if (now - open.LastSent >= AlertRepeat || (level == "bad" && open.Level != "bad"))
            {
                open.LastSent = now;
                open.Text = text;
                open.Level = level;
                raise.Add(text + " (still, since " + clock.Time(open.First) + " " + clock.Date(open.First) + ")");
            }
            else
            {
                open.Text = text;
            }
        }
        var cleared = state.Open.Where(kv => !current.ContainsKey(kv.Key)).ToList();
        // (The words mailed as resolved are the latest seen, figures and all.)
        foreach (KeyValuePair<string, OpenAlert> c in cleared)
        {
            state.Open.Remove(c.Key);
        }
        if (raise.Count > 0)
        {
            string text = AlertText("Anjal needs attention:", raise, "Open the Anjal console's overview to act on each.");
            foreach (string op in operators)
            {
                await this.SendSystemMailAsync(from, op, raise.Count == 1 ? "Anjal alert: " + raise[0] : "Anjal alert: " + raise.Count.ToString(CultureInfo.InvariantCulture) + " things need attention", text, ct).ConfigureAwait(false);
                sent++;
            }
        }
        if (cleared.Count > 0)
        {
            string text = AlertText("Resolved:", cleared.Select(c => c.Value.Text).ToList(), "Nothing more is needed for these.");
            foreach (string op in operators)
            {
                await this.SendSystemMailAsync(from, op, cleared.Count == 1 ? "Anjal resolved: " + cleared[0].Value.Text : "Anjal resolved: " + cleared.Count.ToString(CultureInfo.InvariantCulture) + " alerts", text, ct).ConfigureAwait(false);
                sent++;
            }
        }
        await this.messageStore.SetServiceRecordAsync(OpsAlertsKind, JsonSerializer.Serialize(state), ct).ConfigureAwait(false);
        return sent;
    }

    /// <summary>Anjal's own clock (ANJAL_TIMEZONE, as for the dates Anjal writes into mail).</summary>
    /// <returns>The clock.</returns>
    public static ZonedClock AnjalClock() =>
        ZonedClock.For(Environment.GetEnvironmentVariable("ANJAL_TIMEZONE") is { Length: > 0 } zone ? zone : MailboxPreferences.DefaultTimeZone, MailboxPreferences.DefaultDateFormat);

    /// <summary>The icon for a health tile's line.</summary>
    /// <param name="title">The tile's title.</param>
    /// <returns>A LiPicons name.</returns>
    public static string OpsIcon(string title) => title switch
    {
        "Delivery queue" => "upload",
        "Certificates" => "lock",
        "Disk" => "database",
        "Last good backup" => "archive",
        "DKIM key lock" => "lock",
        "Reputation" => "security",
        "TLS reports" => "lock",
        "DKIM" => "world-route",
        _ => "home",
    };

    private static string OrgName(OrgSummary o) => o.Tenant.DisplayName.Length > 0 ? o.Tenant.DisplayName : o.Tenant.Slug;

    private static Dictionary<string, string> OrgArgs(OrgSummary o) => new(StringComparer.Ordinal) { ["org"] = OrgName(o) };

    private const string AlertKeyMark = "k1|";

    // What a line of Needs attention is, figures left out: its words with the blanks still in
    // them, its second line's, and where it opens (the organisation or the page).
    private static string AlertKey(DashLine line) => AlertKeyMark + line.Text + "|" + line.Sub + "|" + line.Href;

    private static string AlertText(string head, IReadOnlyList<string> lines, string foot)
    {
        var sb = new StringBuilder();
        sb.Append(head).Append("\r\n\r\n");
        foreach (string l in lines)
        {
            sb.Append("- ").Append(l).Append("\r\n");
        }
        sb.Append("\r\n").Append(foot).Append("\r\n\r\nOperators manage the service, never anyone's mail.\r\n");
        return sb.ToString();
    }

    // The mails go from no-reply at the first operator's own domain on this server.
    private async Task<MailboxRow?> OperatorSenderAsync(IReadOnlyList<string> operators, CancellationToken ct)
    {
        foreach (string op in operators)
        {
            int at = op.LastIndexOf('@');
            if (at > 0 && await this.store.GetMailboxAsync(op[..at], op[(at + 1)..], ct).ConfigureAwait(false) is MailboxRow row)
            {
                return row;
            }
        }
        return null;
    }
}
