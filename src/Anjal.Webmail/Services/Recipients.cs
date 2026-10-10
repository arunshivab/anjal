using Anjal.Mime;

namespace Anjal.Webmail.Services;

/// <summary>One person a message went to.</summary>
/// <param name="Name">The display name, or the address when there is none.</param>
/// <param name="Address">The address.</param>
/// <param name="Line">"To" or "Cc".</param>
/// <param name="IsYou">True for the person reading.</param>
/// <param name="Inside">True when the address is in the reader's organisation.</param>
public sealed record Recipient(string Name, string Address, string Line, bool IsYou, bool Inside);

/// <summary>
/// The people a message went to, for the envelope (rc.11, item 52): "You" first,
/// then To, then Cc; who is inside the organisation and who is outside; the
/// summary; and the people grouped by organisation for the full list.
/// </summary>
public sealed class Recipients
{
    /// <summary>Up to this many are shown in full; above it, names collapse to chips with "+N more" (item 52).</summary>
    public const int ShownInFull = 5;

    private Recipients(IReadOnlyList<Recipient> people)
    {
        this.People = people;
    }

    /// <summary>Everyone, "You" first, then To, then Cc, each in the order written.</summary>
    public IReadOnlyList<Recipient> People { get; }

    /// <summary>How many people in all.</summary>
    public int Count => this.People.Count;

    /// <summary>How many inside the organisation.</summary>
    public int InsideCount => this.People.Count(p => p.Inside);

    /// <summary>How many outside it.</summary>
    public int OutsideCount => this.People.Count(p => !p.Inside);

    /// <summary>True when the names collapse to chips.</summary>
    public bool Collapsed => this.Count > ShownInFull;

    /// <summary>Everyone grouped by the domain of their address, the reader's organisation first.</summary>
    public IEnumerable<IGrouping<string, Recipient>> ByOrganisation =>
        this.People.GroupBy(p => DomainOf(p.Address)).OrderBy(g => g.All(p => p.Inside) ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal);

    /// <summary>Work out the recipients of a message.</summary>
    /// <param name="to">The To line.</param>
    /// <param name="cc">The Cc line.</param>
    /// <param name="you">The reader's own address.</param>
    /// <param name="ownDomains">The reader's organisation's domains.</param>
    /// <returns>The recipients.</returns>
    public static Recipients Of(string? to, string? cc, string? you, IEnumerable<string> ownDomains)
    {
        ArgumentNullException.ThrowIfNull(ownDomains);
        var domains = new HashSet<string>(ownDomains.Select(d => d.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        string me = (you ?? string.Empty).Trim();
        var people = new List<Recipient>();
        foreach ((string line, string? value) in new[] { ("To", to), ("Cc", cc) })
        {
            foreach (MailAddress a in AddressParser.Parse(EncodedWordDecoder.Decode(value ?? string.Empty)))
            {
                if (people.Any(p => string.Equals(p.Address, a.Address, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                bool isYou = me.Length > 0 && string.Equals(a.Address, me, StringComparison.OrdinalIgnoreCase);
                people.Add(new Recipient(a.DisplayName.Length > 0 ? a.DisplayName : a.Address, a.Address, line, isYou, domains.Contains(DomainOf(a.Address))));
            }
        }
        return new Recipients(people.OrderBy(p => p.IsYou ? 0 : 1).ToList());
    }

    private static string DomainOf(string address)
    {
        int at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address[(at + 1)..].Trim().ToLowerInvariant();
    }
}
