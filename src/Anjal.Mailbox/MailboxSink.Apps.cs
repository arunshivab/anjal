using System.Text.Json;
using Anjal.Store;

namespace Anjal.Mailbox;

/// <summary>
/// rc.26 (DES-14 §13-§18, §28.5): mail for applications - an application's inbox addresses, sole or
/// shared with a mailbox (D-227), each copy encrypted to the application's own key (D-224, D-268);
/// and the per-send return paths, which take delivery and feedback reports only (D-229).
/// </summary>
public sealed partial class MailboxSink
{
    /// <summary>The line a shared address's holder copy carries: the application that also received it.</summary>
    public const string AlsoDeliveredHeader = "X-Anjal-Also-Delivered-To";

    private static readonly char[] ChainSeparators = { ' ', '\t', '\r', '\n', ',' };

    private static readonly char[] AuthSeparators = { ';', ' ', '\t', '\r', '\n' };

    /// <summary>Whether an address belongs to an application here: an inbox address, or a send's return path.</summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when this server takes mail for it.</returns>
    public async System.Threading.Tasks.Task<bool> IsApplicationAddressAsync(string address, System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(address);
        if (this.store is not IAppApiStore apps)
        {
            return false;
        }
        if (AppBounceAddress.IsBounceAddress(address))
        {
            return AppBounceAddress.SendOf(address) is System.Guid sid && await apps.GetAppSendAsync(sid, ct).ConfigureAwait(false) is not null;
        }
        return await apps.GetAppInboxAddressAsync(address, ct).ConfigureAwait(false) is not null;
    }

