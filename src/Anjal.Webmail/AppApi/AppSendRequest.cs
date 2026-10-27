using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anjal.Webmail.AppApi;

/// <summary>An address in a send body: {email, name}; "address" is read as "email" (DES-14 §4).</summary>
/// <param name="Email">The address, lower case.</param>
/// <param name="Name">The display name, or empty.</param>
public sealed record AppAddress(string Email, string Name);

/// <summary>An attachment in a send body.</summary>
/// <param name="FileName">Its name.</param>
/// <param name="ContentType">Its type.</param>
/// <param name="Content">Its bytes.</param>
public sealed record AppAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>A template choice in a send body (DES-14 §22).</summary>
/// <param name="Name">The template.</param>
/// <param name="Language">The language, or empty for English.</param>
/// <param name="Values">The placeholders' values.</param>
public sealed record AppTemplateChoice(string Name, string Language, IReadOnlyDictionary<string, string> Values);

/// <summary>A refusal of the API, with its HTTP status, code and words.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The error code.</param>
/// <param name="Message">What is wrong, in words.</param>
/// <param name="Extra">More fields for the body.</param>
public sealed record AppApiFailure(int Status, string Code, string Message, IReadOnlyDictionary<string, object?>? Extra = null);

/// <summary>A send body, read and checked (DES-14 §4).</summary>
public sealed class AppSendRequest
{
    /// <summary>At most this many recipients in To, Cc and Bcc together.</summary>
    public const int MaxRecipients = 50;

    /// <summary>At most this many attachment bytes (18 MB of files; about 25 MB encoded, D-188).</summary>
    public const long MaxAttachmentBytes = 18L * 1024 * 1024;

    private static readonly char[] BadAddressChars = { ' ', '<', '>', '"', ',', ';', '\r', '\n' };

