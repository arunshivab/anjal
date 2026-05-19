namespace Anjal.Dns;

/// <summary>
/// A single MX record returned by a DNS resolver. RFC 1035 section 3.3.9.
/// Lower priority is more preferred; senders try priority 0 first.
/// </summary>
public sealed class MxRecord
{
    /// <summary>Preference value (lower is more preferred).</summary>
    public int Priority { get; init; }

    /// <summary>Hostname of the mail exchanger. Always lowercase.</summary>
    public string Exchange { get; init; } = string.Empty;

    /// <inheritdoc/>
    public override string ToString() => $"{this.Priority} {this.Exchange}";
}
