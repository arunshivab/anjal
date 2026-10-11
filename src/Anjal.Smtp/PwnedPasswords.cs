namespace Anjal.Smtp;

/// <summary>
/// The downloaded list of passwords known from data leaks (rc.15, item 35; owner's decisions of
/// 7 Oct 2026): Have I Been Pwned's Pwned Passwords, kept on the server as a compact file and
/// refreshed every six months, alongside an online check of every new password (see
/// <see cref="PwnedPasswords"/>). Anjal is built first for applications, transactional mail and
/// hospital systems, where sign-in has a second step, so the list keeps the passwords seen in
/// at least three leaks (ANJAL_PWNED_MIN_COUNT, default 3) - about 1.1 billion of the 2.1
/// billion, in about 5.8 GB. Checking against this list sends nothing anywhere.
/// <para>
/// Each leaked password is kept as the first 8 bytes of its SHA-1 fingerprint. The file groups
/// them by their first 3 bytes, so only the other 5 are stored: a 32-byte header ("ANJALPW2",
/// the number of fingerprints, when it was built, the fewest leaks a password needed to be
/// included), then where each of the 16,777,216 groups starts (4 bytes each, plus one for the
/// end), then the fingerprints' last 5 bytes, sorted. A password that was never leaked is
/// wrongly refused about once in 16 billion. A check reads about ten small pieces of the file
/// through the system's file cache; the file is never loaded whole.
/// </para>
/// </summary>
public sealed class PwnedPasswordList
{
    /// <summary>The first bytes of every list file.</summary>
    public static readonly byte[] Magic = "ANJALPW2"u8.ToArray();

    /// <summary>The size of the header, in bytes.</summary>
    public const int HeaderLength = 32;

    /// <summary>How many groups the fingerprints are sorted into (by their first 3 bytes).</summary>
    public const int Groups = 1 << 24;

    /// <summary>Where the stored fingerprints begin: after the header and the group starts.</summary>
    public const long EntriesStart = HeaderLength + ((Groups + 1L) * 4);

    /// <summary>The bytes kept of each fingerprint (its first 3 are its group).</summary>
    public const int EntryLength = 5;

    private PwnedPasswordList(string path, long count, System.DateTimeOffset builtAt, int minimumCount)
    {
        this.Path = path;
        this.Count = count;
        this.BuiltAt = builtAt;
        this.MinimumCount = minimumCount;
    }

    /// <summary>Where the list was read from.</summary>
    public string Path { get; }

    /// <summary>How many leaked passwords the list holds.</summary>
    public long Count { get; }

    /// <summary>When the list was built.</summary>
    public System.DateTimeOffset BuiltAt { get; }

    /// <summary>The fewest leaks a password needed to be included.</summary>
    public int MinimumCount { get; }

    /// <summary>Open a list file, checking its header and size.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The list.</returns>
    /// <exception cref="System.IO.InvalidDataException">The file is not a complete list file.</exception>
    public static PwnedPasswordList Open(string path)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = OpenHandle(path);
        (long count, System.DateTimeOffset builtAt, int minimum) = ReadHeader(handle);
        return new PwnedPasswordList(path, count, builtAt, minimum);
    }

    /// <summary>The fingerprint Anjal keeps for a password: the first 8 bytes of its SHA-1, as a number.</summary>
    /// <param name="password">The password.</param>
    /// <returns>The fingerprint.</returns>
    public static ulong Fingerprint(string password)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        System.Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // SHA-1 is the list's own format (Have I Been Pwned publishes SHA-1); it protects nothing here.
        System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(password), hash);
