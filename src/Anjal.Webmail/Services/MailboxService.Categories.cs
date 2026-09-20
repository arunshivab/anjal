using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A category as the webmail shows it, with where it came from.</summary>
public sealed class CategoryView
{
    /// <summary>The stored row.</summary>
    public CategoryRow Row { get; init; } = new();

    /// <summary>How many messages in this mailbox carry it.</summary>
    public long Count { get; init; }

    /// <summary>True when it is a tenant default, which a mailbox may not edit.</summary>
    public bool Shared => this.Row.IsShared;
}

/// <summary>
/// Categories and the dashboard. Categories exist at two levels: the
/// tenant's defaults, shared by every mailbox and the only ones an
/// institution can report across, and a mailbox's own, private to it.
/// <para>
/// Colour comes from one of eight slots, and the eight are a per-mailbox
/// budget: the tenant's defaults take slots in order, a mailbox's own take
/// what remains, and anything past the eighth is stored with
/// <see cref="CategoryRow.NoSlot"/> and told apart by name. Slots are never
/// recomputed when a category is deleted, because a category that changes
/// colour repaints every chart it has ever appeared in.
/// </para>
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>
    /// The categories a new tenant starts with. Six, not eight, so every
    /// mailbox keeps two coloured slots for its own.
    /// </summary>
    public static readonly IReadOnlyList<string> HospitalDefaults = new[]
    {
        "Clinical", "Referrals", "Diagnostics", "Billing", "Vendors", "Circulars",
    };

    /// <summary>The label shown for messages carrying no category.</summary>
    public const string Uncategorised = "Uncategorised";

    /// <summary>
    /// Create the tenant's default categories if it has none. Safe to call
    /// on every sign-in: it does nothing once they exist, and never
    /// re-creates one the tenant deleted, because the check is on the count
    /// of shared categories rather than on each name.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<CategoryRow>> EnsureTenantDefaultsAsync(Guid tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<CategoryRow> existing = await this.store.ListCategoriesAsync(tenantId, null, ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return existing;
        }
        int slot = 1;
        foreach (string name in HospitalDefaults)
        {
            await this.store.UpsertCategoryAsync(new CategoryRow
            {
                TenantId = tenantId,
                MailboxId = null,
                Name = name,
                Slot = slot++,
            }, ct).ConfigureAwait(false);
        }
        return await this.store.ListCategoriesAsync(tenantId, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every category this mailbox can use - the tenant's defaults then its
    /// own - with how many of its messages carry each.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<CategoryView>> ListCategoriesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<CategoryView>();
        }
        await this.EnsureTenantDefaultsAsync(context.Value.Tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyList<CategoryRow> rows = await this.store.ListCategoriesAsync(context.Value.Tenant.Id, mailboxId, ct).ConfigureAwait(false);

        // One activity read covers every count; a query per category would be
        // a query per row on a page that lists them all.
        MailboxActivity activity = await this.store.GetActivityAsync(mailboxId, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), ct).ConfigureAwait(false);
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedCount c in activity.ByCategory)
        {
            counts[c.Name] = c.Count;
        }

        var result = new List<CategoryView>();
        foreach (CategoryRow row in rows)
        {
            counts.TryGetValue(row.Name, out long n);
            result.Add(new CategoryView { Row = row, Count = n });
        }
        return result;
    }

    /// <summary>
    /// Add a category owned by this mailbox. Returns the error to show, or
    /// null. The next free colour slot is taken; when all eight are in use
    /// the category is still created, without a colour.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="name">The new name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> AddCategoryAsync(Guid mailboxId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "A category needs a name.";
        }
        if (trimmed.Length > 40)
        {
            return "A category name can be at most 40 characters.";
        }
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return "Mailbox is not available.";
        }

        IReadOnlyList<CategoryRow> existing = await this.store.ListCategoriesAsync(context.Value.Tenant.Id, mailboxId, ct).ConfigureAwait(false);
        foreach (CategoryRow row in existing)
        {
            if (string.Equals(row.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return row.IsShared
                    ? $"\u201c{row.Name}\u201d is already one of your organisation's categories."
                    : $"You already have a category called \u201c{row.Name}\u201d.";
            }
        }

        await this.store.UpsertCategoryAsync(new CategoryRow
        {
            TenantId = context.Value.Tenant.Id,
            MailboxId = mailboxId,
            Name = trimmed,
            Slot = NextFreeSlot(existing),
        }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The lowest colour slot not already taken, or <see cref="CategoryRow.NoSlot"/>
    /// when all eight are in use.
    /// </summary>
    /// <param name="existing">Categories already visible to the mailbox.</param>
    public static int NextFreeSlot(IReadOnlyList<CategoryRow> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var taken = new HashSet<int>();
        foreach (CategoryRow row in existing)
        {
            taken.Add(row.Slot);
        }
        for (int slot = 1; slot <= CategoryRow.MaxSlot; slot++)
        {
            if (!taken.Contains(slot))
            {
                return slot;
            }
        }
        return CategoryRow.NoSlot;
    }

    /// <summary>
    /// Rename a category this mailbox owns. A tenant default is refused:
    /// renaming it for everyone is the administrator's decision.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="categoryId">The category.</param>
    /// <param name="name">The new name.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> RenameCategoryAsync(Guid mailboxId, Guid categoryId, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        CategoryRow? row = await this.store.GetCategoryAsync(categoryId, ct).ConfigureAwait(false);
        if (row is null || row.MailboxId != mailboxId)
        {
            return row is not null && row.IsShared
                ? "That category belongs to your organisation, so only an administrator can rename it."
                : "That category is not available.";
        }
        string trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40)
        {
            return "A category name must be between 1 and 40 characters.";
        }
        row.Name = trimmed;
        await this.store.UpsertCategoryAsync(row, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Delete a category this mailbox owns. Messages carrying it keep their
    /// place and simply become uncategorised; the slot is not reused by
    /// renumbering, only by the next category that needs one.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="categoryId">The category.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> DeleteCategoryAsync(Guid mailboxId, Guid categoryId, CancellationToken ct = default)
    {
        CategoryRow? row = await this.store.GetCategoryAsync(categoryId, ct).ConfigureAwait(false);
        if (row is null || row.MailboxId != mailboxId)
        {
            return row is not null && row.IsShared
                ? "That category belongs to your organisation, so only an administrator can remove it."
                : "That category is not available.";
        }
        await this.store.DeleteCategoryAsync(categoryId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Put a category on a message, or clear it with null. Optionally
    /// records a rule so future mail from the same sender is filed the same
    /// way. Returns false when the message or category is not this
    /// mailbox's.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="categoryId">The category, or null to clear.</param>
    /// <param name="alsoFutureMail">Whether to remember the sender.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> CategoriseAsync(Guid mailboxId, Guid messageId, Guid? categoryId, bool alsoFutureMail = false, CancellationToken ct = default)
    {
        MessageRow? row = await this.GetOwnedRowAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        if (categoryId is Guid id && !await this.CanUseCategoryAsync(mailboxId, id, ct).ConfigureAwait(false))
        {
            return false;
        }

        await this.store.SetMessageCategoryAsync(mailboxId, messageId, categoryId, ct).ConfigureAwait(false);

        if (alsoFutureMail && categoryId is Guid categoryForRule)
        {
            string sender = SenderAddressOf(row);
            if (sender.Length > 0)
            {
                await this.store.UpsertCategoryRuleAsync(new CategoryRuleRow
                {
                    MailboxId = mailboxId,
                    Pattern = sender,
                    CategoryId = categoryForRule,
                }, ct).ConfigureAwait(false);
            }
        }
        return true;
    }

    /// <summary>Whether a category is one this mailbox may use.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="categoryId">The category.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> CanUseCategoryAsync(Guid mailboxId, Guid categoryId, CancellationToken ct = default)
    {
        CategoryRow? category = await this.store.GetCategoryAsync(categoryId, ct).ConfigureAwait(false);
        if (category is null)
        {
            return false;
        }
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null || category.TenantId != context.Value.Tenant.Id)
        {
            return false;
        }
        return category.IsShared || category.MailboxId == mailboxId;
    }

    /// <summary>The bare address a message came from, for a sender rule.</summary>
    /// <param name="row">The message.</param>
    public static string SenderAddressOf(MessageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        IReadOnlyList<Anjal.Mime.MailAddress> parsed =
            Anjal.Mime.AddressParser.Parse(Anjal.Mime.EncodedWordDecoder.Decode(row.FromHeader));
        if (parsed.Count > 0 && parsed[0].Address.Length > 0)
        {
            return parsed[0].Address.ToLowerInvariant();
        }
        return row.EnvelopeFrom.ToLowerInvariant();
    }

    /// <summary>The mailbox's sender-to-category rules, newest patterns first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<IReadOnlyList<CategoryRuleRow>> ListCategoryRulesAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.ListCategoryRulesAsync(mailboxId, ct);

    /// <summary>Remove a sender-to-category rule this mailbox owns.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DeleteCategoryRuleAsync(Guid mailboxId, Guid ruleId, CancellationToken ct = default)
    {
        foreach (CategoryRuleRow rule in await this.store.ListCategoryRulesAsync(mailboxId, ct).ConfigureAwait(false))
        {
            if (rule.Id == ruleId)
            {
                return await this.store.DeleteCategoryRuleAsync(ruleId, ct).ConfigureAwait(false);
            }
        }
        return false;
    }

    // ---------------- Dashboard ----------------

    /// <summary>Periods the dashboard offers.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, int Days)> Periods = new[]
    {
        ("7d", "Last 7 days", 7),
        ("30d", "Last 30 days", 30),
        ("90d", "Last 90 days", 90),
        ("365d", "Last 12 months", 365),
    };

    /// <summary>
    /// What this mailbox did over a period. The period runs to the end of
    /// today so the current day is included whole rather than cut off at
    /// the moment the page loads.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="periodKey">One of <see cref="Periods"/>; unknown values fall back to 30 days.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<MailboxActivity> GetActivityAsync(Guid mailboxId, string periodKey, CancellationToken ct = default)
    {
        int days = 30;
        foreach ((string Key, string Label, int Days) period in Periods)
        {
            if (string.Equals(period.Key, periodKey, StringComparison.OrdinalIgnoreCase))
            {
                days = period.Days;
            }
        }
        DateTimeOffset end = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(1);
        DateTimeOffset start = end.AddDays(-days);
        MailboxActivity activity = await this.store.GetActivityAsync(mailboxId, start, end, ct).ConfigureAwait(false);
        activity.ByDay = FillGaps(activity.ByDay, start, end);
        return activity;
    }

    /// <summary>
    /// Put a zero on every day the period covers that has no messages, so
    /// the line chart shows a quiet week as a flat line rather than closing
    /// the gap and implying activity that never happened.
    /// </summary>
    /// <param name="counts">Days that have messages.</param>
    /// <param name="start">Period start, inclusive.</param>
    /// <param name="end">Period end, exclusive.</param>
    public static IReadOnlyList<DailyCount> FillGaps(IReadOnlyList<DailyCount> counts, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var have = new Dictionary<DateTimeOffset, DailyCount>();
        foreach (DailyCount c in counts)
        {
            have[new DateTimeOffset(c.Day.UtcDateTime.Date, TimeSpan.Zero)] = c;
        }
        var filled = new List<DailyCount>();
        for (DateTimeOffset day = new(start.UtcDateTime.Date, TimeSpan.Zero); day < end; day = day.AddDays(1))
        {
            filled.Add(have.TryGetValue(day, out DailyCount? found)
                ? found
                : new DailyCount { Day = day, Received = 0, Sent = 0 });
        }
        return filled;
    }
}
