using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anjal.Client;

/// <summary>An address: the address, and a display name or none.</summary>
/// <param name="Email">The address.</param>
/// <param name="Name">The display name, or null.</param>
public sealed record EmailAddress(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string? Name = null);

/// <summary>A file to attach.</summary>
/// <param name="FileName">Its name.</param>
/// <param name="ContentType">Its type, such as application/pdf.</param>
/// <param name="Content">Its bytes.</param>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>A template to send from, in place of a subject, text and HTML (DES-14 §22).</summary>
/// <param name="Name">The template's name.</param>
/// <param name="Values">The values of its placeholders.</param>
/// <param name="Language">The language (en, ta, ml, hi, mr, gu); null for English.</param>
public sealed record TemplateChoice(string Name, IReadOnlyDictionary<string, string> Values, string? Language = null);

/// <summary>One message to send (DES-14 §4).</summary>
public sealed class SendRequest
{
    /// <summary>From: one of the application's allowed senders.</summary>
    public EmailAddress From { get; set; } = new(string.Empty);

    /// <summary>To.</summary>
    public IList<EmailAddress> To { get; } = new List<EmailAddress>();

    /// <summary>Cc.</summary>
    public IList<EmailAddress> Cc { get; } = new List<EmailAddress>();

    /// <summary>Bcc.</summary>
    public IList<EmailAddress> Bcc { get; } = new List<EmailAddress>();

    /// <summary>Reply-To.</summary>
    public IList<EmailAddress> ReplyTo { get; } = new List<EmailAddress>();

    /// <summary>The subject.</summary>
    public string? Subject { get; set; }

    /// <summary>The text.</summary>
    public string? Text { get; set; }

    /// <summary>The HTML.</summary>
    public string? Html { get; set; }

    /// <summary>Files.</summary>
    public IList<EmailAttachment> Attachments { get; } = new List<EmailAttachment>();

    /// <summary>Headers whose names begin X-.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Short words to find the message by. Never patient details.</summary>
    public IList<string> Tags { get; } = new List<string>();

    /// <summary>String pairs that come back with status and events. Never patient details.</summary>
    public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>When to send, up to 7 days ahead; null for now.</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>True to add List-Unsubscribe.</summary>
    public bool Unsubscribe { get; set; }

    /// <summary>A template, in place of Subject, Text and Html.</summary>
    public TemplateChoice? Template { get; set; }

    /// <summary>The body as the API reads it.</summary>
    /// <returns>The JSON object.</returns>
    public Dictionary<string, object?> ToJson()
    {
        var body = new Dictionary<string, object?> { ["from"] = this.From, ["to"] = this.To };
        if (this.Cc.Count > 0)
        {
            body["cc"] = this.Cc;
        }
        if (this.Bcc.Count > 0)
        {
            body["bcc"] = this.Bcc;
        }
        if (this.ReplyTo.Count > 0)
        {
            body["replyTo"] = this.ReplyTo;
        }
        if (this.Template is TemplateChoice t)
        {
            body["template"] = new Dictionary<string, object?> { ["name"] = t.Name, ["language"] = t.Language, ["values"] = t.Values };
        }
        else
        {
            body["subject"] = this.Subject ?? string.Empty;
            if (this.Text is not null)
            {
                body["text"] = this.Text;
            }
            if (this.Html is not null)
            {
                body["html"] = this.Html;
            }
        }
        if (this.Attachments.Count > 0)
        {
            body["attachments"] = this.Attachments.Select(a => new Dictionary<string, string> { ["filename"] = a.FileName, ["contentType"] = a.ContentType, ["content"] = Convert.ToBase64String(a.Content) }).ToList();
        }
        if (this.Headers.Count > 0)
        {
            body["headers"] = this.Headers;
        }
        if (this.Tags.Count > 0)
        {
            body["tags"] = this.Tags;
        }
        if (this.Metadata.Count > 0)
        {
            body["metadata"] = this.Metadata;
        }
        if (this.ScheduledAt is DateTimeOffset at)
        {
            body["scheduledAt"] = at.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (this.Unsubscribe)
        {
            body["unsubscribe"] = true;
        }
        return body;
    }
}

/// <summary>What a send was given (202).</summary>
public sealed class SendResult
{
    /// <summary>The send's id.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>queued, delivered (all here) or suppressed.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>The Message-ID Anjal gave the message.</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>True for a sandbox key's send.</summary>
    [JsonPropertyName("sandbox")]
    public bool Sandbox { get; set; }

