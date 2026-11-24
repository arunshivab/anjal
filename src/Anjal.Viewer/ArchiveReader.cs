using System.Text;
using Anjal.Mime;
using Anjal.Viewing;
using Chuvadi.Zip;

namespace Anjal.Viewer;

/// <summary>
/// rc.28 (DES-18 §10, D-337), moved into the viewer process in rc.29 (AC-17, D-381) and read with Chuvadi.Zip
/// (AC-20): what is inside a ZIP, read without unpacking it, and one entry unpacked in memory - never to disk - when
/// the person opens or downloads it.
/// <list type="bullet">
/// <item>The whole archive: 2,000 entries, 200 MB unpacked, 100:1 - on the sizes it declares, before anything is
/// unpacked; and the library's own caps (<see cref="ZipExtractionLimits.Untrusted"/>, lowered to these) on the
/// bytes actually produced.</item>
/// <item>Each entry it gives out: exactly its own declared size, no more than 64 MB (a viewer's limit), and 100:1 on
/// its own once it is larger than 1 MB.</item>
/// <item>An archive inside the archive is listed as the second level, sharing the outer archive's budgets; a third
/// level is a plain entry.</item>
/// <item>Programs - by name, and by their first bytes - are never given out or opened.</item>
/// <item>A locked archive (WinZip AES, or the old ZipCrypto) is opened only with its password, here only: its
/// files are shown, never given out one by one (AC-20).</item>
/// <item>Names are read as UTF-8 when the entry says so or the bytes are valid UTF-8, otherwise in the DOS code
/// page (437) that older Windows tools write.</item>
/// </list>
/// </summary>
public static class ArchiveReader
{
    /// <summary>At most this many entries are listed (D-337), the outer and inner archive together.</summary>
    public const int MaxEntries = 2000;

    /// <summary>At most this many times larger unpacked than packed (D-337).</summary>
    public const long MaxRatio = 100;

    /// <summary>At most this many bytes unpacked, the outer and inner archive together (D-337).</summary>
    public const long MaxExpanded = 200L * 1024 * 1024;

    /// <summary>An entry larger than this must also stay within <see cref="MaxRatio"/> on its own (AC-17).</summary>
    public const long EntryRatioFrom = 1024 * 1024;

    /// <summary>The most an entry may weigh to be opened or given out (a viewer's limit, DES-22 §5).</summary>
    public const long MaxEntryBytes = ViewFrames.MaxFileBytes;

    /// <summary>How many archives deep a listing goes (AC-17): the attachment, and one inside it.</summary>
    public const int MaxLevels = 2;

