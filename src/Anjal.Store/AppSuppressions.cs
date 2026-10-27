using System.Text.Json;

namespace Anjal.Store;

/// <summary>
/// rc.26 (DES-14 §19): the do-not-send list as the application API sees it - the organisation's
/// one list (rc.17), each entry for one application or for all of them, with a code. Adding or
/// removing through here gives the application the events suppression.added and suppression.removed.
/// </summary>
public static class AppSuppressions
{
    /// <summary>The codes an entry may carry.</summary>
    public static readonly IReadOnlyList<string> Codes = new[] { "hard_bounce", "complaint", "unsubscribe", "manual" };

    /// <summary>An entry's code: its own, or for an entry made before rc.26, worked out from its reason.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>hard_bounce, complaint, unsubscribe or manual.</returns>
    public static string CodeOf(DoNotSendEntry entry)
    {
        System.ArgumentNullException.ThrowIfNull(entry);
        if (entry.Code.Length > 0)
        {
            return entry.Code;
        }
        return entry.Reason switch
        {
            "Unsubscribed" => "unsubscribe",
            "Marked as spam" => "complaint",
            "Mailbox gone" or "No such domain" or "Gave up 3 times" when entry.By == DoNotSend.ByAnjal => "hard_bounce",
            _ => "manual",
        };
    }

    /// <summary>Whether an entry holds back an application's mail.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="appId">The application.</param>
    /// <returns>True when it applies.</returns>
    public static bool AppliesTo(DoNotSendEntry entry, string appId)
    {
        System.ArgumentNullException.ThrowIfNull(entry);
        return entry.App.Length == 0 || string.Equals(entry.App, appId, System.StringComparison.Ordinal);
    }

