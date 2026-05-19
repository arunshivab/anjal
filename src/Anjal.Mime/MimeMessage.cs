
namespace Anjal.Mime;

/// <summary>
/// A top-level RFC 5322 message. Combines a single root MIME entity with
/// convenience accessors for the well-known headers (From, To, Subject etc.)
/// </summary>
public sealed class MimeMessage
{
    /// <summary>
    /// Construct an empty message with a default <see cref="MimePart"/> as
    /// its body.
    /// </summary>
    public MimeMessage()
    {
        this.Body = new MimePart();
    }

    /// <summary>
    /// Construct a message with a specific root entity.
    /// </summary>
    /// <param name="body">The root MIME entity.</param>
    public MimeMessage(MimeEntity body)
    {
        ArgumentNullException.ThrowIfNull(body);
        this.Body = body;
    }

    /// <summary>
    /// The root MIME entity. May be a <see cref="MimePart"/> for simple
    /// single-body messages or a <see cref="MimeMultipart"/> for messages
    /// with attachments or alternative renderings.
    /// </summary>
    public MimeEntity Body { get; set; }

    /// <summary>
    /// The headers of the root entity. Convenience shortcut for
    /// <c>Body.Headers</c>.
    /// </summary>
    public HeaderCollection Headers => this.Body.Headers;

    /// <summary>
    /// The decoded Subject header, or empty string if absent.
    /// </summary>
    public string Subject
    {
        get
        {
            string? raw = this.Headers.Get("Subject");
            return raw is null ? string.Empty : EncodedWordDecoder.Decode(raw);
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            this.Headers.Set("Subject", value);
        }
    }

    /// <summary>
    /// The parsed addresses from the From header. Empty list if absent.
    /// </summary>
    public IReadOnlyList<MailAddress> From => AddressParser.Parse(this.Headers.Get("From"));

    /// <summary>
    /// The parsed addresses from the To header. Empty list if absent.
    /// </summary>
    public IReadOnlyList<MailAddress> To => AddressParser.Parse(this.Headers.Get("To"));

    /// <summary>
    /// The parsed addresses from the Cc header. Empty list if absent.
    /// </summary>
    public IReadOnlyList<MailAddress> Cc => AddressParser.Parse(this.Headers.Get("Cc"));

    /// <summary>
    /// The Date header value as a string. Returns empty string if absent.
    /// Date parsing into <see cref="DateTimeOffset"/> is left to a future
    /// extension to keep this type focused.
    /// </summary>
    public string Date
    {
        get => this.Headers.Get("Date") ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            this.Headers.Set("Date", value);
        }
    }

    /// <summary>
    /// The Message-ID header value with angle brackets stripped, or empty.
    /// </summary>
    public string MessageId
    {
        get
        {
            string? raw = this.Headers.Get("Message-ID");
            if (string.IsNullOrEmpty(raw))
            {
                return string.Empty;
            }
            string trimmed = raw.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[trimmed.Length - 1] == '>')
            {
                return trimmed.Substring(1, trimmed.Length - 2);
            }
            return trimmed;
        }
    }
}
