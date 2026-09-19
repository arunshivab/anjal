namespace Anjal.Mailbox;

/// <summary>
/// Result of writing one message into a Maildir folder.
/// </summary>
public sealed class MaildirWriteResult
{
    /// <summary>Path of the written file relative to the folder's Maildir directory (e.g. <c>new/1726560000.M1P42Q7.host</c>).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Absolute path of the written file.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Size of the written file in bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>
/// Filesystem side of mailbox storage. Lays out one Maildir per mailbox at
/// <c>&lt;root&gt;/&lt;tenant-slug&gt;/&lt;local-part&gt;@&lt;domain&gt;/</c>
/// with the standard <c>tmp/</c>, <c>new/</c>, <c>cur/</c> triple, and
/// Maildir++ <c>.Folder/</c> subdirectories for folders other than INBOX.
/// Writes are crash-safe in the Maildir sense: the file is fully written
/// and flushed under <c>tmp/</c> and then atomically renamed into
/// <c>new/</c>, so a reader never sees a partial message.
/// </summary>
public interface IMaildirStore
{
    /// <summary>The root directory under which all tenants live.</summary>
    string Root { get; }

    /// <summary>
    /// Ensure the Maildir for a mailbox folder exists (creating
    /// <c>tmp/new/cur</c> as needed). Returns the folder's Maildir directory.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>), used as the directory name.</param>
    /// <param name="folderName">Folder name; <c>INBOX</c> is the Maildir root.</param>
    string EnsureFolder(string tenantSlug, string address, string folderName);

    /// <summary>
    /// Write a message into a folder's <c>new/</c> directory.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="folderName">Folder name; <c>INBOX</c> is the Maildir root.</param>
    /// <param name="rawBytes">The RFC 5322 message bytes.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<MaildirWriteResult> WriteAsync(string tenantSlug, string address, string folderName, byte[] rawBytes, System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Read a message previously written. Returns <see langword="null"/>
    /// if the file no longer exists.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="folderName">Folder name.</param>
    /// <param name="relativePath">The <see cref="MaildirWriteResult.RelativePath"/> recorded at write time.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<byte[]?> ReadAsync(string tenantSlug, string address, string folderName, string relativePath, System.Threading.CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IMaildirStore"/> backed by <see cref="System.IO"/>.
/// </summary>
public sealed class MaildirStore : IMaildirStore
{
    private static readonly char[] InvalidPathChars = System.IO.Path.GetInvalidFileNameChars();
    private static int sequence;
    private readonly string hostName;

    /// <summary>
    /// Construct with the root directory. The directory is created on first
    /// write if it does not already exist.
    /// </summary>
    /// <param name="root">Absolute root path, e.g. <c>/var/mail/anjal</c>.</param>
    /// <param name="hostName">Host name embedded in Maildir file names. Defaults to the machine name.</param>
    public MaildirStore(string root, string? hostName = null)
    {
        System.ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new System.ArgumentException("Maildir root must not be empty.", nameof(root));
        }
        this.Root = System.IO.Path.GetFullPath(root);
        string host = string.IsNullOrWhiteSpace(hostName) ? System.Environment.MachineName : hostName!;
        // Maildir file names use '.' and ':' as separators; '/' is a path separator.
        this.hostName = host.Replace('/', '_').Replace(':', '_').Replace('.', '_');
    }

    /// <inheritdoc/>
    public string Root { get; }

