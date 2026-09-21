namespace Anjal.Smtp;

/// <summary>
/// Configuration for a <see cref="RelayMailSender"/>.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>Host of the relay (e.g. "smtp-relay.gmail.com").</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Port of the relay. 587 is the SMTP submission port.</summary>
    public int Port { get; init; } = 587;

    /// <summary>Hostname this client claims in EHLO.</summary>
    public string ClientHostName { get; init; } = "anjal.localhost";

    /// <summary>Connect timeout.</summary>
    public System.TimeSpan ConnectTimeout { get; init; } = System.TimeSpan.FromSeconds(15);

    /// <summary>TLS configuration. When null, TLS is disabled.</summary>
    public TlsClientOptions? Tls { get; init; }
}

/// <summary>
/// Outbound sender that always relays through a single configured host.
/// This is the implementation used when port 25 is blocked outbound
/// (e.g. Azure, AWS, most Indian VPS providers) and the host is an
/// authenticated SMTP relay service like SES, Mailgun, or Brevo.
///
/// Note: TLS and SMTP AUTH are NOT yet implemented in v0.3.0. This sender
/// will work against an unauthenticated test relay on the local machine
/// (the end-to-end example uses Anjal's own receiver as the test relay) but
/// not against production providers until Phase 2.
/// </summary>
public sealed class RelayMailSender : IMailSender
{
    private readonly RelayOptions options;

    /// <summary>
    /// Construct the relay sender with options.
    /// </summary>
    /// <param name="options">Relay configuration.</param>
    public RelayMailSender(RelayOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(options);
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

        try
        {
            using SmtpClientSession session = await SmtpClientSession
                .ConnectAsync(this.options.Host, this.options.Port, this.options.ConnectTimeout, ct)
                .ConfigureAwait(false);

            SmtpReply ehlo = await session.EhloAsync(this.options.ClientHostName, ct).ConfigureAwait(false);
            if (ehlo.Code != 250)
            {
                await session.QuitAsync(ct).ConfigureAwait(false);
                return ClassifyReply(ehlo, "EHLO");
            }

            // STARTTLS, then re-issue EHLO over the encrypted channel.
            if (this.options.Tls is not null)
            {
                Anjal.Store.TlsMode mode = await this.options.Tls.ResolveModeAsync(this.options.Host, ct).ConfigureAwait(false);
                bool offered = SmtpClientSession.EhloSupportsStartTls(ehlo);

                if (mode == Anjal.Store.TlsMode.Required && !offered)
                {
                    await session.QuitAsync(ct).ConfigureAwait(false);
                    return new SendResult
                    {
                        Outcome = SendOutcome.TransientFailure,
                        Message = $"TLS required for {this.options.Host} but server did not advertise STARTTLS",
                    };
                }
                if (mode != Anjal.Store.TlsMode.Disabled && offered)
                {
                    // A relay is a named, configured provider: always validated.
                    SmtpReply tlsReply = await session.StartTlsAsync(this.options.Host, this.options.Tls.ValidateCertificate, this.options.Tls.Revocation, ct).ConfigureAwait(false);
                    if (tlsReply.Code != 220)
                    {
                        await session.QuitAsync(ct).ConfigureAwait(false);
                        return ClassifyReply(tlsReply, "STARTTLS");
                    }
                    SmtpReply ehlo2 = await session.EhloAsync(this.options.ClientHostName, ct).ConfigureAwait(false);
                    if (ehlo2.Code != 250)
                    {
                        await session.QuitAsync(ct).ConfigureAwait(false);
                        return ClassifyReply(ehlo2, "EHLO (after STARTTLS)");
                    }
                }
            }

            SmtpReply mailFrom = await session.MailFromAsync(delivery.EnvelopeFrom, ct).ConfigureAwait(false);
            if (mailFrom.Code != 250)
            {
                await session.QuitAsync(ct).ConfigureAwait(false);
                return ClassifyReply(mailFrom, "MAIL FROM");
            }

            foreach (string rcpt in delivery.EnvelopeTo)
            {
                SmtpReply r = await session.RcptToAsync(rcpt, ct).ConfigureAwait(false);
                if (r.Code != 250 && r.Code != 251)
                {
                    await session.QuitAsync(ct).ConfigureAwait(false);
                    return ClassifyReply(r, $"RCPT TO:<{rcpt}>");
                }
            }

            SmtpReply data = await session.DataAsync(delivery.RawBytes, ct).ConfigureAwait(false);
            await session.QuitAsync(ct).ConfigureAwait(false);

            if (data.Code == 250)
            {
                return new SendResult { Outcome = SendOutcome.Sent, ReplyCode = 250, Message = data.Text };
            }
            return ClassifyReply(data, "DATA");
        }
        catch (SmtpProtocolException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"Protocol error: {ex.Message}" };
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"Network error: {ex.Message}" };
        }
        catch (System.IO.IOException ex)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = $"IO error: {ex.Message}" };
        }
        catch (System.OperationCanceledException)
        {
            return new SendResult { Outcome = SendOutcome.TransientFailure, Message = "Timed out" };
        }
    }

    /// <summary>
    /// Classify an SMTP reply code as transient (4xx) or permanent (5xx).
    /// Exposed as static for use by other senders.
    /// </summary>
    /// <param name="reply">The reply to classify.</param>
    /// <param name="stage">The transaction stage, for the message string.</param>
    /// <returns>A SendResult with the right outcome.</returns>
    public static SendResult ClassifyReply(SmtpReply reply, string stage)
    {
        System.ArgumentNullException.ThrowIfNull(reply);
        SendOutcome outcome = reply.Code >= 500
            ? SendOutcome.PermanentFailure
            : SendOutcome.TransientFailure;
        return new SendResult
        {
            Outcome = outcome,
            ReplyCode = reply.Code,
            Message = $"{stage}: {reply.Code} {reply.Text}",
        };
    }
}
