
namespace Anjal.Mime;

/// <summary>
/// A multipart MIME entity (multipart/mixed, multipart/alternative,
/// multipart/related etc.) that contains an ordered list of child entities.
/// Children may themselves be multiparts, allowing nested structure.
/// </summary>
public sealed class MimeMultipart : MimeEntity
{
    /// <summary>
    /// Construct an empty multipart.
    /// </summary>
    public MimeMultipart()
    {
        this.Parts = new List<MimeEntity>();
    }

    /// <summary>
    /// The child entities in declaration order.
    /// </summary>
    public IList<MimeEntity> Parts { get; }

    /// <summary>
    /// The text that appears before the first boundary marker. Per RFC 2046
    /// section 5.1.1, recipients should ignore this; we preserve it for
    /// fidelity but it has no semantic meaning.
    /// </summary>
    public string Preamble { get; set; } = string.Empty;

    /// <summary>
    /// The text that appears after the closing boundary marker. Same
    /// semantics as <see cref="Preamble"/>.
    /// </summary>
    public string Epilogue { get; set; } = string.Empty;
}
