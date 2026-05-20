using System.Text;

namespace Anjal.Dns;

/// <summary>
/// Parsed DNS response message. Only the fields we need for MX lookups
/// are exposed.
/// </summary>
public sealed class DnsMessage
{
    /// <summary>The 16-bit transaction ID echoed by the server.</summary>
    public ushort TransactionId { get; init; }

    /// <summary>Whether the response was truncated (TC flag set).</summary>
    public bool Truncated { get; init; }

    /// <summary>The RCODE (0=NoError, 3=NXDOMAIN, 2=SERVFAIL, etc.).</summary>
    public int ResponseCode { get; init; }

    /// <summary>Answers from the answer section.</summary>
    public IReadOnlyList<DnsAnswer> Answers { get; init; } = System.Array.Empty<DnsAnswer>();

    /// <summary>
    /// Parse raw response bytes. Throws <see cref="DnsException"/> on malformed input.
    /// </summary>
    /// <param name="raw">Raw DNS response.</param>
    /// <returns>Parsed message.</returns>
    public static DnsMessage Parse(byte[] raw)
    {
        System.ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length < 12)
        {
            throw new DnsException("DNS response shorter than 12 bytes.");
        }

        var reader = new DnsReader(raw);
        ushort txn = reader.ReadU16();
        ushort flags = reader.ReadU16();
        ushort qdCount = reader.ReadU16();
        ushort anCount = reader.ReadU16();
        _ = reader.ReadU16(); // NSCOUNT
        _ = reader.ReadU16(); // ARCOUNT

        bool truncated = (flags & 0x0200) != 0;
        int rcode = flags & 0x000F;

        // Skip question section.
        for (int q = 0; q < qdCount; q++)
        {
            _ = reader.ReadName();
            _ = reader.ReadU16(); // QTYPE
            _ = reader.ReadU16(); // QCLASS
        }

        var answers = new List<DnsAnswer>(anCount);
        for (int a = 0; a < anCount; a++)
        {
            string name = reader.ReadName();
            ushort type = reader.ReadU16();
            ushort cls = reader.ReadU16();
            uint ttl = reader.ReadU32();
            ushort rdLength = reader.ReadU16();
            int rdStart = reader.Position;
            int rdEnd = rdStart + rdLength;

            MxRecord? mx = null;
            System.Collections.Generic.IReadOnlyList<string>? txtStrings = null;
            if (type == 15) // MX
            {
                ushort preference = reader.ReadU16();
                string exchange = reader.ReadName();
                mx = new MxRecord { Priority = preference, Exchange = exchange.ToLowerInvariant() };
            }
            else if (type == 16) // TXT
            {
                // TXT rdata is one or more <length-prefixed string> chunks.
                // Most records are a single chunk but the wire format allows
                // multiple. Each chunk is up to 255 bytes.
                var chunks = new System.Collections.Generic.List<string>();
                while (reader.Position < rdEnd)
                {
                    int chunkLen = reader.ReadByte();
                    if (chunkLen == 0) break;
                    if (reader.Position + chunkLen > rdEnd) break;
                    chunks.Add(System.Text.Encoding.UTF8.GetString(reader.ReadBytes(chunkLen)));
                }
                txtStrings = chunks;
            }
            reader.Position = rdEnd; // Skip rdata even if we didn't parse it.

            answers.Add(new DnsAnswer
            {
                Name = name,
                Type = type,
                Class = cls,
                Ttl = ttl,
                MxRecord = mx,
                TxtStrings = txtStrings,
            });
        }

        return new DnsMessage
        {
            TransactionId = txn,
            Truncated = truncated,
            ResponseCode = rcode,
            Answers = answers,
        };
    }
}

/// <summary>
/// A single answer record. Only MX-specific data is exposed; other types
/// are kept by RDATA but not parsed.
/// </summary>
public sealed class DnsAnswer
{
    /// <summary>The owner name of this record.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Record type code (e.g. 15 = MX).</summary>
    public ushort Type { get; init; }

    /// <summary>Record class code (almost always 1 = IN).</summary>
    public ushort Class { get; init; }

    /// <summary>Time-to-live in seconds.</summary>
    public uint Ttl { get; init; }

