using System.Security.Cryptography;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A person whose mail waits for an administrator to restore it (DES-15 §6.1).</summary>
/// <param name="Mailbox">The person's mailbox.</param>
/// <param name="Since">When their password was changed without the old one.</param>
/// <param name="Granted">Restored already; waiting for their next sign-in.</param>
public sealed record WaitingRestore(MailboxRow Mailbox, DateTimeOffset Since, bool Granted);

/// <summary>What the organisation console's Encryption section shows (DES-15 §6, §11).</summary>
public sealed class EncryptionView
{
    /// <summary>Whether the organisation's mail is encrypted (every organisation; not the individuals' service).</summary>
    public bool Encrypts { get; init; }

    /// <summary>Whether this administrator's session holds the organisation's recovery key.</summary>
    public bool HaveRecovery { get; init; }

    /// <summary>Whether the temporary copy of the recovery key still exists (no sheet confirmed yet).</summary>
    public bool TemporaryCopy { get; init; }

    /// <summary>When the recovery sheet in use was confirmed; null when none is.</summary>
    public DateTimeOffset? SheetConfirmedAt { get; init; }

    /// <summary>Whether a sheet was issued and is waiting to be typed back.</summary>
    public bool SheetWaiting { get; init; }

    /// <summary>Whether the break-glass key is on for this organisation (D-240).</summary>
    public bool BreakGlassOn { get; init; }

    /// <summary>Whether the current sheet's secret is also sealed to the break-glass key.</summary>
    public bool BreakGlassSealed { get; init; }

    /// <summary>Whether Anjal's break-glass key has been made and set on this server.</summary>
    public bool BreakGlassReady { get; init; }

    /// <summary>People whose mail waits for a restore.</summary>
    public IReadOnlyList<WaitingRestore> Restores { get; init; } = Array.Empty<WaitingRestore>();

    /// <summary>People and shared mailboxes whose mail is encrypted.</summary>
    public int WithKeys { get; init; }

    /// <summary>People who have not signed in since encryption began (their mail moves at their first sign-in, §11).</summary>
    public IReadOnlyList<MailboxRow> NotYet { get; init; } = Array.Empty<MailboxRow>();

    /// <summary>Administrators who hold the recovery key.</summary>
    public int AdminsWithKey { get; init; }

    /// <summary>All administrators.</summary>
    public int Admins { get; init; }
}

