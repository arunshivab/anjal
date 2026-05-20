using System.Net;
using System.Net.Sockets;

namespace Anjal.Auth;

/// <summary>
/// Verifies SPF records per RFC 7208. Supports the mechanisms
/// <c>all</c>, <c>ip4</c>, <c>ip6</c>, <c>a</c>, <c>mx</c>, <c>include</c>,
/// <c>exists</c>, and the <c>redirect=</c> modifier. The <c>ptr</c>
/// mechanism is treated as Neutral (deprecated by RFC 7208 §5.5).
/// Macro expansion (<c>exp=</c>) is not implemented; such records evaluate
/// based on their literal tokens, which is correct for the vast majority
/// of modern records.
/// </summary>
public sealed class SpfVerifier
{
    /// <summary>Maximum total DNS lookups during a single check (RFC 7208 §4.6.4).</summary>
    public const int MaxLookups = 10;

    private readonly Anjal.Dns.DnsResolver dns;

    /// <summary>Construct.</summary>
    /// <param name="dns">DNS resolver for TXT and A/MX lookups.</param>
    public SpfVerifier(Anjal.Dns.DnsResolver dns)
    {
        System.ArgumentNullException.ThrowIfNull(dns);
        this.dns = dns;
    }

    /// <summary>
    /// Run an SPF check. The <paramref name="peerAddress"/> is the IP that
    /// connected to Anjal; <paramref name="mailFromDomain"/> is from the
    /// SMTP MAIL FROM (RFC 7208 calls this the "MAIL FROM identity"); if
    /// MAIL FROM is empty (bounce), use the HELO identity instead.
    /// </summary>
    /// <param name="peerAddress">Connecting client's IP address.</param>
    /// <param name="mailFromDomain">Domain to check against.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<SpfDetail> CheckAsync(
        IPAddress peerAddress,
        string mailFromDomain,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(peerAddress);
        System.ArgumentNullException.ThrowIfNull(mailFromDomain);

        // Empty MAIL FROM domain is legitimate for bounce messages (RFC 5321
        // section 4.5.5). RFC 7208 section 2.4 says receivers SHOULD then
        // check HELO instead, but our caller has already chosen what to pass
        // here. If the domain is empty, we report None - no verdict
        // computable - rather than treating it as an error.
        if (string.IsNullOrWhiteSpace(mailFromDomain))
        {
            return new SpfDetail
            {
                Result = SpfResult.None,
                Domain = string.Empty,
                PeerAddress = peerAddress.ToString(),
                Explanation = "SPF check skipped (empty MAIL FROM domain).",
            };
        }

        var state = new SpfState { PeerAddress = peerAddress };
        SpfResult verdict;
        string mechanism = string.Empty;
        string explanation;

        try
        {
            (verdict, mechanism) = await this.EvaluateDomainAsync(mailFromDomain, state, ct).ConfigureAwait(false);
            explanation = ExplainResult(verdict, mailFromDomain, peerAddress, mechanism);
        }
        catch (SpfPermError ex)
        {
            verdict = SpfResult.PermError;
            explanation = $"SPF PermError: {ex.Message}";
        }
        catch (SpfTempError ex)
        {
            verdict = SpfResult.TempError;
            explanation = $"SPF TempError: {ex.Message}";
        }
        catch (Anjal.Dns.DnsException ex)
        {
            verdict = SpfResult.TempError;
            explanation = $"SPF DNS error: {ex.Message}";
        }
        catch (System.ArgumentException ex)
        {
            // Defensive: any malformed-domain error from deeper layers
            // (e.g. include: targets that hit empty domain validation in
            // DNS) becomes a PermError rather than crashing the verifier.
            verdict = SpfResult.PermError;
            explanation = $"SPF malformed domain: {ex.Message}";
        }

