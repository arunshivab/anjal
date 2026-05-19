using System.Text;

namespace Anjal.Mime;

/// <summary>
/// A leaf MIME entity carrying a body of bytes. The bytes stored here are
/// always the decoded form - any Content-Transfer-Encoding has been undone.
/// Builders re-encode them on output according to the entity's encoding header.
/// </summary>
public sealed class MimePart : MimeEntity
{
    /// <summary>
    /// Construct an empty part.
    /// </summary>
    public MimePart()
    {
        this.Body = Array.Empty<byte>();
    }

    /// <summary>
    /// The decoded body bytes. Setter replaces the body entirely.
    /// </summary>
    public byte[] Body { get; set; }

    /// <summary>
    /// Decode the body as text using the charset from the Content-Type
    /// header, falling back to US-ASCII. Useful for text/* parts.
    /// </summary>
    /// <returns>The body as a string.</returns>
    public string GetBodyAsText()
    {
        string? charset = this.ContentType.Charset;
        Encoding encoding;
        if (string.IsNullOrEmpty(charset))
        {
            encoding = Encoding.ASCII;
        }
        else
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }
        return encoding.GetString(this.Body);
    }

    /// <summary>
    /// Set the body from a string using the given encoding.
    /// </summary>
    /// <param name="text">The text content.</param>
    /// <param name="encoding">The encoding to use. Defaults to UTF-8.</param>
    public void SetBodyAsText(string text, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        encoding ??= Encoding.UTF8;
        this.Body = encoding.GetBytes(text);
    }
}