    // Enough of an entry for a program's mark and DICOM's "DICM" at 128.
    private const int HeadBytes = 132;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Lists an archive, or the archive inside it that <paramref name="entryPath"/> names.</summary>
    /// <param name="bytes">The attachment's bytes.</param>
    /// <param name="entryPath">Null for the attachment itself; an entry's place ("3") for an archive inside it.</param>
    /// <param name="passwords">The passwords typed for the archive and for the archive inside it, if any.</param>
    /// <returns>
    /// The listing; <see cref="ArchiveListing.Status"/> says when a password is needed or wrong, and
    /// <see cref="ArchiveView.Refused"/> why it is not listed.
    /// </returns>
    public static ArchiveListing List(byte[] bytes, string? entryPath = null, ArchivePasswords passwords = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int[] path = ParsePath(entryPath);
        if (path.Length >= MaxLevels)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.TooDeep);
        }
        ArchiveListing outer = Read(bytes, level: 1, usedEntries: 0, usedBytes: 0, passwords.Outer, place: string.Empty);
        if (path.Length == 0 || !outer.Listed)
        {
            return outer;
        }
        ArchiveItem? item = outer.View.Entries.FirstOrDefault(e => e.Index == path[0]);
        if (item is null || item.Folder || item.Blocked || item.Opens != FileKinds.Archive)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.Unreadable);
        }
        EntryResult unpacked = Unpack(bytes, item, passwords.Outer, outer.View.Locked);
        if (unpacked.Bytes is not byte[] inner)
        {
            return unpacked.Status == ViewStatus.TooLarge ? ArchiveListing.Refuse(ArchiveRefusals.TooLarge) : ArchiveListing.Of(unpacked.Status, string.Empty);
        }
        // The inner archive shares the outer one's budgets: its entries and its unpacked size are added to the
        // outer archive's, less the inner archive's own place in it.
        ArchiveListing listing = Read(inner, level: 2, usedEntries: outer.View.Entries.Count(e => e.Index >= 0), usedBytes: outer.View.Expanded - item.Size, passwords.Inner, place: path[0].ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Files from inside a locked outer archive stay view only, however the inner archive was made.
        return outer.View.Locked && listing.Listed && !listing.View.Locked ? listing with { View = Relock(listing.View) } : listing;
    }

    /// <summary>
    /// One file from inside an archive, unpacked in memory, for a viewer or its Download: never a program, a folder,
    /// an entry from a refused archive, or more than its own declared size or 64 MB. A file from a locked archive is
    /// given, marked <see cref="EntryResult.Locked"/>, only for a viewer.
    /// </summary>
    /// <param name="bytes">The attachment's bytes.</param>
    /// <param name="entryPath">The entry's place ("5"), or an archive's and then the entry's ("3/5").</param>
    /// <param name="passwords">The passwords typed for the archive and for the archive inside it, if any.</param>
    /// <returns>The entry, or the status that says why it is not given.</returns>
    public static EntryResult Entry(byte[] bytes, string entryPath, ArchivePasswords passwords = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(entryPath);
        int[] path = ParsePath(entryPath);
        if (path.Length == 0 || path.Length > MaxLevels)
        {
            return EntryResult.Of(ViewStatus.Refused);
        }
        byte[] archive = bytes;
        string? password = passwords.Outer;
        ArchiveListing listing = List(bytes, null, passwords);
        if (path.Length == 2)
        {
            if (!listing.Listed)
            {
                return EntryResult.From(listing);
            }
            ArchiveItem? holder = listing.View.Entries.FirstOrDefault(e => e.Index == path[0]);
            if (holder is null || holder.Opens != FileKinds.Archive)
            {
                return EntryResult.Of(ViewStatus.Refused);
            }
            EntryResult inner = Unpack(bytes, holder, passwords.Outer, listing.View.Locked);
            if (inner.Bytes is null)
            {
                return inner with { Place = string.Empty };
            }
            archive = inner.Bytes;
            password = passwords.Inner;
            listing = List(bytes, path[0].ToString(System.Globalization.CultureInfo.InvariantCulture), passwords);
        }
        if (!listing.Listed)
        {
            return EntryResult.From(listing);
        }
        if (listing.View.Entries.FirstOrDefault(e => e.Index == path[^1]) is not ArchiveItem listed || listed.Folder || listed.Blocked)
        {
            return EntryResult.Of(ViewStatus.Refused);
        }
        if (listed.Unsupported)
        {
            return EntryResult.Of(ViewStatus.Unsupported) with { Place = string.Empty };
        }
        EntryResult result = Unpack(archive, listed, password, listing.View.Locked);
        return result.Bytes is null ? result with { Place = path.Length == 2 ? path[0].ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty } : result;
    }

    /// <summary>
    /// rc.30 (AC-19): one listed entry of the archive itself (not one inside an archive in it), unpacked in memory with
    /// the same caps as <see cref="Entry"/> - for the DICOM grouping, which reads the listing once and then each file
    /// in turn, holding one at a time.
    /// </summary>
    /// <param name="bytes">The attachment's bytes.</param>
    /// <param name="listing">The archive's listing, from <see cref="List"/>.</param>
    /// <param name="item">One of its entries.</param>
    /// <param name="passwords">The passwords typed for the archive.</param>
    /// <returns>The entry, or the status that says why it is not given.</returns>
    public static EntryResult Unpacked(byte[] bytes, ArchiveListing listing, ArchiveItem item, ArchivePasswords passwords = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(item);
        if (!listing.Listed || item.Folder || item.Blocked || item.Index < 0)
        {
            return EntryResult.Of(ViewStatus.Refused);
        }
        return item.Unsupported ? EntryResult.Of(ViewStatus.Unsupported) : Unpack(bytes, item, passwords.Outer, listing.View.Locked);
    }

    /// <summary>Reads "3" or "3/5" into places; an empty array for null or anything else.</summary>
    /// <param name="entryPath">The path.</param>
    /// <returns>The places.</returns>
    public static int[] ParsePath(string? entryPath)
    {
        if (string.IsNullOrEmpty(entryPath))
        {
            return Array.Empty<int>();
        }
        string[] parts = entryPath.Split('/');
        var places = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out places[i]) || places[i] >= MaxEntries)
            {
                return Array.Empty<int>();
            }
        }
        return places;
    }

    /// <summary>
    /// The library's caps for an archive Anjal reads: its preset for untrusted input, each lowered to Anjal's own
    /// budget where Anjal's is tighter (AC-20).
    /// </summary>
    /// <param name="entriesLeft">How many entries this archive may still have (the outer one's count counts).</param>
    /// <returns>The caps.</returns>
    public static ZipExtractionLimits Limits(int entriesLeft)
    {
        ZipExtractionLimits u = ZipExtractionLimits.Untrusted;
        return new ZipExtractionLimits
        {
            MaxEntries = Math.Min(u.MaxEntries ?? int.MaxValue, entriesLeft),
            MaxEntryBytes = Math.Min(u.MaxEntryBytes ?? long.MaxValue, MaxEntryBytes),
            MaxTotalBytes = Math.Min(u.MaxTotalBytes ?? long.MaxValue, MaxExpanded),
            MaxCompressionRatio = Math.Min(u.MaxCompressionRatio ?? double.MaxValue, MaxRatio),
            MaxPathDepth = u.MaxPathDepth,
            MaxNameLength = u.MaxNameLength,
            MaxCentralDirectoryBytes = u.MaxCentralDirectoryBytes,
        };
    }

    private static ZipReader Open(byte[] bytes, int entriesLeft) => ZipReader.Open(bytes, new ZipReaderOptions
    {
        Limits = Limits(entriesLeft),
        // Latin-1 keeps every byte of a name the archive does not mark UTF-8; the names are decoded below.
        NameEncoding = Encoding.Latin1,
        VerifyCrc = true,
        DetectOverlappingEntries = true,
    });

    private static ArchiveListing Read(byte[] bytes, int level, int usedEntries, long usedBytes, string? password, string place)
    {
        int left = MaxEntries - usedEntries;
        if (left <= 0)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.TooMany);
        }
        try
        {
            using ZipReader zip = Open(bytes, left);
            long expanded = 0, packed = 0;
            foreach (ZipEntryInfo e in zip.Entries)
            {
                expanded += e.Size;
                packed += e.CompressedSize;
            }
            if (expanded + usedBytes > MaxExpanded)
            {
                return ArchiveListing.Refuse(ArchiveRefusals.TooLarge);
            }
            if (packed > 0 && expanded / packed > MaxRatio)
            {
                return ArchiveListing.Refuse(ArchiveRefusals.TooPacked);
            }
            bool locked = zip.Entries.Any(e => e.IsEncrypted && !e.IsDirectory);
            if (locked && string.IsNullOrEmpty(password))
            {
                return ArchiveListing.Of(ViewStatus.NeedsPassword, place);
            }
            var entries = new List<ArchiveItem>(zip.Count);
            foreach (ZipEntryInfo e in zip.Entries)
            {
                string path = Decode(e).Replace('\\', '/');
                bool folder = e.IsDirectory || path.EndsWith('/');
                string trimmed = path.TrimEnd('/');
                int depth = trimmed.Count(c => c == '/');
                string name = depth == 0 ? trimmed : trimmed[(trimmed.LastIndexOf('/') + 1)..];
                bool unsupported = !folder && !e.IsSupported;
                byte[]? head = null;
                if (!folder && !unsupported)
                {
                    HeadRead read = Head(zip, e, password);
                    if (read.Status is ViewStatus.NeedsPassword or ViewStatus.WrongPassword)
                    {
                        return ArchiveListing.Of(read.Status, place);
                    }
                    unsupported = read.Status == ViewStatus.Unsupported;
                    head = read.Bytes;
                }
                // An entry whose first bytes cannot be read is blocked: nothing can say it is not a program. One Anjal
                // cannot unpack is never opened or given out, so only its name is judged.
                bool blocked = !folder && (ProgramSigns.IsProgramName(name) || (!unsupported && (head is null || (head.Length >= 4 && ProgramSigns.IsProgramContent(head)))));
                string? opens = folder || blocked || unsupported || e.IsSymlink || head is null ? null : OpensWith(name, head, level);
                entries.Add(new ArchiveItem
                {
                    Index = e.Index,
                    Path = path,
                    Name = name,
                    Depth = depth,
                    Folder = folder,
                    Size = e.Size,
                    Modified = e.LastModified,
                    Blocked = blocked,
                    Unsupported = unsupported,
                    Opens = opens,
                });
            }
            return ArchiveListing.Of(new ArchiveView { Entries = Tree(entries), Locked = locked, Expanded = expanded });
        }
        catch (ZipLimitExceededException ex)
        {
            return ArchiveListing.Refuse(ex.Limit == ZipLimit.EntryCount ? ArchiveRefusals.TooMany : ex.Limit == ZipLimit.CompressionRatio ? ArchiveRefusals.TooPacked : ArchiveRefusals.TooLarge);
        }
        catch (ZipUnsupportedException)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.Unsupported);
        }
        catch (ZipFormatException)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.Unreadable);
        }
        catch (InvalidDataException)
        {
            return ArchiveListing.Refuse(ArchiveRefusals.Unreadable);
        }
    }

    // rc.29: the list as a tree. Many tools write "Quality/Agenda.pdf" with no entry for the folder "Quality/"; each
    // such folder is added (with a negative index: it is not in the archive and cannot be opened), and every entry
    // follows its folder - folders first at each level, then by name.
    internal static List<ArchiveItem> Tree(List<ArchiveItem> entries)
    {
        var folders = new HashSet<string>(entries.Where(e => e.Folder).Select(e => e.Path.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
        var added = new List<ArchiveItem>();
        foreach (ArchiveItem e in entries.Where(e => !Climbs(e.Path)))
        {
            string[] parts = e.Path.TrimEnd('/').Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                string folder = string.Join('/', parts, 0, i);
                if (folders.Add(folder))
                {
                    added.Add(new ArchiveItem
                    {
                        Index = -1 - added.Count,
                        Path = folder + "/",
                        Name = parts[i - 1],
                        Depth = i - 1,
                        Folder = true,
                    });
                }
            }
        }
        var all = new List<ArchiveItem>(entries.Count + added.Count);
        all.AddRange(entries);
        all.AddRange(added);
        all.Sort(TreeOrder);
        return all;
    }

    // A path that starts at the root or climbs ("/abs/x", "../../etc/passwd") is only text: no folders are made from
    // it, and it is placed by its whole path among the top-level files.
    private static bool Climbs(string path) => path.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p == "." || p == "..");

    private static string[] Steps(ArchiveItem e) => Climbs(e.Path) ? [e.Path] : e.Path.TrimEnd('/').Split('/');

    private static int TreeOrder(ArchiveItem a, ArchiveItem b)
    {
        string[] x = Steps(a);
        string[] y = Steps(b);
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int same = string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            bool xFolder = i < x.Length - 1 || (a.Folder && !Climbs(a.Path));
            bool yFolder = i < y.Length - 1 || (b.Folder && !Climbs(b.Path));
            if (same == 0 && xFolder == yFolder)
            {
                continue;
            }
            if (xFolder != yFolder)
            {
                return xFolder ? -1 : 1;
            }
            return same != 0 ? same : a.Index.CompareTo(b.Index);
        }
        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : a.Index.CompareTo(b.Index);
    }

    // Which viewer opens an entry: by its name, and for DICOM by its first bytes. An archive opens only at the
    // first level; at the second it is a plain entry (AC-17).
    private static string? OpensWith(string name, byte[] head, int level)
    {
        if (FileKinds.IsDicom(head))
        {
            return FileKinds.Dicom;
        }
        string? kind = FileKinds.Of(name, null);
        return kind == FileKinds.Archive && level >= MaxLevels ? null : kind;
    }

    private readonly record struct HeadRead(string Status, byte[]? Bytes);

    // The first bytes of an entry, through its password when it is locked. A wrong password shows at the first
    // locked entry (both methods check it before any data); anything else unreadable leaves the head empty.
    private static HeadRead Head(ZipReader zip, ZipEntryInfo e, string? password)
    {
        try
        {
            using Stream s = zip.OpenRead(e, e.IsEncrypted ? password : null);
            byte[] head = new byte[HeadBytes];
            int read = 0, n;
            while (read < head.Length && (n = s.Read(head, read, head.Length - read)) > 0)
            {
                read += n;
            }
            return new HeadRead(ViewStatus.Ok, head[..read]);
        }
        catch (ZipPasswordRequiredException ex)
        {
            return new HeadRead(ex.WrongPassword ? ViewStatus.WrongPassword : ViewStatus.NeedsPassword, null);
        }
        catch (ZipUnsupportedException)
        {
            return new HeadRead(ViewStatus.Unsupported, null);
        }
        catch (ZipFormatException)
        {
            return new HeadRead(ViewStatus.Damaged, null);
        }
        catch (InvalidDataException)
        {
            return new HeadRead(ViewStatus.Damaged, null);
        }
        catch (IOException)
        {
            return new HeadRead(ViewStatus.Damaged, null);
        }
    }

    // One entry unpacked in memory: exactly its declared size, never past 64 MB or (above 1 MB) 100:1 on its own -
    // the library checks the bytes it produces against the same caps. After a ZipCrypto password, a failure later in
    // the data cannot be told from a wrong password (AC-20).
    private static EntryResult Unpack(byte[] archive, ArchiveItem listed, string? password, bool locked)
    {
        if (listed.Size > MaxEntryBytes)
        {
            return EntryResult.Of(ViewStatus.TooLarge);
        }
        ZipEncryption method = ZipEncryption.None;
        try
        {
            using ZipReader zip = Open(archive, MaxEntries);
            if (listed.Index >= zip.Count)
            {
                return EntryResult.Of(ViewStatus.Refused);
            }
            ZipEntryInfo e = zip.Entries[listed.Index];
            method = e.Encryption;
            if (e.Size != listed.Size)
            {
                return EntryResult.Of(ViewStatus.Refused);
            }
            if (e.Size > EntryRatioFrom && (e.CompressedSize <= 0 || e.Size / e.CompressedSize > MaxRatio))
            {
                return EntryResult.Of(ViewStatus.TooLarge);
            }
            byte[] body = zip.ReadAllBytes(e, e.IsEncrypted ? password : null);
            // Never other than it said it was: a lying header does not get to give out more, or less.
            return body.Length == e.Size ? new EntryResult(ViewStatus.Ok, listed.Name, body, locked) : EntryResult.Of(ViewStatus.Damaged);
        }
        catch (ZipPasswordRequiredException ex)
        {
            return EntryResult.Of(!ex.WrongPassword ? ViewStatus.NeedsPassword : method == ZipEncryption.ZipCrypto ? ViewStatus.WrongOrDamaged : ViewStatus.WrongPassword);
        }
        catch (ZipLimitExceededException)
        {
            return EntryResult.Of(ViewStatus.TooLarge);
        }
        catch (ZipUnsupportedException)
        {
            return EntryResult.Of(ViewStatus.Unsupported);
        }
        catch (ZipFormatException)
        {
            return EntryResult.Of(method == ZipEncryption.ZipCrypto && !string.IsNullOrEmpty(password) ? ViewStatus.WrongOrDamaged : ViewStatus.Damaged);
        }
        catch (InvalidDataException)
        {
            return EntryResult.Of(method == ZipEncryption.ZipCrypto && !string.IsNullOrEmpty(password) ? ViewStatus.WrongOrDamaged : ViewStatus.Damaged);
        }
    }

    private static ArchiveView Relock(ArchiveView v) => new() { Entries = v.Entries, Locked = true, Expanded = v.Expanded, Refused = v.Refused };

    // UTF-8 when the entry says so, or when its bytes are valid UTF-8; otherwise the DOS code page 437. The library
    // was asked for Latin-1, which gives every byte back unchanged.
    private static string Decode(ZipEntryInfo e)
    {
        if (e.IsUtf8Name)
        {
            return e.Name;
        }
        byte[] raw = Encoding.Latin1.GetBytes(e.Name);
        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return DosCodePage.Decode(raw);
        }
    }
}

