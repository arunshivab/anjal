using System.Globalization;
using System.Text;
using Anjal.Mailbox;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.22 (item 51, mockup 25): the one-press Unsubscribe on a newsletter. A newsletter is recognised
/// by its List-Unsubscribe header (RFC 2369); pressing the button does what the header offers - a
/// mail to its mailto: address, or, with List-Unsubscribe-Post (RFC 8058), one POST to its web
/// address - and nothing else.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The audit action written when a person unsubscribes.</summary>
    public const string UnsubscribeAudit = "webmail.unsubscribed";

    private static readonly HttpClient OneClick = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>What a message's List-Unsubscribe header offers: a mailto: address and a web address, either empty.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The offers, or null when the message has no List-Unsubscribe header.</returns>
    public async Task<(string Mailto, string Link)?> UnsubscribeOfAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }
        string header = HeaderOf(found.Value.Raw, "List-Unsubscribe");
        if (header.Length == 0)
        {
            return null;
        }
        (string mailto, string link) = ParseUnsubscribe(header);
        return mailto.Length == 0 && link.Length == 0 ? null : (mailto, link);
    }

    /// <summary>The mailto: and https: addresses in a List-Unsubscribe header value.</summary>
    /// <param name="header">The header's value, for example "&lt;mailto:leave@x.test?subject=stop&gt;, &lt;https://x.test/leave/1&gt;".</param>
    /// <returns>The addresses, either empty.</returns>
    public static (string Mailto, string Link) ParseUnsubscribe(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        string mailto = string.Empty;
        string link = string.Empty;
        foreach (string piece in header.Split(','))
        {
            string p = piece.Trim().Trim('<', '>').Trim();
            if (p.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && mailto.Length == 0)
            {
                mailto = p[7..];
            }
            else if (p.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && link.Length == 0 && Uri.TryCreate(p, UriKind.Absolute, out Uri? u) && u.Scheme == Uri.UriSchemeHttps)
            {
                link = u.ToString();
            }
        }
        return (mailto, link);
    }

    /// <summary>
    /// Unsubscribe from a newsletter the way its header offers: a mail to its mailto: address (with
    /// the subject the header asks for, or "unsubscribe"), else one POST to its web address when it
    /// allows one-click. Returns the error, or null.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The newsletter.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<string?> UnsubscribeAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (found is null || context is null)
        {
            return "That message was not found.";
        }
        (string mailto, string link) = ParseUnsubscribe(HeaderOf(found.Value.Raw, "List-Unsubscribe"));
        MailboxRow mailbox = context.Value.Mailbox;
        if (mailto.Length > 0)
        {
            string address = mailto;
            string subject = "unsubscribe";
            string body = "unsubscribe";
            int q = mailto.IndexOf('?', StringComparison.Ordinal);
            if (q >= 0)
            {
                address = mailto[..q];
                foreach (string pair in mailto[(q + 1)..].Split('&'))
                {
                    int eq = pair.IndexOf('=', StringComparison.Ordinal);
                    string key = eq < 0 ? pair : pair[..eq];
                    string value = Uri.UnescapeDataString((eq < 0 ? string.Empty : pair[(eq + 1)..]).Replace('+', ' '));
                    if (key.Equals("subject", StringComparison.OrdinalIgnoreCase))
                    {
                        subject = value;
                    }
                    else if (key.Equals("body", StringComparison.OrdinalIgnoreCase))
                    {
                        body = value;
                    }
                }
            }
            if (!MailboxSink.TrySplitAddress(address, out string local, out string domain))
            {
                return "The newsletter's unsubscribe address is not usable.";
            }
            address = local + "@" + domain;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var sb = new StringBuilder();
            sb.Append("From: <").Append(mailbox.Address).Append(">\r\n");
            sb.Append("To: <").Append(address).Append(">\r\n");
            sb.Append("Subject: ").Append(MailboxSink.EncodeHeader(Clip(subject, 200))).Append("\r\n");
            sb.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(mailbox.Domain).Append(">\r\n");
            sb.Append("MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n");
            sb.Append(Clip(body, 2000)).Append("\r\n");
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            var sink = new MailboxSink(this.store, this.maildir);
            if (await sink.ResolveAsync(address, ct).ConfigureAwait(false) is not null)
            {
                await sink.DeliverAsync(new Anjal.Smtp.DeliveryContext { EnvelopeFrom = mailbox.Address, EnvelopeTo = new[] { address }, RawBytes = raw, AuthenticatedUser = mailbox.Address }, ct).ConfigureAwait(false);
            }
            else
            {
                await this.messageStore.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = mailbox.Address, EnvelopeTo = address, RawBytes = raw, CreatedAt = now, NextAttemptAt = now, GiveUpAt = now + OutboundMessage.DefaultGiveUp }, ct).ConfigureAwait(false);
            }
            // rc.26: the audit line no longer carries the mail's subject (since rc.25 Anjal must not keep it readable).
            await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = mailbox.Address, Action = UnsubscribeAudit, Subject = address, Detail = string.Empty }, ct).ConfigureAwait(false);
            await this.RememberListLeftAsync(mailbox.Id, found.Value.Raw, ct).ConfigureAwait(false);
            return null;
        }
        if (link.Length > 0 && HeaderOf(found.Value.Raw, "List-Unsubscribe-Post").Contains("One-Click", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("List-Unsubscribe", "One-Click") });
                using HttpResponseMessage response = await OneClick.PostAsync(new Uri(link), content, ct).ConfigureAwait(false);
                await this.messageStore.AppendAuditAsync(new AuditEvent { Actor = mailbox.Address, Action = UnsubscribeAudit, Subject = link, Detail = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await this.RememberListLeftAsync(mailbox.Id, found.Value.Raw, ct).ConfigureAwait(false);
                    return null;
                }
                return "The sender's page did not accept the request. Open their link instead.";
            }
            catch (HttpRequestException)
            {
                return "The sender's page could not be reached. Open their link instead.";
            }
            catch (TaskCanceledException)
            {
                return "The sender's page did not answer. Open their link instead.";
            }
        }
        return link.Length > 0 ? "This newsletter offers only its own page: open the link." : "This message offers no way to unsubscribe.";
    }

    /// <summary>
    /// rc.26 (item 73): what the reader's mailing-list card says about a message: the list's name,
    /// how many mails from it came this month, the address it came to, and when the person
    /// unsubscribed; null when the message is not from a list.
    /// </summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="monthStart">The start of the person's month.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The card, or null.</returns>
    public async Task<ListCard?> ListCardAsync(Guid mailboxId, Guid messageId, DateTimeOffset monthStart, CancellationToken ct = default)
    {
        (MessageRow Row, FolderRow Folder, byte[] Raw)? found = await this.ReadRawAsync(mailboxId, messageId, ct).ConfigureAwait(false);
        (TenantRow Tenant, MailboxRow Mailbox)? context = await this.GetContextAsync(mailboxId, ct).ConfigureAwait(false);
        if (found is null || context is null || TryParse(found.Value.Raw) is not Anjal.Mime.MimeMessage parsed)
        {
            return null;
        }
        string identity = MailboxSink.ListIdentity(parsed);
        if (identity.Length == 0)
        {
            return null;
        }
        string mark = MailboxSink.ListMarkOf(mailboxId, identity, MailKeys.PepperFromEnvironment());
        string listId = (parsed.Headers.Get("List-Id") ?? string.Empty).Trim();
        int lt = listId.LastIndexOf('<');
        string name = lt > 0 ? listId[..lt].Trim().Trim('"') : string.Empty;
        if (name.Length == 0)
        {
            IReadOnlyList<Anjal.Mime.MailAddress> senders = Anjal.Mime.AddressParser.Parse(found.Value.Row.FromHeader);
            name = senders.Count > 0 && !string.IsNullOrWhiteSpace(senders[0].DisplayName) ? senders[0].DisplayName : identity;
        }
        long count = await this.store.CountListMailAsync(mailboxId, mark, monthStart, ct).ConfigureAwait(false);
        string me = context.Value.Mailbox.Address;
        string to = (HeaderOf(found.Value.Raw, "To") + "," + HeaderOf(found.Value.Raw, "Cc")).Contains(me, StringComparison.OrdinalIgnoreCase)
            ? me
            : Anjal.Mime.AddressParser.Parse(found.Value.Row.ToHeader).Select(a => a.Address).FirstOrDefault() ?? me;
        DateTimeOffset? left = null;
        if (await this.ReadDocumentAsync<Dictionary<string, DateTimeOffset>>(mailboxId, MailboxSink.ListsLeftKind, ct).ConfigureAwait(false) is { } lists && lists.TryGetValue(mark, out DateTimeOffset when))
        {
            left = when;
        }
        return new ListCard(Clip(name, 80), Math.Max(1, count), to, left);
    }

    // rc.26 (item 73): later mail from this list goes to Junk.
    private async Task RememberListLeftAsync(Guid mailboxId, byte[] raw, CancellationToken ct)
    {
        string identity = MailboxSink.ListIdentity(TryParse(raw));
        if (identity.Length == 0)
        {
            return;
        }
        Dictionary<string, DateTimeOffset> lists = await this.ReadDocumentAsync<Dictionary<string, DateTimeOffset>>(mailboxId, MailboxSink.ListsLeftKind, ct).ConfigureAwait(false) ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        lists[MailboxSink.ListMarkOf(mailboxId, identity, MailKeys.PepperFromEnvironment())] = DateTimeOffset.UtcNow;
        await this.WriteDocumentAsync(mailboxId, MailboxSink.ListsLeftKind, lists, ct).ConfigureAwait(false);
    }
}

/// <summary>rc.26 (item 73): the reader's mailing-list card.</summary>
/// <param name="Name">The list's name.</param>
/// <param name="ThisMonth">Mails from it this month.</param>
/// <param name="SentTo">The address it came to.</param>
/// <param name="Left">When the person unsubscribed; null when they have not.</param>
public sealed record ListCard(string Name, long ThisMonth, string SentTo, DateTimeOffset? Left);
