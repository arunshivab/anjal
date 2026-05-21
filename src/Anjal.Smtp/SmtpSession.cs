using System.Net.Sockets;
using System.Text;

namespace Anjal.Smtp;

/// <summary>
/// One connected SMTP client. Implements the RFC 5321 command grammar for the
/// subset of commands a non-relay server needs: HELO, EHLO, MAIL, RCPT,
/// DATA, RSET, NOOP, QUIT, HELP, VRFY. Line endings are normalised to CRLF
/// on output; input tolerates bare LF.
/// </summary>
public sealed class SmtpSession
{
    private const int MaxLineLength = 998 + 2; // RFC 5321 section 4.5.3.1.6 + CRLF

    private readonly TcpClient client;
    private Stream stream;
    private readonly SmtpServerOptions options;
    private readonly IMessageSink sink;
    private readonly IInboundAuthenticator? authenticator;
    private readonly bool enforceReject;
    private readonly ISmtpAuthenticator? smtpAuthenticator;
    private readonly ILocalDomainResolver? localDomains;
    private readonly string remoteAddress;
    private bool isTls;

    private State state;
    private string clientHostName = string.Empty;
    private string envelopeFrom = string.Empty;
    private readonly List<string> envelopeTo = new();
    private AuthenticatedUser? authenticatedUser;

    /// <summary>
    /// Construct a session for an accepted TCP client.
    /// </summary>
    /// <param name="client">The accepted client.</param>
    /// <param name="options">Server configuration.</param>
    /// <param name="sink">Where delivered messages go.</param>
    public SmtpSession(TcpClient client, SmtpServerOptions options, IMessageSink sink)
        : this(client, options, sink, authenticator: null, enforceReject: false,
               smtpAuthenticator: null, localDomains: null)
    { }

    /// <summary>
    /// Construct a session with inbound authentication (SPF/DKIM/DMARC) but
    /// no submission-side AUTH. Retained for backward compatibility with
    /// PR 8 callers.
    /// </summary>
    /// <param name="client">Accepted TCP client.</param>
    /// <param name="options">Server options.</param>
    /// <param name="sink">Message sink.</param>
    /// <param name="authenticator">Optional inbound authenticator (SPF/DKIM/DMARC).</param>
    /// <param name="enforceReject">When true and authenticator says DMARC reject,
    /// refuse the message with SMTP 550 before invoking the sink.</param>
    public SmtpSession(TcpClient client, SmtpServerOptions options, IMessageSink sink,
        IInboundAuthenticator? authenticator, bool enforceReject)
        : this(client, options, sink, authenticator, enforceReject,
               smtpAuthenticator: null, localDomains: null)
    { }

