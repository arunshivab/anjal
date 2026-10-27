using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anjal.Client;

/// <summary>A refusal of Anjal's API: the HTTP status, the error code, the words, and the request's id for support.</summary>
public sealed class AnjalApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AnjalApiException"/> class.</summary>
    public AnjalApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnjalApiException"/> class.</summary>
    /// <param name="message">The words.</param>
    public AnjalApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnjalApiException"/> class.</summary>
    /// <param name="message">The words.</param>
    /// <param name="inner">The cause.</param>
    public AnjalApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnjalApiException"/> class.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The error code, such as rate_limited.</param>
    /// <param name="message">The words.</param>
    /// <param name="requestId">The request's id.</param>
    /// <param name="body">The whole answer.</param>
    public AnjalApiException(HttpStatusCode status, string code, string message, string requestId, string body)
        : base(message)
    {
        this.Status = status;
        this.Code = code;
        this.RequestId = requestId;
        this.Body = body;
    }

    /// <summary>The HTTP status.</summary>
    public HttpStatusCode Status { get; }

    /// <summary>The error code.</summary>
    public string Code { get; } = string.Empty;

    /// <summary>The request's id (Anjal-Request-Id).</summary>
    public string RequestId { get; } = string.Empty;

    /// <summary>The whole answer.</summary>
    public string Body { get; } = string.Empty;
}

