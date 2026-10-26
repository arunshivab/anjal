namespace Anjal.Store;

/// <summary>rc.25 (DES-15 §2, §10): a mailbox's keys as stored - nothing in it opens anything alone.</summary>
public sealed class MailboxKeyRow
{
    /// <summary>The mailbox.</summary>
    public Guid MailboxId { get; set; }

    /// <summary>The RSA public key (SubjectPublicKeyInfo): arriving mail is sealed to it.</summary>
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    /// <summary>The salt of the person's unlock key.</summary>
    public byte[] UnlockSalt { get; set; } = Array.Empty<byte>();

    /// <summary>The private key wrapped with the unlock key (password + salt + pepper); null until the person has a password here.</summary>
    public byte[]? PrivateByPassword { get; set; }

    /// <summary>The private key sealed to the organisation's recovery key.</summary>
    public byte[] PrivateByOrg { get; set; } = Array.Empty<byte>();

    /// <summary>The data key sealed to this mailbox's public key.</summary>
    public byte[] DataKey { get; set; } = Array.Empty<byte>();

    /// <summary>When made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>rc.25 (DES-15 §6): an organisation's recovery key and its break-glass copy.</summary>
public sealed class TenantKeyRow
{
    /// <summary>The organisation.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The recovery public key: every mailbox private key of the organisation is also sealed to it.</summary>
    public byte[] RecoveryPublic { get; set; } = Array.Empty<byte>();

    /// <summary>The recovery private key wrapped with the key derived from the recovery secret (the sheet).</summary>
    public byte[] RecoveryBySecret { get; set; } = Array.Empty<byte>();

    /// <summary>A check of the recovery secret (its symmetric key id), to tell a typed sheet is right.</summary>
    public string SecretCheck { get; set; } = string.Empty;

    /// <summary>When the administrator confirmed the sheet by typing it back; null until then.</summary>
    public DateTimeOffset? SheetConfirmedAt { get; set; }

    /// <summary>Whether the break-glass key may recover this organisation (D-240: on by default).</summary>
    public bool BreakGlassOn { get; set; } = true;

    /// <summary>The recovery secret sealed to the break-glass public key; null when switched off.</summary>
    public byte[]? SecretByBreakGlass { get; set; }

    /// <summary>
    /// The recovery private key wrapped with a key derived from the pepper, only until an administrator
    /// has the key and has confirmed a recovery sheet (DES-15 §6.2); null after.
    /// </summary>
    public byte[]? RecoveryByServer { get; set; }

    /// <summary>When made.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A key given to a holder (an administrator's copy of the recovery key; a member's copy of a shared mailbox key).</summary>
/// <param name="KeyOwner">Whose key: the organisation or the shared mailbox.</param>
/// <param name="HolderId">The holder's mailbox.</param>
/// <param name="Kind"><see cref="KeyGrant.OrgAdmin"/> or <see cref="KeyGrant.SharedMember"/>.</param>
/// <param name="Body">The private key sealed to the holder's public key.</param>
public sealed record KeyGrant(Guid KeyOwner, Guid HolderId, string Kind, byte[] Body)
{
    /// <summary>An administrator's copy of the organisation's recovery key.</summary>
    public const string OrgAdmin = "org-admin";

    /// <summary>A member's copy of a shared mailbox's private key.</summary>
    public const string SharedMember = "shared-member";
}

/// <summary>A restore request (DES-15 §6.1): made when a person sets a new password without the old one.</summary>
public sealed class RestoreRequestRow
{
    /// <summary>The mailbox.</summary>
    public Guid MailboxId { get; set; }

    /// <summary>The one-time restore public key.</summary>
    public byte[] RestorePublic { get; set; } = Array.Empty<byte>();

    /// <summary>The restore private key wrapped with the person's new unlock key.</summary>
    public byte[] RestoreByUnlock { get; set; } = Array.Empty<byte>();

    /// <summary>The salt of that unlock key.</summary>
    public byte[] UnlockSalt { get; set; } = Array.Empty<byte>();

    /// <summary>The mailbox private key sealed to the restore public key, once an administrator restored it.</summary>
    public byte[]? Granted { get; set; }

    /// <summary>Who restored it.</summary>
    public string GrantedBy { get; set; } = string.Empty;

    /// <summary>When.</summary>
    public DateTimeOffset? GrantedAt { get; set; }

    /// <summary>When the request was made.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The break-glass key as kept on the server: its public half, and its private half wrapped with a secret that exists only as paper shares.</summary>
/// <param name="PublicKey">The public key.</param>
/// <param name="PrivateBySecret">The private key wrapped with the break-glass secret.</param>
/// <param name="CreatedAt">When made.</param>
public sealed record BreakGlassRow(byte[] PublicKey, byte[] PrivateBySecret, DateTimeOffset CreatedAt);

/// <summary>rc.25 (DES-15 §10): where the keys are stored.</summary>
public interface IMailKeyStore
{
    /// <summary>A mailbox's keys, or null when the mailbox has none (individuals; before the move).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row.</returns>
    Task<MailboxKeyRow?> GetMailboxKeysAsync(Guid mailboxId, CancellationToken ct = default);

    /// <summary>
    /// Change a mailbox's keys (a new password, a restore). The public key never changes: a row whose
    /// public key differs from the stored one is refused (<see cref="InvalidOperationException"/>).
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default);

    /// <summary>
    /// Store a mailbox's first keys, unless it has keys already (two first sign-ins at once): the
    /// keys stored first win, and false says so - the caller then reads them.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether these keys were stored.</returns>
    Task<bool> AddMailboxKeysAsync(MailboxKeyRow row, CancellationToken ct = default);

