using System.Security.Cryptography;
using System.Text;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>A mailbox's keys, unlocked: its private key (PKCS#8) and its data key.</summary>
/// <param name="PrivateKey">The private key.</param>
/// <param name="DataKey">The data key.</param>
public sealed record UnlockedKeys(byte[] PrivateKey, byte[] DataKey);

/// <summary>What happened when a person's password was used to open their mail.</summary>
public enum UnlockOutcome
{
    /// <summary>The mailbox has no keys and gets none (an individual).</summary>
    NotEncrypted = 0,

    /// <summary>Opened.</summary>
    Opened = 1,

    /// <summary>Made now (the first sign-in after the move to encryption): open, and the mail already stored must be moved.</summary>
    Created = 2,

    /// <summary>Opened through a restore an administrator granted (DES-15 §6.1).</summary>
    Restored = 3,

    /// <summary>Locked: the password was reset and an administrator has not restored the mail yet.</summary>
    Locked = 4,
}

/// <summary>The result of opening a person's mail with their password.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Keys">The keys, when opened.</param>
public sealed record UnlockResult(UnlockOutcome Outcome, UnlockedKeys? Keys);

/// <summary>
/// rc.25 (DES-15): every operation on keys - making them, opening them with a password, keeping a
/// session's copy, an organisation's recovery key and its sheet, an administrator restoring a
/// person's mail, shared mailboxes. It never sees a password longer than one call, and never
/// stores anything that opens a key on its own.
/// </summary>
public sealed class MailKeyManager
{
    private static readonly byte[] TemporaryInfo = Encoding.ASCII.GetBytes("anjal-temporary-v1");
    private static readonly byte[] HoldInfo = Encoding.ASCII.GetBytes("anjal-scheduled-v1");
    private readonly IMailKeyStore keys;
    private readonly IMailboxStore boxes;
    private readonly byte[] pepper;

    /// <summary>Initializes a new instance of the <see cref="MailKeyManager"/> class.</summary>
    /// <param name="keys">The key store.</param>
    /// <param name="boxes">The mailbox store (tenants, mailboxes, the tenant kind).</param>
    /// <param name="pepper">The pepper (<see cref="MailKeys.PepperFromEnvironment"/>; empty in development).</param>
    public MailKeyManager(IMailKeyStore keys, IMailboxStore boxes, byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(pepper);
        this.keys = keys;
        this.boxes = boxes;
        this.pepper = (byte[])pepper.Clone();
    }

    /// <summary>The key store.</summary>
    public IMailKeyStore Store => this.keys;

    /// <summary>Whether an organisation's mail is encrypted: every organisation, not the individuals' service (D-233, D-234).</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is.</returns>
    public async Task<bool> EncryptsAsync(Guid tenantId, CancellationToken ct = default) =>
        !await TenantKind.IsIndividualsAsync(this.boxes, tenantId, ct).ConfigureAwait(false);

