using System.Globalization;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>Which inactive-account reminders went for a mailbox (rc.26, item 63), kept as its "inactive" document.</summary>
public sealed class InactiveNotes
{
    /// <summary>The mailbox document kind.</summary>
    public const string Kind = "inactive";

    /// <summary>The inactive moment these notes are for; a sign-in moves it, and the notes start again.</summary>
    public DateTimeOffset For { get; set; }

    /// <summary>The reminders sent: days before deletion.</summary>
    public List<int> Sent { get; set; } = new();
}

/// <summary>
/// rc.26 (item 63, POL-01 v1.1 §4, D-166; approved 26 Oct 2026): inactive accounts. The mail server
/// refuses mail to an inactive mailbox (InactivePolicy); this daily pass sends the reminders - 60, 30,
/// 15, 7, 3 and 1 days before deletion, to the organisation's administrators and the person's own
/// recovery address (or, without one, to the mailbox itself, and the operator is told) - and deletes the
/// mailbox 186 days after it became inactive, with a notice to the same people. The address is never
/// given to anyone again.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>Note a sign-in for the inactive-account clock.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task TouchSignInAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.TouchSignInAsync(mailboxId, DateTimeOffset.UtcNow, ct);

    /// <summary>Where a person stands on the inactive-account clock, for the organisation console; null when the clock does not apply.</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>When it becomes (or became) inactive, and when it would be deleted.</returns>
    public async Task<(DateTimeOffset Inactive, DateTimeOffset Deleted)?> InactiveClockAsync(MailboxRow mailbox, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return await InactiveAccounts.CountsAsync(this.store, mailbox, ct).ConfigureAwait(false)
            ? (InactiveAccounts.InactiveFrom(mailbox), InactiveAccounts.DeletedOn(mailbox))
            : null;
    }

    /// <summary>The daily pass: reminders and deletions. Returns how many mails and deletions it made.</summary>
    /// <param name="maildirRoot">Where mail files live.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>(Reminders, Deleted).</returns>
    public async Task<(int Reminders, int Deleted)> InactivePassAsync(string maildirRoot, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maildirRoot);
        int reminders = 0;
        int deleted = 0;
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(null, ct).ConfigureAwait(false))
        {
            if (now < InactiveAccounts.InactiveFrom(m) || !await InactiveAccounts.CountsAsync(this.store, m, ct).ConfigureAwait(false))
            {
                continue;
            }
            if (await this.store.GetTenantByIdAsync(m.TenantId, ct).ConfigureAwait(false) is not TenantRow tenant)
            {
                continue;
            }
            DateTimeOffset deleteOn = InactiveAccounts.DeletedOn(m);
            if (now >= deleteOn)
            {
                IReadOnlyList<string> told = await this.InactiveAddresseesAsync(tenant, m, ct).ConfigureAwait(false);
                string? error = await this.DeleteAccountAsync(tenant, m.Id, maildirRoot, ct).ConfigureAwait(false);
                if (error is null)
                {
                    deleted++;
                    await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = "anjal", Action = "inactive.deleted", Subject = m.Address, Detail = "No sign-in since " + m.LastSignInAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
                    await this.InactiveMailAsync(tenant, m, told, "Mailbox deleted: " + m.Address,
                        $"The mailbox {m.Address} of {tenant.DisplayName} was deleted today, {InactiveAccounts.DeletedAfter.TotalDays:0} days after it became inactive (nobody had signed in to it since {m.LastSignInAt.UtcDateTime:d MMM yyyy}). Its mail and settings are gone; the address will never be given to anyone else.", ct).ConfigureAwait(false);
                }
                else
                {
                    Console.Error.WriteLine($"[inactive] {m.Address} not deleted: {error}");
                }
                continue;
            }
            InactiveNotes notes = await this.ReadDocumentAsync<InactiveNotes>(m.Id, InactiveNotes.Kind, ct).ConfigureAwait(false) ?? new InactiveNotes();
            DateTimeOffset inactiveFrom = InactiveAccounts.InactiveFrom(m);
            if (notes.For != inactiveFrom)
            {
                notes = new InactiveNotes { For = inactiveFrom };
            }
            int daysLeft = (int)Math.Ceiling((deleteOn - now).TotalDays);
            int? due = InactiveAccounts.ReminderDays.Where(d => daysLeft <= d && !notes.Sent.Contains(d)).Cast<int?>().LastOrDefault();
            if (due is int d)
            {
                // Every reminder whose day has passed counts as sent; only the nearest goes.
                foreach (int passed in InactiveAccounts.ReminderDays.Where(x => x >= d))
                {
                    if (!notes.Sent.Contains(passed))
                    {
                        notes.Sent.Add(passed);
                    }
                }
                await this.WriteDocumentAsync(m.Id, InactiveNotes.Kind, notes, ct).ConfigureAwait(false);
                IReadOnlyList<string> to = await this.InactiveAddresseesAsync(tenant, m, ct).ConfigureAwait(false);
                await this.InactiveMailAsync(tenant, m, to, "Mailbox to be deleted in " + daysLeft.ToString(CultureInfo.InvariantCulture) + (daysLeft == 1 ? " day: " : " days: ") + m.Address,
                    $"Nobody has signed in to the mailbox {m.Address} of {tenant.DisplayName} since {m.LastSignInAt.UtcDateTime:d MMM yyyy}. It is inactive: mail to it is refused, and the senders are told to ask the person for another address.\r\n\r\n"
                    + $"It will be deleted on {deleteOn.UtcDateTime:d MMM yyyy}, with all its mail and settings, unless someone signs in to it before then - one sign-in is enough. The address will never be given to anyone else.", ct).ConfigureAwait(false);
                reminders += to.Count;
            }
        }
        return (reminders, deleted);
    }

    // The administrators and the person's own recovery address; without one, the mailbox itself, and the operators are told.
    private async Task<IReadOnlyList<string>> InactiveAddresseesAsync(TenantRow tenant, MailboxRow m, CancellationToken ct)
    {
        var to = new List<string>();
        foreach (Guid admin in await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false))
        {
            if (admin != m.Id && await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a && !to.Contains(a.Address, StringComparer.OrdinalIgnoreCase))
            {
                to.Add(a.Address);
            }
        }
        string personal = (await this.ReadDocumentAsync<SecurityDocument>(m.Id, SecurityKind, ct).ConfigureAwait(false))?.RecoveryConfirmedAddress ?? string.Empty;
        if (personal.Length > 0)
        {
            to.Add(personal);
        }
        else
        {
            to.Add(m.Address);
            to.AddRange(Operators.List().Where(o => !to.Contains(o, StringComparer.OrdinalIgnoreCase)));
        }
        return to;
    }

    private async Task InactiveMailAsync(TenantRow tenant, MailboxRow m, IReadOnlyList<string> to, string subject, string text, CancellationToken ct)
    {
        MailboxRow? from = await this.OperatorSenderAsync(Operators.List(), ct).ConfigureAwait(false) ?? m;
        foreach (string address in to)
        {
            await this.SendSystemMailAsync(from, address, subject, text + "\r\n\r\nAnjal", ct).ConfigureAwait(false);
        }
    }
}