#pragma warning restore CA5350
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    /// <summary>True when the password is on the list.</summary>
    /// <param name="password">The password.</param>
    /// <returns>True when it is a known leaked password.</returns>
    public bool Contains(string password)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        return this.ContainsFingerprint(Fingerprint(password));
    }

    /// <summary>
    /// True when the fingerprint is on the list. The file is opened for each check and closed
    /// again: passwords are checked rarely, and holding nothing open lets a refresh replace the
    /// file on every operating system.
    /// </summary>
    /// <param name="fingerprint">A fingerprint from <see cref="Fingerprint(string)"/>.</param>
    /// <returns>True when it is on the list.</returns>
    public bool ContainsFingerprint(ulong fingerprint)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = OpenHandle(this.Path);
        ReadHeader(handle);
        int group = (int)(fingerprint >> 40);
        ulong rest = fingerprint & 0xFF_FFFF_FFFFUL;
        System.Span<byte> bounds = stackalloc byte[8];
        ReadExactly(handle, bounds, HeaderLength + (group * 4L));
        long lo = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bounds);
        long hi = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bounds[4..]) - 1;
        System.Span<byte> five = stackalloc byte[EntryLength];
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) / 2);
            ReadExactly(handle, five, EntriesStart + (mid * EntryLength));
            ulong at = Read40(five);
            if (at == rest)
            {
                return true;
            }
            if (at < rest)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return false;
    }

    /// <summary>The size a list of this many fingerprints has.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Bytes.</returns>
    public static long SizeFor(long count) => EntriesStart + (count * EntryLength);

    internal static ulong Read40(System.ReadOnlySpan<byte> b) =>
        ((ulong)b[0] << 32) | ((ulong)b[1] << 24) | ((ulong)b[2] << 16) | ((ulong)b[3] << 8) | b[4];

    internal static void Write40(System.Span<byte> b, ulong v)
    {
        b[0] = (byte)(v >> 32);
        b[1] = (byte)(v >> 24);
        b[2] = (byte)(v >> 16);
        b[3] = (byte)(v >> 8);
        b[4] = (byte)v;
    }

    private static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Span<byte> into, long offset)
    {
        if (System.IO.RandomAccess.Read(handle, into, offset) != into.Length)
        {
            throw new System.IO.InvalidDataException("The leaked-password file ended early.");
        }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenHandle(string path) =>
        System.IO.File.OpenHandle(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);

    private static (long Count, System.DateTimeOffset BuiltAt, int MinimumCount) ReadHeader(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        System.Span<byte> header = stackalloc byte[HeaderLength];
        long length = System.IO.RandomAccess.GetLength(handle);
        if (length < EntriesStart || System.IO.RandomAccess.Read(handle, header, 0) < HeaderLength)
        {
            throw new System.IO.InvalidDataException("The leaked-password file is too short to be a list.");
        }
        if (!header[..8].SequenceEqual(Magic))
        {
            throw new System.IO.InvalidDataException("The file is not an Anjal leaked-password list.");
        }
        long count = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(header.Slice(8, 8));
        long built = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8));
        int minimum = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.Slice(24, 4));
        if (count < 0 || count > uint.MaxValue || length != SizeFor(count))
        {
            throw new System.IO.InvalidDataException("The leaked-password file is incomplete: its size does not match its count.");
        }
        return (count, System.DateTimeOffset.FromUnixTimeSeconds(built), minimum);
    }
}

/// <summary>
/// The leaked-password checks of the running service (rc.15, item 35; owner, 7 Oct 2026:
/// "both"). Every new password is checked twice: against the downloaded list (passwords seen in
/// 3 or more leaks, refreshed every six months) and online against Have I Been Pwned's full,
/// current list, by k-anonymity - only the first 5 of the 40 characters of the password's SHA-1
/// fingerprint are sent, the answer holds every leaked fingerprint starting with them (padded to
/// a uniform size), and the match is made here. When the online service cannot be reached the
/// downloaded list's answer stands, so a password change is never held up.
/// <para>
/// ANJAL_PWNED_MODE chooses: "both" (the default), "download" (nothing is sent at password
/// time: for a customer whose servers may not reach the internet), "online", or "off". The
/// service names the list file once at start-up; when a refresh replaces it, the new one is
/// picked up within a minute, by the server and the webmail alike. Without a file (before the
/// first download finishes) the online check and the short list built into
/// <see cref="PasswordPolicy"/> apply.
/// </para>
/// </summary>
public static class PwnedPasswords
{
    private static readonly System.Threading.Lock Gate = new();
    private static string? watched;
    private static PwnedPasswordList? current;
    private static System.DateTime loadedStamp;
    private static System.DateTimeOffset lastLook;

