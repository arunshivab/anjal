namespace Anjal.Store;

/// <summary>
/// rc.28 (DES-20 §2): a password made for one mail app on one device. It reads and sends mail only; when
/// the mailbox is encrypted, it opens its own copy of the mail key, which is deleted with it.
/// </summary>
public sealed class AppPasswordRow
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>The device's name, as the person gave it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The password's PBKDF2 hash, as account passwords are kept.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The salt of the key made from the app password.</summary>
    public byte[] UnlockSalt { get; set; } = System.Array.Empty<byte>();

    /// <summary>The mail key wrapped with the app password's key; null for a mailbox that is not encrypted.</summary>
    public byte[]? PrivateByApp { get; set; }

    /// <summary>When it was made.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When an app last signed in with it.</summary>
    public System.DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>From where (an address on the internet).</summary>
    public string LastUsedFrom { get; set; } = string.Empty;

    /// <summary>When it was removed; null while it works.</summary>
    public System.DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>rc.28 (DES-20 §3): a folder's IMAP numbers - its UIDVALIDITY and the UID each message has.</summary>
/// <param name="UidValidity">The folder's UIDVALIDITY.</param>
/// <param name="UidNext">The next UID to be given.</param>
/// <param name="Uids">Each message in the folder with its UID, in UID order.</param>
public sealed record ImapFolderNumbers(long UidValidity, long UidNext, System.Collections.Generic.IReadOnlyList<(System.Guid MessageId, long Uid)> Uids);

/// <summary>rc.28 (DES-20 §5): one job bringing old mail in.</summary>
public sealed class ImportJobRow
{
    /// <summary>A file the person uploaded.</summary>
    public const string FileKind = "file";

    /// <summary>A copy from another mail server.</summary>
    public const string ServerKind = "server";

    /// <summary>Waiting to start.</summary>
    public const string Waiting = "waiting";

    /// <summary>Running.</summary>
    public const string Running = "running";

    /// <summary>Finished.</summary>
    public const string Done = "done";

    /// <summary>Stopped by a problem (see <see cref="Error"/>).</summary>
    public const string Failed = "failed";

    /// <summary>Stopped by the person.</summary>
    public const string Stopped = "stopped";

    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary><see cref="FileKind"/> or <see cref="ServerKind"/>.</summary>
    public string Kind { get; set; } = FileKind;

    /// <summary>For a file, its path on the server; for a server, "host:port account".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>For a server, its password sealed to the person's key; deleted when the job ends.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>The folder to bring mail into; empty for one folder per old folder under "Imported".</summary>
    public string TargetFolder { get; set; } = string.Empty;

    /// <summary>For a server, the folders chosen, separated by new lines.</summary>
    public string Folders { get; set; } = string.Empty;

    /// <summary>Where it got to, so it can go on after a restart (the job's own words).</summary>
    public string Cursor { get; set; } = string.Empty;

    /// <summary>The state.</summary>
    public string State { get; set; } = Waiting;

    /// <summary>Messages seen so far.</summary>
    public int Seen { get; set; }

    /// <summary>Messages brought in.</summary>
    public int Imported { get; set; }

    /// <summary>Messages skipped (already there, too large, a virus or a program).</summary>
    public int Skipped { get; set; }

    /// <summary>All the messages there are, once known; 0 until then.</summary>
    public int Total { get; set; }

    /// <summary>Why it failed.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>When it was made.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it last changed.</summary>
    public System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>rc.28 (DES-20): what mail apps and import need from the store.</summary>
public interface IMailAppStore
{
    /// <summary>Keep a new app password.</summary>
    /// <param name="row">The row (its id is set when empty).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row as kept.</returns>
    System.Threading.Tasks.Task<AppPasswordRow> AddAppPasswordAsync(AppPasswordRow row, System.Threading.CancellationToken ct = default);

    /// <summary>A mailbox's app passwords that still work, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<AppPasswordRow>> ListAppPasswordsAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>Remove an app password: it stops at once and its key copy is deleted.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="id">The app password.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it was this mailbox's and still worked.</returns>
    System.Threading.Tasks.Task<bool> RemoveAppPasswordAsync(System.Guid mailboxId, System.Guid id, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>Note a sign-in with an app password.</summary>
    /// <param name="id">The app password.</param>
    /// <param name="at">When.</param>
    /// <param name="from">From where.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task TouchAppPasswordAsync(System.Guid id, System.DateTimeOffset at, string from, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// A folder's IMAP numbers: UIDVALIDITY made the first time, and a UID given to each message that has
    /// none in this folder (in the order they arrived), atomically.
    /// </summary>
    /// <param name="folderId">The folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The numbers.</returns>
    System.Threading.Tasks.Task<ImapFolderNumbers> AssignImapUidsAsync(System.Guid folderId, System.Threading.CancellationToken ct = default);

    /// <summary>Give a message the time it arrived at first (mail imported, or appended by an app with its date).</summary>
    /// <param name="messageId">The message.</param>
    /// <param name="at">When it arrived.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task SetReceivedAtAsync(System.Guid messageId, System.DateTimeOffset at, System.Threading.CancellationToken ct = default);

    /// <summary>Keep a new import job.</summary>
    /// <param name="job">The job.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The job as kept.</returns>
    System.Threading.Tasks.Task<ImportJobRow> AddImportJobAsync(ImportJobRow job, System.Threading.CancellationToken ct = default);

    /// <summary>One import job.</summary>
    /// <param name="id">The job.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The job, or null.</returns>
    System.Threading.Tasks.Task<ImportJobRow?> GetImportJobAsync(System.Guid id, System.Threading.CancellationToken ct = default);

    /// <summary>A mailbox's import jobs, newest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The jobs.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ImportJobRow>> ListImportJobsAsync(System.Guid mailboxId, System.Threading.CancellationToken ct = default);

    /// <summary>Jobs waiting or running (to go on after a restart), oldest first.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The jobs.</returns>
    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<ImportJobRow>> ListOpenImportJobsAsync(System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Take a job to run: true only for the one caller that moved it from waiting (or from running, when
    /// it has not changed since <paramref name="staleBefore"/> - a run stopped by a restart) to running.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="staleBefore">A running job not touched since then may be taken again.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when this caller runs it.</returns>
    System.Threading.Tasks.Task<bool> ClaimImportJobAsync(System.Guid id, System.DateTimeOffset staleBefore, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// A job still waiting for its person to choose folders since before <paramref name="before"/> is stopped
    /// and its sealed password emptied (the person left the page).
    /// </summary>
    /// <param name="before">Made before this.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were stopped.</returns>
    System.Threading.Tasks.Task<int> ExpireChoosingImportJobsAsync(System.DateTimeOffset before, System.Threading.CancellationToken ct = default);

    /// <summary>Save a job's state, counts, cursor, error and secret. A job the person stopped stays stopped: only another "stopped" is saved over it, so a running import cannot undo Stop.</summary>
    /// <param name="job">The job.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    System.Threading.Tasks.Task SaveImportJobAsync(ImportJobRow job, System.Threading.CancellationToken ct = default);
}
