namespace Anjal.Smtp;

/// <summary>
/// Configuration for a <see cref="DirectMailSender"/>.
/// </summary>
public sealed class DirectSenderOptions
{
    /// <summary>Hostname this client claims in EHLO (should match reverse DNS in production).</summary>
    public string ClientHostName { get; init; } = "anjal.localhost";

    /// <summary>TCP port to connect to remote MXs. Always 25 on the public internet.</summary>
    public int Port { get; init; } = 25;

    /// <summary>Connect timeout per MX attempt.</summary>
    public System.TimeSpan ConnectTimeout { get; init; } = System.TimeSpan.FromSeconds(15);

    /// <summary>
    /// TLS configuration. When null, TLS is disabled. The policy lookup is
    /// keyed by destination domain (e.g. "gmail.com"), not MX hostname.
    /// </summary>
    public TlsClientOptions? Tls { get; init; }

    /// <summary>
    /// Looks up whether a domain has an address, for implicit MX (v1.0.0-rc.9,
    /// RFC 5321 5.1). Null uses the system resolver; tests replace it.
    /// </summary>
    public System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<HostLookup>>? HostLookup { get; init; }
}

/// <summary>
/// Outbound sender that looks up the destination domain's MX records and
/// connects directly. Used when the host has port 25 outbound permitted.
/// Tries each MX in priority order; falls through on transient failures.
/// </summary>
public sealed class DirectMailSender : IMailSender
{
    private readonly Anjal.Dns.DnsResolver dns;
    private readonly DirectSenderOptions options;

    /// <summary>
    /// Construct a direct sender.
    /// </summary>
    /// <param name="dns">DNS resolver for MX lookups.</param>
    /// <param name="options">Sender configuration.</param>
    public DirectMailSender(Anjal.Dns.DnsResolver dns, DirectSenderOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(dns);
        System.ArgumentNullException.ThrowIfNull(options);
        this.dns = dns;
        this.options = options;
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<SendResult> SendAsync(
        OutboundDelivery delivery,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.EnvelopeTo.Count == 0)
        {
            return new SendResult { Outcome = SendOutcome.PermanentFailure, Message = "No recipients" };
        }

        string? domain = DomainOf(delivery.EnvelopeTo[0]);
        if (domain is null)
        {
            return new SendResult { Outcome = SendOutcome.PermanentFailure, Message = $"Recipient '{delivery.EnvelopeTo[0]}' has no domain" };
        }

        // All recipients must share the destination domain - the caller groups
        // by domain before invoking. Reject if mixed.
        foreach (string r in delivery.EnvelopeTo)
        {
            string? d = DomainOf(r);
            if (d is null || !string.Equals(d, domain, System.StringComparison.OrdinalIgnoreCase))
            {
                return new SendResult { Outcome = SendOutcome.PermanentFailure, Message = "Recipients span multiple destination domains" };
            }
        }

        System.Collections.Generic.IReadOnlyList<Anjal.Dns.MxRecord> mxs;
        try
        {
            mxs = await this.dns.ResolveMxAsync(domain, ct).ConfigureAwait(false);
        }
        catch (Anjal.Dns.DnsException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"DNS error for {domain}: {ex.Message}" };
        }

        // RFC 7505 (v1.0.0-rc.9, DEF-071): a null MX - the root name "." - says the
        // domain accepts no mail. Fail at once; never attempt delivery.
        if (mxs.Count > 0 && mxs.All(m => IsRootName(m.Exchange)))
        {
            return new SendResult { Outcome = SendOutcome.PermanentFailure, ReplyCode = 556, Message = $"556 5.1.10 {domain} does not accept mail (null MX, RFC 7505)" };
        }

