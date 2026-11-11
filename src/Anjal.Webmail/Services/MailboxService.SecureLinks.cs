using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Anjal.Mime;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>A secure link as its page needs it (rc.28, DES-19 §4).</summary>
/// <param name="Link">The link.</param>
/// <param name="SenderName">The sender's name.</param>
/// <param name="SenderAddress">The sender's address.</param>
/// <param name="Masked">The recipient's address with most of its first part hidden.</param>
/// <param name="Organisation">The sender's organisation.</param>
public sealed record SecureLinkPage(SecureLinkRow Link, string SenderName, string SenderAddress, string Masked, string Organisation);

/// <summary>A secure mail, opened for its recipient (rc.28, DES-19 §4).</summary>
public sealed class SecureMailView
{
    /// <summary>From.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>To.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Cc.</summary>
    public string Cc { get; init; } = string.Empty;

    /// <summary>The subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The date.</summary>
    public string Date { get; init; } = string.Empty;

    /// <summary>The words, made safe for a sandboxed frame.</summary>
    public string BodyHtml { get; init; } = string.Empty;

    /// <summary>The files; programs are listed but never given out.</summary>
    public IReadOnlyList<(AttachmentView File, bool Blocked)> Files { get; init; } = Array.Empty<(AttachmentView, bool)>();
}

/// <summary>rc.28 (DES-19, item 74): secure links to outside people.</summary>
public sealed partial class MailboxService
{
    /// <summary>How long a link works.</summary>
    public static readonly TimeSpan SecureLinkLife = TimeSpan.FromDays(7);

    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan SecureCodeLife = TimeSpan.FromMinutes(10);

    /// <summary>How long a visit lasts after a right code.</summary>
    public static readonly TimeSpan SecureVisitLife = TimeSpan.FromMinutes(30);

    /// <summary>How soon a new code can be asked for.</summary>
    public static readonly TimeSpan SecureCodeGap = TimeSpan.FromSeconds(60);

    /// <summary>Wrong codes before a link stops.</summary>
    public const int SecureMaxWrong = 5;

    /// <summary>Codes a link can mail in an hour.</summary>
    public const int SecureCodesPerHour = 5;

    /// <summary>Code requests one address on the internet can make in an hour, over all links.</summary>
    public const int SecureCodesPerIpHour = 20;

    /// <summary>The longest reply, in characters.</summary>
    public const int SecureReplyMax = 20_000;

    /// <summary>The header on a reply that came through a secure link: the link's id.</summary>
    public const string SecureReplyHeader = "X-Anjal-Secure-Reply";

    /// <summary>The page's word when a link no longer works.</summary>
    public const string SecureEnded = "This link has ended.";

    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> SecureCodeRequests = new(StringComparer.Ordinal);

    private ISecureLinkStore? SecureLinks => this.messageStore as ISecureLinkStore;

    /// <summary>Whether this mailbox may send secure links (DES-19 §8, D-341: organisations' people).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it may.</returns>
    public async Task<bool> MaySendSecureLinksAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.SecureLinks is not null
        && (await this.OrganisationDomainsAsync(mailboxId, ct).ConfigureAwait(false)).Count > 0
        && !await this.IsIndividualAsync(mailboxId, ct).ConfigureAwait(false);

    /// <summary>
    /// Make one link per outside address for a mail just sent, and mail each its notice (DES-19 §3): the
    /// notice names the sender and the link, never the mail's subject or words (D-339).
    /// </summary>
    /// <param name="mailbox">The sender's mailbox.</param>
    /// <param name="tenant">The sender's organisation.</param>
    /// <param name="raw">The mail as sent.</param>
    /// <param name="outside">The outside addresses.</param>
    /// <param name="sentBy">The person who sent it.</param>
    /// <param name="sentCopyId">The Sent copy, when kept.</param>
    /// <param name="baseUrl">The webmail's address, for the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The links made.</returns>
    internal async Task<IReadOnlyList<SecureLinkRow>> MakeSecureLinksAsync(MailboxRow mailbox, TenantRow tenant, byte[] raw, IReadOnlyList<MailAddress> outside, Guid? sentBy, Guid? sentCopyId, string baseUrl, CancellationToken ct)
    {
        var made = new List<SecureLinkRow>();
        if (this.SecureLinks is not ISecureLinkStore links)
        {
            return made;
        }
        string messageId = "<" + MessageIdOf(raw) + ">";
        foreach (MailAddress person in outside.DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string token = SecureLinkCrypto.NewToken();
            SecureLinkRow link = await links.AddSecureLinkAsync(new SecureLinkRow
            {
                MailboxId = mailbox.Id,
                SentBy = sentBy,
                Recipient = person.Address.ToLowerInvariant(),
                TokenSha256 = SecureLinkCrypto.Hash(token),
                Sealed = SecureLinkCrypto.Seal(token, raw),
                MessageId = messageId,
                SentCopyId = sentCopyId,
                CreatedAt = now,
                ExpiresAt = now + SecureLinkLife,
            }, ct).ConfigureAwait(false);
            await this.MailSecureNoticeAsync(mailbox, tenant, link, token, baseUrl, ct).ConfigureAwait(false);
            made.Add(await links.UpdateSecureLinkAsync(link.Id, new SecureLinkChange { MailedAt = now }, ct).ConfigureAwait(false) ?? link);
            await this.SecureAuditAsync(mailbox.Address, "made", link, string.Empty, ct).ConfigureAwait(false);
        }
        return made;
    }

