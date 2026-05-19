
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
    public MimeHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        this.Name = name;
        this.Value = value;
    }

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
