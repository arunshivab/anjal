using System.IO.Compression;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.28 (DES-18 §10, R10 [10c], D-337): what is inside a ZIP, read on the server without unpacking it -
/// names, sizes and dates, folders indented, programs marked Blocked (by name, and by their first bytes)
/// and never offered. An archive that is too large when unpacked, too tightly packed, or has too many
/// entries is refused outright (an "archive bomb"), and an encrypted one is said to be locked.
/// </summary>
public static class ZipListing
{
    /// <summary>At most this many entries are listed (D-337).</summary>
    public const int MaxEntries = 2000;

    /// <summary>At most this many times larger unpacked than packed, over the whole archive (D-337).</summary>
    public const long MaxRatio = 100;

    /// <summary>At most this many bytes unpacked, over the whole archive (D-337).</summary>
    public const long MaxExpanded = 200L * 1024 * 1024;

    /// <summary>Read an archive's list.</summary>
    /// <param name="bytes">The ZIP's bytes.</param>
    /// <returns>The listing; <see cref="ZipView.Refused"/> says why when it is not listed.</returns>
    public static ZipView Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (CountFromEnd(bytes) is int declared && declared > MaxEntries)
        {
            return ZipView.Refuse(ZipView.TooMany);
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                return ZipView.Refuse(ZipView.TooMany);
            }
            long expanded = 0, packed = 0;
            bool locked = false;
            var entries = new List<ZipEntryView>();
            for (int i = 0; i < zip.Entries.Count; i++)
            {
                ZipArchiveEntry e = zip.Entries[i];
                expanded += e.Length;
                packed += e.CompressedLength;
                locked |= e.IsEncrypted;
                string path = e.FullName.Replace('\\', '/');
                bool folder = path.EndsWith('/');
                string trimmed = path.TrimEnd('/');
                int depth = trimmed.Count(c => c == '/');
                string name = depth == 0 ? trimmed : trimmed[(trimmed.LastIndexOf('/') + 1)..];
                entries.Add(new ZipEntryView(i, path, name, depth, folder, e.Length, e.LastWriteTime, !folder && IsBlocked(e)));
            }
            if (expanded > MaxExpanded)
            {
                return ZipView.Refuse(ZipView.TooLarge);
            }
            if (packed > 0 && expanded / packed > MaxRatio)
            {
                return ZipView.Refuse(ZipView.TooPacked);
            }
            return new ZipView(entries, null, locked, expanded);
        }
        catch (InvalidDataException)
        {
            return ZipView.Refuse(ZipView.Unreadable);
        }
    }

    /// <summary>
    /// One entry's bytes, for "Download" in the listing - never a program, never from a locked or refused
    /// archive, and never more than its own declared size.
    /// </summary>
    /// <param name="bytes">The ZIP's bytes.</param>
    /// <param name="index">The entry's place in the archive.</param>
    /// <returns>The entry's name and bytes, or null when it is not given out.</returns>
    public static (string Name, byte[] Bytes)? Entry(byte[] bytes, int index)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ZipView view = Read(bytes);
        if (view.Refused is not null || view.Locked || view.Entries.FirstOrDefault(e => e.Index == index) is not ZipEntryView listed || listed.Folder || listed.Blocked)
        {
            return null;
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            ZipArchiveEntry e = zip.Entries[index];
            using Stream s = e.Open();
            using var copy = new MemoryStream();
            byte[] buffer = new byte[81920];
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Never more than it said it was: a lying header does not get to unpack past it.
                if (copy.Length + n > e.Length)
                {
                    return null;
                }
                copy.Write(buffer, 0, n);
            }
            return (listed.Name, copy.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    // A program by its name, or by its first bytes (Windows, Linux and Mac programs, Java, "#!" scripts).
    private static bool IsBlocked(ZipArchiveEntry e)
    {
        if (Anjal.Mailbox.AttachmentPolicy.IsProgram(e.Name))
        {
            return true;
        }
        if (e.IsEncrypted)
        {
            // Locked: its bytes cannot be read here, and the archive says so (nothing inside is given out).
            return false;
        }
        try
        {
            using Stream s = e.Open();
            byte[] head = new byte[64];
            int read = 0, n;
            while (read < head.Length && (n = s.Read(head, read, head.Length - read)) > 0)
            {
                read += n;
            }
            return read >= 4 && Anjal.Mailbox.AttachmentPolicy.IsProgramContent(head[..read]);
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    // The entry count the end of the archive declares, read before the archive is opened.
    private static int? CountFromEnd(byte[] b)
    {
        for (int i = b.Length - 22; i >= Math.Max(0, b.Length - 65557); i--)
        {
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 0x05 && b[i + 3] == 0x06)
            {
                return b[i + 10] | (b[i + 11] << 8);
            }
        }
        return null;
    }
}

/// <summary>One entry of a listed archive (rc.28).</summary>
/// <param name="Index">Its place in the archive.</param>
/// <param name="Path">Its full path inside.</param>
/// <param name="Name">Its own name.</param>
/// <param name="Depth">How many folders deep.</param>
/// <param name="Folder">True for a folder.</param>
/// <param name="Size">Its size unpacked.</param>
/// <param name="Modified">When it was last changed.</param>
/// <param name="Blocked">True for a program: never offered.</param>
public sealed record ZipEntryView(int Index, string Path, string Name, int Depth, bool Folder, long Size, DateTimeOffset Modified, bool Blocked);

/// <summary>A listed archive, or why it is not listed (rc.28).</summary>
/// <param name="Entries">The entries.</param>
/// <param name="Refused">Why it is not listed: <see cref="TooMany"/>, <see cref="TooLarge"/>, <see cref="TooPacked"/> or <see cref="Unreadable"/>; null when listed.</param>
/// <param name="Locked">True when entries are encrypted: nothing inside can be looked at or given out.</param>
/// <param name="Expanded">Its size unpacked.</param>
public sealed record ZipView(IReadOnlyList<ZipEntryView> Entries, string? Refused, bool Locked, long Expanded)
{
    /// <summary>More entries than are listed.</summary>
    public const string TooMany = "too-many";

    /// <summary>Larger unpacked than is allowed.</summary>
    public const string TooLarge = "too-large";

    /// <summary>Packed more tightly than an ordinary archive.</summary>
    public const string TooPacked = "too-packed";

    /// <summary>Not a readable ZIP.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>A refusal.</summary>
    /// <param name="why">Why.</param>
    /// <returns>The view.</returns>
    public static ZipView Refuse(string why) => new(Array.Empty<ZipEntryView>(), why, false, 0);
}