    /// <summary>The links a sent mail made (DES-19 §6).</summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="messageId">The mail's Message-ID, with or without its angle brackets.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The links.</returns>
    public async Task<IReadOnlyList<SecureLinkRow>> SecureLinksOfAsync(Guid mailboxId, string? messageId, CancellationToken ct = default)
    {
        if (this.SecureLinks is not ISecureLinkStore links || string.IsNullOrWhiteSpace(messageId))
        {
            return Array.Empty<SecureLinkRow>();
        }
        string id = messageId.Trim();
        id = id.StartsWith('<') ? id : "<" + id + ">";
        return await links.ListSecureLinksForMessageAsync(mailboxId, id, ct).ConfigureAwait(false);
    }

    /// <summary>The sender stops a link at once (DES-19 §6).</summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="linkId">The link.</param>
    /// <param name="actor">Who.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it was this mailbox's link.</returns>
    public async Task<bool> StopSecureLinkAsync(Guid mailboxId, Guid linkId, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (this.SecureLinks is not ISecureLinkStore links || await links.GetSecureLinkAsync(linkId, ct).ConfigureAwait(false) is not SecureLinkRow link || link.MailboxId != mailboxId)
        {
            return false;
        }
        SecureLinkRow? stopped = await links.UpdateSecureLinkAsync(linkId, new SecureLinkChange { StoppedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        await this.SecureAuditAsync(actor, "stopped", stopped ?? link, string.Empty, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// "Send a new link": the old one stops and the same person gets a new one, made from the Sent copy
    /// (the old link's mail cannot be read without its token, which Anjal never kept).
    /// </summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="linkId">The old link.</param>
    /// <param name="actor">Who.</param>
    /// <param name="baseUrl">The webmail's address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> RenewSecureLinkAsync(Guid mailboxId, Guid linkId, string actor, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (this.SecureLinks is not ISecureLinkStore links || await links.GetSecureLinkAsync(linkId, ct).ConfigureAwait(false) is not SecureLinkRow old || old.MailboxId != mailboxId)
        {
            return "This link is not yours.";
        }
        if (old.SentCopyId is not Guid copy || await this.ReadRawAsync(mailboxId, copy, ct).ConfigureAwait(false) is not { } sent)
        {
            return "No copy of this mail was kept, so a new link cannot be made. Write the mail again.";
        }
        if (await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow mailbox))
        {
            return "Mailbox is not available.";
        }
        await links.UpdateSecureLinkAsync(old.Id, new SecureLinkChange { StoppedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        IReadOnlyList<SecureLinkRow> made = await this.MakeSecureLinksAsync(mailbox, tenant, sent.Raw, new[] { new MailAddress(old.Recipient) }, old.SentBy, copy, baseUrl, ct).ConfigureAwait(false);
        await this.SecureAuditAsync(actor, "renewed", old, string.Empty, ct).ConfigureAwait(false);
        return made.Count == 1 ? null : "The new link could not be made.";
    }

    /// <summary>
    /// "Send as a secure link" in the Outbox (DES-19 §2.3, brief 11c): a mail that could not be delivered
    /// to an outside person (for example, held because their server would not encrypt) goes to them as a
    /// secure link instead, and leaves the Outbox.
    /// </summary>
    /// <param name="mailboxId">The sender's mailbox.</param>
    /// <param name="outboundId">The Outbox row.</param>
    /// <param name="actor">Who.</param>
    /// <param name="baseUrl">The webmail's address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> OutboxAsSecureLinkAsync(Guid mailboxId, Guid outboundId, string actor, string baseUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!await this.MaySendSecureLinksAsync(mailboxId, ct).ConfigureAwait(false))
        {
            return "Secure links are for an organisation's people.";
        }
        if (await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow mailbox)
            || await this.GetOutboxMessageAsync(mailboxId, outboundId, ct).ConfigureAwait(false) is not OutboundMessage queued || queued.RawBytes.Length == 0)
        {
            return "This mail is no longer in the Outbox.";
        }
        IReadOnlyList<SecureLinkRow> made = await this.MakeSecureLinksAsync(mailbox, tenant, queued.RawBytes, new[] { new MailAddress(queued.EnvelopeTo) }, mailbox.Id, null, baseUrl, ct).ConfigureAwait(false);
        if (made.Count == 0)
        {
            return "The secure link could not be made.";
        }
        await this.CancelOutboxAsync(mailboxId, outboundId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The page for a link (DES-19 §4); null for a token that is no link.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page's facts.</returns>
    public async Task<SecureLinkPage?> SecureLinkPageAsync(string? token, CancellationToken ct = default)
    {
        if (this.SecureLinks is not ISecureLinkStore links || string.IsNullOrEmpty(token) || SecureLinkCrypto.Hash(token) is not { Length: > 0 } hash
            || await links.GetSecureLinkByTokenAsync(hash, ct).ConfigureAwait(false) is not SecureLinkRow link
            || await this.GetContextAsync(link.MailboxId, ct).ConfigureAwait(false) is not (TenantRow tenant, MailboxRow mailbox))
        {
            return null;
        }
        string name = mailbox.DisplayName.Trim().Length > 0 ? mailbox.DisplayName.Trim() : mailbox.Address;
        return new SecureLinkPage(link, name, mailbox.Address, Mask(link.Recipient), tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : mailbox.Domain);
    }

    /// <summary>Mail a code to the link's recipient (DES-19 §4, step 2).</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="remoteIp">The asker's address on the internet.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null when a code went.</returns>
    public async Task<string?> MailSecureCodeAsync(string? token, string remoteIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (await this.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || this.SecureLinks is not ISecureLinkStore links
            || !page.Link.IsOpenAt(now) || await this.GetContextAsync(page.Link.MailboxId, ct).ConfigureAwait(false) is not (TenantRow _, MailboxRow mailbox))
        {
            return SecureEnded;
        }
        IReadOnlyList<SecureLinkCodeRow> lastHour = await links.ListSecureLinkCodesAsync(page.Link.Id, now.AddHours(-1), ct).ConfigureAwait(false);
        if (lastHour.Count >= SecureCodesPerHour)
        {
            return "Too many codes were asked for. Try again in an hour.";
        }
        // Taken in the store, so two asks at once mail one code.
        if (!await links.TakeSecureCodeMailAsync(page.Link.Id, now, now - SecureCodeGap, ct).ConfigureAwait(false))
        {
            return "A code was mailed a moment ago. Wait a minute before asking for another.";
        }
        if (!TakeIpCode(remoteIp, now))
        {
            return "Too many codes were asked for. Try again in an hour.";
        }
        string code = SecureLinkCrypto.NewCode();
        await links.AddSecureLinkCodeAsync(new SecureLinkCodeRow { LinkId = page.Link.Id, CodeHmac = SecureLinkCrypto.CodeHash(token!, code), MadeAt = now }, ct).ConfigureAwait(false);
        string spaced = code[..3] + " " + code[3..];
        // Anjal's own mail, in English like its other mails (the code is the point of it).
        string text = "Your code for the secure mail from " + page.SenderName + " is:\n\n    " + spaced
            + "\n\nIt works for 10 minutes. If you did not open a link to a secure mail, ignore this mail: nothing is shown without the code";
        string? error = await this.SendSecurityMailAsync(mailbox, page.Link.Recipient, "Your code for the secure mail from " + page.SenderName, text, ct).ConfigureAwait(false);
        if (error is not null)
        {
            return "The code could not be mailed. Try again in a few minutes.";
        }
        await this.SecureAuditAsync(page.Link.Recipient, "code-mailed", page.Link, remoteIp, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>Check a typed code (DES-19 §4, step 3): right once, within 10 minutes; five wrong stop the link.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="code">What was typed.</param>
    /// <param name="remoteIp">The typer's address on the internet.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether it was right, and the tries left when not (0 when the link stopped or ended).</returns>
    public async Task<(bool Right, int Left)> CheckSecureCodeAsync(string? token, string? code, string remoteIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (await this.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || this.SecureLinks is not ISecureLinkStore links || !page.Link.IsOpenAt(now))
        {
            return (false, 0);
        }
        // The try is taken before the code is compared: tries sent at once are each counted, and the
        // fifth is the last however they arrive. A right code gives its try back.
        if (await links.TakeSecureCodeTryAsync(page.Link.Id, SecureMaxWrong, ct).ConfigureAwait(false) is not int tries)
        {
            return (false, 0);
        }
        string typed = new((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (typed.Length == 6)
        {
            string hash = SecureLinkCrypto.CodeHash(token!, typed);
            foreach (SecureLinkCodeRow c in await links.ListSecureLinkCodesAsync(page.Link.Id, now - SecureCodeLife, ct).ConfigureAwait(false))
            {
                if (c.UsedAt is null && SecureLinkCrypto.Same(c.CodeHmac, hash) && await links.UseSecureLinkCodeAsync(c.Id, now, ct).ConfigureAwait(false))
                {
                    await links.UpdateSecureLinkAsync(page.Link.Id, new SecureLinkChange { OpenedAt = now, AddWrongCodes = -1 }, ct).ConfigureAwait(false);
                    await this.SecureAuditAsync(page.Link.Recipient, "opened", page.Link, remoteIp, ct).ConfigureAwait(false);
                    return (true, 0);
                }
            }
        }
        SecureLinkRow after = await links.GetSecureLinkAsync(page.Link.Id, ct).ConfigureAwait(false) ?? page.Link;
        await this.SecureAuditAsync(page.Link.Recipient, "wrong-code", after, remoteIp, ct).ConfigureAwait(false);
        if (tries < SecureMaxWrong)
        {
            return (false, SecureMaxWrong - tries);
        }
        SecureLinkRow locked = await links.UpdateSecureLinkAsync(page.Link.Id, new SecureLinkChange { LockedAt = now }, ct).ConfigureAwait(false) ?? after;
        await this.SecureAuditAsync(page.Link.Recipient, "locked", locked, remoteIp, ct).ConfigureAwait(false);
        if (await this.GetContextAsync(page.Link.MailboxId, ct).ConfigureAwait(false) is (TenantRow _, MailboxRow sender))
        {
            string text = "Someone typed wrong codes " + SecureMaxWrong.ToString(CultureInfo.InvariantCulture) + " times on your secure mail to " + page.Masked
                + ", so its link has stopped. Nothing of the mail was shown.\n\nIf it was them, open the mail in Sent and choose \"Send a new link\"";
            await this.SendSecurityMailAsync(sender, sender.Address, "A secure link has stopped after wrong codes", text, ct).ConfigureAwait(false);
        }
        return (false, 0);
    }

    /// <summary>The mail behind a link, for a visit that typed the right code (DES-19 §4).</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The mail, or null when the link no longer works.</returns>
    public async Task<SecureMailView?> OpenSecureMailAsync(string? token, CancellationToken ct = default)
    {
        if (await this.SecureRawAsync(token, ct).ConfigureAwait(false) is not byte[] raw)
        {
            return null;
        }
        MimeMessage? parsed = TryParse(raw);
        MimePart? html = null;
        MimePart? text = null;
        var files = new List<(MimePart Part, AttachmentView View)>();
        if (parsed is not null)
        {
            Walk(parsed.Body, ref html, ref text, files);
        }
        string body = html is not null ? HtmlSanitizer.Sanitize(html.GetBodyAsText())
            : text is not null ? HtmlSanitizer.FromPlainText(text.GetBodyAsText())
            : HtmlSanitizer.FromPlainText(string.Empty);
        return new SecureMailView
        {
            From = DecodeHeader(parsed, "From", string.Empty),
            To = DecodeHeader(parsed, "To", string.Empty),
            Cc = DecodeHeader(parsed, "Cc", string.Empty),
            Subject = parsed?.Subject ?? string.Empty,
            Date = parsed?.Date ?? string.Empty,
            BodyHtml = body,
            Files = files.Select(f => (f.View, Anjal.Mailbox.AttachmentPolicy.IsProgram(f.View.FileName) || Anjal.Mailbox.AttachmentPolicy.IsProgramContent(f.Part.Body.AsSpan(0, Math.Min(64, f.Part.Body.Length)).ToArray()))).ToList(),
        };
    }

    /// <summary>One file of the mail behind a link; never a program.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="index">The file's place.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The file, or null.</returns>
    public async Task<(string Name, string Type, byte[] Bytes)?> SecureFileAsync(string? token, int index, CancellationToken ct = default)
    {
        if (await this.SecureRawAsync(token, ct).ConfigureAwait(false) is not byte[] raw || TryParse(raw) is not MimeMessage parsed)
        {
            return null;
        }
        MimePart? html = null;
        MimePart? text = null;
        var files = new List<(MimePart Part, AttachmentView View)>();
        Walk(parsed.Body, ref html, ref text, files);
        if (index < 0 || index >= files.Count)
        {
            return null;
        }
        (MimePart part, AttachmentView view) = files[index];
        if (Anjal.Mailbox.AttachmentPolicy.IsProgram(view.FileName) || Anjal.Mailbox.AttachmentPolicy.IsProgramContent(part.Body.AsSpan(0, Math.Min(64, part.Body.Length)).ToArray()))
        {
            return null;
        }
        return (view.FileName, view.ContentType, part.Body);
    }

    /// <summary>
    /// The outside person's reply (DES-19 §5): delivered to the sender's Inbox from the proven address,
    /// marked with the link, files checked as any mail from outside.
    /// </summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="text">The reply.</param>
    /// <param name="files">Files, already within the attachment limit.</param>
    /// <param name="remoteIp">The writer's address on the internet.</param>
    /// <param name="visitStart">When the visit's code was typed; a visit gives one reply.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The error, or null.</returns>
    public async Task<string?> SecureReplyAsync(string? token, string? text, IReadOnlyList<(string FileName, string ContentType, byte[] Bytes)> files, string remoteIp, DateTimeOffset visitStart, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(remoteIp);
        string words = (text ?? string.Empty).Trim();
        if (words.Length == 0 && files.Count == 0)
        {
            return "Write a reply first.";
        }
        if (words.Length > SecureReplyMax)
        {
            return "The reply is too long. Keep it under 20,000 characters.";
        }
        if (await this.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || this.SecureLinks is not ISecureLinkStore links
            || !page.Link.IsOpenAt(DateTimeOffset.UtcNow) || await this.OpenSecureMailAsync(token, ct).ConfigureAwait(false) is not SecureMailView mail
            || await this.GetContextAsync(page.Link.MailboxId, ct).ConfigureAwait(false) is not (TenantRow _, MailboxRow sender))
        {
            return SecureEnded;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!await links.TakeSecureReplyAsync(page.Link.Id, visitStart, now, ct).ConfigureAwait(false))
        {
            return "This visit has sent its reply. To reply again, ask for a new code.";
        }
        string subject = mail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? mail.Subject : "Re: " + mail.Subject;
        var sb = new StringBuilder();
        sb.Append("Received: by ").Append(this.hostName).Append(" (Anjal secure link) with HTTPS from [").Append(remoteIp).Append("];\r\n\t").Append(FormatDate(now)).Append("\r\n");
        sb.Append("From: <").Append(page.Link.Recipient).Append(">\r\n");
        sb.Append("To: ").Append(EncodeHeaderText(page.SenderName)).Append(" <").Append(page.SenderAddress).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeaderText(subject)).Append("\r\n");
        sb.Append("Date: ").Append(FormatDate(now)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(sender.Domain).Append(">\r\n");
        sb.Append("In-Reply-To: ").Append(page.Link.MessageId).Append("\r\nReferences: ").Append(page.Link.MessageId).Append("\r\n");
        sb.Append(SecureReplyHeader).Append(": ").Append(page.Link.Id.ToString("N")).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        string plain = words.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        if (files.Count == 0)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)))).Append("\r\n");
        }
        else
        {
            string boundary = "anjal-" + Guid.NewGuid().ToString("N");
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)))).Append("\r\n");
            foreach ((string FileName, string ContentType, byte[] Bytes) f in files)
            {
                // The visitor's file name and type are theirs to choose: only a plain type/subtype is kept, and a
                // name loses quotes and backslashes, so neither can add a parameter of its own.
                string name = EncodeHeaderText(new string(f.FileName.Where(c => c is not ('"' or '\\') && !char.IsControl(c)).ToArray()));
                string type = PlainMediaType().IsMatch(f.ContentType) ? f.ContentType.ToLowerInvariant() : "application/octet-stream";
                sb.Append("--").Append(boundary).Append("\r\nContent-Type: ").Append(type).Append("; name=\"").Append(name).Append("\"\r\n");
                sb.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Wrap(Convert.ToBase64String(f.Bytes))).Append("\r\n");
            }
            sb.Append("--").Append(boundary).Append("--\r\n");
        }
        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        Anjal.Smtp.DeliveryResult delivered = await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext
        {
            EnvelopeFrom = page.Link.Recipient,
            EnvelopeTo = new[] { page.SenderAddress },
            RawBytes = Encoding.UTF8.GetBytes(sb.ToString()),
            RemoteAddress = remoteIp,
            ClientHostName = "secure-link",
            // It came over HTTPS to this server: recorded as encrypted, and its files checked as any mail from outside.
            TransportTls = SecureReplyTransport,
        }, ct).ConfigureAwait(false);
        if (delivered.Outcome != Anjal.Smtp.DeliveryOutcome.Accepted)
        {
            return "The reply could not be delivered. Try again later.";
        }
        SecureLinkRow after = await links.UpdateSecureLinkAsync(page.Link.Id, new SecureLinkChange { RepliedAt = now, AddReplies = 1 }, ct).ConfigureAwait(false) ?? page.Link;
        await this.SecureAuditAsync(page.Link.Recipient, "replied", after, remoteIp, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// How a reply through a secure link is marked as it is stored. Mail from SMTP is marked with its own
    /// connection's TLS, so a header alone, sent from outside, cannot earn the reply's badge.
    /// </summary>
    public const string SecureReplyTransport = "TLS (HTTPS, secure link)";

    /// <summary>Whether a mail in this mailbox is a reply through one of its secure links (DES-19 §5).</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="headerValue">The <see cref="SecureReplyHeader"/> value.</param>
    /// <param name="from">The mail's From address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when the link is this mailbox's and was made for that address.</returns>
    public async Task<bool> IsSecureReplyAsync(Guid mailboxId, string? headerValue, string? from, CancellationToken ct = default)
    {
        if (this.SecureLinks is not ISecureLinkStore links || !Guid.TryParseExact((headerValue ?? string.Empty).Trim(), "N", out Guid id)
            || await links.GetSecureLinkAsync(id, ct).ConfigureAwait(false) is not SecureLinkRow link)
        {
            return false;
        }
        string address = AddressParser.Parse(from ?? string.Empty) is { Count: > 0 } parsedFrom ? parsedFrom[0].Address : string.Empty;
        return link.MailboxId == mailboxId && string.Equals(link.Recipient, address, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The daily clearing (DES-19 §7, D-342): the sealed copies of links that ended over a day ago.</summary>
    /// <param name="now">The time.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were removed.</returns>
    public async Task<int> PurgeSecureLinksAsync(DateTimeOffset now, CancellationToken ct = default) =>
        this.SecureLinks is ISecureLinkStore links ? await links.PurgeEndedSecureLinksAsync(now.AddDays(-1), now, ct).ConfigureAwait(false) : 0;

    // The address with its first part mostly hidden: p•••@gmail.test.
    internal static string Mask(string address)
    {
        int at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "•••" : address[..1] + "•••" + address[at..];
    }

    private async Task<byte[]?> SecureRawAsync(string? token, CancellationToken ct)
    {
        if (await this.SecureLinkPageAsync(token, ct).ConfigureAwait(false) is not SecureLinkPage page || !page.Link.IsOpenAt(DateTimeOffset.UtcNow))
        {
            return null;
        }
        return SecureLinkCrypto.Open(token!, page.Link.Sealed);
    }

    private async Task MailSecureNoticeAsync(MailboxRow mailbox, TenantRow tenant, SecureLinkRow link, string token, string baseUrl, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string name = mailbox.DisplayName.Trim().Length > 0 ? mailbox.DisplayName.Trim() : mailbox.Address;
        string org = tenant.DisplayName.Trim().Length > 0 ? tenant.DisplayName.Trim() : mailbox.Domain;
        string url = baseUrl.TrimEnd('/') + "/s/" + token;
        string until = link.ExpiresAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string subject = "Secure mail from " + name;
        string text = string.Format(CultureInfo.InvariantCulture,
            "{0} ({1}) sent you a mail through Anjal, kept safe on Anjal's server. To read it, open the link below; Anjal will mail a 6-digit code to this address, and you type it on the page.\n\n    {2}\n\nThe link works until {3}. If you did not expect a mail from {0}, you can ignore this one.",
            name, mailbox.Address, url, until);
        string boundary = "anjal-" + Guid.NewGuid().ToString("N");
        string Crlf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("From: ").Append(EncodeHeaderText(name)).Append(" <").Append(mailbox.Address).Append(">\r\n");
        sb.Append("To: <").Append(link.Recipient).Append(">\r\n");
        sb.Append("Subject: ").Append(EncodeHeaderText(subject)).Append("\r\n");
        sb.Append("Date: ").Append(FormatDate(now)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(mailbox.Domain).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n").Append(Crlf(text)).Append("\r\n");
        sb.Append("--").Append(boundary).Append("\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
        sb.Append(Crlf(SystemMailLayout.Html(SystemMailLayout.Notice, subject, text, org, mailbox.Domain)));
        sb.Append("--").Append(boundary).Append("--\r\n");
        byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
        var sink = new Anjal.Mailbox.MailboxSink(this.store, this.maildir);
        if (await sink.ResolveAsync(link.Recipient, ct).ConfigureAwait(false) is not null)
        {
            await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext { EnvelopeFrom = mailbox.Address, EnvelopeTo = new[] { link.Recipient }, RawBytes = raw, AuthenticatedUser = mailbox.Address }, ct).ConfigureAwait(false);
            return;
        }
        await this.messageStore.EnqueueOutboundAsync(new OutboundMessage
        {
            EnvelopeFrom = mailbox.Address,
            EnvelopeTo = link.Recipient,
            RawBytes = raw,
            CreatedAt = now,
            NextAttemptAt = now,
            GiveUpAt = now + OutboundMessage.DefaultGiveUp,
        }, ct).ConfigureAwait(false);
    }

    // The activity log: the outside address's domain only, never the mail's words (DES-19 §6).
    private Task<AuditEvent> SecureAuditAsync(string actor, string what, SecureLinkRow link, string remoteIp, CancellationToken ct)
    {
        int at = link.Recipient.LastIndexOf('@');
        return this.messageStore.AppendAuditAsync(new AuditEvent
        {
            Actor = actor.Contains('@', StringComparison.Ordinal) && string.Equals(actor, link.Recipient, StringComparison.OrdinalIgnoreCase) ? "outside person at " + actor[(at + 1)..] : actor,
            Action = "securelink." + what,
            Subject = at > 0 ? link.Recipient[(at + 1)..] : link.Recipient,
            Detail = link.MessageId + (what == "wrong-code" ? " · " + link.WrongCodes.ToString(CultureInfo.InvariantCulture) + " wrong" : string.Empty),
            RemoteAddress = remoteIp,
        }, ct);
    }

    private static bool TakeIpCode(string ip, DateTimeOffset now)
    {
        // Now and then, addresses with nothing in the last hour are forgotten, so the list stays small.
        if (SecureCodeRequests.Count > 10_000)
        {
            foreach (KeyValuePair<string, List<DateTimeOffset>> kv in SecureCodeRequests)
            {
                lock (kv.Value)
                {
                    if (kv.Value.TrueForAll(t => now - t > TimeSpan.FromHours(1)))
                    {
                        SecureCodeRequests.TryRemove(kv);
                    }
                }
            }
        }
        List<DateTimeOffset> times = SecureCodeRequests.GetOrAdd(ip, _ => new List<DateTimeOffset>());
        lock (times)
        {
            times.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (times.Count >= SecureCodesPerIpHour)
            {
                return false;
            }
            times.Add(now);
            return true;
        }
    }

    private static string Wrap(string base64)
    {
        var sb = new StringBuilder(base64.Length + (base64.Length / 76 * 2));
        for (int i = 0; i < base64.Length; i += 76)
        {
            sb.Append(base64, i, Math.Min(76, base64.Length - i)).Append("\r\n");
        }
        return sb.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+_-]{0,63}/[A-Za-z0-9][A-Za-z0-9.+_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlainMediaType();
}
