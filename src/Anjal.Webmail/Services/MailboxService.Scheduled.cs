using System.Collections.Concurrent;
using System.Globalization;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>One message in the Outbox: still being delivered to someone outside (item 9).</summary>
/// <param name="Id">The queue row.</param>
/// <param name="To">The recipient.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Queued">When it was sent.</param>
/// <param name="Attempts">Tries so far.</param>
/// <param name="NextTry">When the next try is due.</param>
/// <param name="GiveUp">When Anjal stops trying and tells the sender.</param>
/// <param name="LastReply">What the receiving server said last.</param>
/// <param name="Text">The message's text, to show it.</param>
public sealed record OutboxItem(Guid Id, string To, string Subject, DateTimeOffset Queued, int Attempts, DateTimeOffset NextTry, DateTimeOffset GiveUp, string LastReply, string Text);

/// <summary>
/// Send later, undo send and the Outbox (rc.12, items 8, 9 and UX-02).
/// A message sent with a delay, or for a later time, waits in the
/// Scheduled folder - a real folder, so it is kept like any other mail and
/// survives a restart - with its send time as its Date line. The sender in
/// the background sends it when the time comes; Undo puts it back in Drafts.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The folder holding mail waiting for its send time.</summary>
    public const string ScheduledFolder = "Scheduled";

    /// <summary>How long Send waits so it can be undone (UX-02): ten seconds.</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    /// <summary>The kind of the document holding notices for the person (a held message that could not be sent).</summary>
    public const string NoticesKind = "notices";

    // Held messages this process knows are waiting: id -> (mailbox, send time).
    private readonly ConcurrentDictionary<Guid, (Guid Mailbox, DateTimeOffset At)> waiting = new();

    /// <summary>
    /// Keep a message to be sent at a time: in ten seconds for Send with
    /// undo, or later for Send later. Checks first what would stop it being
    /// sent, so the person is told at once. Returns the error, or the id of
    /// the waiting message.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="request">The compose form, attachments already read.</param>
    /// <param name="sendAt">When to send.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, Guid? Id)> HoldAsync(Guid mailboxId, ComposeRequest request, DateTimeOffset sendAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return ("Mailbox is not available.", null);
        }
        if ((await StoragePlan.StateOfAsync(this.store, context.Value.Mailbox, ct).ConfigureAwait(false)).Full)
        {
            return (FullToSend, null);
        }
        if (CheckForSend(request) is string notReady)
        {
            return (notReady, null);
        }
        Guid? id = await this.SaveComposeCopyAsync(mailboxId, request, ScheduledFolder, sendAt, ct).ConfigureAwait(false);
        if (id is Guid held)
        {
            this.waiting[held] = (mailboxId, sendAt);
        }
        return (id is null ? "The message could not be kept for sending. Please try again." : null, id);
    }

    /// <summary>
    /// Undo: the waiting message goes back to Drafts, to be edited. Returns
    /// its id there, or null when it has already gone.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="heldId">The waiting message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Guid?> UnholdAsync(Guid mailboxId, Guid heldId, CancellationToken ct = default)
    {
        if (!await this.IsWaitingAsync(mailboxId, heldId, ct).ConfigureAwait(false))
        {
            return null;
        }
        this.waiting.TryRemove(heldId, out _);
        MessageRow? moved = await this.MoveAsync(mailboxId, heldId, "Drafts", ct).ConfigureAwait(false);
        return moved?.Id;
    }

    /// <summary>
    /// Send a waiting message now ("Send now", or its time has come). On
    /// failure it goes back to Drafts with a notice saying why. Returns the
    /// error, or null when it was sent.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="heldId">The waiting message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> SendHeldAsync(Guid mailboxId, Guid heldId, CancellationToken ct = default)
    {
        // Claimed first, so two passes never send the same message twice.
        if (!this.waiting.TryRemove(heldId, out _) && !await this.IsWaitingAsync(mailboxId, heldId, ct).ConfigureAwait(false))
        {
            return "This message is no longer waiting to be sent.";
        }
        ComposeRequest? request = await this.LoadComposeCopyAsync(mailboxId, heldId, ScheduledFolder, ct).ConfigureAwait(false);
        if (request is null)
        {
            return "This message is no longer waiting to be sent.";
        }
        IReadOnlyList<AttachmentView> files = await this.ListAttachmentsAsync(mailboxId, heldId, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>();
        request.DraftId = heldId;
        request.CarryFrom = heldId;
        foreach (AttachmentView a in files)
        {
            request.CarryIndexes.Add(a.Index);
        }
        string? error = await this.AddCarriedAttachmentsAsync(mailboxId, request, ct).ConfigureAwait(false)
            ?? await this.SendAsync(mailboxId, request, ct).ConfigureAwait(false);
        if (error is not null)
        {
            await this.MoveAsync(mailboxId, heldId, "Drafts", ct).ConfigureAwait(false);
            string subject = request.Subject.Trim().Length > 0 ? request.Subject.Trim() : "(no subject)";
            await this.AddNoticeAsync(mailboxId, $"\"{subject}\" was not sent: {error} It is in Drafts.", ct).ConfigureAwait(false);
        }
        return error;
    }

    /// <summary>
    /// Change when a waiting message is sent. Returns the error, or null.
    /// The message keeps its id.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="heldId">The waiting message.</param>
    /// <param name="sendAt">The new time.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(string? Error, Guid? Id)> RescheduleAsync(Guid mailboxId, Guid heldId, DateTimeOffset sendAt, CancellationToken ct = default)
    {
        ComposeRequest? request = await this.LoadComposeCopyAsync(mailboxId, heldId, ScheduledFolder, ct).ConfigureAwait(false);
        if (request is null)
        {
            return ("This message is no longer waiting to be sent.", null);
        }
        this.waiting.TryRemove(heldId, out _);
        IReadOnlyList<AttachmentView> files = await this.ListAttachmentsAsync(mailboxId, heldId, ct).ConfigureAwait(false) ?? Array.Empty<AttachmentView>();
        request.CarryFrom = heldId;
        foreach (AttachmentView a in files)
        {
            request.CarryIndexes.Add(a.Index);
        }
        await this.AddCarriedAttachmentsAsync(mailboxId, request, ct).ConfigureAwait(false);
        request.DraftId = heldId;
        return await this.HoldAsync(mailboxId, request, sendAt, ct).ConfigureAwait(false);
    }

    /// <summary>When a waiting message will be sent: its Date line.</summary>
    /// <param name="row">The message in Scheduled.</param>
    public static DateTimeOffset? SendTimeOf(MessageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string s = row.DateHeader.Trim();
        // "Sun, 27 Sep 2026 23:24:33 +0530": the offset needs a colon for "zzz".
        if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-'))
        {
            s = s[..^2] + ":" + s[^2..];
        }
        return DateTimeOffset.TryParseExact(s, "ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) ? at : null;
    }

    /// <summary>
    /// Send every waiting message whose time has come. Called by the sender
    /// in the background every few seconds; with <paramref name="rescan"/>
    /// it first reads every mailbox's Scheduled folder (at start, and now and
    /// then, so nothing kept before a restart is forgotten).
    /// </summary>
    /// <param name="now">The present moment.</param>
    /// <param name="rescan">Read the Scheduled folders again.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were sent.</returns>
    public async Task<int> SendDueAsync(DateTimeOffset now, bool rescan, CancellationToken ct = default)
    {
        if (rescan)
        {
            foreach (MailboxRow mb in await this.store.ListMailboxesAsync(null, ct).ConfigureAwait(false))
            {
                FolderRow? scheduled = (await this.store.ListFoldersAsync(mb.Id, ct).ConfigureAwait(false))
                    .FirstOrDefault(f => f.Name == ScheduledFolder);
                if (scheduled is null)
                {
                    continue;
                }
                foreach (MessageRow row in await this.store.ListMessagesAsync(mb.Id, scheduled.Id, 1000, 0, ct).ConfigureAwait(false))
                {
                    this.waiting[row.Id] = (mb.Id, SendTimeOf(row) ?? now);
                }
            }
        }
        int sent = 0;
        foreach (KeyValuePair<Guid, (Guid Mailbox, DateTimeOffset At)> due in this.waiting.Where(w => w.Value.At <= now).ToList())
        {
            if (await this.SendHeldAsync(due.Value.Mailbox, due.Key, ct).ConfigureAwait(false) is null)
            {
                sent++;
            }
        }
        return sent;
    }

    /// <summary>The Outbox: this mailbox's mail still being delivered outside, oldest first.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<OutboxItem>> ListOutboxAsync(Guid mailboxId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<OutboxItem>();
        }
        IReadOnlyList<OutboundMessage> rows = await this.messageStore.ListOutboundForSenderAsync(context.Value.Mailbox.Address, ct).ConfigureAwait(false);
        return rows.Select(r =>
        {
            MimeMessage? parsed = TryParse(r.RawBytes);
            return new OutboxItem(
                r.Id,
                r.EnvelopeTo,
                EncodedWordDecoder.Decode(parsed?.Subject ?? string.Empty),
                r.CreatedAt,
                r.Attempts,
                r.NextAttemptAt,
                r.GiveUpAt,
                r.LastError,
                FirstPlainText(parsed?.Body));
        }).ToList();
    }

    /// <summary>How many messages are in the Outbox.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<int> CountOutboxAsync(Guid mailboxId, CancellationToken ct = default) =>
        (await this.ListOutboxAsync(mailboxId, ct).ConfigureAwait(false)).Count;

    /// <summary>Stop delivering one Outbox message to its recipient.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="outboundId">The Outbox row.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> CancelOutboxAsync(Guid mailboxId, Guid outboundId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        return context is not null && await this.messageStore.CancelOutboundAsync(outboundId, context.Value.Mailbox.Address, ct).ConfigureAwait(false);
    }

    /// <summary>Try one Outbox message again now.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="outboundId">The Outbox row.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> RetryOutboxAsync(Guid mailboxId, Guid outboundId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        return context is not null && await this.messageStore.RetryOutboundNowAsync(outboundId, context.Value.Mailbox.Address, ct).ConfigureAwait(false);
    }

    /// <summary>Raw bytes of one Outbox message, for showing it; null when not this mailbox's.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="outboundId">The Outbox row.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<OutboundMessage?> GetOutboxMessageAsync(Guid mailboxId, Guid outboundId, CancellationToken ct = default)
    {
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        OutboundMessage? row = await this.messageStore.GetOutboundByIdAsync(outboundId, ct).ConfigureAwait(false);
        return context is not null && row is not null && string.Equals(row.EnvelopeFrom, context.Value.Mailbox.Address, StringComparison.OrdinalIgnoreCase)
            ? row
            : null;
    }

    /// <summary>Leave a notice for the person, shown once on their next page.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="text">The notice.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task AddNoticeAsync(Guid mailboxId, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> all = await this.ReadDocumentAsync<List<string>>(mailboxId, NoticesKind, ct).ConfigureAwait(false) ?? new List<string>();
        all.Add(text);
        await this.WriteDocumentAsync(mailboxId, NoticesKind, all.TakeLast(20).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>Take the notices waiting for the person: returned once, then cleared.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<string>> TakeNoticesAsync(Guid mailboxId, CancellationToken ct = default)
    {
        List<string>? all = await this.ReadDocumentAsync<List<string>>(mailboxId, NoticesKind, ct).ConfigureAwait(false);
        if (all is null || all.Count == 0)
        {
            return Array.Empty<string>();
        }
        await this.WriteDocumentAsync(mailboxId, NoticesKind, new List<string>(), ct).ConfigureAwait(false);
        return all;
    }

    /// <summary>
    /// The choices Send later offers (the board): tomorrow at 09:00, and
    /// next Monday at 09:00, in the person's own time zone.
    /// </summary>
    /// <param name="clock">The person's clock.</param>
    /// <param name="now">The present moment.</param>
    public static (DateTimeOffset Tomorrow, DateTimeOffset Monday) SendLaterChoices(ZonedClock clock, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTimeOffset local = clock.Local(now);
        DateTime tomorrow = local.Date.AddDays(1).AddHours(9);
        int toMonday = ((int)DayOfWeek.Monday - (int)local.DayOfWeek + 7) % 7;
        DateTime monday = local.Date.AddDays(toMonday == 0 ? 7 : toMonday).AddHours(9);
        return (AtLocal(clock, tomorrow), AtLocal(clock, monday));
    }

    /// <summary>A wall-clock time in the person's zone, as an instant.</summary>
    /// <param name="clock">The person's clock.</param>
    /// <param name="local">The wall-clock time.</param>
    public static DateTimeOffset AtLocal(ZonedClock clock, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(clock);
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset = clock.Zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }

    private async Task<bool> IsWaitingAsync(Guid mailboxId, Guid heldId, CancellationToken ct)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? loaded = await this.ReadRawAsync(mailboxId, heldId, ct).ConfigureAwait(false);
        return loaded is not null && loaded.Value.Folder.Name == ScheduledFolder;
    }
}

