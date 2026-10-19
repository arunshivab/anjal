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
    /// rc.22 (item 50): what in a message looks like patient information going outside the
    /// organisation, in words; empty when nothing does, when every recipient is inside, or for an
    /// individual (no organisation to go outside of).
    /// </summary>
    /// <param name="mailboxId">The writer.</param>
    /// <param name="request">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reasons: the outside address first, then what was found.</returns>
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
        IReadOnlyList<string> signs = PatientInformation.Signs(request.Subject, request.Body, request.Attachments.Select(x => x.FileName));
        if (signs.Count == 0)
        {
            return Array.Empty<string>();
        }
        var all = new List<string> { string.Join(", ", outside.Take(3)) };
        all.AddRange(signs);
        return all;
    }
}
