namespace Anjal.Store;

/// <summary>rc.26 (DES-14 §28.1): the application API, in memory.</summary>
public sealed partial class InMemoryMessageStore : IAppApiStore
{
    private readonly List<AppSend> appSends = new();
    private readonly List<AppEvent> appEvents = new();
    private readonly List<AppWebhookDelivery> appDeliveries = new();
    private readonly Dictionary<(string Key, string Signature), System.DateTimeOffset> signaturesSeen = new();
    private readonly Dictionary<string, AppInboxAddress> appAddresses = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> dispatchedEvents = new();
    private long appEventSeq;

    /// <summary>Tests: the clock the application API's 24-hour and 10-minute windows read; null for the real clock.</summary>
    public System.Func<System.DateTimeOffset>? AppClock { get; set; }

    private System.DateTimeOffset AppNow => this.AppClock?.Invoke() ?? System.DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public Task<(AppSend Send, bool Existing)> StartAppSendAsync(AppSend send, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(send);
        lock (this.gate)
        {
            System.DateTimeOffset now = this.AppNow;
            if (send.IdempotencyKey is { Length: > 0 } key)
            {
                AppSend? same = this.appSends.Find(s => s.AppId == send.AppId && s.IdempotencyKey == key);
                if (same is not null && same.CreatedAt > now - System.TimeSpan.FromHours(24))
                {
                    return Task.FromResult((CloneSend(same), true));
                }
                if (same is not null)
                {
                    same.IdempotencyKey = null;
                }
            }
            AppSend row = CloneSend(send);
            if (row.CreatedAt == default)
            {
                row.CreatedAt = now;
            }
            if (row.IdempotencyKey is { Length: 0 })
            {
                row.IdempotencyKey = null;
            }
            this.appSends.Add(row);
            return Task.FromResult((CloneSend(row), false));
        }
    }

    /// <inheritdoc/>
    public Task UpdateAppSendAsync(System.Guid id, string status, string? response, string? messageId, System.Guid? evidenceId = null, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(status);
        lock (this.gate)
        {
            if (this.appSends.Find(s => s.Id == id) is AppSend s)
            {
                s.Status = status;
                s.Response = response ?? s.Response;
                s.MessageId = messageId ?? s.MessageId;
                s.EvidenceId = evidenceId ?? s.EvidenceId;
            }
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task DeleteAppSendAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.appSends.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<AppSend?> GetAppSendAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            AppSend? s = this.appSends.Find(x => x.Id == id);
            return Task.FromResult(s is null ? null : CloneSend(s));
        }
    }

    /// <inheritdoc/>
    public Task<AppSend?> FindAppSendByKeyAsync(string appId, string key, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(key);
        lock (this.gate)
        {
            AppSend? s = this.appSends.Find(x => x.AppId == appId && x.IdempotencyKey == key && x.CreatedAt > this.AppNow - System.TimeSpan.FromHours(24));
            return Task.FromResult(s is null ? null : CloneSend(s));
        }
    }

