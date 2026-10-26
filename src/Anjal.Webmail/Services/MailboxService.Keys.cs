using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.25 (DES-15, organisation-held encryption): the webmail's side of the keys - opening a person's
/// mail at sign-in, a new password, moving the mail stored before encryption, re-keying what
/// arrived since the last sign-in, and whether this request can open a mailbox at all.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>How many rows the session's housekeeping takes at a time.</summary>
    private const int HousekeepingBatch = 200;

    /// <summary>The key operations; null when the store keeps no keys.</summary>
    public MailKeyManager? KeyManager { get; private set; }

    /// <summary>Whether an organisation's mailbox has keys but this request cannot open it (signed out of it, or locked waiting for a restore).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it is locked here.</returns>
    public async Task<bool> MailLockedHereAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.KeyManager is MailKeyManager km && !MailKeyring.Current.Has(mailboxId) &&
        await km.Store.GetMailboxKeysAsync(mailboxId, ct).ConfigureAwait(false) is not null;

    /// <summary>Why a person's own mail is locked: "waiting" (for an administrator's restore), "restored" (the password now opens it), or "password".</summary>
    /// <param name="mailboxId">The person's mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    public async Task<string> LockStateAsync(Guid mailboxId, CancellationToken ct = default)
    {
        if (this.KeyManager is not MailKeyManager km || await km.Store.GetRestoreRequestAsync(mailboxId, ct).ConfigureAwait(false) is not RestoreRequestRow restore)
        {
            return "password";
        }
        return restore.Granted is null ? "waiting" : "restored";
    }

    /// <summary>
    /// Open a person's mail at sign-in (the password already checked). Made now if the mailbox has no
    /// keys yet; then the mail already stored is moved in the background (the move, DES-15 §11).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="password">The password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<UnlockResult> UnlockAtSignInAsync(Guid mailboxId, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (this.KeyManager is not MailKeyManager km || await this.store.GetMailboxByIdAsync(mailboxId, ct).ConfigureAwait(false) is not MailboxRow mailbox)
        {
            return new UnlockResult(UnlockOutcome.NotEncrypted, null);
        }
        bool waitingBefore = await km.Store.GetRestoreRequestAsync(mailboxId, ct).ConfigureAwait(false) is not null;
        UnlockResult result = await km.UnlockAsync(mailbox, password, ct).ConfigureAwait(false);
        if (result.Outcome == UnlockOutcome.Created)
        {
            this.ForgetKeyCache(mailbox.Address);
        }
        else if (result.Outcome == UnlockOutcome.Locked && !waitingBefore && await km.Store.GetRestoreRequestAsync(mailboxId, ct).ConfigureAwait(false) is not null)
        {
            await this.TellAdminsRestoreWaitingAsync(mailbox, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// The session's housekeeping for a mailbox just opened: mail still stored plain (from before
    /// encryption, or delivered in the minute before the server learnt the key) is sealed, and mail
    /// that arrived for the RSA key is re-keyed to the data key, file and row (DES-15 §5.3, §11).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="keys">Its keys, open.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many mails were sealed or re-keyed.</returns>
    public async Task<int> HousekeepAsync(Guid mailboxId, UnlockedKeys keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (this.KeyManager is not MailKeyManager km || await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow Tenant, MailboxRow Mailbox) context)
        {
            return 0;
        }
        var folders = (await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false)).ToDictionary(f => f.Id, f => f.Name);
        using var ring = new MailKeyring();
        ring.Add(mailboxId, keys.PrivateKey, keys.DataKey);
        int done = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool plain = pass == 0;
            // A row that cannot be sealed or re-keyed (damaged, or not Anjal's) comes back in every batch:
            // each row is handled once per run, and the run stops when a batch brings nothing new.
            var handled = new HashSet<Guid>();
            while (true)
            {
                IReadOnlyList<StoredMessageText> batch = await km.Store.ListStoredTextsAsync(mailboxId, plain, HousekeepingBatch, ct).ConfigureAwait(false);
                if (batch.Count == 0 || batch.All(r => handled.Contains(r.Id)))
                {
                    break;
                }
                foreach (StoredMessageText row in batch)
                {
                    if (!handled.Add(row.Id))
                    {
                        continue;
                    }
                    if (folders.TryGetValue(row.FolderId, out string? folder) && this.maildir is MaildirStore files)
                    {
                        await SealOrRekeyFileAsync(files, context.Tenant.Slug, context.Mailbox.Address, folder, row.MaildirFile, ring, keys.DataKey, ct).ConfigureAwait(false);
                    }
                    StoredText text = plain
                        ? SealPlainFields(row, keys.DataKey)
                        : new StoredText(
                            MailEnvelope.RekeyField(row.Subject, ring, keys.DataKey),
                            MailEnvelope.RekeyField(row.BodyText, ring, keys.DataKey),
                            row.Snippet.Length == 0 ? row.Snippet : MailEnvelope.RekeyField(row.Snippet, ring, keys.DataKey));
                    await km.Store.SaveStoredTextAsync(row.Id, text, ct).ConfigureAwait(false);
                    done++;
                }
                if (batch.Count < HousekeepingBatch)
                {
                    break;
                }
            }
        }
        using (MailKeyring.Use(ring))
        {
            done += await this.SealPlainDocumentsAsync(mailboxId, ct).ConfigureAwait(false);
        }
        return done;
    }

    /// <summary>Forget what this process knows of an address's key (its keys were just made).</summary>
    /// <param name="address">The address.</param>
    public void ForgetKeyCache(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (this.maildir is MaildirStore files && files.Keys is MailboxKeyDirectory directory)
        {
            directory.Forget(address);
        }
    }

    private void InitKeys()
    {
        if (this.store is not IMailKeyStore keyStore)
        {
            return;
        }
        this.KeyManager = new MailKeyManager(keyStore, this.store, MailKeys.PepperFromEnvironment());
        if (this.maildir is MaildirStore files && files.Keys is null)
        {
            files.Keys = new MailboxKeyDirectory(this.store, keyStore);
            files.HoldKey = this.KeyManager.HoldKey();
        }
    }

    // DES-15 §7.4: the scope in which mail waiting in Scheduled can be read - the webmail's hold key
    // added to whatever keys the request already has.
    private IDisposable UseHoldKey()
    {
        if (this.KeyManager is not MailKeyManager km)
        {
            return new NoScope();
        }
        var ring = new MailKeyring(MailKeyring.Current);
        ring.AddSymmetricKey(km.HoldKey());
        return new HoldScope(MailKeyring.Use(ring), ring);
    }

    // The texts of a row stored before encryption, each sealed unless it already is.
    private static StoredText SealPlainFields(StoredMessageText row, byte[] dataKey)
    {
        string body = MessageCipher.IsSealed(row.BodyText) ? row.BodyText : MailEnvelope.FieldWithKey(row.BodyText, dataKey);
        string snippet = MessageCipher.IsSealed(row.Snippet) ? row.Snippet
            : MailEnvelope.FieldWithKey(MessageCipher.IsSealed(row.BodyText) ? string.Empty : (row.BodyText.Length > MessageCipher.SnippetLength ? row.BodyText[..MessageCipher.SnippetLength] : row.BodyText), dataKey);
        return new StoredText(MessageCipher.IsSealed(row.Subject) ? row.Subject : MailEnvelope.FieldWithKey(row.Subject, dataKey), body, snippet);
    }

    // A message moved out of Scheduled (back to Drafts) is sealed to its mailbox again.
    private async Task ResealFromHoldAsync(Guid mailboxId, Guid messageId, CancellationToken ct)
    {
        if (this.maildir is not MaildirStore files || files.Keys is null ||
            await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow Tenant, MailboxRow Mailbox) context ||
            await this.store.GetMessageByIdAsync(messageId, ct).ConfigureAwait(false) is not MessageRow row)
        {
            return;
        }
        FolderRow? folder = (await this.store.ListFoldersAsync(mailboxId, ct).ConfigureAwait(false)).FirstOrDefault(f => f.Id == row.FolderId);
        byte[]? stored = folder is null ? null : await files.ReadStoredAsync(context.Tenant.Slug, context.Mailbox.Address, folder.Name, row.MaildirFile, ct).ConfigureAwait(false);
        if (folder is null || stored is null || MailEnvelope.Describe(stored)?.Kind != MailEnvelope.KindSymmetric ||
            await files.Keys.PublicKeyAsync(context.Tenant.Slug, context.Mailbox.Address, ct).ConfigureAwait(false) is not byte[] publicKey)
        {
            return;
        }
        using IDisposable hold = this.UseHoldKey();
        if (MailEnvelope.Open(stored, MailKeyring.Current) is byte[] plain && MailEnvelope.Describe(stored)?.KeyId == MailKeys.SymmetricKeyIdText(this.KeyManager!.HoldKey()))
        {
            await files.ReplaceStoredAsync(context.Tenant.Slug, context.Mailbox.Address, folder.Name, row.MaildirFile, MailEnvelope.SealToPublicKey(plain, publicKey), ct).ConfigureAwait(false);
        }
    }

    private sealed class HoldScope(IDisposable scope, MailKeyring ring) : IDisposable
    {
        public void Dispose()
        {
            scope.Dispose();
            ring.Dispose();
        }
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // A new password (DES-15 §4, §6.1): with the mail open in this request, the key is wrapped with
    // the new password; without (forgotten, reset by an administrator), the mail is locked and a
    // restore request made; a mailbox that has no keys yet gets them now.
    private async Task KeysAfterPasswordAsync(MailboxRow mailbox, string newPassword, CancellationToken ct)
    {
        if (this.KeyManager is not MailKeyManager km)
        {
            return;
        }
        if (await km.Store.GetMailboxKeysAsync(mailbox.Id, ct).ConfigureAwait(false) is null)
        {
            if (await km.CreateMailboxKeysAsync(mailbox, newPassword, ct).ConfigureAwait(false) is not null)
            {
                this.ForgetKeyCache(mailbox.Address);
            }
            return;
        }
        MailKeyring ring = MailKeyring.Current;
        if (ring.PrivateKeyOf(mailbox.Id) is byte[] priv && ring.DataKey(mailbox.Id) is byte[] dataKey)
        {
            await km.RewrapAsync(mailbox.Id, new UnlockedKeys(priv, dataKey), newPassword, ct).ConfigureAwait(false);
            return;
        }
        if (await km.StartRestoreAsync(mailbox.Id, newPassword, passwordReset: true, ct).ConfigureAwait(false))
        {
            await this.TellAdminsRestoreWaitingAsync(mailbox, ct).ConfigureAwait(false);
        }
    }

    private static async Task SealOrRekeyFileAsync(MaildirStore files, string tenantSlug, string address, string folder, string relativePath, MailKeyring ring, byte[] dataKey, CancellationToken ct)
    {
        byte[]? stored = await files.ReadStoredAsync(tenantSlug, address, folder, relativePath, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }
        byte[]? replacement = null;
        if (!MailEnvelope.IsEnvelope(stored))
        {
            replacement = MailEnvelope.SealWithKey(stored, dataKey);
        }
        else if (MailEnvelope.Describe(stored)?.Kind == MailEnvelope.KindPublicKey)
        {
            replacement = MailEnvelope.Rekey(stored, ring, dataKey);
        }
        if (replacement is not null)
        {
            await files.ReplaceStoredAsync(tenantSlug, address, folder, relativePath, replacement, ct).ConfigureAwait(false);
        }
    }
}