    /// <summary>
    /// Construct a session with full feature set: inbound auth, submission
    /// auth, and local-domain resolution. This is the constructor used by
    /// <see cref="SmtpServer"/> in PR 9 and later.
    /// </summary>
    /// <param name="client">Accepted TCP client.</param>
    /// <param name="options">Server options. The <see cref="SmtpServerOptions.Role"/>
    /// field determines whether this is an MTA listener or a Submission listener.</param>
    /// <param name="sink">Message sink.</param>
    /// <param name="authenticator">Optional inbound (SPF/DKIM/DMARC) authenticator.</param>
    /// <param name="enforceReject">Whether to enforce DMARC p=reject at SMTP layer.</param>
    /// <param name="smtpAuthenticator">For Submission role, validates AUTH PLAIN/LOGIN credentials.
    /// Required when role is Submission.</param>
    /// <param name="localDomains">For MTA role, decides whether a RCPT domain
    /// is local (must be local or RCPT is refused as relay-denied). When null,
    /// MTA role accepts any RCPT TO - useful for closed-network testing.</param>
    public SmtpSession(TcpClient client, SmtpServerOptions options, IMessageSink sink,
        IInboundAuthenticator? authenticator, bool enforceReject,
        ISmtpAuthenticator? smtpAuthenticator, ILocalDomainResolver? localDomains)
    {
        System.ArgumentNullException.ThrowIfNull(client);
        System.ArgumentNullException.ThrowIfNull(options);
        System.ArgumentNullException.ThrowIfNull(sink);

        this.client = client;
        this.options = options;
        this.sink = sink;
        this.authenticator = authenticator;
        this.enforceReject = enforceReject;
        this.smtpAuthenticator = smtpAuthenticator;
        this.localDomains = localDomains;
        this.stream = client.GetStream();
        this.remoteAddress = (client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? string.Empty;
        this.state = State.AwaitingGreeting;
    }

    /// <summary>
    /// Run the session until the client quits or disconnects. Safe to call
    /// once per session.
    /// </summary>
    /// <param name="ct">Cancellation that closes the connection if signalled.</param>
    public async System.Threading.Tasks.Task RunAsync(System.Threading.CancellationToken ct = default)
    {
        try
        {
            await this.WriteLineAsync($"220 {this.options.AdvertisedHostName} Anjal SMTP service ready", ct).ConfigureAwait(false);
            this.state = State.AwaitingHelo;

            while (!ct.IsCancellationRequested)
            {
                string? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                bool keep = await this.HandleCommandAsync(line, ct).ConfigureAwait(false);
                if (!keep)
                {
                    break;
                }
            }
        }
        catch (System.IO.IOException)
        {
            // Network drop - terminate the session.
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Connection reset by peer - terminate.
        }
        finally
        {
            try
            {
                this.client.Close();
            }
            catch (System.IO.IOException)
            {
                // Already closing - swallow.
            }
        }
    }

    private async System.Threading.Tasks.Task<bool> HandleCommandAsync(string line, System.Threading.CancellationToken ct)
    {
        (string verb, string args) = SplitCommand(line);
        string upper = verb.ToUpperInvariant();

        switch (upper)
        {
            case "EHLO": return await this.HandleEhloAsync(args, isExtended: true, ct).ConfigureAwait(false);
            case "HELO": return await this.HandleEhloAsync(args, isExtended: false, ct).ConfigureAwait(false);
            case "STARTTLS": return await this.HandleStarttlsAsync(ct).ConfigureAwait(false);
            case "AUTH": return await this.HandleAuthAsync(args, ct).ConfigureAwait(false);
            case "MAIL": return await this.HandleMailAsync(args, ct).ConfigureAwait(false);
            case "RCPT": return await this.HandleRcptAsync(args, ct).ConfigureAwait(false);
            case "DATA": return await this.HandleDataAsync(ct).ConfigureAwait(false);
            case "RSET": return await this.HandleRsetAsync(ct).ConfigureAwait(false);
            case "NOOP": return await this.HandleNoopAsync(ct).ConfigureAwait(false);
            case "QUIT": return await this.HandleQuitAsync(ct).ConfigureAwait(false);
            case "HELP": return await this.HandleHelpAsync(ct).ConfigureAwait(false);
            case "VRFY":
                // RFC 5321 section 3.5.2 - acceptable to deny VRFY for privacy.
                await this.WriteLineAsync("252 Cannot VRFY user, but will accept message and attempt delivery", ct).ConfigureAwait(false);
                return true;
            default:
                await this.WriteLineAsync("500 Syntax error, command unrecognised", ct).ConfigureAwait(false);
                return true;
        }
    }

    private async System.Threading.Tasks.Task<bool> HandleEhloAsync(string args, bool isExtended, System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await this.WriteLineAsync("501 Syntax: EHLO hostname", ct).ConfigureAwait(false);
            return true;
        }

        this.clientHostName = args.Trim();
        this.envelopeFrom = string.Empty;
        this.envelopeTo.Clear();
        this.state = State.Ready;

        if (isExtended)
        {
            // Multi-line response with capabilities. Each non-final line has "250-".
            await this.WriteLineAsync($"250-{this.options.AdvertisedHostName} Hello {this.clientHostName} [{this.remoteAddress}]", ct).ConfigureAwait(false);
            await this.WriteLineAsync($"250-SIZE {this.options.MaxMessageBytes}", ct).ConfigureAwait(false);
            await this.WriteLineAsync("250-8BITMIME", ct).ConfigureAwait(false);
            if (this.options.TlsCertificate is not null && !this.isTls)
            {
                await this.WriteLineAsync("250-STARTTLS", ct).ConfigureAwait(false);
            }
            // Advertise AUTH only on submission listeners, and only when
            // either TLS is active or plaintext AUTH is explicitly allowed.
            // Per RFC 4954 best practice, AUTH should not be offered on
            // insecure channels because credentials would travel in
            // clear/base64 on the wire.
            if (this.options.Role == SmtpServerRole.Submission &&
                this.smtpAuthenticator is not null &&
                (this.isTls || this.options.AllowPlaintextAuth))
            {
                await this.WriteLineAsync("250-AUTH PLAIN LOGIN", ct).ConfigureAwait(false);
            }
            await this.WriteLineAsync("250 HELP", ct).ConfigureAwait(false);
        }
        else
        {
            await this.WriteLineAsync($"250 {this.options.AdvertisedHostName} Hello {this.clientHostName} [{this.remoteAddress}]", ct).ConfigureAwait(false);
        }
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleStarttlsAsync(System.Threading.CancellationToken ct)
    {
        if (this.options.TlsCertificate is null)
        {
            await this.WriteLineAsync("502 STARTTLS not supported", ct).ConfigureAwait(false);
            return true;
        }
        if (this.isTls)
        {
            await this.WriteLineAsync("503 STARTTLS already active", ct).ConfigureAwait(false);
            return true;
        }

        // Per RFC 3207, we send "220 Ready to start TLS" first, then immediately
        // upgrade the stream. After upgrade the client must re-issue EHLO and the
        // session state resets (clientHostName, envelope, etc.).
        await this.WriteLineAsync("220 Ready to start TLS", ct).ConfigureAwait(false);

        var ssl = new System.Net.Security.SslStream(this.stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                this.options.TlsCertificate,
                clientCertificateRequired: false,
                enabledSslProtocols: System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                checkCertificateRevocation: false).ConfigureAwait(false);
        }
        catch (System.Exception)
        {
            // RFC 3207 says we MUST close the connection on TLS handshake failure -
            // not return to plain mode (which would let a MITM strip TLS).
            try { ssl.Dispose(); } catch (System.Exception) { /* swallow */ }
            return false;
        }

        this.stream = ssl;
        this.isTls = true;
        this.clientHostName = string.Empty;
        this.envelopeFrom = string.Empty;
        this.envelopeTo.Clear();
        this.state = State.AwaitingHelo;
        return true;
    }

