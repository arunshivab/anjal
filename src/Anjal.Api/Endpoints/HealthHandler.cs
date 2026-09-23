using Anjal.Api.Dto;
using Anjal.Store;

namespace Anjal.Api.Endpoints;

/// <summary>
/// <c>GET /healthz</c> (unauthenticated) and <c>GET /metrics</c>
/// (authenticated). Health probes the store, the Maildir root and the
/// TLS certificate and returns 200 when everything is <c>ok</c>, 503
/// when any component is <c>down</c>; <c>degraded</c> (e.g. a
/// certificate close to expiry) keeps 200 so a monitor can alert on the
/// body without treating the service as failed.
/// </summary>
public sealed class HealthHandler
{
    private readonly IMessageStore store;
    private readonly ApiOptions options;
    private readonly System.DateTimeOffset startedAt = System.DateTimeOffset.UtcNow;

    /// <summary>Construct.</summary>
    /// <param name="store">Store to probe.</param>
    /// <param name="options">API options (Maildir root, ACME directory, thresholds).</param>
    public HealthHandler(IMessageStore store, ApiOptions options)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(options);
        this.store = store;
        this.options = options;
    }

    /// <summary>Run every probe and build the report.</summary>
    public async System.Threading.Tasks.Task<HealthResponse> CheckAsync()
    {
        var components = new System.Collections.Generic.List<HealthComponent>
        {
            await this.CheckStoreAsync().ConfigureAwait(false),
            this.CheckMaildir(),
            this.CheckCertificate(),
        };
        string overall = "ok";
        foreach (HealthComponent c in components)
        {
            if (c.Status == "down")
            {
                overall = "down";
                break;
            }
            if (c.Status == "degraded")
            {
                overall = "degraded";
            }
        }
        return new HealthResponse
        {
            Status = overall,
            Version = Anjal.Api.ModuleInfo.Version,
            UptimeSeconds = (long)(System.DateTimeOffset.UtcNow - this.startedAt).TotalSeconds,
            CheckedAt = System.DateTimeOffset.UtcNow,
            Components = components,
        };
    }

    /// <summary>Write the health report.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task WriteHealthAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        HealthResponse report = await this.CheckAsync().ConfigureAwait(false);
        await ctx.WriteJsonAsync(report.Status == "down" ? 503 : 200, report).ConfigureAwait(false);
    }

    /// <summary>Write the Prometheus metrics page.</summary>
    /// <param name="ctx">Request context.</param>
    public async System.Threading.Tasks.Task WriteMetricsAsync(RequestContext ctx)
    {
        System.ArgumentNullException.ThrowIfNull(ctx);
        Anjal.Smtp.Counters.RegisterGauge("anjal_uptime_seconds", "Seconds since the API server started.", () => (System.DateTimeOffset.UtcNow - this.startedAt).TotalSeconds);
        await ctx.WriteTextAsync(200, "text/plain; version=0.0.4; charset=utf-8", Anjal.Smtp.Counters.RenderPrometheus()).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<HealthComponent> CheckStoreAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(5));
            await this.store.ListLocalDomainsAsync(cts.Token).ConfigureAwait(false);
            return new HealthComponent { Name = "store", Status = "ok", Detail = $"{sw.ElapsedMilliseconds} ms" };
        }
#pragma warning disable CA1031 // Any failure means the store is down.
        catch (System.Exception ex)
        {
            // /healthz needs no token, so it says only that the store is
            // unreachable: the driver, host and port stay in the log, where
            // the full exception is already recorded.
            this.options.Log?.Invoke($"healthz: store unreachable: {ex.GetType().Name}: {ex.Message}");
            return new HealthComponent { Name = "store", Status = "down", Detail = "unreachable" };
        }
#pragma warning restore CA1031
    }

    private HealthComponent CheckMaildir()
    {
        if (string.IsNullOrEmpty(this.options.MaildirRoot))
        {
            return new HealthComponent { Name = "maildir", Status = "ok", Detail = "not configured" };
        }
        try
        {
            System.IO.Directory.CreateDirectory(this.options.MaildirRoot);
            string probe = System.IO.Path.Combine(this.options.MaildirRoot, ".healthz-" + System.Guid.NewGuid().ToString("N"));
            System.IO.File.WriteAllText(probe, "ok");
            System.IO.File.Delete(probe);
            return new HealthComponent { Name = "maildir", Status = "ok", Detail = "writable" };
        }
        catch (System.IO.IOException ex)
        {
            this.options.Log?.Invoke($"healthz: maildir unusable: {ex.GetType().Name}: {ex.Message}");
            return new HealthComponent { Name = "maildir", Status = "down", Detail = "unusable" };
        }
        catch (System.UnauthorizedAccessException ex)
        {
            this.options.Log?.Invoke($"healthz: maildir unusable: {ex.GetType().Name}: {ex.Message}");
            return new HealthComponent { Name = "maildir", Status = "down", Detail = "unusable" };
        }
    }

    private HealthComponent CheckCertificate()
    {
        if (string.IsNullOrEmpty(this.options.AcmeDirectory))
        {
            return new HealthComponent { Name = "tls", Status = "ok", Detail = "ACME not configured" };
        }
        var acmeStore = new Anjal.Acme.CertificateStore(this.options.AcmeDirectory);
        using System.Security.Cryptography.X509Certificates.X509Certificate2? cert = acmeStore.LoadCertificate();
        if (cert is null)
        {
            Anjal.Acme.AcmeStatus? status = acmeStore.ReadStatus();
            string why = status is null || status.LastError.Length == 0 ? "no certificate issued yet" : "no certificate; last error: " + status.LastError;
            return new HealthComponent { Name = "tls", Status = "degraded", Detail = why };
        }
        double days = (cert.NotAfter.ToUniversalTime() - System.DateTime.UtcNow).TotalDays;
        if (days < 0)
        {
            return new HealthComponent { Name = "tls", Status = "down", Detail = "certificate expired " + cert.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) };
        }
        string detail = $"expires in {System.Math.Floor(days)} days";
        return new HealthComponent
        {
            Name = "tls",
            Status = days < this.options.CertificateWarnDays ? "degraded" : "ok",
            Detail = detail,
        };
    }
}
