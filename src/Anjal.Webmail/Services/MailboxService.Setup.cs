using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One step of an organisation's set-up (design review [6f]).</summary>
/// <param name="Key">name, domain, prove, people, shared or apps.</param>
/// <param name="Title">The step's words.</param>
/// <param name="Done">Whether it is done.</param>
/// <param name="DoneAt">When it was done, when known.</param>
/// <param name="Href">Where it is done.</param>
/// <param name="Action">The button's words for the current step.</param>
public sealed record SetupStep(string Key, string Title, bool Done, DateTimeOffset? DoneAt, string Href, string Action);

/// <summary>The organisation's set-up checklist (rc.16, design review [6f], owner 11 Oct 2026).</summary>
public sealed partial class MailboxService
{
    // The records a domain needs before mail for it can be trusted.
    private static readonly string[] ProvedRecords = { "MX", "SPF", "DKIM", "DMARC" };

    /// <summary>
    /// The six steps a new organisation goes through, and how far it has got: its name and logo
    /// (a logo set in Branding), its domain added, the domain proved (the daily check found MX,
    /// SPF, DKIM and DMARC right for every domain), its first people invited, a shared mailbox,
    /// and an application that sends mail. Worked out from what the organisation already has.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="people">Its people.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The steps, in order.</returns>
    public async Task<IReadOnlyList<SetupStep>> SetupStepsAsync(TenantRow tenant, IReadOnlyList<PersonView> people, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(people);
        Branding branding = await this.ReadTenantDocumentAsync<Branding>(tenant.Id, BrandingKind, ct).ConfigureAwait(false) ?? new Branding();
        IReadOnlyList<TenantDomainRow> domains = await this.store.ListTenantDomainsAsync(tenant.Id, ct).ConfigureAwait(false);
        DnsCheckRecord? check = await this.DnsCheckOfAsync(tenant.Id, ct).ConfigureAwait(false);
        bool proved = domains.Count > 0 && check is not null && domains.All(d =>
            check.Domains.TryGetValue(d.Domain, out Dictionary<string, string>? found)
            && ProvedRecords.All(k => found.TryGetValue(k, out string? s) && s is not ("missing" or "different" or "unchecked")));
        IReadOnlyList<(MailboxRow Mailbox, SharedMailbox Info, long Unread)> shared = await this.SharedListAsync(tenant.Id, ct).ConfigureAwait(false);
        List<OrgApp> apps = await this.AppsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        List<PersonView> invited = people.Where(p => !p.Admin).ToList();
        var steps = new List<SetupStep>();
        // rc.25 (DES-15 §6.2, §11): an encrypted organisation prints its recovery sheet before anything else.
        if (this.KeyManager is Anjal.Mailbox.MailKeyManager km && await km.EncryptsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            DateTimeOffset? sheet = (await km.Store.GetTenantKeysAsync(tenant.Id, ct).ConfigureAwait(false))?.SheetConfirmedAt;
            steps.Add(new SetupStep("sheet", "Print the recovery sheet", sheet is not null, sheet, "/org/encryption", "Print the sheet"));
        }
        steps.AddRange(new[]
        {
            new SetupStep("name", "Name and logo", branding.Logo.Length > 0, null, "/org/branding", "Add your logo"),
            new SetupStep("domain", "Add your domain", domains.Count > 0, domains.Count > 0 ? domains.Min(d => d.CreatedAt) : null, "/org/domains", "Add a domain"),
            new SetupStep("prove", "Prove the domain (MX, SPF, DKIM, DMARC)", proved, proved ? check!.At : null, "/org/domains", "Check the records"),
            new SetupStep("people", "Invite your first people", invited.Count > 0, invited.Count > 0 ? invited.Min(p => p.Mailbox.CreatedAt) : null, "/org/people", "Invite people"),
            new SetupStep("shared", "Shared mailboxes", shared.Count > 0, null, "/org/shared", "Add a shared mailbox"),
            new SetupStep("apps", "Applications that send mail", apps.Count > 0, apps.Count > 0 && apps.SelectMany(a => a.Keys).Any() ? apps.SelectMany(a => a.Keys).Min(k => k.Created) : null, "/org/apps", "Add an application"),
        });
        return steps;
    }
}
