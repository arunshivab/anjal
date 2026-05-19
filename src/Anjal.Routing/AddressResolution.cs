namespace Anjal.Routing;

/// <summary>
/// Result of splitting an SMTP recipient address into its routing components.
/// </summary>
public sealed class AddressResolution
{
    /// <summary>The recipient as received over SMTP, in lowercase.</summary>
    public string Recipient { get; init; } = string.Empty;

    /// <summary>The local-part with any sub-address tag removed.</summary>
    public string LocalPart { get; init; } = string.Empty;

    /// <summary>The "+tag" portion if present, otherwise empty.</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>The domain portion of the address.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// Parse a recipient address into local-part / tag / domain. Sub-addressing
    /// uses "+" as the separator per RFC 5233 - the portion between the local-part
    /// and "+" is the routable key; the portion between "+" and "@" is the tag.
    /// </summary>
    /// <param name="recipient">An address such as <c>reports+X7Y9@host</c>.</param>
    /// <returns>The parsed address, or <see langword="null"/> if the input is
    /// not a valid local-part@domain pair.</returns>
    public static AddressResolution? Parse(string recipient)
    {
        System.ArgumentNullException.ThrowIfNull(recipient);

        int at = recipient.LastIndexOf('@');
        if (at <= 0 || at == recipient.Length - 1)
        {
            return null;
        }

        string localFull = recipient.Substring(0, at);
        string domain = recipient.Substring(at + 1);

        string localPart;
        string tag;
        int plus = localFull.IndexOf('+', System.StringComparison.Ordinal);
        if (plus < 0)
        {
            localPart = localFull;
            tag = string.Empty;
        }
        else
        {
            localPart = localFull.Substring(0, plus);
            tag = localFull.Substring(plus + 1);
        }

        if (localPart.Length == 0)
        {
            return null;
        }

        return new AddressResolution
        {
            Recipient = recipient.ToLowerInvariant(),
            LocalPart = localPart.ToLowerInvariant(),
            Tag = tag.ToLowerInvariant(),
            Domain = domain.ToLowerInvariant(),
        };
    }
}
