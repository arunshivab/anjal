namespace Anjal.Store;

/// <summary>
/// rc.28 (DES-19, item 74): one secure link - a mail kept on Anjal for one outside person, sealed with a
/// key made from the link's token (which Anjal does not keep), opened with a code mailed to that person.
/// </summary>
public sealed class SecureLinkRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The sender's mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>The person who signed in and sent it (the mailbox for a person; the member for a desk).</summary>
    public System.Guid? SentBy { get; set; }

    /// <summary>The outside address it was made for.</summary>
    public string Recipient { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the link's token, hex; the token itself is never kept.</summary>
    public string TokenSha256 { get; set; } = string.Empty;

    /// <summary>The mail as sent, sealed under the token's key (AES-256-GCM).</summary>
    public byte[] Sealed { get; set; } = System.Array.Empty<byte>();

    /// <summary>The sent mail's Message-ID, with its angle brackets.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>The sender's Sent copy, when one was kept.</summary>
    public System.Guid? SentCopyId { get; set; }

    /// <summary>When it was made.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it ends by itself (7 days after it was made).</summary>
    public System.DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the sender stopped it; null while it stands.</summary>
    public System.DateTimeOffset? StoppedAt { get; set; }

    /// <summary>When too many wrong codes stopped it; null while it stands.</summary>
    public System.DateTimeOffset? LockedAt { get; set; }

    /// <summary>When the notice mail went.</summary>
    public System.DateTimeOffset? MailedAt { get; set; }

    /// <summary>When a code was last mailed.</summary>
    public System.DateTimeOffset? LastCodeAt { get; set; }

    /// <summary>When the mail was first shown after a right code.</summary>
    public System.DateTimeOffset? OpenedAt { get; set; }

    /// <summary>When the outside person last replied.</summary>
    public System.DateTimeOffset? RepliedAt { get; set; }

    /// <summary>Wrong codes typed, in all.</summary>
    public int WrongCodes { get; set; }

    /// <summary>Replies sent through it.</summary>
    public int Replies { get; set; }

    /// <summary>When its sealed copy was removed (after it ended); null while kept.</summary>
    public System.DateTimeOffset? PurgedAt { get; set; }

    /// <summary>True while it can be opened at <paramref name="now"/>.</summary>
    /// <param name="now">The time.</param>
    /// <returns>True when it stands.</returns>
    public bool IsOpenAt(System.DateTimeOffset now) => this.StoppedAt is null && this.LockedAt is null && this.PurgedAt is null && now < this.ExpiresAt;
}

/// <summary>One code mailed for a secure link (rc.28, DES-19 §4).</summary>
public sealed class SecureLinkCodeRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The link.</summary>
    public System.Guid LinkId { get; set; }

    /// <summary>The code's keyed hash (HMAC-SHA256 under the link's token key), hex.</summary>
    public string CodeHmac { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public System.DateTimeOffset MadeAt { get; set; }

    /// <summary>When it was used; null while unused.</summary>
    public System.DateTimeOffset? UsedAt { get; set; }
}

