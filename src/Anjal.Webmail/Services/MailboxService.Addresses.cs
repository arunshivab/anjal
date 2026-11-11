using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.22 (item 41, mockup 17): extra addresses (aliases) on a mailbox, and group addresses that
/// reach several people. Kept in the organisation's address book; delivery expands them.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>An organisation's address book.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The book.</returns>
    public Task<AddressBook> AddressBookAsync(Guid tenantId, CancellationToken ct = default) => AddressBook.ReadAsync(this.store, tenantId, ct);

    /// <summary>Add an extra address that delivers to a person. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="local">The address, before the @.</param>
    /// <param name="domain">Its domain.</param>
    /// <param name="mailboxId">The person it delivers to.</param>
    /// <param name="canSend">Whether they may send from it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AddAliasAsync(TenantRow tenant, string local, string domain, Guid mailboxId, bool canSend, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(domain);
        (string? problem, string address) = await this.AddressProblemAsync(tenant, local, domain, ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }
        MailboxRow? person = await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false);
        if (person is null || person.TenantId != tenant.Id)
        {
            return "That person is not in this organisation.";
        }
        AddressBook book = await this.AddressBookAsync(tenant.Id, ct).ConfigureAwait(false);
        if (book.Aliases.Count >= 500)
        {
            return "The organisation has the most extra addresses allowed.";
        }
        book.Aliases.Add(new AliasEntry { Address = address, MailboxId = mailboxId, CanSend = canSend });
        await AddressBook.WriteAsync(this.store, tenant.Id, book, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove an extra address.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> RemoveAliasAsync(TenantRow tenant, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        AddressBook book = await this.AddressBookAsync(tenant.Id, ct).ConfigureAwait(false);
        int removed = book.Aliases.RemoveAll(a => string.Equals(a.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await AddressBook.WriteAsync(this.store, tenant.Id, book, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>Make or change a group address. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="local">The address, before the @.</param>
    /// <param name="domain">Its domain.</param>
    /// <param name="members">The members' mailboxes.</param>
    /// <param name="orgOnly">True when only the organisation's own people may write to it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SaveGroupAsync(TenantRow tenant, string local, string domain, IReadOnlyList<Guid> members, bool orgOnly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(members);
        AddressBook book = await this.AddressBookAsync(tenant.Id, ct).ConfigureAwait(false);
        string address = local.Trim().ToLowerInvariant() + "@" + domain.Trim().ToLowerInvariant();
        GroupEntry? existing = book.GroupAt(address);
        if (existing is null)
        {
            (string? problem, string clean) = await this.AddressProblemAsync(tenant, local, domain, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }
            address = clean;
            if (book.Groups.Count >= 200)
            {
                return "The organisation has the most group addresses allowed.";
            }
        }
        var people = new List<Guid>();
        foreach (Guid id in members.Distinct())
        {
            MailboxRow? m = await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false);
            if (m is not null && m.TenantId == tenant.Id)
            {
                people.Add(id);
            }
        }
        if (people.Count == 0)
        {
            return "Choose at least one member.";
        }
        if (people.Count > AddressBook.MaxMembers)
        {
            return $"A group address reaches at most {AddressBook.MaxMembers} people.";
        }
        if (existing is null)
        {
            book.Groups.Add(new GroupEntry { Address = address, Members = people, OrgOnly = orgOnly });
        }
        else
        {
            existing.Members = people;
            existing.OrgOnly = orgOnly;
        }
        await AddressBook.WriteAsync(this.store, tenant.Id, book, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Remove a group address.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was found.</returns>
    public async Task<bool> RemoveGroupAsync(TenantRow tenant, string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(address);
        AddressBook book = await this.AddressBookAsync(tenant.Id, ct).ConfigureAwait(false);
        int removed = book.Groups.RemoveAll(g => string.Equals(g.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await AddressBook.WriteAsync(this.store, tenant.Id, book, ct).ConfigureAwait(false);
        }
        return removed > 0;
    }

    /// <summary>The extra addresses a person may send from (rc.22), for the From choice when writing.</summary>
    /// <param name="mailboxId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The addresses, lower case.</returns>
    public async Task<IReadOnlyList<string>> SendAsAddressesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        Guid? tenant = await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false);
        if (tenant is not Guid t)
        {
            return Array.Empty<string>();
        }
        AddressBook book = await this.AddressBookAsync(t, ct).ConfigureAwait(false);
        return book.Aliases.Where(a => a.MailboxId == mailboxId && a.CanSend).Select(a => a.Address).OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    private async Task<(string? Problem, string Address)> AddressProblemAsync(TenantRow tenant, string local, string domain, CancellationToken ct)
    {
        string u = local.Trim().ToLowerInvariant();
        string d = domain.Trim().ToLowerInvariant();
        if (UsernameProblem(u, false) is string bad)
        {
            return (bad, string.Empty);
        }
        if (!(await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Any(x => string.Equals(x.Domain, d, StringComparison.OrdinalIgnoreCase)))
        {
            return ($"{d} is not one of this organisation's domains.", string.Empty);
        }
        if (!await this.IsFreeAsync(tenant, u, d, ct).ConfigureAwait(false))
        {
            return ("That address is already taken.", string.Empty);
        }
        return (null, u + "@" + d);
    }

    /// <summary>
    /// rc.22 (item 50; owner 19 Oct 2026): what in a message looks like patient or personal
    /// information going outside the organisation, in words - or, when the organisation asks about
    /// every file, that files are going outside; empty when nothing does, when every recipient is
    /// inside, or for an individual (no organisation to go outside of).
    /// </summary>
    /// <param name="mailboxId">The writer.</param>
    /// <param name="request">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The kind (patient, personal, both, or files when the organisation asks about every file), the outside address, then what was found.</returns>
    public async Task<IReadOnlyList<string>> PatientWarningAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> domains = await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false);
        if (domains.Count == 0 || await this.IsIndividualAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return Array.Empty<string>();
        }
        var outside = new List<string>();
        foreach (Anjal.Mime.MailAddress a in Anjal.Mime.AddressParser.Parse(request.To + ", " + request.Cc + ", " + request.Bcc))
        {
            int at = a.Address.LastIndexOf('@');
            if (at > 0 && !domains.Contains(a.Address[(at + 1)..], StringComparer.OrdinalIgnoreCase) && !outside.Contains(a.Address, StringComparer.OrdinalIgnoreCase))
            {
                outside.Add(a.Address);
            }
        }
        if (outside.Count == 0)
        {
            return Array.Empty<string>();
        }
        List<string> fileNames = request.Attachments.Select(x => x.FileName).ToList();
        IReadOnlyList<string> patient = Anjal.Mailbox.PatientInformation.Signs(request.Subject, request.Body, fileNames);
        IReadOnlyList<string> personal = Anjal.Mailbox.PatientInformation.PersonalSigns(request.Subject, request.Body, fileNames);
        string kind = patient.Count > 0 && personal.Count > 0 ? "both" : patient.Count > 0 ? "patient" : personal.Count > 0 ? "personal" : string.Empty;
        if (kind.Length == 0 && fileNames.Count > 0)
        {
            // Owner, 19 Oct 2026: the organisation's switch - any files going outside are asked about.
            Guid? tenant = await this.TenantOfAsync(mailboxId, ct).ConfigureAwait(false);
            if (tenant is Guid t && (await this.MailPolicyOfAsync(t, ct).ConfigureAwait(false)).AskBeforeFilesOutside)
            {
                kind = "files";
            }
        }
        if (kind.Length == 0)
        {
            return Array.Empty<string>();
        }
        var all = new List<string> { kind, string.Join(", ", outside.Take(3)) };
        all.AddRange(patient);
        all.AddRange(personal);
        if (kind == "files")
        {
            all.Add(fileNames.Count == 1 ? fileNames[0] : fileNames.Count + " files");
        }
        return all;
    }

    /// <summary>
    /// rc.27 (item 72.4, brief 6k): the patient warning in the activity log - when it was shown, and
    /// when the person chose to send anyway: what Anjal noticed and where it was going (domains only),
    /// never the mail's words or its files' names. rc.28 (D-331): also when the person went back to
    /// change the mail, and the sent mail's Message-ID, so the line leads to its evidence copy.
    /// </summary>
    /// <param name="actor">Who.</param>
    /// <param name="signs">What <see cref="PatientWarningAsync"/> found.</param>
    /// <param name="outcome">One of <see cref="PatientShown"/>, <see cref="PatientSent"/> and <see cref="PatientChanged"/>.</param>
    /// <param name="messageId">For <see cref="PatientSent"/>, the sent mail's Message-ID; empty when there is none (Send one each makes one per person).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task LogPatientWarningAsync(string actor, IReadOnlyList<string> signs, string outcome, string messageId = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signs);
        ArgumentNullException.ThrowIfNull(outcome);
        if (signs.Count < 2 || outcome is not (PatientShown or PatientSent or PatientChanged or PatientSecure or PatientRemoved))
        {
            return;
        }
        string domains = string.Join(", ", signs[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Contains('@', StringComparison.Ordinal) ? a[(a.LastIndexOf('@') + 1)..] : a).Distinct(StringComparer.OrdinalIgnoreCase));
        // A file's name can itself be patient information (review, 28 Oct): the log says "a file", never which.
        string noticed = string.Join(", ", (signs[0] == "files" ? Array.Empty<string>() : signs.Skip(2))
            .Select(x => x.StartsWith("a file named ", StringComparison.Ordinal) ? "a file" : x)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture) : g.Key));
        await this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = actor,
            Action = "webmail.patient-warning." + outcome,
            Subject = domains,
            Detail = signs[0] + (noticed.Length > 0 ? ": " + noticed : string.Empty)
                + (outcome is PatientSent or PatientSecure && !string.IsNullOrWhiteSpace(messageId) ? " · " + messageId.Trim() : string.Empty),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>rc.28 (D-331): the warning was shown and the mail held back.</summary>
    public const string PatientShown = "shown";

    /// <summary>rc.28 (D-331): the person sent it anyway, unchanged since the warning.</summary>
    public const string PatientSent = "sent";

    /// <summary>rc.28 (D-331): the person chose "Change the mail" after the warning.</summary>
    public const string PatientChanged = "changed";

    /// <summary>rc.28 (DES-19 §2): the person sent the outside people a secure link instead.</summary>
    public const string PatientSecure = "secure-link";

    /// <summary>rc.28 (DES-19 §2): the person took the outside people off and sent to the rest.</summary>
    public const string PatientRemoved = "removed";

    /// <summary>
    /// rc.28 (DES-19 §2, brief 6k): "Remove them and send to the rest" - every address outside the
    /// organisation is taken off To, Cc and Bcc.
    /// </summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="request">The mail, changed in place.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error when nobody inside is left, or null.</returns>
    public async Task<string?> DropOutsideAsync(Guid mailboxId, ComposeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<string> domains = await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false);
        string Keep(string field) => string.Join(", ", Anjal.Mime.AddressParser.Parse(field)
            .Where(a => domains.Contains(a.Domain, StringComparer.OrdinalIgnoreCase)).Select(a => a.ToString()));
        string to = Keep(request.To);
        string cc = Keep(request.Cc);
        string bcc = Keep(request.Bcc);
        if (domains.Count == 0 || to.Length + cc.Length + bcc.Length == 0)
        {
            return "Nobody inside the organisation is left to send it to.";
        }
        // A mail needs a To: the first person left moves up when only Cc or Bcc remain.
        if (to.Length == 0)
        {
            (to, cc, bcc) = cc.Length > 0 ? (cc, string.Empty, bcc) : (bcc, cc, string.Empty);
        }
        request.To = to;
        request.Cc = cc;
        request.Bcc = bcc;
        return null;
    }
}