    /// <summary>The public keys of many mailboxes at once (null entries are absent).</summary>
    /// <param name="mailboxIds">The mailboxes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Mailbox to public key.</returns>
    Task<IReadOnlyDictionary<Guid, byte[]>> GetPublicKeysAsync(IReadOnlyCollection<Guid> mailboxIds, CancellationToken ct = default);

    /// <summary>An organisation's keys, or null.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row.</returns>
    Task<TenantKeyRow?> GetTenantKeysAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Change an organisation's keys (a sheet, the break-glass switch). The recovery public key never
    /// changes: a row whose recovery public key differs from the stored one is refused.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default);

    /// <summary>Store an organisation's first keys, unless it has keys already; false when another request stored them first.</summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether these keys were stored.</returns>
    Task<bool> AddTenantKeysAsync(TenantKeyRow row, CancellationToken ct = default);

    /// <summary>The keys given to a holder.</summary>
    /// <param name="holderId">The holder's mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The grants.</returns>
    Task<IReadOnlyList<KeyGrant>> ListGrantsForHolderAsync(Guid holderId, CancellationToken ct = default);

    /// <summary>The holders of one key.</summary>
    /// <param name="keyOwner">The key's owner.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The grants.</returns>
    Task<IReadOnlyList<KeyGrant>> ListGrantsOfAsync(Guid keyOwner, string kind, CancellationToken ct = default);

    /// <summary>Give a key to a holder (replacing an earlier grant).</summary>
    /// <param name="grant">The grant.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveGrantAsync(KeyGrant grant, CancellationToken ct = default);

    /// <summary>Take a key from a holder.</summary>
    /// <param name="keyOwner">The key's owner.</param>
    /// <param name="holderId">The holder.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DeleteGrantAsync(Guid keyOwner, Guid holderId, string kind, CancellationToken ct = default);

    /// <summary>Keep a session's wrapped keys.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="body">The keys wrapped with the session cookie's value.</param>
    /// <param name="expiresAt">When they go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveSessionKeysAsync(string sessionId, Guid mailboxId, byte[] body, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>A session's wrapped keys, or null (gone, expired, another mailbox's).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The body.</returns>
    Task<byte[]?> GetSessionKeysAsync(string sessionId, Guid mailboxId, CancellationToken ct = default);

    /// <summary>Forget a session's keys (sign-out).</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DeleteSessionKeysAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Forget every session's keys of a mailbox (sign out everywhere; a new password).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many went.</returns>
    Task<int> DeleteMailboxSessionKeysAsync(Guid mailboxId, CancellationToken ct = default);

    /// <summary>Remove expired session keys.</summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many went.</returns>
    Task<int> PurgeSessionKeysAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>A mailbox's restore request, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row.</returns>
    Task<RestoreRequestRow?> GetRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default);

    /// <summary>The restore requests waiting in an organisation (not yet granted).</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<RestoreRequestRow>> ListWaitingRestoresAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Insert or replace a restore request.</summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveRestoreRequestAsync(RestoreRequestRow row, CancellationToken ct = default);

    /// <summary>Remove a restore request (used, or no longer needed).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DeleteRestoreRequestAsync(Guid mailboxId, CancellationToken ct = default);

    /// <summary>
    /// A mailbox's message texts exactly as stored, for the session's housekeeping: the rows still
    /// plain (to be sealed when the mailbox gets keys - the move), or the rows that arrived for the
    /// RSA key (to be re-keyed to the data key, DES-15 §5.3).
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="plain">True for plain rows; false for rows not yet re-keyed.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<StoredMessageText>> ListStoredTextsAsync(Guid mailboxId, bool plain, int limit, CancellationToken ct = default);

    /// <summary>Replace a message's stored text.</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="text">The stored text.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveStoredTextAsync(Guid messageId, StoredText text, CancellationToken ct = default);

    /// <summary>The break-glass key's server half, or null when none was made.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row.</returns>
    Task<BreakGlassRow?> GetBreakGlassAsync(CancellationToken ct = default);

    /// <summary>Keep the break-glass key's server half (made once, offline).</summary>
    /// <param name="row">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveBreakGlassAsync(BreakGlassRow row, CancellationToken ct = default);
}

/// <summary>
/// rc.25 (DES-15 §6.3, OPS-16): the one line that carries Anjal's break-glass key to the server's
/// settings (<c>ANJAL_BREAKGLASS_KEY</c>): its public half, and its private half wrapped with the
/// secret that exists only as the three paper shares. The line alone opens nothing.
/// <code>anjal-breakglass-v1.&lt;public, base64url&gt;.&lt;wrapped private, base64url&gt;</code>
/// </summary>
public static class BreakGlassLine
{
    /// <summary>The setting that holds the line.</summary>
    public const string Setting = "ANJAL_BREAKGLASS_KEY";

    private const string Prefix = "anjal-breakglass-v1.";

    /// <summary>Write the line.</summary>
    /// <param name="publicKey">The public key.</param>
    /// <param name="privateBySecret">The private key wrapped with the shared secret.</param>
    /// <returns>The line.</returns>
    public static string Write(byte[] publicKey, byte[] privateBySecret)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(privateBySecret);
        return Prefix + MailEnvelope.Base64Url(publicKey) + "." + MailEnvelope.Base64Url(privateBySecret);
    }

    /// <summary>Read the line; null when it is not one.</summary>
    /// <param name="line">The text.</param>
    /// <returns>The public key and the wrapped private key, or null.</returns>
    public static (byte[] PublicKey, byte[] PrivateBySecret)? Read(string? line)
    {
        string text = (line ?? string.Empty).Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }
        string[] parts = text[Prefix.Length..].Split('.');
        if (parts.Length != 2)
        {
            return null;
        }
        try
        {
            return (MailEnvelope.FromBase64Url(parts[0]), MailEnvelope.FromBase64Url(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
