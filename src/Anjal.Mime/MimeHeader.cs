
namespace Anjal.Mime;

/// <summary>
/// A single header field of a MIME entity. Header field names are case-insensitive
/// per RFC 5322 section 1.2.2; the original casing is preserved in <see cref="Name"/>
/// but equality comparisons use case-insensitive matching.
/// </summary>
public sealed class MimeHeader : IEquatable<MimeHeader>
{
    /// <summary>
    /// Construct a header from its name and value. The value must already be
    /// unfolded - parsers are responsible for joining continuation lines before
    /// constructing this object.
    /// </summary>
    /// <param name="name">The field name as it appeared on the wire.</param>
    /// <param name="value">The unfolded field value.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="name"/> or
    /// <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">If the name is empty or contains
    /// CR, LF, NUL, a colon or whitespace, or the value contains CR, LF or
    /// NUL.</exception>
    /// <remarks>
    /// The check lives here, at the type boundary, so every producer of a
    /// header is covered at once - compose fields, draft fields, reply
    /// linkage, the API. A value carrying CR or LF would otherwise end the
    /// header early and start a new one on the wire: a second From or
    /// Reply-To inserted by the sender, then DKIM-signed by Anjal as though
    /// Anjal had written it. The parser folds and trims before constructing,
    /// so well-formed and ordinarily malformed incoming mail is unaffected.
    /// </remarks>
    public MimeHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValidName(name))
        {
            throw new ArgumentException("A header name must be a non-empty token without CR, LF, NUL, ':' or whitespace.", nameof(name));
        }
        if (!IsValidValue(value))
        {
            throw new ArgumentException("A header value must not contain CR, LF or NUL.", nameof(value));
        }
        this.Name = name;
        this.Value = value;
    }

    /// <summary>Whether a string is acceptable as a header field name (RFC 5322 section 3.6.8).</summary>
    /// <param name="name">Candidate name.</param>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return false;
        }
        foreach (char c in name)
        {
            // Printable US-ASCII except the colon; no spaces or controls.
            if (c <= ' ' || c > '~' || c == ':')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a string is acceptable as an unfolded header value.</summary>
    /// <param name="value">Candidate value.</param>
    public static bool IsValidValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.IndexOfAny(Forbidden) < 0;
    }

    /// <summary>
    /// A value with every CR, LF and NUL replaced by a space: for input that
    /// comes from the wire and should be kept rather than refused.
    /// </summary>
    /// <param name="value">The value.</param>
    public static string Neutralise(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(Forbidden) < 0)
        {
            return value;
        }
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\r' || chars[i] == '\n' || chars[i] == '\0')
            {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }

    private static readonly char[] Forbidden = { '\r', '\n', '\0' };

    /// <summary>
    /// The field name in the casing it was created with. Compare using
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The field value with continuation folding removed. May be empty.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc/>
    public bool Equals(MimeHeader? other)
    {
        if (other is null)
        {
            return false;
        }
        return string.Equals(this.Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(this.Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => this.Equals(obj as MimeHeader);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(this.Name),
        this.Value);

    /// <inheritdoc/>
    public override string ToString() => $"{this.Name}: {this.Value}";
}
