using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A person's Mail settings that live outside the mailbox row (rc.13, board SetMail).</summary>
public sealed class MailSettings
{
    /// <summary>Seconds Send waits so it can be undone; 0 sends at once.</summary>
    public int UndoSeconds { get; set; } = 10;

    /// <summary>Days a message stays in Trash before it is deleted for good - the person's own choice, used only when <see cref="TrashChosen"/>.</summary>
    public int TrashDays { get; set; } = MailboxService.DefaultTrashDays;

    /// <summary>
    /// True once the person has chosen their own Trash days (rc.15, item 22). Until then they
    /// follow their organisation's setting, including any later change to it.
    /// </summary>
    public bool TrashChosen { get; set; }

    /// <summary>Keep mail on this person's own devices to read offline (rc.15, item 65b); off unless they turn it on.</summary>
    public bool OfflineMail { get; set; }

    /// <summary>The person's own clock, "24" or "12"; empty to follow their organisation (owner, 8 Oct 2026).</summary>
    public string Clock { get; set; } = string.Empty;

    /// <summary>
    /// True once the person has chosen their own colour (DES-11 D7). Until then a change to the
    /// organisation's default colour may reach them, as its administrator decides.
    /// </summary>
    public bool ThemeChosen { get; set; }

    /// <summary>
    /// True once the person has chosen their own language (owner, 10 Oct 2026: D-131 covers the
    /// language too). Until then a change to the organisation's default language may reach them.
    /// </summary>
    public bool LanguageChosen { get; set; }

    /// <summary>
    /// Days in Junk kept for this person when the organisation changed its setting for people
    /// joining from then on only (DES-11 D7); 0 to follow the organisation.
    /// </summary>
    public int JunkKept { get; set; }

    /// <summary>"Your habits" on the person's dashboard (rc.15, item 58): on unless they turn it off; seen only by them.</summary>
    public bool Habits { get; set; } = true;

    /// <summary>The order the person chose for each folder, and for search (owner, 9 Oct 2026); a folder not here is newest first.</summary>
    public Dictionary<string, string> Sorts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Mail settings (rc.13): how long Send waits for Undo, and emptying Trash
/// and Junk. A message is deleted only once it has been seen in Trash (or
/// Junk) for the whole period: each is noted with the moment it was first
/// found there, so nothing put in Trash today can go before its time,
/// whatever its date.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the document holding the Mail settings.</summary>
    public const string MailSettingsKind = "mail-settings";

    /// <summary>The kind of the document noting when messages were first found in Trash and Junk.</summary>
    public const string FolderClockKind = "folder-clock";

    /// <summary>Days in Trash, unless the person chooses otherwise.</summary>
    public const int DefaultTrashDays = 30;

    /// <summary>The fewest days a person may choose for Trash.</summary>
    public const int MinTrashDays = 7;

    /// <summary>The most days a person may choose for Trash.</summary>
    public const int MaxTrashDays = 90;

    /// <summary>Days in Junk, the same for everyone (until the organisation sets its own).</summary>
    public const int JunkDays = 90;

    /// <summary>The choices for Undo send, in seconds.</summary>
    public static readonly IReadOnlyList<int> UndoChoices = new[] { 0, 5, 10, 20, 30 };

    /// <summary>The choices for emptying Trash, in days.</summary>
    public static readonly IReadOnlyList<int> TrashChoices = new[] { 7, 14, 30, 60, 90 };

    /// <summary>Set the person's own clock, or follow the organisation (owner, 8 Oct 2026).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="clock">"24", "12", or empty to follow the organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The choice kept.</returns>
    public async Task<string> SetClockAsync(Guid mailboxId, string? clock, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        s.Clock = Clocks.Choice(clock);
        await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
        return s.Clock;
    }

