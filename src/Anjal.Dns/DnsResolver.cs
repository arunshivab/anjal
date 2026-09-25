using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Anjal.Dns;

/// <summary>
/// Hand-rolled DNS resolver for MX records. Speaks RFC 1035 directly over
/// UDP/53 to a configurable nameserver. No NuGet dependency. If the response
/// is truncated (TC bit set) the resolver retries over TCP per section 4.2.2.
/// </summary>
public sealed class DnsResolver
{
    private const int DnsPort = 53;
    private const ushort TypeMx = 15;
    private const ushort TypeTxt = 16;
    private const ushort ClassIn = 1;
    private const int UdpReceiveTimeoutMs = 5000;
    private const int TcpTimeoutMs = 8000;

    private readonly IPEndPoint nameServer;

    /// <summary>
    /// Construct a resolver bound to a specific nameserver address.
    /// </summary>
    /// <param name="nameServer">The DNS server to query.</param>
    public DnsResolver(IPEndPoint nameServer)
    {
        System.ArgumentNullException.ThrowIfNull(nameServer);
        this.nameServer = nameServer;
    }

    /// <summary>
    /// Construct a resolver pointing at the first IPv4 system DNS server,
    /// falling back to 1.1.1.1 if none can be determined. Never throws.
    /// </summary>
    /// <remarks>
    /// DEF-048: on Linux the server list is read from <c>/etc/resolv.conf</c>
    /// directly. Enumerating network interfaces needs a netlink socket, which
    /// the production systemd unit does not allow (RestrictAddressFamilies);
    /// the enumeration threw and the mail server aborted at startup. Interface
    /// enumeration remains the source on Windows, and a failure of either
    /// source falls back instead of throwing.
    /// </remarks>
    /// <returns>A resolver ready to use.</returns>
    public static DnsResolver CreateFromSystem()
    {
        IPAddress server = FindSystemDnsServer(ReadResolvConf, EnumerateInterfaceDnsServers)
            ?? IPAddress.Parse("1.1.1.1");
        return new DnsResolver(new IPEndPoint(server, DnsPort));
    }

    /// <summary>
    /// The first IPv4 DNS server from <paramref name="readResolvConf"/>, else
    /// from <paramref name="enumerateInterfaces"/>, else null. A source that
    /// fails is skipped rather than allowed to throw.
    /// </summary>
    internal static IPAddress? FindSystemDnsServer(
        System.Func<string?> readResolvConf,
        System.Func<System.Collections.Generic.IEnumerable<IPAddress>> enumerateInterfaces)
    {
        try
        {
            string? text = readResolvConf();
            IPAddress? fromFile = text is null ? null : ParseResolvConf(text);
            if (fromFile is not null)
            {
                return fromFile;
            }
        }
        catch (System.IO.IOException)
        {
        }
        catch (System.UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (IPAddress addr in enumerateInterfaces())
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    return addr;
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        catch (System.PlatformNotSupportedException)
        {
        }
        catch (System.UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// The first IPv4 <c>nameserver</c> in resolv.conf text, or null. Comment
    /// lines (<c>#</c> or <c>;</c>) and IPv6 servers are skipped.
    /// </summary>
    internal static IPAddress? ParseResolvConf(string text)
    {
        System.ArgumentNullException.ThrowIfNull(text);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }
            string[] parts = line.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && string.Equals(parts[0], "nameserver", System.StringComparison.Ordinal)
                && IPAddress.TryParse(parts[1], out IPAddress? addr)
                && addr.AddressFamily == AddressFamily.InterNetwork)
            {
                return addr;
            }
        }
        return null;
    }

    private static string? ReadResolvConf()
    {
        const string path = "/etc/resolv.conf";
        return System.OperatingSystem.IsWindows() || !System.IO.File.Exists(path)
            ? null
            : System.IO.File.ReadAllText(path);
    }

