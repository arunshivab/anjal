using System.Security.Cryptography;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// An organisation's domains (rc.14, board OrgDomains). A domain is added
/// only once its owner has proved it with a TXT record; a DKIM key is then
/// made for it, so its records can be published at once.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The DKIM selector Anjal makes keys under.</summary>
    public const string DkimSelector = "anjal";

    /// <summary>The organisation owning a domain, or null when no organisation has it.</summary>
    /// <param name="domain">The domain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The domain row, or null.</returns>
    public Task<TenantDomainRow?> TenantDomainAsync(string domain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return this.store.GetTenantDomainAsync(domain.Trim().ToLowerInvariant(), ct);
    }

    /// <summary>An organisation's domains.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its domains.</returns>
    public Task<IReadOnlyList<TenantDomainRow>> TenantDomainsAsync(Guid tenantId, CancellationToken ct = default) =>
        this.store.ListTenantDomainsAsync(tenantId, ct);

    /// <summary>
    /// Add a proved domain to an organisation and make its DKIM key when it
    /// has none (and keys can be kept safely). Returns the error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="domain">The domain, already proved.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>An error, or null.</returns>
    public async Task<string?> AddTenantDomainAsync(TenantRow tenant, string domain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(domain);
        string d = domain.Trim().ToLowerInvariant();
        if (await this.store.GetTenantDomainAsync(d, ct).ConfigureAwait(false) is TenantDomainRow existing)
        {
            return existing.TenantId == tenant.Id ? null : "That domain is already in use on this service.";
        }
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = d, Verified = true }, ct).ConfigureAwait(false);
        if (await this.messageStore.GetDkimKeyAsync(d, ct).ConfigureAwait(false) is null)
        {
            // DES-11 S6 (owner, 10 Oct 2026, "A"): locked with the mail server's public seal key,
            // so the webmail never keeps or holds a DKIM key it could read.
            SealRecord? seal = await KeySeal.ReadRecordAsync(this.messageStore, ct).ConfigureAwait(false);
            if (seal is not null || KeysCanBeKept(this.messageStore))
            {
                using var rsa = RSA.Create(2048);
                string pem = rsa.ExportPkcs8PrivateKeyPem();
                await this.messageStore.UpsertDkimKeyAsync(new DkimKeyRow
                {
                    Domain = d,
                    Selector = DkimSelector,
                    PrivateKeyPem = seal is null ? pem : KeySeal.Seal(pem, Convert.FromBase64String(seal.PublicKey), d, DkimSelector),
                }, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    /// <summary>What the organisation console's sections list beside their names (rc.14).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>People, of whom invited; domains; shared mailboxes' addresses; applications; keys.</returns>
    public async Task<(int People, int Invited, int Domains, IReadOnlyList<string> Shared, int Apps, int Keys)> OrgCountsAsync(TenantRow tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Dictionary<Guid, SharedMailbox> shared = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyList<MailboxRow> boxes = await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false);
        List<MailboxRow> people = boxes.Where(b => !shared.ContainsKey(b.Id)).ToList();
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        return (
            people.Count,
            people.Count(p => p.Enabled && p.PasswordPbkdf2.Length == 0),
            (await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false)).Count,
            boxes.Where(b => shared.ContainsKey(b.Id)).Select(b => b.LocalPart + "@").OrderBy(a => a, StringComparer.Ordinal).ToList(),
            apps.Count,
            apps.Sum(a => a.Keys.Count));
    }

    /// <summary>An organisation's shared mailboxes, with their settings and unread mail (rc.14, board OrgShared).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailboxes, by address.</returns>
    public async Task<IReadOnlyList<(MailboxRow Mailbox, SharedMailbox Info, long Unread)>> SharedListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var result = new List<(MailboxRow, SharedMailbox, long)>();
        foreach ((Guid id, SharedMailbox info) in await this.SharedOfTenantAsync(tenantId, ct).ConfigureAwait(false))
        {
            if (await this.store.GetMailboxByIdAsync(id, ct).ConfigureAwait(false) is MailboxRow m)
            {
                result.Add((m, info, (await this.InboxStateAsync(id, ct).ConfigureAwait(false)).Unread));
            }
        }
        return result.OrderBy(r => r.Item1.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A mailbox by id, whatever its kind (a person's or a shared one).</summary>
    /// <param name="id">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mailbox, or null.</returns>
    public Task<MailboxRow?> GetMailboxAnyAsync(Guid id, CancellationToken ct = default) => this.store.GetMailboxByIdAsync(id, ct);

    /// <summary>One CSV cell: quoted when needed, and a leading = + - @ made text so a spreadsheet does not run it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static string CsvCell(string? value) => CsvField(value ?? string.Empty);

    /// <summary>Where a mailbox stands against its organisation's storage plan (DES-11 D2).</summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    public Task<StorageState> StorageStateAsync(MailboxRow mailbox, CancellationToken ct = default) =>
        StoragePlan.StateOfAsync(this.store, mailbox, ct);

    /// <summary>
    /// True when Anjal can make and keep DKIM keys itself: the mail server has published its seal
    /// key (DES-11 S6), or the store is in memory, or plain keys were allowed on purpose.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a key is made when a domain is added.</returns>
    public async Task<bool> CanMakeDkimKeysAsync(CancellationToken ct = default) =>
        await KeySeal.ReadRecordAsync(this.messageStore, ct).ConfigureAwait(false) is not null || KeysCanBeKept(this.messageStore);

    // Without the seal, a DKIM key is kept by the webmail only in memory, or in plain where that
    // was allowed on purpose. (ANJAL_KEK no longer counts here: the webmail's store never had it,
    // so with it set the keys were kept in plain all the same - DES-11 S6.)
    private static bool KeysCanBeKept(IMessageStore store) =>
        store is not PostgresMessageStore
        || string.Equals(Environment.GetEnvironmentVariable("ANJAL_ALLOW_PLAINTEXT_KEYS"), "true", StringComparison.OrdinalIgnoreCase);
}
