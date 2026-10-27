using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Anjal.Webmail.AppApi;

/// <summary>Who is calling the API: the application, its key, and the request's id.</summary>
/// <param name="Tenant">The organisation.</param>
/// <param name="App">The application.</param>
/// <param name="Key">The HTTP key used.</param>
/// <param name="RequestId">The request's id (Anjal-Request-Id).</param>
/// <param name="Client">The client's address.</param>
/// <param name="Body">The request body.</param>
public sealed record AppCaller(TenantRow Tenant, OrgApp App, AppHttpKey Key, string RequestId, string Client, byte[] Body)
{
    /// <summary>True for a sandbox key.</summary>
    public bool Sandbox => this.Key.Sandbox;
}

/// <summary>One rate window's figures (DES-14 §6).</summary>
/// <param name="Window">minute, hour or day.</param>
/// <param name="Limit">The limit.</param>
/// <param name="Used">Used in the window.</param>
/// <param name="Reset">When the window starts again.</param>
public sealed record AppRateWindow(string Window, int Limit, long Used, DateTimeOffset Reset)
{
    /// <summary>What is left.</summary>
    public long Remaining => Math.Max(0, this.Limit - this.Used);
}

/// <summary>
/// rc.26 (DES-14): the application API's checks and answers. Every request is checked in the order
/// of §28.3 - the key names the application; then Allowed from; then the key's state; then the
/// signature when required - and every answer carries Anjal-Request-Id and, once the application is
/// known, the rate-limit headers.
/// </summary>
public sealed partial class AppApiService : IDisposable
{
    /// <summary>The largest body of a send (25 MB of attachments with base64's overhead).</summary>
    public const long MaxBody = 30L * 1024 * 1024;

    /// <summary>The largest body of a batch.</summary>
    public const long MaxBatchBody = 60L * 1024 * 1024;

    /// <summary>Sends a minute (Anjal's burst limit).</summary>
    public const int PerMinute = 60;

    private const int FailuresBeforeThrottle = 10;

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan SignatureWindow = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly MailboxService mail;
    private readonly IMailboxStore store;
    private readonly IMessageStore messages;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Version, Guid TenantId, string AppId, string KeyId)> verified = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim directoryGate = new(1, 1);
    private (int Version, Dictionary<string, List<(Guid TenantId, string AppId, string KeyId, string Hash)>> ByFingerprint) directory = (-1, new());

    /// <summary>Initializes a new instance of the <see cref="AppApiService"/> class.</summary>
    /// <param name="mail">The mail service.</param>
    /// <param name="store">The mailbox store.</param>
    /// <param name="messages">The message store.</param>
    public AppApiService(MailboxService mail, IMailboxStore store, IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(messages);
        this.mail = mail;
        this.store = store;
        this.messages = messages;
    }

    /// <inheritdoc/>
    public void Dispose() => this.directoryGate.Dispose();

    /// <summary>The clock (tests set it).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private IAppApiStore Apps => this.messages as IAppApiStore ?? throw new InvalidOperationException("The store does not keep the application API.");

    /// <summary>Run one request: the checks, then the handler; every failure as JSON.</summary>
    /// <param name="http">The request.</param>
    /// <param name="route">The route.</param>
    /// <param name="handler">What the route does, once the caller is known.</param>
    /// <returns>The result.</returns>
    public async Task<IResult> RunAsync(HttpContext http, AppApiRoute route, Func<AppCaller, Task<IResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(handler);
        string requestId = Guid.NewGuid().ToString("N")[..16];
        http.Response.Headers["Anjal-Request-Id"] = requestId;
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            (AppCaller? caller, IResult? refused) = await this.CheckAsync(http, route, requestId).ConfigureAwait(false);
            if (refused is not null)
            {
                return refused;
            }
            IResult result = await handler(caller!).ConfigureAwait(false);
            // After the request's own work, so the figures count it (§6).
            await this.RateHeadersAsync(http, caller!).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
#pragma warning disable CA1031 // Any fault is answered 500 with the request id, and written to the log with it.
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] app-api 500 [{requestId}] {http.Request.Method} {http.Request.Path}: {ex.GetType().Name}: {ex.Message}");
            return Error(new AppApiFailure(500, "internal_error", "Something went wrong at Anjal. Quote the request id to support."), requestId);
        }
