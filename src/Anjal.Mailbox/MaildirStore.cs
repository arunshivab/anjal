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

    /// <summary>
    /// Rename a message so its file name carries the given Maildir flags.
    /// A message in <c>new/</c> moves to <c>cur/</c>; a message already in
    /// <c>cur/</c> has its <c>:2,</c> suffix rewritten. Returns the new
    /// relative path, or <see langword="null"/> if the file does not exist.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="folderName">Folder name.</param>
    /// <param name="relativePath">Current relative path.</param>
    /// <param name="seen">Maildir "S" flag.</param>
    /// <param name="flagged">Maildir "F" flag.</param>
    /// <param name="answered">Maildir "R" flag.</param>
    string? SetFlags(string tenantSlug, string address, string folderName, string relativePath, bool seen, bool flagged, bool answered);

    /// <summary>
    /// Move a message file to another folder of the same mailbox, keeping
    /// its file name and flags. Returns the new relative path, or
    /// <see langword="null"/> if the source file does not exist.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="fromFolder">Source folder name.</param>
    /// <param name="relativePath">Current relative path within the source folder.</param>
    /// <param name="toFolder">Destination folder name (created if missing).</param>
    string? Move(string tenantSlug, string address, string fromFolder, string relativePath, string toFolder);

    /// <summary>
    /// Delete a message file. Returns <see langword="true"/> if a file was removed.
    /// </summary>
    /// <param name="tenantSlug">Tenant directory name.</param>
    /// <param name="address">Mailbox address (<c>local@domain</c>).</param>
    /// <param name="folderName">Folder name.</param>
    /// <param name="relativePath">Relative path within the folder.</param>
    bool Delete(string tenantSlug, string address, string folderName, string relativePath);
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

        if (!TrySplitRelative(relativePath, out string sub, out string name))
        {
            return null;
        }
        string full = System.IO.Path.Combine(dir, sub, name);
        if (!System.IO.File.Exists(full))
        {
            return null;
        }
        return await System.IO.File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compose the Maildir flag suffix (<c>:2,</c> followed by flags in
    /// ASCII order) for the given flags. Empty flags still yield <c>:2,</c>.
    /// </summary>
    /// <param name="seen">Maildir "S" flag.</param>
    /// <param name="flagged">Maildir "F" flag.</param>
    /// <param name="answered">Maildir "R" flag.</param>
    public static string FlagSuffix(bool seen, bool flagged, bool answered)
    {
        var sb = new System.Text.StringBuilder(":2,");
        if (flagged)
        {
            sb.Append('F');
        }
        if (answered)
        {
            sb.Append('R');
        }
        if (seen)
        {
            sb.Append('S');
        }
        return sb.ToString();
    }

    /// <inheritdoc/>
    public string? SetFlags(string tenantSlug, string address, string folderName, string relativePath, bool seen, bool flagged, bool answered)
    {
        System.ArgumentNullException.ThrowIfNull(relativePath);
        string dir = this.FolderPath(tenantSlug, address, folderName);
        if (!TrySplitRelative(relativePath, out string sub, out string name))
        {
            return null;
        }
        string source = System.IO.Path.Combine(dir, sub, name);
        if (!System.IO.File.Exists(source))
        {
            return null;
        }

        // Windows forbids ':' in file names; use ';' there (the Dovecot
        // convention for Windows-hosted Maildirs) and ':' elsewhere.
        string baseName = StripFlags(name);
        string suffix = FlagSuffix(seen, flagged, answered);
        if (System.OperatingSystem.IsWindows())
        {
            suffix = string.Concat(";", suffix.AsSpan(1));
        }
        string newName = baseName + suffix;
        string target = System.IO.Path.Combine(dir, "cur", newName);
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "cur"));
        if (!string.Equals(source, target, System.StringComparison.Ordinal))
        {
            System.IO.File.Move(source, target, overwrite: true);
        }
        return "cur/" + newName;
    }

    /// <inheritdoc/>
    public string? Move(string tenantSlug, string address, string fromFolder, string relativePath, string toFolder)
    {
        System.ArgumentNullException.ThrowIfNull(relativePath);
        string fromDir = this.FolderPath(tenantSlug, address, fromFolder);
        if (!TrySplitRelative(relativePath, out string sub, out string name))
        {
            return null;
        }
        string source = System.IO.Path.Combine(fromDir, sub, name);
        if (!System.IO.File.Exists(source))
        {
            return null;
        }
        string toDir = this.EnsureFolder(tenantSlug, address, toFolder);
        string target = System.IO.Path.Combine(toDir, sub, name);
        System.IO.File.Move(source, target, overwrite: true);
        return sub + "/" + name;
    }

    /// <inheritdoc/>
    public bool Delete(string tenantSlug, string address, string folderName, string relativePath)
    {
        System.ArgumentNullException.ThrowIfNull(relativePath);
        string dir = this.FolderPath(tenantSlug, address, folderName);
        if (!TrySplitRelative(relativePath, out string sub, out string name))
        {
            return false;
        }
        string full = System.IO.Path.Combine(dir, sub, name);
        if (!System.IO.File.Exists(full))
        {
            return false;
        }
        System.IO.File.Delete(full);
        return true;
    }

    /// <summary>
    /// Strip a <c>:2,flags</c> (or Windows <c>;2,flags</c>) suffix from a
    /// Maildir file name, returning the unique base name.
    /// </summary>
    /// <param name="name">A Maildir file name (no directory).</param>
    public static string StripFlags(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        int colon = name.IndexOf(":2,", System.StringComparison.Ordinal);
        if (colon < 0)
        {
            colon = name.IndexOf(";2,", System.StringComparison.Ordinal);
        }
        return colon < 0 ? name : name.Substring(0, colon);
    }

    /// <summary>
    /// Validate and split a relative path of the form <c>new/&lt;name&gt;</c>
    /// or <c>cur/&lt;name&gt;</c>, rejecting anything that could escape the
    /// folder directory.
    /// </summary>
    private static bool TrySplitRelative(string relativePath, out string sub, out string name)
    {
        sub = string.Empty;
        name = string.Empty;
        string[] parts = relativePath.Split('/');
        if (parts.Length != 2 || (parts[0] != "new" && parts[0] != "cur") || parts[1].Length == 0 ||
            parts[1].Contains("..", System.StringComparison.Ordinal) || parts[1].Contains('\\', System.StringComparison.Ordinal))
        {
            return false;
        }
        sub = parts[0];
        name = parts[1];
        return true;
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
