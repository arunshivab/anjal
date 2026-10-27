namespace Anjal.Store;

/// <summary>
/// rc.26 (DES-14 §28.1): one send through the application API - one message, however many
/// recipients. Its status is gathered from its rows in the outbound queue. No subject is kept
/// (DES-14 §28.2): the application knows its own subjects.
/// </summary>
public sealed class AppSend
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The organisation.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The application.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>When it was received.</summary>
    public System.DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it is to be sent, or null for at once.</summary>
    public System.DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>The From address.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Every recipient (To, Cc and Bcc), lower case.</summary>
    public List<string> To { get; set; } = new();

    /// <summary>The application's tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>The application's metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(System.StringComparer.Ordinal);

    /// <summary>The Idempotency-Key, or null (cleared after 24 hours).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>The SHA-256 of the request body, hex.</summary>
    public string BodySha256 { get; set; } = string.Empty;

    /// <summary>The response first given (JSON), replayed for the same key and body.</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>True for a sandbox key's send: nothing leaves.</summary>
    public bool Sandbox { get; set; }

    /// <summary>The template used, or empty.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The template's version used.</summary>
    public int TemplateVersion { get; set; }

    /// <summary>The Message-ID Anjal gave it, without angle brackets.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>"processing" while it is checked, then queued, sending, delivered, deferred, bounced, failed, suppressed or sandbox.</summary>
    public string Status { get; set; } = "processing";

    /// <summary>The last event's type.</summary>
    public string LastEvent { get; set; } = string.Empty;

    /// <summary>The evidence copy of the message as built (DEF-076), or null (sandbox, refused).</summary>
    public System.Guid? EvidenceId { get; set; }
}

/// <summary>What <see cref="IAppApiStore.ListAppSendsAsync"/> filters by.</summary>
public sealed class AppSendQuery
{
    /// <summary>From.</summary>
    public System.DateTimeOffset? Since { get; set; }

    /// <summary>Until.</summary>
    public System.DateTimeOffset? Until { get; set; }

    /// <summary>Only sends with this tag.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Only sends received before this one (the cursor).</summary>
    public System.DateTimeOffset? Before { get; set; }

    /// <summary>At most this many.</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>rc.26 (DES-14 §8): one event of an application's, for /events and the webhooks.</summary>
public sealed class AppEvent
{
    /// <summary>The order events happened in (the /events cursor).</summary>
    public long Seq { get; set; }

    /// <summary>The event's id (Anjal-Event-Id).</summary>
    public System.Guid Id { get; set; }

    /// <summary>The organisation.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The application.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The send it is about, or null.</summary>
    public System.Guid? SendId { get; set; }

    /// <summary>message.queued, message.delivered and the rest.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>When.</summary>
    public System.DateTimeOffset At { get; set; }

    /// <summary>The event's data, a JSON object.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>The event types an outbound row's new state gives.</summary>
    /// <param name="status">The new state.</param>
    /// <param name="lastError">The reply, or why it stopped.</param>
    /// <returns>The type.</returns>
    public static string ForOutbound(OutboundStatus status, string lastError)
    {
        System.ArgumentNullException.ThrowIfNull(lastError);
        return status switch
        {
            OutboundStatus.Sent => "message.delivered",
            OutboundStatus.Pending or OutboundStatus.Sending => "message.deferred",
            _ when lastError.StartsWith(DoNotSend.SuppressedPrefix, System.StringComparison.Ordinal) => "message.suppressed",
            _ when lastError.StartsWith("Give-up reached", System.StringComparison.Ordinal)
                || lastError == OutboundMessage.CancelledBySender
                || lastError.StartsWith("DKIM", System.StringComparison.Ordinal)
                || lastError.StartsWith("Not sent", System.StringComparison.Ordinal) => "message.failed",
            _ => "message.bounced",
        };
    }

    /// <summary>The data of an outbound row's event: who, the reply, the attempt.</summary>
    /// <param name="to">The recipient.</param>
    /// <param name="reply">The reply or reason.</param>
    /// <param name="attempts">The attempts so far.</param>
    /// <param name="type">The event type.</param>
    /// <returns>The JSON.</returns>
    public static string OutboundData(string to, string reply, int attempts, string type)
    {
        System.ArgumentNullException.ThrowIfNull(type);
        var data = new Dictionary<string, object> { ["to"] = to ?? string.Empty, ["reply"] = reply ?? string.Empty, ["attempt"] = attempts };
        if (type == "message.bounced")
        {
            data["kind"] = "hard";
            data["source"] = "smtp";
        }
        else if (type == "message.deferred")
        {
            data["kind"] = "soft";
        }
        return System.Text.Json.JsonSerializer.Serialize(data);
    }
}

/// <summary>rc.26 (DES-14 §8): one event's delivery to one webhook, with its retries.</summary>
public sealed class AppWebhookDelivery
{
    /// <summary>Its id.</summary>
    public System.Guid Id { get; set; }

