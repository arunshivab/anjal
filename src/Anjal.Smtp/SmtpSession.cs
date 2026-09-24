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
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCertificate;
    private readonly IMessageSink sink;
    private readonly IInboundAuthenticator? authenticator;
    private readonly bool enforceReject;
    private readonly ISmtpAuthenticator? smtpAuthenticator;
    private readonly ILocalDomainResolver? localDomains;
    private readonly string remoteAddress;
    private bool isTls;

    // Buffered input. One 16 KB window instead of an allocation and a
    // stream read per byte; over TLS each of those reads was a full trip
    // through the decryption path.
    private readonly byte[] inBuf = new byte[16 * 1024];
    private int inStart;
    private int inEnd;
    private int pushedBack = -1;
    private bool lastLineTooLong;
    private int authFailures;

    // DATA-mode history of the raw bytes, for spotting a lone "." line that
    // is delimited by anything other than CRLF.
    private bool inData;
    private int h1;
    private int h2;
    private int h3;
    private bool pendingDotCr;
    private bool bareDotSeen;

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
        this.tlsCertificate = options.CurrentTlsCertificate();
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
        // Every read and write in the session runs under this token, so the
        // whole conversation - however active - ends at MaxSessionDuration.
        using var lifetime = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(this.options.MaxSessionDuration);
        System.Threading.CancellationToken outer = ct;
        ct = lifetime.Token;
        try
        {
            if (this.options.ImplicitTls && !await this.BeginImplicitTlsAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            PolicyDecision connect = await this.ConsultAsync(p => p.OnConnectAsync(this.remoteAddress, ct)).ConfigureAwait(false);
            if (!connect.Allowed)
            {
                await this.WriteLineAsync($"{connect.ReplyCode} {connect.ReplyText}", ct).ConfigureAwait(false);
                return;
            }

            Counters.Increment(this.options.Role == SmtpServerRole.Submission ? "anjal_smtp_submission_connections_total" : "anjal_smtp_mta_connections_total");
            await this.WriteLineAsync($"220 {this.options.AdvertisedHostName} Anjal SMTP service ready", ct).ConfigureAwait(false);
            this.state = State.AwaitingHelo;

            while (!ct.IsCancellationRequested)
            {
                string? line = await this.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (this.lastLineTooLong)
                {
                    // The rest of the line was read and discarded, so the
                    // next command starts cleanly rather than mid-line.
                    await this.WriteLineAsync("500 5.5.2 Line too long", ct).ConfigureAwait(false);
                    continue;
                }

                bool keep = await this.HandleCommandAsync(line, ct).ConfigureAwait(false);
                if (!keep)
                {
                    break;
                }
            }
        }
        catch (IdleTimeoutException)
        {
            await this.TryWriteFinalAsync("421 4.4.2 Idle timeout, closing connection").ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // The session lifetime ran out rather than the server stopping.
            await this.TryWriteFinalAsync("421 4.4.2 Session time limit reached, closing connection").ConfigureAwait(false);
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
            if (this.tlsCertificate is not null && !this.isTls)
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
        if (this.tlsCertificate is null)
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
        // Anything the client sent after STARTTLS in the same packet arrived in
        // plaintext. Keeping it would let those bytes be read back as though
        // they came through TLS - a man in the middle could append commands
        // (CVE-2011-0411). They are discarded, never interpreted.
        this.inStart = this.inEnd = 0;
        this.pushedBack = -1;

        await this.WriteLineAsync("220 Ready to start TLS", ct).ConfigureAwait(false);

        var ssl = new System.Net.Security.SslStream(this.stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                this.tlsCertificate,
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
        // An address that has failed too often recently is refused before the
        // password is checked, so guessing costs the attacker time and costs
        // this server nothing.
        if (this.options.AuthFailures is AuthFailureLimiter limiter && !limiter.IsAllowed(this.remoteAddress))
        {
            Counters.Increment("anjal_smtp_auth_throttled_total");
            await this.WriteLineAsync("454 4.7.0 Too many failed attempts, try again later", ct).ConfigureAwait(false);
            return false;
        }

        AuthenticatedUser? user = null;
        bool authenticatorFailed = false;
        try
        {
            user = await this.smtpAuthenticator!.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Authenticator failure is reported as a temporary error, not "bad password".
        catch (System.Exception)
        {
            authenticatorFailed = true;
        }
#pragma warning restore CA1031

        if (authenticatorFailed)
        {
            // A dead database is not a wrong password: say so with a 4xx so
            // the client retries, and do not count it against the address.
            Counters.Increment("anjal_smtp_auth_errors_total");
            await this.WriteLineAsync("454 4.7.0 Temporary authentication failure", ct).ConfigureAwait(false);
            return true;
        }

        if (user is null)
        {
            Counters.Increment("anjal_smtp_auth_failures_total");
            this.options.AuthFailures?.RecordFailure(this.remoteAddress);
            if (++this.authFailures >= this.options.MaxAuthFailuresPerSession)
            {
                await this.WriteLineAsync("421 4.7.0 Too many authentication failures, closing connection", ct).ConfigureAwait(false);
                return false;
            }
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
        if (this.options.RequireTlsForMail && this.tlsCertificate is not null && !this.isTls)
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

        // RFC 1870: a client that declares SIZE= larger than the limit is
        // refused here, before a byte of the body is sent.
        long declared = DeclaredSize(args);
        if (declared > this.options.MaxMessageBytes)
        {
            await this.WriteLineAsync($"552 5.3.4 Message size exceeds fixed maximum of {this.options.MaxMessageBytes} bytes", ct).ConfigureAwait(false);
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

        PolicyDecision mailPolicy = await this.ConsultAsync(p => p.OnMailFromAsync(this.remoteAddress, this.authenticatedUser?.Username, addr, ct)).ConfigureAwait(false);
        if (!mailPolicy.Allowed)
        {
            await this.WriteLineAsync($"{mailPolicy.ReplyCode} {mailPolicy.ReplyText}", ct).ConfigureAwait(false);
            return true;
        }

        this.envelopeFrom = addr;
        this.envelopeTo.Clear();
        this.state = State.HasMail;
        await this.WriteLineAsync("250 OK", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Ask the configured policy. A missing policy, or one that throws,
    /// allows the command - a policy bug must never take the server down.
    /// </summary>
    private async System.Threading.Tasks.Task<PolicyDecision> ConsultAsync(System.Func<ISmtpPolicy, System.Threading.Tasks.Task<PolicyDecision>> check)
    {
        ISmtpPolicy? policy = this.options.Policy;
        if (policy is null)
        {
            return PolicyDecision.Allow;
        }
        try
        {
            return await check(policy).ConfigureAwait(false) ?? PolicyDecision.Allow;
        }
#pragma warning disable CA1031 // Policy failures are fail-open by design.
        catch (System.Exception)
        {
            return PolicyDecision.Allow;
        }
#pragma warning restore CA1031
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
            bool isLocal;
            try
            {
                isLocal = await this.localDomains.IsLocalAsync(rcptDomain, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // A failed lookup means "cannot tell right now", never "not ours".
            catch (System.Exception ex)
            {
                // The store is unreachable, so this server cannot know whether
                // the recipient is local. A permanent 550 here would make the
                // sending server return the message to its sender - mail lost
                // during any database hiccup (DEF-003). A 451 makes it retry.
                Counters.Increment("anjal_smtp_lookup_deferrals_total");
                this.options.Log?.Invoke($"Local-domain lookup for {rcptDomain} failed ({ex.GetType().Name}); deferring.");
                await this.WriteLineAsync("451 4.3.0 Temporary server error, try again later", ct).ConfigureAwait(false);
                return true;
            }
#pragma warning restore CA1031
            if (!isLocal)
            {
                await this.WriteLineAsync("550 5.7.1 Relaying denied", ct).ConfigureAwait(false);
                return true;
            }

            // The domain is ours: refuse now if the address certainly does not
            // exist, rather than after the whole message has been transferred
            // (DEF-042). A megabyte of attachments addressed to a typo is
            // refused in one line, and the sender is told at once. Anything
            // uncertain - including a store that cannot answer - is accepted
            // here and decided at delivery, so an outage never bounces mail.
            if (this.options.Recipients is not null)
            {
                bool? exists;
                try
                {
                    exists = await this.options.Recipients.ExistsAsync(addr, ct).ConfigureAwait(false);
                }
                catch (System.OperationCanceledException)
                {
                    throw;
                }
#pragma warning disable CA1031 // Cannot tell: accept, and decide at delivery.
                catch (System.Exception ex)
                {
                    this.options.Log?.Invoke($"Recipient check for {addr} failed ({ex.GetType().Name}); accepting and deciding at delivery.");
                    exists = null;
                }
#pragma warning restore CA1031
                if (exists == false)
                {
                    Counters.Increment("anjal_smtp_unknown_recipient_total");
                    await this.WriteLineAsync("550 5.1.1 No such mailbox here", ct).ConfigureAwait(false);
                    return true;
                }
            }
        }

        PolicyDecision rcptPolicy = await this.ConsultAsync(p => p.OnRcptToAsync(this.remoteAddress, this.authenticatedUser?.Username, this.envelopeFrom, addr, ct)).ConfigureAwait(false);
        if (!rcptPolicy.Allowed)
        {
            await this.WriteLineAsync($"{rcptPolicy.ReplyCode} {rcptPolicy.ReplyText}", ct).ConfigureAwait(false);
            return true;
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

        DataResult data = await this.ReadDataBodyAsync(ct).ConfigureAwait(false);
        if (data.TooLarge)
        {
            // The whole oversized body was read and discarded, so the
            // session is still in step with the client.
            await this.WriteLineAsync("552 5.3.4 Message size exceeds maximum permitted", ct).ConfigureAwait(false);
            this.ResetTransaction();
            return true;
        }
        if (data.IsBareDot)
        {
            // The client and this server may now disagree about where the
            // message ended, so nothing further on this connection can be
            // trusted: refuse and close.
            Counters.Increment("anjal_smtp_bare_dot_rejected_total");
            await this.WriteLineAsync("554 5.5.2 Line endings around \".\" must be CRLF; message rejected", ct).ConfigureAwait(false);
            return false;
        }
        if (data.Body is null)
        {
            return false;
        }
        byte[] body = data.Body;

        // Inbound authentication (SPF/DKIM/DMARC). Runs only if an
        // authenticator was supplied. Failures here MUST NOT crash the
        // session; the authenticator returns TempError/PermError verdicts.
        InboundAuthResult? authResult = null;
        // A sender's Authentication-Results claiming to be from this server
        // is removed (RFC 8601 5); only the one added below may bear our name
        // (DEF-038). DKIM is verified on the message as it arrived.
        byte[] cleaned = AuthResultsHeader.RemoveClaimsBy(body, this.options.AdvertisedHostName);
        byte[] bodyToDeliver = cleaned;
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
                    byte[] combined = new byte[hdrBytes.Length + cleaned.Length];
                    System.Buffer.BlockCopy(hdrBytes, 0, combined, 0, hdrBytes.Length);
                    System.Buffer.BlockCopy(cleaned, 0, combined, hdrBytes.Length, cleaned.Length);
                    bodyToDeliver = combined;
                }
            }
#pragma warning disable CA1031 // Authenticator failures must not crash the session.
            catch (System.Exception ex)
            {
                // Treat any authenticator failure as auth-not-performed, but
                // record it: a silent failure here would hide a broken DNS
                // resolver behind every message scoring as unauthenticated.
                Counters.Increment("anjal_inbound_auth_errors_total");
                this.options.Log?.Invoke($"Inbound authentication failed for mail from {this.remoteAddress}: {ex.GetType().Name}: {ex.Message}");
                authResult = null;
                bodyToDeliver = cleaned;
            }
#pragma warning restore CA1031
        }

        // RFC 5321 section 4.4: every server that handles a message adds a
        // trace line. It records where the message came from and when, which
        // is what an abuse report or a delivery investigation starts from,
        // and it lets loops be detected.
        bodyToDeliver = PrependHeader(bodyToDeliver, this.ReceivedHeader());

        var deliveryCtx = new DeliveryContext
        {
            EnvelopeFrom = this.envelopeFrom,
            EnvelopeTo = this.envelopeTo.ToArray(),
            RawBytes = bodyToDeliver,
            RemoteAddress = this.remoteAddress,
            ClientHostName = this.clientHostName,
            AuthenticatedUser = this.authenticatedUser?.Username,
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
        Counters.Increment(result.Outcome switch
        {
            DeliveryOutcome.Accepted => "anjal_smtp_messages_accepted_total",
            DeliveryOutcome.TransientFailure => "anjal_smtp_messages_deferred_total",
            _ => "anjal_smtp_messages_rejected_total",
        });
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

    /// <summary>
    /// Read a DATA body up to the end-of-data marker.
    /// <para>
    /// Only <c>CRLF.CRLF</c> ends the body. A dot line ended by a bare LF or a
    /// bare CR does not, which is what keeps Anjal from being a receiver that
    /// SMTP smuggling (CVE-2023-51764 class) can split. Once the body is
    /// complete, any bare CR or LF left inside it is rewritten as CRLF, so the
    /// stored message - and anything later relayed from it - carries no
    /// ambiguous line endings for a lenient downstream server to misread.
    /// </para>
    /// <para>
    /// When the body passes <see cref="SmtpServerOptions.MaxMessageBytes"/>
    /// the rest is read and discarded up to the marker, so the remainder is
    /// never parsed as commands; the caller then replies 552. A body that
    /// runs past twice the limit without ending is treated as abuse and the
    /// connection is dropped.
    /// </para>
    /// </summary>
    private async System.Threading.Tasks.Task<DataResult> ReadDataBodyAsync(System.Threading.CancellationToken ct)
    {
        // The body starts just after the CRLF that ended the DATA command.
        this.inData = true;
        this.h3 = '\r';
        this.h2 = '\n';
        this.h1 = -1;
        this.pendingDotCr = false;
        this.bareDotSeen = false;
        try
        {
            return await this.ReadDataBodyCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            this.inData = false;
        }
    }

    private async System.Threading.Tasks.Task<DataResult> ReadDataBodyCoreAsync(System.Threading.CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var lineBuf = new MemoryStream(256);
        long seen = 0;
        long ceiling = (long)this.options.MaxMessageBytes * 2 + 64 * 1024;
        bool tooBig = false;

        while (true)
        {
            int b = await this.ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return this.bareDotSeen ? DataResult.BareDot : DataResult.Dropped;
            }
            if (++seen > ceiling)
            {
                return DataResult.Dropped;
            }

            if (b == '\r')
            {
                int next = await this.ReadByteAsync(ct).ConfigureAwait(false);
                if (next < 0 && this.bareDotSeen)
                {
                    return DataResult.BareDot;
                }
                if (next == '\n')
                {
                    // A real line end.
                    if (lineBuf.Length == 1 && lineBuf.GetBuffer()[0] == (byte)'.')
                    {
                        return tooBig ? DataResult.Oversized : DataResult.Of(NormaliseLineEndings(ms.ToArray()));
                    }
                    if (!tooBig)
                    {
                        // Dot-unstuffing: strip one leading "." (RFC 5321 section 4.5.2).
                        byte[] line = lineBuf.GetBuffer();
                        int length = (int)lineBuf.Length;
                        int offset = length > 0 && line[0] == (byte)'.' ? 1 : 0;
                        ms.Write(line, offset, length - offset);
                        ms.WriteByte((byte)'\r');
                        ms.WriteByte((byte)'\n');
                        if (ms.Length > this.options.MaxMessageBytes)
                        {
                            tooBig = true;
                            ms.SetLength(0);
                        }
                    }
                    lineBuf.SetLength(0);
                    continue;
                }

                // A CR not followed by LF stays in the line as data. The byte
                // after it is pushed back and examined afresh, so "CR CR LF"
                // still ends the line at the second CR.
                lineBuf.WriteByte((byte)'\r');
                if (next >= 0)
                {
                    this.PushBack(next);
                }
                continue;
            }

            if (!tooBig)
            {
                lineBuf.WriteByte((byte)b);
                if (lineBuf.Length > this.options.MaxMessageBytes)
                {
                    tooBig = true;
                    ms.SetLength(0);
                    lineBuf.SetLength(0);
                }
            }
            else if (b != '.' || lineBuf.Length > 1)
            {
                // Past the limit, keep only enough of each line to recognise
                // the terminator.
                lineBuf.SetLength(0);
                lineBuf.WriteByte(0);
            }
            else
            {
                lineBuf.WriteByte((byte)b);
            }
        }
    }

    /// <summary>
    /// Rewrite every bare CR and bare LF as CRLF, leaving existing CRLF pairs
    /// alone. Applied to a completed DATA body, after the terminator has
    /// already been found strictly, so it cannot change where the message
    /// ended.
    /// </summary>
    /// <param name="data">Message bytes.</param>
    public static byte[] NormaliseLineEndings(byte[] data)
    {
        System.ArgumentNullException.ThrowIfNull(data);
        bool clean = true;
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            if ((c == (byte)'\r' && (i + 1 >= data.Length || data[i + 1] != (byte)'\n')) ||
                (c == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r')))
            {
                clean = false;
                break;
            }
        }
        if (clean)
        {
            return data;
        }

        using var ms = new MemoryStream(data.Length + 64);
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            if (c == (byte)'\r')
            {
                ms.WriteByte((byte)'\r');
                ms.WriteByte((byte)'\n');
                if (i + 1 < data.Length && data[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else if (c == (byte)'\n')
            {
                ms.WriteByte((byte)'\r');
                ms.WriteByte((byte)'\n');
            }
            else
            {
                ms.WriteByte(c);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Next byte from the buffered input, refilling it as needed. Each refill
    /// waits at most <see cref="SmtpServerOptions.CommandTimeout"/>; a client
    /// that sends nothing for that long ends the session with 421, which is
    /// what stops a slowloris from holding connections open indefinitely.
    /// </summary>
    private async System.Threading.Tasks.Task<int> ReadByteAsync(System.Threading.CancellationToken ct)
    {
        if (this.pushedBack >= 0)
        {
            // Already seen once: not inspected again.
            int b = this.pushedBack;
            this.pushedBack = -1;
            return b;
        }
        if (this.inStart < this.inEnd)
        {
            return this.Inspect(this.inBuf[this.inStart++]);
        }

        using var idle = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(this.options.CommandTimeout);
        int n;
        try
        {
            n = await this.stream.ReadAsync(this.inBuf.AsMemory(0, this.inBuf.Length), idle.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IdleTimeoutException();
        }
        if (n <= 0)
        {
            return -1;
        }
        this.inStart = 1;
        this.inEnd = n;
        return this.Inspect(this.inBuf[0]);
    }

    /// <summary>
    /// During DATA, watch the raw bytes for a single "." standing on a line
    /// of its own where either line break is not CRLF: LF.LF, CR.CR, LF.CRLF
    /// and the like. A compliant client dot-stuffs every such line, so this
    /// only ever comes from an attempt to smuggle a second message past a
    /// lenient server, or from a client that would otherwise hang waiting
    /// for a terminator Anjal will never accept. Either way the message is
    /// refused. The real terminator, CRLF.CRLF, passes.
    /// </summary>
    private int Inspect(int b)
    {
        if (!this.inData || b < 0)
        {
            return b;
        }
        bool eol = b == '\r' || b == '\n';
        if (this.pendingDotCr)
        {
            // Seen CRLF "." CR - the terminator only if LF comes next.
            this.pendingDotCr = false;
            if (b != '\n')
            {
                this.bareDotSeen = true;
                return -1;
            }
        }
        else if (eol && this.h1 == '.' && (this.h2 == '\r' || this.h2 == '\n'))
        {
            if (b == '\r' && this.h2 == '\n' && this.h3 == '\r')
            {
                this.pendingDotCr = true;
            }
            else
            {
                this.bareDotSeen = true;
                return -1;
            }
        }
        this.h3 = this.h2;
        this.h2 = this.h1;
        this.h1 = b;
        return b;
    }

    private void PushBack(int b) => this.pushedBack = b;

    /// <summary>
    /// Read one command line. A line longer than the RFC 5321 limit is read
    /// to its end and discarded, and <see cref="lastLineTooLong"/> is set so
    /// the caller replies 500 - the overflow is never mistaken for the next
    /// command.
    /// </summary>
    private async System.Threading.Tasks.Task<string?> ReadLineAsync(System.Threading.CancellationToken ct)
    {
        this.lastLineTooLong = false;
        using var buf = new MemoryStream(64);
        while (true)
        {
            int b = await this.ReadByteAsync(ct).ConfigureAwait(false);
            if (b < 0)
            {
                return buf.Length == 0 && !this.lastLineTooLong ? null : Encoding.ASCII.GetString(buf.ToArray());
            }
            if (b == '\n')
            {
                if (this.lastLineTooLong)
                {
                    return string.Empty;
                }
                byte[] bytes = buf.ToArray();
                int len = bytes.Length;
                if (len > 0 && bytes[len - 1] == (byte)'\r')
                {
                    len--;
                }
                return Encoding.ASCII.GetString(bytes, 0, len);
            }
            if (this.lastLineTooLong)
            {
                continue; // discarding the rest of an overlong line
            }
            if (buf.Length >= MaxLineLength)
            {
                this.lastLineTooLong = true;
                buf.SetLength(0);
                continue;
            }
            buf.WriteByte((byte)b);
        }
    }

    /// <summary>
    /// The trace header for this message:
    /// <c>Received: from helo (ip) by host with ESMTPS id x; date</c>.
    /// The protocol word follows RFC 3848: ESMTP, plus S when TLS was in
    /// use and A when the client authenticated.
    /// </summary>
    private string ReceivedHeader()
    {
        string protocol = "ESMTP" + (this.isTls ? "S" : string.Empty) + (this.authenticatedUser is not null ? "A" : string.Empty);
        string helo = SanitiseTraceToken(this.clientHostName.Length > 0 ? this.clientHostName : "unknown");
        string id = System.Guid.NewGuid().ToString("N").Substring(0, 16);
        string date = System.DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture);
        return $"Received: from {helo} ([{this.remoteAddress}])\r\n\tby {this.options.AdvertisedHostName} with {protocol} id {id};\r\n\t{date}\r\n";
    }

    /// <summary>
    /// The client's HELO name is untrusted text going into a header; keep
    /// only characters that can appear in a host name or address literal.
    /// </summary>
    private static string SanitiseTraceToken(string value)
    {
        var sb = new StringBuilder(System.Math.Min(value.Length, 255));
        foreach (char c in value)
        {
            if (sb.Length >= 255)
            {
                break;
            }
            if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ':' || c == '[' || c == ']')
            {
                sb.Append(c);
            }
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    private static byte[] PrependHeader(byte[] message, string headerLines)
    {
        byte[] head = Encoding.ASCII.GetBytes(headerLines);
        byte[] combined = new byte[head.Length + message.Length];
        System.Buffer.BlockCopy(head, 0, combined, 0, head.Length);
        System.Buffer.BlockCopy(message, 0, combined, head.Length, message.Length);
        return combined;
    }

    /// <summary>
    /// Wrap the connection in TLS before anything is said (port 465). The
    /// handshake itself is bounded by the idle timeout, so a client that
    /// connects and never speaks TLS cannot hold the slot.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> BeginImplicitTlsAsync(System.Threading.CancellationToken ct)
    {
        if (this.tlsCertificate is null)
        {
            // No certificate yet (a fresh install before ACME finishes): an
            // implicit-TLS port has nothing it can safely say in plaintext.
            return false;
        }
        var ssl = new System.Net.Security.SslStream(this.stream, leaveInnerStreamOpen: false);
        using var handshake = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(this.options.CommandTimeout);
        try
        {
            await ssl.AuthenticateAsServerAsync(
                new System.Net.Security.SslServerAuthenticationOptions
                {
                    ServerCertificate = this.tlsCertificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                },
                handshake.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any handshake failure closes the connection.
        catch (System.Exception)
        {
            try { await ssl.DisposeAsync().ConfigureAwait(false); } catch (System.Exception) { /* closing */ }
            return false;
        }
#pragma warning restore CA1031
        this.stream = ssl;
        this.isTls = true;
        return true;
    }

    /// <summary>The SIZE= value on a MAIL FROM line, or 0 when absent or unreadable.</summary>
    private static long DeclaredSize(string args)
    {
        foreach (string part in args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("SIZE=", System.StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(part.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long size))
            {
                return size;
            }
        }
        return 0;
    }

    /// <summary>Best-effort final reply on a session that is closing anyway.</summary>
    private async System.Threading.Tasks.Task TryWriteFinalAsync(string line)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5));
            await this.WriteLineAsync(line, cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The connection is being closed; a failed goodbye changes nothing.
        catch (System.Exception)
        {
        }
#pragma warning restore CA1031
    }

    /// <summary>A read waited longer than the idle timeout.</summary>
    private sealed class IdleTimeoutException : System.Exception
    {
    }

    /// <summary>The outcome of reading a DATA body.</summary>
    private readonly struct DataResult
    {
        private DataResult(byte[]? body, bool tooLarge, bool bareDot = false)
        {
            this.Body = body;
            this.TooLarge = tooLarge;
            this.IsBareDot = bareDot;
        }

        public bool IsBareDot { get; }

        /// <summary>The body could not be read: the connection must close.</summary>
        public static DataResult Dropped => new(null, false);

        /// <summary>The body was read in full but exceeded the size limit.</summary>
        public static DataResult Oversized => new(null, true);

        /// <summary>A lone "." line with non-CRLF line breaks: refused, and the session closed.</summary>
        public static DataResult BareDot => new(null, false, bareDot: true);

        public byte[]? Body { get; }

        public bool TooLarge { get; }

        public static DataResult Of(byte[] body) => new(body, false);
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
