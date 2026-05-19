using System.Collections;

namespace Anjal.Mime;

/// <summary>
/// An ordered, case-insensitive collection of MIME header fields. Headers can
/// occur more than once with the same name (e.g. multiple Received headers);
/// this collection preserves both order and duplicates.
/// </summary>
public sealed class HeaderCollection : IEnumerable<MimeHeader>
{
    private readonly List<MimeHeader> headers = new();

    /// <summary>
    /// The number of header fields in the collection.
    /// </summary>
    public int Count => this.headers.Count;

    /// <summary>
    /// Append a header. Existing headers with the same name are not removed -
    /// use <see cref="Set"/> for replace-or-add semantics.
    /// </summary>
    /// <param name="header">The header to append.</param>
    public void Add(MimeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        this.headers.Add(header);
    }

    /// <summary>
    /// Append a header given its name and value. Equivalent to
    /// <c>Add(new MimeHeader(name, value))</c>.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    public void Add(string name, string value) => this.Add(new MimeHeader(name, value));

    /// <summary>
    /// Set a header to a single value. Removes all existing headers with the
    /// same name (case-insensitive) and appends a single new one.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    public void Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        this.headers.RemoveAll(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        this.headers.Add(new MimeHeader(name, value));
    }

    /// <summary>
    /// Remove all headers with the given name (case-insensitive).
    /// </summary>
    /// <param name="name">The header name to remove.</param>
    /// <returns>The number of headers removed.</returns>
    public int Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return this.headers.RemoveAll(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the value of the first header matching the given name
    /// (case-insensitive), or <see langword="null"/> if no such header exists.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>The header value, or <see langword="null"/>.</returns>
    public string? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var h in this.headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Get all values of headers matching the given name (case-insensitive),
    /// in the order they appear.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>All matching header values. Never <see langword="null"/>.</returns>
    public IReadOnlyList<string> GetAll(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var result = new List<string>();
        foreach (var h in this.headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(h.Value);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether the collection contains at least one header with the given
    /// name (case-insensitive).
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns><see langword="true"/> if at least one matching header exists.</returns>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var h in this.headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<MimeHeader> GetEnumerator() => this.headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
