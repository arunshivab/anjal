namespace Anjal.Server;

/// <summary>
/// Composition root for the Anjal mail server host process. Wires
/// Store + Routing + Smtp + Mime together and runs until cancellation.
///
/// Environment variables:
///   ANJAL_BIND              - bind address, default "127.0.0.1"
///   ANJAL_PORT              - SMTP receiver port, default 2525
///   ANJAL_HOSTNAME          - hostname for SMTP banner, default "anjal.localhost"
///   ANJAL_POSTGRES          - PostgreSQL connection string. If unset, an
///                             in-memory store is used (suitable for demos).
///   ANJAL_OUTBOUND_MODE     - "direct" (MX-based, requires port 25 outbound),
///                             "relay" (single upstream, requires ANJAL_RELAY_HOST),
///                             or "none" (no outbound, inbound-only). Default "none".
///   ANJAL_RELAY_HOST        - upstream host for relay mode.
///   ANJAL_RELAY_PORT        - upstream port for relay mode, default 587.
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

        var options = new Anjal.Smtp.SmtpServerOptions
        {
            BindAddress = System.Net.IPAddress.Parse(bind),
            Port = port,
            AdvertisedHostName = hostname,
        };

        using var cts = new System.Threading.CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log("Shutdown requested.");
        };

        using var server = new Anjal.Smtp.SmtpServer(options, sink);
        Log($"Anjal SMTP listening on {bind}:{port} as {hostname}");
        Log(pg is null ? "Using in-memory store." : "Using PostgreSQL store.");

        // Optional outbound side.
        System.Threading.Tasks.Task? workerTask = null;
        Anjal.Smtp.IMailSender? mailSender = ConfigureSender(outboundMode, hostname, Log);
        if (mailSender is not null)
        {
            var worker = new OutboundWorker(store, mailSender, new OutboundWorkerOptions(), log: Log);
            workerTask = worker.RunAsync(cts.Token);
            Log($"Outbound worker started (mode={outboundMode}).");
        }
        else
        {
            Log("Outbound disabled (set ANJAL_OUTBOUND_MODE=direct or relay to enable).");
        }

        Log("Press Ctrl+C to stop.");

        try
        {
            await server.StartAsync(cts.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                // Expected.
            }
        }
        return 0;
    }

    private static Anjal.Smtp.IMailSender? ConfigureSender(string mode, string hostname, System.Action<string> log)
    {
        switch (mode)
        {
            case "direct":
                var dns = Anjal.Dns.DnsResolver.CreateFromSystem();
                return new Anjal.Smtp.DirectMailSender(dns, new Anjal.Smtp.DirectSenderOptions
                {
                    ClientHostName = hostname,
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
                });

            case "none":
            default:
                return null;
        }
    }
}
