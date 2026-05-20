using System.Net;

namespace Anjal.Auth;

/// <summary>
/// Orchestrates SPF + DKIM + DMARC verification for an inbound message.
/// Designed to be invoked from <c>SmtpSession</c> after DATA, producing
/// a structured <see cref="AuthenticationResults"/> for the routing
/// pipeline.
/// </summary>
public sealed class InboundAuthenticator
{
    private readonly SpfVerifier spf;
    private readonly DkimVerifier dkim;
    private readonly DmarcEvaluator dmarc;
    private readonly string servingHost;

    /// <summary>Construct.</summary>
    /// <param name="dns">Shared DNS resolver.</param>
    /// <param name="servingHost">This server's hostname, used as the <c>authserv-id</c>
    /// in the <c>Authentication-Results</c> header.</param>
    public InboundAuthenticator(Anjal.Dns.DnsResolver dns, string servingHost)
    {
        System.ArgumentNullException.ThrowIfNull(dns);
        System.ArgumentNullException.ThrowIfNull(servingHost);
        this.spf = new SpfVerifier(dns);
        this.dkim = new DkimVerifier(dns);
        this.dmarc = new DmarcEvaluator(dns);
        this.servingHost = servingHost;
    }

    /// <summary>
    /// Run all three checks and assemble an <see cref="AuthenticationResults"/>.
    /// </summary>
    /// <param name="peerAddress">Client IP for SPF.</param>
    /// <param name="envelopeFrom">SMTP MAIL FROM value (may be empty for bounces).</param>
    /// <param name="messageBytes">Full RFC 5322 message for DKIM verification + From-header extraction.</param>
    /// <param name="ct">Cancellation.</param>
    public async System.Threading.Tasks.Task<AuthenticationResults> AuthenticateAsync(
        IPAddress peerAddress,
        string envelopeFrom,
        byte[] messageBytes,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(peerAddress);
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        System.ArgumentNullException.ThrowIfNull(messageBytes);

        // The MAIL FROM domain is used by SPF. For empty MAIL FROM (bounces),
        // SPF falls back to checking the HELO identity; we use envelope.
        string mailFromDomain = ExtractDomain(envelopeFrom);

        // Extract the From: header's domain for DMARC.
        string fromDomain = ExtractFromDomain(messageBytes);

        // Run SPF and DKIM in parallel (they don't depend on each other).
        System.Threading.Tasks.Task<SpfDetail> spfTask = this.spf.CheckAsync(peerAddress, mailFromDomain, ct);
        System.Threading.Tasks.Task<DkimDetail> dkimTask = this.dkim.VerifyAsync(messageBytes, ct);
        await System.Threading.Tasks.Task.WhenAll(spfTask, dkimTask).ConfigureAwait(false);
        SpfDetail spfDetail = await spfTask.ConfigureAwait(false);
        DkimDetail dkimDetail = await dkimTask.ConfigureAwait(false);

        // Then DMARC, which needs both results.
        DmarcDetail dmarcDetail = await this.dmarc.EvaluateAsync(
            fromDomain, mailFromDomain, spfDetail, dkimDetail, ct).ConfigureAwait(false);

        string header = AuthenticationResultsBuilder.Build(
            this.servingHost, spfDetail, dkimDetail, dmarcDetail);

        return new AuthenticationResults
        {
            ServingHost = this.servingHost,
            Spf = spfDetail,
            Dkim = dkimDetail,
            Dmarc = dmarcDetail,
            HeaderValue = header,
        };
    }

    /// <summary>Extract the domain portion from a "user@domain" address.</summary>
    private static string ExtractDomain(string address)
    {
        if (string.IsNullOrEmpty(address)) return string.Empty;
        int at = address.LastIndexOf('@');
        return at < 0 ? string.Empty : address.Substring(at + 1).Trim().ToLowerInvariant();
    }

    /// <summary>Extract the domain from the message's From header.</summary>
    private static string ExtractFromDomain(byte[] messageBytes)
    {
        try
        {
            Anjal.Dkim.DkimMessage parsed = Anjal.Dkim.DkimMessage.Parse(messageBytes);
            string? fromValue = parsed.GetHeaderValue("From");
            if (fromValue is null) return string.Empty;

            int lt = fromValue.IndexOf('<', System.StringComparison.Ordinal);
            int gt = fromValue.IndexOf('>', System.StringComparison.Ordinal);
            string addr = (lt >= 0 && gt > lt) ? fromValue.Substring(lt + 1, gt - lt - 1) : fromValue.Trim();
            return ExtractDomain(addr);
        }
#pragma warning disable CA1031
        catch (System.Exception)
        {
            return string.Empty;
        }
#pragma warning restore CA1031
    }
}
