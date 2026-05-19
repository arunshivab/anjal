
namespace Anjal.Mime;

/// <summary>
/// A single mail address: a local-part + "@" + domain, optionally accompanied
/// by a human-readable display name. See RFC 5322 section 3.4.
/// </summary>
public sealed class MailAddress : IEquatable<MailAddress>
{
    /// <summary>
    /// Construct a mail address. The address must contain exactly one "@".
    /// </summary>
    /// <param name="address">The addr-spec (e.g. "alice@example.com").</param>
    /// <param name="displayName">The optional human-readable name. May be
    /// <see langword="null"/> or empty.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="address"/> does
    /// not contain a single "@" or has empty local-part or domain.</exception>
    public MailAddress(string address, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        int at = address.LastIndexOf('@');
        if (at < 1 || at == address.Length - 1)
        {
            throw new ArgumentException(
                $"Address '{address}' is not a valid addr-spec; expected local-part@domain.",
                nameof(address));
        }

        this.LocalPart = address.Substring(0, at);
        this.Domain = address.Substring(at + 1);
        this.DisplayName = displayName ?? string.Empty;
    }

    /// <summary>
    /// Construct a mail address from explicit local-part and domain parts.
    /// </summary>
    /// <param name="localPart">The text before the "@".</param>
    /// <param name="domain">The text after the "@".</param>
    /// <param name="displayName">Optional display name.</param>
    public static MailAddress FromParts(string localPart, string domain, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(localPart);
        ArgumentNullException.ThrowIfNull(domain);
        return new MailAddress($"{localPart}@{domain}", displayName);
    }

    /// <summary>The local-part of the address (left of the "@").</summary>
    public string LocalPart { get; }

    /// <summary>The domain part of the address (right of the "@").</summary>
    public string Domain { get; }

    /// <summary>
    /// Optional human-readable display name. Empty string if not provided.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// The bare "local-part@domain" form, with no display name and no angle brackets.
    /// </summary>
    public string Address => $"{this.LocalPart}@{this.Domain}";

    /// <inheritdoc/>
    public bool Equals(MailAddress? other)
    {
        if (other is null)
        {
            return false;
        }
        return string.Equals(this.LocalPart, other.LocalPart, StringComparison.Ordinal)
            && string.Equals(this.Domain, other.Domain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(this.DisplayName, other.DisplayName, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => this.Equals(obj as MailAddress);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        this.LocalPart,
        StringComparer.OrdinalIgnoreCase.GetHashCode(this.Domain),
        this.DisplayName);

    /// <inheritdoc/>
    public override string ToString() => string.IsNullOrEmpty(this.DisplayName)
        ? this.Address
        : $"{this.DisplayName} <{this.Address}>";
}
