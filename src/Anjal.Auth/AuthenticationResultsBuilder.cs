using System.Text;

namespace Anjal.Auth;

/// <summary>
/// Formats an RFC 8601 <c>Authentication-Results</c> header value from
/// SPF, DKIM, and DMARC verdicts. The format Anjal produces is
/// interoperable with what Postfix, OpenDKIM, OpenDMARC, and mail
/// processors generally expect.
/// </summary>
public static class AuthenticationResultsBuilder
{
    /// <summary>
    /// Build the header VALUE (no field name prefix, no terminating CRLF).
    /// The caller prepends <c>Authentication-Results: </c> and the CRLF.
    /// </summary>
    /// <param name="servingHost">The <c>authserv-id</c> (this server's hostname).</param>
    /// <param name="spf">SPF detail.</param>
    /// <param name="dkim">DKIM detail.</param>
    /// <param name="dmarc">DMARC detail.</param>
    public static string Build(string servingHost, SpfDetail spf, DkimDetail dkim, DmarcDetail dmarc)
    {
        System.ArgumentNullException.ThrowIfNull(servingHost);
        System.ArgumentNullException.ThrowIfNull(spf);
        System.ArgumentNullException.ThrowIfNull(dkim);
        System.ArgumentNullException.ThrowIfNull(dmarc);

        var sb = new StringBuilder();
        sb.Append(servingHost);

        // spf=pass smtp.mailfrom=example.com
        sb.Append(";\r\n    spf=").Append(SpfToken(spf.Result));
        if (spf.Result == SpfResult.Pass || spf.Result == SpfResult.Fail || spf.Result == SpfResult.SoftFail)
        {
            sb.Append(" (").Append(spf.Explanation).Append(')');
        }
        if (!string.IsNullOrEmpty(spf.Domain))
        {
            sb.Append(" smtp.mailfrom=").Append(spf.Domain);
        }

        // dkim=pass header.d=example.com header.s=default
        sb.Append(";\r\n    dkim=").Append(DkimToken(dkim.Result));
        if (dkim.Result is DkimResult.Pass or DkimResult.Fail or DkimResult.PermError)
        {
            sb.Append(" (").Append(dkim.Explanation).Append(')');
        }
        if (!string.IsNullOrEmpty(dkim.Domain))
        {
            sb.Append(" header.d=").Append(dkim.Domain);
        }
        if (!string.IsNullOrEmpty(dkim.Selector))
        {
            sb.Append(" header.s=").Append(dkim.Selector);
        }

        // dmarc=pass header.from=example.com
        sb.Append(";\r\n    dmarc=").Append(DmarcToken(dmarc.Result));
        if (dmarc.Result is DmarcResult.Pass or DmarcResult.Fail)
        {
            sb.Append(" (p=").Append(PolicyToken(dmarc.Policy)).Append(')');
        }
        if (!string.IsNullOrEmpty(dmarc.FromDomain))
        {
            sb.Append(" header.from=").Append(dmarc.FromDomain);
        }

        return sb.ToString();
    }

    private static string SpfToken(SpfResult r) => r switch
    {
        SpfResult.Pass => "pass",
        SpfResult.Fail => "fail",
        SpfResult.SoftFail => "softfail",
        SpfResult.Neutral => "neutral",
        SpfResult.None => "none",
        SpfResult.PermError => "permerror",
        SpfResult.TempError => "temperror",
        _ => "none",
    };

    private static string DkimToken(DkimResult r) => r switch
    {
        DkimResult.Pass => "pass",
        DkimResult.Fail => "fail",
        DkimResult.PermError => "permerror",
        DkimResult.TempError => "temperror",
        DkimResult.None => "none",
        _ => "none",
    };

    private static string DmarcToken(DmarcResult r) => r switch
    {
        DmarcResult.Pass => "pass",
        DmarcResult.Fail => "fail",
        DmarcResult.PermError => "permerror",
        DmarcResult.TempError => "temperror",
        DmarcResult.None => "none",
        _ => "none",
    };

    private static string PolicyToken(DmarcPolicy p) => p switch
    {
        DmarcPolicy.None => "none",
        DmarcPolicy.Quarantine => "quarantine",
        DmarcPolicy.Reject => "reject",
        _ => "none",
    };
}
