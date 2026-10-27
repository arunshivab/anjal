using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Anjal.Client;

/// <summary>A file of a decrypted mail.</summary>
/// <param name="FileName">Its name.</param>
/// <param name="ContentType">Its type.</param>
/// <param name="Content">Its bytes.</param>
public sealed record MailFile(string FileName, string ContentType, byte[] Content);

/// <summary>A mail of the application's inbox, decrypted and read (DES-14 §14).</summary>
public sealed class DecryptedMail
{
    /// <summary>The mail exactly as it arrived.</summary>
    public byte[] Raw { get; init; } = Array.Empty<byte>();

    /// <summary>Its headers, each name with its values in order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>From.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>To.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Cc.</summary>
    public string Cc { get; init; } = string.Empty;

    /// <summary>Reply-To.</summary>
    public string ReplyTo { get; init; } = string.Empty;

    /// <summary>The subject, decoded.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The text part.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The HTML part.</summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>The files.</summary>
    public IReadOnlyList<MailFile> Attachments { get; init; } = Array.Empty<MailFile>();

    /// <summary>Files Anjal warns about (a locked archive, macros), from the envelope's meta.</summary>
    public IReadOnlyList<(string File, string Risk)> Risks { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>
/// rc.26 (DES-14 §18): one mail of the inbox as Anjal stores it - an envelope encrypted to the
/// application's own RSA key: a fresh AES-256-GCM key per mail, wrapped with RSA-OAEP-SHA256.
/// <see cref="Decrypt(RSA)"/> opens it with the application's private key.
/// </summary>
public sealed class InboxMessage
{
    private JsonElement root;

    /// <summary>The mail's id.</summary>
    public Guid Id { get; private set; }

    /// <summary>The envelope as Anjal gave it.</summary>
    public string Json { get; private set; } = string.Empty;

    /// <summary>The key it is encrypted to (the first 16 characters of its fingerprint).</summary>
    public string KeyId => this.root.GetProperty("keyId").GetString() ?? string.Empty;

    /// <summary>What Anjal keeps readable beside it: receivedAt, size, spamScore, auth, inReplyToSend, risks.</summary>
    public JsonElement Meta => this.root.TryGetProperty("meta", out JsonElement m) ? m : default;

    /// <summary>Read an envelope.</summary>
    /// <param name="id">The mail's id.</param>
    /// <param name="json">The envelope.</param>
    /// <returns>The message.</returns>
    public static InboxMessage FromJson(Guid id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("v").GetInt32() != 1 || doc.RootElement.GetProperty("alg").GetString() != "RSA-OAEP-256+A256GCM")
        {
            throw new NotSupportedException("This envelope's form is not known to this package; update Anjal.Client.");
        }
        return new InboxMessage { Id = id, Json = json, root = doc.RootElement.Clone() };
    }

    /// <summary>The mail exactly as it arrived.</summary>
    /// <param name="privateKey">The application's RSA private key.</param>
    /// <returns>The bytes.</returns>
    public byte[] DecryptRaw(RSA privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        byte[] key = privateKey.Decrypt(Convert.FromBase64String(this.root.GetProperty("wrappedKey").GetString()!), RSAEncryptionPadding.OaepSHA256);
        try
        {
            byte[] cipher = Convert.FromBase64String(this.root.GetProperty("ciphertext").GetString()!);
            byte[] plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(Convert.FromBase64String(this.root.GetProperty("iv").GetString()!), cipher, Convert.FromBase64String(this.root.GetProperty("tag").GetString()!), plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Decrypt and read the mail.</summary>
    /// <param name="privateKey">The application's RSA private key.</param>
    /// <returns>The mail.</returns>
    public DecryptedMail Decrypt(RSA privateKey)
    {
        byte[] raw = this.DecryptRaw(privateKey);
        MimeReader.Part top = MimeReader.Read(raw);
        string text = string.Empty;
        string html = string.Empty;
        var files = new List<MailFile>();
        foreach (MimeReader.Part leaf in top.Leaves())
        {
            string type = leaf.MediaType;
            string name = leaf.FileName;
            if (name.Length == 0 && type == "text/plain" && text.Length == 0)
            {
                text = leaf.Text();
            }
            else if (name.Length == 0 && type == "text/html" && html.Length == 0)
            {
                html = leaf.Text();
            }
            else if (!type.StartsWith("multipart/", StringComparison.Ordinal))
            {
                files.Add(new MailFile(name.Length > 0 ? name : "attachment", type, leaf.Body));
            }
        }
        var risks = new List<(string, string)>();
        if (this.Meta.ValueKind == JsonValueKind.Object && this.Meta.TryGetProperty("risks", out JsonElement r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement x in r.EnumerateArray())
            {
                risks.Add((x.GetProperty("file").GetString() ?? string.Empty, x.GetProperty("risk").GetString() ?? string.Empty));
            }
        }
        return new DecryptedMail
        {
            Raw = raw,
            Headers = top.Headers,
            From = top.Header("From"),
            To = top.Header("To"),
            Cc = top.Header("Cc"),
            ReplyTo = top.Header("Reply-To"),
            Subject = MimeReader.DecodeWords(top.Header("Subject")),
            Text = text,
            Html = html,
            Attachments = files,
            Risks = risks,
        };
    }
}

/// <summary>A small MIME reader for decrypted mail: headers, multipart, base64 and quoted-printable, encoded words.</summary>
internal static class MimeReader
{
    internal static Part Read(byte[] raw)
    {
        string all = Encoding.Latin1.GetString(raw);
        int split = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int gap = 4;
        if (split < 0)
        {
            split = all.IndexOf("\n\n", StringComparison.Ordinal);
            gap = 2;
        }
        string head = split < 0 ? all : all[..split];
        string body = split < 0 ? string.Empty : all[(split + gap)..];
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in head.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n ", " ", StringComparison.Ordinal).Replace("\n\t", " ", StringComparison.Ordinal).Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            string name = line[..colon].Trim();
            if (!headers.TryGetValue(name, out List<string>? values))
            {
                values = new List<string>();
                headers[name] = values;
            }
            values.Add(line[(colon + 1)..].Trim());
        }
        var part = new Part(headers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase), Encoding.Latin1.GetBytes(body));
        if (part.MediaType.StartsWith("multipart/", StringComparison.Ordinal) && part.Parameter("boundary") is { Length: > 0 } boundary)
        {
            string marker = "--" + boundary;
            foreach (string piece in body.Split(marker).Skip(1))
            {
                if (piece.StartsWith("--", StringComparison.Ordinal))
                {
                    break;
                }
                string p = piece.StartsWith("\r\n", StringComparison.Ordinal) ? piece[2..] : piece.TrimStart('\n');
                if (p.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    p = p[..^2];
                }
                part.Children.Add(Read(Encoding.Latin1.GetBytes(p)));
            }
        }
        return part;
    }

    internal static string DecodeWords(string text)
    {
        var result = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf("=?", i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            int q1 = text.IndexOf('?', start + 2);
            int q2 = q1 < 0 ? -1 : text.IndexOf('?', q1 + 1);
            int end = q2 < 0 ? -1 : text.IndexOf("?=", q2 + 1, StringComparison.Ordinal);
            if (end < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }
            string between = text[i..start];
            if (between.Trim().Length > 0 || result.Length == 0)
            {
                result.Append(between);
            }
            string charset = text[(start + 2)..q1];
            string mode = text[(q1 + 1)..q2].ToUpperInvariant();
            string data = text[(q2 + 1)..end];
            byte[] bytes = mode == "B" ? Convert.FromBase64String(data) : QuotedPrintable(data.Replace('_', ' '));
            result.Append(EncodingOf(charset).GetString(bytes));
            i = end + 2;
        }
        return result.ToString();
    }

    private static Encoding EncodingOf(string charset)
    {
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static byte[] QuotedPrintable(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '=' && i + 1 < text.Length && (text[i + 1] == '\r' || text[i + 1] == '\n'))
            {
                i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
        return bytes.ToArray();
    }

    internal sealed class Part
    {
        private readonly byte[] raw;

        internal Part(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, byte[] raw)
        {
            this.Headers = headers;
            this.raw = raw;
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

        internal List<Part> Children { get; } = new();

        internal string MediaType
        {
            get
            {
                string type = this.Header("Content-Type");
                int semi = type.IndexOf(';', StringComparison.Ordinal);
                string t = (semi >= 0 ? type[..semi] : type).Trim().ToLowerInvariant();
                return t.Length == 0 ? "text/plain" : t;
            }
        }

        internal string FileName =>
            ParameterOf(this.Header("Content-Disposition"), "filename") is { Length: > 0 } f ? DecodeWords(f) : DecodeWords(this.Parameter("name") ?? string.Empty);

        internal byte[] Body
        {
            get
            {
                string encoding = this.Header("Content-Transfer-Encoding").ToLowerInvariant();
                string text = Encoding.Latin1.GetString(this.raw);
                return encoding switch
                {
                    "base64" => Convert.FromBase64String(new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray())),
                    "quoted-printable" => QuotedPrintable(text),
                    _ => this.raw,
                };
            }
        }

        internal string Header(string name) => this.Headers.TryGetValue(name, out IReadOnlyList<string>? v) && v.Count > 0 ? v[0] : string.Empty;

        internal string? Parameter(string name) => ParameterOf(this.Header("Content-Type"), name);

        internal string Text() => EncodingOf(this.Parameter("charset") ?? "utf-8").GetString(this.Body);

        internal IEnumerable<Part> Leaves()
        {
            if (this.Children.Count == 0)
            {
                yield return this;
                yield break;
            }
            foreach (Part c in this.Children)
            {
                foreach (Part leaf in c.Leaves())
                {
                    yield return leaf;
                }
            }
        }

        private static string? ParameterOf(string header, string name)
        {
            foreach (string piece in header.Split(';').Skip(1))
            {
                int eq = piece.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && string.Equals(piece[..eq].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return piece[(eq + 1)..].Trim().Trim('"');
                }
            }
            return null;
        }
    }
}

/// <summary>
/// rc.26 (DES-14 §8): checks that a webhook's post came from Anjal - <c>Anjal-Signature:
/// t=&lt;unix&gt;, v1=&lt;hex HMAC-SHA256(secret, t + "." + body)&gt;</c> - and is recent.
/// </summary>
public static class WebhookVerifier
{
    /// <summary>Whether a post is Anjal's, within the tolerance.</summary>
    /// <param name="secret">The webhook's secret.</param>
    /// <param name="signatureHeader">The Anjal-Signature header.</param>
    /// <param name="body">The body exactly as received.</param>
    /// <param name="now">Now.</param>
    /// <param name="tolerance">How old a post may be; 5 minutes when null.</param>
    /// <returns>True when it is Anjal's.</returns>
    public static bool Verify(string secret, string signatureHeader, string body, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(signatureHeader);
        ArgumentNullException.ThrowIfNull(body);
        string? t = null;
        string? v1 = null;
        foreach (string piece in signatureHeader.Split(','))
        {
            string p = piece.Trim();
            if (p.StartsWith("t=", StringComparison.Ordinal))
            {
                t = p[2..];
            }
            else if (p.StartsWith("v1=", StringComparison.Ordinal))
            {
                v1 = p[3..];
            }
        }
        if (t is null || v1 is null || !long.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long seconds))
        {
            return false;
        }
        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > (tolerance ?? TimeSpan.FromMinutes(5)).TotalSeconds)
        {
            return false;
        }
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + "." + body));
        byte[] given;
        try
        {
            given = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