    /// <inheritdoc/>
    public Task<AppSend?> FindAppSendByMessageIdAsync(string appId, string messageId, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(messageId);
        lock (this.gate)
        {
            AppSend? s = messageId.Length == 0 ? null : this.appSends.Find(x => x.AppId == appId && x.MessageId == messageId);
            return Task.FromResult(s is null ? null : CloneSend(s));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppSend>> ListAppSendsAsync(string appId, AppSendQuery query, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        System.ArgumentNullException.ThrowIfNull(query);
        lock (this.gate)
        {
            IReadOnlyList<AppSend> found = this.appSends
                .Where(s => s.AppId == appId && s.Status is not ("batch" or "refused")
                    && (query.Since is null || s.CreatedAt >= query.Since)
                    && (query.Until is null || s.CreatedAt < query.Until)
                    && (query.Before is null || s.CreatedAt < query.Before)
                    && (string.IsNullOrEmpty(query.Tag) || s.Tags.Contains(query.Tag, System.StringComparer.Ordinal)))
                .OrderByDescending(s => s.CreatedAt)
                .Take(System.Math.Clamp(query.Limit, 1, 1000))
                .Select(CloneSend)
                .ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountAppSendsAsync(string appId, System.DateTimeOffset since, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        lock (this.gate)
        {
            return Task.FromResult(this.appSends.LongCount(s => s.AppId == appId && s.CreatedAt >= since && s.Status is not ("batch" or "refused")));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboundMessage>> ListOutboundOfAppSendAsync(System.Guid sendId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<OutboundMessage> found = this.outbound.Where(o => o.AppSendId == sendId).OrderBy(o => o.CreatedAt).ThenBy(o => o.EnvelopeTo, System.StringComparer.Ordinal).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<long> CountAppOutboundElsewhereAsync(System.Guid tenantId, IReadOnlyCollection<string> ownDomains, System.DateTimeOffset start, System.DateTimeOffset until, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(ownDomains);
        lock (this.gate)
        {
            var own = new HashSet<string>(ownDomains, System.StringComparer.OrdinalIgnoreCase);
            long n = this.outbound.LongCount(o => o.AppSendId is System.Guid sid && o.CreatedAt >= start && o.CreatedAt < until
                && this.appSends.Exists(s => s.Id == sid && s.TenantId == tenantId)
                && !own.Contains(o.EnvelopeFrom[(o.EnvelopeFrom.LastIndexOf('@') + 1)..]));
            return Task.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public Task<AppEvent> AddAppEventAsync(AppEvent appEvent, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appEvent);
        lock (this.gate)
        {
            return Task.FromResult(this.AddAppEventLocked(appEvent));
        }
    }

    // Called under the gate.
    private AppEvent AddAppEventLocked(AppEvent appEvent)
    {
        var row = new AppEvent
        {
            Seq = ++this.appEventSeq,
            Id = appEvent.Id == System.Guid.Empty ? System.Guid.NewGuid() : appEvent.Id,
            TenantId = appEvent.TenantId,
            AppId = appEvent.AppId,
            SendId = appEvent.SendId,
            Type = appEvent.Type,
            At = appEvent.At == default ? this.AppNow : appEvent.At,
            Data = appEvent.Data,
        };
        this.appEvents.Add(row);
        if (row.SendId is System.Guid sid && this.appSends.Find(s => s.Id == sid) is AppSend send)
        {
            send.LastEvent = row.Type;
        }
        return CloneEvent(row);
    }

    // rc.26 (DES-14 §28.4): an outbound row of an API send changed state (called under the gate).
    private void OutboundEventLocked(OutboundMessage row)
    {
        if (row.AppSendId is not System.Guid sid || this.appSends.Find(s => s.Id == sid) is not AppSend send)
        {
            return;
        }
        string type = AppEvent.ForOutbound(row.Status, row.LastError);
        this.AddAppEventLocked(new AppEvent
        {
            TenantId = send.TenantId,
            AppId = send.AppId,
            SendId = sid,
            Type = type,
            Data = AppEvent.OutboundData(row.EnvelopeTo, row.LastError, row.Attempts, type),
        });
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppEvent>> ListAppEventsAsync(string? appId, long afterSeq, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<AppEvent> found = this.appEvents.Where(e => (appId is null || e.AppId == appId) && e.Seq > afterSeq)
                .OrderBy(e => e.Seq).Take(System.Math.Clamp(limit, 1, 5000)).Select(CloneEvent).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppEvent>> UndispatchedAppEventsAsync(int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<AppEvent> found = this.appEvents.Where(e => !this.dispatchedEvents.Contains(e.Seq)).OrderBy(e => e.Seq).Take(System.Math.Clamp(limit, 1, 5000)).Select(CloneEvent).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task MarkAppEventDispatchedAsync(long seq, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            this.dispatchedEvents.Add(seq);
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<AppEvent?> GetAppEventAsync(long seq, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            AppEvent? e = this.appEvents.Find(x => x.Seq == seq);
            return Task.FromResult(e is null ? null : CloneEvent(e));
        }
    }

    /// <inheritdoc/>
    public Task SaveAppWebhookDeliveryAsync(AppWebhookDelivery delivery, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(delivery);
        lock (this.gate)
        {
            if (delivery.Id == System.Guid.Empty)
            {
                delivery.Id = System.Guid.NewGuid();
            }
            if (delivery.At == default)
            {
                delivery.At = this.AppNow;
            }
            this.appDeliveries.RemoveAll(d => d.Id == delivery.Id);
            this.appDeliveries.Add(CloneDelivery(delivery));
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppWebhookDelivery>> DueAppWebhookDeliveriesAsync(System.DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<AppWebhookDelivery> found = this.appDeliveries.Where(d => d.Status == "pending" && d.NextAt <= now)
                .OrderBy(d => d.NextAt).Take(System.Math.Clamp(limit, 1, 1000)).Select(CloneDelivery).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppWebhookDelivery>> ListAppWebhookDeliveriesAsync(string appId, int limit, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(appId);
        lock (this.gate)
        {
            IReadOnlyList<AppWebhookDelivery> found = this.appDeliveries.Where(d => d.AppId == appId)
                .OrderByDescending(d => d.At).Take(System.Math.Clamp(limit, 1, 1000)).Select(CloneDelivery).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<AppWebhookDelivery?> GetAppWebhookDeliveryAsync(System.Guid id, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            AppWebhookDelivery? d = this.appDeliveries.Find(x => x.Id == id);
            return Task.FromResult(d is null ? null : CloneDelivery(d));
        }
    }

    /// <inheritdoc/>
    public Task<bool> RememberSignatureAsync(string keyId, string signature, System.DateTimeOffset now, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(keyId);
        System.ArgumentNullException.ThrowIfNull(signature);
        lock (this.gate)
        {
            if (this.signaturesSeen.TryGetValue((keyId, signature), out System.DateTimeOffset seen) && seen > now - System.TimeSpan.FromMinutes(10))
            {
                return Task.FromResult(false);
            }
            this.signaturesSeen[(keyId, signature)] = now;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AppInboxAddress>> ListAppInboxAddressesAsync(System.Guid tenantId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<AppInboxAddress> found = this.appAddresses.Values.Where(a => a.TenantId == tenantId)
                .OrderBy(a => a.Address, System.StringComparer.Ordinal).Select(CloneAddress).ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<AppInboxAddress?> GetAppInboxAddressAsync(string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        lock (this.gate)
        {
            return Task.FromResult(this.appAddresses.TryGetValue(address.Trim(), out AppInboxAddress? a) ? CloneAddress(a) : null);
        }
    }

    /// <inheritdoc/>
    public Task SetAppInboxAddressAsync(AppInboxAddress address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        lock (this.gate)
        {
            AppInboxAddress row = CloneAddress(address);
            row.Address = row.Address.Trim().ToLowerInvariant();
            if (this.appAddresses.TryGetValue(row.Address, out AppInboxAddress? old))
            {
                row.AddedAt = old.AddedAt;
            }
            else if (row.AddedAt == default)
            {
                row.AddedAt = this.AppNow;
            }
            this.appAddresses[row.Address] = row;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAppInboxAddressAsync(string address, CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        lock (this.gate)
        {
            return Task.FromResult(this.appAddresses.Remove(address.Trim()));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MessageRow>> ListAppInboxAsync(System.Guid mailboxId, bool? acknowledged, (System.DateTimeOffset At, System.Guid Id)? after, int limit, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            IReadOnlyList<MessageRow> found = this.mailboxMessages
                .Where(m => m.MailboxId == mailboxId
                    && (acknowledged is null || (m.AcknowledgedAt is not null) == acknowledged)
                    && (after is null || m.ReceivedAt > after.Value.At || (m.ReceivedAt == after.Value.At && m.Id.CompareTo(after.Value.Id) > 0)))
                .OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id)
                .Take(System.Math.Clamp(limit, 1, 1000))
                .Select(Clone)
                .ToList();
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc/>
    public Task<bool> AcknowledgeAppMailAsync(System.Guid mailboxId, System.Guid messageId, System.DateTimeOffset at, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            if (this.mailboxMessages.Find(m => m.Id == messageId && m.MailboxId == mailboxId) is not MessageRow row)
            {
                return Task.FromResult(false);
            }
            row.AcknowledgedAt ??= at;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<(long Count, System.DateTimeOffset? Oldest)> AppInboxWaitingAsync(System.Guid mailboxId, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            List<MessageRow> waiting = this.mailboxMessages.Where(m => m.MailboxId == mailboxId && m.AcknowledgedAt is null).ToList();
            return Task.FromResult(((long)waiting.Count, waiting.Count == 0 ? (System.DateTimeOffset?)null : waiting.Min(m => m.ReceivedAt)));
        }
    }

    /// <inheritdoc/>
    public Task<int> PruneAppApiAsync(System.DateTimeOffset now, CancellationToken ct = default)
    {
        lock (this.gate)
        {
            int n = this.appEvents.RemoveAll(e => e.At < now - System.TimeSpan.FromDays(30));
            n += this.appDeliveries.RemoveAll(d => d.At < now - System.TimeSpan.FromDays(30) && d.Status != "pending");
            foreach ((string, string) key in this.signaturesSeen.Where(kv => kv.Value < now - System.TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
            {
                this.signaturesSeen.Remove(key);
                n++;
            }
            foreach (AppSend s in this.appSends.Where(s => s.IdempotencyKey is not null && s.CreatedAt < now - System.TimeSpan.FromHours(24)))
            {
                s.IdempotencyKey = null;
            }
            n += this.appSends.RemoveAll(s => s.Sandbox && s.CreatedAt < now - System.TimeSpan.FromDays(7));
            return Task.FromResult(n);
        }
    }

    private static AppSend CloneSend(AppSend s) => new()
    {
        Id = s.Id,
        TenantId = s.TenantId,
        AppId = s.AppId,
        CreatedAt = s.CreatedAt,
        ScheduledAt = s.ScheduledAt,
        From = s.From,
        To = new List<string>(s.To),
        Tags = new List<string>(s.Tags),
        Metadata = new Dictionary<string, string>(s.Metadata, System.StringComparer.Ordinal),
        IdempotencyKey = s.IdempotencyKey,
        BodySha256 = s.BodySha256,
        Response = s.Response,
        Sandbox = s.Sandbox,
        Template = s.Template,
        TemplateVersion = s.TemplateVersion,
        MessageId = s.MessageId,
        Status = s.Status,
        LastEvent = s.LastEvent,
        EvidenceId = s.EvidenceId,
    };

    private static AppEvent CloneEvent(AppEvent e) => new()
    {
        Seq = e.Seq,
        Id = e.Id,
        TenantId = e.TenantId,
        AppId = e.AppId,
        SendId = e.SendId,
        Type = e.Type,
        At = e.At,
        Data = e.Data,
    };

    private static AppWebhookDelivery CloneDelivery(AppWebhookDelivery d) => new()
    {
        Id = d.Id,
        EventSeq = d.EventSeq,
        AppId = d.AppId,
        WebhookId = d.WebhookId,
        Attempts = d.Attempts,
        At = d.At,
        Code = d.Code,
        NextAt = d.NextAt,
        Status = d.Status,
        Error = d.Error,
    };

    private static AppInboxAddress CloneAddress(AppInboxAddress a) => new()
    {
        Address = a.Address,
        TenantId = a.TenantId,
        AppId = a.AppId,
        MailboxId = a.MailboxId,
        SharedMailboxId = a.SharedMailboxId,
        AddedAt = a.AddedAt,
    };
}
