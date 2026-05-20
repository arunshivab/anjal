namespace Anjal.Auth;

/// <summary>
/// Evaluates DMARC per RFC 7489. Fetches the <c>_dmarc.&lt;domain&gt;</c>
/// TXT record, parses the policy, and checks alignment between the From
/// header's domain and the SPF/DKIM identifiers.
/// </summary>
public sealed class DmarcEvaluator
{
    private readonly Anjal.Dns.DnsResolver dns;

    /// <summary>Construct.</summary>
    /// <param name="dns">DNS resolver for the <c>_dmarc</c> TXT lookup.</param>
    public DmarcEvaluator(Anjal.Dns.DnsResolver dns)
    {
        System.ArgumentNullException.ThrowIfNull(dns);
        this.dns = dns;
    }

    /// <summary>
    /// Evaluate DMARC for a message. Pass the From header's domain, the
    /// MAIL FROM domain that SPF was checked against, and the SPF + DKIM
    /// verdicts so we can apply the alignment + policy rules.
    /// </summary>
    /// <param name="fromDomain">Domain from the message's <c>From:</c> header.</param>
    /// <param name="mailFromDomain">SPF's RFC5321.MailFrom domain.</param>
    /// <param name="spf">SPF detail (used for the aligned-pass check).</param>
    /// <param name="dkim">DKIM detail (used for the aligned-pass check).</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<DmarcDetail> EvaluateAsync(
        string fromDomain,
        string mailFromDomain,
        SpfDetail spf,
        DkimDetail dkim,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(fromDomain);
        System.ArgumentNullException.ThrowIfNull(mailFromDomain);
        System.ArgumentNullException.ThrowIfNull(spf);
        System.ArgumentNullException.ThrowIfNull(dkim);

        // Look up _dmarc.<fromDomain> first, then organizational domain if not found.
        DmarcRecord? record;
        try
        {
            record = await this.LookupDmarcAsync(fromDomain, ct).ConfigureAwait(false);
        }
        catch (Anjal.Dns.DnsException ex)
        {
            return new DmarcDetail
            {
                Result = DmarcResult.TempError,
                FromDomain = fromDomain,
                Explanation = $"DMARC DNS lookup failed: {ex.Message}",
            };
        }
        catch (DmarcParseError ex)
        {
            return new DmarcDetail
            {
                Result = DmarcResult.PermError,
                FromDomain = fromDomain,
                Explanation = $"DMARC record malformed: {ex.Message}",
            };
        }
        if (record is null)
        {
            return new DmarcDetail
            {
                Result = DmarcResult.None,
                FromDomain = fromDomain,
                Explanation = $"No DMARC record for {fromDomain}.",
            };
        }

        // Check alignment.
        bool spfAligned = spf.Result == SpfResult.Pass &&
            IsAligned(mailFromDomain, fromDomain, record.SpfAlignment);
        bool dkimAligned = dkim.Result == DkimResult.Pass &&
            IsAligned(dkim.Domain, fromDomain, record.DkimAlignment);

        bool pass = spfAligned || dkimAligned;
        string alignedDomain = string.Empty;
        if (dkimAligned) alignedDomain = dkim.Domain;
        else if (spfAligned) alignedDomain = mailFromDomain;

        string explanation;
        if (pass)
        {
            explanation = $"DMARC pass via " + (dkimAligned ? "DKIM" : "SPF") +
                $" alignment with {fromDomain}.";
        }
        else
        {
            explanation = $"DMARC fail: SPF aligned={spfAligned}, DKIM aligned={dkimAligned}.";
        }