/// <summary>AC-20: the passwords a person typed for an archive and for an archive inside it, for one request.</summary>
/// <param name="Outer">The attachment's own password.</param>
/// <param name="Inner">The password of the archive inside it ("3" of "3/5").</param>
public readonly record struct ArchivePasswords(string? Outer, string? Inner);

/// <summary>An archive's listing, or why there is none: a password needed or wrong, or a refusal.</summary>
/// <param name="Status">Ok, or the status to answer with.</param>
/// <param name="Place">For a password: which archive it is for ("" the attachment, "3" the one inside it).</param>
/// <param name="View">The listing (with <see cref="ArchiveView.Refused"/> set when refused).</param>
public sealed record ArchiveListing(string Status, string? Place, ArchiveView View)
{
    /// <summary>True when the entries are listed.</summary>
    public bool Listed => this.Status == ViewStatus.Ok && this.View.Refused is null;

    /// <summary>A listing.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The listing.</returns>
    public static ArchiveListing Of(ArchiveView view) => new(ViewStatus.Ok, null, view);

    /// <summary>No listing, for a status (a password needed or wrong, or the archive damaged).</summary>
    /// <param name="status">The status.</param>
    /// <param name="place">Which archive.</param>
    /// <returns>The listing.</returns>
    public static ArchiveListing Of(string status, string? place) => new(status, place, new ArchiveView());