        var hosts = mxs.Where(m => !IsRootName(m.Exchange)).Select(m => m.Exchange).ToList();
        if (hosts.Count == 0)
        {
            // RFC 5321 5.1 (v1.0.0-rc.9, DEF-067): with no MX, the domain itself
            // is the mail host ("implicit MX") - if it has an address.
            HostLookup found = await (this.options.HostLookup ?? SystemHostLookupAsync)(domain, ct).ConfigureAwait(false);
            switch (found)
            {
                case Smtp.HostLookup.Found:
                    hosts.Add(domain);
                    break;
                case Smtp.HostLookup.NotFound:
                    return new SendResult { Outcome = SendOutcome.PermanentFailure, ReplyCode = 550, Message = $"550 5.1.2 {domain} has no mail server: no MX and no address record (RFC 5321 5.1)" };
                default:
                    return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"DNS lookup of {domain} failed temporarily" };
            }
        }

        SendResult? lastResult = null;
        foreach (string host in hosts)
        {
            SendResult attempt = await this.TryDeliverToHostAsync(host, domain, delivery, ct).ConfigureAwait(false);
            lastResult = attempt;
            if (attempt.Outcome == SendOutcome.Sent)
            {
                return attempt;
            }
            if (attempt.Outcome == SendOutcome.PermanentFailure)
            {
                return attempt;
            }
            // Transient: try the next MX.
        }
        return lastResult ?? new SendResult { Outcome = SendOutcome.TransientFailure, Message = "No MX could be tried" };
    }

    private static bool IsRootName(string name) => name.Length == 0 || name == ".";

    /// <summary>The system resolver's answer: an address, no such host, or a temporary failure.</summary>
    private static async System.Threading.Tasks.Task<HostLookup> SystemHostLookupAsync(string host, System.Threading.CancellationToken ct)
    {
        try
        {
            System.Net.IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length > 0 ? Smtp.HostLookup.Found : Smtp.HostLookup.NotFound;
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData)
        {
            return Smtp.HostLookup.NotFound;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return Smtp.HostLookup.TryAgain;
        }
    }

    private async System.Threading.Tasks.Task<SendResult> TryDeliverToHostAsync(
        string host,
        string domain,
        OutboundDelivery delivery,
        System.Threading.CancellationToken ct)
    {
        // v1.0.0-rc.8: every result - success, refusal or network error - names
        // the server spoken to and the TLS used, for the evidence of the attempt.
        var route = new RouteNote();
        SendResult result = await this.TryDeliverToHostCoreAsync(host, domain, delivery, route, ct).ConfigureAwait(false);
        return result.WithRoute(host, route.Tls);
    }

    private async System.Threading.Tasks.Task<SendResult> TryDeliverToHostCoreAsync(
        string host,
        string domain,
        OutboundDelivery delivery,
        RouteNote route,
        System.Threading.CancellationToken ct)
    {
        try
        {
            using SmtpClientSession session = await SmtpClientSession
                .ConnectAsync(host, this.options.Port, this.options.ConnectTimeout, ct)
                .ConfigureAwait(false);

            SmtpReply ehlo = await session.EhloAsync(this.options.ClientHostName, ct).ConfigureAwait(false);
            if (ehlo.Code != 250)
            {
                await session.QuitAsync(ct).ConfigureAwait(false);
                return RelayMailSender.ClassifyReply(ehlo, $"EHLO to {host}");
            }

            // STARTTLS using policy keyed by destination domain, then re-issue EHLO.
            if (this.options.Tls is not null)
            {
                Anjal.Store.TlsMode mode = await this.options.Tls.ResolveModeAsync(domain, ct).ConfigureAwait(false);
                bool offered = SmtpClientSession.EhloSupportsStartTls(ehlo);

                TlsAction action = TlsDecision.Decide(mode, offered, this.options.Tls.AllowPlaintext, loopback: false);
                if (action == TlsAction.Hold)
                {
                    // Never unencrypted (owner's rule, 27 Sep 2026): hold the
                    // message; the worker retries and finally returns it.
                    await session.QuitAsync(ct).ConfigureAwait(false);
                    return new SendResult
                    {
                        Outcome = SendOutcome.TransientFailure,
                        Message = mode == Anjal.Store.TlsMode.Required
                            ? $"TLS required for {domain} but {host} did not advertise STARTTLS"
                            : $"Not sent: {host} does not offer encryption (STARTTLS), and this server never sends mail unencrypted",
                    };
                }
                if (action == TlsAction.StartTls)
                {
                    // Opportunistic TLS encrypts without authenticating the
                    // peer (RFC 7435): many MX hosts present self-signed or
                    // mismatched certificates, and refusing them would mean
                    // sending nothing at all rather than sending encrypted.
                    // A domain whose policy requires TLS gets full validation.
                    bool validate = mode == Anjal.Store.TlsMode.Required && this.options.Tls.ValidateCertificate;
                    SmtpReply tlsReply = await session.StartTlsAsync(host, validate, this.options.Tls.Revocation, ct).ConfigureAwait(false);
                    if (tlsReply.Code != 220)
                    {
                        await session.QuitAsync(ct).ConfigureAwait(false);
                        return RelayMailSender.ClassifyReply(tlsReply, $"STARTTLS at {host}");
                    }
                    route.Tls = session.NegotiatedTls;
                    SmtpReply ehlo2 = await session.EhloAsync(this.options.ClientHostName, ct).ConfigureAwait(false);
                    if (ehlo2.Code != 250)
                    {
                        await session.QuitAsync(ct).ConfigureAwait(false);
                        return RelayMailSender.ClassifyReply(ehlo2, $"EHLO (after STARTTLS) at {host}");
                    }
                }
            }

            SmtpReply mailFrom = await session.MailFromAsync(delivery.EnvelopeFrom, ct).ConfigureAwait(false);
            if (mailFrom.Code != 250)
            {
                await session.QuitAsync(ct).ConfigureAwait(false);
                return RelayMailSender.ClassifyReply(mailFrom, $"MAIL FROM at {host}");
            }

            foreach (string rcpt in delivery.EnvelopeTo)
            {
                SmtpReply r = await session.RcptToAsync(rcpt, ct).ConfigureAwait(false);
                if (r.Code != 250 && r.Code != 251)
                {
                    await session.QuitAsync(ct).ConfigureAwait(false);
                    return RelayMailSender.ClassifyReply(r, $"RCPT TO:<{rcpt}> at {host}");
                }
            }

            SmtpReply data = await session.DataAsync(delivery.RawBytes, ct).ConfigureAwait(false);
            await session.QuitAsync(ct).ConfigureAwait(false);

            if (data.Code == 250)
            {
                string tls = session.NegotiatedTls is null ? "unencrypted" : session.NegotiatedTls;
                return new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = $"Accepted by {host} ({tls}): {data.Text}" };
            }
            return RelayMailSender.ClassifyReply(data, $"DATA at {host}");
        }
        catch (SmtpProtocolException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"Protocol error talking to {host}: {ex.Message}" };
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"Network error to {host}: {ex.Message}" };
        }
        catch (System.IO.IOException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"IO error to {host}: {ex.Message}" };
        }
        catch (System.OperationCanceledException)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"Timed out connecting to {host}" };
        }
    }

    private static string? DomainOf(string address)
    {
        int at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return null;
        }
        return address.Substring(at + 1).ToLowerInvariant();
    }
}

/// <summary>Whether a domain has an address (v1.0.0-rc.9, implicit MX).</summary>
public enum HostLookup
{
    /// <summary>At least one address.</summary>
    Found,

    /// <summary>No such host, or no address records: a permanent answer.</summary>
    NotFound,

    /// <summary>The lookup failed temporarily.</summary>
    TryAgain,
}