/// <summary>
/// rc.26 (DES-14): Anjal's application API for .NET. Every request carries the key; with a signing
/// key, every request is signed (§11). Sends get an Idempotency-Key unless told not to, and are
/// retried with the same key on 408, 429, 5xx, 409 idempotency_in_progress and timeouts, after
/// Retry-After - so a retry never sends twice.
/// </summary>
public sealed class AnjalClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string apiKey;
    private readonly ECDsa? signingKey;
    private readonly string? signingKeyId;
    private readonly Uri baseAddress;

    /// <summary>Initializes a new instance of the <see cref="AnjalClient"/> class.</summary>
    /// <param name="baseAddress">The server, such as https://mail.anjal.co.in.</param>
    /// <param name="apiKey">The HTTP key (ak_live_… or ak_test_…).</param>
    /// <param name="signingKey">The application's ECDSA P-256 private key, to sign every request; null for none.</param>
    /// <param name="signingKeyId">The signing key's id on the console (sk_…), with a signing key.</param>
    /// <param name="httpClient">An HttpClient to use, or null for the client's own.</param>
    public AnjalClient(Uri baseAddress, string apiKey, ECDsa? signingKey = null, string? signingKeyId = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(apiKey);
        if (signingKey is not null && string.IsNullOrEmpty(signingKeyId))
        {
            throw new ArgumentException("A signing key needs its id from the console (sk_...).", nameof(signingKeyId));
        }
        this.baseAddress = new Uri(baseAddress.ToString().TrimEnd('/') + "/");
        this.apiKey = apiKey;
        this.signingKey = signingKey;
        this.signingKeyId = signingKeyId;
        this.ownsHttp = httpClient is null;
        this.http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>How many times a request is tried in all.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>True (the default) to give every send an Idempotency-Key of its own.</summary>
    public bool AutoIdempotency { get; set; } = true;

    /// <summary>The longest wait between tries, whatever Retry-After says.</summary>
    public TimeSpan MaxRetryWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tests: the clock the signatures' timestamps read.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The string a request is signed over (DES-14 §11): the method in capitals, the path and query
    /// exactly as sent, the Unix timestamp, and the lowercase hex SHA-256 of the body, joined by "\n".
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="pathAndQuery">The path and query.</param>
    /// <param name="unixSeconds">The timestamp.</param>
    /// <param name="body">The body.</param>
    /// <returns>The string.</returns>
    public static string SigningString(string method, string pathAndQuery, long unixSeconds, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(body);
        return method.ToUpperInvariant() + "\n" + pathAndQuery + "\n" + unixSeconds.ToString(CultureInfo.InvariantCulture) + "\n" + Convert.ToHexStringLower(SHA256.HashData(body));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.ownsHttp)
        {
            this.http.Dispose();
        }
    }

    /// <summary>Send one message (POST /api/v1/messages/email).</summary>
    /// <param name="request">The message.</param>
    /// <param name="idempotencyKey">The Idempotency-Key; null for a new one (or none, with AutoIdempotency off).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What it was given.</returns>
    public async Task<SendResult> SendAsync(SendRequest request, string? idempotencyKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (HttpResponseMessage res, string body) = await this.CallAsync(HttpMethod.Post, "messages/email", request.ToJson(), this.KeyFor(idempotencyKey), ct).ConfigureAwait(false);
        using (res)
        {
            SendResult result = JsonSerializer.Deserialize<SendResult>(body, Json) ?? new SendResult();
            result.Replayed = res.Headers.TryGetValues("Idempotent-Replayed", out IEnumerable<string>? r) && r.Contains("true");
            return result;
        }
    }

    /// <summary>Send up to 100 messages; each is checked and queued on its own (POST /messages/email/batch).</summary>
    /// <param name="requests">The messages.</param>
    /// <param name="idempotencyKey">One Idempotency-Key for the whole batch; null for a new one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One result per message, in order.</returns>
    public async Task<IReadOnlyList<BatchItemResult>> SendBatchAsync(IEnumerable<SendRequest> requests, string? idempotencyKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var body = new Dictionary<string, object?> { ["messages"] = requests.Select(r => r.ToJson()).ToList() };
        (HttpResponseMessage res, string text) = await this.CallAsync(HttpMethod.Post, "messages/email/batch", body, this.KeyFor(idempotencyKey), ct).ConfigureAwait(false);
        res.Dispose();
        using JsonDocument doc = JsonDocument.Parse(text);
        return JsonSerializer.Deserialize<List<BatchItemResult>>(doc.RootElement.GetProperty("results").GetRawText(), Json) ?? new List<BatchItemResult>();
    }

    /// <summary>A message's status (GET /messages/{id}).</summary>
    /// <param name="id">The send's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    public async Task<MessageStatus> GetMessageAsync(Guid id, CancellationToken ct = default)
    {
        string text = await this.GetTextAsync("messages/" + id.ToString(), ct).ConfigureAwait(false);
        MessageStatus status = JsonSerializer.Deserialize<MessageStatus>(text, Json) ?? new MessageStatus();
        status.Raw = JsonDocument.Parse(text).RootElement.Clone();
        return status;
    }

    /// <summary>Events after the last one seen, oldest first (GET /events).</summary>
    /// <param name="since">The last sequence number seen; 0 for the beginning.</param>
    /// <param name="limit">At most this many (500).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The events.</returns>
    public async Task<IReadOnlyList<AnjalEvent>> EventsAsync(long since = 0, int limit = 500, CancellationToken ct = default)
    {
        using JsonDocument doc = JsonDocument.Parse(await this.GetTextAsync(string.Create(CultureInfo.InvariantCulture, $"events?since={since}&limit={limit}"), ct).ConfigureAwait(false));
        return JsonSerializer.Deserialize<List<AnjalEvent>>(doc.RootElement.GetProperty("events").GetRawText(), Json) ?? new List<AnjalEvent>();
    }

    /// <summary>Mail waiting in the inbox, oldest first (GET /inbox; signed).</summary>
    /// <param name="cursor">The cursor from the last page, or null.</param>
    /// <param name="limit">At most this many (100).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A page.</returns>
    public async Task<Page<InboxItem>> InboxAsync(string? cursor = null, int limit = 100, CancellationToken ct = default)
    {
        string path = string.Create(CultureInfo.InvariantCulture, $"inbox?limit={limit}") + (cursor is null ? string.Empty : "&since=" + Uri.EscapeDataString(cursor));
        using JsonDocument doc = JsonDocument.Parse(await this.GetTextAsync(path, ct).ConfigureAwait(false));
        List<InboxItem> items = JsonSerializer.Deserialize<List<InboxItem>>(doc.RootElement.GetProperty("mails").GetRawText(), Json) ?? new List<InboxItem>();
        return new Page<InboxItem>(items, doc.RootElement.TryGetProperty("next", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
    }

    /// <summary>One mail's encrypted envelope (GET /inbox/{id}; signed). Decrypt it with the application's private key.</summary>
    /// <param name="id">The mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The envelope.</returns>
    public async Task<InboxMessage> GetInboxMessageAsync(Guid id, CancellationToken ct = default) =>
        InboxMessage.FromJson(id, await this.GetTextAsync("inbox/" + id.ToString(), ct).ConfigureAwait(false));

    /// <summary>"I have saved it on my side" (POST /inbox/{id}/ack; signed). Harmless twice.</summary>
    /// <param name="id">The mail.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        (HttpResponseMessage res, _) = await this.CallAsync(HttpMethod.Post, "inbox/" + id.ToString() + "/ack", null, null, ct).ConfigureAwait(false);
        res.Dispose();
    }

    /// <summary>Acknowledge up to 500 mails (POST /inbox/ack; signed); the ids that were acknowledged.</summary>
    /// <param name="ids">The mails.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The acknowledged ids.</returns>
    public async Task<IReadOnlyList<Guid>> AcknowledgeAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        (HttpResponseMessage res, string text) = await this.CallAsync(HttpMethod.Post, "inbox/ack", new Dictionary<string, object?> { ["ids"] = ids.Select(i => i.ToString()).ToList() }, null, ct).ConfigureAwait(false);
        res.Dispose();
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Where(r => r.GetProperty("acknowledged").GetBoolean())
            .Select(r => Guid.Parse(r.GetProperty("id").GetString()!))
            .ToList();
    }

    /// <summary>Whether an address is suppressed, and why (GET /suppressions?address=).</summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry.</returns>
    public async Task<Suppression> GetSuppressionAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        return JsonSerializer.Deserialize<Suppression>(await this.GetTextAsync("suppressions?address=" + Uri.EscapeDataString(address), ct).ConfigureAwait(false), Json) ?? new Suppression();
    }

    /// <summary>Suppress an address by hand (POST /suppressions).</summary>
    /// <param name="address">The address.</param>
    /// <param name="note">Why.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry.</returns>
    public async Task<Suppression> AddSuppressionAsync(string address, string? note = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        (HttpResponseMessage res, string text) = await this.CallAsync(HttpMethod.Post, "suppressions", new Dictionary<string, object?> { ["address"] = address, ["note"] = note }, null, ct).ConfigureAwait(false);
        res.Dispose();
        return JsonSerializer.Deserialize<Suppression>(text, Json) ?? new Suppression();
    }

    /// <summary>Take an address off the list (DELETE /suppressions/{address}): hard bounces and manual entries only.</summary>
    /// <param name="address">The address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RemoveSuppressionAsync(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        (HttpResponseMessage res, _) = await this.CallAsync(HttpMethod.Delete, "suppressions/" + Uri.EscapeDataString(address), null, null, ct).ConfigureAwait(false);
        res.Dispose();
    }

    /// <summary>Add a webhook (POST /webhooks); its secret is in the answer, this once.</summary>
    /// <param name="url">The https:// address.</param>
    /// <param name="events">The event types; none for all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The webhook, with its secret.</returns>
    public async Task<Webhook> AddWebhookAsync(Uri url, IEnumerable<string>? events = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        (HttpResponseMessage res, string text) = await this.CallAsync(HttpMethod.Post, "webhooks", new Dictionary<string, object?> { ["url"] = url.ToString(), ["events"] = events?.ToList() }, null, ct).ConfigureAwait(false);
        res.Dispose();
        return JsonSerializer.Deserialize<Webhook>(text, Json) ?? new Webhook();
    }

    /// <summary>The application's limits and today's use (GET /limits), as JSON.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public async Task<JsonElement> LimitsAsync(CancellationToken ct = default) =>
        JsonDocument.Parse(await this.GetTextAsync("limits", ct).ConfigureAwait(false)).RootElement.Clone();

    /// <summary>Whether the application can send now (GET /health): {ok, key, domains, limits, server}.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer (also when it is 503).</returns>
    public async Task<JsonElement> HealthAsync(CancellationToken ct = default)
    {
        using HttpRequestMessage req = this.Build(HttpMethod.Get, "health", null, null);
        using HttpResponseMessage res = await this.http.SendAsync(req, ct).ConfigureAwait(false);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement.Clone();
    }

    private string? KeyFor(string? given) => given ?? (this.AutoIdempotency ? Guid.NewGuid().ToString("N") : null);

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        (HttpResponseMessage res, string body) = await this.CallAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
        res.Dispose();
        return body;
    }

    // One call with its retries. The same body and Idempotency-Key each time, freshly signed.
    private async Task<(HttpResponseMessage Response, string Body)> CallAsync(HttpMethod method, string path, object? json, string? idempotencyKey, CancellationToken ct)
    {
        byte[]? body = json is null ? null : JsonSerializer.SerializeToUtf8Bytes(json, Json);
        for (int attempt = 1; ; attempt++)
        {
            HttpResponseMessage res;
            try
            {
                using HttpRequestMessage req = this.Build(method, path, body, idempotencyKey);
                res = await this.http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) && attempt < this.MaxAttempts)
            {
                await Task.Delay(this.Backoff(attempt, null), ct).ConfigureAwait(false);
                continue;
            }
            string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                return (res, text);
            }
            (string code, string message, string requestId) = ErrorOf(res, text);
            bool retry = res.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500
                || (res.StatusCode == HttpStatusCode.Conflict && code == "idempotency_in_progress");
            if (retry && attempt < this.MaxAttempts && (method != HttpMethod.Post || idempotencyKey is not null || path.StartsWith("inbox", StringComparison.Ordinal)))
            {
                TimeSpan? after = res.Headers.RetryAfter?.Delta;
                res.Dispose();
                await Task.Delay(this.Backoff(attempt, after), ct).ConfigureAwait(false);
                continue;
            }
            res.Dispose();
            throw new AnjalApiException(res.StatusCode, code, message, requestId, text);
        }
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        TimeSpan wait = retryAfter ?? TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
        return wait > this.MaxRetryWait ? this.MaxRetryWait : wait;
    }

    private static (string Code, string Message, string RequestId) ErrorOf(HttpResponseMessage res, string text)
    {
        string requestId = res.Headers.TryGetValues("Anjal-Request-Id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() ?? string.Empty : string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return (doc.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? string.Empty : string.Empty,
                doc.RootElement.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : res.ReasonPhrase ?? string.Empty,
                requestId);
        }
        catch (JsonException)
        {
            return (string.Empty, res.ReasonPhrase ?? "HTTP " + ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture), requestId);
        }
    }

    private HttpRequestMessage Build(HttpMethod method, string path, byte[]? body, string? idempotencyKey)
    {
        var uri = new Uri(this.baseAddress, "api/" + ModuleInfo.ApiVersion + "/" + path);
        var req = new HttpRequestMessage(method, uri);
        byte[] bytes = body ?? Array.Empty<byte>();
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        req.Headers.Add("X-Api-Key", this.apiKey);
        if (idempotencyKey is not null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        if (this.signingKey is ECDsa key)
        {
            long ts = this.Clock().ToUnixTimeSeconds();
            byte[] signed = Encoding.UTF8.GetBytes(SigningString(method.Method, uri.PathAndQuery, ts, bytes));
            byte[] signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            req.Headers.Add("Anjal-Key-Id", this.signingKeyId);
            req.Headers.Add("Anjal-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
            req.Headers.Add("Anjal-Signature", Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }
        return req;
    }
}
