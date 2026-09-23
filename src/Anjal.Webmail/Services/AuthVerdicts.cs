using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// The sender checks this server made when a message arrived (DEF-038).
/// Only an Authentication-Results header bearing this server's own name is
/// trusted: the name is taken from the top Received line - the one this
/// server wrote - and the MTA removes any incoming header claiming that name
/// (RFC 8601 section 5). A sender's own "dmarc=pass" therefore never shows.
/// </summary>
public sealed partial class AuthVerdicts
{
    /// <summary>SPF result, e.g. pass, fail, softfail, none.</summary>
    public string Spf { get; init; } = "none";

    /// <summary>DKIM result; "pass" when any signature passed.</summary>
    public string Dkim { get; init; } = "none";

    /// <summary>DMARC result, e.g. pass, fail, none.</summary>
    public string Dmarc { get; init; } = "none";

    /// <summary>
    /// What a DMARC result means for the reader, in plain words; empty when
    /// there is nothing useful to say.
    /// </summary>
    /// <param name="dmarc">The DMARC result.</param>
    public static string DmarcMeaning(string dmarc) => dmarc switch
    {
        "pass" => "The sender's domain vouches for this message.",
        "fail" => "The sender's domain does not vouch for this message. It may be an impersonation: be careful with links, attachments and requests.",
        "none" => "The sender's domain publishes no policy, so this cannot be checked.",
        _ => string.Empty,
    };

    /// <summary>A class name for styling a result: ok, bad or none.</summary>
    /// <param name="result">A result word.</param>
    public static string Tone(string result) => result switch
    {
        "pass" => "ok",
        "fail" or "softfail" or "permerror" or "temperror" => "bad",
        _ => "none",
    };

    /// <summary>
    /// The trusted verdicts from a message's headers, or null when there are
    /// none: no Received line, or no Authentication-Results bearing the name
    /// of the server that wrote the top Received line.
    /// </summary>
    /// <param name="headers">The message's headers, top first.</param>
    public static AuthVerdicts? FromHeaders(Anjal.Mime.HeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        IReadOnlyList<string> received = headers.GetAll("Received");
        if (received.Count == 0)
        {
            return null;
        }
        Match by = ByHostRegex().Match(received[0]);
        if (!by.Success)
        {
            return null;
        }
        string host = by.Groups[1].Value;
        foreach (string value in headers.GetAll("Authentication-Results"))
        {
            if (!string.Equals(Anjal.Smtp.AuthResultsHeader.AuthServIdOf(value), host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string spf = "none", dkim = "none", dmarc = "none";
            bool seenSpf = false, seenDmarc = false;
            foreach (Match m in ResultRegex().Matches(value))
            {
                string method = m.Groups[1].Value.ToLowerInvariant();
                string result = m.Groups[2].Value.ToLowerInvariant();
                if (method == "spf" && !seenSpf)
                {
                    spf = result;
                    seenSpf = true;
                }
                else if (method == "dmarc" && !seenDmarc)
                {
                    dmarc = result;
                    seenDmarc = true;
                }
                else if (method == "dkim" && (dkim == "none" || result == "pass"))
                {
                    dkim = result;
                }
            }
            return new AuthVerdicts { Spf = spf, Dkim = dkim, Dmarc = dmarc };
        }
        return null;
    }

    [GeneratedRegex(@"\bby\s+([^\s;()]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ByHostRegex();

    [GeneratedRegex(@"\b(spf|dkim|dmarc)\s*=\s*([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultRegex();
}
