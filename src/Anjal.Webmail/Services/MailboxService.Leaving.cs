using System.Globalization;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>Where a person's leaving stands (rc.26, item 80, brief 11h, D-266): each step, when it was done.</summary>
public sealed class LeaverSteps
{
    /// <summary>Sign-in stopped.</summary>
    public DateTimeOffset? Stopped { get; set; }

    /// <summary>The automatic reply set for 90 days.</summary>
    public DateTimeOffset? Replying { get; set; }

    /// <summary>New mail forwarded.</summary>
    public DateTimeOffset? Forwarding { get; set; }

    /// <summary>To whom new mail is forwarded.</summary>
    public string ForwardTo { get; set; } = string.Empty;

    /// <summary>The mail handed to a colleague.</summary>
    public DateTimeOffset? HandedOver { get; set; }

    /// <summary>The colleague.</summary>
    public Guid? Colleague { get; set; }

    /// <summary>The records produced (the production's id).</summary>
    public Guid? Records { get; set; }
}

/// <summary>
/// rc.26 (item 80, brief 11h; D-266): a person leaving. One page, in order: stop sign-in, an automatic
/// reply for 90 days, forward new mail, hand the mail to a colleague, download it for the records. The
/// handover is what rc.25 made necessary: the leaver's password is gone, so an administrator whose session
/// holds the organisation's recovery key gives a colleague a copy of the leaver's mailbox key - the leaver's
/// mailbox becomes a shared mailbox the colleague opens from their account menu.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The organisation document holding each leaver's steps.</summary>
    public const string LeaversKind = "leavers";

    /// <summary>How long the leaver's automatic reply stays on.</summary>
    public static readonly TimeSpan LeaverReplyFor = TimeSpan.FromDays(90);

    /// <summary>A leaver's steps so far.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The steps.</returns>
    public async Task<LeaverSteps> LeaverStepsAsync(Guid tenantId, Guid personId, CancellationToken ct = default) =>
        (await this.ReadTenantDocumentAsync<Dictionary<Guid, LeaverSteps>>(tenantId, LeaversKind, ct).ConfigureAwait(false))?.GetValueOrDefault(personId) ?? new LeaverSteps();

    /// <summary>The automatic reply a leaver's page offers first.</summary>
    /// <param name="person">The leaver.</param>
    /// <param name="organisation">The organisation's name.</param>
    /// <param name="writeTo">The address to write to instead.</param>
    /// <returns>The text.</returns>
    public static string LeaverReplyText(MailboxRow person, string organisation, string writeTo)
    {
        ArgumentNullException.ThrowIfNull(person);
        string name = person.DisplayName.Trim().Length > 0 ? person.DisplayName.Trim() : person.Address;
        return string.Create(CultureInfo.InvariantCulture, $"{name} has left {organisation}. Please write to {writeTo}.");
    }

    /// <summary>Note one leaver step.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="personId">The person.</param>
    /// <param name="change">What to note.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task NoteLeaverStepAsync(Guid tenantId, Guid personId, Action<LeaverSteps> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        Dictionary<Guid, LeaverSteps> all = await this.ReadTenantDocumentAsync<Dictionary<Guid, LeaverSteps>>(tenantId, LeaversKind, ct).ConfigureAwait(false) ?? new Dictionary<Guid, LeaverSteps>();
        LeaverSteps steps = all.GetValueOrDefault(personId) ?? new LeaverSteps();
        change(steps);
        all[personId] = steps;
        await this.WriteTenantDocumentAsync(tenantId, LeaversKind, all, ct).ConfigureAwait(false);
    }

    /// <summary>Step 2: the leaver's automatic reply, to everyone, for 90 days. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="personId">The leaver.</param>
    /// <param name="text">The reply.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> LeaverReplyAsync(TenantRow tenant, Guid personId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(text);
        string t = text.Trim();
        if (t.Length == 0 || t.Length > 4000)
        {
            return "Write the reply, up to 4,000 characters.";
        }
        AwayReply away = await AwayReply.ReadAsync(this.store, personId, ct).ConfigureAwait(false);
        away.On = true;
        away.From = null;
        away.Until = DateTimeOffset.UtcNow + LeaverReplyFor;
        away.Text = t;
        away.ReplyTo = "everyone";
        await AwayReply.WriteAsync(this.store, personId, away, ct).ConfigureAwait(false);
        await this.NoteLeaverStepAsync(tenant.Id, personId, s => s.Replying = DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Step 3: new mail to the leaver forwarded to a person or shared mailbox of the organisation. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="person">The leaver.</param>
    /// <param name="to">The address inside the organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> LeaverForwardAsync(TenantRow tenant, MailboxRow person, string to, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(to);
        string target = to.Trim().ToLowerInvariant();
        if (!MailboxSink.TrySplitAddress(target, out string local, out string domain) || await this.store.GetMailboxAsync(local, domain, ct).ConfigureAwait(false) is not MailboxRow found
            || found.TenantId != tenant.Id || found.Id == person.Id)
        {
            return "Choose a person or shared mailbox of this organisation.";
        }
        List<MailRule> rules = (await this.ListRulesAsync(person.Id, ct).ConfigureAwait(false)).ToList();
        rules.RemoveAll(r => r.Name == LeaverRuleName);
        var rule = new MailRule { Name = LeaverRuleName, ForwardTo = found.Address };
        rule.Conditions.Add(new RuleCondition { Field = "to", Op = "contains", Value = "@" });
        rules.Insert(0, rule);
        await this.store.SetMailboxDocumentAsync(person.Id, MailRules.Kind, System.Text.Json.JsonSerializer.Serialize(rules), ct).ConfigureAwait(false);
        await this.NoteLeaverStepAsync(tenant.Id, person.Id, s => { s.Forwarding = DateTimeOffset.UtcNow; s.ForwardTo = found.Address; }, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The name of the rule a leaver's page makes.</summary>
    public const string LeaverRuleName = "Forward while away for good";

    /// <summary>
    /// Step 4 (D-266): hand the leaver's mail to a colleague - the leaver's mailbox becomes a shared
    /// mailbox with the colleague as its member, and the colleague gets a copy of its key now, from this
    /// administrator's session (which holds the recovery key). Returns the error, or null.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="person">The leaver.</param>
    /// <param name="colleagueId">The colleague.</param>
    /// <param name="right">"read" or "send".</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> HandOverAsync(TenantRow tenant, Guid adminId, MailboxRow person, Guid colleagueId, string right, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(person);
        if (colleagueId == person.Id)
        {
            return "Choose someone other than the person leaving.";
        }
        // rc.26 review: only a leaver's mail is handed on (sign-in stopped first, step 1), and only to a
        // person of the same organisation.
        if (person.TenantId != tenant.Id || (await this.LeaverStepsAsync(tenant.Id, person.Id, ct).ConfigureAwait(false)).Stopped is null)
        {
            return "Stop the person's sign-in first (step 1).";
        }
        if (await this.store.GetMailboxByIdAsync(colleagueId, ct).ConfigureAwait(false) is not MailboxRow chosen || chosen.TenantId != tenant.Id || !chosen.Enabled)
        {
            return "Choose a person or shared mailbox of this organisation.";
        }
        if (this.KeyManager is MailKeyManager km && await km.Store.GetMailboxKeysAsync(person.Id, ct).ConfigureAwait(false) is not null)
        {
            if (await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false) is not byte[] recovery)
            {
                return "Your session does not hold the organisation's recovery key. Sign in with your password and try again.";
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(recovery);
        }
        Dictionary<Guid, SharedMailbox> all = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (all.ContainsKey(colleagueId))
        {
            return "Choose a person, not a shared mailbox.";
        }
        if (!all.TryGetValue(person.Id, out SharedMailbox? info))
        {
            info = new SharedMailbox { KeepSentCopy = true };
            all[person.Id] = info;
        }
        info.Members[colleagueId] = right == "read" ? "read" : "send";
        await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, all, ct).ConfigureAwait(false);
        this.rights.TryRemove((colleagueId, person.Id), out _);
        // The colleague's copy of the key, now (the same step a shared mailbox's member gets, D-261).
        await this.SyncKeysAsync(tenant, adminId, ct).ConfigureAwait(false);
        await this.NoteLeaverStepAsync(tenant.Id, person.Id, s => { s.HandedOver = DateTimeOffset.UtcNow; s.Colleague = colleagueId; }, ct).ConfigureAwait(false);
        if (await this.store.GetMailboxByIdAsync(colleagueId, ct).ConfigureAwait(false) is MailboxRow colleague)
        {
            foreach (Guid admin in await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false))
            {
                if (await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a)
                {
                    await this.SendSystemMailAsync(a, a.Address, "A leaver's mail was handed to a colleague",
                        $"The mail of {person.Address}, who has left {tenant.DisplayName}, was handed to {colleague.Address} through the organisation's recovery key. "
                        + "They open it from their account menu. It is in the activity log; it can be taken back on the person's Leaving page.\r\n\r\nAnjal", ct).ConfigureAwait(false);
                }
            }
        }
        return null;
    }

    /// <summary>Take the leaver's mail back from the colleague. Returns the error, or null.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="person">The leaver.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> TakeBackAsync(TenantRow tenant, Guid adminId, MailboxRow person, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(person);
        Dictionary<Guid, SharedMailbox> all = await this.SharedOfTenantAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!all.TryGetValue(person.Id, out SharedMailbox? info))
        {
            return "Nothing has been handed over.";
        }
        foreach (Guid member in info.Members.Keys.ToList())
        {
            this.rights.TryRemove((member, person.Id), out _);
        }
        info.Members.Clear();
        await this.WriteTenantDocumentAsync(tenant.Id, SharedKind, all, ct).ConfigureAwait(false);
        await this.SyncKeysAsync(tenant, adminId, ct).ConfigureAwait(false);
        await this.NoteLeaverStepAsync(tenant.Id, person.Id, s => { s.HandedOver = null; s.Colleague = null; }, ct).ConfigureAwait(false);
        return null;
    }
}