    /// <summary>The event.</summary>
    public long EventSeq { get; set; }

    /// <summary>The application.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The webhook.</summary>
    public string WebhookId { get; set; } = string.Empty;

    /// <summary>Attempts made.</summary>
    public int Attempts { get; set; }

    /// <summary>When it was made, or last tried.</summary>
    public System.DateTimeOffset At { get; set; }

    /// <summary>The last HTTP status, or null.</summary>
    public int? Code { get; set; }

    /// <summary>The next attempt, while pending.</summary>
    public System.DateTimeOffset? NextAt { get; set; }

    /// <summary>pending, delivered or failed.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Why the last attempt failed.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>rc.26 (DES-14 §13): an application's inbox address.</summary>
public sealed class AppInboxAddress
{
    /// <summary>The address, lower case.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The organisation.</summary>
    public System.Guid TenantId { get; set; }

    /// <summary>The application.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>The application's hidden mailbox.</summary>
    public System.Guid MailboxId { get; set; }

    /// <summary>The mailbox that already held the address and keeps receiving it (D-227), or null.</summary>
    public System.Guid? SharedMailboxId { get; set; }

    /// <summary>When it was added.</summary>
    public System.DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// rc.26 (DES-14 §28.1): what the application API keeps, in both stores. Like
/// <see cref="IEvidenceStore"/>, a store that has it is asked for it by a cast.
/// </summary>
public interface IAppApiStore
{
    /// <summary>
    /// Record a new send. When the application already used the same Idempotency-Key in the last
    /// 24 hours, nothing is written and that send is returned with <c>Existing</c> true.
    /// </summary>
    /// <param name="send">The send (its id set by the caller).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The send, and whether it was already there.</returns>
    Task<(AppSend Send, bool Existing)> StartAppSendAsync(AppSend send, CancellationToken ct = default);

    /// <summary>Set a send's status, response and Message-ID.</summary>
    /// <param name="id">The send.</param>
    /// <param name="status">Its status.</param>
    /// <param name="response">The response given (JSON); null keeps it.</param>
    /// <param name="messageId">The Message-ID; null keeps it.</param>
    /// <param name="evidenceId">The evidence copy; null keeps it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task UpdateAppSendAsync(System.Guid id, string status, string? response, string? messageId, System.Guid? evidenceId = null, CancellationToken ct = default);

    /// <summary>Remove a send that was refused before anything was kept, so its key may be used again.</summary>
    /// <param name="id">The send.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DeleteAppSendAsync(System.Guid id, CancellationToken ct = default);

    /// <summary>One send.</summary>
    /// <param name="id">The send.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The send, or null.</returns>
    Task<AppSend?> GetAppSendAsync(System.Guid id, CancellationToken ct = default);

    /// <summary>The send (or batch) an application made with an Idempotency-Key in the last 24 hours, or null.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="key">The key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The send, or null.</returns>
    Task<AppSend?> FindAppSendByKeyAsync(string appId, string key, CancellationToken ct = default);

    /// <summary>The send of an application whose Message-ID this is, or null (replies, DES-14 §16).</summary>
    /// <param name="appId">The application.</param>
    /// <param name="messageId">The Message-ID, without angle brackets.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The send, or null.</returns>
    Task<AppSend?> FindAppSendByMessageIdAsync(string appId, string messageId, CancellationToken ct = default);

    /// <summary>An application's sends, newest first.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="query">The filter.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The sends.</returns>
    Task<IReadOnlyList<AppSend>> ListAppSendsAsync(string appId, AppSendQuery query, CancellationToken ct = default);

    /// <summary>How many sends an application made since a moment (its rate windows).</summary>
    /// <param name="appId">The application.</param>
    /// <param name="since">From.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    Task<long> CountAppSendsAsync(string appId, System.DateTimeOffset since, CancellationToken ct = default);

    /// <summary>A send's rows in the outbound queue, without their content.</summary>
    /// <param name="sendId">The send.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<OutboundMessage>> ListOutboundOfAppSendAsync(System.Guid sendId, CancellationToken ct = default);

    /// <summary>
    /// How many queue rows an organisation's API sends made in a period with a return path on none of
    /// its own domains (Anjal's own return domain, DES-14 §17 option 3) - for the day's sending figure.
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ownDomains">Its domains and their bounce. names, which are counted by domain already.</param>
    /// <param name="start">From.</param>
    /// <param name="until">Until.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    Task<long> CountAppOutboundElsewhereAsync(System.Guid tenantId, IReadOnlyCollection<string> ownDomains, System.DateTimeOffset start, System.DateTimeOffset until, CancellationToken ct = default);

