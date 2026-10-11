namespace Anjal.Store;

/// <summary>
/// An organisation's storage plan (DES-11 D2, owner 10 Oct 2026), chosen by the operator in the
/// Anjal console:
/// <list type="bullet">
/// <item><c>person</c>: every mailbox gets <see cref="PersonBytes"/>;</item>
/// <item><c>shared</c>: one total, <see cref="SharedBytes"/>, that all its people draw from;</item>
/// <item><c>mixed</c>: every mailbox gets <see cref="PersonBytes"/>, and when that is full it draws
/// from a shared reserve of <see cref="SharedBytes"/>;</item>
/// <item><c>none</c>: Anjal's default, 1 GB a mailbox.</item>
/// </list>
/// The organisation's administrator may give some people a smaller limit (<see cref="Caps"/>),
/// never a larger one. A mailbox's own limit is kept in <see cref="MailboxRow.QuotaBytes"/> (0 when
/// it has none of its own, under a shared plan without a smaller limit); the shared total and the
/// reserve are checked on top. The limit is enforced: a full mailbox receives nothing (outside
/// senders are told "452 mailbox full" and try again later) and cannot send, except Anjal's own
/// security mail, which always arrives.
/// </summary>
public sealed class StoragePlan
{
    /// <summary>The organisation document that keeps the plan.</summary>
    public const string Kind = "storage-plan";

    /// <summary>The plans.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> Plans = new[] { "none", "person", "shared", "mixed" };

    /// <summary>"none", "person", "shared" or "mixed".</summary>
    public string Plan { get; set; } = "none";

    /// <summary>Each mailbox's own size (person and mixed plans).</summary>
    public long PersonBytes { get; set; }

    /// <summary>The shared total (shared plan) or the shared reserve (mixed plan).</summary>
    public long SharedBytes { get; set; }

    /// <summary>The administrator's smaller limits, by mailbox.</summary>
    public System.Collections.Generic.Dictionary<System.Guid, long> Caps { get; set; } = new();

    /// <summary>True when the plan has a shared total or reserve.</summary>
    public bool HasShared => this.Plan is "shared" or "mixed";

    /// <summary>What is wrong with the plan, or null when it can be saved.</summary>
    /// <returns>The problem in words.</returns>
    public string? Problem()
    {
        if (!Plans.Contains(this.Plan))
        {
            return "Choose a storage plan.";
        }
        if (this.Plan is "person" or "mixed" && this.PersonBytes < 1024L * 1024 * 10)
        {
            return "Give each mailbox at least 10 MB.";
        }
        if (this.HasShared && this.SharedBytes < 1024L * 1024 * 10)
        {
            return this.Plan == "shared" ? "Give the shared total at least 10 MB." : "Give the shared reserve at least 10 MB.";
        }
        return null;
    }

    /// <summary>A mailbox's own limit under this plan, the administrator's smaller limit taken in; 0 for none of its own.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <returns>Bytes.</returns>
    public long OwnLimit(System.Guid mailboxId)
    {
        long size = this.Plan switch
        {
            "person" or "mixed" => this.PersonBytes,
            "shared" => 0,
            _ => MailboxRow.DefaultQuotaBytes,
        };
        if (this.Caps.TryGetValue(mailboxId, out long cap) && cap > 0)
        {
            size = size == 0 ? cap : System.Math.Min(size, cap);
        }
        return size;
    }

    /// <summary>An organisation's plan; <c>none</c> when it has not been chosen.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public static async System.Threading.Tasks.Task<StoragePlan> ReadAsync(IMailboxStore store, System.Guid tenantId, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        string? json = await store.GetTenantDocumentAsync(tenantId, Kind, ct).ConfigureAwait(false);
        try
        {
            return json is null ? new StoragePlan() : System.Text.Json.JsonSerializer.Deserialize<StoragePlan>(json) ?? new StoragePlan();
        }
        catch (System.Text.Json.JsonException)
        {
            return new StoragePlan();
        }
    }

    /// <summary>
    /// Where a mailbox stands: its own use and limit, the shared total or reserve and its use, and
    /// whether it is full - that is, whether it may receive or send.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    public static async System.Threading.Tasks.Task<StorageState> StateOfAsync(IMailboxStore store, MailboxRow mailbox, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(mailbox);
        StoragePlan plan = await ReadAsync(store, mailbox.TenantId, ct).ConfigureAwait(false);
        long own = mailbox.QuotaBytes;
        bool ownFull = own > 0 && mailbox.UsedBytes >= own;
        if (!plan.HasShared)
        {
            return new StorageState(plan.Plan, mailbox.UsedBytes, own, 0, 0, ownFull);
        }
        long sharedUsed = 0;
        foreach (MailboxRow m in await store.ListMailboxesAsync(mailbox.TenantId, ct).ConfigureAwait(false))
        {
            sharedUsed += plan.Plan == "shared" ? m.UsedBytes : System.Math.Max(0, m.UsedBytes - m.QuotaBytes);
        }
        bool sharedFull = sharedUsed >= plan.SharedBytes;
        bool full = plan.Plan == "shared"
            ? ownFull || sharedFull
            : (ownFull && sharedFull) || (plan.Caps.TryGetValue(mailbox.Id, out long cap) && cap > 0 && mailbox.UsedBytes >= cap);
        return new StorageState(plan.Plan, mailbox.UsedBytes, own, plan.SharedBytes, sharedUsed, full);
    }
}

/// <summary>Where a mailbox stands against its organisation's storage plan (DES-11 D2).</summary>
/// <param name="Plan">"none", "person", "shared" or "mixed".</param>
/// <param name="Used">The mailbox's own use.</param>
/// <param name="OwnLimit">Its own limit; 0 for none of its own.</param>
/// <param name="Shared">The shared total or reserve; 0 when the plan has none.</param>
/// <param name="SharedUsed">How much of it is used.</param>
/// <param name="Full">True when the mailbox may neither receive nor send.</param>
public sealed record StorageState(string Plan, long Used, long OwnLimit, long Shared, long SharedUsed, bool Full)
{
    /// <summary>
    /// The figure the warnings go by, in percent: the mailbox's own use; under a shared plan the
    /// shared total's; under a mixed plan the reserve's once the mailbox's own space is used.
    /// </summary>
    public int Percent
    {
        get
        {
            if (this.Plan == "shared" && this.OwnLimit == 0)
            {
                return Pct(this.SharedUsed, this.Shared);
            }
            if (this.Plan == "mixed" && this.OwnLimit > 0 && this.Used >= this.OwnLimit)
            {
                return Pct(this.SharedUsed, this.Shared);
            }
            return Pct(this.Used, this.OwnLimit);
        }
    }

    // Rounded to the nearest, never 100 before full nor 0 when something is used (the 9 Oct rule).
    private static int Pct(long part, long whole)
    {
        if (whole <= 0 || part <= 0)
        {
            return 0;
        }
        if (part >= whole)
        {
            return 100;
        }
        return (int)System.Math.Clamp(System.Math.Round(part * 100d / whole, System.MidpointRounding.AwayFromZero), 1, 99);
    }
}