/// <summary>rc.28 (DES-19): what secure links need from the store.</summary>
public interface ISecureLinkStore
{
    /// <summary>
    /// Take one try at a code, before the code is compared, so tries made at once are all counted: the
    /// link's count goes up by one only while it is under <paramref name="most"/> and the link is not
    /// locked or stopped.
    /// </summary>
    /// <param name="id">The link.</param>
    /// <param name="most">The tries allowed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count with this try, or null when no try is left.</returns>
    System.Threading.Tasks.Task<int?> TakeSecureCodeTryAsync(System.Guid id, int most, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Take the one reply of a visit that started at <paramref name="visitStart"/>: true only when the link
    /// has had no reply since then, so a visit (or its cookie, sent again) gives one reply.
    /// </summary>
    /// <param name="id">The link.</param>
    /// <param name="visitStart">When the visit's code was typed.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when this reply may go.</returns>
    System.Threading.Tasks.Task<bool> TakeSecureReplyAsync(System.Guid id, System.DateTimeOffset visitStart, System.DateTimeOffset now, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Mark that a code is being mailed, only when none was mailed since <paramref name="gapStart"/>, so two
    /// asks at once mail one code.
    /// </summary>
    /// <param name="id">The link.</param>
    /// <param name="now">Now.</param>
    /// <param name="gapStart">The last code must be older than this.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when this ask may mail a code.</returns>
    System.Threading.Tasks.Task<bool> TakeSecureCodeMailAsync(System.Guid id, System.DateTimeOffset now, System.DateTimeOffset gapStart, System.Threading.CancellationToken ct = default);

    /// <summary>Keep a new link.</summary>
    /// <param name="link">The link (its id is set when empty).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The link as kept.</returns>
    System.Threading.Tasks.Task<SecureLinkRow> AddSecureLinkAsync(SecureLinkRow link, System.Threading.CancellationToken ct = default);

    /// <summary>A link by its token's hash.</summary>
    /// <param name="tokenSha256">The hash, hex.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The link, or null.</returns>
    System.Threading.Tasks.Task<SecureLinkRow?> GetSecureLinkByTokenAsync(string tokenSha256, System.Threading.CancellationToken ct = default);

    /// <summary>A link by its id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The link, or null.</returns>
    System.Threading.Tasks.Task<SecureLinkRow?> GetSecureLinkAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>The links made for one sent mail, by its Message-ID.</summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="messageId">The Message-ID, with its angle brackets.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The links.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SecureLinkRow>> ListSecureLinksForMessageAsync(System.Guid mailboxId, string messageId, System.Threading.CancellationToken ct = default);

    /// <summary>Record what happened to a link: the given fields are set when not null; counts are added.</summary>
    /// <param name="id">The link.</param>
    /// <param name="change">What to set.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The link afterwards, or null when there is none.</returns>
    System.Threading.Tasks.Task<SecureLinkRow?> UpdateSecureLinkAsync(System.Guid id, SecureLinkChange change, System.Threading.CancellationToken ct = default);

    /// <summary>Keep a code mailed for a link.</summary>
    /// <param name="code">The code.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task AddSecureLinkCodeAsync(SecureLinkCodeRow code, System.Threading.CancellationToken ct = default);

    /// <summary>The codes of a link made since a time, newest first.</summary>
    /// <param name="linkId">The link.</param>
    /// <param name="since">From when.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The codes.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<SecureLinkCodeRow>> ListSecureLinkCodesAsync(System.Guid linkId, System.DateTimeOffset since, System.Threading.CancellationToken ct = default);

    /// <summary>Use a code once: true only for the first caller.</summary>
    /// <param name="codeId">The code.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when this call used it.</returns>
    System.Threading.Tasks.Task<bool> UseSecureLinkCodeAsync(System.Guid codeId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Remove the sealed copies of links that ended (expired, stopped or locked) before a time: the rows
    /// stay, for the sender's record, with no mail in them.
    /// </summary>
    /// <param name="endedBefore">Links ended before this.</param>
    /// <param name="now">The time recorded.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were removed.</returns>
    System.Threading.Tasks.Task<int> PurgeEndedSecureLinksAsync(System.DateTimeOffset endedBefore, System.DateTimeOffset now, System.Threading.CancellationToken ct = default);
}

/// <summary>What to change on a secure link (rc.28): a null field is left as it is.</summary>
public sealed class SecureLinkChange
{
    /// <summary>Stopped by the sender at.</summary>
    public System.DateTimeOffset? StoppedAt { get; set; }

    /// <summary>Locked by wrong codes at.</summary>
    public System.DateTimeOffset? LockedAt { get; set; }

    /// <summary>The notice mailed at.</summary>
    public System.DateTimeOffset? MailedAt { get; set; }

    /// <summary>A code mailed at.</summary>
    public System.DateTimeOffset? LastCodeAt { get; set; }

    /// <summary>Opened at (kept only the first time).</summary>
    public System.DateTimeOffset? OpenedAt { get; set; }

    /// <summary>Replied at.</summary>
    public System.DateTimeOffset? RepliedAt { get; set; }

    /// <summary>Wrong codes to add.</summary>
    public int AddWrongCodes { get; set; }

    /// <summary>Replies to add.</summary>
    public int AddReplies { get; set; }
}
