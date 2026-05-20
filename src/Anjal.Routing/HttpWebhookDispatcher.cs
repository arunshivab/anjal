using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Anjal.Routing;

/// <summary>
/// Payload sent to a webhook subscriber. The receiver should validate the
/// signature using the shared secret before processing.
/// </summary>
public sealed class WebhookPayload
{
    /// <summary>Identifier of the persisted inbound message.</summary>
    public System.Guid InboundMessageId { get; init; }

    /// <summary>Lowercased recipient address as received over SMTP.</summary>
    public string Recipient { get; init; } = string.Empty;

    /// <summary>The matched local-part of the routing rule.</summary>
    public string LocalPart { get; init; } = string.Empty;

    /// <summary>The "+tag" if present, otherwise empty.</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>Correlation key from the matched tag grant, or empty.</summary>
    public string CorrelationKey { get; init; } = string.Empty;

    /// <summary>The SMTP envelope sender.</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The decoded Subject header.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The Message-ID with angle brackets stripped.</summary>
    public string MessageId { get; init; } = string.Empty;

    /// <summary>Time the message was received.</summary>
    public System.DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The raw RFC 5322 bytes of the message, base64-encoded.</summary>
    public string RawBytesBase64 { get; init; } = string.Empty;

    /// <summary>
    /// Inbound authentication results (SPF/DKIM/DMARC) as a JSON object
    /// fragment (without surrounding braces, e.g. <c>"spf":{...},"dkim":{...},"dmarc":{...}</c>).
    /// Empty when inbound authentication is disabled. When populated, the
    /// payload's <c>authResults</c> key embeds this fragment. Produced by
    /// <c>Anjal.Auth.AuthResultsJson.Serialize</c>.
    /// </summary>
    public string AuthResultsJson { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of a webhook delivery attempt.
/// </summary>
public sealed class WebhookDispatchResult
{
    /// <summary>The HTTP status code returned, or 0 if no response.</summary>
    public int StatusCode { get; init; }

    /// <summary>Whether the call completed without throwing.</summary>
    public bool Completed { get; init; }

    /// <summary>Error text if <see cref="Completed"/> is <see langword="false"/>.</summary>
    public string ErrorMessage { get; init; } = string.Empty;
}

/// <summary>
/// Sends signed webhook payloads to subscribers.
/// </summary>
public interface IWebhookDispatcher
{
    /// <summary>
    /// POST the payload to the given URL, signing it with the given secret.
    /// </summary>
    /// <param name="url">Webhook URL.</param>
    /// <param name="secret">The shared HMAC secret in hex form.</param>
    /// <param name="payload">The payload to send.</param>
    /// <param name="ct">Cancellation.</param>
    System.Threading.Tasks.Task<WebhookDispatchResult> SendAsync(
        string url,
        string secret,
        WebhookPayload payload,
        System.Threading.CancellationToken ct = default);
}

/// <summary>
/// HTTP-based implementation that signs requests with HMAC-SHA256 and
/// includes a Unix timestamp for replay protection. Header conventions:
/// <list type="bullet">
///   <item><c>X-Anjal-Timestamp</c> - Unix seconds at time of send.</item>
///   <item><c>X-Anjal-Signature</c> - <c>sha256=&lt;hex&gt;</c> over <c>timestamp + "." + body</c>.</item>
/// </list>
/// </summary>
public sealed class HttpWebhookDispatcher : IWebhookDispatcher
{
    private readonly HttpClient httpClient;
    private readonly System.Func<System.DateTimeOffset> clock;

    /// <summary>
    /// Construct the dispatcher with a pre-built <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <param name="httpClient">An <see cref="System.Net.Http.HttpClient"/> the caller owns.</param>
    /// <param name="clock">Optional clock for tests.</param>
    public HttpWebhookDispatcher(HttpClient httpClient, System.Func<System.DateTimeOffset>? clock = null)
    {
        System.ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.clock = clock ?? (() => System.DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async System.Threading.Tasks.Task<WebhookDispatchResult> SendAsync(
        string url,
        string secret,
        WebhookPayload payload,
        System.Threading.CancellationToken ct = default)
    {
        System.ArgumentNullException.ThrowIfNull(url);
        System.ArgumentNullException.ThrowIfNull(secret);
        System.ArgumentNullException.ThrowIfNull(payload);

        string body = SerializePayload(payload);
        long timestamp = this.clock().ToUnixTimeSeconds();
        string signature = ComputeSignature(secret, timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.TryAddWithoutValidation("X-Anjal-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Anjal-Signature", $"sha256={signature}");

        try
        {
            using HttpResponseMessage response = await this.httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return new WebhookDispatchResult
            {
                StatusCode = (int)response.StatusCode,
                Completed = true,
            };
        }
        catch (HttpRequestException ex)
        {
            return new WebhookDispatchResult
            {
                StatusCode = 0,
                Completed = false,
                ErrorMessage = ex.Message,
            };
        }
        catch (System.Threading.Tasks.TaskCanceledException ex)
        {
            return new WebhookDispatchResult
            {
                StatusCode = 0,
                Completed = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    /// <summary>
    /// Compute the HMAC-SHA256 signature over <c>timestamp + "." + body</c>.
    /// Exposed so receivers can validate using the same code.
    /// </summary>
    /// <param name="secretHex">The shared secret in hex form.</param>
    /// <param name="timestamp">Unix-seconds timestamp from the request header.</param>
    /// <param name="body">The exact request body string as received.</param>
    /// <returns>The lowercase hex signature.</returns>
    public static string ComputeSignature(string secretHex, long timestamp, string body)
    {
        System.ArgumentNullException.ThrowIfNull(secretHex);
        System.ArgumentNullException.ThrowIfNull(body);

        byte[] key = HexDecode(secretHex);
        string signed = timestamp.ToString(CultureInfo.InvariantCulture) + "." + body;
        byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed));
        return HexEncode(mac);
    }

    /// <summary>
    /// Serialise a payload to canonical JSON. Hand-written to avoid taking a
    /// dependency on System.Text.Json - the schema is small and stable.
    /// </summary>
    /// <param name="payload">The payload.</param>
    /// <returns>The JSON string.</returns>
    public static string SerializePayload(WebhookPayload payload)
    {
        System.ArgumentNullException.ThrowIfNull(payload);
        var sb = new StringBuilder(256);
        sb.Append('{');
        AppendField(sb, "inboundMessageId", payload.InboundMessageId.ToString("D", CultureInfo.InvariantCulture), first: true);
        AppendField(sb, "recipient", payload.Recipient, first: false);
        AppendField(sb, "localPart", payload.LocalPart, first: false);
        AppendField(sb, "tag", payload.Tag, first: false);
        AppendField(sb, "correlationKey", payload.CorrelationKey, first: false);
        AppendField(sb, "envelopeFrom", payload.EnvelopeFrom, first: false);
        AppendField(sb, "subject", payload.Subject, first: false);
        AppendField(sb, "messageId", payload.MessageId, first: false);
        AppendField(sb, "receivedAt", payload.ReceivedAt.ToString("O", CultureInfo.InvariantCulture), first: false);
        AppendField(sb, "rawBytesBase64", payload.RawBytesBase64, first: false);
        if (!string.IsNullOrEmpty(payload.AuthResultsJson))
        {
            // The fragment is already a complete JSON object including
            // surrounding braces; embed verbatim under the authResults key.
            sb.Append(",\"authResults\":").Append(payload.AuthResultsJson);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string key, string value, bool first)
    {
        if (!first)
        {
            sb.Append(',');
        }
        sb.Append('"').Append(key).Append("\":\"");
        AppendJsonEscaped(sb, value ?? string.Empty);
        sb.Append('"');
    }

    private static void AppendJsonEscaped(StringBuilder sb, string s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
    }

    private static byte[] HexDecode(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new System.FormatException("Hex string must have an even length.");
        }
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[(i * 2) + 1]));
        }
        return result;
    }

    private static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }
        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }
        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }
        throw new System.FormatException($"Invalid hex character: '{c}'.");
    }

    private static string HexEncode(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