/// <summary>Sends waiting messages when their time comes (rc.12, items 8 and UX-02).</summary>
internal sealed class ScheduledSender : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly MailboxService mail;
    private readonly string maildirRoot;

    public ScheduledSender(MailboxService mail, string maildirRoot)
    {
        this.mail = mail;
        this.maildirRoot = maildirRoot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset lastScan = DateTimeOffset.MinValue;
        // rc.13: Trash and Junk are emptied of old messages once an hour, the first time a few minutes after start.
        DateTimeOffset lastEmptied = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(55);
        // DES-11 F4: the operators' summary and alerts, checked every 15 minutes, the first time two minutes after start.
        DateTimeOffset lastOperatorMail = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(13);
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool rescan = now - lastScan > TimeSpan.FromMinutes(5);
            try
            {
                await this.mail.SendDueAsync(now, rescan, stoppingToken).ConfigureAwait(false);
                if (rescan)
                {
                    lastScan = now;
                }
                if (now - lastOperatorMail > TimeSpan.FromMinutes(15))
                {
                    lastOperatorMail = now;
                    int mails = await this.mail.OperatorMailAsync(this.maildirRoot, now, stoppingToken).ConfigureAwait(false);
                    if (mails > 0)
                    {
                        Console.WriteLine($"[operators] {mails} summary or alert mail(s) sent.");
                    }
                }
                if (now - lastEmptied > TimeSpan.FromHours(1))
                {
                    lastEmptied = now;
                    int emptied = await this.mail.EmptyOldAsync(now, stoppingToken).ConfigureAwait(false);
                    if (emptied > 0)
                    {
                        Console.WriteLine($"[emptying] {emptied} message(s) deleted from Trash and Junk after their time.");
                    }
                    // rc.14: an application's old key stops when its rotation overlap ends.
                    int retired = await this.mail.RetireEndedKeysAsync(now, stoppingToken).ConfigureAwait(false);
                    if (retired > 0)
                    {
                        Console.WriteLine($"[keys] {retired} rotated application key(s) stopped working, as planned.");
                    }
                    await this.DailyAsync(now, stoppingToken).ConfigureAwait(false);
                    // DES-11 D2: storage warnings at 80, 90 and 100%.
                    int warned = await this.mail.StorageWarningsAsync(stoppingToken).ConfigureAwait(false);
                    if (warned > 0)
                    {
                        Console.WriteLine($"[storage] {warned} storage warning(s) sent.");
                    }
                }
            }
#pragma warning disable CA1031 // A failed pass is tried again on the next one; the sender never stops.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[scheduled] {ex.GetType().Name}: {ex.Message}");
            }
#pragma warning restore CA1031
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // rc.15 (items 59 and 61), each at most once a day: every organisation's DNS records
    // checked (ANJAL_DNS_CHECK=off stops it), the disk record's line, and the IP location
    // list refreshed monthly in the background.
    private async Task DailyAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ANJAL_DNS_CHECK"), "off", StringComparison.OrdinalIgnoreCase))
            {
                int checkedOrgs = await this.mail.CheckDnsDailyAsync(now, name => Anjal.Webmail.DnsLookup.TxtAsync(name, ct), ct).ConfigureAwait(false);
                if (checkedOrgs > 0)
                {
                    Console.WriteLine($"[dns] the records of {checkedOrgs} organisation(s) checked.");
                }
            }
            await this.mail.RecordDiskTodayAsync(now, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The daily work is tried again within the hour; sending never waits for it.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[daily] {ex.GetType().Name}: {ex.Message}");
        }
#pragma warning restore CA1031
        _ = IpLocations.RefreshIfDueAsync(now, Console.WriteLine, ct);
    }
}
