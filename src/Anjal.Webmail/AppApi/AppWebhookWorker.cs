using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.AppApi;

/// <summary>
/// rc.26 (DES-14 §8, §28.4): delivers applications' events to their webhooks. It reads new events,
/// makes one delivery for each webhook that takes the event's type, and posts each one signed -
/// <c>Anjal-Signature: t=&lt;unix&gt;, v1=&lt;hex HMAC-SHA256(secret, t + "." + body)&gt;</c> - with
/// Anjal-Event-Id. A 2xx within 10 seconds counts; otherwise it tries again at 1, 5 and 30 minutes,
/// then every hour for 24 hours; after that the delivery has failed, the webhook is marked failing,
/// and the organisation's administrators are told. The application can still pull /events.
/// </summary>
public sealed class AppWebhookWorker : BackgroundService
{
    private static readonly TimeSpan[] Retries = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30) };

    private readonly AppApiService api;
    private readonly MailboxService mail;
    private readonly IMessageStore messages;
    private readonly IMailboxStore store;
    private readonly HttpClient http;

    // For organisations whose operator allowed private addresses (an application on this machine).
    private readonly HttpClient open;

    /// <summary>Initializes a new instance of the <see cref="AppWebhookWorker"/> class.</summary>
    /// <param name="api">The API service (for the events' bodies).</param>
    /// <param name="mail">The mail service.</param>
    /// <param name="messages">The message store.</param>
    /// <param name="store">The mailbox store.</param>
    public AppWebhookWorker(AppApiService api, MailboxService mail, IMessageStore messages, IMailboxStore store)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(store);
        this.api = api;
        this.mail = mail;
        this.messages = messages;
        this.store = store;
        // The address is checked again when the connection is made: a name may resolve to a public
        // address when the webhook is added and to a private one later (DNS rebinding).
        this.http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false, ConnectCallback = ConnectPublicAsync }) { Timeout = TimeSpan.FromSeconds(10) };
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("Anjal-Webhooks/1.0");
        this.open = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(10) };
        this.open.DefaultRequestHeaders.UserAgent.ParseAdd("Anjal-Webhooks/1.0");
    }

    /// <summary>
    /// Connects to the first public address of the host; a host that resolves only to private
    /// addresses (loopback, the private ranges, link-local, the cloud's metadata address) is refused.
    /// </summary>
    /// <param name="context">The host and port.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The connected stream.</returns>
    public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? new[] { literal }
            : await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        IPAddress target = Array.Find(addresses, a => !MailboxService.IsPrivate(a))
            ?? throw new HttpRequestException("The webhook's address is a private address.");
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>How often the worker looks for work (tests shorten it).</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The signature header's value for a body (DES-14 §8).</summary>
    /// <param name="secret">The webhook's secret.</param>
    /// <param name="unixSeconds">The time.</param>
    /// <param name="body">The body.</param>
    /// <returns>The header's value.</returns>
    public static string Signature(string secret, long unixSeconds, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);
        string t = unixSeconds.ToString(CultureInfo.InvariantCulture);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + "." + body));
        return "t=" + t + ", v1=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>The next attempt after a failed one, or null when 24 hours have passed since the first.</summary>
    /// <param name="attempts">Attempts made so far.</param>
    /// <param name="first">When the delivery was made.</param>
    /// <param name="now">Now.</param>
    /// <returns>The next attempt, or null.</returns>
    public static DateTimeOffset? NextAttempt(int attempts, DateTimeOffset first, DateTimeOffset now)
    {
        DateTimeOffset next = now + (attempts <= Retries.Length ? Retries[Math.Max(0, attempts - 1)] : TimeSpan.FromHours(1));
        return next - first > TimeSpan.FromHours(24) ? null : next;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        this.http.Dispose();
        this.open.Dispose();
        base.Dispose();
    }

    /// <summary>One round: new events into deliveries, then the deliveries that are due. Returns how many were posted.</summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Deliveries attempted.</returns>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (this.messages is not IAppApiStore apps)
        {
            return 0;
        }
        foreach (AppEvent e in await apps.UndispatchedAppEventsAsync(500, ct).ConfigureAwait(false))
        {
            if (await this.AppOfAsync(e.TenantId, e.AppId, ct).ConfigureAwait(false) is OrgApp app)
            {
                string? only = e.Type == "webhook.test" ? TestTarget(e.Data) : null;
                foreach (AppWebhook hook in app.Webhooks.Where(h => only is null ? h.Events.Count == 0 || h.Events.Contains(e.Type) : h.Id == only))
                {
                    await apps.SaveAppWebhookDeliveryAsync(new AppWebhookDelivery { EventSeq = e.Seq, AppId = app.Id, WebhookId = hook.Id, At = now, NextAt = now, Status = "pending" }, ct).ConfigureAwait(false);
                }
            }
            await apps.MarkAppEventDispatchedAsync(e.Seq, ct).ConfigureAwait(false);
        }
        int posted = 0;
        foreach (AppWebhookDelivery d in await apps.DueAppWebhookDeliveriesAsync(now, 100, ct).ConfigureAwait(false))
        {
            await this.DeliverAsync(apps, d, now, ct).ConfigureAwait(false);
            posted++;
        }
        return posted;
    }

    /// <summary>Post one delivery now (also the console's "Send again").</summary>
    /// <param name="apps">The store.</param>
    /// <param name="d">The delivery.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task DeliverAsync(IAppApiStore apps, AppWebhookDelivery d, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(d);
        AppEvent? e = await apps.GetAppEventAsync(d.EventSeq, ct).ConfigureAwait(false);
        OrgApp? app = e is null ? null : await this.AppOfAsync(e.TenantId, d.AppId, ct).ConfigureAwait(false);
        AppWebhook? hook = app?.Webhooks.Find(h => h.Id == d.WebhookId);
        d.Attempts++;
        d.At = now;
        if (e is null || app is null || hook is null)
        {
            d.Status = "failed";
            d.Error = "The event or the webhook is gone.";
            d.NextAt = null;
            await apps.SaveAppWebhookDeliveryAsync(d, ct).ConfigureAwait(false);
            return;
        }
        string body = JsonSerializer.Serialize(await this.api.EventBodyAsync(e, new Dictionary<Guid, AppSend?>(), ct).ConfigureAwait(false), AppApiService.JsonOptions);
        string? problem = await this.mail.WebhookUrlProblemAsync(e.TenantId, hook.Url, ct).ConfigureAwait(false);
        int? code = null;
        if (problem is null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add("Anjal-Signature", Signature(hook.Secret, now.ToUnixTimeSeconds(), body));
                request.Headers.Add("Anjal-Event-Id", e.Id.ToString());
                HttpClient client = await this.mail.AllowsPrivateWebhooksAsync(e.TenantId, ct).ConfigureAwait(false) ? this.open : this.http;
                using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(false);
                code = (int)response.StatusCode;
                problem = response.IsSuccessStatusCode ? null : "HTTP " + code.Value.ToString(CultureInfo.InvariantCulture);
            }
            catch (HttpRequestException ex)
            {
                problem = ex.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                problem = "No answer within 10 seconds";
            }
        }
        d.Code = code;
        if (problem is null)
        {
            d.Status = "delivered";
            d.Error = string.Empty;
            d.NextAt = null;
            if (hook.FailingSince is not null)
            {
                await this.mail.ChangeAppAsync(e.TenantId, app.Id, a =>
                {
                    if (a.Webhooks.Find(h => h.Id == hook.Id) is AppWebhook w)
                    {
                        w.FailingSince = null;
                    }
                    return null;
                }, ct).ConfigureAwait(false);
            }
        }
        else
        {
            d.Error = problem.Length > 300 ? problem[..300] : problem;
            DateTimeOffset first = await FirstAttemptAsync(apps, d, ct).ConfigureAwait(false);
            d.NextAt = NextAttempt(d.Attempts, first, now);
            if (d.NextAt is null)
            {
                d.Status = "failed";
                await this.MarkFailingAsync(e.TenantId, app, hook, now, ct).ConfigureAwait(false);
            }
        }
        await apps.SaveAppWebhookDeliveryAsync(d, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await this.RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The worker keeps going; a fault is written to the log.
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] app-webhooks: {ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // The first attempt's time: the delivery's own first "At" is overwritten, so the event's time is used.
    private static async Task<DateTimeOffset> FirstAttemptAsync(IAppApiStore apps, AppWebhookDelivery d, CancellationToken ct) =>
        (await apps.GetAppEventAsync(d.EventSeq, ct).ConfigureAwait(false))?.At ?? d.At;

    private static string? TestTarget(string data)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("webhookId", out JsonElement id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<OrgApp?> AppOfAsync(Guid tenantId, string appId, CancellationToken ct) =>
        await this.mail.AppOfAsync(tenantId, appId, ct).ConfigureAwait(false);

    // 24 hours of failure: the webhook is failing; its organisation's administrators are told once.
    private async Task MarkFailingAsync(Guid tenantId, OrgApp app, AppWebhook hook, DateTimeOffset now, CancellationToken ct)
    {
        if (hook.FailingSince is not null)
        {
            return;
        }
        await this.mail.ChangeAppAsync(tenantId, app.Id, a =>
        {
            if (a.Webhooks.Find(h => h.Id == hook.Id) is AppWebhook w)
            {
                w.FailingSince ??= now;
            }
            return null;
        }, ct).ConfigureAwait(false);
        if (await this.store.GetTenantByIdAsync(tenantId, ct).ConfigureAwait(false) is not TenantRow tenant)
        {
            return;
        }
        foreach (Guid admin in await this.mail.AdminsOfAsync(tenant, ct).ConfigureAwait(false))
        {
            if (await this.store.GetMailboxByIdAsync(admin, ct).ConfigureAwait(false) is MailboxRow a)
            {
                await this.mail.SendSystemMailAsync(a, a.Address, "A webhook of " + app.Name + " is failing",
                    "For 24 hours Anjal could not deliver events to " + hook.Url + ", the webhook of " + app.Name + ". It keeps the events: the application can read them from /api/v1/events, and the console's Webhooks list can send them again once the address answers.\r\n\r\nAnjal", ct).ConfigureAwait(false);
            }
        }
    }
}