    // An application's copy of a mail (DES-14 §13, §18): encrypted to its newest key, never Junk.
    private async System.Threading.Tasks.Task<(bool Delivered, bool Full, string? Refusal, string AppName)> DeliverToApplicationAsync(
        IAppApiStore apps, AppInboxAddress inbox, string rcpt, byte[] raw, Anjal.Mime.MimeMessage? parsed, Anjal.Smtp.DeliveryContext ctx, int spamScore,
        System.Collections.Generic.HashSet<string> doneApps, System.Threading.CancellationToken ct)
    {
        AppRecord? app = await AppRecords.GetAsync(this.store, inbox.TenantId, inbox.AppId, ct).ConfigureAwait(false);
        if (app is null || app.Retired is not null)
        {
            return (false, false, "No such mailbox", string.Empty);
        }
        if (!doneApps.Add(app.Id))
        {
            return (true, false, null, app.Name);
        }
        // An address cannot be activated without a key (the console sees to it); never stored readable.
        if (app.NewestKey is not AppEncryptionKey key || inbox.MailboxId == System.Guid.Empty)
        {
            this.log?.Invoke($"Mailbox: {rcpt} belongs to an application with no encryption key; refused");
            return (false, false, "No such mailbox", app.Name);
        }
        MailboxRow? hidden = await this.store.GetMailboxByIdAsync(inbox.MailboxId, ct).ConfigureAwait(false);
        TenantRow? tenant = await this.store.GetTenantByIdAsync(inbox.TenantId, ct).ConfigureAwait(false);
        if (hidden is null || tenant is null)
        {
            return (false, false, "No such mailbox", app.Name);
        }
        if (app.InboxQuotaBytes > 0 && hidden.UsedBytes >= app.InboxQuotaBytes)
        {
            this.log?.Invoke($"Mailbox: the inbox of the application {app.Name} is full; {rcpt} answered 'try later'");
            Anjal.Smtp.Counters.Increment("anjal_app_inbox_full_total");
            return (false, true, null, app.Name);
        }

        // Replies to one of the application's sends (DES-14 §16), by the chain alone.
        System.Guid? inReplyTo = null;
        foreach (string id in ReferencedIds(parsed))
        {
            if (await apps.FindAppSendByMessageIdAsync(app.Id, id, ct).ConfigureAwait(false) is AppSend send)
            {
                inReplyTo = send.Id;
                break;
            }
        }
        var meta = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["receivedAt"] = System.DateTimeOffset.UtcNow,
            ["size"] = raw.LongLength,
            ["spamScore"] = spamScore,
            ["auth"] = AuthResultsOf(parsed),
            ["inReplyToSend"] = inReplyTo,
            ["risks"] = AttachmentPolicy.RisksIn(parsed).Select(r => new System.Collections.Generic.Dictionary<string, string> { ["file"] = r.File, ["risk"] = r.Risk }).ToList(),
        };
        byte[] envelope = AppEnvelope.Seal(WithReturnPath(raw, ctx.EnvelopeFrom), key.Pem, meta);
        FolderRow folder = await this.store.EnsureFolderAsync(hidden.Id, FolderRow.Inbox, ct).ConfigureAwait(false);
        MaildirWriteResult written = await this.maildir.WriteAsync(tenant.Slug, hidden.Address, folder.Name, envelope, ct).ConfigureAwait(false);
        MessageRow row = await this.store.SaveMessageAsync(new MessageRow
        {
            MailboxId = hidden.Id,
            FolderId = folder.Id,
            MaildirFile = written.RelativePath,
            EnvelopeFrom = ctx.EnvelopeFrom,
            MessageId = parsed?.MessageId ?? string.Empty,
            FromHeader = parsed?.Headers.Get("From") ?? string.Empty,
            ToHeader = rcpt.Trim().ToLowerInvariant(),
            Subject = string.Empty,
            DateHeader = parsed?.Date ?? string.Empty,
            SizeBytes = written.SizeBytes,
            SpamScore = spamScore,
            SpamChecked = ctx.AuthenticatedUser is null && parsed?.Headers.Get(Anjal.Spam.SpamHeaders.Score) is not null,
            TransportEncrypted = ctx.AuthenticatedUser is null ? ctx.TransportTls is not null : null,
            TransportTls = ctx.AuthenticatedUser is null ? ctx.TransportTls : null,
            EvidenceId = ctx.EvidenceId,
            HasAttachments = HasAttachment(parsed?.Body),
            BodyText = JsonSerializer.Serialize(meta),
        }, ct).ConfigureAwait(false);
        await this.store.AddMailboxUsageAsync(hidden.Id, written.SizeBytes, ct).ConfigureAwait(false);
        if (ctx.EvidenceId is System.Guid evidenceId && this.store is IEvidenceStore evidenceStore)
        {
            await evidenceStore.RaiseEvidenceRetentionAsync(evidenceId, tenant.EvidenceRetentionDays, ct).ConfigureAwait(false);
        }
        // "You have mail" (DES-14 §15): the id only, never the content.
        await apps.AddAppEventAsync(new AppEvent
        {
            TenantId = tenant.Id,
            AppId = app.Id,
            Type = "inbox.received",
            Data = JsonSerializer.Serialize(new { inboxId = row.Id, to = row.ToHeader }),
        }, ct).ConfigureAwait(false);
        if (inReplyTo is System.Guid replied)
        {
            await apps.AddAppEventAsync(new AppEvent
            {
                TenantId = tenant.Id,
                AppId = app.Id,
                SendId = replied,
                Type = "message.replied",
                Data = JsonSerializer.Serialize(new { inboxId = row.Id }),
            }, ct).ConfigureAwait(false);
        }
        this.log?.Invoke($"Delivered {rcpt} -> the inbox of the application {app.Name} ({written.SizeBytes} bytes, encrypted to {key.Fingerprint[..System.Math.Min(16, key.Fingerprint.Length)]})");
        Anjal.Smtp.Counters.Increment("anjal_app_inbox_delivered_total");
        return (true, false, null, app.Name);
    }

    // The Message-IDs a mail answers: In-Reply-To first, then References from the newest.
    private static System.Collections.Generic.IEnumerable<string> ReferencedIds(Anjal.Mime.MimeMessage? parsed)
    {
        if (parsed is null)
        {
            yield break;
        }
        string chain = (parsed.Headers.Get("In-Reply-To") ?? string.Empty) + " " + string.Join(" ", (parsed.Headers.Get("References") ?? string.Empty).Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Reverse());
        foreach (string token in chain.Split(ChainSeparators, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string id = token.Trim().Trim('<', '>');
            if (id.Contains('@', System.StringComparison.Ordinal))
            {
                yield return id;
            }
        }
    }

    // The SPF, DKIM and DMARC results this server wrote (the topmost Authentication-Results).
    private static System.Collections.Generic.Dictionary<string, string> AuthResultsOf(Anjal.Mime.MimeMessage? parsed)
    {
        var result = new System.Collections.Generic.Dictionary<string, string> { ["spf"] = "none", ["dkim"] = "none", ["dmarc"] = "none" };
        string header = parsed?.Headers.Get("Authentication-Results") ?? string.Empty;
        foreach (string piece in header.Split(AuthSeparators, System.StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = piece.IndexOf('=', System.StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            string name = piece[..eq].Trim().ToLowerInvariant();
            if (name is "spf" or "dkim" or "dmarc" && result[name] == "none")
            {
                result[name] = piece[(eq + 1)..].Trim().ToLowerInvariant();
            }
        }
        return result;
    }

    // DES-14 §17: a report to a send's return path. Null when taken; else the refusal.
    private async System.Threading.Tasks.Task<string?> TakeReportAsync(IAppApiStore apps, string rcpt, Anjal.Smtp.DeliveryContext ctx, Anjal.Mime.MimeMessage? parsed, System.Threading.CancellationToken ct)
    {
        const string onlyReports = "This address takes delivery reports only";
        if (AppBounceAddress.SendOf(rcpt) is not System.Guid sid || await apps.GetAppSendAsync(sid, ct).ConfigureAwait(false) is not AppSend send)
        {
            return "No such mailbox";
        }
        Anjal.Mime.ContentType type = parsed?.Body.ContentType ?? Anjal.Mime.ContentType.Default();
        string reportType = type.Parameters.TryGetValue("report-type", out string? rt) ? rt.Trim('"').ToLowerInvariant() : string.Empty;
        if (ctx.AuthenticatedUser is not null || parsed?.Body is not Anjal.Mime.MimeMultipart multi || type.MimeType != "multipart/report"
            || reportType is not ("delivery-status" or "feedback-report"))
        {
            this.log?.Invoke($"Mailbox: {rcpt} refused: not a delivery or feedback report");
            Anjal.Smtp.Counters.Increment("anjal_bounce_refused_total");
            return onlyReports;
        }
        System.DateTimeOffset now = System.DateTimeOffset.UtcNow;
        if (reportType == "feedback-report")
        {
            string fields = PartText(multi, "message/feedback-report");
            string to = FieldOf(fields, "Original-Rcpt-To");
            if (to.Length == 0 && send.To.Count == 1)
            {
                to = send.To[0];
            }
            to = StripAddressType(to);
            // A report counts only for an address the send went to: anyone can write to the return
            // path, so a report naming someone else must not put them on the do-not-send list.
            if (!SentTo(send, to))
            {
                this.log?.Invoke($"Mailbox: {rcpt}: a feedback report for an address the send did not go to; ignored");
                Anjal.Smtp.Counters.Increment("anjal_bounce_refused_total");
                return null;
            }
            await apps.AddAppEventAsync(new AppEvent
            {
                TenantId = send.TenantId,
                AppId = send.AppId,
                SendId = send.Id,
                Type = "message.complained",
                At = now,
                Data = JsonSerializer.Serialize(new { to, feedbackType = FieldOf(fields, "Feedback-Type"), source = "arf" }),
            }, ct).ConfigureAwait(false);
            {
                await AppSuppressions.AddAsync(this.store, apps, send.TenantId, send.AppId, to, "complaint", "Marked as spam", DoNotSend.ByAnjal, send.Id, now, ct).ConfigureAwait(false);
            }
            Anjal.Smtp.Counters.Increment("anjal_app_complaints_total");
            return null;
        }
        string status = PartText(multi, "message/delivery-status");
        // The per-recipient blocks follow the per-message block, each after a blank line.
        foreach (string block in status.Replace("\r\n", "\n", System.StringComparison.Ordinal).Split("\n\n", System.StringSplitOptions.RemoveEmptyEntries))
        {
            string code = FieldOf(block, "Status");
            string to = StripAddressType(FieldOf(block, "Final-Recipient"));
            if (code.Length == 0 || !SentTo(send, to))
            {
                continue;
            }
            string diagnostic = FieldOf(block, "Diagnostic-Code");
            bool hard = code.StartsWith('5');
            if (!hard && !code.StartsWith('4'))
            {
                continue;
            }
            await apps.AddAppEventAsync(new AppEvent
            {
                TenantId = send.TenantId,
                AppId = send.AppId,
                SendId = send.Id,
                Type = hard ? "message.bounced" : "message.deferred",
                At = now,
                Data = JsonSerializer.Serialize(new { to, kind = hard ? "hard" : "soft", status = code, diagnostic, source = "dsn" }),
            }, ct).ConfigureAwait(false);
            if (hard)
            {
                await AppSuppressions.AddAsync(this.store, apps, send.TenantId, send.AppId, to, "hard_bounce", DoNotSend.HardReason(diagnostic) ?? "Bounced (" + code + ")", DoNotSend.ByAnjal, send.Id, now, ct).ConfigureAwait(false);
            }
        }
        Anjal.Smtp.Counters.Increment("anjal_app_reports_total");
        return null;
    }

    private static string PartText(Anjal.Mime.MimeMultipart multi, string mimeType)
    {
        foreach (Anjal.Mime.MimeEntity part in multi.Parts)
        {
            if (part is Anjal.Mime.MimePart p && p.ContentType.MimeType == mimeType)
            {
                return System.Text.Encoding.UTF8.GetString(p.Body);
            }
        }
        return string.Empty;
    }

    // A header-like field's value in a report block (unfolded), or "".
    private static string FieldOf(string block, string name)
    {
        foreach (string line in block.Replace("\r\n", "\n", System.StringComparison.Ordinal).Replace("\n ", " ", System.StringComparison.Ordinal).Replace("\n\t", " ", System.StringComparison.Ordinal).Split('\n'))
        {
            int colon = line.IndexOf(':', System.StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon].Trim(), name, System.StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }
        return string.Empty;
    }

    // "rfc822; person@example.com" gives "person@example.com".
    // Whether a send went to an address (a report about anyone else is ignored).
    private static bool SentTo(AppSend send, string address) =>
        address.Length > 0 && send.To.Exists(t => string.Equals(t.Trim(), address, System.StringComparison.OrdinalIgnoreCase));

    private static string StripAddressType(string value)
    {
        int semi = value.IndexOf(';', System.StringComparison.Ordinal);
        return (semi >= 0 ? value[(semi + 1)..] : value).Trim().Trim('<', '>').ToLowerInvariant();
    }

    // A header only Anjal writes is removed from mail as it arrives.
    private static byte[] RemoveHeader(byte[] raw, string name)
    {
        string head = System.Text.Encoding.Latin1.GetString(raw, 0, System.Math.Min(raw.Length, 65536));
        int end = head.IndexOf("\r\n\r\n", System.StringComparison.Ordinal);
        if (end < 0 || head[..end].IndexOf(name + ":", System.StringComparison.OrdinalIgnoreCase) < 0)
        {
            return raw;
        }
        var kept = new System.Text.StringBuilder();
        bool skipping = false;
        foreach (string line in head[..end].Split("\r\n"))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (!skipping)
                {
                    kept.Append(line).Append("\r\n");
                }
                continue;
            }
            skipping = line.StartsWith(name + ":", System.StringComparison.OrdinalIgnoreCase);
            if (!skipping)
            {
                kept.Append(line).Append("\r\n");
            }
        }
        byte[] headBytes = System.Text.Encoding.Latin1.GetBytes(kept.ToString());
        return Prepend(headBytes, raw[(end + 2)..]);
    }

    // The holder's copy of a shared address's mail names the application (DES-14 §13).
    private static byte[] WithAlsoDelivered(byte[] raw, string appName) =>
        Prepend(System.Text.Encoding.UTF8.GetBytes(AlsoDeliveredHeader + ": " + EncodeHeader(appName) + "\r\n"), raw);
}
