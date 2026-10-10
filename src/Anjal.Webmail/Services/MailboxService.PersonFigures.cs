using System.Globalization;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>An organisation's "Figures per person" setting (rc.15, item 60): off unless an administrator turns it on, with a reason.</summary>
public sealed class PersonFiguresSetting
{
    /// <summary>On or off.</summary>
    public bool On { get; set; }

    /// <summary>Why the organisation counts figures for each person; required to turn it on.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>How far back mail is counted: "30d", "90d", "1y" or "all".</summary>
    public string From { get; set; } = "30d";

    /// <summary>The first moment counted (null for all kept mail).</summary>
    public DateTimeOffset? FromAt { get; set; }

    /// <summary>The administrator who turned it on or off.</summary>
    public string By { get; set; } = string.Empty;

    /// <summary>When it was last turned on or off.</summary>
    public DateTimeOffset? ChangedAt { get; set; }
}

/// <summary>One person's figures (rc.15, item 60): counts only, never mail.</summary>
/// <param name="Mailbox">The person.</param>
/// <param name="Received">Messages received in the period (Junk not included).</param>
/// <param name="Sent">Messages sent.</param>
/// <param name="Junk">Messages filed in Junk.</param>
public sealed record PersonFigures(MailboxRow Mailbox, long Received, long Sent, long Junk);

/// <summary>
/// rc.15 (item 60; owner, 7 Oct 2026): figures per person. Off by default.
/// An administrator turns it on with a purpose and a starting point; every
/// person is told by mail why, what is counted and from when, and sees it in
/// Settings; turning it on or off is written to the activity log; turning it
/// off tells everyone too. Only counts are shown - never what mail says, who
/// it is from or to, or its subjects.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the organisation document holding the setting.</summary>
    public const string PersonFiguresKind = "person-figures";

    /// <summary>The choices of where counting starts, as offered to the administrator.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> PersonFiguresStarts = new[]
    {
        ("30d", "The last 30 days"),
        ("90d", "The last 90 days"),
        ("1y", "The last year"),
        ("all", "All mail kept"),
    };

    /// <summary>An organisation's setting (off when never set).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The setting.</returns>
    public async Task<PersonFiguresSetting> PersonFiguresOfAsync(Guid tenantId, CancellationToken ct = default) =>
        await this.ReadTenantDocumentAsync<PersonFiguresSetting>(tenantId, PersonFiguresKind, ct).ConfigureAwait(false) ?? new PersonFiguresSetting();

    /// <summary>
    /// Turn figures per person on (with a purpose and a start) or off, and tell
    /// every person in the organisation by mail. Returns the error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="by">The administrator.</param>
    /// <param name="on">On or off.</param>
    /// <param name="purpose">Why; required to turn it on.</param>
    /// <param name="from">"30d", "90d", "1y" or "all".</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>An error, or null.</returns>
    public async Task<string?> SetPersonFiguresAsync(TenantRow tenant, MailboxRow by, bool on, string? purpose, string? from, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(by);
        PersonFiguresSetting setting = await this.PersonFiguresOfAsync(tenant.Id, ct).ConfigureAwait(false);
        if (on == setting.On)
        {
            return null;
        }
        string why = (purpose ?? string.Empty).Trim();
        if (on && why.Length < 10)
        {
            return "Write why the figures are needed (at least 10 letters). Everyone will be told.";
        }
        string start = PersonFiguresStarts.Any(s => s.Key == from) ? from! : "30d";
        setting = on
            ? new PersonFiguresSetting
            {
                On = true,
                Purpose = why.Length > 300 ? why[..300] : why,
                From = start,
                FromAt = start switch { "90d" => now.AddDays(-90), "1y" => now.AddYears(-1), "all" => null, _ => now.AddDays(-30) },
                By = by.Address,
                ChangedAt = now,
            }
            : new PersonFiguresSetting { On = false, Purpose = setting.Purpose, From = setting.From, FromAt = setting.FromAt, By = by.Address, ChangedAt = now };
        await this.WriteTenantDocumentAsync(tenant.Id, PersonFiguresKind, setting, ct).ConfigureAwait(false);

        string org = tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : tenant.Slug;
        string who = by.DisplayName.Trim().Length > 0 ? by.DisplayName.Trim() + " (" + by.Address + ")" : by.Address;
        string day = now.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string subject = on ? $"{org} now counts mail figures for each person" : $"{org} has stopped counting mail figures for each person";
        string text = on
            ? $"{who} turned on figures per person for {org} on {day} (UTC).\r\n\r\n"
                + $"Why: {setting.Purpose}\r\n\r\n"
                + "What is counted for each person: how many messages they received, sent and had filed in Junk, and how much space their mailbox uses. "
                + "What mail says, who it is from or to, and its subjects are never shown.\r\n\r\n"
                + $"Counted from: {(setting.FromAt is DateTimeOffset f ? f.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "the oldest mail kept")}.\r\n\r\n"
                + $"Only {org}'s administrators see these figures. Turning them on or off is written to the activity log, and everyone is told. You can see this at any time in Settings, Security.\r\n\r\n{org} mail"
            : $"{who} turned off figures per person for {org} on {day} (UTC). The figures are no longer counted or shown.\r\n\r\n{org} mail";
        foreach (MailboxRow person in await this.PeopleOfAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            await this.SendSystemMailAsync(person, person.Address, subject, text, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Each person's figures for a period - counted only from the setting's
    /// start - when the setting is on; empty when it is off.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="period">The period.</param>
    /// <param name="clock">The viewer's zone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Each person's figures, most received first.</returns>
    public async Task<IReadOnlyList<PersonFigures>> PersonFiguresAsync(TenantRow tenant, DashPeriod period, ZonedClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(clock);
        PersonFiguresSetting setting = await this.PersonFiguresOfAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!setting.On)
        {
            return Array.Empty<PersonFigures>();
        }
        DashPeriod counted = setting.FromAt is DateTimeOffset start && start > period.Start ? period with { Start = start } : period;
        if (counted.Start >= counted.End)
        {
            return Array.Empty<PersonFigures>();
        }
        var rows = new List<PersonFigures>();
        foreach (MailboxRow person in await this.PeopleOfAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            MailFigures f = await this.FiguresAsync(FigureScope.Mailbox(person.Id), counted, clock, ct).ConfigureAwait(false);
            rows.Add(new PersonFigures(person, f.Received, f.Sent, f.Junk));
        }
        return rows.OrderByDescending(r => r.Received).ThenBy(r => r.Mailbox.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The organisation's people: its mailboxes that are not shared ones.
    private async Task<IReadOnlyList<MailboxRow>> PeopleOfAsync(Guid tenantId, CancellationToken ct)
    {
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenantId, ct).ConfigureAwait(false);
        return (await this.store.ListMailboxesAsync(tenantId, ct).ConfigureAwait(false)).Where(m => !shared.ContainsKey(m.Id) && m.Enabled).ToList();
    }
}
