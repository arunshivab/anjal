
namespace Anjal.Mime;

/// <summary>
/// Abstract base for any MIME entity - a part with headers and a body of
/// some sort. Concrete subclasses are <see cref="MimePart"/> (leaf body) and
/// <see cref="MimeMultipart"/> (container of nested entities).
/// </summary>
public abstract class MimeEntity
{
    /// <summary>
    /// Construct an entity with an empty header collection.
    /// </summary>
    protected MimeEntity()
    {
        this.Headers = new HeaderCollection();
    }

    /// <summary>
    /// The header fields of this entity, in order.
    /// </summary>
    public HeaderCollection Headers { get; }

    /// <summary>
    /// The parsed Content-Type for this entity. If the header is absent or
    /// unparseable, returns <see cref="ContentType.Default"/>.
    /// </summary>
    public ContentType ContentType => ContentType.Parse(this.Headers.Get("Content-Type"));

    /// <summary>
    /// The parsed Content-Transfer-Encoding for this entity. Defaults to
    /// <see cref="ContentTransferEncoding.SevenBit"/> if absent.
    /// </summary>
    public ContentTransferEncoding ContentTransferEncoding =>
        ContentTransferEncodingExtensions.Parse(this.Headers.Get("Content-Transfer-Encoding"));
}
