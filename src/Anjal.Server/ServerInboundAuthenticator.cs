namespace Anjal.Server;

/// <summary>
/// Adapts <see cref="Anjal.Auth.InboundAuthenticator"/> to the
/// <see cref="Anjal.Smtp.IInboundAuthenticator"/> interface expected by
/// <c>SmtpServer</c>. Decides whether DMARC verdict should trigger an
/// SMTP-layer reject based on the configured enforcement mode.
/// </summary>
public sealed class ServerInboundAuthenticator : Anjal.Smtp.IInboundAuthenticator
{
    private readonly Anjal.Auth.InboundAuthenticator inner;
    private readonly bool enforceDmarcReject;

    /// <summary>Construct.</summary>
    /// <param name="inner">The actual authenticator from <c>Anjal.Auth</c>.</param>
    /// <param name="enforceDmarcReject">When true, set <c>ShouldReject</c> on
    /// the result when DMARC says <c>p=reject</c> and authentication failed.</param>
    public ServerInboundAuthenticator(Anjal.Auth.InboundAuthenticator inner, bool enforceDmarcReject)
    {
        System.ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.enforceDmarcReject = enforceDmarcReject;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<Anjal.Smtp.InboundAuthResult> AuthenticateAsync(
        string remoteAddress,
        string envelopeFrom,
        byte[] messageBytes,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(remoteAddress);
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        System.ArgumentNullException.ThrowIfNull(messageBytes);

        if (!System.Net.IPAddress.TryParse(remoteAddress, out System.Net.IPAddress? peer) || peer is null)
        {
            return new Anjal.Smtp.InboundAuthResult
            {
                HeaderValue = string.Empty,
                ShouldReject = false,
                Detail = null,
            };
        }

        Anjal.Auth.AuthenticationResults results =
            await this.inner.AuthenticateAsync(peer, envelopeFrom, messageBytes, ct).ConfigureAwait(false);

        bool reject = this.enforceDmarcReject &&
            results.Dmarc.Result == Anjal.Auth.DmarcResult.Fail &&
            results.Dmarc.Policy == Anjal.Auth.DmarcPolicy.Reject;

        return new Anjal.Smtp.InboundAuthResult
        {
            HeaderValue = results.HeaderValue,
            ShouldReject = reject,
            Detail = results,
            RejectReason = reject
                ? $"DMARC policy reject for {results.Dmarc.FromDomain} (no aligned pass)"
                : string.Empty,
        };
    }
}
