using System.Globalization;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>The last storage warning mailed: 0, 80, 90 or 100 (DES-11 D2).</summary>
public sealed class StorageWarned
{
    /// <summary>The level, in percent.</summary>
    public int Level { get; set; }
}

/// <summary>
/// DES-11 D2 (owner, 9-10 Oct 2026): each organisation's storage plan, chosen by the operator; the
/// smaller limits its administrator may give some people; and the warnings at 80, 90 and 100%.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The mailbox document that remembers the last storage warning mailed.</summary>
    public const string StorageWarnedKind = "storage-warned";

    /// <summary>The organisation document that remembers the last shared-storage warning mailed.</summary>
    public const string SharedWarnedKind = "storage-shared-warned";

    private static readonly int[] WarnAt = { 80, 90, 100 };

    /// <summary>An organisation's storage plan.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; "none" when not chosen.</returns>
    public Task<StoragePlan> StoragePlanOfAsync(Guid tenantId, CancellationToken ct = default) => StoragePlan.ReadAsync(this.store, tenantId, ct);

    /// <summary>
    /// Save an organisation's plan (the operator) and apply it at once to every mailbox: raising it
    /// gives everyone more now; lowering it leaves anyone above the new size "over" - they keep their
    /// mail and receive nothing until they are back under. The administrator's smaller limits are kept.
    /// Returns the error, or null.
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="plan">"none", "person", "shared" or "mixed".</param>
    /// <param name="personBytes">Each mailbox's size (person and mixed).</param>
    /// <param name="sharedBytes">The shared total or reserve (shared and mixed).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SaveStoragePlanAsync(Guid tenantId, string plan, long personBytes, long sharedBytes, CancellationToken ct = default)
    {
        StoragePlan current = await StoragePlan.ReadAsync(this.store, tenantId, ct).ConfigureAwait(false);
        var next = new StoragePlan
        {
            Plan = plan,
            PersonBytes = plan is "person" or "mixed" ? personBytes : 0,
            SharedBytes = plan is "shared" or "mixed" ? sharedBytes : 0,
            Caps = current.Caps,
        };
        if (next.Problem() is string problem)
        {
            return problem;
        }
        // A smaller limit that is no longer smaller than the plan has no meaning: it is dropped.
        long ceiling = next.Plan switch { "person" or "mixed" => next.PersonBytes + (next.Plan == "mixed" ? next.SharedBytes : 0), "shared" => next.SharedBytes, _ => MailboxRow.DefaultQuotaBytes };
        foreach (Guid id in next.Caps.Where(kv => kv.Value <= 0 || kv.Value >= ceiling).Select(kv => kv.Key).ToList())
        {
            next.Caps.Remove(id);
        }
        await this.store.SetTenantDocumentAsync(tenantId, StoragePlan.Kind, System.Text.Json.JsonSerializer.Serialize(next), ct).ConfigureAwait(false);
        await this.ApplyStoragePlanAsync(tenantId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Set every mailbox's own limit from its organisation's plan (after a plan, a limit or a new mailbox).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task ApplyStoragePlanAsync(Guid tenantId, CancellationToken ct = default)
    {
        StoragePlan plan = await StoragePlan.ReadAsync(this.store, tenantId, ct).ConfigureAwait(false);
        foreach (MailboxRow m in await this.store.ListMailboxesAsync(tenantId, ct).ConfigureAwait(false))
        {
            long own = plan.OwnLimit(m.Id);
            if (m.QuotaBytes != own)
            {
                m.QuotaBytes = own;
                await this.store.UpsertMailboxAsync(m, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The administrator gives a person a smaller limit, or takes it away (0). It may only be smaller
    /// than the plan gives - an organisation may only make things stricter. Returns the error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="capBytes">The limit; 0 to take it away.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SetStorageCapAsync(TenantRow tenant, Guid personId, long capBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        StoragePlan plan = await StoragePlan.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
        long ceiling = plan.Plan switch { "person" => plan.PersonBytes, "mixed" => plan.PersonBytes + plan.SharedBytes, "shared" => plan.SharedBytes, _ => MailboxRow.DefaultQuotaBytes };
        if (capBytes < 0 || (capBytes > 0 && capBytes < 1024L * 1024 * 10))
        {
            return "A limit is at least 10 MB.";
        }
        if (capBytes >= ceiling)
        {
            return "A limit can only be smaller than what the plan gives: " + Sizes.Text(ceiling) + ".";
        }
        if (capBytes == 0)
        {
            plan.Caps.Remove(personId);
        }
        else
        {
            plan.Caps[personId] = capBytes;
        }
        await this.store.SetTenantDocumentAsync(tenant.Id, StoragePlan.Kind, System.Text.Json.JsonSerializer.Serialize(plan), ct).ConfigureAwait(false);
        await this.ApplyStoragePlanAsync(tenant.Id, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The plan in words for the Anjal console and the organisation's dashboard, for example
    /// "2.00 GB each" or "50.00 GB shared".
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The words and their values.</returns>
    public static (string Text, IReadOnlyDictionary<string, string> Args) PlanWords(StoragePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["each"] = Sizes.Text(plan.PersonBytes), ["shared"] = Sizes.Text(plan.SharedBytes), ["default"] = Sizes.Text(MailboxRow.DefaultQuotaBytes) };
        return plan.Plan switch
        {
            "person" => ("{each} each", args),
            "shared" => ("{shared} shared", args),
            "mixed" => ("{each} each, and {shared} shared reserve", args),
            _ => ("Anjal's default: {default} each", args),
        };
    }

    /// <summary>
    /// The storage warnings (DES-11 D2): at 80% and 90% a mail to the person; at 100% to the person
    /// and to the organisation's administrators; for a shared total or reserve, to the administrators.
    /// Each is sent once as it is crossed, and again only after the use has fallen back below it.
    /// Run by the background sender every hour. Returns how many mails went.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of mails sent.</returns>
    public async Task<int> StorageWarningsAsync(CancellationToken ct = default)
    {
        int sent = 0;
        foreach (TenantRow tenant in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            StoragePlan plan = await StoragePlan.ReadAsync(this.store, tenant.Id, ct).ConfigureAwait(false);
            IReadOnlyList<MailboxRow> people = await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false);
            IReadOnlyList<Guid> adminIds = await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false);
            List<MailboxRow> admins = people.Where(p => adminIds.Contains(p.Id) && p.Enabled).ToList();
            foreach (MailboxRow m in people.Where(p => p.Enabled && p.QuotaBytes > 0))
            {
                int p = Sizes.Percent(m.UsedBytes, m.QuotaBytes);
                int level = WarnAt.LastOrDefault(w => p >= w);
                int warned = (await this.ReadDocumentAsync<StorageWarned>(m.Id, StorageWarnedKind, ct).ConfigureAwait(false))?.Level ?? 0;
                if (level > warned)
                {
                    string text = Blanks(level >= 100
                        ? "Your mailbox {address} is full ({used} of {quota}). Nothing more arrives - senders are asked to try again later - and you cannot send until you delete or archive some mail."
                        : "Your mailbox {address} is {p}% full ({used} of {quota}). Delete or archive some mail before it fills: a full mailbox receives nothing and cannot send.", m, level);
                    await this.SendStorageMailAsync(m, m.Address, level >= 100 ? "Your mailbox is full" : $"Your mailbox is {level}% full", text, ct).ConfigureAwait(false);
                    sent++;
                    if (level >= 100)
                    {
                        foreach (MailboxRow admin in admins.Where(a => a.Id != m.Id))
                        {
                            await this.SendStorageMailAsync(m, admin.Address, $"The mailbox {m.Address} is full", Blanks("The mailbox {address} is full ({used} of {quota}). It receives nothing and cannot send until mail is deleted or archived, or it is given more space.", m, level), ct).ConfigureAwait(false);
                            sent++;
                        }
                    }
                }
                if (level != warned)
                {
                    await this.WriteDocumentAsync(m.Id, StorageWarnedKind, new StorageWarned { Level = level }, ct).ConfigureAwait(false);
                }
            }
            if (plan.HasShared && people.Count > 0 && admins.Count > 0)
            {
                StorageState state = await StoragePlan.StateOfAsync(this.store, people[0], ct).ConfigureAwait(false);
                int p = Sizes.Percent(state.SharedUsed, state.Shared);
                int level = WarnAt.LastOrDefault(w => p >= w);
                int warned = (await this.ReadTenantDocumentAsync<StorageWarned>(tenant.Id, SharedWarnedKind, ct).ConfigureAwait(false))?.Level ?? 0;
                if (level > warned)
                {
                    string what = plan.Plan == "shared" ? "shared storage" : "shared reserve";
                    foreach (MailboxRow admin in admins)
                    {
                        await this.SendStorageMailAsync(admin, admin.Address, level >= 100 ? $"Your organisation's {what} is full" : $"Your organisation's {what} is {level}% full",
                            $"The {what} of {tenant.DisplayName} is {(level >= 100 ? "full" : level.ToString(CultureInfo.InvariantCulture) + "% used")}: {Sizes.Text(state.SharedUsed)} of {Sizes.Text(state.Shared)}. {(level >= 100 ? "Mailboxes that draw on it receive nothing and cannot send until mail is deleted or archived, or more space is arranged with Anjal." : "Ask people to delete or archive mail, or arrange more space with Anjal.")}", ct).ConfigureAwait(false);
                        sent++;
                    }
                }
                if (level != warned)
                {
                    await this.WriteTenantDocumentAsync(tenant.Id, SharedWarnedKind, new StorageWarned { Level = level }, ct).ConfigureAwait(false);
                }
            }
        }
        return sent;
    }

    private static string Blanks(string text, MailboxRow m, int level) => text
        .Replace("{address}", m.Address, StringComparison.Ordinal)
        .Replace("{used}", Sizes.Text(m.UsedBytes), StringComparison.Ordinal)
        .Replace("{quota}", Sizes.Text(m.QuotaBytes), StringComparison.Ordinal)
        .Replace("{p}", level.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    // Storage warnings matter as much as a sign-in alert: they reach even a full mailbox.
    private Task<string?> SendStorageMailAsync(MailboxRow about, string to, string subject, string text, CancellationToken ct) =>
        this.SendSecurityMailAsync(about, to, subject, text, ct);
}