    private static System.Collections.Generic.IEnumerable<IPAddress> EnumerateInterfaceDnsServers()
    {
        var found = new System.Collections.Generic.List<IPAddress>();
        foreach (System.Net.NetworkInformation.NetworkInterface nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }
            found.AddRange(nic.GetIPProperties().DnsAddresses);
        }
        return found;
    }

    /// <summary>
    /// Resolve MX records for a domain. Returns records sorted by ascending
    /// priority (most-preferred first). An empty list means no MX records
    /// were returned for the domain.
    /// </summary>
    /// <param name="domain">Domain name to query, e.g. "gmail.com".</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The MX records, sorted by priority ascending.</returns>
    /// <exception cref="DnsException">If the query fails (server error,
    /// timeout, malformed response, NXDOMAIN, etc.).</exception>
    public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MxRecord>> ResolveMxAsync(
        string domain,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new System.ArgumentException("Domain cannot be empty.", nameof(domain));
        }

        // Unpredictable transaction ids are half of what stops an off-path
        // attacker forging a reply; the other half is checking who replied.
        ushort transactionId = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 0xFFFF);
        byte[] query = BuildQuery(transactionId, domain, TypeMx);

        byte[] response = await this.SendQueryAsync(query, ct).ConfigureAwait(false);
        DnsMessage parsed = DnsMessage.Parse(response);

        if (parsed.TransactionId != transactionId)
        {
            throw new DnsException($"Transaction ID mismatch: sent {transactionId}, got {parsed.TransactionId}.");
        }
        if (parsed.Truncated)
        {
            // Retry over TCP.
            byte[] tcpResponse = await this.SendQueryViaTcpAsync(query, ct).ConfigureAwait(false);
            parsed = DnsMessage.Parse(tcpResponse);
        }
        if (parsed.ResponseCode != 0 && parsed.ResponseCode != 3)
        {
            // RCODE 0 = no error, RCODE 3 = NXDOMAIN (treat as empty list).
            throw new DnsException($"DNS server returned RCODE {parsed.ResponseCode} for {domain}.");
        }

        var result = new System.Collections.Generic.List<MxRecord>(parsed.Answers.Count);
        foreach (DnsAnswer a in parsed.Answers)
        {
            if (a.Type == TypeMx && a.MxRecord is not null)
            {
                result.Add(a.MxRecord);
            }
        }
        result.Sort((x, y) => x.Priority.CompareTo(y.Priority));
        return result;
    }

    /// <summary>
    /// Look up TXT records for a domain. Returns the list of TXT records,
    /// each as a single concatenated string (per RFC 6376 / 7208 / 7489
    /// conventions, multiple length-prefixed chunks in one TXT record are
    /// joined). Returns an empty list if the domain has no TXT records or
    /// does not exist (NXDOMAIN treated as empty).
    /// </summary>
    /// <param name="domain">Domain name to query, e.g. "gmail.com" or
    /// "default._domainkey.example.com".</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="DnsException">If the query fails (server error,
    /// timeout, malformed response).</exception>
    public async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<string>> LookupTxtAsync(
        string domain,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new System.ArgumentException("Domain cannot be empty.", nameof(domain));
        }

        // Unpredictable transaction ids are half of what stops an off-path
        // attacker forging a reply; the other half is checking who replied.
        ushort transactionId = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 0xFFFF);
        byte[] query = BuildQuery(transactionId, domain, TypeTxt);

        byte[] response = await this.SendQueryAsync(query, ct).ConfigureAwait(false);
        DnsMessage parsed = DnsMessage.Parse(response);

        if (parsed.TransactionId != transactionId)
        {
            throw new DnsException($"Transaction ID mismatch: sent {transactionId}, got {parsed.TransactionId}.");
        }
        if (parsed.Truncated)
        {
            byte[] tcpResponse = await this.SendQueryViaTcpAsync(query, ct).ConfigureAwait(false);
            parsed = DnsMessage.Parse(tcpResponse);
        }
        if (parsed.ResponseCode != 0 && parsed.ResponseCode != 3)
        {
            throw new DnsException($"DNS server returned RCODE {parsed.ResponseCode} for TXT {domain}.");
        }

        var result = new System.Collections.Generic.List<string>(parsed.Answers.Count);
        foreach (DnsAnswer a in parsed.Answers)
        {
            if (a.Type == TypeTxt && a.TxtStrings is not null)
            {
                // Join multiple length-prefixed chunks into one logical record value.
                result.Add(string.Concat(a.TxtStrings));
            }
        }
        return result;
    }

    private async System.Threading.Tasks.Task<byte[]> SendQueryAsync(byte[] query, System.Threading.CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = UdpReceiveTimeoutMs;
        await udp.SendAsync(query, this.nameServer, ct).ConfigureAwait(false);

        using var timeoutCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UdpReceiveTimeoutMs);
        try
        {
            // Only the server that was asked may answer. A datagram from any
            // other address is ignored and the wait continues.
            while (true)
            {
                UdpReceiveResult res = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                if (res.RemoteEndPoint.Address.Equals(this.nameServer.Address) && res.RemoteEndPoint.Port == this.nameServer.Port)
                {
                    return res.Buffer;
                }
            }
        }
        catch (System.OperationCanceledException)
        {
            throw new DnsException("Timeout waiting for DNS response.");
        }
    }

    private async System.Threading.Tasks.Task<byte[]> SendQueryViaTcpAsync(byte[] query, System.Threading.CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var timeoutCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TcpTimeoutMs);

        try
        {
            await tcp.ConnectAsync(this.nameServer.Address, this.nameServer.Port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            throw new DnsException("Timeout connecting to DNS server via TCP.");
        }

        using NetworkStream stream = tcp.GetStream();

        // TCP DNS prefixes payload with a 2-byte big-endian length.
        byte[] lengthPrefix = new byte[2];
        lengthPrefix[0] = (byte)((query.Length >> 8) & 0xFF);
        lengthPrefix[1] = (byte)(query.Length & 0xFF);
        await stream.WriteAsync(lengthPrefix.AsMemory(0, 2), timeoutCts.Token).ConfigureAwait(false);
        await stream.WriteAsync(query.AsMemory(0, query.Length), timeoutCts.Token).ConfigureAwait(false);

        await ReadExactlyAsync(stream, lengthPrefix, 0, 2, timeoutCts.Token).ConfigureAwait(false);
        int responseLen = (lengthPrefix[0] << 8) | lengthPrefix[1];

        byte[] buf = new byte[responseLen];
        await ReadExactlyAsync(stream, buf, 0, responseLen, timeoutCts.Token).ConfigureAwait(false);
        return buf;
    }

    private static async System.Threading.Tasks.Task ReadExactlyAsync(NetworkStream s, byte[] buf, int offset, int count, System.Threading.CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new DnsException("DNS TCP stream closed before full response was received.");
            }
            read += n;
        }
    }

    /// <summary>
    /// Encode a DNS query message. Exposed as static for use by test harnesses.
    /// </summary>
    /// <param name="transactionId">The transaction ID to put in the header.</param>
    /// <param name="domain">The domain to query.</param>
    /// <param name="recordType">The record type code (e.g. 15 for MX).</param>
    /// <returns>The raw query bytes.</returns>
    public static byte[] BuildQuery(ushort transactionId, string domain, ushort recordType)
    {
        System.ArgumentNullException.ThrowIfNull(domain);
        using var ms = new System.IO.MemoryStream();

        // Header: 12 bytes.
        WriteU16(ms, transactionId);
        WriteU16(ms, 0x0100); // QR=0, OPCODE=0, RD=1 (recursion desired).
        WriteU16(ms, 1); // QDCOUNT
        WriteU16(ms, 0); // ANCOUNT
        WriteU16(ms, 0); // NSCOUNT
        WriteU16(ms, 0); // ARCOUNT

        // Question: encoded name + qtype + qclass.
        WriteEncodedName(ms, domain.ToLowerInvariant());
        WriteU16(ms, recordType);
        WriteU16(ms, ClassIn);

        return ms.ToArray();
    }

    private static void WriteU16(System.IO.MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)((v >> 8) & 0xFF));
        ms.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteEncodedName(System.IO.MemoryStream ms, string name)
    {
        foreach (string label in name.Split('.'))
        {
            if (label.Length == 0)
            {
                continue;
            }
            if (label.Length > 63)
            {
                throw new System.ArgumentException("DNS label exceeds 63 octets.", nameof(name));
            }
            ms.WriteByte((byte)label.Length);
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            ms.Write(labelBytes, 0, labelBytes.Length);
        }
        ms.WriteByte(0); // Root label.
    }
}