    /// <summary>
    /// The default Maildir root for this platform when nothing is configured:
    /// <c>/var/mail/anjal</c> on Unix, <c>%LOCALAPPDATA%\Anjal\mail</c> on Windows.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            if (System.OperatingSystem.IsWindows())
            {
                string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                return System.IO.Path.Combine(local, "Anjal", "mail");
            }
            return "/var/mail/anjal";
        }
    }

    /// <summary>
    /// Compute the Maildir directory for a folder without creating it.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="folderName">Folder name; <c>INBOX</c> is the Maildir root.</param>
    public string FolderPath(string tenantSlug, string address, string folderName)
    {
        System.ArgumentNullException.ThrowIfNull(tenantSlug);
        System.ArgumentNullException.ThrowIfNull(address);
        System.ArgumentNullException.ThrowIfNull(folderName);

        string mailboxDir = System.IO.Path.Combine(this.Root, SafeSegment(tenantSlug), SafeSegment(address));
        string sub = Anjal.Store.FolderRow.MaildirNameFor(folderName);
        return sub.Length == 0 ? mailboxDir : System.IO.Path.Combine(mailboxDir, SafeSegment(sub));
    }

    /// <inheritdoc/>
    public string EnsureFolder(string tenantSlug, string address, string folderName)
    {
        string dir = this.FolderPath(tenantSlug, address, folderName);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "tmp"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "new"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "cur"));
        return dir;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<MaildirWriteResult> WriteAsync(
        string tenantSlug,
        string address,
        string folderName,
        byte[] rawBytes,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(rawBytes);
        string dir = this.EnsureFolder(tenantSlug, address, folderName);

        string name = this.NextUniqueName();
        string tmpPath = System.IO.Path.Combine(dir, "tmp", name);
        string newPath = System.IO.Path.Combine(dir, "new", name);

        // Maildir delivery: write fully to tmp/, fsync, then rename into new/.
        await using (var fs = new System.IO.FileStream(tmpPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None, 65536, useAsync: true))
        {
            await fs.WriteAsync(rawBytes, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        System.IO.File.Move(tmpPath, newPath);

        return new MaildirWriteResult
        {
            RelativePath = "new/" + name,
            FullPath = newPath,
            SizeBytes = rawBytes.LongLength,
        };
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<byte[]?> ReadAsync(
        string tenantSlug,
        string address,
        string folderName,
        string relativePath,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(relativePath);
        string dir = this.FolderPath(tenantSlug, address, folderName);

        // The relative path is "new/<name>" or "cur/<name>[:2,flags]"; reject
        // anything that tries to escape the folder directory.
        string[] parts = relativePath.Split('/');
        if (parts.Length != 2 || (parts[0] != "new" && parts[0] != "cur") || parts[1].Length == 0 ||
            parts[1].Contains("..", System.StringComparison.Ordinal) || parts[1].Contains('\\', System.StringComparison.Ordinal))
        {
            return null;
        }
        string full = System.IO.Path.Combine(dir, parts[0], parts[1]);
        if (!System.IO.File.Exists(full))
        {
            return null;
        }
        return await System.IO.File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generate a Maildir unique file name: <c>&lt;seconds&gt;.M&lt;microseconds&gt;P&lt;pid&gt;Q&lt;sequence&gt;.&lt;host&gt;</c>.
    /// The combination of time, process id and a process-wide counter makes
    /// names unique even for two deliveries in the same microsecond.
    /// </summary>
    private string NextUniqueName()
    {
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        long seconds = now.ToUnixTimeSeconds();
        long micros = (now.Ticks % System.TimeSpan.TicksPerSecond) / 10;
        int pid = System.Environment.ProcessId;
        int seq = System.Threading.Interlocked.Increment(ref sequence);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seconds}.M{micros}P{pid}Q{seq}.{this.hostName}");
    }

    /// <summary>
    /// Make a path segment safe: reject separators and traversal so a
    /// crafted tenant slug or address can never escape the root.
    /// </summary>
    private static string SafeSegment(string segment)
    {
        if (segment.Length == 0 || segment == "." || segment == "..")
        {
            throw new System.ArgumentException($"Invalid path segment '{segment}'.");
        }
        foreach (char c in segment)
        {
            if (c == '/' || c == '\\' || System.Array.IndexOf(InvalidPathChars, c) >= 0)
            {
                throw new System.ArgumentException($"Path segment '{segment}' contains an invalid character '{c}'.");
            }
        }
        return segment;
    }
}
