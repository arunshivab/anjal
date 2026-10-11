using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>rc.15 (item 63): what the person said about replies to mail they sent.</summary>
public sealed class ReplyMarks
{
    /// <summary>Messages marked "Expect a reply" when written, matched later by recipient and subject.</summary>
    public IList<ExpectMark> Expect { get; init; } = new List<ExpectMark>();

    /// <summary>Sent messages marked "No reply needed".</summary>
    public IList<Guid> NoReplyNeeded { get; init; } = new List<Guid>();
}

/// <summary>One "Expect a reply": the message is told apart by its first recipient and subject.</summary>
public sealed class ExpectMark
{
    /// <summary>The first address in To.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>The subject as written.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>When it was marked.</summary>
    public DateTimeOffset At { get; set; }
}

/// <summary>rc.15 (item 63): "Expect a reply" and "No reply needed".</summary>
public sealed partial class MailboxService
{
    /// <summary>The kind of the reply-marks document.</summary>
    public const string ReplyMarksKind = "reply-marks";

    /// <summary>The person's reply marks.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<ReplyMarks> GetReplyMarksAsync(Guid mailboxId, CancellationToken ct = default) =>
        await this.ReadDocumentAsync<ReplyMarks>(mailboxId, ReplyMarksKind, ct).ConfigureAwait(false) ?? new ReplyMarks();

    /// <summary>
    /// "Expect a reply" on a message being sent: it counts on the dashboard after three days
    /// without an answer, even when it went only to Cc or ends with a short closing line.
    /// </summary>
    /// <param name="mailboxId">The sending mailbox.</param>
    /// <param name="to">The To line as written.</param>
    /// <param name="subject">The subject as written.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task MarkExpectReplyAsync(Guid mailboxId, string to, string subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(subject);
        IReadOnlyList<Anjal.Mime.MailAddress> parsed = Anjal.Mime.AddressParser.Parse(to);
        if (parsed.Count == 0)
        {
            return;
        }
        ReplyMarks marks = await this.GetReplyMarksAsync(mailboxId, ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ((List<ExpectMark>)marks.Expect).RemoveAll(m => m.At < now.AddDays(-60));
        marks.Expect.Add(new ExpectMark { To = parsed[0].Address, Subject = subject.Trim(), At = now });
        await this.WriteDocumentAsync(mailboxId, ReplyMarksKind, marks, ct).ConfigureAwait(false);
    }

    /// <summary>"No reply needed" on a sent message: it leaves the dashboard's count.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="messageId">The sent message.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task MarkNoReplyNeededAsync(Guid mailboxId, Guid messageId, CancellationToken ct = default)
    {
        ReplyMarks marks = await this.GetReplyMarksAsync(mailboxId, ct).ConfigureAwait(false);
        if (!marks.NoReplyNeeded.Contains(messageId))
        {
            marks.NoReplyNeeded.Add(messageId);
            while (marks.NoReplyNeeded.Count > 5000)
            {
                marks.NoReplyNeeded.RemoveAt(0);
            }
            await this.WriteDocumentAsync(mailboxId, ReplyMarksKind, marks, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether a sent message was marked "Expect a reply".</summary>
    /// <param name="marks">The person's marks.</param>
    /// <param name="m">The sent message.</param>
    internal static bool Expected(ReplyMarks marks, MessageRow m) =>
        marks.Expect.Any(e => m.ReceivedAt >= e.At.AddMinutes(-2)
            && m.ToHeader.Contains(e.To, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Subject.Trim(), e.Subject, StringComparison.Ordinal));
}
