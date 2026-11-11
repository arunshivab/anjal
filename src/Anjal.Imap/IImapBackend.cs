namespace Anjal.Imap;

/// <summary>A folder as a mail app sees it (rc.28, DES-20 §3).</summary>
/// <param name="Name">Its name in Anjal.</param>
/// <param name="SpecialUse">"\Sent", "\Drafts", "\Trash", "\Junk", "\Archive", or empty.</param>
public sealed record ImapFolder(string Name, string SpecialUse);

/// <summary>One message in an open folder.</summary>
public sealed class ImapMessage
{
    /// <summary>Its id in Anjal.</summary>
    public Guid Id { get; init; }

    /// <summary>Its UID in this folder.</summary>
    public long Uid { get; init; }

    /// <summary>Read.</summary>
    public bool Seen { get; set; }

    /// <summary>Flagged.</summary>
    public bool Flagged { get; set; }

    /// <summary>Answered.</summary>
    public bool Answered { get; set; }

    /// <summary>Marked for EXPUNGE in this session only.</summary>
    public bool Deleted { get; set; }

    /// <summary>When it arrived.</summary>
    public DateTimeOffset Received { get; init; }
}

/// <summary>A folder opened: its numbers and its messages in UID order.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="UidValidity">UIDVALIDITY.</param>
/// <param name="UidNext">UIDNEXT.</param>
/// <param name="Messages">The messages.</param>
public sealed record ImapView(ImapFolder Folder, long UidValidity, long UidNext, IReadOnlyList<ImapMessage> Messages);

/// <summary>The outcome of a sign-in.</summary>
/// <param name="Account">The account, or null when refused.</param>
/// <param name="Refusal">Why it was refused (for the app's user), or null.</param>
public sealed record ImapLogin(IImapAccount? Account, string? Refusal);

/// <summary>rc.28 (DES-20): where the IMAP server finds people and their mail.</summary>
public interface IImapBackend
{
    /// <summary>Sign in with an address and an app password.</summary>
    /// <param name="user">The address.</param>
    /// <param name="password">The app password.</param>
    /// <param name="remoteAddress">The app's address on the internet.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The account, or why not.</returns>
    Task<ImapLogin> LoginAsync(string user, string password, string remoteAddress, CancellationToken ct = default);
}

/// <summary>One signed-in person's mail, for one IMAP session. Every call runs with their keys open.</summary>
public interface IImapAccount : IDisposable
{
    /// <summary>The address signed in.</summary>
    string Address { get; }

    /// <summary>
    /// Whether the session may go on: the app password not removed, the mailbox and its organisation not
    /// switched off. Asked before every command and while idling; it may answer from a recent look.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>False when the session must end.</returns>
    Task<bool> StillAllowedAsync(CancellationToken ct = default);

    /// <summary>The folders.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The folders.</returns>
    Task<IReadOnlyList<ImapFolder>> FoldersAsync(CancellationToken ct = default);

    /// <summary>Open a folder; null when there is none.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The view.</returns>
    Task<ImapView?> OpenAsync(string name, CancellationToken ct = default);

    /// <summary>A message's bytes, opened.</summary>
    /// <param name="id">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes, or null.</returns>
    Task<byte[]?> ReadAsync(Guid id, CancellationToken ct = default);

    /// <summary>Set a message's flags.</summary>
    /// <param name="id">The message.</param>
    /// <param name="seen">Read.</param>
    /// <param name="flagged">Flagged.</param>
    /// <param name="answered">Answered.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SetFlagsAsync(Guid id, bool seen, bool flagged, bool answered, CancellationToken ct = default);

    /// <summary>Copy a message to another folder.</summary>
    /// <param name="id">The message.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The copy's id, or null.</returns>
    Task<Guid?> CopyAsync(Guid id, string folder, CancellationToken ct = default);

    /// <summary>Move a message to another folder.</summary>
    /// <param name="id">The message.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when moved.</returns>
    Task<bool> MoveAsync(Guid id, string folder, CancellationToken ct = default);

    /// <summary>Delete a message for good (EXPUNGE).</summary>
    /// <param name="id">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when deleted.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Add a message to a folder (APPEND).</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="raw">The message.</param>
    /// <param name="seen">Read.</param>
    /// <param name="flagged">Flagged.</param>
    /// <param name="answered">Answered.</param>
    /// <param name="arrived">Its date, or null for now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The message's id, or null when refused; and why.</returns>
    Task<(Guid? Id, string? Refusal)> AppendAsync(string folder, byte[] raw, bool seen, bool flagged, bool answered, DateTimeOffset? arrived, CancellationToken ct = default);

    /// <summary>Make a folder.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    Task<string?> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>Remove an empty folder of the person's own.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    Task<string?> RemoveAsync(string name, CancellationToken ct = default);

    /// <summary>Rename a folder of the person's own.</summary>
    /// <param name="oldName">The old name.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Why not, or null.</returns>
    Task<string?> RenameAsync(string oldName, string newName, CancellationToken ct = default);
}