    private static readonly Regex TagPattern = new("^[A-Za-z0-9_.:-]{1,40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex HeaderNamePattern = new("^X-[A-Za-z0-9-]{1,60}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>From.</summary>
    public AppAddress From { get; private set; } = new(string.Empty, string.Empty);

    /// <summary>To.</summary>
    public List<AppAddress> To { get; } = new();

    /// <summary>Cc.</summary>
    public List<AppAddress> Cc { get; } = new();

    /// <summary>Bcc.</summary>
    public List<AppAddress> Bcc { get; } = new();

    /// <summary>Reply-To.</summary>
    public List<AppAddress> ReplyTo { get; } = new();

    /// <summary>The subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The HTML.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>The attachments.</summary>
    public List<AppAttachment> Attachments { get; } = new();

    /// <summary>The X- headers.</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tags.</summary>
    public List<string> Tags { get; } = new();

    /// <summary>The metadata.</summary>
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    /// <summary>When to send, or null for now.</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>True to add List-Unsubscribe.</summary>
    public bool Unsubscribe { get; private set; }

    /// <summary>The template, or null.</summary>
    public AppTemplateChoice? Template { get; private set; }

    /// <summary>Every recipient, lower case, once each.</summary>
    public IReadOnlyList<string> AllRecipients => this.To.Concat(this.Cc).Concat(this.Bcc).Select(a => a.Email).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Read a send body; the request, or the failure (422 invalid_request, naming the field).</summary>
    /// <param name="root">The body's JSON.</param>
    /// <param name="now">Now.</param>
    /// <returns>The request, or the failure.</returns>
    public static (AppSendRequest? Request, AppApiFailure? Failure) Read(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, Invalid(string.Empty, "The body is a JSON object."));
        }
        var r = new AppSendRequest();
        AppApiFailure? f = r.ReadAll(root, now);
        return f is null ? (r, null) : (null, f);
    }

    private AppApiFailure? ReadAll(JsonElement root, DateTimeOffset now)
    {
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (p.Name is not ("from" or "to" or "cc" or "bcc" or "replyTo" or "subject" or "text" or "html" or "attachments" or "headers"
                or "tags" or "metadata" or "scheduledAt" or "unsubscribe" or "template"))
            {
                return Invalid(p.Name, "Unknown field: " + p.Name + ".");
            }
        }
        List<AppAddress> froms = new();
        if (!root.TryGetProperty("from", out JsonElement from) || ReadAddresses(from, "from", out froms) is not null || froms.Count != 1)
        {
            return Invalid("from", "from is one address: {\"email\": \"...\", \"name\": \"...\"}.");
        }
        this.From = froms[0];
        foreach ((string field, List<AppAddress> list) in new[] { ("to", this.To), ("cc", this.Cc), ("bcc", this.Bcc), ("replyTo", this.ReplyTo) })
        {
            if (root.TryGetProperty(field, out JsonElement e) && e.ValueKind != JsonValueKind.Null)
            {
                if (ReadAddresses(e, field, out List<AppAddress> found) is AppApiFailure bad)
                {
                    return bad;
                }
                list.AddRange(found);
            }
        }
        if (this.To.Count + this.Cc.Count + this.Bcc.Count == 0)
        {
            return Invalid("to", "At least one recipient.");
        }
        if (this.To.Count + this.Cc.Count + this.Bcc.Count > MaxRecipients)
        {
            return Invalid("to", "At most 50 recipients in to, cc and bcc together; send more with /messages/email/batch.");
        }
        this.Subject = Str(root, "subject");
        this.Text = Str(root, "text");
        this.Html = Str(root, "html");
        if (root.TryGetProperty("template", out JsonElement template) && template.ValueKind == JsonValueKind.Object)
        {
            if (this.Subject.Length > 0 || this.Text.Length > 0 || this.Html.Length > 0)
            {
                return Invalid("template", "Send either a template or subject, text and html, not both.");
            }
            string name = Str(template, "name");
            if (name.Length == 0)
            {
                return Invalid("template.name", "Name the template.");
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (template.TryGetProperty("values", out JsonElement v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in v.EnumerateObject())
                {
                    values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
                }
            }
            this.Template = new AppTemplateChoice(name, Str(template, "language").ToLowerInvariant(), values);
        }
        else if (this.Text.Length == 0 && this.Html.Length == 0)
        {
            return Invalid("text", "Give text, html or both.");
        }
        if (this.Subject.Length > 998 || this.Subject.Contains('\r', StringComparison.Ordinal) || this.Subject.Contains('\n', StringComparison.Ordinal))
        {
            return Invalid("subject", "The subject is one line, at most 998 characters.");
        }
        if (root.TryGetProperty("attachments", out JsonElement atts) && atts.ValueKind == JsonValueKind.Array)
        {
            long total = 0;
            foreach (JsonElement a in atts.EnumerateArray())
            {
                string fileName = Str(a, "filename");
                string content = Str(a, "content");
                if (fileName.Length == 0 || content.Length == 0)
                {
                    return Invalid("attachments", "Each attachment has a filename and content (base64).");
                }
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(content);
                }
                catch (FormatException)
                {
                    return Invalid("attachments", "The content of " + fileName + " is not base64.");
                }
                total += bytes.LongLength;
                string type = Str(a, "contentType");
                this.Attachments.Add(new AppAttachment(fileName, type.Length > 0 ? type : "application/octet-stream", bytes));
            }
            if (total > MaxAttachmentBytes)
            {
                return new AppApiFailure(413, "attachments_too_large", "The attachments come to more than 18 MB.");
            }
        }
        if (root.TryGetProperty("headers", out JsonElement headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty h in headers.EnumerateObject())
            {
                string value = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? string.Empty : string.Empty;
                if (!HeaderNamePattern.IsMatch(h.Name) || h.Name.StartsWith("X-Anjal-", StringComparison.OrdinalIgnoreCase)
                    || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Length > 900)
                {
                    return Invalid("headers", "Only headers whose names begin X- (not X-Anjal-), one line each: " + h.Name + ".");
                }
                this.Headers[h.Name] = value;
            }
            if (this.Headers.Count > 10)
            {
                return Invalid("headers", "At most 10 headers.");
            }
        }
        if (root.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement t in tags.EnumerateArray())
            {
                string tag = t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
                if (!TagPattern.IsMatch(tag))
                {
                    return Invalid("tags", "A tag is a short word: letters, digits, _ . : -, at most 40.");
                }
                if (!this.Tags.Contains(tag))
                {
                    this.Tags.Add(tag);
                }
            }
            if (this.Tags.Count > 10)
            {
                return Invalid("tags", "At most 10 tags.");
            }
        }
        if (root.TryGetProperty("metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
        {
            int size = 0;
            foreach (JsonProperty m in meta.EnumerateObject())
            {
                if (m.Value.ValueKind != JsonValueKind.String)
                {
                    return Invalid("metadata", "Metadata values are strings.");
                }
                this.Metadata[m.Name] = m.Value.GetString() ?? string.Empty;
                size += m.Name.Length + this.Metadata[m.Name].Length;
            }
            if (this.Metadata.Count > 20 || size > 1024)
            {
                return Invalid("metadata", "At most 20 pairs, 1 KB in all.");
            }
        }
        if (root.TryGetProperty("scheduledAt", out JsonElement when) && when.ValueKind == JsonValueKind.String)
        {
            if (!DateTimeOffset.TryParse(when.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at))
            {
                return Invalid("scheduledAt", "scheduledAt is a time in RFC 3339, with its offset.");
            }
            if (at > now + TimeSpan.FromDays(7))
            {
                return Invalid("scheduledAt", "At most 7 days ahead.");
            }
            this.ScheduledAt = at > now ? at : null;
        }
        if (root.TryGetProperty("unsubscribe", out JsonElement unsub))
        {
            this.Unsubscribe = unsub.ValueKind == JsonValueKind.True;
        }
        return null;
    }

    private static AppApiFailure? ReadAddresses(JsonElement e, string field, out List<AppAddress> found)
    {
        found = new List<AppAddress>();
        if (e.ValueKind == JsonValueKind.Object)
        {
            return ReadOne(e, field, found);
        }
        if (e.ValueKind == JsonValueKind.String)
        {
            return ReadText(e.GetString() ?? string.Empty, string.Empty, field, found);
        }
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in e.EnumerateArray())
            {
                AppApiFailure? bad = item.ValueKind == JsonValueKind.String ? ReadText(item.GetString() ?? string.Empty, string.Empty, field, found) : ReadOne(item, field, found);
                if (bad is not null)
                {
                    return bad;
                }
            }
            return null;
        }
        return Invalid(field, field + " is an address object or a list of them.");
    }

    private static AppApiFailure? ReadOne(JsonElement e, string field, List<AppAddress> found)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return Invalid(field, field + " is an address object or a list of them.");
        }
        string email = Str(e, "email");
        if (email.Length == 0)
        {
            email = Str(e, "address");
        }
        return ReadText(email, Str(e, "name"), field, found);
    }

    private static AppApiFailure? ReadText(string email, string name, string field, List<AppAddress> found)
    {
        string a = email.Trim().ToLowerInvariant();
        if (!Anjal.Mailbox.MailboxSink.TrySplitAddress(a, out _, out string domain) || !domain.Contains('.', StringComparison.Ordinal) || a.Length > 254
            || a.IndexOfAny(BadAddressChars) >= 0)
        {
            return Invalid(field, "Not an address: " + email + ".");
        }
        if (name.Contains('\r', StringComparison.Ordinal) || name.Contains('\n', StringComparison.Ordinal) || name.Length > 200)
        {
            return Invalid(field, "A name is one line, at most 200 characters.");
        }
        found.Add(new AppAddress(a, name.Trim()));
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    /// <summary>422 invalid_request naming a field.</summary>
    /// <param name="field">The field.</param>
    /// <param name="message">What is wrong.</param>
    /// <returns>The failure.</returns>
    public static AppApiFailure Invalid(string field, string message) =>
        new(422, "invalid_request", message, new Dictionary<string, object?> { ["field"] = field });
}