    /// <summary>The entry that holds back an application's mail to an address, or null.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry, or null.</returns>
    public static async Task<DoNotSendEntry?> FindAsync(IMailboxStore store, System.Guid tenantId, string appId, string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        DoNotSendDocument doc = await DoNotSend.ReadAsync(store, tenantId, ct).ConfigureAwait(false);
        return doc.Entries.Find(e => AppliesTo(e, appId) && string.Equals(e.Address, address.Trim(), System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entries that hold back an application's mail, newest first.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    public static async Task<IReadOnlyList<DoNotSendEntry>> ListAsync(IMailboxStore store, System.Guid tenantId, string appId, CancellationToken ct = default)
    {
        DoNotSendDocument doc = await DoNotSend.ReadAsync(store, tenantId, ct).ConfigureAwait(false);
        return doc.Entries.Where(e => AppliesTo(e, appId)).OrderByDescending(e => e.Added).ToList();
    }

    /// <summary>Put an address on an application's list; null when it was already held back for it.</summary>
    /// <param name="store">The store.</param>
    /// <param name="apps">The application API's store, for the event; null for none.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="address">The address.</param>
    /// <param name="code">hard_bounce, complaint, unsubscribe or manual.</param>
    /// <param name="reason">The reason in words.</param>
    /// <param name="by">Who: "anjal", "api", or an administrator.</param>
    /// <param name="sendId">The send that caused it, or null.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The new entry, or null.</returns>
    public static async Task<DoNotSendEntry?> AddAsync(IMailboxStore store, IAppApiStore? apps, System.Guid tenantId, string appId, string address, string code, string reason, string by, System.Guid? sendId, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(address);
        string a = address.Trim().ToLowerInvariant();
        var entry = new DoNotSendEntry { Address = a, Reason = reason, Added = now, By = by, App = appId, Code = code, SendId = sendId };
        bool added = await DoNotSend.ChangeAsync(store, tenantId, doc =>
        {
            if (doc.Entries.Exists(e => AppliesTo(e, appId) && string.Equals(e.Address, a, System.StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            doc.Entries.Add(entry);
            return true;
        }, ct).ConfigureAwait(false);
        if (!added)
        {
            return null;
        }
        if (apps is not null)
        {
            await apps.AddAppEventAsync(new AppEvent
            {
                TenantId = tenantId,
                AppId = appId,
                SendId = sendId,
                Type = "suppression.added",
                At = now,
                Data = JsonSerializer.Serialize(new { address = a, reason = code }),
            }, ct).ConfigureAwait(false);
        }
        return entry;
    }

    /// <summary>
    /// Take an address off an application's list. Through the API only hard_bounce and manual entries
    /// may be removed; a complaint or an unsubscribe is the recipient's own and only an administrator
    /// removes it. Returns null when removed, "not_found", or "removal_not_allowed".
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="apps">The application API's store, for the event; null for none.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="appId">The application.</param>
    /// <param name="address">The address.</param>
    /// <param name="byApi">True when the application asks; false for an administrator.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error code, or null.</returns>
    public static async Task<string?> RemoveAsync(IMailboxStore store, IAppApiStore? apps, System.Guid tenantId, string appId, string address, bool byApi, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(address);
        string a = address.Trim().ToLowerInvariant();
        string? refusal = null;
        await DoNotSend.ChangeAsync(store, tenantId, doc =>
        {
            List<DoNotSendEntry> mine = doc.Entries.Where(e => AppliesTo(e, appId) && string.Equals(e.Address, a, System.StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0)
            {
                refusal = "not_found";
                return false;
            }
            // A complaint or an unsubscribe is the recipient's own; an entry for the whole organisation
            // is the administrator's to remove, not one application's.
            if (byApi && (mine.Exists(e => CodeOf(e) is "complaint" or "unsubscribe") || mine.Exists(e => e.App.Length == 0)))
            {
                refusal = "removal_not_allowed";
                return false;
            }
            foreach (DoNotSendEntry e in mine)
            {
                doc.Entries.Remove(e);
            }
            doc.GaveUp.Remove(a);
            return true;
        }, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }
        if (apps is not null)
        {
            await apps.AddAppEventAsync(new AppEvent
            {
                TenantId = tenantId,
                AppId = appId,
                Type = "suppression.removed",
                At = now,
                Data = JsonSerializer.Serialize(new { address = a }),
            }, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// The outbound worker's step for an API send that failed for good (DES-14 §17): a hard bounce
    /// puts the address on the application's list. No bounce notice is filed anywhere: the
    /// application has the event.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="apps">The application API's store.</param>
    /// <param name="message">The queue row.</param>
    /// <param name="reply">The receiving server's reply.</param>
    /// <param name="permanent">True for a permanent refusal.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry added, or null.</returns>
    public static async Task<DoNotSendEntry?> NoteFinalFailureAsync(IMailboxStore store, IAppApiStore apps, OutboundMessage message, string reply, bool permanent, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(apps);
        System.ArgumentNullException.ThrowIfNull(message);
        System.ArgumentNullException.ThrowIfNull(reply);
        if (!permanent || message.AppSendId is not System.Guid sid || DoNotSend.HardReason(reply) is not string reason
            || await apps.GetAppSendAsync(sid, ct).ConfigureAwait(false) is not AppSend send)
        {
            return null;
        }
        return await AddAsync(store, apps, send.TenantId, send.AppId, message.EnvelopeTo, "hard_bounce", reason, DoNotSend.ByAnjal, sid, now, ct).ConfigureAwait(false);
    }

    /// <summary>The outbound worker's check for an API send's row just before it is sent: the reason, or null.</summary>
    /// <param name="store">The store.</param>
    /// <param name="apps">The application API's store.</param>
    /// <param name="message">The queue row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reason, or null.</returns>
    public static async Task<string?> CheckRowAsync(IMailboxStore store, IAppApiStore apps, OutboundMessage message, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(apps);
        System.ArgumentNullException.ThrowIfNull(message);
        if (message.AppSendId is not System.Guid sid || await apps.GetAppSendAsync(sid, ct).ConfigureAwait(false) is not AppSend send)
        {
            return null;
        }
        return (await FindAsync(store, send.TenantId, send.AppId, message.EnvelopeTo, ct).ConfigureAwait(false))?.Reason;
    }
}
