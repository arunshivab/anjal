using System.Text;

namespace Anjal.Mime;

/// <summary>
/// A parsed Content-Type header per RFC 2045 section 5. Identifies the media
/// type of an entity body and any associated parameters such as charset or boundary.
/// </summary>
public sealed class ContentType
{
    /// <summary>
    /// Construct a ContentType from its components.
    /// </summary>
    /// <param name="mediaType">The top-level type (e.g. "text").</param>
    /// <param name="subType">The subtype (e.g. "plain").</param>
    /// <param name="parameters">Optional parameters such as charset or boundary.</param>
    public ContentType(string mediaType, string subType, IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(subType);
        this.MediaType = mediaType;
        this.SubType = subType;

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                dict[kv.Key] = kv.Value;
            }
        }
        this.Parameters = dict;
    }

    /// <summary>The top-level media type (e.g. "text", "multipart", "application").</summary>
    public string MediaType { get; }

    /// <summary>The subtype (e.g. "plain", "html", "mixed").</summary>
    public string SubType { get; }

    /// <summary>
    /// Parameters from the header, e.g. <c>charset</c> for text bodies or
    /// <c>boundary</c> for multipart bodies. Keys are compared case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>
    /// The "type/subtype" string with no parameters, lowercase per RFC 2045
    /// section 5.1 ("type and subtype values are not case sensitive").
    /// </summary>
    public string MimeType => $"{this.MediaType.ToLowerInvariant()}/{this.SubType.ToLowerInvariant()}";

    /// <summary>The charset parameter if present, otherwise <see langword="null"/>.</summary>
    public string? Charset =>
        this.Parameters.TryGetValue("charset", out var v) ? v : null;

    /// <summary>The boundary parameter (for multipart entities) if present.</summary>
    public string? Boundary =>
        this.Parameters.TryGetValue("boundary", out var v) ? v : null;

    /// <summary>Whether the media type is "multipart".</summary>
    public bool IsMultipart =>
        string.Equals(this.MediaType, "multipart", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the media type is "text".</summary>
    public bool IsText =>
        string.Equals(this.MediaType, "text", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse a Content-Type header value (the part after the colon).
    /// Returns the default <c>text/plain; charset=us-ascii</c> per RFC 2045
    /// section 5.2 if the input is null, empty, or unparseable.
    /// </summary>
    /// <param name="headerValue">The unfolded header value.</param>
    /// <returns>The parsed Content-Type.</returns>
    public static ContentType Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return Default();
        }

        string typeSegment;
        string paramSegment;
        int semi = FindUnquotedSemicolon(headerValue);
        if (semi < 0)
        {
            typeSegment = headerValue;
            paramSegment = string.Empty;
        }
        else
        {
            typeSegment = headerValue.Substring(0, semi);
            paramSegment = headerValue.Substring(semi + 1);
        }

        string trimmedType = typeSegment.Trim();
        int slash = trimmedType.IndexOf('/');
        if (slash <= 0 || slash == trimmedType.Length - 1)
        {
            return Default();
        }

        string media = trimmedType.Substring(0, slash).Trim();
        string sub = trimmedType.Substring(slash + 1).Trim();

        Dictionary<string, string> parameters = ParseParameters(paramSegment);
        return new ContentType(media, sub, parameters);
    }

    /// <summary>
    /// The default Content-Type per RFC 2045 section 5.2: <c>text/plain; charset=us-ascii</c>.
    /// </summary>
    public static ContentType Default() => new(
        "text",
        "plain",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["charset"] = "us-ascii" });

    /// <summary>
    /// Serialise this Content-Type to a header value (the part after "Content-Type: ").
    /// </summary>
    /// <returns>The header value string.</returns>
    public string ToHeaderValue()
    {
        var sb = new StringBuilder(this.MimeType);
        foreach (var kv in this.Parameters)
        {
            sb.Append("; ");
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(NeedsQuoting(kv.Value) ? QuoteString(kv.Value) : kv.Value);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseParameters(string segment)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(segment))
        {
            return dict;
        }

        int i = 0;
        while (i < segment.Length)
        {
            while (i < segment.Length && (segment[i] == ' ' || segment[i] == '\t'))
            {
                i++;
            }
            if (i >= segment.Length)
            {
                break;
            }

            int equals = segment.IndexOf('=', i);
            if (equals < 0)
            {
                break;
            }

            string key = segment.Substring(i, equals - i).Trim();
            int valueStart = equals + 1;
            while (valueStart < segment.Length && (segment[valueStart] == ' ' || segment[valueStart] == '\t'))
            {
                valueStart++;
            }

            string value;
            int valueEnd;
            if (valueStart < segment.Length && segment[valueStart] == '"')
            {
                var sb = new StringBuilder();
                int j = valueStart + 1;
                while (j < segment.Length && segment[j] != '"')
                {
                    if (segment[j] == '\\' && j + 1 < segment.Length)
                    {
                        sb.Append(segment[j + 1]);
                        j += 2;
                    }
                    else
                    {
                        sb.Append(segment[j]);
                        j++;
                    }
                }
                value = sb.ToString();
                valueEnd = j < segment.Length ? j + 1 : segment.Length;
            }
            else
            {
                int semi = FindUnquotedSemicolon(segment, valueStart);
                valueEnd = semi < 0 ? segment.Length : semi;
                value = segment.Substring(valueStart, valueEnd - valueStart).Trim();
            }

            if (!string.IsNullOrEmpty(key))
            {
                dict[key] = value;
            }

            int next = FindUnquotedSemicolon(segment, valueEnd);
            i = next < 0 ? segment.Length : next + 1;
        }
        return dict;
    }

    private static int FindUnquotedSemicolon(string s, int start = 0)
    {
        bool inQuote = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == '\\' && inQuote && i + 1 < s.Length)
            {
                i++;
            }
            else if (c == ';' && !inQuote)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool NeedsQuoting(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }
        foreach (char c in value)
        {
            if (c is ' ' or '\t' or '"' or '\\' or '(' or ')' or '<' or '>' or '@' or ',' or ';' or ':' or '/' or '[' or ']' or '?' or '=')
            {
                return true;
            }
            if (c < 33 || c > 126)
            {
                return true;
            }
        }
        return false;
    }

    private static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            if (c is '\\' or '"')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => this.ToHeaderValue();
}