/// <summary>rc.25 (DES-15 §6, §7.3): the organisation's keys as its administrators handle them.</summary>
public sealed partial class MailboxService
{
    /// <summary>
    /// The organisation's recovery private key, if this request can open it: from the
    /// administrator's own copy (in the request's keyring), else from the temporary copy kept until
    /// the first sheet is confirmed. A copy of the caller's is the caller's to clear.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key, or null.</returns>
    public async Task<byte[]?> RecoveryKeyHereAsync(TenantRow tenant, Guid adminId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (this.KeyManager is not MailKeyManager km || !await km.EncryptsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            return null;
        }
        TenantKeyRow keys = await km.EnsureTenantKeysAsync(tenant.Id, ct).ConfigureAwait(false);
        MailKeyring ring = MailKeyring.Current;
        if (ring.PrivateKeyFor(keys.RecoveryPublic) is byte[] held)
        {
            return (byte[])held.Clone();
        }
        if (ring.PrivateKeyOf(adminId) is byte[] priv && ring.DataKey(adminId) is byte[] data)
        {
            return await km.OpenRecoveryAsync(tenant.Id, adminId, new UnlockedKeys(priv, data), ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Bring the organisation's key copies up to date, in an administrator's request that holds the
    /// recovery key: each administrator with keys gets their copy of it; each shared mailbox gets its
    /// keys, each member with keys their copy of them, and a person no longer a member loses theirs.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many copies were given or taken.</returns>
    public async Task<int> SyncKeysAsync(TenantRow tenant, Guid adminId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (this.KeyManager is not MailKeyManager km || await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false) is not byte[] recovery)
        {
            return 0;
        }
        int changed = 0;
        try
        {
            IReadOnlyList<Guid> admins = await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false);
            var holders = (await km.Store.ListGrantsOfAsync(tenant.Id, KeyGrant.OrgAdmin, ct).ConfigureAwait(false)).Select(g => g.HolderId).ToHashSet();
            foreach (Guid admin in admins)
            {
                if (!holders.Contains(admin) && await km.GrantAdminAsync(tenant.Id, admin, recovery, ct).ConfigureAwait(false))
                {
                    changed++;
                }
            }
            // Someone who is no longer an administrator loses their copy of the recovery key.
            foreach (Guid former in holders.Where(h => !admins.Contains(h)))
            {
                await km.Store.DeleteGrantAsync(tenant.Id, former, KeyGrant.OrgAdmin, ct).ConfigureAwait(false);
                changed++;
            }
            foreach ((MailboxRow box, SharedMailbox info, long _) in await this.SharedListAsync(tenant.Id, ct).ConfigureAwait(false))
            {
                changed += await this.SyncSharedAsync(km, box, info, recovery, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
        return changed;
    }

    /// <summary>What the Encryption section shows.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator looking.</param>
    /// <param name="people">The organisation's people.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The view.</returns>
    public async Task<EncryptionView> EncryptionOfAsync(TenantRow tenant, Guid adminId, IReadOnlyList<MailboxRow> people, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(people);
        if (this.KeyManager is not MailKeyManager km || !await km.EncryptsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            return new EncryptionView();
        }
        TenantKeyRow keys = await km.EnsureTenantKeysAsync(tenant.Id, ct).ConfigureAwait(false);
        byte[]? recovery = await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false);
        if (recovery is not null)
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
        var restores = new List<WaitingRestore>();
        foreach (RestoreRequestRow r in await km.Store.ListWaitingRestoresAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            if (people.FirstOrDefault(p => p.Id == r.MailboxId) is MailboxRow m)
            {
                restores.Add(new WaitingRestore(m, r.CreatedAt, r.Granted is not null));
            }
        }
        IReadOnlyDictionary<Guid, byte[]> publicKeys = await km.Store.GetPublicKeysAsync(people.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
        IReadOnlyList<Guid> admins = await this.AdminsOfAsync(tenant, ct).ConfigureAwait(false);
        int adminsWithKey = 0;
        foreach (Guid a in admins)
        {
            if ((await km.Store.ListGrantsForHolderAsync(a, ct).ConfigureAwait(false)).Any(g => g.KeyOwner == tenant.Id && g.Kind == KeyGrant.OrgAdmin))
            {
                adminsWithKey++;
            }
        }
        BreakGlassRow? glass = await km.BreakGlassAsync(ct).ConfigureAwait(false);
        return new EncryptionView
        {
            Encrypts = true,
            HaveRecovery = recovery is not null,
            TemporaryCopy = keys.RecoveryByServer is not null,
            SheetConfirmedAt = keys.SheetConfirmedAt,
            SheetWaiting = keys.SheetConfirmedAt is null && await this.SheetIssuedAsync(tenant.Id, ct).ConfigureAwait(false),
            BreakGlassOn = keys.BreakGlassOn,
            BreakGlassSealed = keys.SecretByBreakGlass is not null,
            BreakGlassReady = glass is not null,
            Restores = restores,
            WithKeys = publicKeys.Count,
            NotYet = people.Where(p => !publicKeys.ContainsKey(p.Id)).ToList(),
            AdminsWithKey = adminsWithKey,
            Admins = admins.Count,
        };
    }

    /// <summary>Restore a person's mail (DES-15 §6.1); they are told by mail, and it opens at their next sign-in.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="personId">The person.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null on success, else why not ("no-key", "nothing-waiting", "wrong-key").</returns>
    public async Task<string?> RestoreMailAsync(TenantRow tenant, Guid adminId, Guid personId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (this.KeyManager is not MailKeyManager km || await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false) is not byte[] recovery)
        {
            return "no-key";
        }
        if (await this.store.GetMailboxByIdAsync(personId, ct).ConfigureAwait(false) is not MailboxRow person || person.TenantId != tenant.Id ||
            await this.store.GetMailboxByIdAsync(adminId, ct).ConfigureAwait(false) is not MailboxRow admin)
        {
            CryptographicOperations.ZeroMemory(recovery);
            return "nothing-waiting";
        }
        string? error;
        try
        {
            error = await km.GrantRestoreAsync(personId, recovery, admin.Address, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
        if (error is null)
        {
            string name = admin.DisplayName.Trim().Length > 0 ? admin.DisplayName.Trim() + " (" + admin.Address + ")" : admin.Address;
            await this.SendSystemMailAsync(person, person.Address, "Your mail has been restored",
                $"{name} restored your mail on {DateTimeOffset.UtcNow.UtcDateTime:d MMM yyyy}.\r\n\r\nSign in with your new password and your mail opens as before. If you did not ask for this, tell your administrator.\r\n", ct).ConfigureAwait(false);
        }
        return error;
    }

    /// <summary>Make a new recovery sheet; the secret is returned to show once (DES-15 §6.2).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The sheet's text, or null when this session does not hold the recovery key.</returns>
    public async Task<string?> NewSheetAsync(TenantRow tenant, Guid adminId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (this.KeyManager is not MailKeyManager km || await this.RecoveryKeyHereAsync(tenant, adminId, ct).ConfigureAwait(false) is not byte[] recovery)
        {
            return null;
        }
        try
        {
            byte[] secret = await km.IssueSheetAsync(tenant.Id, recovery, ct).ConfigureAwait(false);
            string text = PaperForm.Write(secret);
            CryptographicOperations.ZeroMemory(secret);
            await this.WriteTenantDocumentAsync(tenant.Id, SheetIssuedKind, new SheetIssued { At = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
            return text;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
    }

    /// <summary>The sheet typed back: confirmed, and the temporary copy of the recovery key destroyed.</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="typed">What was typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it matched the sheet in use.</returns>
    public Task<bool> ConfirmSheetAsync(TenantRow tenant, string typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(typed);
        return this.KeyManager is MailKeyManager km ? km.ConfirmSheetAsync(tenant.Id, typed, ct) : Task.FromResult(false);
    }

    /// <summary>
    /// Use the recovery sheet (DES-15 §6.1): when no administrator holds the recovery key any more,
    /// the sheet opens it and this administrator gets their copy.
    /// </summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="adminId">The administrator (their mail must be open in this request).</param>
    /// <param name="typed">The sheet as typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null on success, else why not ("wrong-sheet", "locked").</returns>
    public async Task<string?> UseSheetAsync(TenantRow tenant, Guid adminId, string typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(typed);
        if (this.KeyManager is not MailKeyManager km)
        {
            return "wrong-sheet";
        }
        byte[]? recovery = await km.OpenWithSheetAsync(tenant.Id, typed, ct).ConfigureAwait(false);
        if (recovery is null)
        {
            return "wrong-sheet";
        }
        try
        {
            // An administrator whose own mail is locked (their password was reset) is restored at once.
            if (await km.Store.GetRestoreRequestAsync(adminId, ct).ConfigureAwait(false) is { Granted: null } &&
                await this.store.GetMailboxByIdAsync(adminId, ct).ConfigureAwait(false) is MailboxRow admin)
            {
                await km.GrantRestoreAsync(adminId, recovery, admin.Address, ct).ConfigureAwait(false);
            }
            return await km.GrantAdminAsync(tenant.Id, adminId, recovery, ct).ConfigureAwait(false) ? null : "locked";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recovery);
        }
    }

    /// <summary>Switch the break-glass key on or off for the organisation (D-240).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task SetBreakGlassAsync(TenantRow tenant, bool on, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return this.KeyManager is MailKeyManager km ? km.SetBreakGlassAsync(tenant.Id, on, ct) : Task.CompletedTask;
    }

    /// <summary>An organisation's encryption as Anjal's operators see it (DES-15 §7.14).</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What they see.</returns>
    public async Task<OpsEncryption> OpsEncryptionOfAsync(TenantRow tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (this.KeyManager is not MailKeyManager km || !await km.EncryptsAsync(tenant.Id, ct).ConfigureAwait(false))
        {
            return new OpsEncryption(false, null, false, string.Empty, 0, 0, false);
        }
        TenantKeyRow? keys = await km.Store.GetTenantKeysAsync(tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyList<MailboxRow> people = await this.store.ListMailboxesAsync(tenant.Id, ct).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, byte[]> publicKeys = await km.Store.GetPublicKeysAsync(people.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
        return new OpsEncryption(
            true,
            keys?.SheetConfirmedAt,
            keys?.BreakGlassOn ?? true,
            keys?.SecretByBreakGlass is byte[] sealedSecret ? MailEnvelope.Base64Url(sealedSecret) : string.Empty,
            publicKeys.Count,
            people.Count(p => !publicKeys.ContainsKey(p.Id)),
            keys?.RecoveryByServer is not null);
    }

    /// <summary>The kind of the tenant document noting when a sheet was last issued.</summary>
    internal const string SheetIssuedKind = "recovery-sheet";

    private async Task<bool> SheetIssuedAsync(Guid tenantId, CancellationToken ct) =>
        await this.ReadTenantDocumentAsync<SheetIssued>(tenantId, SheetIssuedKind, ct).ConfigureAwait(false) is not null;

    // A shared mailbox's keys: made if it has none (and its stored mail moved in the background),
    // given to each member who has keys, taken from anyone no longer a member.
    private async Task<int> SyncSharedAsync(MailKeyManager km, MailboxRow box, SharedMailbox info, byte[] recovery, CancellationToken ct)
    {
        int changed = 0;
        byte[]? priv;
        if (await km.Store.GetMailboxKeysAsync(box.Id, ct).ConfigureAwait(false) is null)
        {
            UnlockedKeys? made = await km.CreateMailboxKeysAsync(box, null, ct).ConfigureAwait(false);
            if (made is null)
            {
                return 0;
            }
            this.ForgetKeyCache(box.Address);
            this.HousekeepInBackground(box.Id, made);
            priv = (byte[])made.PrivateKey.Clone();
            changed++;
        }
        else
        {
            priv = await km.OpenMailboxPrivateByOrgAsync(box.Id, recovery, ct).ConfigureAwait(false);
        }
        if (priv is null)
        {
            return changed;
        }
        try
        {
            var holders = (await km.Store.ListGrantsOfAsync(box.Id, KeyGrant.SharedMember, ct).ConfigureAwait(false)).Select(g => g.HolderId).ToHashSet();
            foreach (Guid member in info.Members.Keys)
            {
                if (!holders.Contains(member) && await km.GrantSharedAsync(box.Id, member, priv, ct).ConfigureAwait(false))
                {
                    changed++;
                }
            }
            foreach (Guid holder in holders.Where(h => !info.Members.ContainsKey(h)))
            {
                await km.RevokeSharedAsync(box.Id, holder, ct).ConfigureAwait(false);
                changed++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
        }
        return changed;
    }

    /// <summary>Run a mailbox's housekeeping in the background (it is run again at the next chance if stopped).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="keys">Its keys (copied).</param>
    internal void HousekeepInBackground(Guid mailboxId, UnlockedKeys keys)
    {
        var copy = new UnlockedKeys((byte[])keys.PrivateKey.Clone(), (byte[])keys.DataKey.Clone());
        _ = Task.Run(async () =>
        {
            try
            {
                // The request's keyring flowed here and goes when the request ends; housekeeping uses its own.
                using IDisposable none = MailKeyring.Use(MailKeyring.Empty);
                await this.HousekeepAsync(mailboxId, copy, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Housekeeping is done again at the next chance; it never stops a request.
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[keys] housekeeping for {mailboxId:N} stopped: {ex.GetType().Name}");
            }
#pragma warning restore CA1031
            finally
            {
                CryptographicOperations.ZeroMemory(copy.PrivateKey);
                CryptographicOperations.ZeroMemory(copy.DataKey);
            }
        }, CancellationToken.None);
    }

    // Tell the organisation's administrators that someone's mail waits to be restored.
    private async Task TellAdminsRestoreWaitingAsync(MailboxRow person, CancellationToken ct)
    {
        if (await this.GetContextAsync(person.Id, ct).ConfigureAwait(false) is not (TenantRow Tenant, MailboxRow _) context)
        {
            return;
        }
        string who = person.DisplayName.Trim().Length > 0 ? person.DisplayName.Trim() + " (" + person.Address + ")" : person.Address;
        foreach (Guid admin in await this.AdminsOfAsync(context.Tenant, ct).ConfigureAwait(false))
        {
            if (admin != person.Id && await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a)
            {
                await this.SendSystemMailAsync(a, a.Address, $"{who} needs their mail restored",
                    $"{who} has a new password set without the old one, so their mail is locked. Anjal cannot open it; an administrator can.\r\n\r\nOpen the organisation console, Encryption, and press Restore beside their name. Their mail opens at their next sign-in.\r\n", ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>When the last recovery sheet was issued.</summary>
    internal sealed class SheetIssued
    {
        /// <summary>When.</summary>
        public DateTimeOffset At { get; set; }
    }
}

/// <summary>What Anjal's operators see of an organisation's encryption (DES-15 §7.14): never content, never a key that opens anything.</summary>
/// <param name="Encrypts">Whether its mail is encrypted.</param>
/// <param name="SheetConfirmedAt">When its recovery sheet was confirmed.</param>
/// <param name="BreakGlassOn">Whether the break-glass key is on for it.</param>
/// <param name="SealedSecret">Its recovery secret sealed to the break-glass key (base64url), for the offline tool on its written request; empty when none.</param>
/// <param name="WithKeys">Mailboxes encrypted.</param>
/// <param name="NotYet">People who have not signed in since encryption began.</param>
/// <param name="TemporaryCopy">Whether the temporary copy of its recovery key still exists.</param>
public sealed record OpsEncryption(bool Encrypts, DateTimeOffset? SheetConfirmedAt, bool BreakGlassOn, string SealedSecret, int WithKeys, int NotYet, bool TemporaryCopy);
