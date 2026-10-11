using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Webmail.Services;

/// <summary>Where an address is, as the location list says (rc.15, item 59).</summary>
/// <param name="City">The town or city; may be empty.</param>
/// <param name="Region">The state or province; may be empty.</param>
/// <param name="Country">The two-letter country code; "ZZ" for addresses no country holds.</param>
public sealed record IpPlace(string City, string Region, string Country)
{
    /// <summary>Town and country for people to read, for example "Pune, India".</summary>
    public string Text
    {
        get
        {
            string country = CountryName(this.Country);
            return this.City.Length > 0 && country.Length > 0 ? this.City + ", " + country : this.City.Length > 0 ? this.City : country;
        }
    }

    /// <summary>A country's name in English from its two-letter code; the code when the name is not known here.</summary>
    /// <param name="code">The code, for example "IN".</param>
    /// <returns>The name, for example "India".</returns>
    public static string CountryName(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "ZZ")
        {
            return string.Empty;
        }
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }
}

/// <summary>
/// Where sign-ins come from (rc.15, item 59; owner, 8 Oct 2026): the free
/// DB-IP "IP to City Lite" list (Creative Commons Attribution 4.0 - every
/// page showing a place credits DB-IP with a link), downloaded to the
/// server once a month and packed into one file that is searched on disk.
/// No address ever leaves the server to be looked up.
/// </summary>
/// <remarks>
/// Settings: ANJAL_GEO_MODE - "download" (the default on Linux: fetch the
/// list monthly), "file" (use a list put in place by hand, never download),
/// or "off" (the default elsewhere); ANJAL_GEO_FILE - where the packed list
/// is kept (default /var/lib/anjal/ip-locations.bin on Linux).
/// </remarks>
public static class IpLocations
{
    /// <summary>The credit DB-IP's licence asks for, with its link.</summary>
    public const string Credit = "IP Geolocation by DB-IP";

    /// <summary>Where the credit links.</summary>
    public const string CreditLink = "https://db-ip.com";

    /// <summary>The monthly file's address; {month} is yyyy-MM.</summary>
    public const string SourceUrl = "https://download.db-ip.com/free/dbip-city-lite-{month}.csv.gz";

    private const int HeaderLength = 32;
    private static readonly byte[] Magic = "ANJALIP1"u8.ToArray();
    private static readonly object Gate = new();
    private static int refreshing;

    /// <summary>"download", "file" or "off".</summary>
    public static string Mode => (Environment.GetEnvironmentVariable("ANJAL_GEO_MODE") ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "download" => "download",
        "file" => "file",
        "off" => "off",
        _ => OperatingSystem.IsLinux() ? "download" : "off",
    };

    /// <summary>The usual place for the packed list.</summary>
    public static string DefaultPath => OperatingSystem.IsLinux()
        ? "/var/lib/anjal/ip-locations.bin"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anjal", "ip-locations.bin");

    /// <summary>The file named by ANJAL_GEO_FILE, or <see cref="DefaultPath"/>.</summary>
    public static string ConfiguredPath => Environment.GetEnvironmentVariable("ANJAL_GEO_FILE") is { Length: > 0 } p ? p : DefaultPath;

    /// <summary>When the list in use was built, or null when there is none.</summary>
    /// <param name="path">The packed list; the configured one when null.</param>
    /// <returns>The moment, or null.</returns>
    public static DateTimeOffset? BuiltAt(string? path = null)
    {
        try
        {
            using FileStream f = File.OpenRead(path ?? ConfiguredPath);
            byte[] head = new byte[HeaderLength];
            if (f.Read(head, 0, HeaderLength) != HeaderLength || !head.AsSpan(0, 8).SequenceEqual(Magic))
            {
                return null;
            }
            return DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(8)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>True for an address on this computer or a private network, which no list places.</summary>
    /// <param name="ip">The address.</param>
    /// <returns>Whether it is private.</returns>
    public static bool IsPrivate(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0;
        }
        return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || ip.Equals(IPAddress.IPv6None);
    }

    /// <summary>The network an address belongs to, for telling a new network from a known one: /24 for IPv4, /48 for IPv6.</summary>
    /// <param name="ip">The address.</param>
    /// <returns>The network, for example "203.0.113.0/24".</returns>
    public static string NetworkOf(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{b[0]}.{b[1]}.{b[2]}.0/24");
        }
        byte[] net = new byte[16];
        Array.Copy(b, net, 6);
        return new IPAddress(net) + "/48";
    }

