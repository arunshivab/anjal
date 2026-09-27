using System.Security.Cryptography;

namespace Anjal.Mailbox;

/// <summary>
/// The files of the evidence store (v1.0.0-rc.8, ANJAL-DES-01):
/// <c>root/YYYY/MM/DD/&lt;id&gt;.eml</c>, written once and never replaced.
/// A file is written to a temporary name, flushed to disk, made read-only,
/// then renamed into place - so a file that exists is always complete.
/// </summary>
public sealed class EvidenceVault
{
    /// <summary>Construct.</summary>
    /// <param name="root">The evidence root, e.g. /var/lib/anjal/evidence.</param>
    public EvidenceVault(string root)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.Root = System.IO.Path.GetFullPath(root);
    }

    /// <summary>The evidence root.</summary>
    public string Root { get; }

    /// <summary>
    /// The default evidence root when nothing is configured:
    /// <c>/var/lib/anjal/evidence</c> on Unix, <c>%LOCALAPPDATA%\Anjal\evidence</c> on Windows.
    /// </summary>
    public static string DefaultRoot => System.OperatingSystem.IsWindows()
        ? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Anjal", "evidence")
        : "/var/lib/anjal/evidence";

    /// <summary>SHA-256 of bytes, lower-case hex.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The hash.</returns>
    public static string Sha256Hex(byte[] bytes)
    {
        System.ArgumentNullException.ThrowIfNull(bytes);
        return System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Write a new evidence file. Throws if it cannot be written, or if it already exists.</summary>
    /// <param name="id">The evidence id (the file name).</param>
    /// <param name="capturedAt">When it was captured; decides the folder (UTC date).</param>
    /// <param name="bytes">The exact bytes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The path under the root (forward slashes), the size and the SHA-256.</returns>
    public async Task<(string RelativePath, long SizeBytes, string Sha256)> WriteAsync(System.Guid id, System.DateTimeOffset capturedAt, byte[] bytes, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(bytes);
        System.DateTimeOffset utc = capturedAt.ToUniversalTime();
        string relative = $"{utc:yyyy}/{utc:MM}/{utc:dd}/{id:N}.eml";
        string final = this.FullPath(relative);
        string dir = System.IO.Path.GetDirectoryName(final)!;
        CreateDirectory(dir);
        if (System.IO.File.Exists(final))
        {
            throw new System.IO.IOException("evidence file already exists: " + relative);
        }
        string temp = System.IO.Path.Combine(dir, "." + id.ToString("N") + ".tmp");
        await using (var fs = new System.IO.FileStream(temp, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None, 81920, useAsync: true))
        {
            await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        if (!System.OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(temp, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead);
        }
        System.IO.File.Move(temp, final, overwrite: false);
        return (relative, bytes.LongLength, Sha256Hex(bytes));
    }

    /// <summary>Read an evidence file.</summary>
    /// <param name="relativePath">Its path under the root.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes.</returns>
    public Task<byte[]> ReadAsync(string relativePath, CancellationToken ct = default) =>
        System.IO.File.ReadAllBytesAsync(this.FullPath(relativePath), ct);

    /// <summary>Whether an evidence file exists.</summary>
    /// <param name="relativePath">Its path under the root.</param>
    /// <returns>True when present.</returns>
    public bool Exists(string relativePath) => System.IO.File.Exists(this.FullPath(relativePath));

    /// <summary>Remove an evidence file (retention purge only). Missing files are ignored.</summary>
    /// <param name="relativePath">Its path under the root.</param>
    public void Delete(string relativePath)
    {
        string full = this.FullPath(relativePath);
        if (System.IO.File.Exists(full))
        {
            if (System.OperatingSystem.IsWindows())
            {
                System.IO.File.SetAttributes(full, System.IO.FileAttributes.Normal);
            }
            System.IO.File.Delete(full);
        }
    }

    /// <summary>A path under the root, refusing anything that would leave it.</summary>
    /// <param name="relativePath">Forward-slash path under the root.</param>
    /// <returns>The full path.</returns>
    public string FullPath(string relativePath)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(this.Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        string rootWithSep = this.Root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? this.Root : this.Root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, System.StringComparison.Ordinal))
        {
            throw new System.ArgumentException("path leaves the evidence root: " + relativePath, nameof(relativePath));
        }
        return full;
    }

    private static void CreateDirectory(string dir)
    {
        if (System.OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        else
        {
            System.IO.Directory.CreateDirectory(dir, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupExecute);
        }
    }
}
