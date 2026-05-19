using System.Text;

namespace Anjal.Dkim;

/// <summary>
/// A parsed view of an RFC 5322 message split into headers and body for
/// DKIM processing. Headers preserve their original casing and value
/// exactly as they appear in the message (canonicalization is done by the
/// signer at sign time).
/// </summary>
public sealed class DkimMessage
{
    /// <summary>The header lines in the order they appeared. Each entry is
    /// the name and the raw value (no terminating CRLF; folded continuations
    /// included verbatim).</summary>
    public System.Collections.Generic.IReadOnlyList<RawHeader> Headers { get; }

    /// <summary>The body bytes (everything after the blank line between headers
    /// and body, including the body's terminating CRLF if any).</summary>
    public byte[] Body { get; }

    private DkimMessage(System.Collections.Generic.IReadOnlyList<RawHeader> headers, byte[] body)
    {
        this.Headers = headers;
        this.Body = body;
    }

    /// <summary>
    /// Parse a message into its DKIM view. Accepts either CRLF or bare LF
    /// line terminators on input; the canonicalizer normalizes to CRLF later.
    /// </summary>
    /// <param name="rawBytes">The full RFC 5322 message.</param>
    /// <exception cref="System.FormatException">No header/body separator found.</exception>
    public static DkimMessage Parse(byte[] rawBytes)
    {
        System.ArgumentNullException.ThrowIfNull(rawBytes);

        // Normalize to CRLF for splitting, then find the blank line that
        // separates headers and body. The blank line is CRLF CRLF.
        string text = Encoding.UTF8.GetString(rawBytes);
        // Normalize bare LF to CRLF first.
        text = text.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace("\n", "\r\n", System.StringComparison.Ordinal);

        int splitIdx = text.IndexOf("\r\n\r\n", System.StringComparison.Ordinal);
        if (splitIdx < 0)
        {
            throw new System.FormatException("DKIM message has no header/body separator (CRLF CRLF).");
        }

        string headerText = text.Substring(0, splitIdx);
        string bodyText = text.Substring(splitIdx + 4); // skip CRLF CRLF

        var headers = new System.Collections.Generic.List<RawHeader>();
        // Split header text into logical headers. A logical header is one or
        // more lines; the second and later lines are continuations that begin
        // with SP or HTAB.
        var currentName = new StringBuilder();
        var currentValue = new StringBuilder();
        bool haveName = false;

        foreach (string line in headerText.Split("\r\n"))
        {
            if (line.Length == 0) continue;
            if (line[0] == ' ' || line[0] == '\t')
            {
                // Continuation. RFC requires we keep the CRLF and the WSP for
                // simple canonicalization; we preserve as "\r\n" + line.
                if (!haveName)
                {
                    // Malformed - continuation before any header. Skip.
                    continue;
                }
                currentValue.Append("\r\n");
                currentValue.Append(line);
            }
            else
            {
                // Flush previous.
                if (haveName)
                {
                    headers.Add(new RawHeader(currentName.ToString(), currentValue.ToString()));
                }
                int colon = line.IndexOf(':', System.StringComparison.Ordinal);
                if (colon < 0)
                {
                    // Malformed line; skip.
                    haveName = false;
                    continue;
                }
                currentName.Clear();
                currentName.Append(line, 0, colon);
                currentValue.Clear();
                // Skip the colon. Per RFC 5322, the value may have leading WSP
                // which is part of the field-body; we keep it.
                currentValue.Append(line, colon + 1, line.Length - colon - 1);
                haveName = true;
            }
        }
        if (haveName)
        {
            headers.Add(new RawHeader(currentName.ToString(), currentValue.ToString()));
        }

        return new DkimMessage(headers, Encoding.UTF8.GetBytes(bodyText));
    }

    /// <summary>
    /// Look up the most recently-occurring header with the given name
    /// (case-insensitive). Returns the raw value or null if not present.
    /// </summary>
    /// <param name="name">Header name to find.</param>
    public string? GetHeaderValue(string name)
    {
        System.ArgumentNullException.ThrowIfNull(name);
        for (int i = this.Headers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(this.Headers[i].Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return this.Headers[i].Value;
            }
        }
        return null;
    }
}

/// <summary>One parsed header line: name and raw value (no colon, possibly
/// containing internal CRLF for folded continuations).</summary>
public sealed class RawHeader
{
    /// <summary>The header name as it appeared (original casing).</summary>
    public string Name { get; }

    /// <summary>The header value as it appeared (with leading WSP and any
    /// continuation lines preserved verbatim).</summary>
    public string Value { get; }

    /// <summary>Construct.</summary>
    /// <param name="name">Header name.</param>
    /// <param name="value">Header value.</param>
    public RawHeader(string name, string value)
    {
        this.Name = name ?? throw new System.ArgumentNullException(nameof(name));
        this.Value = value ?? throw new System.ArgumentNullException(nameof(value));
    }
}