    private static readonly System.Lazy<System.Net.Http.HttpClient> DefaultOnline = new(() =>
        new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(5) });

    /// <summary>"both", "download", "online" or "off", from ANJAL_PWNED_MODE (default "both").</summary>
    public static string Mode => (System.Environment.GetEnvironmentVariable("ANJAL_PWNED_MODE") ?? "both").Trim().ToLowerInvariant() switch
    {
        "download" => "download",
        "online" => "online",
        "off" => "off",
        _ => "both",
    };

    /// <summary>True when the downloaded list is used (mode "both" or "download").</summary>
    public static bool UsesDownload => Mode is "both" or "download";

    /// <summary>True when passwords are also checked online (mode "both" or "online").</summary>
    public static bool ChecksOnline => Mode is "both" or "online";

    /// <summary>
    /// The fewest leaks that make a password refused by the online check, from
    /// ANJAL_PWNED_ONLINE_MIN_COUNT (default 1: any leak).
    /// </summary>
    public static int OnlineMinimumCount =>
        int.TryParse(System.Environment.GetEnvironmentVariable("ANJAL_PWNED_ONLINE_MIN_COUNT"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1 ? n : 1;

    /// <summary>The HTTP client for the online check (tests set a stand-in; null restores the usual one).</summary>
    public static System.Net.Http.HttpClient? OnlineClient { get; set; }

    /// <summary>The range API for the online check (tests set a stand-in).</summary>
    public static string OnlineBaseUrl { get; set; } = PwnedPasswordBuilder.RangeApi;

    /// <summary>
    /// Ask Have I Been Pwned whether the password has leaked, by k-anonymity: only the first 5
    /// characters of its SHA-1 fingerprint are sent; the full fingerprint is matched here, against
    /// every leaked one in the answer. Waits at most 3 seconds.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when leaked at least <see cref="OnlineMinimumCount"/> times, false when not, null when the service could not be reached.</returns>
    public static async System.Threading.Tasks.Task<bool?> IsLeakedOnlineAsync(string password, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(password);
#pragma warning disable CA5350 // SHA-1 is the service's own format; it protects nothing here.
        string hex = System.Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        string prefix = hex[..5];
        string suffix = hex[5..];
        using var wait = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(System.TimeSpan.FromSeconds(3));
        try
        {
            using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, OnlineBaseUrl + prefix);
            request.Headers.UserAgent.ParseAdd("Anjal-password-check/1.0");
            // Padding makes every answer about the same size, so the range asked is not given away by it.
            request.Headers.TryAddWithoutValidation("Add-Padding", "true");
            using System.Net.Http.HttpResponseMessage response = await (OnlineClient ?? DefaultOnline.Value).SendAsync(request, wait.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            string body = await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false);
            int least = OnlineMinimumCount;
            foreach (System.ReadOnlySpan<char> raw in body.AsSpan().EnumerateLines())
            {
                System.ReadOnlySpan<char> line = raw.Trim();
                if (line.Length > 36 && line[35] == ':' && line[..35].Equals(suffix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return long.TryParse(line[36..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long seen) && seen >= least;
                }
            }
            return false;
        }
        catch (System.Exception ex) when (!ct.IsCancellationRequested && ex is System.Net.Http.HttpRequestException or System.OperationCanceledException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>The usual place for the list: /var/lib/anjal on Linux, the local application data folder elsewhere.</summary>
    public static string DefaultPath => System.OperatingSystem.IsLinux()
        ? "/var/lib/anjal/pwned-passwords.bin"
        : System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Anjal", "pwned-passwords.bin");

    /// <summary>The file named by ANJAL_PWNED_FILE, or <see cref="DefaultPath"/>.</summary>
    public static string ConfiguredPath => System.Environment.GetEnvironmentVariable("ANJAL_PWNED_FILE") is { Length: > 0 } p ? p : DefaultPath;

    /// <summary>The list in use, or null when there is none yet.</summary>
    public static PwnedPasswordList? Current
    {
        get
        {
            Refresh(false);
            return current;
        }
    }

    /// <summary>Check passwords against the list in this file from now on.</summary>
    /// <param name="path">The list file; it need not exist yet.</param>
    /// <param name="log">Where to report what was found.</param>
    public static void Use(string path, System.Action<string>? log = null)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        lock (Gate)
        {
            watched = path;
            loadedStamp = default;
            lastLook = default;
        }
        Refresh(true);
        PwnedPasswordList? list = current;
        log?.Invoke(list is null
            ? $"Leaked passwords: no list at {path} yet; the short built-in list is used until it is downloaded."
            : $"Leaked passwords: {list.Count:N0} fingerprints from {path}, built {list.BuiltAt:yyyy-MM-dd}.");
    }

    /// <summary>Stop checking against a file (tests).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            watched = null;
            current = null;
            loadedStamp = default;
        }
    }

    /// <summary>True when the password is on the downloaded leaked-password list.</summary>
    /// <param name="password">The password.</param>
    /// <returns>True when it is a known leaked password.</returns>
    public static bool IsLeaked(string password)
    {
        System.ArgumentNullException.ThrowIfNull(password);
        if (!UsesDownload)
        {
            return false;
        }
        PwnedPasswordList? list = Current;
        if (list is null)
        {
            return false;
        }
        try
        {
            return list.Contains(password);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            // Being replaced this very moment: look again, once.
            Refresh(true);
            return Current is PwnedPasswordList again && !ReferenceEquals(again, list) && again.Contains(password);
        }
    }

    /// <summary>Look at the file again now, rather than within the minute (after a refresh in this process).</summary>
    public static void Reload() => Refresh(true);

    private static void Refresh(bool force)
    {
        string? path = watched;
        if (path is null)
        {
            return;
        }
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        if (!force && now - lastLook < System.TimeSpan.FromMinutes(1))
        {
            return;
        }
        lock (Gate)
        {
            lastLook = now;
            System.DateTime stamp = System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : default;
            if (stamp == loadedStamp && (current is not null || stamp == default))
            {
                return;
            }
            PwnedPasswordList? fresh = null;
            if (stamp != default)
            {
                try
                {
                    fresh = PwnedPasswordList.Open(path);
                }
#pragma warning disable CA1031 // A damaged or half-written file leaves the previous list in use.
                catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.IO.InvalidDataException)
                {
                    return;
                }
#pragma warning restore CA1031
            }
            current = fresh;
            loadedStamp = stamp;
        }
    }
}