#pragma warning restore CA1031
    }

    // §28.3 in order. Returns the caller, or the refusal.
    private async Task<(AppCaller? Caller, IResult? Refused)> CheckAsync(HttpContext http, AppApiRoute route, string requestId)
    {
        CancellationToken ct = http.RequestAborted;
        DateTimeOffset now = this.Clock();
        IPAddress? remote = http.Connection.RemoteIpAddress;
        string client = remote?.ToString() ?? string.Empty;
        if (this.Throttled(client, now) is TimeSpan wait)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return (null, Error(new AppApiFailure(429, "too_many_failures", "Too many failed requests from this address; wait and try again.", new Dictionary<string, object?> { ["retryAfter"] = (int)Math.Ceiling(wait.TotalSeconds) }), requestId));
        }

        // The key (§2): X-Api-Key, or Authorization: Bearer with the same key.
        string header = http.Request.Headers["X-Api-Key"].ToString().Trim();
        string bearer = http.Request.Headers.Authorization.ToString().Trim();
        bearer = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer[7..].Trim() : string.Empty;
        string key = header.Length > 0 ? header : bearer;
        if (header.Length > 0 && bearer.Length > 0 && !string.Equals(header, bearer, StringComparison.Ordinal))
        {
            return (null, await this.InvalidKeyAsync(client, now, requestId, "The X-Api-Key and Authorization headers name different keys.").ConfigureAwait(false));
        }
        if (!IsKeyShape(key))
        {
            return (null, await this.InvalidKeyAsync(client, now, requestId, key.Length == 0 ? "Send the key in X-Api-Key." : "That is not an Anjal key.").ConfigureAwait(false));
        }
        (TenantRow Tenant, OrgApp App, AppHttpKey Key)? found = await this.FindKeyAsync(key, ct).ConfigureAwait(false);
        if (found is not { } f)
        {
            return (null, await this.InvalidKeyAsync(client, now, requestId, "That key is not known.").ConfigureAwait(false));
        }

        // Allowed from (§12), before anything else about the application is said.
        if (f.App.AllowedFrom.Count > 0 && (remote is null || !f.App.AllowedFrom.Any(r => IPNetwork.TryParse(r, out IPNetwork net) && net.Contains(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))))
        {
            await this.messages.AppendAuditAsync(new AuditEvent { Actor = "app:" + f.App.Name, Action = "app.api.address-refused", Subject = client, Detail = f.Key.Fingerprint, RemoteAddress = client }, ct).ConfigureAwait(false);
            return (null, Error(new AppApiFailure(403, "address_not_allowed", "Requests from " + client + " are not allowed for this application."), requestId));
        }
        if (f.App.Retired is not null || f.Key.Retired is not null || (f.Key.Expires is DateTimeOffset e && e <= now))
        {
            return (null, Error(new AppApiFailure(403, "key_retired", "This key is retired. Use the application's live key.", new Dictionary<string, object?> { ["fingerprint"] = f.Key.Fingerprint }), requestId));
        }

        long limit = route.Path.EndsWith("/batch", StringComparison.Ordinal) ? MaxBatchBody : MaxBody;
        byte[]? body = await ReadBodyAsync(http, limit, ct).ConfigureAwait(false);
        if (body is null)
        {
            return (null, Error(new AppApiFailure(413, "body_too_large", string.Create(CultureInfo.InvariantCulture, $"The body is larger than {limit / (1024 * 1024)} MB.")), requestId));
        }

        // Signing (§11): when the application asks for it, with the patient-data mark, and always for the inbox.
        if (route.MustSign || f.App.SigningRequired || f.App.PatientData)
        {
            if (await this.CheckSignatureAsync(http, f.App, body, now, client, requestId).ConfigureAwait(false) is IResult bad)
            {
                return (null, bad);
            }
        }
        if (body.Length > 0 && !(http.Request.ContentType ?? string.Empty).StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Error(new AppApiFailure(415, "unsupported_media_type", "Send the body as application/json."), requestId));
        }
        if (f.Key.LastUsed is not DateTimeOffset used || now - used > TimeSpan.FromHours(1))
        {
            string keyId = f.Key.Id;
            await this.mail.ChangeAppAsync(f.Tenant.Id, f.App.Id, a =>
            {
                if (a.HttpKeys.Find(k => k.Id == keyId) is AppHttpKey k)
                {
                    k.LastUsed = now;
                }
                return null;
            }, ct).ConfigureAwait(false);
        }
        return (new AppCaller(f.Tenant, f.App, f.Key, requestId, client, body), null);
    }

    private static bool IsKeyShape(string key) =>
        key.Length == 40 && (key.StartsWith("ak_live_", StringComparison.Ordinal) || key.StartsWith("ak_test_", StringComparison.Ordinal))
        && key[8..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // A 401: written to the activity log with the address, counted towards the throttle.
    private async Task<IResult> InvalidKeyAsync(string client, DateTimeOffset now, string requestId, string message)
    {
        this.NoteFailure(client, now);
        await this.messages.AppendAuditAsync(new AuditEvent { Actor = "anonymous", Action = "app.api.invalid-key", Subject = client, Detail = message, RemoteAddress = client }).ConfigureAwait(false);
        return Error(new AppApiFailure(401, "invalid_key", message), requestId);
    }

    private void NoteFailure(string client, DateTimeOffset now)
    {
        // Addresses with no failure in the window are forgotten, so the table cannot grow without end.
        if (this.failures.Count > 10_000)
        {
            foreach (KeyValuePair<string, List<DateTimeOffset>> entry in this.failures)
            {
                bool stale;
                lock (entry.Value)
                {
                    stale = entry.Value.Count == 0 || now - entry.Value[^1] > FailureWindow;
                }
                if (stale)
                {
                    this.failures.TryRemove(entry.Key, out _);
                }
            }
        }
        List<DateTimeOffset> list = this.failures.GetOrAdd(client, _ => new List<DateTimeOffset>());
        lock (list)
        {
            list.RemoveAll(t => now - t > FailureWindow);
            list.Add(now);
        }
    }

    // Ten failures from one address in 15 minutes throttle it for 15 minutes (the sign-in rule, D-175).
    private TimeSpan? Throttled(string client, DateTimeOffset now)
    {
        if (!this.failures.TryGetValue(client, out List<DateTimeOffset>? list))
        {
            return null;
        }
        lock (list)
        {
            list.RemoveAll(t => now - t > FailureWindow);
            return list.Count >= FailuresBeforeThrottle ? list[^1] + FailureWindow - now : null;
        }
    }

    // The application a key belongs to: by its fingerprint, then its salted hash; verified keys cached until the apps change.
    private async Task<(TenantRow Tenant, OrgApp App, AppHttpKey Key)?> FindKeyAsync(string key, CancellationToken ct)
    {
        string sha = MailboxService.Sha256Hex(key);
        int version = MailboxService.AppsVersion;
        if (this.verified.TryGetValue(sha, out (int Version, Guid TenantId, string AppId, string KeyId) known) && known.Version == version)
        {
            return await this.LoadAsync(known.TenantId, known.AppId, known.KeyId, ct).ConfigureAwait(false);
        }
        Dictionary<string, List<(Guid TenantId, string AppId, string KeyId, string Hash)>> byFingerprint = await this.DirectoryAsync(version, ct).ConfigureAwait(false);
        if (!byFingerprint.TryGetValue(sha[..8], out List<(Guid TenantId, string AppId, string KeyId, string Hash)>? candidates))
        {
            return null;
        }
        foreach ((Guid tenantId, string appId, string keyId, string hash) in candidates)
        {
            if (Anjal.Smtp.Pbkdf2Hasher.Verify(key, hash))
            {
                this.verified[sha] = (version, tenantId, appId, keyId);
                return await this.LoadAsync(tenantId, appId, keyId, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private async Task<(TenantRow Tenant, OrgApp App, AppHttpKey Key)?> LoadAsync(Guid tenantId, string appId, string keyId, CancellationToken ct)
    {
        TenantRow? tenant = await this.store.GetTenantByIdAsync(tenantId, ct).ConfigureAwait(false);
        OrgApp? app = tenant is null ? null : await this.mail.AppOfAsync(tenantId, appId, ct).ConfigureAwait(false);
        AppHttpKey? k = app?.HttpKeys.Find(x => x.Id == keyId);
        return tenant is null || app is null || k is null || !tenant.Enabled ? null : (tenant, app, k);
    }

    private async Task<Dictionary<string, List<(Guid TenantId, string AppId, string KeyId, string Hash)>>> DirectoryAsync(int version, CancellationToken ct)
    {
        if (this.directory.Version == version)
        {
            return this.directory.ByFingerprint;
        }
        await this.directoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.directory.Version != version)
            {
                var map = new Dictionary<string, List<(Guid TenantId, string AppId, string KeyId, string Hash)>>(StringComparer.Ordinal);
                foreach (TenantRow t in await this.store.ListTenantsAsync(ct).ConfigureAwait(false))
                {
                    foreach (OrgApp app in await this.mail.AppsOfAsync(t.Id, ct).ConfigureAwait(false))
                    {
                        foreach (AppHttpKey k in app.HttpKeys)
                        {
                            if (!map.TryGetValue(k.Fingerprint, out List<(Guid TenantId, string AppId, string KeyId, string Hash)>? list))
                            {
                                list = new();
                                map[k.Fingerprint] = list;
                            }
                            list.Add((t.Id, app.Id, k.Id, k.Hash));
                        }
                    }
                }
                this.directory = (version, map);
                this.verified.Clear();
            }
            return this.directory.ByFingerprint;
        }
        finally
        {
            this.directoryGate.Release();
        }
    }

    // The body, or null when it is larger than allowed.
    private static async Task<byte[]?> ReadBodyAsync(HttpContext http, long limit, CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit + 1;
        }
        if (http.Request.ContentLength is long declared && declared > limit)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        try
        {
            while ((read = await http.Request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > limit)
                {
                    return null;
                }
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return null;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The string an application signs (DES-14 §11): the method in capitals, the path with its query
    /// exactly as sent, the timestamp, and the hex SHA-256 of the body, joined by newlines.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="pathAndQuery">The path and query as sent.</param>
    /// <param name="timestamp">Unix seconds, as sent.</param>
    /// <param name="body">The body.</param>
    /// <returns>The string.</returns>
    public static string SigningString(string method, string pathAndQuery, string timestamp, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(body);
        return method.ToUpperInvariant() + "\n" + pathAndQuery + "\n" + timestamp + "\n" + Convert.ToHexStringLower(SHA256.HashData(body));
    }

    private async Task<IResult?> CheckSignatureAsync(HttpContext http, OrgApp app, byte[] body, DateTimeOffset now, string client, string requestId)
    {
        string keyId = http.Request.Headers["Anjal-Key-Id"].ToString().Trim();
        string timestamp = http.Request.Headers["Anjal-Timestamp"].ToString().Trim();
        string signature = http.Request.Headers["Anjal-Signature"].ToString().Trim();
        IResult Refuse(int status, string code, string message, Dictionary<string, object?>? extra = null)
        {
            this.NoteFailure(client, now);
            _ = this.messages.AppendAuditAsync(new AuditEvent { Actor = "app:" + app.Name, Action = "app.api." + code.Replace('_', '-'), Subject = client, Detail = keyId, RemoteAddress = client });
            return Error(new AppApiFailure(status, code, message, extra), requestId);
        }
        if (keyId.Length == 0 || timestamp.Length == 0 || signature.Length == 0)
        {
            return Refuse(401, "signature_required", "This request must be signed: Anjal-Key-Id, Anjal-Timestamp and Anjal-Signature.");
        }
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || Math.Abs(now.ToUnixTimeSeconds() - seconds) > SignatureWindow.TotalSeconds)
        {
            return Refuse(401, "timestamp_out_of_window", "Anjal-Timestamp is more than 5 minutes from Anjal's clock.", new Dictionary<string, object?> { ["serverTime"] = now.ToUnixTimeSeconds() });
        }
        AppSigningKey? key = app.SigningKeys.Find(k => k.Id == keyId);
        if (key is null)
        {
            return Refuse(401, "signature_invalid", "That signing key is not this application's.");
        }
        if (key.Retired is not null)
        {
            return Refuse(403, "signing_key_retired", "That signing key is retired.");
        }
        string target = http.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw ? raw : http.Request.Path + http.Request.QueryString;
        byte[] signed = Encoding.UTF8.GetBytes(SigningString(http.Request.Method, target, timestamp, body));
        if (WebAuthn.FromBase64Url(signature.TrimEnd('=').Replace('+', '-').Replace('/', '_')) is not byte[] sig)
        {
            return Refuse(401, "signature_invalid", "Anjal-Signature is not base64url.");
        }
        bool valid;
        try
        {
            using ECDsa ec = ECDsa.Create();
            ec.ImportFromPem(key.Pem);
            valid = sig.Length == 64
                ? ec.VerifyData(signed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : ec.VerifyData(signed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            valid = false;
        }
        if (!valid)
        {
            return Refuse(401, "signature_invalid", "The signature does not match the request.");
        }
        string mark;
        try
        {
            mark = SignatureMark(sig);
        }
        catch (System.Formats.Asn1.AsnContentException)
        {
            return Refuse(401, "signature_invalid", "Anjal-Signature is not a DER or P1363 signature.");
        }
        if (!await this.Apps.RememberSignatureAsync(keyId, mark, now, http.RequestAborted).ConfigureAwait(false))
        {
            return Refuse(401, "signature_replayed", "This signed request was seen already. Sign each request afresh.");
        }
        return null;
    }

    // The order of the P-256 group: s and n - s are both valid for one signature.
    private static readonly System.Numerics.BigInteger P256Order = System.Numerics.BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// The replay cache's mark of a verified signature (DES-14 §11): r and the lower of s and n - s,
    /// whatever the encoding (P1363 or DER, with or without padding). One signature has one mark, so a
    /// re-encoded or re-signed copy of a seen request is refused as a replay.
    /// </summary>
    /// <param name="sig">The signature's bytes, as verified.</param>
    /// <returns>The mark (hex SHA-256).</returns>
    public static string SignatureMark(byte[] sig)
    {
        ArgumentNullException.ThrowIfNull(sig);
        System.Numerics.BigInteger r;
        System.Numerics.BigInteger sv;
        if (sig.Length == 64)
        {
            r = new System.Numerics.BigInteger(sig.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
            sv = new System.Numerics.BigInteger(sig.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        }
        else
        {
            var reader = new System.Formats.Asn1.AsnReader(sig, System.Formats.Asn1.AsnEncodingRules.DER);
            System.Formats.Asn1.AsnReader seq = reader.ReadSequence();
            r = seq.ReadInteger();
            sv = seq.ReadInteger();
        }
        System.Numerics.BigInteger low = System.Numerics.BigInteger.Min(sv, P256Order - sv);
        byte[] mark = new byte[64];
        r.TryWriteBytes(mark.AsSpan(32 - r.GetByteCount(isUnsigned: true), r.GetByteCount(isUnsigned: true)), out _, isUnsigned: true, isBigEndian: true);
        low.TryWriteBytes(mark.AsSpan(64 - low.GetByteCount(isUnsigned: true), low.GetByteCount(isUnsigned: true)), out _, isUnsigned: true, isBigEndian: true);
        return Convert.ToHexStringLower(SHA256.HashData(mark));
    }

    /// <summary>The application's rate windows now: the minute, the hour and Anjal's day.</summary>
    /// <param name="app">The application.</param>
    /// <param name="now">Now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The windows.</returns>
    public async Task<IReadOnlyList<AppRateWindow>> WindowsAsync(OrgApp app, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        DateTimeOffset day = Anjal.Mailbox.SendingDay.DayStart(now);
        return new[]
        {
            new AppRateWindow("minute", PerMinute, await this.Apps.CountAppSendsAsync(app.Id, minute, ct).ConfigureAwait(false), minute.AddMinutes(1)),
            new AppRateWindow("hour", app.PerHour, await this.Apps.CountAppSendsAsync(app.Id, hour, ct).ConfigureAwait(false), hour.AddHours(1)),
            new AppRateWindow("day", app.PerDay, await this.Apps.CountAppSendsAsync(app.Id, day, ct).ConfigureAwait(false), day.AddDays(1)),
        };
    }

    // §6: the tightest window on every response.
    private async Task RateHeadersAsync(HttpContext http, AppCaller caller)
    {
        DateTimeOffset now = this.Clock();
        AppRateWindow tight = (await this.WindowsAsync(caller.App, now, http.RequestAborted).ConfigureAwait(false)).OrderBy(w => w.Remaining).ThenBy(w => w.Reset).First();
        http.Response.Headers["X-RateLimit-Limit"] = tight.Limit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Remaining"] = tight.Remaining.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Reset"] = ((int)Math.Ceiling((tight.Reset - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A failure as the API answers it: {error, message, requestId, ...}.</summary>
    /// <param name="failure">The failure.</param>
    /// <param name="requestId">The request id.</param>
    /// <returns>The result.</returns>
    public static IResult Error(AppApiFailure failure, string requestId)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var body = new Dictionary<string, object?> { ["error"] = failure.Code, ["message"] = failure.Message, ["requestId"] = requestId };
        foreach ((string k, object? v) in failure.Extra ?? new Dictionary<string, object?>())
        {
            body[k] = v;
        }
        return Results.Json(body, Json, statusCode: failure.Status);
    }

    /// <summary>A JSON answer in the API's naming.</summary>
    /// <param name="value">The value.</param>
    /// <param name="status">The status.</param>
    /// <returns>The result.</returns>
    public static IResult Ok(object? value, int status = 200) => Results.Json(value, Json, statusCode: status);

    /// <summary>The API's JSON options (camelCase, nulls left out).</summary>
    public static JsonSerializerOptions JsonOptions => Json;
}
