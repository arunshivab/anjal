using System.Text.Json;

namespace Anjal.Store;

/// <summary>One address an organisation's applications must not send to again (rc.17, design review [7d]).</summary>
public sealed class DoNotSendEntry
{
    /// <summary>The address, lower case.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Why: "Mailbox gone", "No such domain", "Gave up 3 times", "Marked as spam", or what the administrator wrote.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>When it was added.</summary>
    public System.DateTimeOffset Added { get; set; }

    /// <summary>Who added it: an administrator's address, or "anjal" when the server did.</summary>
    public string By { get; set; } = string.Empty;
}

/// <summary>An organisation's do-not-send list, kept as the organisation's "do-not-send" document.</summary>
public sealed class DoNotSendDocument
{
    /// <summary>The addresses not to send to.</summary>
    public List<DoNotSendEntry> Entries { get; set; } = new();

    /// <summary>How many times delivery to an address was given up (three adds it).</summary>
    public Dictionary<string, int> GaveUp { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// rc.17 (design review [7d], owner 11 Oct 2026): the do-not-send list. Addresses that bounced hard,
/// were given up on three times, or that an administrator added, are not sent to again by the
/// organisation's applications (the addresses in its "apps" document): the queue marks such a message
/// "Suppressed" and /api/outbound answers "suppressed". People's own mail is never held by it.
/// </summary>
public static class DoNotSend
{
    /// <summary>The tenant document that holds the list.</summary>
    public const string Kind = "do-not-send";

    /// <summary>The tenant document that holds the organisation's applications (written by the webmail).</summary>
    public const string AppsKind = "apps";

    /// <summary>The start of LastError on a message the list held back.</summary>
    public const string SuppressedPrefix = "Suppressed: ";

    /// <summary>Who adds an entry when the server does it.</summary>
    public const string ByAnjal = "anjal";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Read an organisation's list.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The list (empty when there is none).</returns>
    public static async Task<DoNotSendDocument> ReadAsync(IMailboxStore store, System.Guid tenantId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        string? json = await store.GetTenantDocumentAsync(tenantId, Kind, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DoNotSendDocument();
        }
        try
        {
            DoNotSendDocument doc = JsonSerializer.Deserialize<DoNotSendDocument>(json, Json) ?? new DoNotSendDocument();
            doc.GaveUp = new Dictionary<string, int>(doc.GaveUp, System.StringComparer.OrdinalIgnoreCase);
            return doc;
        }
        catch (JsonException)
        {
            return new DoNotSendDocument();
        }
    }

    /// <summary>Save an organisation's list.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="doc">The list.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static Task WriteAsync(IMailboxStore store, System.Guid tenantId, DoNotSendDocument doc, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(doc);
        return store.SetTenantDocumentAsync(tenantId, Kind, JsonSerializer.Serialize(doc, Json), ct);
    }

    /// <summary>The organisation an envelope sender belongs to, and whether it is one of its applications.</summary>
    /// <param name="store">The store.</param>
    /// <param name="envelopeFrom">The envelope sender.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The organisation, or null when the sender's domain is not one of an organisation's.</returns>
    public static async Task<(System.Guid TenantId, bool App)?> SenderAsync(IMailboxStore store, string envelopeFrom, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        int at = envelopeFrom.LastIndexOf('@');
        if (at <= 0 || at == envelopeFrom.Length - 1)
        {
            return null;
        }
        TenantDomainRow? domain = await store.GetTenantDomainAsync(envelopeFrom[(at + 1)..].Trim(), ct).ConfigureAwait(false);
        if (domain is null)
        {
            return null;
        }
        IReadOnlyList<string> apps = await AppSendersAsync(store, domain.TenantId, ct).ConfigureAwait(false);
        return (domain.TenantId, apps.Contains(envelopeFrom.Trim(), System.StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The addresses an organisation's applications send as.</summary>
    /// <param name="store">The store.</param>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The addresses, lower case.</returns>
    public static async Task<IReadOnlyList<string>> AppSendersAsync(IMailboxStore store, System.Guid tenantId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        string? json = await store.GetTenantDocumentAsync(tenantId, AppsKind, ct).ConfigureAwait(false);
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return found;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement app in doc.RootElement.EnumerateArray())
                {
                    if (app.ValueKind == JsonValueKind.Object && app.TryGetProperty("SendsAs", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString() is { Length: > 0 } a)
                    {
                        found.Add(a.Trim().ToLowerInvariant());
                    }
                }
            }
        }
        catch (JsonException)
        {
            return found;
        }
        return found;
    }

    /// <summary>Why a message from an application to an address must not be sent, or null to send it.</summary>
    /// <param name="store">The store.</param>
    /// <param name="envelopeFrom">The envelope sender.</param>
    /// <param name="envelopeTo">The recipient.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reason, or null.</returns>
    public static async Task<string?> CheckAsync(IMailboxStore store, string envelopeFrom, string envelopeTo, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        System.ArgumentNullException.ThrowIfNull(envelopeTo);
        if (await SenderAsync(store, envelopeFrom, ct).ConfigureAwait(false) is not { App: true } sender)
        {
            return null;
        }
        DoNotSendDocument doc = await ReadAsync(store, sender.TenantId, ct).ConfigureAwait(false);
        DoNotSendEntry? entry = doc.Entries.Find(e => string.Equals(e.Address, envelopeTo.Trim(), System.StringComparison.OrdinalIgnoreCase));
        return entry?.Reason;
    }

    /// <summary>
    /// After an application's message failed for good: a hard bounce (the mailbox or the domain does not
    /// exist) adds the address at once; giving up adds it the third time.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="envelopeFrom">The envelope sender.</param>
    /// <param name="envelopeTo">The recipient.</param>
    /// <param name="reply">The receiving server's last reply, or why it was given up.</param>
    /// <param name="permanent">True for a permanent refusal, false when the give-up time passed.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reason it was added with, or null when it was not.</returns>
    public static async Task<string?> NoteFailureAsync(IMailboxStore store, string envelopeFrom, string envelopeTo, string reply, bool permanent, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(store);
        System.ArgumentNullException.ThrowIfNull(envelopeFrom);
        System.ArgumentNullException.ThrowIfNull(envelopeTo);
        System.ArgumentNullException.ThrowIfNull(reply);
        if (await SenderAsync(store, envelopeFrom, ct).ConfigureAwait(false) is not { App: true } sender)
        {
            return null;
        }
        string to = envelopeTo.Trim().ToLowerInvariant();
        DoNotSendDocument doc = await ReadAsync(store, sender.TenantId, ct).ConfigureAwait(false);
        if (doc.Entries.Exists(e => string.Equals(e.Address, to, System.StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        string? reason = null;
        if (permanent)
        {
            reason = HardReason(reply);
        }
        else
        {
            int times = (doc.GaveUp.TryGetValue(to, out int n) ? n : 0) + 1;
            doc.GaveUp[to] = times;
            if (times >= 3)
            {
                reason = "Gave up 3 times";
                doc.GaveUp.Remove(to);
            }
        }
        if (reason is not null)
        {
            doc.Entries.Add(new DoNotSendEntry { Address = to, Reason = reason, Added = now, By = ByAnjal });
        }
        await WriteAsync(store, sender.TenantId, doc, ct).ConfigureAwait(false);
        return reason;
    }

    /// <summary>
    /// The reason a permanent refusal means the address is gone for good, or null when it is some other
    /// refusal (a policy, a full mailbox, a message too big) that a later message might pass.
    /// </summary>
    /// <param name="reply">The receiving server's reply.</param>
    /// <returns>"Mailbox gone", "No such domain", or null.</returns>
    public static string? HardReason(string reply)
    {
        System.ArgumentNullException.ThrowIfNull(reply);
        string r = reply.ToLowerInvariant();
        if (r.Contains("5.1.2", System.StringComparison.Ordinal) || r.Contains("5.1.10", System.StringComparison.Ordinal)
            || r.Contains("no such domain", System.StringComparison.Ordinal) || r.Contains("domain not found", System.StringComparison.Ordinal)
            || r.Contains("nxdomain", System.StringComparison.Ordinal) || r.Contains("no mx", System.StringComparison.Ordinal))
        {
            return "No such domain";
        }
        if (r.Contains("5.1.1", System.StringComparison.Ordinal) || r.Contains("user unknown", System.StringComparison.Ordinal)
            || r.Contains("no such user", System.StringComparison.Ordinal) || r.Contains("does not exist", System.StringComparison.Ordinal)
            || r.Contains("recipient not found", System.StringComparison.Ordinal)
            || r.Contains("unknown recipient", System.StringComparison.Ordinal) || r.Contains("account disabled", System.StringComparison.Ordinal))
        {
            return "Mailbox gone";
        }
        return null;
    }
}
