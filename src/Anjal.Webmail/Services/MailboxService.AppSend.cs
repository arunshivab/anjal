using System.Globalization;
using System.Text;
using System.Text.Json;
using Anjal.Mime;
using Anjal.Store;
using Anjal.Webmail.AppApi;

namespace Anjal.Webmail.Services;

/// <summary>What became of one application send.</summary>
/// <param name="Failure">Why it was refused, or null.</param>
/// <param name="Status">queued, delivered, suppressed or sandbox.</param>
/// <param name="MessageId">The Message-ID given.</param>
/// <param name="Suppressed">Recipients held back by the do-not-send list.</param>
public sealed record AppSendResult(AppApiFailure? Failure, string Status, string MessageId, IReadOnlyList<string> Suppressed);

/// <summary>
/// rc.26 (DES-14 §4, §17, §28.3): sending for an application - the sender, the content, the
/// do-not-send list, the organisation's day; the message built and kept as evidence before anything
/// goes; recipients here delivered at once, the rest queued with the send's own return path.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The sandbox's addresses that answer with a hard and a soft bounce (DES-14 §7).</summary>
    public const string SandboxBounce = "bounce@sandbox.anjal";

    /// <summary>The sandbox's soft bounce.</summary>
    public const string SandboxDefer = "defer@sandbox.anjal";

    /// <summary>How long a sandbox send waits before it is "delivered".</summary>
    public static TimeSpan SandboxDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether an application may send as an address (DES-14 §4).</summary>
    /// <param name="app">The application.</param>
    /// <param name="from">The address.</param>
    /// <param name="domains">The organisation's domains.</param>
    /// <returns>True when it may.</returns>
    public static bool AppMaySendAs(OrgApp app, string from, IReadOnlyCollection<string> domains)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(domains);
        string a = from.Trim().ToLowerInvariant();
        string domain = a[(a.LastIndexOf('@') + 1)..];
        if (!domains.Contains(domain))
        {
            return false;
        }
        if (app.AllowedSenders.Count == 0)
        {
            return string.Equals(a, app.SendsAs.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return app.AllowedSenders.Any(s => s.StartsWith('@') ? string.Equals(s[1..], domain, StringComparison.OrdinalIgnoreCase) : string.Equals(s, a, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The return domain of a send (DES-14 §17, D-229): the sending domain when its MX points here;
    /// else bounce.&lt;domain&gt; when its optional record is in place; else Anjal's own.
    /// </summary>
    /// <param name="tenantId">The organisation.</param>
    /// <param name="sendingDomain">The From address's domain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The domain, and which of the three it is (1, 2 or 3).</returns>
    public async Task<(string Domain, int Mode)> AppReturnDomainAsync(Guid tenantId, string sendingDomain, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sendingDomain);
        string d = sendingDomain.ToLowerInvariant();
        DnsCheckRecord? check = await this.DnsCheckOfAsync(tenantId, ct).ConfigureAwait(false);
        if (check?.Domains.GetValueOrDefault(d) is Dictionary<string, string> found)
        {
            if (found.GetValueOrDefault("MX") == "pass")
            {
                return (d, 1);
            }
            if (found.GetValueOrDefault("Return path") == "pass")
            {
                return ("bounce." + d, 2);
            }
        }
        return (this.AnjalBounceDomain, 3);
    }

    /// <summary>Anjal's own return domain: ANJAL_BOUNCE_DOMAIN, else bounce. and the server's parent domain.</summary>
    public string AnjalBounceDomain
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ANJAL_BOUNCE_DOMAIN") is { Length: > 0 } set)
            {
                return set.Trim().ToLowerInvariant();
            }
            string host = this.hostName.ToLowerInvariant();
            int dot = host.IndexOf('.', StringComparison.Ordinal);
            return "bounce." + (dot > 0 && host.IndexOf('.', dot + 1) > 0 ? host[(dot + 1)..] : host);
        }
    }

    /// <summary>Send one message for an application. The send is already recorded (status "processing").</summary>
    /// <param name="tenant">The organisation.</param>
    /// <param name="app">The application.</param>
    /// <param name="request">The checked body.</param>
    /// <param name="record">The send's record.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What became of it.</returns>
    public async Task<AppSendResult> SendForAppAsync(TenantRow tenant, OrgApp app, AppSendRequest request, AppSend record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(record);
        if (this.messageStore is not IAppApiStore apps)
        {
            return Fail(new AppApiFailure(503, "unavailable", "This server's store cannot keep application sends."));
        }
        IReadOnlyList<string> domains = await this.DomainsOfAsync(tenant.Id, ct).ConfigureAwait(false);
        if (!AppMaySendAs(app, request.From.Email, domains))
        {
            return Fail(new AppApiFailure(403, "sender_not_allowed", request.From.Email + " is not an allowed sender of this application."));
        }
        string subject = request.Subject;
        string text = request.Text;
        string html = request.Html;
        if (request.Template is AppTemplateChoice choice)
        {
            AppTemplate? template = (await this.AppTemplatesAsync(tenant.Id, ct).ConfigureAwait(false)).Find(t => t.Name == choice.Name);
            (string s, string t, string h, int version, string? failure, string missing) = FillAppTemplate(template, choice.Language, choice.Values);
            if (failure == "template_not_found")
            {
                return Fail(new AppApiFailure(404, failure, "No template called " + choice.Name + "."));
            }
            if (failure is not null)
            {
                return Fail(new AppApiFailure(422, failure, "The template needs a value for " + missing + ".", new Dictionary<string, object?> { ["placeholder"] = missing }));
            }
            (subject, text, html) = (s, t, h);
            record.Template = choice.Name;
            record.TemplateVersion = version;
        }

        string messageId = Guid.NewGuid().ToString("N") + "@" + request.From.Email[(request.From.Email.LastIndexOf('@') + 1)..];
        byte[] raw = BuildAppMessage(request, subject, text, html, messageId);
        MimeMessage? parsed = TryParse(raw);
        // D-173 / D-05: programs, scripts and disk images are not sent, inside or out; ClamAV when configured.
        if (Anjal.Mailbox.AttachmentPolicy.RefusedInside(parsed) is string refused)
        {
            return Fail(new AppApiFailure(422, "content_refused", refused + "."));
        }
        if (this.Scanner is not null && request.Attachments.Count > 0)
        {
            Anjal.Mailbox.ScanResult scan = await this.Scanner.ScanAsync(raw, ct).ConfigureAwait(false);
            if (scan.Verdict == Anjal.Mailbox.ScanVerdict.Infected)
            {
                return Fail(new AppApiFailure(422, "content_refused", "A virus was found (" + scan.Name + ")."));
            }
        }

        // The do-not-send list (rc.17, DES-14 §19): suppressed recipients are held back.
        var suppressed = new List<string>();
        var recipients = new List<string>();
        foreach (string to in request.AllRecipients)
        {
            if (await AppSuppressions.FindAsync(this.store, tenant.Id, app.Id, to, ct).ConfigureAwait(false) is not null)
            {
                suppressed.Add(to);
            }
            else
            {
                recipients.Add(to);
            }
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (recipients.Count == 0)
        {
            await apps.UpdateAppSendAsync(record.Id, "suppressed", null, messageId, null, ct).ConfigureAwait(false);
            await apps.AddAppEventAsync(new AppEvent { TenantId = tenant.Id, AppId = app.Id, SendId = record.Id, Type = "message.suppressed", At = now, Data = JsonSerializer.Serialize(new { to = suppressed }) }, ct).ConfigureAwait(false);
            return new AppSendResult(new AppApiFailure(409, "suppressed", "Every recipient is on the do-not-send list.", new Dictionary<string, object?> { ["id"] = record.Id, ["suppressed"] = suppressed }), "suppressed", messageId, suppressed);
        }

        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        var local = new List<string>();
        var external = new List<string>();
        foreach (string to in recipients)
        {
            bool here = request.ScheduledAt is null
                && (await sink.ResolveAsync(to, ct).ConfigureAwait(false) is not null || await sink.IsApplicationAddressAsync(to, ct).ConfigureAwait(false));
            (here ? local : external).Add(to);
        }
        if (!record.Sandbox && external.Count > 0
            && await Anjal.Mailbox.SendingDay.RefusedAsync(this.store, this.messageStore, tenant.Id, external.Count, now, ct).ConfigureAwait(false) is string overToday)
        {
            return Fail(new AppApiFailure(429, "rate_limited", overToday + ".", new Dictionary<string, object?> { ["window"] = "organisation_day", ["retryAfter"] = (int)(Anjal.Mailbox.SendingDay.DayStart(now).AddDays(1) - now).TotalSeconds }));
        }

        if (record.Sandbox)
        {
            // DES-14 §7: every step but the last - nothing is handed to the outbound worker, nothing kept as evidence.
            await apps.UpdateAppSendAsync(record.Id, "queued", null, messageId, null, ct).ConfigureAwait(false);
            await apps.AddAppEventAsync(new AppEvent { TenantId = tenant.Id, AppId = app.Id, SendId = record.Id, Type = "message.queued", At = now, Data = JsonSerializer.Serialize(new { to = recipients, sandbox = true }) }, ct).ConfigureAwait(false);
            _ = this.FinishSandboxAsync(tenant.Id, app.Id, record.Id, recipients);
            return new AppSendResult(null, "queued", messageId, suppressed);
        }

        // DEF-076: the message as built is kept before anything is delivered or queued.
        Guid? evidenceId = null;
        if (this.Evidence is not null)
        {
            try
            {
                evidenceId = await this.Evidence.RecordInboundAsync(new Anjal.Smtp.InboundEvidence
                {
                    RawBytes = raw,
                    EnvelopeFrom = request.From.Email,
                    EnvelopeTo = recipients,
                    ClientHostName = this.hostName,
                    AuthenticatedUser = "app:" + app.Id,
                    ReceivedAt = now,
                }, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Any failure to keep the original stops the send; the application is told 503.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Fail(new AppApiFailure(503, "evidence_unavailable", "The message could not be kept as evidence, so it was not sent. Try again in a moment with the same Idempotency-Key."));
            }
#pragma warning restore CA1031
        }

        if (local.Count > 0)
        {
            string trace = $"Received: by {this.hostName} (Anjal application API) with HTTPS for local delivery;\r\n\t{FormatDate(now)}\r\n";
            Anjal.Smtp.DeliveryResult delivered = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
            {
                EnvelopeFrom = request.From.Email,
                EnvelopeTo = local.ToArray(),
                RawBytes = Encoding.ASCII.GetBytes(trace).Concat(raw).ToArray(),
                AuthenticatedUser = request.From.Email,
                EvidenceId = evidenceId,
            }, ct).ConfigureAwait(false);
            bool ok = delivered.Outcome == Anjal.Smtp.DeliveryOutcome.Accepted;
            foreach (string to in local)
            {
                await apps.AddAppEventAsync(new AppEvent
                {
                    TenantId = tenant.Id,
                    AppId = app.Id,
                    SendId = record.Id,
                    Type = ok ? "message.delivered" : delivered.Outcome == Anjal.Smtp.DeliveryOutcome.TransientFailure ? "message.failed" : "message.bounced",
                    At = DateTimeOffset.UtcNow,
                    Data = JsonSerializer.Serialize(new { to, reply = delivered.ReplyText, source = "local" }),
                }, ct).ConfigureAwait(false);
            }
        }

        if (external.Count > 0)
        {
            (string returnDomain, _) = await this.AppReturnDomainAsync(tenant.Id, request.From.Email[(request.From.Email.LastIndexOf('@') + 1)..], ct).ConfigureAwait(false);
            string returnPath = AppBounceAddress.For(record.Id, returnDomain);
            DateTimeOffset at = request.ScheduledAt ?? now;
            foreach (string to in external)
            {
                await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
                {
                    EnvelopeFrom = returnPath,
                    EnvelopeTo = to,
                    RawBytes = raw,
                    CreatedAt = now,
                    NextAttemptAt = at,
                    GiveUpAt = at + OutboundMessage.DefaultGiveUp,
                    AppSendId = record.Id,
                }, ct).ConfigureAwait(false);
            }
        }
        if (evidenceId is Guid kept && this.Evidence is not null)
        {
            await this.Evidence.CompleteInboundAsync(kept, accepted: true, ct).ConfigureAwait(false);
            if (this.messageStore is IEvidenceStore retention)
            {
                await retention.RaiseEvidenceRetentionAsync(kept, tenant.EvidenceRetentionDays, ct).ConfigureAwait(false);
            }
        }
        string status = external.Count > 0 ? "queued" : "delivered";
        await apps.UpdateAppSendAsync(record.Id, status, null, messageId, evidenceId, ct).ConfigureAwait(false);
        await apps.AddAppEventAsync(new AppEvent
        {
            TenantId = tenant.Id,
            AppId = app.Id,
            SendId = record.Id,
            Type = "message.queued",
            At = now,
            Data = JsonSerializer.Serialize(new { to = recipients, scheduledAt = request.ScheduledAt }),
        }, ct).ConfigureAwait(false);
        if (app.PatientData)
        {
            // DES-14 §20: the outside-mail warnings do not apply; the activity log keeps each send's recipient domains.
            await this.messageStore.AppendAuditAsync(new AuditEvent
            {
                Actor = "app:" + app.Name,
                Action = "app.send.patient-data",
                Subject = record.Id.ToString(),
                Detail = string.Join(", ", recipients.Select(r => r[(r.LastIndexOf('@') + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase)),
            }, ct).ConfigureAwait(false);
        }
        return new AppSendResult(null, status, messageId, suppressed);

        static AppSendResult Fail(AppApiFailure failure) => new(failure, "refused", string.Empty, Array.Empty<string>());
    }

    // DES-14 §7: a sandbox send is "delivered" two seconds later, or bounces for the two test addresses.
    private async Task FinishSandboxAsync(Guid tenantId, string appId, Guid sendId, IReadOnlyList<string> recipients)
    {
        try
        {
            await Task.Delay(SandboxDelay).ConfigureAwait(false);
            if (this.messageStore is not IAppApiStore apps)
            {
                return;
            }
            string status = "delivered";
            foreach (string to in recipients)
            {
                string type = to == SandboxBounce ? "message.bounced" : to == SandboxDefer ? "message.deferred" : "message.delivered";
                if (type == "message.bounced")
                {
                    status = "bounced";
                }
                else if (type == "message.deferred" && status == "delivered")
                {
                    status = "deferred";
                }
                await apps.AddAppEventAsync(new AppEvent
                {
                    TenantId = tenantId,
                    AppId = appId,
                    SendId = sendId,
                    Type = type,
                    At = DateTimeOffset.UtcNow,
                    Data = JsonSerializer.Serialize(new
                    {
                        to,
                        sandbox = true,
                        kind = type == "message.bounced" ? "hard" : type == "message.deferred" ? "soft" : null,
                        status = type == "message.bounced" ? "5.1.1" : type == "message.deferred" ? "4.2.2" : "2.0.0",
                    }),
                }, CancellationToken.None).ConfigureAwait(false);
            }
            await apps.UpdateAppSendAsync(sendId, status == "delivered" ? "sandbox" : status, null, null, null, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A background step: a failure is written to the log and the send stays "queued".
        catch (Exception ex)
        {
            Console.WriteLine($"[app-api] sandbox send {sendId}: {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>Build an application's message (DES-14 §4): text, HTML or both; attachments; X- headers; Anjal's headers.</summary>
    /// <param name="request">The checked body.</param>
    /// <param name="subject">The subject (from the body or the template).</param>
    /// <param name="text">The text.</param>
    /// <param name="html">The HTML.</param>
    /// <param name="messageId">The Message-ID, without angle brackets.</param>
    /// <returns>The message's bytes.</returns>
    public static byte[] BuildAppMessage(AppSendRequest request, string subject, string text, string html, string messageId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(messageId);
        string plain = text.Length > 0 ? text : HtmlText.ToPlain(html);
        plain = plain.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        if (!plain.EndsWith("\r\n", StringComparison.Ordinal))
        {
            plain += "\r\n";
        }
        var textPart = new MimePart();
        textPart.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        textPart.Headers.Add("Content-Transfer-Encoding", "quoted-printable");
        textPart.SetBodyAsText(plain);
        MimeEntity content = textPart;
        if (html.Length > 0)
        {
            var htmlPart = new MimePart();
            htmlPart.Headers.Add("Content-Type", "text/html; charset=utf-8");
            htmlPart.Headers.Add("Content-Transfer-Encoding", "quoted-printable");
            htmlPart.SetBodyAsText(html.EndsWith('\n') ? html : html + "\r\n");
            MimeMultipart alternative = MultipartFactory.Create("alternative");
            alternative.Parts.Add(textPart);
            alternative.Parts.Add(htmlPart);
            content = alternative;
        }
        MimeEntity root = content;
        if (request.Attachments.Count > 0)
        {
            MimeMultipart mixed = MultipartFactory.Create("mixed");
            mixed.Parts.Add(content);
            foreach (AppAttachment a in request.Attachments)
            {
                string safeName = SafeFileName(a.FileName);
                var part = new MimePart();
                part.Headers.Add("Content-Type", $"{MimeHeader.Neutralise(a.ContentType)}; name=\"{safeName}\"");
                part.Headers.Add("Content-Disposition", $"attachment; filename=\"{safeName}\"");
                part.Headers.Add("Content-Transfer-Encoding", "base64");
                part.Body = a.Content;
                mixed.Parts.Add(part);
            }
            root = mixed;
        }
        var msg = new MimeMessage(root);
        msg.Headers.Add("From", Format(request.From));
        msg.Headers.Add("To", request.To.Count > 0 ? string.Join(", ", request.To.Select(Format)) : "undisclosed-recipients:;");
        if (request.Cc.Count > 0)
        {
            msg.Headers.Add("Cc", string.Join(", ", request.Cc.Select(Format)));
        }
        if (request.ReplyTo.Count > 0)
        {
            msg.Headers.Add("Reply-To", string.Join(", ", request.ReplyTo.Select(Format)));
        }
        msg.Subject = EncodeHeaderText(subject.Trim());
        msg.Date = FormatDate(DateTimeOffset.UtcNow);
        msg.Headers.Add("Message-ID", "<" + messageId + ">");
        msg.Headers.Add("MIME-Version", "1.0");
        foreach ((string name, string value) in request.Headers)
        {
            msg.Headers.Add(name, MimeHeader.Neutralise(value));
        }
        if (request.Unsubscribe)
        {
            // rc.22 (item 51): unsubscribe@ the sending domain puts the recipient on the do-not-send list.
            string domain = request.From.Email[(request.From.Email.LastIndexOf('@') + 1)..];
            string who = request.To.Count == 1 ? "?subject=" + Uri.EscapeDataString("unsubscribe " + request.To[0].Email) : string.Empty;
            msg.Headers.Add("List-Unsubscribe", "<mailto:" + Anjal.Mailbox.MailboxSink.UnsubscribeLocal + "@" + domain + who + ">");
        }
        return MimeBuilder.Build(msg);

        static string Format(AppAddress a) => a.Name.Length == 0 ? a.Email : QuoteDisplayName(a.Name) + " <" + a.Email + ">";
    }

    private static string StatusOfRows(IReadOnlyList<OutboundMessage> rows, string stored)
    {
        if (rows.Count == 0)
        {
            return stored;
        }
        if (rows.Any(r => r.Status is OutboundStatus.Pending or OutboundStatus.Sending))
        {
            return rows.Any(r => r.Status == OutboundStatus.Sending) ? "sending" : rows.Any(r => r.Attempts > 0) ? "deferred" : "queued";
        }
        if (rows.All(r => r.Status == OutboundStatus.Sent))
        {
            return "delivered";
        }
        List<string> failed = rows.Where(r => r.Status == OutboundStatus.Failed).Select(r => AppEvent.ForOutbound(r.Status, r.LastError)).ToList();
        return failed.Contains("message.bounced") ? "bounced" : failed.All(t => t == "message.suppressed") ? "suppressed" : "failed";
    }

    /// <summary>A send's status now (DES-14 §8), gathered from its rows in the queue.</summary>
    /// <param name="send">The send.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status, and the rows.</returns>
    public async Task<(string Status, IReadOnlyList<OutboundMessage> Rows)> AppSendStatusAsync(AppSend send, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (send.Sandbox || send.Status is "suppressed" or "processing" || this.messageStore is not IAppApiStore apps)
        {
            return (send.Sandbox && send.Status == "sandbox" ? "delivered" : send.Status, Array.Empty<OutboundMessage>());
        }
        IReadOnlyList<OutboundMessage> rows = await apps.ListOutboundOfAppSendAsync(send.Id, ct).ConfigureAwait(false);
        return (StatusOfRows(rows, send.Status), rows);
    }

    /// <summary>The fingerprint of a send's evidence copy, or null.</summary>
    /// <param name="evidenceId">The evidence.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The SHA-256, or null.</returns>
    public async Task<string?> EvidenceShaAsync(Guid? evidenceId, CancellationToken ct = default) =>
        evidenceId is Guid id && this.messageStore is IEvidenceStore evidence ? (await evidence.GetEvidenceAsync(id, ct).ConfigureAwait(false))?.Sha256 : null;

    /// <summary>The time in Anjal's zone, as RFC 3339 with its offset (DES-14 §1).</summary>
    /// <param name="at">The time.</param>
    /// <returns>The text.</returns>
    public static string Rfc3339(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
