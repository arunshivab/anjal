namespace Anjal.Server;

/// <summary>
/// Composition root for the Anjal mail server host process. Wires
/// Store + Routing + Smtp + Mime + Api together and runs until cancellation.
///
/// Environment variables:
///   ANJAL_BIND              - bind address, default "127.0.0.1"
///   ANJAL_PORT              - SMTP receiver port, default 2525
///   ANJAL_HOSTNAME          - hostname for SMTP banner, default "anjal.localhost"
///   ANJAL_POSTGRES          - PostgreSQL connection string. If unset, an
///                             in-memory store is used (suitable for demos).
///   ANJAL_OUTBOUND_MODE     - "direct" (MX-based, requires port 25 outbound),
///                             "relay" (single upstream, requires ANJAL_RELAY_HOST),
///                             or "none". Default "none".
///   ANJAL_RELAY_HOST        - upstream host for relay mode.
///   ANJAL_RELAY_PORT        - upstream port for relay mode, default 587.
///   ANJAL_API_PORT          - HTTP API port. If unset or 0, the API is disabled.
///   ANJAL_API_TOKEN         - bearer token clients must present (24 characters or more).
///   ANJAL_API_TOKEN_PREVIOUS - optional: the token being replaced, accepted
///                             until callers have moved to the new one (rotation).
///   ANJAL_API_BIND          - HTTP API bind address. Defaults to 127.0.0.1 (never ANJAL_BIND).
///   ANJAL_API_ALLOW_NO_AUTH - "true" allows the API to run without ANJAL_API_TOKEN (development only).
///   ANJAL_API_ALLOW_PUBLIC  - "true" allows a non-loopback ANJAL_API_BIND (only behind a TLS proxy).
///   ANJAL_KEK               - base64 of 32 random bytes; seals DKIM private keys at rest (AES-256-GCM).
///   ANJAL_ALLOW_PLAINTEXT_KEYS - "true" lets the API store DKIM keys without ANJAL_KEK (development only).
///   ANJAL_WEBHOOK_ALLOW_HTTP    - "true" allows plain-http webhook URLs (default https only).
///   ANJAL_WEBHOOK_ALLOW_PRIVATE - "true" allows webhooks to loopback/private addresses (default public only).
///   ANJAL_SUBMISSION_TLS_PORT         - Implicit-TLS submission port, normally 465 (RFC 8314). 0 disables.
///   ANJAL_SMTP_IDLE_TIMEOUT_SECONDS   - Close a session idle this long (default 120).
///   ANJAL_SMTP_MAX_SESSION_MINUTES    - Hard limit on one session (default 15).
///   ANJAL_SMTP_MAX_CONNECTIONS        - Concurrent sessions per listener (default 200).
///   ANJAL_SMTP_MAX_CONNECTIONS_PER_IP - Concurrent sessions per client address (default 10).
///   ANJAL_SMTP_AUTH_FAILURES_PER_IP   - Failed AUTH per address per 15 min on 587 (default 10).
///   ANJAL_TLS_CERT_PATH     - path to fullchain.pem (with private key, or use ANJAL_TLS_KEY_PATH).
///   ANJAL_TLS_KEY_PATH      - path to privkey.pem if not embedded in fullchain.
///   ANJAL_TLS_REQUIRE       - if "true", server refuses MAIL FROM until STARTTLS. Default false.
///   ANJAL_TLS_VALIDATE_PEER - if "false", outbound TLS skips cert validation (testing only). Default true.
///                             Validation applies to domains whose TLS policy is "required" and to relays;
///                             opportunistic TLS encrypts without validating (RFC 7435).
///   ANJAL_TLS_REVOCATION    - "online" (default) or "nocheck": revocation checking when validating.
///   ANJAL_TLS_DEFAULT_MODE  - default outbound TLS mode if no per-domain policy:
///                             "opportunistic" (default), "required", or "disabled".
///   ANJAL_MAILDIR_ROOT      - root directory for tenant Maildirs. Default
///                             /var/mail/anjal (Unix) or %LOCALAPPDATA%\Anjal\mail (Windows).
///   ANJAL_SPAM_ACTION       - "junk" (default: score, mark, file in Junk) or "reject"
///                             (refuse with 550 at or above ANJAL_SPAM_REJECT_THRESHOLD).
///   ANJAL_SPAM_REJECT_THRESHOLD - score for reject mode, default 5.
///   ANJAL_SPAM_DNS          - "false" disables the DNS-based scoring rules. Default true.
///   ANJAL_RATE_CONN_PER_MIN - connections per IP per minute, default 60 (0 disables).
///   ANJAL_RATE_MSG_PER_HOUR - unauthenticated messages per IP per hour, default 200.
///   ANJAL_RATE_USER_MSG_PER_HOUR - messages per authenticated user per hour, default 100.
///   ANJAL_SMTP_LATE_RECIPIENT_CHECK - "true" accepts unknown local recipients at
///                             RCPT TO and refuses them after DATA instead. Default false.
///   ANJAL_GREYLIST          - "false" disables greylisting on the MTA port. Default true.
///   ANJAL_GREYLIST_DELAY_SECONDS - greylist delay, default 300.
///   ANJAL_ACME_*            - see <see cref="Anjal.Acme.AcmeEnvironment"/>. When
///                             ANJAL_ACME_DOMAINS is set and ANJAL_TLS_CERT_PATH is not,
///                             STARTTLS uses the ACME certificate and picks up renewals
///                             without a restart. ANJAL_ACME_HOST=true makes this process
///                             run renewal itself (for deployments without the webmail),
///                             serving HTTP-01 on ANJAL_ACME_HTTP_PORT (80).
///
/// Command line:
///   --acme-renew-now        - ask the renewal service (in whichever process hosts it)
///                             to renew at its next check, then exit.
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static async System.Threading.Tasks.Task<int> Main(string[] args)
    {
        if (args is not null && System.Array.IndexOf(args, "--acme-renew-now") >= 0)
        {
            Anjal.Acme.AcmeEnvironment acmeEnv = Anjal.Acme.AcmeEnvironment.Read(hostByDefault: false);
            new Anjal.Acme.CertificateStore(acmeEnv.Directory).RequestRenewal();
            System.Console.WriteLine($"Renewal requested; marker written to {acmeEnv.Directory}. The hosting process will act on it within a minute.");
            return 0;
        }

        string bind = System.Environment.GetEnvironmentVariable("ANJAL_BIND") ?? "127.0.0.1";
        string portStr = System.Environment.GetEnvironmentVariable("ANJAL_PORT") ?? "2525";
        string hostname = System.Environment.GetEnvironmentVariable("ANJAL_HOSTNAME") ?? "anjal.localhost";
        string? pg = System.Environment.GetEnvironmentVariable("ANJAL_POSTGRES");
        string outboundMode = (System.Environment.GetEnvironmentVariable("ANJAL_OUTBOUND_MODE") ?? "none").ToLowerInvariant();

        if (!int.TryParse(portStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int port))
        {
            System.Console.Error.WriteLine($"Invalid ANJAL_PORT: {portStr}");
            return 1;
        }

        // DKIM private keys are sealed at rest under ANJAL_KEK. A malformed
        // KEK stops startup; a missing one is allowed (keys then stay in
        // plaintext) but new keys are refused by the API unless explicitly
        // permitted - see ANJAL_ALLOW_PLAINTEXT_KEYS.
        Anjal.Store.SecretProtector? secrets;
        try
        {
            secrets = Anjal.Store.SecretProtector.FromEnvironment();
        }
        catch (System.InvalidOperationException ex)
        {
            System.Console.Error.WriteLine(ex.Message);
            return 1;
        }
        if (secrets is null && pg is not null)
        {
            Log("WARNING: ANJAL_KEK is not set - DKIM private keys are stored unencrypted. Generate one with: openssl rand -base64 32");
        }

        Anjal.Store.IMessageStore store = pg is null
            ? new Anjal.Store.InMemoryMessageStore()
            : new Anjal.Store.PostgresMessageStore(pg) { Secrets = secrets };

        var routing = new Anjal.Routing.StoreBackedRoutingTable(store);
        // Webhooks go only where the target policy allows: https to public
        // addresses by default, checked on the address actually dialled, and
        // never following a redirect.
        Anjal.Routing.WebhookTargetPolicy webhookPolicy = Anjal.Routing.WebhookTargetPolicy.FromEnvironment();
        using var http = webhookPolicy.CreateClient(System.TimeSpan.FromSeconds(15));
        var dispatcher = new Anjal.Routing.HttpWebhookDispatcher(http);

        void Log(string line) => System.Console.WriteLine($"[{System.DateTime.UtcNow:HH:mm:ss}] {line}");

        // Mailbox storage (v0.9.0): Maildir on disk + index in the store.
        // Both built-in stores implement IMailboxStore, so this is always on.
        var mailboxStore = (Anjal.Store.IMailboxStore)store;
        string maildirRoot = System.Environment.GetEnvironmentVariable("ANJAL_MAILDIR_ROOT") ?? Anjal.Mailbox.MaildirStore.DefaultRoot;
        var maildir = new Anjal.Mailbox.MaildirStore(maildirRoot, hostname);
        var mailboxSink = new Anjal.Mailbox.MailboxSink(mailboxStore, maildir, Log);

        // Fan out: mailbox sink first, then webhook routing. An address may
        // be a mailbox, a webhook target, or both.
        // Webhook notifications are queued durably and delivered by this
        // worker; the sink wakes it the moment a job is stored.
        using var webhookWorker = new WebhookWorker(store, dispatcher, Log);
        var routingSink = new RoutingMessageSink(store, routing, dispatcher, Log, webhookWorker.Wake);
        var fanOut = new Anjal.Smtp.CompositeMessageSink(mailboxSink, routingSink);

        // Anti-spam (v0.11.0): score unauthenticated mail before fan-out.
        // Verdict headers ride along; the mailbox sink files Junk.
        Anjal.Spam.SpamFilterSink sink = BuildSpamFilter(fanOut, Log);
        Anjal.Spam.CompositeSmtpPolicy mtaPolicy = BuildMtaPolicy(mailboxStore, Log);
        Anjal.Spam.RateLimiter submissionPolicy = BuildSubmissionPolicy();

        // TLS cert for SMTP receiver: a static file (ANJAL_TLS_CERT_PATH) or,
        // when ACME is configured, the live ACME certificate with hot reload.
        System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCert = LoadServerCert(Log);
        bool requireTls = string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_TLS_REQUIRE"), "true", System.StringComparison.OrdinalIgnoreCase);
        Anjal.Acme.AcmeEnvironment acme = Anjal.Acme.AcmeEnvironment.Read(hostByDefault: false);
        Anjal.Acme.CertificateWatcher? certWatcher = null;
        if (tlsCert is null && acme.Configured)
        {
            certWatcher = new Anjal.Acme.CertificateWatcher(new Anjal.Acme.CertificateStore(acme.Directory));
            certWatcher.Reloaded += c => Log($"TLS: certificate reloaded (expires {c.NotAfter.ToUniversalTime():yyyy-MM-dd}).");
            tlsCert = certWatcher.Current;
            Log(tlsCert is null
                ? $"TLS: ACME configured for {string.Join(", ", acme.Domains)} but no certificate in {acme.Directory} yet; STARTTLS off until one is issued."
                : $"TLS: using ACME certificate from {acme.Directory}.");
        }
        System.Func<System.Security.Cryptography.X509Certificates.X509Certificate2?>? certSource = certWatcher is null ? null : () => certWatcher.Current;

        var smtpOptions = new Anjal.Smtp.SmtpServerOptions
        {
            BindAddress = System.Net.IPAddress.Parse(bind),
            Port = port,
            AdvertisedHostName = hostname,
            TlsCertificate = tlsCert,
            TlsCertificateSource = certSource,
            RequireTlsForMail = requireTls && (tlsCert is not null || certWatcher is not null),
            Role = Anjal.Smtp.SmtpServerRole.Mta,
            Policy = mtaPolicy,
            CommandTimeout = System.TimeSpan.FromSeconds(ParseIntEnv("ANJAL_SMTP_IDLE_TIMEOUT_SECONDS", 120)),
            MaxSessionDuration = System.TimeSpan.FromMinutes(ParseIntEnv("ANJAL_SMTP_MAX_SESSION_MINUTES", 15)),
            MaxConcurrentSessions = ParseIntEnv("ANJAL_SMTP_MAX_CONNECTIONS", 200),
            MaxSessionsPerAddress = ParseIntEnv("ANJAL_SMTP_MAX_CONNECTIONS_PER_IP", 10),
            Log = Log,
            // Refuse an unknown recipient at RCPT TO rather than after the
            // message has been transferred (DEF-042). Set
            // ANJAL_SMTP_LATE_RECIPIENT_CHECK=true to go back to deciding at
            // delivery, which tells a stranger less about which addresses exist.
            Recipients = string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_SMTP_LATE_RECIPIENT_CHECK"), "true", System.StringComparison.OrdinalIgnoreCase)
                ? null
                : new ServerRecipientResolver(mailboxSink, routing),
        };
        Log($"SMTP limits: idle {smtpOptions.CommandTimeout.TotalSeconds:0}s, session {smtpOptions.MaxSessionDuration.TotalMinutes:0} min, " +
            $"{smtpOptions.MaxConcurrentSessions} connections ({smtpOptions.MaxSessionsPerAddress} per address).");

        using var cts = new System.Threading.CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log("Shutdown requested.");
        };

        // Inbound auth (SPF + DKIM + DMARC). Configurable via ANJAL_INBOUND_AUTH_ENFORCE:
        //   "dmarc-reject" -> enforce DMARC p=reject as SMTP 550 refusal (default)
        //   "none"         -> annotate only (always 250)
        (Anjal.Smtp.IInboundAuthenticator? inboundAuth, bool enforceReject) = BuildInboundAuth(hostname, Log);

        // Local-domain resolver: env-var ANJAL_LOCAL_DOMAINS plus store-backed table.
        // When set, the MTA port refuses RCPT TO for non-local domains
        // (open-relay guard). When empty, MTA accepts all RCPTs (legacy).
        Anjal.Smtp.ILocalDomainResolver? localDomains = BuildLocalDomainResolver(store, Log);

        using var server = new Anjal.Smtp.SmtpServer(smtpOptions, sink,
            inboundAuth, enforceReject, smtpAuthenticator: null, localDomains: localDomains);
        Log($"Anjal SMTP (MTA, port {port}) listening on {bind} as {hostname}");
        Log(pg is null ? "Using in-memory store." : "Using PostgreSQL store.");
        Log($"Maildir root: {maildir.Root}");
        if (tlsCert is not null)
        {
            Log($"STARTTLS enabled (cert subject: {tlsCert.Subject}, expires {tlsCert.NotAfter:yyyy-MM-dd}).");
            if (requireTls) Log("Receiver requires TLS before MAIL FROM.");
        }
        else
        {
            Log("STARTTLS disabled (set ANJAL_TLS_CERT_PATH to enable).");
        }

        // Optional submission listener (typically port 587). Requires
        // SMTP authentication and the server-supplied SmtpAuthenticator.
        // Disabled when ANJAL_SUBMISSION_PORT is unset or 0.
        Anjal.Smtp.SmtpServer? submissionServer = null;
        Anjal.Smtp.SmtpServer? implicitTlsServer = null;
        int submissionPort = ParsePortOrZero(System.Environment.GetEnvironmentVariable("ANJAL_SUBMISSION_PORT"));
        if (submissionPort > 0)
        {
            Anjal.Smtp.ISmtpAuthenticator submissionAuth = BuildSmtpAuthenticator(store, Log);
            bool allowPlaintextAuth = string.Equals(
                System.Environment.GetEnvironmentVariable("ANJAL_AUTH_ALLOW_PLAINTEXT"),
                "true", System.StringComparison.OrdinalIgnoreCase);

            var submissionOptions = new Anjal.Smtp.SmtpServerOptions
            {
                BindAddress = System.Net.IPAddress.Parse(bind),
                Port = submissionPort,
                AdvertisedHostName = hostname,
                TlsCertificate = tlsCert,
                TlsCertificateSource = certSource,
                RequireTlsForMail = false, // submission has its own TLS-before-AUTH logic
                Role = Anjal.Smtp.SmtpServerRole.Submission,
                AllowPlaintextAuth = allowPlaintextAuth,
                Policy = submissionPolicy,
                CommandTimeout = smtpOptions.CommandTimeout,
                MaxSessionDuration = smtpOptions.MaxSessionDuration,
                MaxConcurrentSessions = smtpOptions.MaxConcurrentSessions,
                MaxSessionsPerAddress = smtpOptions.MaxSessionsPerAddress,
                MaxAuthFailuresPerSession = 3,
                Log = Log,

                // Across sessions: ten failed logins from one address in 15
                // minutes and that address is refused before any password
                // is checked.
                AuthFailures = new Anjal.Smtp.AuthFailureLimiter(
                    ParseIntEnv("ANJAL_SMTP_AUTH_FAILURES_PER_IP", 10),
                    System.TimeSpan.FromMinutes(15)),
            };

            // Authenticated submission reaches outside addresses through the
            // outbound queue and files a Sent copy (DEF-002); local recipients
            // go through the same sink as inbound mail.
            var submissionSink = new SubmissionSink(sink, localDomains, store, mailboxSink, Log);
            submissionServer = new Anjal.Smtp.SmtpServer(submissionOptions, submissionSink,
                authenticator: null, enforceReject: false,
                smtpAuthenticator: submissionAuth, localDomains: null);

            // Port 465, implicit TLS (RFC 8314): same authentication, limits
            // and policy as 587, but TLS from the first byte.
            int implicitPort = ParsePortOrZero(System.Environment.GetEnvironmentVariable("ANJAL_SUBMISSION_TLS_PORT"));
            if (implicitPort > 0)
            {
                var implicitOptions = new Anjal.Smtp.SmtpServerOptions
                {
                    BindAddress = submissionOptions.BindAddress,
                    Port = implicitPort,
                    AdvertisedHostName = hostname,
                    TlsCertificate = tlsCert,
                    TlsCertificateSource = certSource,
                    RequireTlsForMail = false,
                    Role = Anjal.Smtp.SmtpServerRole.Submission,
                    AllowPlaintextAuth = false,
                    ImplicitTls = true,
                    Policy = submissionPolicy,
                    CommandTimeout = submissionOptions.CommandTimeout,
                    MaxSessionDuration = submissionOptions.MaxSessionDuration,
                    MaxConcurrentSessions = submissionOptions.MaxConcurrentSessions,
                    MaxSessionsPerAddress = submissionOptions.MaxSessionsPerAddress,
                    MaxAuthFailuresPerSession = submissionOptions.MaxAuthFailuresPerSession,
                    AuthFailures = submissionOptions.AuthFailures,
                    Log = Log,
                };
                implicitTlsServer = new Anjal.Smtp.SmtpServer(implicitOptions, submissionSink,
                    authenticator: null, enforceReject: false,
                    smtpAuthenticator: submissionAuth, localDomains: null);
                Log($"Anjal SMTP (Submission, implicit TLS, port {implicitPort}) listening on {bind}");
            }

            Log($"Anjal SMTP (Submission, port {submissionPort}) listening on {bind} as {hostname}");
            if (allowPlaintextAuth)
            {
                Log("WARNING: ANJAL_AUTH_ALLOW_PLAINTEXT=true - AUTH accepted on plaintext channels.");
            }
            else if (tlsCert is null)
            {
                Log("WARNING: submission port has no TLS cert and plaintext AUTH is disabled - AUTH will fail.");
            }
        }

        // Outbound side with TLS policy lookup against the store.
        System.Threading.Tasks.Task? workerTask = null;
        Anjal.Smtp.TlsClientOptions tlsClient = BuildTlsClientOptions(store);
        Anjal.Smtp.IMailSender? mailSender = ConfigureSender(outboundMode, hostname, tlsClient, Log);
        if (mailSender is not null)
        {
            // DKIM signing: env-var default key (if configured) chained with store-backed per-domain lookup.
            (Anjal.Dkim.IDkimKeyResolver? dkimResolver, bool requireDkim) = BuildDkimResolver(store, Log);
            var dkimSigner = new Anjal.Dkim.DkimSigner();

            var worker = new OutboundWorker(
                store,
                mailSender,
                new OutboundWorkerOptions
                {
                    // A message that fails for good gets a bounce notice in
                    // the sender's own INBOX; nothing is sent outbound.
                    OnFinalFailure = async (message, reason, permanent, token) =>
                    {
                        if (message.EnvelopeFrom.Length == 0)
                        {
                            return; // never bounce a bounce
                        }
                        byte[] notice = BounceNotice.Build(hostname, message, reason, permanent, System.DateTimeOffset.UtcNow);
                        await mailboxSink.DeliverAsync(new Anjal.Smtp.DeliveryContext
                        {
                            EnvelopeFrom = string.Empty,
                            EnvelopeTo = new[] { message.EnvelopeFrom },
                            RawBytes = notice,
                            RemoteAddress = "127.0.0.1",
                            ClientHostName = hostname,
                            AuthenticatedUser = "anjal-bounce",
                        }, token).ConfigureAwait(false);
                    },
                },
                log: Log,
                dkimResolver: dkimResolver,
                dkimSigner: dkimSigner,
                requireDkim: requireDkim);
            workerTask = worker.RunAsync(cts.Token);
            string dkimNote = dkimResolver is not null
                ? (requireDkim ? "DKIM=required" : "DKIM=opportunistic")
                : "DKIM=disabled";
            Log($"Outbound worker started (mode={outboundMode}, tls.default={tlsClient.DefaultMode}, {dkimNote}).");
        }
        else
        {
            Log("Outbound disabled (set ANJAL_OUTBOUND_MODE=direct or relay to enable).");
        }

        // HTTP API side.
        System.Threading.Tasks.Task? apiTask = null;
        Anjal.Api.ApiServer? apiServer = null;
        string apiPortStr = System.Environment.GetEnvironmentVariable("ANJAL_API_PORT") ?? string.Empty;
        if (!string.IsNullOrEmpty(apiPortStr) &&
            int.TryParse(apiPortStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int apiPort) &&
            apiPort > 0)
        {
            // The admin API manages every tenant, mailbox and DKIM key. It
            // listens on loopback unless told otherwise explicitly - never
            // inherited from ANJAL_BIND, which is 0.0.0.0 for SMTP.
            string apiBind = System.Environment.GetEnvironmentVariable("ANJAL_API_BIND") ?? "127.0.0.1";

            // The API speaks plain HTTP by design and is reached through an SSH
            // tunnel. Binding it to anything but loopback would put the bearer
            // token on the wire in clear, so it needs a deliberate opt-in.
            bool publicApi = !System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(apiBind));
            if (publicApi && !string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_API_ALLOW_PUBLIC"), "true", System.StringComparison.OrdinalIgnoreCase))
            {
                Log($"ANJAL_API_BIND={apiBind} is not a loopback address; refusing to start. The admin API is plain HTTP: " +
                    "reach it over SSH (ssh -L 8025:127.0.0.1:8025), or set ANJAL_API_ALLOW_PUBLIC=true behind a TLS proxy.");
                return 1;
            }
            string token = System.Environment.GetEnvironmentVariable("ANJAL_API_TOKEN") ?? string.Empty;
            string previousToken = System.Environment.GetEnvironmentVariable("ANJAL_API_TOKEN_PREVIOUS") ?? string.Empty;

            // Fail closed: an API with no token would hand tenant, mailbox and
            // DKIM management to anyone who can reach the port. Running
            // without one needs an explicit, deliberately awkward opt-in.
            bool allowNoAuth = string.Equals(
                System.Environment.GetEnvironmentVariable("ANJAL_API_ALLOW_NO_AUTH"), "true", System.StringComparison.OrdinalIgnoreCase);
            if (token.Length == 0 && !allowNoAuth)
            {
                Log("ANJAL_API_TOKEN is not set; refusing to start. Set a token, or ANJAL_API_ALLOW_NO_AUTH=true for local development only.");
                return 1;
            }

            // A short or obvious token is as good as none: this API can create
            // mailboxes, read every tenant and hold DKIM keys (SEC-R3). The
            // token itself is never logged, here or anywhere.
            if (token.Length > 0 && token.Length < Anjal.Api.ApiOptions.MinimumTokenLength)
            {
                Log($"ANJAL_API_TOKEN is too short ({token.Length} characters); refusing to start. Use at least {Anjal.Api.ApiOptions.MinimumTokenLength} random characters, e.g. openssl rand -base64 32.");
                return 1;
            }
            if (token.Length > 0 && IsObviousToken(token))
            {
                Log("ANJAL_API_TOKEN looks like a placeholder; refusing to start. Use a random value, e.g. openssl rand -base64 32.");
                return 1;
            }
            if (previousToken.Length > 0)
            {
                Log("ANJAL_API_TOKEN_PREVIOUS is set: the old token is still accepted. Remove it once every caller uses the new one.");
            }
            if (token.Length == 0)
            {
                Log("WARNING: the API is running WITHOUT authentication (ANJAL_API_ALLOW_NO_AUTH=true).");
            }

            apiServer = new Anjal.Api.ApiServer(new Anjal.Api.ApiOptions
            {
                BindAddress = System.Net.IPAddress.Parse(apiBind),
                Port = apiPort,
                BearerToken = token,
                AcmeDirectory = acme.Configured ? acme.Directory : null,
                MaildirRoot = maildir.Root,
                PreviousBearerToken = previousToken,
                Log = Log,
            }, store, Log, mailboxStore, maildir);
            Anjal.Smtp.Counters.RegisterGauge("anjal_outbound_pending", "Outbound messages waiting to be sent.", () => store.CountOutboundAsync(Anjal.Store.OutboundStatus.Pending).GetAwaiter().GetResult());
            Anjal.Smtp.Counters.RegisterGauge("anjal_outbound_sending", "Outbound messages currently leased by the worker.", () => store.CountOutboundAsync(Anjal.Store.OutboundStatus.Sending).GetAwaiter().GetResult());
            Anjal.Smtp.Counters.RegisterGauge("anjal_webhook_pending", "Webhook notifications waiting to be delivered.", () => store.CountWebhookJobsAsync(Anjal.Store.WebhookJobStatus.Pending).GetAwaiter().GetResult());
            Anjal.Smtp.Counters.Describe("anjal_smtp_messages_accepted_total", "Messages accepted at DATA (250).");
            Anjal.Smtp.Counters.Describe("anjal_smtp_messages_deferred_total", "Messages deferred at DATA (451).");
            Anjal.Smtp.Counters.Describe("anjal_smtp_messages_rejected_total", "Messages rejected at DATA (550).");
            Anjal.Smtp.Counters.Describe("anjal_mailbox_delivered_total", "Messages filed in a mailbox INBOX.");
            Anjal.Smtp.Counters.Describe("anjal_mailbox_junked_total", "Messages filed in a mailbox Junk folder.");
            Anjal.Smtp.Counters.Describe("anjal_quota_refusals_total", "RCPT commands refused with 452 because the mailbox is full.");
            Anjal.Smtp.Counters.Describe("anjal_tenant_disabled_deferrals_total", "RCPT commands deferred with 450 because the tenant is disabled.");
            Anjal.Smtp.Counters.Describe("anjal_greylist_deferred_total", "RCPT commands deferred by greylisting.");
            Anjal.Smtp.Counters.Describe("anjal_spam_scored_total", "Unauthenticated deliveries scored by the spam filter.");
            Log($"Health at http://{apiBind}:{apiPort}/healthz (no auth), metrics at /metrics (bearer token).");
            apiTask = apiServer.StartAsync(cts.Token);
            string authNote = string.IsNullOrEmpty(token) ? " (auth disabled - DO NOT use in production)" : string.Empty;
            Log($"API listening on http://{apiBind}:{apiPort}/api/...{authNote}");
        }
        else
        {
            Log("API disabled (set ANJAL_API_PORT to enable).");
        }

        // ACME renewal hosted here (webmail-less deployments).
        System.Threading.Tasks.Task? acmeTask = null;
        Anjal.Acme.Http01Listener? http01 = null;
        if (acme.Host)
        {
            var acmeStore = new Anjal.Acme.CertificateStore(acme.Directory);
            var renewal = new Anjal.Acme.AcmeRenewalService(acme.Options, acmeStore, Log);
            http01 = new Anjal.Acme.Http01Listener(acme.HttpBind, acme.HttpPort, Log);
            System.Threading.Tasks.Task responderTask = http01.RunAsync(cts.Token);
            acmeTask = System.Threading.Tasks.Task.WhenAll(responderTask, renewal.RunAsync(cts.Token));
            Log($"ACME: this process hosts renewal; HTTP-01 responder on {acme.HttpBind}:{acme.HttpPort}.");
        }
        else if (acme.Configured)
        {
            Log("ACME: renewal is hosted by another process (set ANJAL_ACME_HOST=true to host it here).");
        }

        Log("Press Ctrl+C to stop.");

        try
        {
            var listeners = new System.Collections.Generic.List<System.Threading.Tasks.Task>
            {
                server.StartAsync(cts.Token),
                webhookWorker.RunAsync(cts.Token),
            };
            if (submissionServer is not null)
            {
                listeners.Add(submissionServer.StartAsync(cts.Token));
            }
            if (implicitTlsServer is not null)
            {
                listeners.Add(implicitTlsServer.StartAsync(cts.Token));
            }
            await System.Threading.Tasks.Task.WhenAll(listeners).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            submissionServer?.Dispose();
            implicitTlsServer?.Dispose();
        }

        if (workerTask is not null)
        {
            try { await workerTask.ConfigureAwait(false); }
            catch (System.OperationCanceledException) { /* expected */ }
        }
        if (apiTask is not null)
        {
            try { await apiTask.ConfigureAwait(false); }
            catch (System.OperationCanceledException) { /* expected */ }
            apiServer?.Dispose();
        }
        if (acmeTask is not null)
        {
            try { await acmeTask.ConfigureAwait(false); }
            catch (System.OperationCanceledException) { /* expected */ }
            http01?.Dispose();
        }
        certWatcher?.Dispose();
        return 0;
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2? LoadServerCert(System.Action<string> log)
    {
        string? certPath = System.Environment.GetEnvironmentVariable("ANJAL_TLS_CERT_PATH");
        if (string.IsNullOrEmpty(certPath))
        {
            return null;
        }
        if (!System.IO.File.Exists(certPath))
        {
            log($"ANJAL_TLS_CERT_PATH set to '{certPath}' but file does not exist; TLS disabled.");
            return null;
        }
        string? keyPath = System.Environment.GetEnvironmentVariable("ANJAL_TLS_KEY_PATH");
        try
        {
            if (!string.IsNullOrEmpty(keyPath) && System.IO.File.Exists(keyPath))
            {
                return System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
            }
            return System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath);
        }
        catch (System.Exception ex)
        {
            log($"Failed to load TLS cert: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Anjal.Smtp.TlsClientOptions BuildTlsClientOptions(Anjal.Store.IMessageStore store)
    {
        string defaultStr = (System.Environment.GetEnvironmentVariable("ANJAL_TLS_DEFAULT_MODE") ?? "opportunistic").ToLowerInvariant();
        Anjal.Store.TlsMode defaultMode = defaultStr switch
        {
            "required" => Anjal.Store.TlsMode.Required,
            "disabled" => Anjal.Store.TlsMode.Disabled,
            _ => Anjal.Store.TlsMode.Opportunistic,
        };

        bool validate = !string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_TLS_VALIDATE_PEER"), "false", System.StringComparison.OrdinalIgnoreCase);

        // ANJAL_TLS_REVOCATION: "online" (default) checks CRL/OCSP when a
        // certificate is validated, soft-failing only when status is
        // unobtainable; "nocheck" skips revocation.
        System.Security.Cryptography.X509Certificates.X509RevocationMode revocation =
            string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_TLS_REVOCATION"), "nocheck", System.StringComparison.OrdinalIgnoreCase)
                ? System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                : System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;

        return new Anjal.Smtp.TlsClientOptions
        {
            DefaultMode = defaultMode,
            ValidateCertificate = validate,
            Revocation = revocation,
            PolicyLookup = async (domain, ct) =>
            {
                Anjal.Store.OutboundTlsPolicy? p = await store.GetOutboundTlsPolicyAsync(domain, ct).ConfigureAwait(false);
                return p?.Mode;
            },
        };
    }

    /// <summary>
    /// Build a DKIM key resolver from env vars (single default key) chained
    /// with the store (per-domain overrides). Returns (resolver, requireDkim).
    /// If nothing is configured, resolver is null and DKIM is fully disabled.
    /// </summary>
    private static (Anjal.Dkim.IDkimKeyResolver?, bool) BuildDkimResolver(Anjal.Store.IMessageStore store, System.Action<string> log)
    {
        string mode = (System.Environment.GetEnvironmentVariable("ANJAL_DKIM_MODE") ?? "off").ToLowerInvariant();
        if (mode != "required" && mode != "opportunistic")
        {
            return (null, false);
        }
        bool requireDkim = mode == "required";

        var resolvers = new System.Collections.Generic.List<Anjal.Dkim.IDkimKeyResolver>();

        // Env-var key, if configured.
        Anjal.Dkim.DkimKey? envKey = TryLoadEnvDkimKey(log);
        if (envKey is not null)
        {
            resolvers.Add(new Anjal.Dkim.SingleKeyResolver(envKey));
            log($"DKIM: env-var key loaded (domain={envKey.Domain}, selector={envKey.Selector}).");
        }

        // Store-backed lookup (per-domain).
        resolvers.Add(new StoreBackedDkimResolver(store));
        log("DKIM: store-backed per-domain lookup enabled.");

        return (new Anjal.Dkim.ChainedKeyResolver(resolvers.ToArray()), requireDkim);
    }

    private static Anjal.Dkim.DkimKey? TryLoadEnvDkimKey(System.Action<string> log)
    {
        string? path = System.Environment.GetEnvironmentVariable("ANJAL_DKIM_KEY_PATH");
        string? domain = System.Environment.GetEnvironmentVariable("ANJAL_DKIM_DOMAIN");
        string? selector = System.Environment.GetEnvironmentVariable("ANJAL_DKIM_SELECTOR");

        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(selector))
        {
            return null;
        }
        if (!System.IO.File.Exists(path))
        {
            log($"ANJAL_DKIM_KEY_PATH '{path}' does not exist; env-var DKIM key skipped.");
            return null;
        }
        try
        {
            string pem = System.IO.File.ReadAllText(path);
            // Validate by import.
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(pem);
            return new Anjal.Dkim.DkimKey
            {
                Domain = domain,
                Selector = selector,
                PrivateKeyPem = pem,
            };
        }
        catch (System.Exception ex)
        {
            log($"Failed to load DKIM key from '{path}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parse an integer from an env-var. Returns 0 when null, empty, or
    /// unparseable - the caller treats 0 as "feature disabled".
    /// </summary>
    private static int ParsePortOrZero(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return int.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 0;
    }

    /// <summary>
    /// Build the SMTP submission authenticator. Combines an optional
    /// env-var single-user (<c>ANJAL_SUBMISSION_USER</c> +
    /// <c>ANJAL_SUBMISSION_PASSWORD</c> + <c>ANJAL_SUBMISSION_DOMAINS</c>)
    /// with the store-backed user table. The env-var password is hashed
    /// at startup via <see cref="Anjal.Smtp.Pbkdf2Hasher"/> so the plain
    /// password is never compared directly at runtime.
    /// </summary>
    private static ServerSmtpAuthenticator BuildSmtpAuthenticator(
        Anjal.Store.IMessageStore store,
        System.Action<string> log)
    {
        string envUser = System.Environment.GetEnvironmentVariable("ANJAL_SUBMISSION_USER") ?? string.Empty;
        string envPass = System.Environment.GetEnvironmentVariable("ANJAL_SUBMISSION_PASSWORD") ?? string.Empty;
        string envDomainsRaw = System.Environment.GetEnvironmentVariable("ANJAL_SUBMISSION_DOMAINS") ?? string.Empty;

        string envHash = string.Empty;
        System.Collections.Generic.IReadOnlyList<string> envDomains = System.Array.Empty<string>();
        if (envUser.Length > 0 && envPass.Length > 0)
        {
            envHash = Anjal.Smtp.Pbkdf2Hasher.Hash(envPass);
            if (envDomainsRaw.Length > 0)
            {
                var domains = new System.Collections.Generic.List<string>();
                foreach (string d in envDomainsRaw.Split(','))
                {
                    string trimmed = d.Trim().ToLowerInvariant();
                    if (trimmed.Length > 0) domains.Add(trimmed);
                }
                envDomains = domains;
            }
            log($"Submission auth: env-var user '{envUser}' configured" +
                (envDomains.Count > 0 ? $" with allowed domains [{string.Join(',', envDomains)}]" : " (admin authority)"));
        }
        else
        {
            log("Submission auth: env-var user not configured (store-backed only).");
        }

        return new ServerSmtpAuthenticator(envUser, envHash, envDomains, store, store as Anjal.Store.IMailboxStore);
    }

    /// <summary>
    /// Build the local-domain resolver. Returns null when neither env-var
    /// list nor store-backed table has any entries, signaling
    /// "accept-all" legacy behavior. Returns a real resolver when at
    /// least the env-var list is non-empty (we can't know if the store
    /// has rows without querying it, so we always wire the store when
    /// available - the resolver checks env first, store second).
    /// </summary>
    /// <summary>
    /// Wrap the delivery sink in the spam scorer. Reads ANJAL_SPAM_ACTION,
    /// ANJAL_SPAM_REJECT_THRESHOLD and ANJAL_SPAM_DNS.
    /// </summary>
    private static Anjal.Spam.SpamFilterSink BuildSpamFilter(Anjal.Smtp.IMessageSink inner, System.Action<string> log)
    {
        string action = (System.Environment.GetEnvironmentVariable("ANJAL_SPAM_ACTION") ?? "junk").Trim().ToLowerInvariant();
        bool useDns = !string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_SPAM_DNS"), "false", System.StringComparison.OrdinalIgnoreCase);
        int rejectThreshold = ParseIntEnv("ANJAL_SPAM_REJECT_THRESHOLD", Anjal.Store.TenantRow.DefaultSpamThreshold);

        Anjal.Spam.ISpamDnsLookup? dns = null;
        if (useDns)
        {
            Anjal.Dns.DnsResolver? mx = null;
            try
            {
                mx = Anjal.Dns.DnsResolver.CreateFromSystem();
            }
#pragma warning disable CA1031 // No system resolver: fall back to A/AAAA-only checks.
            catch (System.Exception ex)
            {
                log($"Spam: no system DNS resolver for MX checks ({ex.GetType().Name}); using A/AAAA only.");
            }
#pragma warning restore CA1031
            dns = new Anjal.Spam.SystemSpamDnsLookup(mx);
        }

        var scorer = new Anjal.Spam.SpamScorer(null, dns);
        var filter = new Anjal.Spam.SpamFilterSink(scorer, inner, log)
        {
            Action = action == "reject" ? Anjal.Spam.SpamAction.Reject : Anjal.Spam.SpamAction.Junk,
            RejectThreshold = rejectThreshold,
        };
        log($"Spam filter: action={filter.Action.ToString().ToLowerInvariant()}, dns={(useDns ? "on" : "off")}" +
            (filter.Action == Anjal.Spam.SpamAction.Reject ? $", reject threshold={rejectThreshold}" : string.Empty));
        return filter;
    }

    /// <summary>MTA-port policy: rate limits plus greylisting.</summary>
    private static Anjal.Spam.CompositeSmtpPolicy BuildMtaPolicy(Anjal.Store.IMailboxStore mailboxStore, System.Action<string> log)
    {
        var limits = new Anjal.Spam.RateLimitOptions
        {
            ConnectionsPerMinute = ParseIntEnv("ANJAL_RATE_CONN_PER_MIN", 60),
            MessagesPerHourPerIp = ParseIntEnv("ANJAL_RATE_MSG_PER_HOUR", 200),
            MessagesPerHourPerUser = ParseIntEnv("ANJAL_RATE_USER_MSG_PER_HOUR", 100),
        };
        var policies = new System.Collections.Generic.List<Anjal.Smtp.ISmtpPolicy> { new Anjal.Spam.RateLimiter(limits) };

        bool greylist = !string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_GREYLIST"), "false", System.StringComparison.OrdinalIgnoreCase);
        if (greylist)
        {
            int delay = ParseIntEnv("ANJAL_GREYLIST_DELAY_SECONDS", 300);
            policies.Add(new Anjal.Spam.Greylist(new Anjal.Spam.GreylistOptions { Delay = System.TimeSpan.FromSeconds(delay) }));
            log($"Greylisting: on (delay {delay}s).");
        }
        else
        {
            log("Greylisting: off.");
        }
        log($"Rate limits: {limits.ConnectionsPerMinute} conn/min, {limits.MessagesPerHourPerIp} msg/hr per IP.");

        // Hard quota: RCPT to a full mailbox is deferred with 452 (v0.13.0).
        policies.Add(new Anjal.Mailbox.QuotaPolicy(mailboxStore, log));

        // A disabled tenant defers with 450 rather than rejecting, so mail is
        // held by the sending servers and nothing bounces while the account
        // is suspended (v0.14.0).
        policies.Add(new Anjal.Mailbox.TenantStatePolicy(mailboxStore, log));
        foreach (Anjal.Smtp.ISmtpPolicy p in policies)
        {
            if (p is Anjal.Spam.Greylist g)
            {
                Anjal.Smtp.Counters.RegisterGauge("anjal_greylist_entries", "Triplets currently remembered by the greylist.", () => g.Count);
            }
        }
        return new Anjal.Spam.CompositeSmtpPolicy(policies.ToArray());
    }

    /// <summary>Submission-port policy: connection and per-user limits only, no greylisting.</summary>
    private static Anjal.Spam.RateLimiter BuildSubmissionPolicy()
    {
        return new Anjal.Spam.RateLimiter(new Anjal.Spam.RateLimitOptions
        {
            ConnectionsPerMinute = ParseIntEnv("ANJAL_RATE_CONN_PER_MIN", 60),
            MessagesPerHourPerIp = 0,
            MessagesPerHourPerUser = ParseIntEnv("ANJAL_RATE_USER_MSG_PER_HOUR", 100),
        });
    }

    /// <summary>
    /// Whether a token is one of the obvious placeholders people leave in
    /// configuration files. Not a strength meter - just a refusal to start
    /// with "changeme" in production (SEC-R3).
    /// </summary>
    private static bool IsObviousToken(string token)
    {
        // Letters and digits only, lower-cased: "CHANGE-ME-long-random-string"
        // is the placeholder in our own deployment template, and a hyphen
        // must not be enough to get past this.
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        string lowered = sb.ToString();
        foreach (string bad in new[] { "changeme", "password", "secret", "token", "anjal", "test", "example", "placeholder", "xxxx", "0000", "1234" })
        {
            if (lowered.Contains(bad, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int ParseIntEnv(string name, int fallback)
    {
        string? raw = System.Environment.GetEnvironmentVariable(name);
        return raw is not null && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : fallback;
    }

    private static ServerLocalDomainResolver? BuildLocalDomainResolver(
        Anjal.Store.IMessageStore store,
        System.Action<string> log)
    {
        string raw = System.Environment.GetEnvironmentVariable("ANJAL_LOCAL_DOMAINS") ?? string.Empty;
        var envDomains = new System.Collections.Generic.List<string>();
        foreach (string d in raw.Split(','))
        {
            string trimmed = d.Trim().ToLowerInvariant();
            if (trimmed.Length > 0) envDomains.Add(trimmed);
        }

        // Always wire the resolver when we have a store - the store-backed
        // local_domains table may have rows the env-var list doesn't.
        // When both env-var is empty and there's no store, we'd be a
        // no-op resolver, so just return null and let the session take
        // its accept-all branch.
        if (envDomains.Count == 0 && store is null)
        {
            log("Local domains: not configured - MTA accepts all RCPT TO (open-relay risk).");
            return null;
        }

        if (envDomains.Count > 0)
        {
            log($"Local domains (env): {string.Join(',', envDomains)}");
        }
        if (store is not null)
        {
            log("Local domains: store-backed local_domains and tenant_domains tables available.");
        }
        return new ServerLocalDomainResolver(envDomains, store, store as Anjal.Store.IMailboxStore);
    }

    /// <summary>
    /// Build the inbound SPF/DKIM/DMARC authenticator from env-var config.
    /// </summary>
    /// <returns>(authenticator, enforceReject). Returns (null, false) when disabled.</returns>
    private static (Anjal.Smtp.IInboundAuthenticator?, bool) BuildInboundAuth(string hostname, System.Action<string> log)
    {
        // ANJAL_INBOUND_AUTH_ENFORCE:
        //   unset/"" -> default to "dmarc-reject"
        //   "none"        -> annotate only, never reject
        //   "dmarc-reject" -> reject when DMARC fails and policy is p=reject
        string mode = (System.Environment.GetEnvironmentVariable("ANJAL_INBOUND_AUTH_ENFORCE") ?? "dmarc-reject")
            .ToLowerInvariant();

        if (mode == "off" || mode == "disabled")
        {
            log("Inbound authentication: DISABLED.");
            return (null, false);
        }

        bool enforceReject = mode == "dmarc-reject";

        // Use system DNS server for lookups.
        var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
        var inner = new Anjal.Auth.InboundAuthenticator(dns, hostname);
        var adapter = new ServerInboundAuthenticator(inner, enforceReject);
        log(enforceReject
            ? "Inbound authentication: SPF+DKIM+DMARC enabled, DMARC p=reject ENFORCED."
            : "Inbound authentication: SPF+DKIM+DMARC enabled, annotate only.");
        return (adapter, enforceReject);
    }

    private static Anjal.Smtp.IMailSender? ConfigureSender(string mode, string hostname, Anjal.Smtp.TlsClientOptions tls, System.Action<string> log)
    {
        switch (mode)
        {
            case "direct":
                var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
                return new Anjal.Smtp.DirectMailSender(dns, new Anjal.Smtp.DirectSenderOptions
                {
                    ClientHostName = hostname,
                    Tls = tls,
                });

            case "relay":
                string relayHost = System.Environment.GetEnvironmentVariable("ANJAL_RELAY_HOST") ?? string.Empty;
                if (string.IsNullOrEmpty(relayHost))
                {
                    log("ANJAL_OUTBOUND_MODE=relay but ANJAL_RELAY_HOST is not set; outbound disabled.");
                    return null;
                }
                string relayPortStr = System.Environment.GetEnvironmentVariable("ANJAL_RELAY_PORT") ?? "587";
                if (!int.TryParse(relayPortStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int relayPort))
                {
                    relayPort = 587;
                }
                return new Anjal.Smtp.RelayMailSender(new Anjal.Smtp.RelayOptions
                {
                    Host = relayHost,
                    Port = relayPort,
                    ClientHostName = hostname,
                    Tls = tls,
                });

            case "none":
            default:
                return null;
        }
    }
}