    /// <summary>A refused archive (<see cref="ArchiveRefusals"/>).</summary>
    /// <param name="why">Why.</param>
    /// <returns>The listing.</returns>
    public static ArchiveListing Refuse(string why) => new(ViewStatus.Ok, null, new ArchiveView { Refused = why });
}

/// <summary>One file taken from an archive in memory, or why it was not.</summary>
/// <param name="Status">Ok, or the status to answer with.</param>
/// <param name="Name">Its own name.</param>
/// <param name="Bytes">Its bytes.</param>
/// <param name="Locked">True when it came from a locked archive: shown only, never given out (AC-20).</param>
public sealed record EntryResult(string Status, string? Name, byte[]? Bytes, bool Locked)
{
    /// <summary>The password's place, when <see cref="Status"/> asks for one.</summary>
    public string? Place { get; init; }

    /// <summary>No file, for a status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The result.</returns>
    public static EntryResult Of(string status) => new(status, null, null, false);

    /// <summary>No file, because its archive was not listed.</summary>
    /// <param name="listing">The archive's listing.</param>
    /// <returns>The result.</returns>
    public static EntryResult From(ArchiveListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return listing.Status != ViewStatus.Ok ? Of(listing.Status) with { Place = listing.Place } : listing.View.Refused == ArchiveRefusals.Unsupported ? Of(ViewStatus.Unsupported) with { Place = string.Empty } : Of(ViewStatus.Refused);
    }
}

/// <summary>rc.29 (AC-17): the DOS code page 437, in which older Windows tools write ZIP names.</summary>
public static class DosCodePage
{
    // Code page 437's characters 128-255, in order.
    private const string High =
        "ÇüéâäàåçêëèïîìÄÅ" +
        "ÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
        "áíóúñÑªº¿⌐¬½¼¡«»" +
        "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧" +
        "╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩" +
        "≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    /// <summary>The 128 characters of the upper half, for a test.</summary>
    public static int HighCount => High.Length;

    /// <summary>Decodes bytes written in code page 437.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append(b < 0x80 ? (char)b : High[b - 0x80]);
        }
        return sb.ToString();
    }
}