    /// <summary>Add an event.</summary>
    /// <param name="appEvent">The event (its id and time set by the caller).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The event, with its sequence number.</returns>
    Task<AppEvent> AddAppEventAsync(AppEvent appEvent, CancellationToken ct = default);

    /// <summary>Events after a sequence number, oldest first.</summary>
    /// <param name="appId">The application, or null for every application (the webhook worker).</param>
    /// <param name="afterSeq">After this one.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The events.</returns>
    Task<IReadOnlyList<AppEvent>> ListAppEventsAsync(string? appId, long afterSeq, int limit, CancellationToken ct = default);

    /// <summary>Events the webhook worker has not yet turned into deliveries, oldest first.</summary>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The events.</returns>
    Task<IReadOnlyList<AppEvent>> UndispatchedAppEventsAsync(int limit, CancellationToken ct = default);

    /// <summary>Mark an event turned into deliveries.</summary>
    /// <param name="seq">The event.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task MarkAppEventDispatchedAsync(long seq, CancellationToken ct = default);

    /// <summary>One event.</summary>
    /// <param name="seq">Its sequence number.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The event, or null.</returns>
    Task<AppEvent?> GetAppEventAsync(long seq, CancellationToken ct = default);

    /// <summary>Add or update a webhook delivery.</summary>
    /// <param name="delivery">The delivery.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveAppWebhookDeliveryAsync(AppWebhookDelivery delivery, CancellationToken ct = default);

    /// <summary>Webhook deliveries due now.</summary>
    /// <param name="now">Now.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The deliveries.</returns>
    Task<IReadOnlyList<AppWebhookDelivery>> DueAppWebhookDeliveriesAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>An application's latest webhook deliveries, newest first.</summary>
    /// <param name="appId">The application.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The deliveries.</returns>
    Task<IReadOnlyList<AppWebhookDelivery>> ListAppWebhookDeliveriesAsync(string appId, int limit, CancellationToken ct = default);

    /// <summary>One webhook delivery.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The delivery, or null.</returns>
    Task<AppWebhookDelivery?> GetAppWebhookDeliveryAsync(System.Guid id, CancellationToken ct = default);

    /// <summary>Remember a signed request; false when the same one was seen in the last 10 minutes (a replay).</summary>
    /// <param name="keyId">The signing key.</param>
    /// <param name="signature">The signature's hash.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when new.</returns>
    Task<bool> RememberSignatureAsync(string keyId, string signature, System.DateTimeOffset now, CancellationToken ct = default);

    /// <summary>An organisation's application inbox addresses.</summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The addresses.</returns>
    Task<IReadOnlyList<AppInboxAddress>> ListAppInboxAddressesAsync(System.Guid tenantId, CancellationToken ct = default);

    /// <summary>The application that takes mail for an address, or null.</summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The address's row, or null.</returns>
    Task<AppInboxAddress?> GetAppInboxAddressAsync(string address, CancellationToken ct = default);

    /// <summary>Add or change an application inbox address.</summary>
    /// <param name="address">The row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SetAppInboxAddressAsync(AppInboxAddress address, CancellationToken ct = default);

    /// <summary>Remove an application inbox address.</summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was there.</returns>
    Task<bool> RemoveAppInboxAddressAsync(string address, CancellationToken ct = default);

    /// <summary>An application inbox's mail, oldest first.</summary>
    /// <param name="mailboxId">The hidden mailbox.</param>
    /// <param name="acknowledged">Only acknowledged (true), only not (false), or both (null).</param>
    /// <param name="after">After this (received time, id) - the cursor - or null.</param>
    /// <param name="limit">At most this many.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows, with <see cref="MessageRow.BodyText"/> holding the envelope's readable meta (JSON).</returns>
    Task<IReadOnlyList<MessageRow>> ListAppInboxAsync(System.Guid mailboxId, bool? acknowledged, (System.DateTimeOffset At, System.Guid Id)? after, int limit, CancellationToken ct = default);

    /// <summary>Acknowledge one mail of an application inbox (harmless twice).</summary>
    /// <param name="mailboxId">The hidden mailbox.</param>
    /// <param name="messageId">The mail.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>False when there is no such mail.</returns>
    Task<bool> AcknowledgeAppMailAsync(System.Guid mailboxId, System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default);

    /// <summary>How many mails wait unacknowledged, and when the oldest arrived.</summary>
    /// <param name="mailboxId">The hidden mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count and the oldest.</returns>
    Task<(long Count, System.DateTimeOffset? Oldest)> AppInboxWaitingAsync(System.Guid mailboxId, CancellationToken ct = default);

    /// <summary>
    /// The daily clearing: events and webhook deliveries after 30 days, signatures after 10 minutes,
    /// Idempotency-Keys after 24 hours, sandbox sends after 7 days.
    /// </summary>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many rows went.</returns>
    Task<int> PruneAppApiAsync(System.DateTimeOffset now, CancellationToken ct = default);
}
