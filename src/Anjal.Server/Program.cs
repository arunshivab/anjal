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
///   ANJAL_API_TOKEN         - bearer token clients must present.
///   ANJAL_API_BIND          - HTTP API bind address. Defaults to ANJAL_BIND.
///   ANJAL_TLS_CERT_PATH     - path to fullchain.pem (with private key, or use ANJAL_TLS_KEY_PATH).
///   ANJAL_TLS_KEY_PATH      - path to privkey.pem if not embedded in fullchain.
///   ANJAL_TLS_REQUIRE       - if "true", server refuses MAIL FROM until STARTTLS. Default false.
///   ANJAL_TLS_VALIDATE_PEER - if "false", outbound TLS skips cert validation (testing only). Default true.
///   ANJAL_TLS_DEFAULT_MODE  - default outbound TLS mode if no per-domain policy:
///                             "opportunistic" (default), "required", or "disabled".
/// </summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async System.Threading.Tasks.Task<int> Main()
    {
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

        Anjal.Store.IMessageStore store = pg is null
            ? new Anjal.Store.InMemoryMessageStore()
            : new Anjal.Store.PostgresMessageStore(pg);

        var routing = new Anjal.Routing.StoreBackedRoutingTable(store);
        using var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
        var dispatcher = new Anjal.Routing.HttpWebhookDispatcher(http);

        void Log(string line) => System.Console.WriteLine($"[{System.DateTime.UtcNow:HH:mm:ss}] {line}");

        var sink = new RoutingMessageSink(store, routing, dispatcher, Log);

        // TLS cert for SMTP receiver.
        System.Security.Cryptography.X509Certificates.X509Certificate2? tlsCert = LoadServerCert(Log);
        bool requireTls = string.Equals(System.Environment.GetEnvironmentVariable("ANJAL_TLS_REQUIRE"), "true", System.StringComparison.OrdinalIgnoreCase);

        var smtpOptions = new Anjal.Smtp.SmtpServerOptions
        {
            BindAddress = System.Net.IPAddress.Parse(bind),
            Port = port,
            AdvertisedHostName = hostname,
            TlsCertificate = tlsCert,
            RequireTlsForMail = requireTls && tlsCert is not null,
        };

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

        using var server = new Anjal.Smtp.SmtpServer(smtpOptions, sink, inboundAuth, enforceReject);
        Log($"Anjal SMTP listening on {bind}:{port} as {hostname}");
        Log(pg is null ? "Using in-memory store." : "Using PostgreSQL store.");
        if (tlsCert is not null)
        {
            Log($"STARTTLS enabled (cert subject: {tlsCert.Subject}, expires {tlsCert.NotAfter:yyyy-MM-dd}).");
            if (requireTls) Log("Receiver requires TLS before MAIL FROM.");
        }
        else
        {
            Log("STARTTLS disabled (set ANJAL_TLS_CERT_PATH to enable).");
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
                new OutboundWorkerOptions(),
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
            string apiBind = System.Environment.GetEnvironmentVariable("ANJAL_API_BIND") ?? bind;
            string token = System.Environment.GetEnvironmentVariable("ANJAL_API_TOKEN") ?? string.Empty;

            apiServer = new Anjal.Api.ApiServer(new Anjal.Api.ApiOptions
            {
                BindAddress = System.Net.IPAddress.Parse(apiBind),
                Port = apiPort,
                BearerToken = token,
            }, store, Log);
            apiTask = apiServer.StartAsync(cts.Token);
            string authNote = string.IsNullOrEmpty(token) ? " (auth disabled - DO NOT use in production)" : string.Empty;
            Log($"API listening on http://{apiBind}:{apiPort}/api/...{authNote}");
        }
        else
        {
            Log("API disabled (set ANJAL_API_PORT to enable).");
        }

        Log("Press Ctrl+C to stop.");

        try
        {
            await server.StartAsync(cts.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // Expected.
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

        return new Anjal.Smtp.TlsClientOptions
        {
            DefaultMode = defaultMode,
            ValidateCertificate = validate,
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