    /// <summary>Recipients held back by the do-not-send list.</summary>
    [JsonPropertyName("suppressed")]
    public IReadOnlyList<string> Suppressed { get; set; } = Array.Empty<string>();

    /// <summary>True when Anjal replayed the first answer to this Idempotency-Key.</summary>
    [JsonIgnore]
    public bool Replayed { get; set; }
}

/// <summary>One message's result in a batch (DES-14 §21).</summary>
public sealed class BatchItemResult
{
    /// <summary>Its place in the batch.</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>The send's id, when it was accepted.</summary>
    [JsonPropertyName("id")]
    public Guid? Id { get; set; }

    /// <summary>Its status, when accepted.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Why it was refused, when it was: {code, message, ...}.</summary>
    [JsonPropertyName("error")]
    public JsonElement? Error { get; set; }

    /// <summary>The refusal's code, or null.</summary>
    [JsonIgnore]
    public string? ErrorCode => this.Error is JsonElement e && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;
}

/// <summary>A message's status (DES-14 §8). The subject is not kept: the application knows its own.</summary>
public sealed class MessageStatus
{
    /// <summary>The send's id.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>queued, sending, delivered, deferred, bounced, failed, suppressed.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>From.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Every recipient.</summary>
    [JsonPropertyName("to")]
    public IReadOnlyList<string> To { get; set; } = Array.Empty<string>();

    /// <summary>The tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>The metadata.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

    /// <summary>The last event's type.</summary>
    [JsonPropertyName("lastEvent")]
    public string? LastEvent { get; set; }

    /// <summary>The Message-ID.</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    /// <summary>Each recipient's state, and every delivery attempt with the receiving server's reply.</summary>
    [JsonPropertyName("attempts")]
    public JsonElement? Attempts { get; set; }

    /// <summary>The whole answer, for fields this package does not name.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>One event (DES-14 §8): message.queued, message.delivered, inbox.received and the rest.</summary>
public sealed class AnjalEvent
{
    /// <summary>The event's id (Anjal-Event-Id).</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Its sequence number: pass the last one seen as "since".</summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    /// <summary>Its type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>When.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>The message it is about, with its tags and metadata: {id, tags, metadata}.</summary>
    [JsonPropertyName("message")]
    public JsonElement? Message { get; set; }

    /// <summary>The event's data.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    /// <summary>The send's id, or null.</summary>
    [JsonIgnore]
    public Guid? SendId => this.Message is JsonElement m && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out JsonElement id) ? id.GetGuid() : null;
}

/// <summary>One mail waiting in the application's inbox (DES-14 §14): no subject, which is inside the envelope.</summary>
public sealed class InboxItem
{
    /// <summary>Its id.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>When it arrived.</summary>
    [JsonPropertyName("receivedAt")]
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The address it came to.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>Who sent it.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>Its size.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>The spam score.</summary>
    [JsonPropertyName("spamScore")]
    public int SpamScore { get; set; }

    /// <summary>The SPF, DKIM and DMARC results: {spf, dkim, dmarc}.</summary>
    [JsonPropertyName("auth")]
    public JsonElement? Auth { get; set; }

    /// <summary>The application's send it answers, or null.</summary>
    [JsonPropertyName("inReplyToSend")]
    public string? InReplyToSend { get; set; }

    /// <summary>True once acknowledged.</summary>
    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }
}

/// <summary>A page of a list, and the cursor for the next.</summary>
/// <typeparam name="T">The item.</typeparam>
/// <param name="Items">The items.</param>
/// <param name="Next">The cursor for the next page, or null at the end.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>An address on the do-not-send list (DES-14 §19).</summary>
public sealed class Suppression
{
    /// <summary>The address.</summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    /// <summary>For a single lookup: whether it is suppressed.</summary>
    [JsonPropertyName("suppressed")]
    public bool? Suppressed { get; set; }

    /// <summary>hard_bounce, complaint, unsubscribe or manual.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The reason in words.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>When it was added.</summary>
    [JsonPropertyName("at")]
    public DateTimeOffset? At { get; set; }

    /// <summary>The send that caused it, or null.</summary>
    [JsonPropertyName("sendId")]
    public Guid? SendId { get; set; }
}

/// <summary>A webhook (DES-14 §8). The secret is given only when it is added.</summary>
public sealed class Webhook
{
    /// <summary>Its id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Its address.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>The event types it takes; empty for all.</summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<string> Events { get; set; } = Array.Empty<string>();

    /// <summary>The secret, when just added; null otherwise.</summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }
}