        return new SpfDetail
        {
            Result = verdict,
            Domain = mailFromDomain,
            PeerAddress = peerAddress.ToString(),
            MatchedMechanism = mechanism,
            LookupCount = state.LookupCount,
            Explanation = explanation,
        };
    }

    /// <summary>
    /// Recursive evaluation for include/redirect chains. Each include
    /// counts against the global lookup limit tracked on <paramref name="state"/>.
    /// </summary>
    private async System.Threading.Tasks.Task<(SpfResult, string)> EvaluateDomainAsync(
        string domain,
        SpfState state,
        System.Threading.CancellationToken ct)
    {
        // Fetch the SPF record (a TXT record starting with "v=spf1").
        string? record = await this.FetchSpfRecordAsync(domain, state, ct).ConfigureAwait(false);
        if (record is null)
        {
            return (SpfResult.None, string.Empty);
        }

        string[] tokens = record.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        // First token is "v=spf1"; skip it.
        string? redirect = null;

        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token.StartsWith("redirect=", System.StringComparison.OrdinalIgnoreCase))
            {
                redirect = token.Substring("redirect=".Length);
                continue;
            }
            if (token.StartsWith("exp=", System.StringComparison.OrdinalIgnoreCase))
            {
                // Skip explanation modifier (we don't fetch or use it).
                continue;
            }

            (char qualifier, string mech) = SplitQualifier(token);
            (bool matched, string detail) = await this.EvaluateMechanismAsync(mech, domain, state, ct).ConfigureAwait(false);
            if (matched)
            {
                return (QualifierToResult(qualifier), detail);
            }
        }

        // No mechanism matched. Apply redirect if present.
        if (!string.IsNullOrEmpty(redirect))
        {
            state.IncrementLookups();
            return await this.EvaluateDomainAsync(redirect!, state, ct).ConfigureAwait(false);
        }

        // No match and no redirect: result is Neutral (RFC 7208 §4.7).
        return (SpfResult.Neutral, "default");
    }

    /// <summary>
    /// Fetch and select the SPF TXT record for a domain. RFC 7208 §4.5:
    /// at most one v=spf1 record is permitted; multiple → PermError.
    /// </summary>
    private async System.Threading.Tasks.Task<string?> FetchSpfRecordAsync(
        string domain,
        SpfState state,
        System.Threading.CancellationToken ct)
    {
        state.IncrementLookups();
        System.Collections.Generic.IReadOnlyList<string> records =
            await this.dns.LookupTxtAsync(domain, ct).ConfigureAwait(false);

        string? found = null;
        foreach (string r in records)
        {
            if (r.StartsWith("v=spf1 ", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r, "v=spf1", System.StringComparison.OrdinalIgnoreCase))
            {
                if (found is not null)
                {
                    throw new SpfPermError($"Multiple v=spf1 records found for {domain}.");
                }
                found = r;
            }
        }
        return found;
    }

    /// <summary>
    /// Evaluate a single mechanism token (without the qualifier prefix).
    /// Returns (matched, detailString) where detailString is suitable for
    /// the SpfDetail.MatchedMechanism field.
    /// </summary>
    private async System.Threading.Tasks.Task<(bool, string)> EvaluateMechanismAsync(
        string mech,
        string currentDomain,
        SpfState state,
        System.Threading.CancellationToken ct)
    {
        // all mechanism: always matches.
        if (string.Equals(mech, "all", System.StringComparison.OrdinalIgnoreCase))
        {
            return (true, "all");
        }

        // ip4:CIDR
        if (mech.StartsWith("ip4:", System.StringComparison.OrdinalIgnoreCase))
        {
            string spec = mech.Substring(4);
            return (MatchesCidr(state.PeerAddress, spec, AddressFamily.InterNetwork), $"ip4:{spec}");
        }

        // ip6:CIDR
        if (mech.StartsWith("ip6:", System.StringComparison.OrdinalIgnoreCase))
        {
            string spec = mech.Substring(4);
            return (MatchesCidr(state.PeerAddress, spec, AddressFamily.InterNetworkV6), $"ip6:{spec}");
        }

        // include:domain - recurse, return matched only if Pass; PermError/TempError propagate.
        if (mech.StartsWith("include:", System.StringComparison.OrdinalIgnoreCase))
        {
            string inc = mech.Substring("include:".Length);
            state.IncrementLookups();
            (SpfResult incResult, _) = await this.EvaluateDomainAsync(inc, state, ct).ConfigureAwait(false);
            // RFC 7208 §5.2: include matches only on Pass. Fail/SoftFail/Neutral
            // don't match (we keep evaluating). PermError/TempError propagate.
            if (incResult == SpfResult.PermError)
            {
                throw new SpfPermError($"include:{inc} -> PermError");
            }
            if (incResult == SpfResult.TempError)
            {
                throw new SpfTempError($"include:{inc} -> TempError");
            }
            if (incResult == SpfResult.None)
            {
                throw new SpfPermError($"include:{inc} -> no SPF record");
            }
            return (incResult == SpfResult.Pass, $"include:{inc}");
        }

        // a[:domain][/cidr] - matches if peer's IP is in the A/AAAA of the domain.
        if (mech.Equals("a", System.StringComparison.OrdinalIgnoreCase) ||
            mech.StartsWith("a:", System.StringComparison.OrdinalIgnoreCase) ||
            mech.StartsWith("a/", System.StringComparison.OrdinalIgnoreCase))
        {
            string targetDomain = currentDomain;
            string? cidr = null;
            ParseADomainAndCidr(mech, "a", currentDomain, out targetDomain, out cidr);
            state.IncrementLookups();
            bool matched = await this.MatchesAOrMxAsync(targetDomain, cidr, state.PeerAddress, useMx: false, ct).ConfigureAwait(false);
            return (matched, $"a:{targetDomain}");
        }

        // mx[:domain][/cidr] - matches if peer's IP is any MX record's IP for the domain.
        if (mech.Equals("mx", System.StringComparison.OrdinalIgnoreCase) ||
            mech.StartsWith("mx:", System.StringComparison.OrdinalIgnoreCase) ||
            mech.StartsWith("mx/", System.StringComparison.OrdinalIgnoreCase))
        {
            string targetDomain = currentDomain;
            string? cidr = null;
            ParseADomainAndCidr(mech, "mx", currentDomain, out targetDomain, out cidr);
            state.IncrementLookups();
            bool matched = await this.MatchesAOrMxAsync(targetDomain, cidr, state.PeerAddress, useMx: true, ct).ConfigureAwait(false);
            return (matched, $"mx:{targetDomain}");
        }

        // exists:domain - matches if the domain has any A record.
        if (mech.StartsWith("exists:", System.StringComparison.OrdinalIgnoreCase))
        {
            string ex = mech.Substring("exists:".Length);
            state.IncrementLookups();
            try
            {
                IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(ex, ct).ConfigureAwait(false);
                return (addresses.Length > 0, $"exists:{ex}");
            }
#pragma warning disable CA1031
            catch (System.Exception)
            {
                return (false, $"exists:{ex}");
            }
#pragma warning restore CA1031
        }

        // ptr is deprecated (RFC 7208 §5.5). Treat as never matching.
        if (mech.Equals("ptr", System.StringComparison.OrdinalIgnoreCase) ||
            mech.StartsWith("ptr:", System.StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ptr (deprecated, treated as no match)");
        }

        // Unknown mechanism → PermError.
        throw new SpfPermError($"Unknown mechanism '{mech}'.");
    }

    /// <summary>Resolve a domain via system DNS (A records for IPv4 or AAAA for IPv6)
    /// or via Anjal.Dns for MX, and check if any resulting IP matches the peer.</summary>
    private async System.Threading.Tasks.Task<bool> MatchesAOrMxAsync(
        string domain,
        string? cidr,
        IPAddress peer,
        bool useMx,
        System.Threading.CancellationToken ct)
    {
        IPAddress[] addresses;
        if (useMx)
        {
            System.Collections.Generic.IReadOnlyList<Anjal.Dns.MxRecord> mxes =
                await this.dns.ResolveMxAsync(domain, ct).ConfigureAwait(false);
            var collected = new System.Collections.Generic.List<IPAddress>();
            foreach (Anjal.Dns.MxRecord m in mxes)
            {
                try
                {
                    IPAddress[] mxIps = await System.Net.Dns.GetHostAddressesAsync(m.Exchange, ct).ConfigureAwait(false);
                    collected.AddRange(mxIps);
                }
#pragma warning disable CA1031
                catch (System.Exception)
                {
                    // Skip MX hosts that don't resolve.
                }
#pragma warning restore CA1031
            }
            addresses = collected.ToArray();
        }
        else
        {
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(domain, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (System.Exception)
            {
                addresses = System.Array.Empty<IPAddress>();
            }
#pragma warning restore CA1031
        }

        foreach (IPAddress ip in addresses)
        {
            if (cidr is null)
            {
                if (ip.Equals(peer)) return true;
            }
            else
            {
                string spec = $"{ip}/{cidr}";
                if (MatchesCidr(peer, spec, ip.AddressFamily)) return true;
            }
        }
        return false;
    }

    /// <summary>Parse the optional :domain and /cidr suffix from an a or mx mechanism.</summary>
    private static void ParseADomainAndCidr(string mech, string prefix, string defaultDomain, out string domain, out string? cidr)
    {
        domain = defaultDomain;
        cidr = null;
        string remainder = mech.Substring(prefix.Length);
        if (remainder.Length == 0) return;

        if (remainder[0] == ':')
        {
            remainder = remainder.Substring(1);
            int slash = remainder.IndexOf('/', System.StringComparison.Ordinal);
            if (slash >= 0)
            {
                domain = remainder.Substring(0, slash);
                cidr = remainder.Substring(slash + 1);
            }
            else
            {
                domain = remainder;
            }
        }
        else if (remainder[0] == '/')
        {
            cidr = remainder.Substring(1);
        }
    }

    /// <summary>Match an IP address against a CIDR specification.
    /// <paramref name="spec"/> is in the form "192.0.2.0/24" or "192.0.2.5"
    /// (no mask = all bits). Returns false if spec is malformed.</summary>
    private static bool MatchesCidr(IPAddress peer, string spec, AddressFamily expectedFamily)
    {
        int slash = spec.IndexOf('/', System.StringComparison.Ordinal);
        string addrPart = slash >= 0 ? spec.Substring(0, slash) : spec;
        int prefixLen;
        if (slash < 0)
        {
            prefixLen = expectedFamily == AddressFamily.InterNetwork ? 32 : 128;
        }
        else if (!int.TryParse(spec.AsSpan(slash + 1), out prefixLen))
        {
            return false;
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? netAddr) || netAddr is null)
        {
            return false;
        }
        if (netAddr.AddressFamily != expectedFamily) return false;
        if (peer.AddressFamily != expectedFamily) return false;

        byte[] netBytes = netAddr.GetAddressBytes();
        byte[] peerBytes = peer.GetAddressBytes();
        if (netBytes.Length != peerBytes.Length) return false;

        int wholeBytes = prefixLen / 8;
        int remBits = prefixLen % 8;

        for (int i = 0; i < wholeBytes; i++)
        {
            if (netBytes[i] != peerBytes[i]) return false;
        }
        if (remBits > 0 && wholeBytes < netBytes.Length)
        {
            int mask = 0xFF & (0xFF << (8 - remBits));
            if ((netBytes[wholeBytes] & mask) != (peerBytes[wholeBytes] & mask)) return false;
        }
        return true;
    }

    private static (char, string) SplitQualifier(string token)
    {
        if (token.Length == 0) return ('+', token);
        char first = token[0];
        if (first == '+' || first == '-' || first == '~' || first == '?')
        {
            return (first, token.Substring(1));
        }
        return ('+', token);
    }

    private static SpfResult QualifierToResult(char q) => q switch
    {
        '+' => SpfResult.Pass,
        '-' => SpfResult.Fail,
        '~' => SpfResult.SoftFail,
        '?' => SpfResult.Neutral,
        _ => SpfResult.Neutral,
    };

    private static string ExplainResult(SpfResult result, string domain, IPAddress peer, string mechanism)
    {
        return result switch
        {
            SpfResult.Pass => $"{peer} authorized by {domain} via {mechanism}",
            SpfResult.Fail => $"{peer} explicitly denied by {domain} via {mechanism}",
            SpfResult.SoftFail => $"{peer} soft-failed by {domain} via {mechanism}",
            SpfResult.Neutral => $"{domain} makes no assertion about {peer}",
            SpfResult.None => $"{domain} has no SPF record",
            SpfResult.PermError => $"{domain} has a malformed SPF record",
            SpfResult.TempError => $"DNS lookup for {domain} failed transiently",
            _ => string.Empty,
        };
    }

    private sealed class SpfState
    {
        public IPAddress PeerAddress { get; init; } = IPAddress.None;
        public int LookupCount { get; private set; }

        public void IncrementLookups()
        {
            this.LookupCount++;
            if (this.LookupCount > MaxLookups)
            {
                throw new SpfPermError($"DNS lookup limit ({MaxLookups}) exceeded.");
            }
        }
    }

    private sealed class SpfPermError : System.Exception
    {
        public SpfPermError(string message) : base(message) { }
    }

    private sealed class SpfTempError : System.Exception
    {
        public SpfTempError(string message) : base(message) { }
    }
}
