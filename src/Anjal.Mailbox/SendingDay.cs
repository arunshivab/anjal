using System.Text.Json;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>An organisation's sending today against its daily figure (rc.26, item 99).</summary>
/// <param name="Limit">The figure the operator set (2,000 by default).</param>
/// <param name="Used">Copies sent outside today, every route together.</param>
public sealed record SendingDayState(int Limit, long Used)
{
    /// <summary>How many more may go today.</summary>
    public long Left => System.Math.Max(0, this.Limit - this.Used);

    /// <summary>Today's use as a whole percentage of the figure.</summary>
    public int Percent => this.Limit <= 0 ? 0 : (int)System.Math.Min(999, this.Used * 100 / this.Limit);
}

/// <summary>
/// rc.26 (item 99, D-219): the organisation's sends per day (the operator's figure, 2,000 by default)
/// counted for every route together - webmail, a mail app on port 587, an application's SMTP key and
/// the application API. One count per organisation per day, in Anjal's own day (<c>ANJAL_TIMEZONE</c>,
/// India by default): every copy queued to an outside address counts, so a mail to five outside
/// people counts five.
/// </summary>
public static class SendingDay
{
    /// <summary>The organisation document the figure is kept in (the operator's record).</summary>
    public const string OpsKind = "ops";

    /// <summary>The default figure when the operator has not set one.</summary>
    public const int DefaultLimit = 2000;

    /// <summary>The reply to a mail app over the figure.</summary>
    /// <param name="limit">The figure.</param>
    /// <returns>The words.</returns>
    public static string OverText(int limit) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Your organisation has sent its {limit:N0} messages for today; try again tomorrow");

    /// <summary>The start of Anjal's day containing a moment.</summary>
    /// <param name="now">The moment.</param>
    /// <returns>The start, as an instant.</returns>
    public static System.DateTimeOffset DayStart(System.DateTimeOffset now)
    {
        System.TimeZoneInfo zone;
        try
        {
            zone = System.TimeZoneInfo.FindSystemTimeZoneById(System.Environment.GetEnvironmentVariable("ANJAL_TIMEZONE") is { Length: > 0 } z ? z : "Asia/Kolkata");
        }
        catch (System.Exception ex) when (ex is System.TimeZoneNotFoundException or System.InvalidTimeZoneException)
        {
            zone = System.TimeZoneInfo.Utc;
        }
        System.DateTimeOffset local = System.TimeZoneInfo.ConvertTime(now, zone);
        // In UTC: the start goes to PostgreSQL, which takes only offset 0.
        return new System.DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
    }

    /// <summary>An organisation's figure and today's use.</summary>
    /// <param name="boxes">The mailbox store.</param>
    /// <param name="queue">The queue store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    public static async System.Threading.Tasks.Task<SendingDayState> StateAsync(IMailboxStore boxes, IMessageStore queue, System.Guid tenantId, System.DateTimeOffset now, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(boxes);
        System.ArgumentNullException.ThrowIfNull(queue);
        int limit = DefaultLimit;
        if (await boxes.GetTenantDocumentAsync(tenantId, OpsKind, ct).ConfigureAwait(false) is { Length: > 0 } json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("SendPerDay", out JsonElement v) && v.TryGetInt32(out int n) && n > 0)
                {
                    limit = n;
                }
            }
            catch (JsonException)
            {
                limit = DefaultLimit;
            }
        }
        var domains = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (TenantDomainRow d in await boxes.ListTenantDomainsAsync(tenantId, ct).ConfigureAwait(false))
        {
            domains.Add(d.Domain);
            // The per-send return path of an application (DES-14 §17) uses bounce.<domain>.
            domains.Add("bounce." + d.Domain);
        }
        System.DateTimeOffset start = DayStart(now);
        long used = (await queue.CountOutboundBySenderDomainAsync(start, start.AddDays(1).AddHours(1), ct).ConfigureAwait(false))
            .Where(t => domains.Contains(t.Domain)).Sum(t => t.Queued);
        // rc.26 (DES-14 §17 option 3): API sends returning to Anjal's own bounce domain count too.
        if (queue is IAppApiStore apps)
        {
            used += await apps.CountAppOutboundElsewhereAsync(tenantId, domains, start, start.AddDays(1).AddHours(1), ct).ConfigureAwait(false);
        }
        return new SendingDayState(limit, used);
    }

    /// <summary>Whether <paramref name="adding"/> more copies may go today; null when they may, else the words.</summary>
    /// <param name="boxes">The mailbox store.</param>
    /// <param name="queue">The queue store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="adding">Copies about to be queued.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Null, or the reason.</returns>
    public static async System.Threading.Tasks.Task<string?> RefusedAsync(IMailboxStore boxes, IMessageStore queue, System.Guid tenantId, int adding, System.DateTimeOffset now, System.Threading.CancellationToken ct = default)
    {
        if (adding <= 0)
        {
            return null;
        }
        SendingDayState state = await StateAsync(boxes, queue, tenantId, now, ct).ConfigureAwait(false);
        return state.Used + adding > state.Limit ? OverText(state.Limit) : null;
    }
}