        return new DmarcDetail
        {
            Result = pass ? DmarcResult.Pass : DmarcResult.Fail,
            FromDomain = fromDomain,
            Policy = record.Policy,
            SpfAlignment = record.SpfAlignment,
            DkimAlignment = record.DkimAlignment,
            SpfAligned = spfAligned,
            DkimAligned = dkimAligned,
            AlignedDomain = alignedDomain,
            Explanation = explanation,
        };
    }

    /// <summary>
    /// Look up the DMARC record at <c>_dmarc.&lt;domain&gt;</c>. If absent,
    /// per RFC 7489 §6.6.3 the lookup walks up to the organizational
    /// domain. We approximate this by also trying the registered-domain
    /// portion (everything after the first dot) once.
    /// </summary>
    private async System.Threading.Tasks.Task<DmarcRecord?> LookupDmarcAsync(string domain, System.Threading.CancellationToken ct)
    {
        DmarcRecord? r = await this.LookupOneAsync($"_dmarc.{domain}", ct).ConfigureAwait(false);
        if (r is not null) return r;

        // Try organizational domain (strip one label).
        int dot = domain.IndexOf('.', System.StringComparison.Ordinal);
        if (dot >= 0 && dot < domain.Length - 1)
        {
            string orgDomain = domain.Substring(dot + 1);
            r = await this.LookupOneAsync($"_dmarc.{orgDomain}", ct).ConfigureAwait(false);
        }
        return r;
    }

    private async System.Threading.Tasks.Task<DmarcRecord?> LookupOneAsync(string name, System.Threading.CancellationToken ct)
    {
        System.Collections.Generic.IReadOnlyList<string> records =
            await this.dns.LookupTxtAsync(name, ct).ConfigureAwait(false);
        foreach (string r in records)
        {
            if (r.StartsWith("v=DMARC1", System.StringComparison.OrdinalIgnoreCase))
            {
                return ParseRecord(r);
            }
        }
        return null;
    }

    /// <summary>
    /// Parse a DMARC TXT record into structured form.
    /// </summary>
    internal static DmarcRecord ParseRecord(string raw)
    {
        System.Collections.Generic.Dictionary<string, string> tags = DkimVerifier.ParseTags(raw);

        if (!tags.TryGetValue("v", out string? v) ||
            !string.Equals(v, "DMARC1", System.StringComparison.OrdinalIgnoreCase))
        {
            throw new DmarcParseError("DMARC record missing v=DMARC1.");
        }
        if (!tags.TryGetValue("p", out string? pStr))
        {
            throw new DmarcParseError("DMARC record missing required p= tag.");
        }
        DmarcPolicy policy = pStr.ToLowerInvariant() switch
        {
            "none" => DmarcPolicy.None,
            "quarantine" => DmarcPolicy.Quarantine,
            "reject" => DmarcPolicy.Reject,
            _ => throw new DmarcParseError($"Unrecognized p= value: {pStr}"),
        };

        AlignmentMode spfAlign = AlignmentMode.Relaxed;
        if (tags.TryGetValue("aspf", out string? aspf) &&
            string.Equals(aspf, "s", System.StringComparison.OrdinalIgnoreCase))
        {
            spfAlign = AlignmentMode.Strict;
        }
        AlignmentMode dkimAlign = AlignmentMode.Relaxed;
        if (tags.TryGetValue("adkim", out string? adkim) &&
            string.Equals(adkim, "s", System.StringComparison.OrdinalIgnoreCase))
        {
            dkimAlign = AlignmentMode.Strict;
        }

        return new DmarcRecord
        {
            Policy = policy,
            SpfAlignment = spfAlign,
            DkimAlignment = dkimAlign,
        };
    }

    /// <summary>
    /// Check whether <paramref name="candidate"/> aligns with
    /// <paramref name="fromDomain"/> under the given mode. Strict =
    /// exact match (case-insensitive). Relaxed = organizational domain
    /// match - the candidate must equal the From domain or end with
    /// <c>.&lt;fromDomain&gt;</c>.
    /// </summary>
    private static bool IsAligned(string candidate, string fromDomain, AlignmentMode mode)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (string.Equals(candidate, fromDomain, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (mode == AlignmentMode.Strict) return false;

        // Relaxed: candidate must be a subdomain of fromDomain, OR
        // fromDomain must be a subdomain of candidate.
        string suffix1 = "." + fromDomain;
        string suffix2 = "." + candidate;
        if (candidate.EndsWith(suffix1, System.StringComparison.OrdinalIgnoreCase)) return true;
        if (fromDomain.EndsWith(suffix2, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal sealed class DmarcRecord
    {
        public DmarcPolicy Policy { get; init; }
        public AlignmentMode SpfAlignment { get; init; }
        public AlignmentMode DkimAlignment { get; init; }
    }

    internal sealed class DmarcParseError : System.Exception
    {
        public DmarcParseError(string message) : base(message) { }
    }
}
