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

        if (mxs.Count == 0)
        {
            // RFC 5321 section 5.1: if there is no MX record, fall back to A/AAAA
            // ("implicit MX"). We treat absent MX as a permanent failure for v0.3.0;
            // implicit-MX fallback is a future improvement.
            return new SendResult { Outcome = SendOutcome.PermanentFailure, Message = $"No MX records for {domain}" };
        }

        SendResult? lastResult = null;
        foreach (Anjal.Dns.MxRecord mx in mxs)
        {
            SendResult attempt = await this.TryDeliverToHostAsync(mx.Exchange, domain, delivery, ct).ConfigureAwait(false);
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

    private async System.Threading.Tasks.Task<SendResult> TryDeliverToHostAsync(
        string host,
        string domain,
        OutboundDelivery delivery,
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

                if (mode == Anjal.Store.TlsMode.Required && !offered)
                {
                    await session.QuitAsync(ct).ConfigureAwait(false);
                    return new SendResult
                    {
                        Outcome = SendOutcome.TransientFailure,
                        Message = $"TLS required for {domain} but {host} did not advertise STARTTLS",
                    };
                }
                if (mode != Anjal.Store.TlsMode.Disabled && offered)
                {
                    SmtpReply tlsReply = await session.StartTlsAsync(host, this.options.Tls.ValidateCertificate, ct).ConfigureAwait(false);
                    if (tlsReply.Code != 220)
                    {
                        await session.QuitAsync(ct).ConfigureAwait(false);
                        return RelayMailSender.ClassifyReply(tlsReply, $"STARTTLS at {host}");
                    }
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
                return new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = $"Accepted by {host}: {data.Text}" };
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