    /// <summary>
    /// Handle AUTH PLAIN and AUTH LOGIN per RFC 4954. AUTH is only valid
    /// after EHLO on submission listeners with an authenticator configured.
    /// Strict TLS-before-AUTH unless options.AllowPlaintextAuth.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> HandleAuthAsync(string args, System.Threading.CancellationToken ct)
    {
        // Only submission listeners offer AUTH.
        if (this.options.Role != SmtpServerRole.Submission || this.smtpAuthenticator is null)
        {
            await this.WriteLineAsync("502 AUTH not available on this listener", ct).ConfigureAwait(false);
            return true;
        }
        // Need an established session (EHLO done).
        if (this.state == State.AwaitingGreeting || this.state == State.AwaitingHelo)
        {
            await this.WriteLineAsync("503 Send EHLO first", ct).ConfigureAwait(false);
            return true;
        }
        if (this.authenticatedUser is not null)
        {
            await this.WriteLineAsync("503 Already authenticated", ct).ConfigureAwait(false);
            return true;
        }
        // Refuse AUTH on insecure channel unless explicit opt-in.
        if (!this.isTls && !this.options.AllowPlaintextAuth)
        {
            await this.WriteLineAsync("538 5.7.11 Encryption required for requested authentication mechanism", ct).ConfigureAwait(false);
            return true;
        }
        // In an in-progress transaction, refuse new AUTH.
        if (!string.IsNullOrEmpty(this.envelopeFrom) || this.envelopeTo.Count > 0)
        {
            await this.WriteLineAsync("503 AUTH not permitted during mail transaction", ct).ConfigureAwait(false);
            return true;
        }

        string trimmed = args.Trim();
        int sp = trimmed.IndexOf(' ', System.StringComparison.Ordinal);
        string mech = sp < 0 ? trimmed : trimmed.Substring(0, sp);
        string initial = sp < 0 ? string.Empty : trimmed.Substring(sp + 1).Trim();
        mech = mech.ToUpperInvariant();

        switch (mech)
        {
            case "PLAIN":
                return await this.HandleAuthPlainAsync(initial, ct).ConfigureAwait(false);
            case "LOGIN":
                return await this.HandleAuthLoginAsync(initial, ct).ConfigureAwait(false);
            default:
                await this.WriteLineAsync("504 5.5.4 Unrecognized authentication mechanism", ct).ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>
    /// AUTH PLAIN flow per RFC 4616: the credentials are base64-encoded
    /// "[authzid] NUL authcid NUL password". If no initial response is
    /// provided, prompt with "334" and read on the next line.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> HandleAuthPlainAsync(string initial, System.Threading.CancellationToken ct)
    {
        string b64 = initial;
        if (b64.Length == 0)
        {
            await this.WriteLineAsync("334 ", ct).ConfigureAwait(false);
            string? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return false; // connection lost
            }
            if (line == "*")
            {
                await this.WriteLineAsync("501 5.7.0 Authentication cancelled", ct).ConfigureAwait(false);
                return true;
            }
            b64 = line.Trim();
        }

        byte[] decoded;
        try
        {
            decoded = System.Convert.FromBase64String(b64);
        }
        catch (System.FormatException)
        {
            await this.WriteLineAsync("501 5.5.2 Malformed AUTH PLAIN response", ct).ConfigureAwait(false);
            return true;
        }

        // Split on NUL: [authzid] NUL authcid NUL password
        var parts = new System.Collections.Generic.List<string>();
        int start = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            if (decoded[i] == 0)
            {
                parts.Add(System.Text.Encoding.UTF8.GetString(decoded, start, i - start));
                start = i + 1;
            }
        }
        parts.Add(System.Text.Encoding.UTF8.GetString(decoded, start, decoded.Length - start));

        if (parts.Count != 3)
        {
            await this.WriteLineAsync("501 5.5.2 AUTH PLAIN requires exactly 3 NUL-separated fields", ct).ConfigureAwait(false);
            return true;
        }

        string username = parts[1];
        string password = parts[2];
        return await this.CompleteAuthAsync(username, password, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// AUTH LOGIN: a Microsoft-originated mechanism widely deployed. The
    /// server prompts "Username:" (base64), client sends base64(username),
    /// server prompts "Password:" (base64), client sends base64(password).
    /// The initial response (if any) is the base64-encoded username.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> HandleAuthLoginAsync(string initial, System.Threading.CancellationToken ct)
    {
        // Prompt for username if not provided as initial response.
        // "VXNlcm5hbWU6" is base64("Username:").
        string b64Username = initial;
        if (b64Username.Length == 0)
        {
            await this.WriteLineAsync("334 VXNlcm5hbWU6", ct).ConfigureAwait(false);
            string? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return false;
            if (line == "*")
            {
                await this.WriteLineAsync("501 5.7.0 Authentication cancelled", ct).ConfigureAwait(false);
                return true;
            }
            b64Username = line.Trim();
        }

        string username;
        try
        {
            username = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(b64Username));
        }
        catch (System.FormatException)
        {
            await this.WriteLineAsync("501 5.5.2 Malformed AUTH LOGIN username", ct).ConfigureAwait(false);
            return true;
        }

        // Prompt for password. "UGFzc3dvcmQ6" is base64("Password:").
        await this.WriteLineAsync("334 UGFzc3dvcmQ6", ct).ConfigureAwait(false);
        string? passLine = await this.ReadLineAsync(ct).ConfigureAwait(false);
        if (passLine is null) return false;
        if (passLine == "*")
        {
            await this.WriteLineAsync("501 5.7.0 Authentication cancelled", ct).ConfigureAwait(false);
            return true;
        }

        string password;
        try
        {
            password = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(passLine.Trim()));
        }
        catch (System.FormatException)
        {
            await this.WriteLineAsync("501 5.5.2 Malformed AUTH LOGIN password", ct).ConfigureAwait(false);
            return true;
        }

        return await this.CompleteAuthAsync(username, password, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Common path for both AUTH PLAIN and AUTH LOGIN: hand credentials to
    /// the authenticator and reply 235 (success) or 535 (failure). On
    /// success the user is bound to this session for subsequent MAIL FROM
    /// authorization.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> CompleteAuthAsync(string username, string password, System.Threading.CancellationToken ct)
    {
        AuthenticatedUser? user = null;
        try
        {
            user = await this.smtpAuthenticator!.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Authenticator failure is "auth failed" - don't leak details.
        catch (System.Exception)
        {
            user = null;
        }
#pragma warning restore CA1031

        if (user is null)
        {
            await this.WriteLineAsync("535 5.7.8 Authentication credentials invalid", ct).ConfigureAwait(false);
            return true;
        }

        this.authenticatedUser = user;
        await this.WriteLineAsync("235 2.7.0 Authentication successful", ct).ConfigureAwait(false);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleMailAsync(string args, System.Threading.CancellationToken ct)
    {
        if (this.state == State.AwaitingHelo)
        {
            await this.WriteLineAsync("503 Bad sequence of commands, send HELO/EHLO first", ct).ConfigureAwait(false);
            return true;
        }
        if (this.options.RequireTlsForMail && this.options.TlsCertificate is not null && !this.isTls)
        {
            await this.WriteLineAsync("530 Must issue a STARTTLS command first", ct).ConfigureAwait(false);
            return true;
        }
        // Submission listener requires the client to have authenticated.
        if (this.options.Role == SmtpServerRole.Submission && this.authenticatedUser is null)
        {
            await this.WriteLineAsync("530 5.7.0 Authentication required", ct).ConfigureAwait(false);
            return true;
        }

        string? addr = ParseAddressArg(args, "FROM:");
        if (addr is null)
        {
            await this.WriteLineAsync("501 Syntax: MAIL FROM:<address>", ct).ConfigureAwait(false);
            return true;
        }

        // On submission, the authenticated user can only send "as" domains
        // they're authorized for. Empty allow-list means admin authority
        // (any domain). Bounce mail (empty MAIL FROM) is allowed.
        if (this.options.Role == SmtpServerRole.Submission && this.authenticatedUser is not null && addr.Length > 0)
        {
            if (this.authenticatedUser.AllowedFromDomains.Count > 0)
            {
                string fromDomain = ExtractDomain(addr);
                bool allowed = false;
                foreach (string d in this.authenticatedUser.AllowedFromDomains)
                {
                    if (string.Equals(fromDomain, d, System.StringComparison.OrdinalIgnoreCase))
                    {
                        allowed = true;
                        break;
                    }
                }
                if (!allowed)
                {
                    await this.WriteLineAsync($"550 5.7.1 Not authorized to send as {fromDomain}", ct).ConfigureAwait(false);
                    return true;
                }
            }
        }

        this.envelopeFrom = addr;
        this.envelopeTo.Clear();
        this.state = State.HasMail;
        await this.WriteLineAsync("250 OK", ct).ConfigureAwait(false);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleRcptAsync(string args, System.Threading.CancellationToken ct)
    {
        if (this.state != State.HasMail && this.state != State.HasRcpt)
        {
            await this.WriteLineAsync("503 Bad sequence of commands, send MAIL FROM first", ct).ConfigureAwait(false);
            return true;
        }

        string? addr = ParseAddressArg(args, "TO:");
        if (addr is null)
        {
            await this.WriteLineAsync("501 Syntax: RCPT TO:<address>", ct).ConfigureAwait(false);
            return true;
        }

        if (this.envelopeTo.Count >= this.options.MaxRecipients)
        {
            await this.WriteLineAsync("452 Too many recipients", ct).ConfigureAwait(false);
            return true;
        }

        // On MTA listener, refuse relay: the destination domain must be local.
        // If no localDomains resolver is configured, accept all (legacy
        // behavior, safe only on closed networks).
        if (this.options.Role == SmtpServerRole.Mta && this.authenticatedUser is null && this.localDomains is not null)
        {
            string rcptDomain = ExtractDomain(addr);
            bool isLocal = false;
            try
            {
                isLocal = await this.localDomains.IsLocalAsync(rcptDomain, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (System.Exception)
            {
                isLocal = false;
            }
#pragma warning restore CA1031
            if (!isLocal)
            {
                await this.WriteLineAsync("550 5.7.1 Relaying denied", ct).ConfigureAwait(false);
                return true;
            }
        }

        this.envelopeTo.Add(addr);
        this.state = State.HasRcpt;
        await this.WriteLineAsync("250 OK", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Extract the domain portion from a "user@domain" address. Returns
    /// empty if not a valid local@domain form.
    /// </summary>
    private static string ExtractDomain(string addr)
    {
        if (string.IsNullOrEmpty(addr)) return string.Empty;
        int at = addr.LastIndexOf('@');
        return at < 0 ? string.Empty : addr.Substring(at + 1).Trim().ToLowerInvariant();
    }

    private async System.Threading.Tasks.Task<bool> HandleDataAsync(System.Threading.CancellationToken ct)
    {
        if (this.state != State.HasRcpt)
        {
            await this.WriteLineAsync("503 Bad sequence of commands, send RCPT TO first", ct).ConfigureAwait(false);
            return true;
        }

        await this.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>", ct).ConfigureAwait(false);

        byte[]? body = await this.ReadDataBodyAsync(ct).ConfigureAwait(false);
        if (body is null)
        {
            await this.WriteLineAsync("552 Message size exceeds maximum permitted", ct).ConfigureAwait(false);
            this.ResetTransaction();
            return true;
        }

        // Inbound authentication (SPF/DKIM/DMARC). Runs only if an
        // authenticator was supplied. Failures here MUST NOT crash the
        // session; the authenticator returns TempError/PermError verdicts.
        InboundAuthResult? authResult = null;
        byte[] bodyToDeliver = body;
        if (this.authenticator is not null)
        {
            try
            {
                authResult = await this.authenticator.AuthenticateAsync(
                    this.remoteAddress, this.envelopeFrom, body, ct).ConfigureAwait(false);

                // Reject before sink dispatch when enforcement is on and DMARC said reject.
                if (this.enforceReject && authResult.ShouldReject)
                {
                    string why = string.IsNullOrEmpty(authResult.RejectReason)
                        ? "Message failed DMARC policy (p=reject)"
                        : authResult.RejectReason;
                    await this.WriteLineAsync($"550 {why}", ct).ConfigureAwait(false);
                    this.ResetTransaction();
                    return true;
                }

                // Prepend Authentication-Results header to the bytes the sink sees.
                if (!string.IsNullOrEmpty(authResult.HeaderValue))
                {
                    string hdrLine = "Authentication-Results: " + authResult.HeaderValue + "\r\n";
                    byte[] hdrBytes = System.Text.Encoding.UTF8.GetBytes(hdrLine);
                    byte[] combined = new byte[hdrBytes.Length + body.Length];
                    System.Buffer.BlockCopy(hdrBytes, 0, combined, 0, hdrBytes.Length);
                    System.Buffer.BlockCopy(body, 0, combined, hdrBytes.Length, body.Length);
                    bodyToDeliver = combined;
                }
            }
#pragma warning disable CA1031 // Authenticator failures must not crash the session.
            catch (System.Exception)
            {
                // Treat any authenticator failure as auth-not-performed.
                authResult = null;
                bodyToDeliver = body;
            }
#pragma warning restore CA1031
        }

        var deliveryCtx = new DeliveryContext
        {
            EnvelopeFrom = this.envelopeFrom,
            EnvelopeTo = this.envelopeTo.ToArray(),
            RawBytes = bodyToDeliver,
            RemoteAddress = this.remoteAddress,
            ClientHostName = this.clientHostName,
            AuthResults = authResult?.Detail,
        };

        DeliveryResult result;
        try
        {
            result = await this.sink.DeliverAsync(deliveryCtx, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Catch broad exception is acceptable at the SMTP boundary - we don't want a sink bug to crash the server.
        catch (System.Exception ex)
        {
            result = new DeliveryResult
            {
                Outcome = DeliveryOutcome.TransientFailure,
                ReplyText = $"Local error while processing message: {ex.GetType().Name}",
            };
        }
#pragma warning restore CA1031

        string code = result.Outcome switch
        {
            DeliveryOutcome.Accepted => "250",
            DeliveryOutcome.TransientFailure => "451",
            DeliveryOutcome.PermanentFailure => "550",
            _ => "451",
        };
        await this.WriteLineAsync($"{code} {result.ReplyText}", ct).ConfigureAwait(false);
        this.ResetTransaction();
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleRsetAsync(System.Threading.CancellationToken ct)
    {
        this.ResetTransaction();
        await this.WriteLineAsync("250 OK", ct).ConfigureAwait(false);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleNoopAsync(System.Threading.CancellationToken ct)
    {
        await this.WriteLineAsync("250 OK", ct).ConfigureAwait(false);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> HandleQuitAsync(System.Threading.CancellationToken ct)
    {
        await this.WriteLineAsync($"221 {this.options.AdvertisedHostName} closing connection", ct).ConfigureAwait(false);
        return false;
    }

    private async System.Threading.Tasks.Task<bool> HandleHelpAsync(System.Threading.CancellationToken ct)
    {
        await this.WriteLineAsync("214-Supported commands:", ct).ConfigureAwait(false);
        await this.WriteLineAsync("214-  EHLO, HELO, MAIL, RCPT, DATA, RSET, NOOP, QUIT, VRFY, HELP", ct).ConfigureAwait(false);
        await this.WriteLineAsync("214 End of HELP", ct).ConfigureAwait(false);
        return true;
    }

    private void ResetTransaction()
    {
        this.envelopeFrom = string.Empty;
        this.envelopeTo.Clear();
        this.state = string.IsNullOrEmpty(this.clientHostName) ? State.AwaitingHelo : State.Ready;
    }

    private async System.Threading.Tasks.Task<byte[]?> ReadDataBodyAsync(System.Threading.CancellationToken ct)
    {
        // Read until terminator <CRLF>.<CRLF>. Unstuff dots (leading "." doubled
        // by sender per RFC 5321 section 4.5.2). Enforce size limit.
        using var ms = new MemoryStream();
        var lineBuf = new MemoryStream();

        while (true)
        {
            int b = await this.ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return null; // Connection dropped mid-DATA.
            }

            if (b == '\r')
            {
                int next = await this.ReadByteAsync(ct).ConfigureAwait(false);
                if (next == '\n')
                {
                    byte[] line = lineBuf.ToArray();
                    lineBuf.SetLength(0);

                    if (line.Length == 1 && line[0] == (byte)'.')
                    {
                        // End-of-data marker.
                        return ms.ToArray();
                    }

                    // Dot-unstuffing: strip a leading "." if present.
                    int offset = (line.Length > 0 && line[0] == (byte)'.') ? 1 : 0;
                    ms.Write(line, offset, line.Length - offset);
                    ms.WriteByte((byte)'\r');
                    ms.WriteByte((byte)'\n');

                    if (ms.Length > this.options.MaxMessageBytes)
                    {
                        return null;
                    }
                    continue;
                }

                // Lone CR - keep in buffer (defensive, shouldn't really happen).
                lineBuf.WriteByte((byte)'\r');
                if (next >= 0)
                {
                    lineBuf.WriteByte((byte)next);
                }
            }
            else
            {
                lineBuf.WriteByte((byte)b);
            }
        }
    }

    private async System.Threading.Tasks.Task<int> ReadByteAsync(System.Threading.CancellationToken ct)
    {
        byte[] buf = new byte[1];
        int n = await this.stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
        return n <= 0 ? -1 : buf[0];
    }

    private async System.Threading.Tasks.Task<string?> ReadLineAsync(System.Threading.CancellationToken ct)
    {
        using var buf = new MemoryStream(64);
        while (true)
        {
            int b = await this.ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return buf.Length == 0 ? null : Encoding.ASCII.GetString(buf.ToArray());
            }
            if (b == '\n')
            {
                byte[] bytes = buf.ToArray();
                int len = bytes.Length;
                if (len > 0 && bytes[len - 1] == (byte)'\r')
                {
                    len--;
                }
                return Encoding.ASCII.GetString(bytes, 0, len);
            }
            if (buf.Length >= MaxLineLength)
            {
                return Encoding.ASCII.GetString(buf.ToArray());
            }
            buf.WriteByte((byte)b);
        }
    }

    private async System.Threading.Tasks.Task WriteLineAsync(string line, System.Threading.CancellationToken ct)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await this.stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await this.stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static (string Verb, string Args) SplitCommand(string line)
    {
        int sp = line.IndexOf(' ', System.StringComparison.Ordinal);
        if (sp < 0)
        {
            return (line, string.Empty);
        }
        return (line.Substring(0, sp), line.Substring(sp + 1));
    }

    private static string? ParseAddressArg(string args, string keyword)
    {
        // Expected forms: "FROM:<addr>" or "TO:<addr>" possibly with whitespace.
        string trimmed = args.TrimStart();
        if (!trimmed.StartsWith(keyword, System.StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = trimmed.Substring(keyword.Length).TrimStart();

        int lt = rest.IndexOf('<', System.StringComparison.Ordinal);
        int gt = rest.IndexOf('>', System.StringComparison.Ordinal);
        if (lt < 0 || gt < lt)
        {
            return null;
        }
        return rest.Substring(lt + 1, gt - lt - 1);
    }

    private enum State
    {
        AwaitingGreeting = 0,
        AwaitingHelo = 1,
        Ready = 2,
        HasMail = 3,
        HasRcpt = 4,
    }
}