/// <summary>
/// Builds the leaked-password list file (rc.15, item 35): from Have I Been Pwned's range API
/// (the way the official Pwned Passwords downloader fetches it - a million small requests, each
/// for the fingerprints starting with five given hex digits; no password or user detail is
/// sent), or from the single file the official downloader writes. Only passwords seen in at
/// least the given number of leaks are kept. The new file is written beside the old under a
/// temporary name and swapped in only when complete.
/// </summary>
public static class PwnedPasswordBuilder
{
    /// <summary>Have I Been Pwned's range API.</summary>
    public const string RangeApi = "https://api.pwnedpasswords.com/range/";

    /// <summary>The last of the 1,048,576 ranges (five hex digits).</summary>
    public const int LastRange = 0xFFFFF;

    /// <summary>The fewest leaks a password needs to be kept, by default (owner, 7 Oct 2026).</summary>
    public const int DefaultMinimumCount = 3;

    /// <summary>The fewest leaks set by ANJAL_PWNED_MIN_COUNT, or <see cref="DefaultMinimumCount"/>.</summary>
    public static int ConfiguredMinimumCount =>
        int.TryParse(System.Environment.GetEnvironmentVariable("ANJAL_PWNED_MIN_COUNT"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1
            ? n
            : DefaultMinimumCount;

    /// <summary>Download the list from the range API and write the file.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="outputPath">Where the list goes; replaced only when the new one is complete.</param>
    /// <param name="minimumCount">The fewest leaks a password needs to be kept.</param>
    /// <param name="progress">Told the percentage done, now and then.</param>
    /// <param name="parallel">How many ranges are fetched at once.</param>
    /// <param name="lastRange">The last range to fetch (tests fetch a few; normally <see cref="LastRange"/>).</param>
    /// <param name="baseUrl">The range API (tests use a stand-in).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many leaked passwords the file holds.</returns>
    public static async System.Threading.Tasks.Task<long> BuildFromApiAsync(
        System.Net.Http.HttpClient http,
        string outputPath,
        int minimumCount = DefaultMinimumCount,
        System.IProgress<int>? progress = null,
        int parallel = 24,
        int lastRange = LastRange,
        string baseUrl = RangeApi,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(http);
        System.ArgumentNullException.ThrowIfNull(outputPath);
        System.ArgumentNullException.ThrowIfNull(baseUrl);
        System.ArgumentOutOfRangeException.ThrowIfLessThan(minimumCount, 1);
        System.ArgumentOutOfRangeException.ThrowIfLessThan(parallel, 1);
        System.ArgumentOutOfRangeException.ThrowIfNegative(lastRange);
        System.ArgumentOutOfRangeException.ThrowIfGreaterThan(lastRange, LastRange);

        string temp = outputPath + ".building";
        const int Batch = 2048;
        int lastPercent = -1;
        long count;
        try
        {
            using ListWriter writer = new(temp);
            for (int start = 0; start <= lastRange; start += Batch)
            {
                int end = System.Math.Min(lastRange, start + Batch - 1);
                ulong[][] results = new ulong[end - start + 1][];
                await System.Threading.Tasks.Parallel.ForEachAsync(
                    System.Linq.Enumerable.Range(start, end - start + 1),
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                    async (range, token) => results[range - start] = await FetchRangeAsync(http, baseUrl, range, minimumCount, token).ConfigureAwait(false)).ConfigureAwait(false);
                foreach (ulong[] block in results)
                {
                    foreach (ulong f in block)
                    {
                        writer.Add(f);
                    }
                }
                int percent = (int)((long)(end + 1) * 100 / (lastRange + 1));
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            }
            count = writer.Finish(minimumCount);
        }
        catch
        {
            // A download that stops part-way leaves no half-written file behind.
            System.IO.File.Delete(temp);
            throw;
        }
        System.IO.File.Move(temp, outputPath, overwrite: true);
        return count;
    }

    /// <summary>
    /// Write the file from the text the official Pwned Passwords downloader writes as one file
    /// (one "SHA1:count" line per leaked password, sorted). For building on another computer
    /// and copying the result to the server.
    /// </summary>
    /// <param name="reader">The text.</param>
    /// <param name="outputPath">Where the list goes; replaced only when the new one is complete.</param>
    /// <param name="minimumCount">The fewest leaks a password needs to be kept.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many leaked passwords the file holds.</returns>
    /// <exception cref="System.IO.InvalidDataException">A line is not a SHA-1 line, or the lines are not sorted.</exception>
    public static async System.Threading.Tasks.Task<long> BuildFromTextAsync(System.IO.TextReader reader, string outputPath, int minimumCount = DefaultMinimumCount, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(reader);
        System.ArgumentNullException.ThrowIfNull(outputPath);
        System.ArgumentOutOfRangeException.ThrowIfLessThan(minimumCount, 1);
        string temp = outputPath + ".building";
        long count;
        long lineNumber = 0;
        ulong last = 0;
        bool any = false;
        try
        {
            using ListWriter writer = new(temp);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                lineNumber++;
                if (line.Length == 0)
                {
                    continue;
                }
                int colon = line.IndexOf(':', System.StringComparison.Ordinal);
                if (colon != 40 || !ulong.TryParse(line.AsSpan(0, 16), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong f))
                {
                    throw new System.IO.InvalidDataException($"Line {lineNumber} is not a SHA-1 line (\"fingerprint:count\").");
                }
                if (any && f < last)
                {
                    throw new System.IO.InvalidDataException($"Line {lineNumber} is out of order; the downloader's single file is sorted. Download again in SHA-1 mode.");
                }
                last = f;
                any = true;
                if (Seen(line.AsSpan(colon + 1)) >= minimumCount)
                {
                    writer.Add(f);
                }
            }
            count = writer.Finish(minimumCount);
        }
        catch
        {
            System.IO.File.Delete(temp);
            throw;
        }
        System.IO.File.Move(temp, outputPath, overwrite: true);
        return count;
    }

    /// <summary>The fingerprints of one range seen in at least <paramref name="minimumCount"/> leaks, sorted: its five hex digits followed by each line's 35.</summary>
    /// <param name="range">The range number (0 to <see cref="LastRange"/>).</param>
    /// <param name="body">The range API's answer.</param>
    /// <param name="minimumCount">The fewest leaks a password needs to be kept.</param>
    /// <returns>The fingerprints.</returns>
    public static ulong[] ParseRange(int range, string body, int minimumCount = 1)
    {
        System.ArgumentNullException.ThrowIfNull(body);
        var list = new System.Collections.Generic.List<ulong>(1024);
        ulong prefix = (ulong)range << 44;
        foreach (System.ReadOnlySpan<char> raw in body.AsSpan().EnumerateLines())
        {
            System.ReadOnlySpan<char> line = raw.Trim();
            int colon = line.IndexOf(':');
            // Padding lines (count 0) are not leaked passwords, and so are below any minimum.
            if (colon != 35 || Seen(line[(colon + 1)..]) < minimumCount)
            {
                continue;
            }
            if (ulong.TryParse(line[..11], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong rest))
            {
                list.Add(prefix | rest);
            }
        }
        list.Sort();
        return list.ToArray();
    }

    private static long Seen(System.ReadOnlySpan<char> count) =>
        long.TryParse(count.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0;

    private static async System.Threading.Tasks.Task<ulong[]> FetchRangeAsync(System.Net.Http.HttpClient http, string baseUrl, int range, int minimumCount, System.Threading.CancellationToken ct)
    {
        string url = baseUrl + range.ToString("X5", System.Globalization.CultureInfo.InvariantCulture);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Anjal-leaked-password-list/1.0");
                using System.Net.Http.HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseRange(range, body, minimumCount);
            }
            catch (System.Exception ex) when (attempt < 6 && !ct.IsCancellationRequested && (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.IO.IOException))
            {
                await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(attempt * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes a list file: fingerprints arrive in order, their last 5 bytes are written as they
    /// come, and the group starts are filled in at the end.
    /// </summary>
    private sealed class ListWriter : System.IDisposable
    {
        private readonly System.IO.FileStream file;
        private readonly uint[] groupCounts = new uint[PwnedPasswordList.Groups];
        private readonly byte[] five = new byte[PwnedPasswordList.EntryLength];
        private ulong last;
        private bool any;
        private long count;

        public ListWriter(string path)
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            this.file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None, 1 << 20);
            this.file.SetLength(PwnedPasswordList.EntriesStart);
            this.file.Position = PwnedPasswordList.EntriesStart;
        }

        public void Add(ulong fingerprint)
        {
            if (this.any && fingerprint <= this.last)
            {
                return;
            }
            PwnedPasswordList.Write40(this.five, fingerprint & 0xFF_FFFF_FFFFUL);
            this.file.Write(this.five);
            this.groupCounts[fingerprint >> 40]++;
            this.last = fingerprint;
            this.any = true;
            this.count++;
        }

        public long Finish(int minimumCount)
        {
            if (this.count > uint.MaxValue)
            {
                throw new System.IO.InvalidDataException("More leaked passwords than one list file can hold.");
            }
            byte[] starts = new byte[(PwnedPasswordList.Groups + 1) * 4];
            uint running = 0;
            for (int g = 0; g < PwnedPasswordList.Groups; g++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(starts.AsSpan(g * 4, 4), running);
                running += this.groupCounts[g];
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(starts.AsSpan(PwnedPasswordList.Groups * 4, 4), running);
            byte[] header = new byte[PwnedPasswordList.HeaderLength];
            PwnedPasswordList.Magic.CopyTo(header, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), this.count);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), minimumCount);
            this.file.Flush();
            this.file.Position = 0;
            this.file.Write(header);
            this.file.Write(starts);
            this.file.Flush(flushToDisk: true);
            return this.count;
        }

        public void Dispose() => this.file.Dispose();
    }
}