    /// <summary>
    /// The organisation's keys, made now if it has none. A new recovery key is kept, until an
    /// administrator confirms a recovery sheet, with a key derived from the pepper (DES-15 §6.2).
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row.</returns>
    public async Task<TenantKeyRow> EnsureTenantKeysAsync(Guid tenantId, CancellationToken ct = default)
    {
        TenantKeyRow? row = await this.keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }
        (byte[] pub, byte[] priv) = MailKeys.NewKeyPair();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] wrapKey = MailKeys.RecoveryWrapKey(secret);
            row = new TenantKeyRow
            {
                TenantId = tenantId,
                RecoveryPublic = pub,
                RecoveryBySecret = MailKeys.Wrap(priv, wrapKey),
                SecretCheck = MailKeys.SymmetricKeyIdText(wrapKey),
                BreakGlassOn = true,
                RecoveryByServer = MailKeys.Wrap(priv, this.TemporaryKey()),
            };
            // Two requests making the first keys at once: the keys stored first are the organisation's.
            if (await this.keys.AddTenantKeysAsync(row, ct).ConfigureAwait(false))
            {
                return row;
            }
            return await this.keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The organisation's keys were neither stored nor found.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(priv);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Make a mailbox's keys (a new person at registration, a person at their first sign-in after the
    /// move, a shared mailbox). Null when the mailbox is an individual's or already has keys.
    /// </summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="password">The person's password; null for a shared mailbox (it is opened through its members).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The unlocked keys.</returns>
    public async Task<UnlockedKeys?> CreateMailboxKeysAsync(MailboxRow mailbox, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        if (!await this.EncryptsAsync(mailbox.TenantId, ct).ConfigureAwait(false) ||
            await this.keys.GetMailboxKeysAsync(mailbox.Id, ct).ConfigureAwait(false) is not null)
        {
            return null;
        }
        TenantKeyRow tenant = await this.EnsureTenantKeysAsync(mailbox.TenantId, ct).ConfigureAwait(false);
        (byte[] pub, byte[] priv) = MailKeys.NewKeyPair();
        byte[] dataKey = MailKeys.NewSymmetricKey();
        byte[] salt = MailKeys.NewSalt();
        var row = new MailboxKeyRow
        {
            MailboxId = mailbox.Id,
            PublicKey = pub,
            UnlockSalt = salt,
            PrivateByPassword = password is null ? null : WrapWithUnlock(priv, password, salt, this.pepper),
            PrivateByOrg = MailEnvelope.SealToPublicKey(priv, tenant.RecoveryPublic),
            DataKey = MailEnvelope.SealToPublicKey(dataKey, pub),
        };
        // Two first sign-ins at once (two devices, a double press): the keys stored first are the
        // mailbox's; the other request's keys are thrown away before anything is sealed with them.
        if (!await this.keys.AddMailboxKeysAsync(row, ct).ConfigureAwait(false))
        {
            CryptographicOperations.ZeroMemory(priv);
            CryptographicOperations.ZeroMemory(dataKey);
            return null;
        }
        return new UnlockedKeys(priv, dataKey);
    }

    /// <summary>
    /// Open a person's mail with their password at sign-in: their own wrap, else a granted restore,
    /// else (no keys yet) make them; locked when the password was reset and no restore was granted.
    /// The caller has already checked the password itself.
    /// </summary>
    /// <param name="mailbox">The mailbox.</param>
    /// <param name="password">The password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<UnlockResult> UnlockAsync(MailboxRow mailbox, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(password);
        MailboxKeyRow? row = await this.keys.GetMailboxKeysAsync(mailbox.Id, ct).ConfigureAwait(false);
        if (row is null)
        {
            UnlockedKeys? made = await this.CreateMailboxKeysAsync(mailbox, password, ct).ConfigureAwait(false);
            if (made is not null)
            {
                return new UnlockResult(UnlockOutcome.Created, made);
            }
            // Not encrypted, or another sign-in made the keys a moment ago: those are opened below.
            row = await this.keys.GetMailboxKeysAsync(mailbox.Id, ct).ConfigureAwait(false);
            if (row is null)
            {
                return new UnlockResult(UnlockOutcome.NotEncrypted, null);
            }
        }
        RestoreRequestRow? restore = await this.keys.GetRestoreRequestAsync(mailbox.Id, ct).ConfigureAwait(false);
        if (row.PrivateByPassword is not null)
        {
            byte[] unlock = MailKeys.UnlockKey(password, row.UnlockSalt, this.pepper);
            byte[]? priv = MailKeys.Unwrap(row.PrivateByPassword, unlock);
            CryptographicOperations.ZeroMemory(unlock);
            if (priv is not null)
            {
                if (restore is not null)
                {
                    // A restore asked for when the pepper was wrong, and the pepper is right again.
                    await this.keys.DeleteRestoreRequestAsync(mailbox.Id, ct).ConfigureAwait(false);
                }
                return new UnlockResult(UnlockOutcome.Opened, new UnlockedKeys(priv, OpenDataKey(row, priv)));
            }
        }
        if (restore is null && row.PrivateByPassword is not null)
        {
            // The password is right (the caller checked it) but no longer opens the key: the pepper was
            // changed or lost. The mail waits for an administrator's restore, as after a forgotten
            // password - and the password's own copy is kept, so that putting the right pepper back opens it.
            await this.StartRestoreAsync(mailbox.Id, password, passwordReset: false, ct).ConfigureAwait(false);
            return new UnlockResult(UnlockOutcome.Locked, null);
        }
        if (restore?.Granted is null)
        {
            return new UnlockResult(UnlockOutcome.Locked, null);
        }
        byte[] restoreUnlock = MailKeys.UnlockKey(password, restore.UnlockSalt, this.pepper);
        byte[]? restorePrivate = MailKeys.Unwrap(restore.RestoreByUnlock, restoreUnlock);
        CryptographicOperations.ZeroMemory(restoreUnlock);
        if (restorePrivate is null)
        {
            return new UnlockResult(UnlockOutcome.Locked, null);
        }
        using var ring = new MailKeyring();
        ring.AddPrivateKey(restorePrivate);
        CryptographicOperations.ZeroMemory(restorePrivate);
        byte[]? mailboxPrivate = MailEnvelope.Open(restore.Granted, ring);
        if (mailboxPrivate is null)
        {
            return new UnlockResult(UnlockOutcome.Locked, null);
        }
        await this.WrapWithPasswordAsync(row, mailboxPrivate, password, ct).ConfigureAwait(false);
        await this.keys.DeleteRestoreRequestAsync(mailbox.Id, ct).ConfigureAwait(false);
        return new UnlockResult(UnlockOutcome.Restored, new UnlockedKeys(mailboxPrivate, OpenDataKey(row, mailboxPrivate)));
    }

    /// <summary>A new password while the mail is open (Settings › Security; a session): the private key is wrapped with the new unlock key; no mail is re-encrypted.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="unlocked">The keys, open in the session.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the mailbox has keys.</returns>
    public async Task<bool> RewrapAsync(Guid mailboxId, UnlockedKeys unlocked, string newPassword, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unlocked);
        ArgumentNullException.ThrowIfNull(newPassword);
        MailboxKeyRow? row = await this.keys.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        await this.WrapWithPasswordAsync(row, unlocked.PrivateKey, newPassword, ct).ConfigureAwait(false);
        await this.keys.DeleteRestoreRequestAsync(mailboxId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A new password without the old one (forgotten; reset by an administrator or the recovery
    /// address): the mail stays locked and a restore request is made with a one-time key that only
    /// the new password opens (DES-15 §6.1). False when the mailbox has no keys (nothing to restore).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="passwordReset">A password reset (true): the old password's copy of the key and every session's copy are removed.
    /// False when the pepper changed: both are kept, so that putting the right pepper back opens the mail again.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a request was made.</returns>
    public async Task<bool> StartRestoreAsync(Guid mailboxId, string newPassword, bool passwordReset = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        MailboxKeyRow? row = await this.keys.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        (byte[] pub, byte[] priv) = MailKeys.NewKeyPair();
        byte[] salt = MailKeys.NewSalt();
        await this.keys.SaveRestoreRequestAsync(
            new RestoreRequestRow
            {
                MailboxId = mailboxId,
                RestorePublic = pub,
                RestoreByUnlock = WrapWithUnlock(priv, newPassword, salt, this.pepper),
                UnlockSalt = salt,
            },
            ct).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(priv);
        if (passwordReset)
        {
            // A reset may follow a stolen password: the old password must open nothing any more.
            row.PrivateByPassword = null;
            await this.keys.SaveMailboxKeysAsync(row, ct).ConfigureAwait(false);
            await this.keys.DeleteMailboxSessionKeysAsync(mailboxId, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// An administrator restores a person's mail (DES-15 §6.1): the person's private key, opened with
    /// the organisation's recovery key, is sealed to their restore request's one-time key.
    /// </summary>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <param name="recoveryPrivate">The organisation's recovery private key, open in the administrator's session.</param>
    /// <param name="by">Who restored it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null on success, else why not.</returns>
    public async Task<string?> GrantRestoreAsync(Guid mailboxId, byte[] recoveryPrivate, string by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryPrivate);
        ArgumentNullException.ThrowIfNull(by);
        RestoreRequestRow? request = await this.keys.GetRestoreRequestAsync(mailboxId, ct).ConfigureAwait(false);
        if (request is null)
        {
            return "nothing-waiting";
        }
        byte[]? priv = await this.OpenMailboxPrivateByOrgAsync(mailboxId, recoveryPrivate, ct).ConfigureAwait(false);
        if (priv is null)
        {
            return "wrong-key";
        }
        request.Granted = MailEnvelope.SealToPublicKey(priv, request.RestorePublic);
        request.GrantedBy = by;
        request.GrantedAt = DateTimeOffset.UtcNow;
        CryptographicOperations.ZeroMemory(priv);
        await this.keys.SaveRestoreRequestAsync(request, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>A mailbox's private key opened with the organisation's recovery key (restores, shared mailboxes, legal holds).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="recoveryPrivate">The recovery private key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The private key, or null.</returns>
    public async Task<byte[]?> OpenMailboxPrivateByOrgAsync(Guid mailboxId, byte[] recoveryPrivate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryPrivate);
        MailboxKeyRow? row = await this.keys.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        using var ring = new MailKeyring();
        ring.AddPrivateKey(recoveryPrivate);
        try
        {
            return MailEnvelope.Open(row.PrivateByOrg, ring);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The organisation's recovery private key for an administrator: from their own grant (opened
    /// with their mailbox key), else from the temporary copy kept until the first sheet is confirmed.
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="adminId">The administrator's mailbox.</param>
    /// <param name="adminKeys">The administrator's keys, open in their session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The recovery private key, or null.</returns>
    public async Task<byte[]?> OpenRecoveryAsync(Guid tenantId, Guid adminId, UnlockedKeys adminKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adminKeys);
        foreach (KeyGrant g in await this.keys.ListGrantsForHolderAsync(adminId, ct).ConfigureAwait(false))
        {
            if (g.KeyOwner == tenantId && g.Kind == KeyGrant.OrgAdmin)
            {
                using var ring = new MailKeyring();
                ring.AddPrivateKey(adminKeys.PrivateKey);
                try
                {
                    if (MailEnvelope.Open(g.Body, ring) is byte[] opened)
                    {
                        return opened;
                    }
                }
                catch (CryptographicException)
                {
                    // A grant to an older key of this administrator: fall through.
                }
            }
        }
        TenantKeyRow? tenant = await this.keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        return tenant?.RecoveryByServer is byte[] temporary ? MailKeys.Unwrap(temporary, this.TemporaryKey()) : null;
    }

    /// <summary>Give an administrator their copy of the organisation's recovery key.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="adminId">The administrator's mailbox (it must have keys).</param>
    /// <param name="recoveryPrivate">The recovery private key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the grant was made.</returns>
    public async Task<bool> GrantAdminAsync(Guid tenantId, Guid adminId, byte[] recoveryPrivate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryPrivate);
        MailboxKeyRow? admin = await this.keys.GetMailboxKeysAsync(adminId, ct).ConfigureAwait(false);
        if (admin is null)
        {
            return false;
        }
        await this.keys.SaveGrantAsync(new KeyGrant(tenantId, adminId, KeyGrant.OrgAdmin, MailEnvelope.SealToPublicKey(recoveryPrivate, admin.PublicKey)), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Make a new recovery sheet (DES-15 §6.2): a new 32-byte secret wraps the recovery key; with the
    /// break-glass key switched on and made, the secret is also sealed to it (D-240). Returns the
    /// secret to show once; it is never stored.
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="recoveryPrivate">The recovery private key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The secret.</returns>
    public async Task<byte[]> IssueSheetAsync(Guid tenantId, byte[] recoveryPrivate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recoveryPrivate);
        TenantKeyRow row = await this.EnsureTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] wrapKey = MailKeys.RecoveryWrapKey(secret);
        row.RecoveryBySecret = MailKeys.Wrap(recoveryPrivate, wrapKey);
        row.SecretCheck = MailKeys.SymmetricKeyIdText(wrapKey);
        row.SheetConfirmedAt = null;
        BreakGlassRow? glass = row.BreakGlassOn ? await this.BreakGlassAsync(ct).ConfigureAwait(false) : null;
        row.SecretByBreakGlass = glass is null ? null : MailEnvelope.SealToPublicKey(secret, glass.PublicKey);
        await this.keys.SaveTenantKeysAsync(row, ct).ConfigureAwait(false);
        return secret;
    }

    /// <summary>The administrator typed the sheet back: it is confirmed, and the temporary copy of the recovery key is destroyed.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="typed">What was typed (the paper form).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it matched.</returns>
    public async Task<bool> ConfirmSheetAsync(Guid tenantId, string typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(typed);
        TenantKeyRow? row = await this.keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        if (row is null || PaperForm.Read(typed) is not byte[] secret ||
            !string.Equals(MailKeys.SymmetricKeyIdText(MailKeys.RecoveryWrapKey(secret)), row.SecretCheck, StringComparison.Ordinal))
        {
            return false;
        }
        row.SheetConfirmedAt = DateTimeOffset.UtcNow;
        row.RecoveryByServer = null;
        await this.keys.SaveTenantKeysAsync(row, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The recovery private key from a typed sheet (no administrator left; or the break-glass secret given back to the organisation).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="typed">The sheet as typed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key, or null.</returns>
    public async Task<byte[]?> OpenWithSheetAsync(Guid tenantId, string typed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(typed);
        TenantKeyRow? row = await this.keys.GetTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        return row is null || PaperForm.Read(typed) is not byte[] secret ? null : MailKeys.Unwrap(row.RecoveryBySecret, MailKeys.RecoveryWrapKey(secret));
    }

    /// <summary>
    /// Anjal's break-glass key as this server knows it: the line in the settings
    /// (<see cref="BreakGlassLine.Setting"/>) is kept in the database the first time it is seen, or
    /// when it is replaced (a new key, OPS-16); null when none was ever set.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The key, or null.</returns>
    public async Task<BreakGlassRow?> BreakGlassAsync(CancellationToken ct = default)
    {
        BreakGlassRow? stored = await this.keys.GetBreakGlassAsync(ct).ConfigureAwait(false);
        if (BreakGlassLine.Read(Environment.GetEnvironmentVariable(BreakGlassLine.Setting)) is (byte[] pub, byte[] wrapped) &&
            (stored is null || !stored.PublicKey.AsSpan().SequenceEqual(pub)))
        {
            stored = new BreakGlassRow(pub, wrapped, DateTimeOffset.UtcNow);
            await this.keys.SaveBreakGlassAsync(stored, ct).ConfigureAwait(false);
        }
        return stored;
    }

    /// <summary>Switch the break-glass key on or off for an organisation (D-240). Switching it on takes effect with the next sheet.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="on">On or off.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SetBreakGlassAsync(Guid tenantId, bool on, CancellationToken ct = default)
    {
        TenantKeyRow row = await this.EnsureTenantKeysAsync(tenantId, ct).ConfigureAwait(false);
        row.BreakGlassOn = on;
        if (!on)
        {
            row.SecretByBreakGlass = null;
        }
        await this.keys.SaveTenantKeysAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>Give a shared mailbox's key to a member (or replace it).</summary>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="memberId">The member's mailbox.</param>
    /// <param name="sharedPrivate">The shared mailbox's private key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the grant was made (the member has keys).</returns>
    public async Task<bool> GrantSharedAsync(Guid sharedId, Guid memberId, byte[] sharedPrivate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sharedPrivate);
        MailboxKeyRow? member = await this.keys.GetMailboxKeysAsync(memberId, ct).ConfigureAwait(false);
        if (member is null)
        {
            return false;
        }
        await this.keys.SaveGrantAsync(new KeyGrant(sharedId, memberId, KeyGrant.SharedMember, MailEnvelope.SealToPublicKey(sharedPrivate, member.PublicKey)), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Take a shared mailbox's key from a member.</summary>
    /// <param name="sharedId">The shared mailbox.</param>
    /// <param name="memberId">The member.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task RevokeSharedAsync(Guid sharedId, Guid memberId, CancellationToken ct = default) =>
        this.keys.DeleteGrantAsync(sharedId, memberId, KeyGrant.SharedMember, ct);

    /// <summary>
    /// Fill a keyring for a person's request: their own keys, the shared mailboxes given to them, and,
    /// for an administrator, their organisation's recovery key.
    /// </summary>
    /// <param name="ring">The keyring.</param>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <param name="own">Their keys.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task FillAsync(MailKeyring ring, Guid mailboxId, UnlockedKeys own, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(own);
        ring.Add(mailboxId, own.PrivateKey, own.DataKey);
        foreach (KeyGrant g in await this.keys.ListGrantsForHolderAsync(mailboxId, ct).ConfigureAwait(false))
        {
            byte[]? opened;
            try
            {
                opened = MailEnvelope.Open(g.Body, ring);
            }
            catch (CryptographicException)
            {
                continue;
            }
            if (opened is null)
            {
                continue;
            }
            if (g.Kind == KeyGrant.OrgAdmin)
            {
                ring.AddPrivateKey(opened);
            }
            else if (await this.keys.GetMailboxKeysAsync(g.KeyOwner, ct).ConfigureAwait(false) is MailboxKeyRow shared)
            {
                ring.Add(g.KeyOwner, opened, OpenDataKey(shared, opened));
            }
            CryptographicOperations.ZeroMemory(opened);
        }
    }

    /// <summary>A new random value for a session's cookie.</summary>
    /// <returns>32 bytes.</returns>
    public static byte[] NewSessionToken() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Keep a session's copy of the keys, wrapped with the cookie's value (DES-15 §4).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="token">The cookie's value.</param>
    /// <param name="unlocked">The keys.</param>
    /// <param name="expiresAt">When the session ends at the latest.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task SaveSessionAsync(string sessionId, Guid mailboxId, byte[] token, UnlockedKeys unlocked, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(unlocked);
        byte[] packed = new byte[4 + unlocked.PrivateKey.Length + unlocked.DataKey.Length];
        packed[0] = (byte)(unlocked.PrivateKey.Length >> 24);
        packed[1] = (byte)(unlocked.PrivateKey.Length >> 16);
        packed[2] = (byte)(unlocked.PrivateKey.Length >> 8);
        packed[3] = (byte)unlocked.PrivateKey.Length;
        unlocked.PrivateKey.CopyTo(packed, 4);
        unlocked.DataKey.CopyTo(packed, 4 + unlocked.PrivateKey.Length);
        byte[] wrapKey = MailKeys.SessionWrapKey(token);
        byte[] body = MailKeys.Wrap(packed, wrapKey);
        CryptographicOperations.ZeroMemory(packed);
        CryptographicOperations.ZeroMemory(wrapKey);
        return this.keys.SaveSessionKeysAsync(sessionId, mailboxId, body, expiresAt, ct);
    }

    /// <summary>A session's keys, opened with the cookie's value; null when there are none or the cookie is wrong.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="token">The cookie's value.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The keys.</returns>
    public async Task<UnlockedKeys?> LoadSessionAsync(string sessionId, Guid mailboxId, byte[] token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(token);
        byte[]? body = await this.keys.GetSessionKeysAsync(sessionId, mailboxId, ct).ConfigureAwait(false);
        byte[] wrapKey = MailKeys.SessionWrapKey(token);
        byte[]? packed = body is null ? null : MailKeys.Unwrap(body, wrapKey);
        CryptographicOperations.ZeroMemory(wrapKey);
        if (packed is null || packed.Length < 4 + 32)
        {
            return null;
        }
        int length = (packed[0] << 24) | (packed[1] << 16) | (packed[2] << 8) | packed[3];
        if (length <= 0 || 4 + length + 32 != packed.Length)
        {
            return null;
        }
        var result = new UnlockedKeys(packed.AsSpan(4, length).ToArray(), packed.AsSpan(4 + length, 32).ToArray());
        CryptographicOperations.ZeroMemory(packed);
        return result;
    }

    private static byte[] OpenDataKey(MailboxKeyRow row, byte[] priv)
    {
        using var ring = new MailKeyring();
        ring.AddPrivateKey(priv);
        return MailEnvelope.Open(row.DataKey, ring) ?? throw new CryptographicException("The data key does not open with this mailbox's key.");
    }

    private async Task WrapWithPasswordAsync(MailboxKeyRow row, byte[] priv, string password, CancellationToken ct)
    {
        row.UnlockSalt = MailKeys.NewSalt();
        row.PrivateByPassword = WrapWithUnlock(priv, password, row.UnlockSalt, this.pepper);
        await this.keys.SaveMailboxKeysAsync(row, ct).ConfigureAwait(false);
    }

    private static byte[] WrapWithUnlock(byte[] secret, string password, byte[] salt, byte[] pepper)
    {
        byte[] unlock = MailKeys.UnlockKey(password, salt, pepper);
        try
        {
            return MailKeys.Wrap(secret, unlock);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unlock);
        }
    }

    /// <summary>The webmail's key for mail waiting in Scheduled (DES-15 §7.4): derived from the pepper, never stored.</summary>
    /// <returns>32 bytes.</returns>
    public byte[] HoldKey() =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, this.pepper.Length > 0 ? this.pepper : new byte[32], 32, Array.Empty<byte>(), HoldInfo);

    // DES-15 §6.2: the key of the temporary copy of a new organisation's recovery key - derived from
    // the pepper, which is not in the database (in development, with no pepper, a fixed key).
    private byte[] TemporaryKey() =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, this.pepper.Length > 0 ? this.pepper : new byte[32], 32, Array.Empty<byte>(), TemporaryInfo);
}
