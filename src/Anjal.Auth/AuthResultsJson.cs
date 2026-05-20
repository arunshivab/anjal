using System.Text;

namespace Anjal.Auth;

/// <summary>
/// Serializes <see cref="AuthenticationResults"/> to a JSON object fragment
/// (no surrounding braces) suitable for embedding into a larger JSON object
/// like the webhook payload. Hand-written to avoid taking a dependency on
/// <c>System.Text.Json</c> from <c>Anjal.Routing</c>.
/// </summary>
public static class AuthResultsJson
{
    /// <summary>
    /// Serialize to a JSON object fragment.
    /// </summary>
    /// <param name="results">The authentication results to serialize.</param>
    /// <returns>A JSON object starting with <c>{</c> and ending with <c>}</c>.</returns>
    public static string Serialize(AuthenticationResults results)
    {
        System.ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder(512);
        sb.Append('{');

        // spf block
        sb.Append("\"spf\":{");
        AppendField(sb, "result", SpfResultName(results.Spf.Result), first: true);
        AppendField(sb, "domain", results.Spf.Domain, first: false);
        AppendField(sb, "peerAddress", results.Spf.PeerAddress, first: false);
        AppendField(sb, "matchedMechanism", results.Spf.MatchedMechanism, first: false);
        AppendNumberField(sb, "lookupCount", results.Spf.LookupCount, first: false);
        AppendField(sb, "explanation", results.Spf.Explanation, first: false);
        sb.Append('}');

        // dkim block
        sb.Append(",\"dkim\":{");
        AppendField(sb, "result", DkimResultName(results.Dkim.Result), first: true);
        AppendField(sb, "domain", results.Dkim.Domain, first: false);
        AppendField(sb, "selector", results.Dkim.Selector, first: false);
        AppendField(sb, "algorithm", results.Dkim.Algorithm, first: false);
        AppendField(sb, "explanation", results.Dkim.Explanation, first: false);
        sb.Append('}');

        // dmarc block
        sb.Append(",\"dmarc\":{");
        AppendField(sb, "result", DmarcResultName(results.Dmarc.Result), first: true);
        AppendField(sb, "fromDomain", results.Dmarc.FromDomain, first: false);
        AppendField(sb, "policy", PolicyName(results.Dmarc.Policy), first: false);
        AppendField(sb, "spfAlignment", AlignmentName(results.Dmarc.SpfAlignment), first: false);
        AppendField(sb, "dkimAlignment", AlignmentName(results.Dmarc.DkimAlignment), first: false);
        AppendBoolField(sb, "spfAligned", results.Dmarc.SpfAligned, first: false);
        AppendBoolField(sb, "dkimAligned", results.Dmarc.DkimAligned, first: false);
        AppendField(sb, "alignedDomain", results.Dmarc.AlignedDomain, first: false);
        AppendField(sb, "explanation", results.Dmarc.Explanation, first: false);
        sb.Append('}');

        // top-level header value (the formatted Authentication-Results header line)
        sb.Append(",\"headerValue\":\"");
        AppendJsonEscaped(sb, results.HeaderValue);
        sb.Append('"');

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string key, string value, bool first)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(key).Append("\":\"");
        AppendJsonEscaped(sb, value ?? string.Empty);
        sb.Append('"');
    }

    private static void AppendNumberField(StringBuilder sb, string key, int value, bool first)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(key).Append("\":")
          .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendBoolField(StringBuilder sb, string key, bool value, bool first)
    {
        if (!first) sb.Append(',');
        sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
    }

    private static void AppendJsonEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
    }

    private static string SpfResultName(SpfResult r) => r switch
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

    private static string DkimResultName(DkimResult r) => r switch
    {
        DkimResult.Pass => "pass",
        DkimResult.Fail => "fail",
        DkimResult.PermError => "permerror",
        DkimResult.TempError => "temperror",
        DkimResult.None => "none",
        _ => "none",
    };

    private static string DmarcResultName(DmarcResult r) => r switch
    {
        DmarcResult.Pass => "pass",
        DmarcResult.Fail => "fail",
        DmarcResult.PermError => "permerror",
        DmarcResult.TempError => "temperror",
        DmarcResult.None => "none",
        _ => "none",
    };

    private static string PolicyName(DmarcPolicy p) => p switch
    {
        DmarcPolicy.None => "none",
        DmarcPolicy.Quarantine => "quarantine",
        DmarcPolicy.Reject => "reject",
        _ => "none",
    };

    private static string AlignmentName(AlignmentMode a) => a switch
    {
        AlignmentMode.Strict => "strict",
        AlignmentMode.Relaxed => "relaxed",
        _ => "relaxed",
    };
}