    /// <summary>Where an address is, from the packed list; null when there is no list or the address is not in it.</summary>
    /// <param name="ip">The address.</param>
    /// <param name="path">The packed list; the configured one when null.</param>
    /// <returns>The place, or null.</returns>
    public static IpPlace? Find(IPAddress ip, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        try
        {
            using FileStream f = new(path ?? ConfiguredPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            byte[] head = new byte[HeaderLength];
            if (f.Read(head, 0, HeaderLength) != HeaderLength || !head.AsSpan(0, 8).SequenceEqual(Magic))
            {
                return null;
            }
            int v4 = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(16));
            int v6 = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(20));
            long v6At = HeaderLength + (v4 * 8L);
            long placesAt = v6At + (v6 * 20L);
            int place = ip.AddressFamily == AddressFamily.InterNetwork
                ? Search(f, HeaderLength, v4, 8, 4, ip.GetAddressBytes())
                : Search(f, v6At, v6, 20, 16, ip.GetAddressBytes());
            return place < 0 ? null : ReadPlace(f, placesAt, place);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pack DB-IP's city list (its CSV: start, end, continent, country, state,
    /// city, latitude, longitude) into the file <see cref="Find"/> searches.
    /// Written beside the target and moved into place, so a reader never sees half a file.
    /// </summary>
    /// <param name="csv">The CSV text.</param>
    /// <param name="outputPath">Where the packed list goes.</param>
    /// <param name="builtAt">When the list was made.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many address ranges it holds.</returns>
    public static async Task<long> BuildAsync(TextReader csv, string outputPath, DateTimeOffset builtAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(outputPath);
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        var placeList = new List<string>();
        var v4 = new List<(uint Start, int Place)>();
        var v6 = new List<(UInt128 Start, int Place)>();
        string? line;
        while ((line = await csv.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            List<string> cells = Cells(line);
            if (cells.Count < 6 || !IPAddress.TryParse(cells[0], out IPAddress? start))
            {
                continue;
            }
            string key = cells[5].Trim() + "\t" + cells[4].Trim() + "\t" + cells[3].Trim().ToUpperInvariant();
            if (!places.TryGetValue(key, out int place))
            {
                place = placeList.Count;
                places[key] = place;
                placeList.Add(key);
            }
            byte[] b = start.GetAddressBytes();
            if (start.AddressFamily == AddressFamily.InterNetwork)
            {
                v4.Add((BinaryPrimitives.ReadUInt32BigEndian(b), place));
            }
            else
            {
                v6.Add((BinaryPrimitives.ReadUInt128BigEndian(b), place));
            }
        }
        v4.Sort((a, b) => a.Start.CompareTo(b.Start));
        v6.Sort((a, b) => a.Start.CompareTo(b.Start));

        string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
        Directory.CreateDirectory(dir);
        string temp = outputPath + ".building";
        await using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            byte[] head = new byte[HeaderLength];
            Magic.CopyTo(head, 0);
            BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(8), builtAt.ToUnixTimeSeconds());
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(16), v4.Count);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(20), v6.Count);
            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(24), placeList.Count);
            await f.WriteAsync(head, ct).ConfigureAwait(false);
            byte[] e4 = new byte[8];
            foreach ((uint s, int p) in v4)
            {
                BinaryPrimitives.WriteUInt32BigEndian(e4, s);
                BinaryPrimitives.WriteInt32LittleEndian(e4.AsSpan(4), p);
                await f.WriteAsync(e4, ct).ConfigureAwait(false);
            }
            byte[] e6 = new byte[20];
            foreach ((UInt128 s, int p) in v6)
            {
                BinaryPrimitives.WriteUInt128BigEndian(e6, s);
                BinaryPrimitives.WriteInt32LittleEndian(e6.AsSpan(16), p);
                await f.WriteAsync(e6, ct).ConfigureAwait(false);
            }
            long offsetsAt = HeaderLength + (v4.Count * 8L) + (v6.Count * 20L);
            long textAt = offsetsAt + (placeList.Count * 8L);
            byte[] off = new byte[8];
            long at = textAt;
            var texts = placeList.Select(p => Encoding.UTF8.GetBytes(p.Length > 600 ? p[..600] : p)).ToList();
            foreach (byte[] t in texts)
            {
                BinaryPrimitives.WriteInt64LittleEndian(off, at);
                await f.WriteAsync(off, ct).ConfigureAwait(false);
                at += 2 + t.Length;
            }
            byte[] len = new byte[2];
            foreach (byte[] t in texts)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)t.Length);
                await f.WriteAsync(len, ct).ConfigureAwait(false);
                await f.WriteAsync(t, ct).ConfigureAwait(false);
            }
        }
        File.Move(temp, outputPath, overwrite: true);
        return v4.Count + v6.Count;
    }

    /// <summary>
    /// Download this month's list (or last month's, early in a month) and pack
    /// it, when the mode is "download" and the list in use is missing or more
    /// than 32 days old. Runs one at a time; never throws.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="log">Where progress is reported.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when a new list was put in place.</returns>
    public static async Task<bool> RefreshIfDueAsync(DateTimeOffset now, Action<string> log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        string path = ConfiguredPath;
        if (Mode != "download" || (BuiltAt(path) is DateTimeOffset built && now - built < TimeSpan.FromDays(32)))
        {
            return false;
        }
        if (Interlocked.Exchange(ref refreshing, 1) == 1)
        {
            return false;
        }
        string download = path + ".download";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Anjal-mail-server");
            foreach (DateTimeOffset month in new[] { now, now.AddMonths(-1) })
            {
                string url = SourceUrl.Replace("{month}", month.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                using HttpResponseMessage res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                await using (FileStream file = File.Create(download))
                {
                    await res.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }
                long ranges;
                await using (FileStream raw = File.OpenRead(download))
                await using (var gz = new GZipStream(raw, CompressionMode.Decompress))
                using (var reader = new StreamReader(gz, Encoding.UTF8))
                {
                    ranges = await BuildAsync(reader, path, now, ct).ConfigureAwait(false);
                }
                log(string.Create(CultureInfo.InvariantCulture, $"IP locations: {ranges:N0} address ranges from {url}. {Credit} ({CreditLink}), Creative Commons Attribution 4.0."));
                return true;
            }
            log("IP locations: the monthly list could not be downloaded from db-ip.com; tried again in an hour.");
            return false;
        }
#pragma warning disable CA1031 // A failed download leaves the old list (or none) in use; sign-in never depends on it.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"IP locations: not refreshed ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            try
            {
                File.Delete(download);
            }
            catch (IOException)
            {
                // Removed on the next try.
            }
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    // Binary search for the last range starting at or below the address.
    private static int Search(FileStream f, long at, int count, int width, int keyLength, byte[] key)
    {
        if (count == 0)
        {
            return -1;
        }
        byte[] entry = new byte[width];
        int lo = 0;
        int hi = count - 1;
        int found = -1;
        lock (Gate)
        {
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                f.Seek(at + ((long)mid * width), SeekOrigin.Begin);
                f.ReadExactly(entry);
                int cmp = entry.AsSpan(0, keyLength).SequenceCompareTo(key);
                if (cmp <= 0)
                {
                    found = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(keyLength));
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
        }
        return found;
    }

    private static IpPlace? ReadPlace(FileStream f, long offsetsAt, int place)
    {
        byte[] off = new byte[8];
        f.Seek(offsetsAt + (place * 8L), SeekOrigin.Begin);
        f.ReadExactly(off);
        f.Seek(BinaryPrimitives.ReadInt64LittleEndian(off), SeekOrigin.Begin);
        byte[] len = new byte[2];
        f.ReadExactly(len);
        byte[] text = new byte[BinaryPrimitives.ReadUInt16LittleEndian(len)];
        f.ReadExactly(text);
        string[] parts = Encoding.UTF8.GetString(text).Split('\t');
        return parts.Length == 3 ? new IpPlace(parts[0], parts[1], parts[2]) : null;
    }

    // One CSV line's cells; a cell may be quoted, with "" for a quote inside.
    private static List<string> Cells(string line)
    {
        var cells = new List<string>(8);
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        cells.Add(sb.ToString());
        return cells;
    }
}