    /// <summary>Parsed MX content if <see cref="Type"/> is 15; otherwise null.</summary>
    public MxRecord? MxRecord { get; init; }

    /// <summary>Parsed TXT content if <see cref="Type"/> is 16; otherwise null.
    /// A single DNS TXT record may contain multiple length-prefixed string
    /// chunks per RFC 1035 section 3.3.14; consumers typically concatenate
    /// them to recover the logical value (this is how SPF, DKIM, and DMARC
    /// records can exceed 255 bytes).</summary>
    public System.Collections.Generic.IReadOnlyList<string>? TxtStrings { get; init; }
}

/// <summary>
/// Exception thrown when a DNS query fails or returns malformed data.
/// </summary>
public sealed class DnsException : System.Exception
{
    /// <summary>Construct with a message.</summary>
    /// <param name="message">Description of the error.</param>
    public DnsException(string message) : base(message) { }

    /// <summary>Construct with a message and inner exception.</summary>
    /// <param name="message">Description of the error.</param>
    /// <param name="inner">The wrapped exception.</param>
    public DnsException(string message, System.Exception inner) : base(message, inner) { }

    /// <summary>Default constructor.</summary>
    public DnsException() { }
}

internal sealed class DnsReader
{
    private readonly byte[] data;

    public DnsReader(byte[] data)
    {
        this.data = data;
        this.Position = 0;
    }

    public int Position { get; set; }

    public ushort ReadU16()
    {
        if (this.Position + 2 > this.data.Length)
        {
            throw new DnsException("Unexpected end of DNS message reading u16.");
        }
        ushort v = (ushort)((this.data[this.Position] << 8) | this.data[this.Position + 1]);
        this.Position += 2;
        return v;
    }

    public int ReadByte()
    {
        if (this.Position + 1 > this.data.Length)
        {
            throw new DnsException("Unexpected end of DNS message reading byte.");
        }
        int v = this.data[this.Position];
        this.Position += 1;
        return v;
    }

    public byte[] ReadBytes(int count)
    {
        if (this.Position + count > this.data.Length)
        {
            throw new DnsException("Unexpected end of DNS message reading bytes.");
        }
        byte[] v = new byte[count];
        System.Buffer.BlockCopy(this.data, this.Position, v, 0, count);
        this.Position += count;
        return v;
    }

    public uint ReadU32()
    {
        if (this.Position + 4 > this.data.Length)
        {
            throw new DnsException("Unexpected end of DNS message reading u32.");
        }
        uint v = ((uint)this.data[this.Position] << 24)
            | ((uint)this.data[this.Position + 1] << 16)
            | ((uint)this.data[this.Position + 2] << 8)
            | this.data[this.Position + 3];
        this.Position += 4;
        return v;
    }

    public string ReadName()
    {
        // Reads a domain name with RFC 1035 section 4.1.4 compression pointers.
        var sb = new StringBuilder();
        int? returnTo = null;
        int safety = 0;

        while (true)
        {
            if (++safety > 128)
            {
                throw new DnsException("DNS name compression loop detected.");
            }
            if (this.Position >= this.data.Length)
            {
                throw new DnsException("Unexpected end of DNS message reading name.");
            }

            byte b = this.data[this.Position];

            if ((b & 0xC0) == 0xC0)
            {
                // Compression pointer.
                if (this.Position + 1 >= this.data.Length)
                {
                    throw new DnsException("Truncated DNS name compression pointer.");
                }
                int ptr = ((b & 0x3F) << 8) | this.data[this.Position + 1];
                this.Position += 2;
                returnTo ??= this.Position;
                this.Position = ptr;
                continue;
            }

            this.Position++;
            if (b == 0)
            {
                break;
            }
            if ((b & 0xC0) != 0)
            {
                throw new DnsException($"Unsupported DNS label type byte 0x{b:X2}.");
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }
            if (this.Position + b > this.data.Length)
            {
                throw new DnsException("DNS label extends past message.");
            }
            sb.Append(Encoding.ASCII.GetString(this.data, this.Position, b));
            this.Position += b;
        }

        if (returnTo.HasValue)
        {
            this.Position = returnTo.Value;
        }
        return sb.ToString();
    }
}
