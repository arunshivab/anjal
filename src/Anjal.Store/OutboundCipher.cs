using System.Text;

namespace Anjal.Store;

/// <summary>
/// rc.25 (DES-15 §7.4, D-244): a message waiting in the outgoing queue. It must be readable when it is
/// handed to the receiving server, so it is sealed to the mail server's seal key (the same key that
/// locks the DKIM keys; only the mail server holds its private half) - the webmail and a copy of the
/// database cannot read it. Beside it, a small record (From, To, Subject, Date, Message-ID) is sealed
/// to whoever may see it: the sending person's mailbox key, or the organisation's recovery key for an
/// application's mail - so Outbox and Sending systems still show what was sent, to the right people.
/// When the message is delivered, the sealed message is removed and only the record stays.
/// <code>
/// "ANJQ" | version 1 | record length (u32, big-endian) | record (envelope, or a plain header block) | queued message (envelope; absent once removed)
/// </code>
/// The record comes first so that a list reading only the first 8 KB still has it.
/// </summary>
public static class OutboundCipher
{
    private const int HeadLength = 9;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ANJQ");
    private static readonly string[] RecordHeaders = { "From", "To", "Cc", "Subject", "Date", "Message-ID" };

    /// <summary>
    /// In the mail server only: opens a queued message with the server's private seal key. Unset in
    /// the webmail, which therefore sees only the record.
    /// </summary>
    public static Func<byte[], byte[]?>? ServerOpener { get; set; }

    /// <summary>Whether stored queue bytes are a sealed container.</summary>
    /// <param name="stored">The stored bytes.</param>
    /// <returns>Whether they are.</returns>
    public static bool IsSealed(ReadOnlySpan<byte> stored) => stored.Length >= HeadLength && stored[..4].SequenceEqual(Magic) && stored[4] == 1;

    /// <summary>The stored form of a message to queue.</summary>
    /// <param name="raw">The message.</param>
    /// <param name="sealPublic">The mail server's seal public key; null keeps the message as it is (development, tests).</param>
    /// <param name="recordPublic">Who may read the record; null keeps the record plain (an individual's mail).</param>
    /// <returns>The bytes to store.</returns>
    public static byte[] Seal(byte[] raw, byte[]? sealPublic, byte[]? recordPublic)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (sealPublic is null || IsSealed(raw))
        {
            return raw;
        }
        byte[] header = RecordOf(raw);
        byte[] record = recordPublic is null ? header : MailEnvelope.SealToPublicKey(header, recordPublic);
        byte[] queued = MailEnvelope.SealToPublicKey(raw, sealPublic);
        byte[] result = new byte[HeadLength + record.Length + queued.Length];
        Magic.CopyTo(result, 0);
        result[4] = 1;
        result[5] = (byte)(record.Length >> 24);
        result[6] = (byte)(record.Length >> 16);
        result[7] = (byte)(record.Length >> 8);
        result[8] = (byte)record.Length;
        record.CopyTo(result, HeadLength);
        queued.CopyTo(result, HeadLength + record.Length);
        return result;
    }

    /// <summary>
    /// What a reader of the queue sees: the whole message in the mail server; elsewhere the record,
    /// opened with the keys of the current flow - or nothing when it cannot be opened here.
    /// </summary>
    /// <param name="stored">The stored bytes (possibly only the first part of them).</param>
    /// <returns>The bytes to work with.</returns>
    public static byte[] ForReader(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!IsSealed(stored))
        {
            return stored;
        }
        long recordLength = ((long)stored[5] << 24) | ((long)stored[6] << 16) | ((long)stored[7] << 8) | stored[8];
        if (HeadLength + recordLength > stored.Length)
        {
            return Array.Empty<byte>();
        }
        int queuedAt = HeadLength + (int)recordLength;
        if (ServerOpener is Func<byte[], byte[]?> open && stored.Length > queuedAt && MailEnvelope.IsEnvelope(stored.AsSpan(queuedAt)))
        {
            try
            {
                if (open(stored[queuedAt..]) is byte[] message)
                {
                    return message;
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A part read cut short (a list's first 8 KB): the record below.
            }
        }
        byte[] record = stored.AsSpan(HeadLength, (int)recordLength).ToArray();
        if (!MailEnvelope.IsEnvelope(record))
        {
            return record;
        }
        try
        {
            return MailEnvelope.Open(record, MailKeyring.Current) ?? Array.Empty<byte>();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>The stored bytes once the message is delivered: the record only (null when not sealed).</summary>
    /// <param name="stored">The stored bytes.</param>
    /// <returns>The record container, or null.</returns>
    public static byte[]? RecordOnly(byte[] stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!IsSealed(stored))
        {
            return null;
        }
        long recordLength = ((long)stored[5] << 24) | ((long)stored[6] << 16) | ((long)stored[7] << 8) | stored[8];
        return HeadLength + recordLength <= stored.Length ? stored[..(HeadLength + (int)recordLength)] : null;
    }

    // The few header lines a record keeps, unfolded, then a blank line.
    private static byte[] RecordOf(byte[] raw)
    {
        int end = HeaderEnd(raw);
        string head = Encoding.UTF8.GetString(raw, 0, end).Replace("\r\n ", " ", StringComparison.Ordinal).Replace("\r\n\t", " ", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (string line in head.Split("\r\n"))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && Array.Exists(RecordHeaders, h => string.Equals(h, line[..colon].Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(line).Append("\r\n");
            }
        }
        sb.Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int HeaderEnd(byte[] raw)
    {
        for (int i = 0; i + 3 < raw.Length; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
            {
                return i + 2;
            }
        }
        return raw.Length;
    }
}