/// <summary>
/// Where a refresh of the leaked-password list stands (rc.15, item 35): written by the server
/// beside the list as "&lt;list&gt;.status", read by the Operations console.
/// </summary>
public sealed class PwnedRefreshStatus
{
    /// <summary>"building", "ready" or "failed".</summary>
    public string State { get; set; } = "ready";

    /// <summary>How far a download has got, 0 to 100.</summary>
    public int Percent { get; set; }

    /// <summary>When the last download started.</summary>
    public System.DateTimeOffset? Started { get; set; }

    /// <summary>When the last download ended.</summary>
    public System.DateTimeOffset? Finished { get; set; }

    /// <summary>Why the last download failed, in a sentence.</summary>
    public string? Error { get; set; }

    /// <summary>The status file of a list file.</summary>
    /// <param name="listPath">The list file.</param>
    /// <returns>Its status file.</returns>
    public static string PathFor(string listPath)
    {
        System.ArgumentNullException.ThrowIfNull(listPath);
        return listPath + ".status";
    }

    /// <summary>Read the status beside a list file, or null when there is none or it cannot be read.</summary>
    /// <param name="listPath">The list file.</param>
    /// <returns>The status, or null.</returns>
    public static PwnedRefreshStatus? Read(string listPath)
    {
        System.ArgumentNullException.ThrowIfNull(listPath);
        try
        {
            string path = PathFor(listPath);
            return System.IO.File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<PwnedRefreshStatus>(System.IO.File.ReadAllText(path)) : null;
        }
#pragma warning disable CA1031 // A status that cannot be read is simply not shown.
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>Write this status beside a list file (replacing the old one whole).</summary>
    /// <param name="listPath">The list file.</param>
    public void Write(string listPath)
    {
        System.ArgumentNullException.ThrowIfNull(listPath);
        string path = PathFor(listPath);
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        System.IO.File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(this));
        System.IO.File.Move(path + ".tmp", path, overwrite: true);
    }
}