    /// <summary>Turn "Your habits" on or off (rc.15, item 58).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetHabitsAsync(Guid mailboxId, bool on, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        s.Habits = on;
        await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
    }

    /// <summary>Note every organisation's clock (at start-up), so each page can use it at once.</summary>
    /// <param name="ct">Cancellation.</param>
    public async Task LoadClocksAsync(CancellationToken ct = default)
    {
        foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
        {
            Branding? b = await this.ReadTenantDocumentAsync<Branding>(t.Id, BrandingKind, ct).ConfigureAwait(false);
            Clocks.SetDefault(t.Slug, b?.DefaultClock);
        }
    }

    /// <summary>The person's Mail settings.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The settings, checked.</returns>
    public async Task<MailSettings> GetMailSettingsAsync(Guid mailboxId, CancellationToken ct = default)
    {
        MailSettings s = await this.ReadDocumentAsync<MailSettings>(mailboxId, MailSettingsKind, ct).ConfigureAwait(false) ?? new MailSettings();
        s.UndoSeconds = UndoChoices.Contains(s.UndoSeconds) ? s.UndoSeconds : 10;
        s.TrashDays = Math.Clamp(s.TrashDays, MinTrashDays, MaxTrashDays);
        return s;
    }

    /// <summary>
    /// How long a person's Trash is kept (rc.15, item 22; owner, 7 Oct 2026): someone who never
    /// chose follows the organisation's setting, including later changes; someone who chose keeps
    /// their choice within the range the organisation allows (7 days up to its longest, which is
    /// never over Anjal's 90). An organisation can only make the rules stricter.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rule.</returns>
    public async Task<TrashRule> TrashRuleForAsync(Guid mailboxId, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        RetentionPolicy org = (await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false)) is MailboxRow owner
            ? await this.ReadTenantDocumentAsync<RetentionPolicy>(owner.TenantId, RetentionKind, ct).ConfigureAwait(false) ?? new RetentionPolicy()
            : new RetentionPolicy();
        int orgDays = Math.Clamp(org.TrashDays, MinTrashDays, MaxTrashDays);
        int longest = Math.Clamp(org.TrashMaxDays, orgDays, MaxTrashDays);
        int junk = Math.Clamp(s.JunkKept > 0 ? s.JunkKept : org.JunkDays, MinTrashDays, JunkDays);
        int days = s.TrashChosen ? Math.Clamp(s.TrashDays, MinTrashDays, longest) : orgDays;
        return new TrashRule(days, s.TrashChosen, orgDays, longest, junk);
    }

    /// <summary>Save the Mail settings; values outside the choices are refused. Returns the error, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="undoSeconds">Seconds for Undo send, or null to keep.</param>
    /// <param name="trashDays">Days for Trash; 0 to follow the organisation again; null to keep.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SetMailSettingsAsync(Guid mailboxId, int? undoSeconds, int? trashDays, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(mailboxId, ct).ConfigureAwait(false);
        if (undoSeconds is int u)
        {
            if (!UndoChoices.Contains(u))
            {
                return "Choose one of the Undo send times listed.";
            }
            s.UndoSeconds = u;
        }
        if (trashDays == 0)
        {
            s.TrashChosen = false;
        }
        else if (trashDays is int d)
        {
            int longest = (await this.TrashRuleForAsync(mailboxId, ct).ConfigureAwait(false)).Longest;
            if (d < MinTrashDays || d > longest)
            {
                return $"Your organisation allows {MinTrashDays} to {longest} days.";
            }
            s.TrashDays = d;
            s.TrashChosen = true;
        }
        await this.WriteDocumentAsync(mailboxId, MailSettingsKind, s, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Whether offline mail is on for a person on this computer: their choice, where the organisation allows it, never on a shared computer.</summary>
    /// <param name="personId">The person.</param>
    /// <param name="shared">True on a shared computer.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>(allowed here, turned on).</returns>
    public async Task<(bool Allowed, bool On)> OfflineMailAsync(Guid personId, bool shared, CancellationToken ct = default)
    {
        bool allowed = !shared && (await this.SignInPolicyForAsync(personId, ct).ConfigureAwait(false)).OfflineMail;
        return (allowed, allowed && (await this.GetMailSettingsAsync(personId, ct).ConfigureAwait(false)).OfflineMail);
    }

    /// <summary>Turn offline mail on or off for a person (their own devices only).</summary>
    /// <param name="personId">The person.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetOfflineMailAsync(Guid personId, bool on, CancellationToken ct = default)
    {
        MailSettings s = await this.GetMailSettingsAsync(personId, ct).ConfigureAwait(false);
        s.OfflineMail = on;
        await this.WriteDocumentAsync(personId, MailSettingsKind, s, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Empty Trash and Junk of every mailbox of messages that have been there
    /// longer than allowed. Returns how many were deleted.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many messages were deleted.</returns>
    public async Task<int> EmptyOldAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        int deleted = 0;
        foreach (MailboxRow mailbox in await this.store.ListMailboxesAsync(null, ct).ConfigureAwait(false))
        {
            if (mailbox.Enabled)
            {
                deleted += await this.EmptyOldAsync(mailbox.Id, now, ct).ConfigureAwait(false);
            }
        }
        return deleted;
    }

    /// <summary>Empty one mailbox's Trash and Junk of messages there longer than allowed.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many messages were deleted.</returns>
    public async Task<int> EmptyOldAsync(Guid mailboxId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (await this.IsHeldAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return 0;
        }
        TrashRule rule = await this.TrashRuleForAsync(mailboxId, ct).ConfigureAwait(false);
        Dictionary<string, Dictionary<Guid, DateTimeOffset>> clock =
            await this.ReadDocumentAsync<Dictionary<string, Dictionary<Guid, DateTimeOffset>>>(mailboxId, FolderClockKind, ct).ConfigureAwait(false)
            ?? new Dictionary<string, Dictionary<Guid, DateTimeOffset>>(StringComparer.Ordinal);
        int deleted = 0;
        foreach ((string folderName, int days) in new[] { ("Trash", rule.Days), (Anjal.Mailbox.MailboxSink.JunkFolder, rule.JunkDays) })
        {
            FolderRow? folder = await this.GetFolderAsync(mailboxId, folderName, ct).ConfigureAwait(false);
            if (folder is null)
            {
                continue;
            }
            Dictionary<Guid, DateTimeOffset> seen = clock.TryGetValue(folderName, out Dictionary<Guid, DateTimeOffset>? s) ? s : new Dictionary<Guid, DateTimeOffset>();
            var present = new Dictionary<Guid, DateTimeOffset>();
            for (int offset = 0; ; offset += 500)
            {
                IReadOnlyList<MessageRow> batch = await this.store.ListMessagesAsync(mailboxId, folder.Id, 500, offset, ct).ConfigureAwait(false);
                foreach (MessageRow m in batch)
                {
                    present[m.Id] = seen.TryGetValue(m.Id, out DateTimeOffset first) ? first : now;
                }
                if (batch.Count < 500)
                {
                    break;
                }
            }
            foreach ((Guid id, DateTimeOffset first) in present.ToList())
            {
                if (now - first >= TimeSpan.FromDays(days) && await this.DeleteAsync(mailboxId, id, ct).ConfigureAwait(false))
                {
                    present.Remove(id);
                    deleted++;
                }
            }
            clock[folderName] = present;
        }
        await this.WriteDocumentAsync(mailboxId, FolderClockKind, clock, ct).ConfigureAwait(false);
        return deleted;
    }
}

/// <summary>How long one person's Trash and Junk are kept (rc.15, item 22).</summary>
/// <param name="Days">Days in Trash for this person.</param>
/// <param name="Chosen">True when that is their own choice; false when they follow the organisation.</param>
/// <param name="OrganisationDays">The organisation's setting, which people who never chose follow.</param>
/// <param name="Longest">The longest the organisation lets a person choose (Anjal's limit is 90).</param>
/// <param name="JunkDays">Days in Junk: the organisation's, the same for everyone, unless the person kept an earlier setting (DES-11 D7).</param>
public sealed record TrashRule(int Days, bool Chosen, int OrganisationDays, int Longest, int JunkDays);
