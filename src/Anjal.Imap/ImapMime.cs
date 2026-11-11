using System.Globalization;
using System.Text;

namespace Anjal.Imap;

/// <summary>
/// rc.28 (DES-20 §3): a message's structure read from its raw bytes, as IMAP needs it - where each part's
/// header and body start and end, its type, parameters and encoding - so BODYSTRUCTURE, ENVELOPE and
/// BODY[section] answer with the exact bytes the message holds.
/// </summary>
public sealed class ImapMime
{
    private static readonly byte[] CrLf = { 13, 10 };

    private ImapMime(byte[] raw, int headerStart, int bodyStart, int bodyEnd, IReadOnlyDictionary<string, string> defaults, int depth)
    {
        this.Raw = raw;
        this.HeaderStart = headerStart;
        this.BodyStart = bodyStart;
        this.BodyEnd = bodyEnd;
        this.Headers = ParseHeaders(raw, headerStart, bodyStart);
        string contentType = this.Header("Content-Type");
        if (contentType.Length == 0)
        {
            contentType = defaults.TryGetValue("type", out string? d) ? d : "text/plain; charset=us-ascii";
        }
        (string type, Dictionary<string, string> parameters) = ParseValue(contentType);
        int slash = type.IndexOf('/', StringComparison.Ordinal);
        this.Type = slash > 0 ? type[..slash].ToLowerInvariant() : "text";
        this.Subtype = slash > 0 ? type[(slash + 1)..].ToLowerInvariant() : "plain";
        this.Parameters = parameters;
        if (this.Type == "text" && !this.Parameters.ContainsKey("charset"))
        {
            this.Parameters["charset"] = "us-ascii";
        }
        this.Children = new List<ImapMime>();
        // Parts nested deeper than the inbound parser allows (Anjal.Mime's limit) are shown as they are,
        // unopened: a message built to be deep cannot make the structure endless.
        if (depth >= MaxDepth)
        {
            return;
        }
        if (this.Type == "multipart" && this.Parameters.TryGetValue("boundary", out string? boundary) && boundary.Length > 0)
        {
            var childDefaults = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = this.Subtype == "digest" ? "message/rfc822" : "text/plain; charset=us-ascii",
            };
            foreach ((int start, int end) in SplitParts(raw, bodyStart, bodyEnd, boundary))
            {
                this.Children.Add(Parse(raw, start, end, childDefaults, depth + 1));
            }
        }
        else if (this.Type == "message" && this.Subtype == "rfc822")
        {
            this.Message = Parse(raw, bodyStart, bodyEnd, new Dictionary<string, string>(StringComparer.Ordinal), depth + 1);
        }
    }

    /// <summary>The whole message's bytes.</summary>
    public byte[] Raw { get; }

    /// <summary>Where this part's header starts.</summary>
    public int HeaderStart { get; }

    /// <summary>Where its body starts (just after the blank line).</summary>
    public int BodyStart { get; }

    /// <summary>Where its body ends.</summary>
    public int BodyEnd { get; }

    /// <summary>The header fields, unfolded, in order.</summary>
    public IReadOnlyList<(string Name, string Value)> Headers { get; }

    /// <summary>The type, lower case ("text").</summary>
    public string Type { get; }

    /// <summary>The subtype, lower case ("plain").</summary>
    public string Subtype { get; }

    /// <summary>The Content-Type parameters, names in lower case.</summary>
    public Dictionary<string, string> Parameters { get; }

    /// <summary>The parts of a multipart.</summary>
    public List<ImapMime> Children { get; }

    /// <summary>The message inside a message/rfc822 part.</summary>
    public ImapMime? Message { get; }

    /// <summary>Read a message.</summary>
    /// <param name="raw">Its bytes.</param>
    /// <returns>Its structure.</returns>
    public static ImapMime Parse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Parse(raw, 0, raw.Length, new Dictionary<string, string>(StringComparer.Ordinal), 0);
    }

    /// <summary>The deepest part opened; the same limit as the inbound parser's.</summary>
    public const int MaxDepth = 32;

    /// <summary>A header field's value (the first), unfolded; empty when absent.</summary>
    /// <param name="name">The field.</param>
    /// <returns>The value.</returns>
    public string Header(string name)
    {
        foreach ((string n, string v) in this.Headers)
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return string.Empty;
    }

    /// <summary>The header bytes, with the blank line after them.</summary>
    /// <returns>The bytes.</returns>
    public byte[] HeaderBytes() => this.Raw[this.HeaderStart..this.BodyStart];

    /// <summary>The body bytes.</summary>
    /// <returns>The bytes.</returns>
    public byte[] BodyBytes() => this.Raw[this.BodyStart..this.BodyEnd];

    /// <summary>The header fields named (or all but those named), with a blank line at the end.</summary>
    /// <param name="names">The fields.</param>
    /// <param name="not">True for every field except those.</param>
    /// <returns>The bytes.</returns>
    public byte[] HeaderFields(IReadOnlyCollection<string> names, bool not)
    {
        ArgumentNullException.ThrowIfNull(names);
        var outp = new List<byte>();
        foreach ((int start, int end, string name) in RawFields(this.Raw, this.HeaderStart, this.BodyStart))
        {
            bool listed = names.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (listed != not)
            {
                outp.AddRange(this.Raw.AsSpan(start, end - start).ToArray());
            }
        }
        outp.AddRange(CrLf);
        return outp.ToArray();
    }

    /// <summary>The part a section number names ("1", "2.3"); null when there is none.</summary>
    /// <param name="numbers">The numbers.</param>
    /// <returns>The part.</returns>
    public ImapMime? PartAt(IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        ImapMime current = this;
        foreach (int n in numbers)
        {
            ImapMime container = current.Message is not null && current.Type == "message" ? current.Message : current;
            if (container.Type == "multipart")
            {
                if (n < 1 || n > container.Children.Count)
                {
                    return null;
                }
                current = container.Children[n - 1];
            }
            else if (n == 1)
            {
                current = container;
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>The BODYSTRUCTURE (with extension data) or BODY (without) of this part.</summary>
    /// <param name="extended">True for BODYSTRUCTURE.</param>
    /// <returns>The IMAP text.</returns>
    public string Structure(bool extended)
    {
        var sb = new StringBuilder();
        this.WriteStructure(sb, extended);
        return sb.ToString();
    }

    /// <summary>The ENVELOPE of this message.</summary>
    /// <returns>The IMAP text.</returns>
    public string Envelope()
    {
        var sb = new StringBuilder("(");
        sb.Append(ImapText.NString(this.Header("Date"))).Append(' ');
        sb.Append(ImapText.NString(this.Header("Subject"))).Append(' ');
        string from = this.Header("From");
        string sender = this.Header("Sender");
        string replyTo = this.Header("Reply-To");
        sb.Append(Addresses(from)).Append(' ');
        sb.Append(Addresses(sender.Length > 0 ? sender : from)).Append(' ');
        sb.Append(Addresses(replyTo.Length > 0 ? replyTo : from)).Append(' ');
        sb.Append(Addresses(this.Header("To"))).Append(' ');
        sb.Append(Addresses(this.Header("Cc"))).Append(' ');
        sb.Append(Addresses(this.Header("Bcc"))).Append(' ');
        sb.Append(ImapText.NString(this.Header("In-Reply-To"))).Append(' ');
        sb.Append(ImapText.NString(this.Header("Message-ID"))).Append(')');
        return sb.ToString();
    }

    private static ImapMime Parse(byte[] raw, int start, int end, IReadOnlyDictionary<string, string> defaults, int depth)
    {
        int bodyStart = FindBodyStart(raw, start, end);
        return new ImapMime(raw, start, bodyStart, end, defaults, depth);
    }

    private void WriteStructure(StringBuilder sb, bool extended)
    {
        if (this.Type == "multipart")
        {
            sb.Append('(');
            if (this.Children.Count == 0)
            {
                // A multipart with no parts is shown as one empty text part (RFC 3501 needs at least one).
                sb.Append("(\"text\" \"plain\" (\"charset\" \"us-ascii\") NIL NIL \"7bit\" 0 0)");
            }
            foreach (ImapMime child in this.Children)
            {
                child.WriteStructure(sb, extended);
            }
            sb.Append(' ').Append(ImapText.Quote(this.Subtype));
            if (extended)
            {
                sb.Append(' ').Append(ParamList(this.Parameters)).Append(' ').Append(this.Disposition()).Append(" NIL NIL");
            }
            sb.Append(')');
            return;
        }
        string encoding = this.Header("Content-Transfer-Encoding").Trim().ToLowerInvariant();
        int size = this.BodyEnd - this.BodyStart;
        sb.Append('(').Append(ImapText.Quote(this.Type)).Append(' ').Append(ImapText.Quote(this.Subtype)).Append(' ');
        sb.Append(ParamList(this.Parameters)).Append(' ');
        sb.Append(ImapText.NString(this.Header("Content-ID"))).Append(' ');
        sb.Append(ImapText.NString(this.Header("Content-Description"))).Append(' ');
        sb.Append(ImapText.Quote(encoding.Length > 0 ? encoding : "7bit")).Append(' ');
        sb.Append(size.ToString(CultureInfo.InvariantCulture));
        if (this.Type == "message" && this.Subtype == "rfc822" && this.Message is not null)
        {
            sb.Append(' ').Append(this.Message.Envelope()).Append(' ');
            this.Message.WriteStructure(sb, extended);
            sb.Append(' ').Append(Lines(this.Raw, this.BodyStart, this.BodyEnd).ToString(CultureInfo.InvariantCulture));
        }
        else if (this.Type == "text")
        {
            sb.Append(' ').Append(Lines(this.Raw, this.BodyStart, this.BodyEnd).ToString(CultureInfo.InvariantCulture));
        }
        if (extended)
        {
            sb.Append(' ').Append(ImapText.NString(this.Header("Content-MD5"))).Append(' ').Append(this.Disposition()).Append(" NIL NIL");
        }
        sb.Append(')');
    }

    private string Disposition()
    {
        string d = this.Header("Content-Disposition");
        if (d.Length == 0)
        {
            return "NIL";
        }
        (string kind, Dictionary<string, string> p) = ParseValue(d);
        return "(" + ImapText.Quote(kind.ToLowerInvariant()) + " " + ParamList(p) + ")";
    }

    private static string ParamList(Dictionary<string, string> p)
    {
        if (p.Count == 0)
        {
            return "NIL";
        }
        return "(" + string.Join(' ', p.Select(kv => ImapText.Quote(kv.Key) + " " + ImapText.Quote(kv.Value))) + ")";
    }

    private static string Addresses(string value)
    {
        if (value.Trim().Length == 0)
        {
            return "NIL";
        }
        IReadOnlyList<Anjal.Mime.MailAddress> list = Anjal.Mime.AddressParser.Parse(value);
        if (list.Count == 0)
        {
            return "NIL";
        }
        var sb = new StringBuilder("(");
        foreach (Anjal.Mime.MailAddress a in list)
        {
            string name = a.DisplayName;
            if (name.Any(c => c > 126))
            {
                name = "=?utf-8?b?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "?=";
            }
            sb.Append('(').Append(ImapText.NString(name)).Append(" NIL ").Append(ImapText.Quote(a.LocalPart)).Append(' ').Append(ImapText.Quote(a.Domain)).Append(')');
        }
        return sb.Append(')').ToString();
    }

    private static int Lines(byte[] raw, int start, int end)
    {
        int n = 0;
        for (int i = start; i < end; i++)
        {
            if (raw[i] == 10)
            {
                n++;
            }
        }
        return end > start && raw[end - 1] != 10 ? n + 1 : n;
    }

    private static int FindBodyStart(byte[] raw, int start, int end)
    {
        // A part may start straight away with a blank line (no header fields).
        if (end - start >= 2 && raw[start] == 13 && raw[start + 1] == 10)
        {
            return start + 2;
        }
        if (end - start >= 1 && raw[start] == 10)
        {
            return start + 1;
        }
        for (int i = start; i < end - 1; i++)
        {
            if (raw[i] == 10 && raw[i + 1] == 10)
            {
                return i + 2;
            }
            if (raw[i] == 10 && i + 2 < end && raw[i + 1] == 13 && raw[i + 2] == 10)
            {
                return i + 3;
            }
        }
        return end;
    }

    private static List<(int Start, int End, string Name)> RawFields(byte[] raw, int start, int end)
    {
        var fields = new List<(int, int, string)>();
        int i = start;
        while (i < end)
        {
            int lineEnd = Array.IndexOf(raw, (byte)10, i, end - i);
            lineEnd = lineEnd < 0 ? end : lineEnd + 1;
            if (lineEnd - i <= 2 && (raw[i] == 13 || raw[i] == 10))
            {
                break;
            }
            int fieldStart = i;
            i = lineEnd;
            while (i < end && (raw[i] == (byte)' ' || raw[i] == (byte)'\t'))
            {
                int next = Array.IndexOf(raw, (byte)10, i, end - i);
                i = next < 0 ? end : next + 1;
            }
            int colon = Array.IndexOf(raw, (byte)':', fieldStart, i - fieldStart);
            if (colon > fieldStart)
            {
                fields.Add((fieldStart, i, Encoding.ASCII.GetString(raw, fieldStart, colon - fieldStart).Trim()));
            }
        }
        return fields;
    }

    private static List<(string, string)> ParseHeaders(byte[] raw, int start, int end)
    {
        var list = new List<(string, string)>();
        foreach ((int s, int e, string name) in RawFields(raw, start, end))
        {
            string text = Encoding.UTF8.GetString(raw, s, e - s);
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            string value = text[(colon + 1)..].Replace("\r\n", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
            list.Add((name, value));
        }
        return list;
    }

    private static (string Value, Dictionary<string, string> Parameters) ParseValue(string text)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        int semi = text.IndexOf(';', StringComparison.Ordinal);
        string value = (semi < 0 ? text : text[..semi]).Trim();
        int i = semi < 0 ? text.Length : semi + 1;
        while (i < text.Length)
        {
            int eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }
            string name = text[i..eq].Trim().ToLowerInvariant();
            i = eq + 1;
            string v;
            if (i < text.Length && text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }
                    sb.Append(text[i++]);
                }
                v = sb.ToString();
                int next = text.IndexOf(';', i);
                i = next < 0 ? text.Length : next + 1;
            }
            else
            {
                int next = text.IndexOf(';', i);
                v = (next < 0 ? text[i..] : text[i..next]).Trim();
                i = next < 0 ? text.Length : next + 1;
            }
            if (name.Length > 0 && !p.ContainsKey(name))
            {
                p[name] = v;
            }
        }
        return (value, p);
    }

    private static List<(int Start, int End)> SplitParts(byte[] raw, int start, int end, string boundary)
    {
        byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var marks = new List<(int LineStart, int AfterLine, bool Close)>();
        int i = start;
        while (i < end)
        {
            int lineEnd = Array.IndexOf(raw, (byte)10, i, end - i);
            int next = lineEnd < 0 ? end : lineEnd + 1;
            if (next - i >= delimiter.Length && raw.AsSpan(i, delimiter.Length).SequenceEqual(delimiter))
            {
                bool close = next - i >= delimiter.Length + 2 && raw[i + delimiter.Length] == (byte)'-' && raw[i + delimiter.Length + 1] == (byte)'-';
                marks.Add((i, next, close));
                if (close)
                {
                    break;
                }
            }
            i = next;
        }
        var parts = new List<(int, int)>();
        for (int m = 0; m + 1 < marks.Count || (m < marks.Count && !marks[m].Close && m == marks.Count - 1); m++)
        {
            if (marks[m].Close)
            {
                break;
            }
            int partStart = marks[m].AfterLine;
            int partEnd = m + 1 < marks.Count ? marks[m + 1].LineStart : end;
            // The line break before a delimiter belongs to the delimiter (RFC 2046).
            if (partEnd > partStart && raw[partEnd - 1] == 10)
            {
                partEnd--;
                if (partEnd > partStart && raw[partEnd - 1] == 13)
                {
                    partEnd--;
                }
            }
            parts.Add((partStart, Math.Max(partStart, partEnd)));
        }
        return parts;
    }
}
