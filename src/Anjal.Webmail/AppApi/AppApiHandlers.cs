using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.AppApi;

/// <summary>rc.26 (DES-14 §3-§23): what each endpoint does once the caller is known.</summary>
public sealed partial class AppApiService
{
    /// <summary>Run a route: the checks, then its handler.</summary>
    /// <param name="http">The request.</param>
    /// <param name="route">The route.</param>
    /// <returns>The result.</returns>
    public Task<IResult> DispatchAsync(HttpContext http, AppApiRoute route)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(route);
        if (route.Anonymous)
        {
            http.Response.Headers["Anjal-Request-Id"] = Guid.NewGuid().ToString("N")[..16];
            return Task.FromResult(Results.Text(AppOpenApi.Document(http.Request.Scheme + "://" + http.Request.Host), "application/json"));
        }
        string Id() => http.Request.RouteValues.TryGetValue("id", out object? v) ? v?.ToString() ?? string.Empty : string.Empty;
        return this.RunAsync(http, route, caller => caller.Sandbox && SandboxRefused.Contains(route.Operation)
            ? Task.FromResult(Error(new AppApiFailure(403, "sandbox_key", "A sandbox key cannot read or change the application's live inbox, webhooks or do-not-send list. Use a live key."), caller.RequestId))
            : route.Operation switch
            {
                "sendEmail" => this.SendAsync(http, caller),
                "sendBatch" => this.BatchAsync(http, caller),
                "getMessage" => this.StatusAsync(http, caller, Id()),
                "listMessages" => this.ListAsync(http, caller),
                "listEvents" => this.EventsAsync(http, caller),
                "listWebhooks" => Task.FromResult(Webhooks(caller)),
                "addWebhook" => this.AddWebhookAsync(http, caller),
                "removeWebhook" => this.RemoveWebhookAsync(http, caller, Id()),
                "testWebhook" => this.TestWebhookAsync(http, caller, Id()),
                "getLimits" => this.LimitsAsync(http, caller),
                "getHealth" => this.HealthAsync(http, caller),
                "listSuppressions" => this.SuppressionsAsync(http, caller),
                "addSuppression" => this.AddSuppressionAsync(http, caller),
                "removeSuppression" => this.RemoveSuppressionAsync(http, caller, http.Request.RouteValues.TryGetValue("address", out object? a) ? Uri.UnescapeDataString(a?.ToString() ?? string.Empty) : string.Empty),
                "listInbox" => this.InboxAsync(http, caller),
                "getInboxMail" or "getInboxMailRaw" => this.InboxMailAsync(http, caller, Id()),
                "ackInboxMail" => this.AckOneAsync(http, caller, Id()),
                "ackInboxMails" => this.AckManyAsync(http, caller),
                _ => Task.FromResult(Error(new AppApiFailure(404, "not_found", "No such endpoint."), caller.RequestId)),
            });
    }

    // rc.26 review: a sandbox key tests sending; it never reaches the application's live data.
    private static readonly HashSet<string> SandboxRefused = new(StringComparer.Ordinal)
    {
        "addWebhook", "removeWebhook", "listSuppressions", "addSuppression", "removeSuppression",
        "listInbox", "getInboxMail", "getInboxMailRaw", "ackInboxMail", "ackInboxMails",
    };

    private static (JsonDocument? Doc, AppApiFailure? Failure) ParseBody(byte[] body)
    {
        if (body.Length == 0)
        {
            return (null, new AppApiFailure(400, "invalid_json", "The body is empty."));
        }
        try
        {
            return (JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }), null);
        }
        catch (JsonException ex)
        {
            return (null, new AppApiFailure(400, "invalid_json", "The body is not JSON: " + ex.Message));
        }
    }

    // POST /messages/email (§4, §5, §6).
    private async Task<IResult> SendAsync(HttpContext http, AppCaller caller)
    {
        (JsonDocument? doc, AppApiFailure? bad) = ParseBody(caller.Body);
        if (doc is null)
        {
            return Error(bad!, caller.RequestId);
        }
        using (doc)
        {
            string key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (key.Length > 128)
            {
                return Error(new AppApiFailure(400, "invalid_idempotency_key", "An Idempotency-Key has at most 128 characters."), caller.RequestId);
            }
            (AppSendRequest? request, AppApiFailure? invalid) = AppSendRequest.Read(doc.RootElement, this.Clock());
            if (request is null)
            {
                return Error(invalid!, caller.RequestId);
            }
            (int status, object body, int? retryAfter, bool replayed) = await this.SendOneAsync(caller, request, key, Convert.ToHexStringLower(SHA256.HashData(caller.Body)), http.RequestAborted).ConfigureAwait(false);
            if (retryAfter is int wait)
            {
                http.Response.Headers.RetryAfter = wait.ToString(CultureInfo.InvariantCulture);
            }
            if (replayed)
            {
                http.Response.Headers["Idempotent-Replayed"] = "true";
            }
            return Ok(body, status);
        }
    }

    // One send: idempotency, limits, the record, the send. Returns the status and body, and Retry-After.
    private async Task<(int Status, object Body, int? RetryAfter, bool Replayed)> SendOneAsync(AppCaller caller, AppSendRequest request, string idempotencyKey, string bodySha, CancellationToken ct)
    {
        DateTimeOffset now = this.Clock();
        if (idempotencyKey.Length > 0 && await this.ReplayAsync(caller, idempotencyKey, bodySha, ct).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }
        if (await this.RateLimitedAsync(caller, now, ct).ConfigureAwait(false) is { } limited)
        {
            return (429, limited.Body, limited.RetryAfter, false);
        }
        var record = new AppSend
        {
            Id = Guid.NewGuid(),
            TenantId = caller.Tenant.Id,
            AppId = caller.App.Id,
            CreatedAt = now,
            ScheduledAt = request.ScheduledAt,
            From = request.From.Email,
            To = request.AllRecipients.ToList(),
            Tags = request.Tags,
            Metadata = request.Metadata,
            IdempotencyKey = idempotencyKey.Length > 0 ? idempotencyKey : null,
            BodySha256 = bodySha,
            Sandbox = caller.Sandbox,
            Template = request.Template?.Name ?? string.Empty,
            TemplateVersion = request.Template is AppTemplateChoice choice
                ? (await this.mail.AppTemplatesAsync(caller.Tenant.Id, ct).ConfigureAwait(false)).Find(t => t.Name == choice.Name)?.Current?.Version ?? 0
                : 0,
            Status = "processing",
        };
        (AppSend started, bool existing) = await this.Apps.StartAppSendAsync(record, ct).ConfigureAwait(false);
        if (existing)
        {
            return await this.ReplayAsync(caller, idempotencyKey, bodySha, ct).ConfigureAwait(false) ?? (409, ErrorBody("idempotency_in_progress", "A request with this Idempotency-Key is still being processed.", caller.RequestId), 2, false);
        }
        AppSendResult result = await this.mail.SendForAppAsync(caller.Tenant, caller.App, request, started, ct).ConfigureAwait(false);
        if (result.Failure is AppApiFailure failure)
        {
            object failed = ErrorBody(failure.Code, failure.Message, caller.RequestId, failure.Extra);
            if (failure.Code == "suppressed")
            {
                // The 409 is the send's answer: replayed for the same key and body.
                await this.Apps.UpdateAppSendAsync(started.Id, "suppressed", Stored(409, failed), null, null, ct).ConfigureAwait(false);
            }
            else
            {
                // Refused before anything was kept: the key may be used again, and it counts towards no limit.
                await this.Apps.DeleteAppSendAsync(started.Id, ct).ConfigureAwait(false);
            }
            int? retry = failure.Extra?.GetValueOrDefault("retryAfter") is int r ? r : failure.Status == 503 ? 30 : null;
            return (failure.Status, failed, retry, false);
        }
        var ok = new Dictionary<string, object?>
        {
            ["id"] = started.Id,
            ["status"] = result.Status,
            ["messageId"] = result.MessageId,
            ["createdAt"] = MailboxService.Rfc3339(started.CreatedAt),
        };
        if (request.ScheduledAt is DateTimeOffset at)
        {
            ok["scheduledAt"] = MailboxService.Rfc3339(at);
        }
        if (caller.Sandbox)
        {
            ok["sandbox"] = true;
        }
        if (result.Suppressed.Count > 0)
        {
            ok["suppressed"] = result.Suppressed;
        }
        await this.Apps.UpdateAppSendAsync(started.Id, result.Status, Stored(202, ok), null, null, ct).ConfigureAwait(false);
        return (202, ok, null, false);
    }

    // §5: the same key and body give the first answer; a different body is a mismatch; a send still being made is in progress.
    private async Task<(int Status, object Body, int? RetryAfter, bool Replayed)?> ReplayAsync(AppCaller caller, string key, string bodySha, CancellationToken ct)
    {
        AppSend? same = await this.Apps.FindAppSendByKeyAsync(caller.App.Id, key, ct).ConfigureAwait(false);
        if (same is null)
        {
            return null;
        }
        if (!string.Equals(same.BodySha256, bodySha, StringComparison.Ordinal))
        {
            return (409, ErrorBody("idempotency_mismatch", "This Idempotency-Key was used with a different body.", caller.RequestId), null, false);
        }
        if (same.Response.Length == 0)
        {
            return (409, ErrorBody("idempotency_in_progress", "A request with this Idempotency-Key is still being processed.", caller.RequestId), 2, false);
        }
        using JsonDocument stored = JsonDocument.Parse(same.Response);
        int status = stored.RootElement.GetProperty("status").GetInt32();
        object body = JsonSerializer.Deserialize<JsonElement>(stored.RootElement.GetProperty("body").GetRawText());
        return (status, body, null, true);
    }

    private static string Stored(int status, object body) => JsonSerializer.Serialize(new { status, body }, Json);

    private static Dictionary<string, object?> ErrorBody(string code, string message, string requestId, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var body = new Dictionary<string, object?> { ["error"] = code, ["message"] = message, ["requestId"] = requestId };
        foreach ((string k, object? v) in extra ?? new Dictionary<string, object?>())
        {
            body[k] = v;
        }
        return body;
    }

    // §6: the window that is full, as the 429 body and its Retry-After; null when there is room.
    private async Task<(object Body, int RetryAfter)?> RateLimitedAsync(AppCaller caller, DateTimeOffset now, CancellationToken ct)
    {
        foreach (AppRateWindow w in await this.WindowsAsync(caller.App, now, ct).ConfigureAwait(false))
        {
            if (w.Remaining <= 0)
            {
                int wait = Math.Max(1, (int)Math.Ceiling((w.Reset - now).TotalSeconds));
                return (ErrorBody("rate_limited", "The application has sent its " + w.Limit.ToString("N0", CultureInfo.InvariantCulture) + " messages for this " + w.Window + ".", caller.RequestId,
                    new Dictionary<string, object?> { ["window"] = w.Window, ["limit"] = w.Limit, ["retryAfter"] = wait }), wait);
            }
        }
        return null;
    }

    // POST /messages/email/batch (§21): each message on its own; one Idempotency-Key for the whole batch.
    private async Task<IResult> BatchAsync(HttpContext http, AppCaller caller)
    {
        CancellationToken ct = http.RequestAborted;
        (JsonDocument? doc, AppApiFailure? bad) = ParseBody(caller.Body);
        if (doc is null)
        {
            return Error(bad!, caller.RequestId);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("messages", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                return Error(AppSendRequest.Invalid("messages", "The body is {\"messages\": [ ... ]}."), caller.RequestId);
            }
            int count = list.GetArrayLength();
            if (count == 0 || count > 100)
            {
                return Error(AppSendRequest.Invalid("messages", "From 1 to 100 messages."), caller.RequestId);
            }
            string key = http.Request.Headers["Idempotency-Key"].ToString().Trim();
            string sha = Convert.ToHexStringLower(SHA256.HashData(caller.Body));
            Guid? batchId = null;
            if (key.Length > 0)
            {
                if (await this.ReplayAsync(caller, key, sha, ct).ConfigureAwait(false) is { } replay)
                {
                    if (replay.Replayed)
                    {
                        http.Response.Headers["Idempotent-Replayed"] = "true";
                    }
                    if (replay.RetryAfter is int w)
                    {
                        http.Response.Headers.RetryAfter = w.ToString(CultureInfo.InvariantCulture);
                    }
                    return Ok(replay.Body, replay.Status);
                }
                (AppSend batch, bool existing) = await this.Apps.StartAppSendAsync(new AppSend
                {
                    Id = Guid.NewGuid(),
                    TenantId = caller.Tenant.Id,
                    AppId = caller.App.Id,
                    CreatedAt = this.Clock(),
                    IdempotencyKey = key,
                    BodySha256 = sha,
                    Sandbox = caller.Sandbox,
                    Status = "batch",
                }, ct).ConfigureAwait(false);
                if (existing)
                {
                    http.Response.Headers.RetryAfter = "2";
                    return Error(new AppApiFailure(409, "idempotency_in_progress", "A request with this Idempotency-Key is still being processed."), caller.RequestId);
                }
                batchId = batch.Id;
            }
            var results = new List<Dictionary<string, object?>>();
            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                (AppSendRequest? request, AppApiFailure? invalid) = AppSendRequest.Read(item, this.Clock());
                if (request is null)
                {
                    results.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = new { code = invalid!.Code, message = invalid.Message } });
                }
                else
                {
                    (int status, object body, _, _) = await this.SendOneAsync(caller, request, string.Empty, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.GetRawText()))), ct).ConfigureAwait(false);
                    var element = JsonSerializer.SerializeToElement(body, Json);
                    if (status == 202)
                    {
                        results.Add(new Dictionary<string, object?> { ["index"] = index, ["id"] = element.GetProperty("id").GetGuid(), ["status"] = element.GetProperty("status").GetString() });
                    }
                    else
                    {
                        var error = new Dictionary<string, object?> { ["code"] = element.GetProperty("error").GetString(), ["message"] = element.GetProperty("message").GetString() };
                        foreach (string extra in new[] { "retryAfter", "window", "id" })
                        {
                            if (element.TryGetProperty(extra, out JsonElement v))
                            {
                                error[extra] = v;
                            }
                        }
                        results.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = error });
                    }
                }
                index++;
            }
            var answer = new { results };
            if (batchId is Guid id)
            {
                await this.Apps.UpdateAppSendAsync(id, "batch", Stored(200, answer), null, null, ct).ConfigureAwait(false);
            }
            return Ok(answer);
        }
    }

    // GET /messages/{id} (§8).
    private async Task<IResult> StatusAsync(HttpContext http, AppCaller caller, string id)
    {
        if (!Guid.TryParse(id, out Guid sendId) || await this.Apps.GetAppSendAsync(sendId, http.RequestAborted).ConfigureAwait(false) is not AppSend send
            || send.AppId != caller.App.Id || send.Status == "batch" || send.Sandbox != caller.Sandbox)
        {
            return Error(new AppApiFailure(404, "not_found", "No such message of this application."), caller.RequestId);
        }
        return Ok(await this.StatusBodyAsync(send, true, http.RequestAborted).ConfigureAwait(false));
    }

    private async Task<Dictionary<string, object?>> StatusBodyAsync(AppSend send, bool full, CancellationToken ct)
    {
        (string status, IReadOnlyList<OutboundMessage> rows) = await this.mail.AppSendStatusAsync(send, ct).ConfigureAwait(false);
        var body = new Dictionary<string, object?>
        {
            ["id"] = send.Id,
            ["status"] = status,
            ["from"] = send.From,
            ["to"] = send.To,
            ["tags"] = send.Tags,
            ["metadata"] = send.Metadata,
            ["createdAt"] = MailboxService.Rfc3339(send.CreatedAt),
            ["scheduledAt"] = send.ScheduledAt is DateTimeOffset at ? MailboxService.Rfc3339(at) : null,
            ["lastEvent"] = send.LastEvent.Length > 0 ? send.LastEvent : null,
        };
        if (send.Sandbox)
        {
            body["sandbox"] = true;
        }
        if (send.Template.Length > 0)
        {
            body["template"] = new { name = send.Template, version = send.TemplateVersion };
        }
        if (!full)
        {
            return body;
        }
        body["messageId"] = send.MessageId;
        body["recipients"] = rows.Select(r => new
        {
            to = r.EnvelopeTo,
            status = r.Status switch
            {
                OutboundStatus.Sent => "delivered",
                OutboundStatus.Pending or OutboundStatus.Sending => r.Attempts > 0 ? "deferred" : "queued",
                _ => AppEvent.ForOutbound(r.Status, r.LastError)["message.".Length..],
            },
            attempts = r.Attempts,
            reply = r.LastError,
            nextAttempt = r.Status is OutboundStatus.Pending ? MailboxService.Rfc3339(r.NextAttemptAt) : null,
        }).ToList();
        var attempts = new List<object>();
        if (this.messages is IEvidenceStore evidence)
        {
            foreach (OutboundMessage r in rows)
            {
                if ((await evidence.GetOutboundEvidenceLinkAsync(r.Id, ct).ConfigureAwait(false)).EvidenceId is Guid e)
                {
                    foreach (EvidenceAttemptRow a in await evidence.ListEvidenceAttemptsAsync(e, ct).ConfigureAwait(false))
                    {
                        attempts.Add(new { at = MailboxService.Rfc3339(a.AttemptedAt), to = a.Recipient, host = a.RemoteHost, tls = a.TransportTls, replyCode = a.ReplyCode, reply = a.ReplyText, outcome = a.Outcome });
                    }
                }
            }
        }
        body["attempts"] = attempts;
        body["evidence"] = await this.mail.EvidenceShaAsync(send.EvidenceId, ct).ConfigureAwait(false) is string sha ? new { sha256 = sha } : null;
        return body;
    }

    // GET /messages (§3): newest first, 100 a page, a cursor.
    private async Task<IResult> ListAsync(HttpContext http, AppCaller caller)
    {
        IQueryCollection q = http.Request.Query;
        int limit = Math.Clamp(int.TryParse(q["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 100, 1, 100);
        string wantStatus = q["status"].ToString();
        var query = new AppSendQuery
        {
            Since = Time(q["since"]),
            Until = Time(q["until"]),
            Before = Time(q["cursor"]),
            Tag = q["tag"].ToString(),
            Limit = wantStatus.Length > 0 ? 500 : limit,
        };
        var items = new List<Dictionary<string, object?>>();
        DateTimeOffset? last = null;
        foreach (AppSend s in await this.Apps.ListAppSendsAsync(caller.App.Id, query, http.RequestAborted).ConfigureAwait(false))
        {
            // A sandbox key sees its sandbox sends only; a live key its live ones.
            if (s.Sandbox != caller.Sandbox)
            {
                continue;
            }
            Dictionary<string, object?> item = await this.StatusBodyAsync(s, false, http.RequestAborted).ConfigureAwait(false);
            last = s.CreatedAt;
            if (wantStatus.Length > 0 && !string.Equals(item["status"] as string, wantStatus, StringComparison.Ordinal))
            {
                continue;
            }
            items.Add(item);
            if (items.Count == limit)
            {
                break;
            }
        }
        return Ok(new { messages = items, next = items.Count == limit && last is DateTimeOffset n ? MailboxService.Rfc3339(n) : null });
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) ? at : null;

    // GET /events (§8): oldest first, 500 a page; "since" is the last sequence number seen.
    private async Task<IResult> EventsAsync(HttpContext http, AppCaller caller)
    {
        IQueryCollection q = http.Request.Query;
        long since = long.TryParse(q["since"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
        int limit = Math.Clamp(int.TryParse(q["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 500, 1, 500);
        IReadOnlyList<AppEvent> events = await this.Apps.ListAppEventsAsync(caller.App.Id, since, limit, http.RequestAborted).ConfigureAwait(false);
        var sends = new Dictionary<Guid, AppSend?>();
        var items = new List<object>();
        foreach (AppEvent e in events)
        {
            // A sandbox key sees the events of its sandbox sends only.
            if (caller.Sandbox)
            {
                if (e.SendId is not Guid sid)
                {
                    continue;
                }
                if (!sends.TryGetValue(sid, out AppSend? owner))
                {
                    owner = await this.Apps.GetAppSendAsync(sid, http.RequestAborted).ConfigureAwait(false);
                    sends[sid] = owner;
                }
                if (owner?.Sandbox != true)
                {
                    continue;
                }
            }
            items.Add(await this.EventBodyAsync(e, sends, http.RequestAborted).ConfigureAwait(false));
        }
        return Ok(new { events = items, next = events.Count > 0 ? events[^1].Seq : since });
    }

    /// <summary>An event's body, as /events and the webhooks give it (DES-14 §8).</summary>
    /// <param name="e">The event.</param>
    /// <param name="sends">Sends already looked up.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The body.</returns>
    public async Task<Dictionary<string, object?>> EventBodyAsync(AppEvent e, Dictionary<Guid, AppSend?> sends, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(sends);
        var body = new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["seq"] = e.Seq,
            ["type"] = e.Type,
            ["at"] = MailboxService.Rfc3339(e.At),
        };
        if (e.SendId is Guid sid)
        {
            if (!sends.TryGetValue(sid, out AppSend? send))
            {
                send = await this.Apps.GetAppSendAsync(sid, ct).ConfigureAwait(false);
                sends[sid] = send;
            }
            body["message"] = new { id = sid, tags = send?.Tags ?? new List<string>(), metadata = send?.Metadata ?? new Dictionary<string, string>() };
        }
        body["data"] = JsonSerializer.Deserialize<JsonElement>(e.Data);
        return body;
    }

    private static IResult Webhooks(AppCaller caller) =>
        Ok(new { webhooks = caller.App.Webhooks.Select(WebhookBody).ToList() });

    private static object WebhookBody(AppWebhook h) => new
    {
        id = h.Id,
        url = h.Url,
        events = h.Events,
        createdAt = MailboxService.Rfc3339(h.Created),
        failingSince = h.FailingSince is DateTimeOffset f ? MailboxService.Rfc3339(f) : null,
        secretEnds = h.Secret.Length > 4 ? h.Secret[^4..] : string.Empty,
    };

    // POST /webhooks: the secret is returned this once.
    private async Task<IResult> AddWebhookAsync(HttpContext http, AppCaller caller)
    {
        (JsonDocument? doc, AppApiFailure? bad) = ParseBody(caller.Body);
        if (doc is null)
        {
            return Error(bad!, caller.RequestId);
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            string url = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
            var events = new List<string>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("events", out JsonElement ev) && ev.ValueKind == JsonValueKind.Array)
            {
                events.AddRange(ev.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty));
            }
            (string? error, AppWebhook? hook) = await this.mail.AddWebhookAsync(caller.Tenant, caller.App.Id, url, events, http.RequestAborted).ConfigureAwait(false);
            if (hook is null)
            {
                return Error(AppSendRequest.Invalid("url", error ?? "The webhook could not be added."), caller.RequestId);
            }
            return Ok(new { id = hook.Id, url = hook.Url, events = hook.Events, secret = hook.Secret, createdAt = MailboxService.Rfc3339(hook.Created) }, 201);
        }
    }

    private async Task<IResult> RemoveWebhookAsync(HttpContext http, AppCaller caller, string id) =>
        await this.mail.RemoveWebhookAsync(caller.Tenant, caller.App.Id, id, http.RequestAborted).ConfigureAwait(false) is null
            ? Results.StatusCode(204)
            : Error(new AppApiFailure(404, "not_found", "No such webhook of this application."), caller.RequestId);

    // POST /webhooks/{id}/test: a webhook.test event for that webhook alone.
    private async Task<IResult> TestWebhookAsync(HttpContext http, AppCaller caller, string id)
    {
        if (!caller.App.Webhooks.Exists(h => h.Id == id))
        {
            return Error(new AppApiFailure(404, "not_found", "No such webhook of this application."), caller.RequestId);
        }
        AppEvent e = await this.Apps.AddAppEventAsync(new AppEvent
        {
            TenantId = caller.Tenant.Id,
            AppId = caller.App.Id,
            Type = "webhook.test",
            At = this.Clock(),
            Data = JsonSerializer.Serialize(new { webhookId = id }),
        }, http.RequestAborted).ConfigureAwait(false);
        return Ok(new { eventId = e.Id }, 202);
    }

    // GET /limits (§6).
    private async Task<IResult> LimitsAsync(HttpContext http, AppCaller caller)
    {
        DateTimeOffset now = this.Clock();
        IReadOnlyList<AppRateWindow> windows = await this.WindowsAsync(caller.App, now, http.RequestAborted).ConfigureAwait(false);
        Anjal.Mailbox.SendingDayState day = await Anjal.Mailbox.SendingDay.StateAsync(this.store, this.messages, caller.Tenant.Id, now, http.RequestAborted).ConfigureAwait(false);
        return Ok(new
        {
            windows = windows.Select(w => new { window = w.Window, limit = w.Limit, used = w.Used, remaining = w.Remaining, reset = MailboxService.Rfc3339(w.Reset) }),
            organisationDay = new { limit = day.Limit, used = day.Used, remaining = day.Left },
            recipientsPerMessage = AppSendRequest.MaxRecipients,
            attachmentsMb = AppSendRequest.MaxAttachmentBytes / (1024 * 1024),
        });
    }

    // GET /health (§3): can this application send now.
    private async Task<IResult> HealthAsync(HttpContext http, AppCaller caller)
    {
        CancellationToken ct = http.RequestAborted;
        IReadOnlyList<string> own = await this.mail.DomainsOfAsync(caller.Tenant.Id, ct).ConfigureAwait(false);
        List<string> sendDomains = (caller.App.AllowedSenders.Count > 0 ? caller.App.AllowedSenders : new List<string> { caller.App.SendsAs })
            .Select(s => s.StartsWith('@') ? s[1..] : s[(s.LastIndexOf('@') + 1)..]).Where(own.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        DnsCheckRecord? check = await this.mail.DnsCheckOfAsync(caller.Tenant.Id, ct).ConfigureAwait(false);
        var domains = new List<object>();
        bool ok = true;
        foreach (string d in sendDomains)
        {
            bool dkim = await this.messages.GetDkimKeyAsync(d, ct).ConfigureAwait(false) is not null;
            ok &= dkim;
            Dictionary<string, string> found = check?.Domains.GetValueOrDefault(d) ?? new Dictionary<string, string>();
            domains.Add(new
            {
                domain = d,
                dkim = dkim ? found.GetValueOrDefault("DKIM", "key") : "no key",
                spf = found.GetValueOrDefault("SPF", "unchecked"),
                dmarc = found.GetValueOrDefault("DMARC", "unchecked"),
                mx = found.GetValueOrDefault("MX", "unchecked"),
                returnPath = (await this.mail.AppReturnDomainAsync(caller.Tenant.Id, d, ct).ConfigureAwait(false)).Domain,
            });
        }
        IReadOnlyList<AppRateWindow> windows = await this.WindowsAsync(caller.App, this.Clock(), ct).ConfigureAwait(false);
        long hourLeft = windows.First(w => w.Window == "hour").Remaining;
        long dayLeft = windows.First(w => w.Window == "day").Remaining;
        ok &= hourLeft > 0 && dayLeft > 0;
        bool storeOk;
        try
        {
            await this.store.GetTenantByIdAsync(caller.Tenant.Id, ct).ConfigureAwait(false);
            storeOk = true;
        }
#pragma warning disable CA1031 // The health answer reports the store as down rather than failing.
        catch (Exception)
        {
            storeOk = false;
        }
#pragma warning restore CA1031
        ok &= storeOk;
        var body = new
        {
            ok,
            key = new { fingerprint = caller.Key.Fingerprint, sandbox = caller.Sandbox },
            domains,
            limits = new { hourRemaining = hourLeft, dayRemaining = dayLeft },
            server = new { store = storeOk ? "ok" : "down", maildir = "ok", tls = http.Request.IsHttps ? "ok" : "off" },
        };
        return Ok(body, ok ? 200 : 503);
    }

    // GET /suppressions (§19): one address, or the list.
    private async Task<IResult> SuppressionsAsync(HttpContext http, AppCaller caller)
    {
        IQueryCollection q = http.Request.Query;
        string address = q["address"].ToString().Trim().ToLowerInvariant();
        if (address.Length > 0)
        {
            DoNotSendEntry? e = await AppSuppressions.FindAsync(this.store, caller.Tenant.Id, caller.App.Id, address, http.RequestAborted).ConfigureAwait(false);
            return Ok(e is null ? new Dictionary<string, object?> { ["address"] = address, ["suppressed"] = false } : SuppressionBody(e, true));
        }
        string reason = q["reason"].ToString();
        int offset = int.TryParse(q["cursor"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? Math.Max(0, c) : 0;
        List<DoNotSendEntry> all = (await AppSuppressions.ListAsync(this.store, caller.Tenant.Id, caller.App.Id, http.RequestAborted).ConfigureAwait(false))
            .Where(e => reason.Length == 0 || AppSuppressions.CodeOf(e) == reason).ToList();
        List<DoNotSendEntry> page = all.Skip(offset).Take(500).ToList();
        return Ok(new { suppressions = page.Select(e => SuppressionBody(e, null)), next = offset + page.Count < all.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null });
    }

    private static Dictionary<string, object?> SuppressionBody(DoNotSendEntry e, bool? suppressed)
    {
        var body = new Dictionary<string, object?>
        {
            ["address"] = e.Address,
            ["reason"] = AppSuppressions.CodeOf(e),
            ["note"] = e.Reason,
            ["at"] = MailboxService.Rfc3339(e.Added),
            ["sendId"] = e.SendId,
            ["scope"] = e.App.Length == 0 ? "organisation" : "application",
        };
        if (suppressed is bool s)
        {
            body["suppressed"] = s;
        }
        return body;
    }

    // POST /suppressions {address, note}.
    private async Task<IResult> AddSuppressionAsync(HttpContext http, AppCaller caller)
    {
        (JsonDocument? doc, AppApiFailure? bad) = ParseBody(caller.Body);
        if (doc is null)
        {
            return Error(bad!, caller.RequestId);
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            string address = (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("address", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? string.Empty : string.Empty).Trim().ToLowerInvariant();
            string note = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("note", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(address, out _, out _))
            {
                return Error(AppSendRequest.Invalid("address", "Give one address."), caller.RequestId);
            }
            DoNotSendEntry? added = await AppSuppressions.AddAsync(this.store, this.Apps, caller.Tenant.Id, caller.App.Id, address, "manual",
                note.Trim().Length > 0 ? note.Trim()[..Math.Min(100, note.Trim().Length)] : "Added by the application", "api", null, this.Clock(), http.RequestAborted).ConfigureAwait(false);
            if (added is null)
            {
                return Error(new AppApiFailure(409, "already_suppressed", "That address is on the list already."), caller.RequestId);
            }
            return Ok(SuppressionBody(added, true), 201);
        }
    }

    // DELETE /suppressions/{address}: hard_bounce and manual only through the API.
    private async Task<IResult> RemoveSuppressionAsync(HttpContext http, AppCaller caller, string address)
    {
        string? error = await AppSuppressions.RemoveAsync(this.store, this.Apps, caller.Tenant.Id, caller.App.Id, address, byApi: true, this.Clock(), http.RequestAborted).ConfigureAwait(false);
        return error switch
        {
            null => Results.StatusCode(204),
            "not_found" => Error(new AppApiFailure(404, "not_found", "That address is not on the application's list."), caller.RequestId),
            _ => Error(new AppApiFailure(403, "removal_not_allowed", "A complaint, an unsubscribe or an entry for the whole organisation is removed only by an administrator, on the console."), caller.RequestId),
        };
    }

    // GET /inbox (§14): new mail, oldest first, 100 a page; no subject.
    private async Task<IResult> InboxAsync(HttpContext http, AppCaller caller)
    {
        IQueryCollection q = http.Request.Query;
        int limit = Math.Clamp(int.TryParse(q["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 100, 1, 100);
        string ack = q["acknowledged"].ToString();
        bool? acknowledged = ack switch { "true" => true, "all" => null, _ => false };
        if (caller.App.InboxMailboxId is not Guid box)
        {
            return Ok(new { mails = Array.Empty<object>(), next = (string?)null });
        }
        (DateTimeOffset, Guid)? after = ReadCursor(q["since"]);
        IReadOnlyList<MessageRow> rows = await this.Apps.ListAppInboxAsync(box, acknowledged, after, limit, http.RequestAborted).ConfigureAwait(false);
        var mails = rows.Select(InboxItem).ToList();
        return Ok(new { mails, next = rows.Count == limit ? Cursor(rows[^1]) : null });
    }

    private static Dictionary<string, object?> InboxItem(MessageRow m)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = m.Id,
            ["receivedAt"] = MailboxService.Rfc3339(m.ReceivedAt),
            ["to"] = m.ToHeader,
            ["from"] = Anjal.Spam.SpamScorer.FirstAddress(m.FromHeader) is { Length: > 0 } f ? f : m.EnvelopeFrom,
            ["size"] = m.SizeBytes,
            ["spamScore"] = m.SpamScore,
            ["acknowledged"] = m.AcknowledgedAt is not null,
        };
        try
        {
            using JsonDocument meta = JsonDocument.Parse(m.BodyText.Length > 0 ? m.BodyText : "{}");
            if (meta.RootElement.TryGetProperty("auth", out JsonElement auth))
            {
                item["auth"] = auth.Clone();
            }
            if (meta.RootElement.TryGetProperty("inReplyToSend", out JsonElement reply) && reply.ValueKind == JsonValueKind.String)
            {
                item["inReplyToSend"] = reply.GetString();
            }
            if (meta.RootElement.TryGetProperty("risks", out JsonElement risks) && risks.GetArrayLength() > 0)
            {
                item["risks"] = risks.Clone();
            }
        }
        catch (JsonException)
        {
            // A row without its meta still lists.
        }
        return item;
    }

    private static string Cursor(MessageRow m) => m.ReceivedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + m.Id.ToString("N");

    private static (DateTimeOffset, Guid)? ReadCursor(string? text)
    {
        string[] parts = (text ?? string.Empty).Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) && Guid.TryParse(parts[1], out Guid id)
            ? (DateTimeOffset.FromUnixTimeMilliseconds(ms), id)
            : null;
    }

    // GET /inbox/{id} and /raw (§14, §18): the envelope as stored.
    private async Task<IResult> InboxMailAsync(HttpContext http, AppCaller caller, string id)
    {
        if (!Guid.TryParse(id, out Guid messageId) || await this.mail.AppInboxFileAsync(caller.Tenant, caller.App, messageId, http.RequestAborted).ConfigureAwait(false) is not byte[] envelope)
        {
            return Error(new AppApiFailure(404, "not_found", "No such mail in this application's inbox."), caller.RequestId);
        }
        return Results.Bytes(envelope, "application/json");
    }

    // POST /inbox/{id}/ack.
    private async Task<IResult> AckOneAsync(HttpContext http, AppCaller caller, string id) =>
        Guid.TryParse(id, out Guid messageId) && caller.App.InboxMailboxId is Guid box && await this.Apps.AcknowledgeAppMailAsync(box, messageId, this.Clock(), http.RequestAborted).ConfigureAwait(false)
            ? Results.StatusCode(204)
            : Error(new AppApiFailure(404, "not_found", "No such mail in this application's inbox."), caller.RequestId);

    // POST /inbox/ack {ids: [...]}: up to 500, each with its result.
    private async Task<IResult> AckManyAsync(HttpContext http, AppCaller caller)
    {
        (JsonDocument? doc, AppApiFailure? bad) = ParseBody(caller.Body);
        if (doc is null)
        {
            return Error(bad!, caller.RequestId);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is 0 or > 500)
            {
                return Error(AppSendRequest.Invalid("ids", "The body is {\"ids\": [ ... ]}, 1 to 500 of them."), caller.RequestId);
            }
            var results = new List<object>();
            foreach (JsonElement e in ids.EnumerateArray())
            {
                string text = e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
                bool done = Guid.TryParse(text, out Guid messageId) && caller.App.InboxMailboxId is Guid box
                    && await this.Apps.AcknowledgeAppMailAsync(box, messageId, this.Clock(), http.RequestAborted).ConfigureAwait(false);
                results.Add(done ? new { id = text, acknowledged = true } : new { id = text, acknowledged = false, error = "not_found" });
            }
            return Ok(new { results });
        }
    }
}
