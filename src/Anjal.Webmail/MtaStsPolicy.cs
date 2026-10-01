namespace Anjal.Webmail;

/// <summary>
/// The MTA-STS policy (RFC 8461) the webmail serves at
/// <c>https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt</c> for each hosted
/// domain (v1.0.0-rc.10, decision D-63). It is built from settings, never
/// hand-written: the mx lines come from the server's own name.
/// </summary>
public sealed class MtaStsPolicy
{
    /// <summary>The path every sender fetches.</summary>
    public const string PolicyPath = "/.well-known/mta-sts.txt";

    /// <summary>The longest max_age RFC 8461 allows (about a year), in seconds.</summary>
    public const int MaximumMaxAge = 31557600;

    /// <summary>The policy mode: <c>testing</c>, <c>enforce</c> or <c>none</c>.</summary>
    public string Mode { get; init; } = "testing";

    /// <summary>The domains whose policy host (mta-sts.&lt;domain&gt;) is answered.</summary>
    public System.Collections.Generic.IReadOnlyList<string> Domains { get; init; } = System.Array.Empty<string>();

    /// <summary>The MX host names the policy allows.</summary>
    public System.Collections.Generic.IReadOnlyList<string> Mx { get; init; } = System.Array.Empty<string>();

    /// <summary>How long senders may cache the policy, in seconds.</summary>
    public int MaxAgeSeconds { get; init; } = 86400;

    /// <summary>
    /// Read the policy from the environment: <c>ANJAL_MTA_STS_MODE</c> (off,
    /// testing, enforce, none; off or unset serves nothing),
    /// <c>ANJAL_MTA_STS_DOMAINS</c> (comma-separated; default the server name's
    /// parent domain), <c>ANJAL_MTA_STS_MX</c> (comma-separated; default the
    /// server name) and <c>ANJAL_MTA_STS_MAX_AGE</c> (seconds; default one day
    /// in testing, one week in enforce).
    /// </summary>
    /// <param name="hostName">The server's own name, for example mail.anjal.co.in.</param>
    /// <param name="note">What was decided, for the start-up log.</param>
    /// <returns>The policy, or null when MTA-STS is off.</returns>
    public static MtaStsPolicy? FromEnvironment(string hostName, out string note)
    {
        System.ArgumentNullException.ThrowIfNull(hostName);
        string mode = (System.Environment.GetEnvironmentVariable("ANJAL_MTA_STS_MODE") ?? string.Empty).Trim().ToLowerInvariant();
        if (mode.Length == 0 || mode == "off")
        {
            note = "MTA-STS: off (ANJAL_MTA_STS_MODE not set to testing, enforce or none).";
            return null;
        }
        if (mode != "testing" && mode != "enforce" && mode != "none")
        {
            note = $"MTA-STS: off - ANJAL_MTA_STS_MODE \"{mode}\" is not testing, enforce, none or off.";
            return null;
        }
        string[] domains = Split(System.Environment.GetEnvironmentVariable("ANJAL_MTA_STS_DOMAINS"));
        if (domains.Length == 0)
        {
            int dot = hostName.IndexOf('.', System.StringComparison.Ordinal);
            domains = new[] { dot > 0 ? hostName.Substring(dot + 1) : hostName };
        }
        string[] mx = Split(System.Environment.GetEnvironmentVariable("ANJAL_MTA_STS_MX"));
        if (mx.Length == 0)
        {
            mx = new[] { hostName };
        }
        int maxAge = mode == "enforce" ? 604800 : 86400;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("ANJAL_MTA_STS_MAX_AGE"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int configured))
        {
            maxAge = System.Math.Clamp(configured, 60, MaximumMaxAge);
        }
        var policy = new MtaStsPolicy { Mode = mode, Domains = domains, Mx = mx, MaxAgeSeconds = maxAge };
        note = $"MTA-STS: {mode} (mx {string.Join(", ", mx)}, max_age {maxAge}) at " + string.Join(", ", System.Linq.Enumerable.Select(domains, d => "https://mta-sts." + d + PolicyPath)) + ".";
        return policy;
    }

    /// <summary>Whether a request for this host is a policy host this server answers.</summary>
    /// <param name="host">The request's host name, without a port.</param>
    /// <returns>True for mta-sts.&lt;domain&gt; of a configured domain.</returns>
    public bool Serves(string host)
    {
        System.ArgumentNullException.ThrowIfNull(host);
        foreach (string domain in this.Domains)
        {
            if (string.Equals(host, "mta-sts." + domain, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The policy text, with the CRLF line ends RFC 8461 specifies.</summary>
    /// <returns>The policy file's content.</returns>
    public string Text()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("version: STSv1\r\n");
        sb.Append("mode: ").Append(this.Mode).Append("\r\n");
        foreach (string mx in this.Mx)
        {
            sb.Append("mx: ").Append(mx).Append("\r\n");
        }
        sb.Append("max_age: ").Append(this.MaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
}
