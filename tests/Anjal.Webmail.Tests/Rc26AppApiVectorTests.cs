using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Anjal.Webmail.AppApi;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// The known-answer test vector for signed application requests (asked for by Sangam, 17 Nov 2026; DES-14 §11): one
/// TEST-ONLY P-256 key, the key id Anjal gives it, and one request with its exact signing string, body hash and a
/// signature that Anjal's own verifier accepts - through the real API, with Anjal's clock held at the request's
/// timestamp - and refuses when one byte of the body changes. Sangam pins the same vector on its side.
/// </summary>
public sealed partial class Rc26AppApiTests
{
    /// <summary>TEST ONLY - never a real key. Its public half.</summary>
    internal const string VectorPublicKey =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGdiauyEJ/xWEd5K4Q7qW7zKmA9cn\n" +
        "H4oUWg69E60A90V5HUH+dXlOrYlRFtu5ouVjdclaepAyhetrzVPTTFyMuQ==\n" +
        "-----END PUBLIC KEY-----\n";

    /// <summary>TEST ONLY - NEVER USE OUTSIDE TESTS. The private half, so the other side can sign the same request.</summary>
    internal const string VectorPrivateKeyTestOnly =
        "-----BEGIN PRIVATE KEY-----\n" +
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgCsBEAMV4i1JC11Kc\n" +
        "mxJbyeFwgnXa0zTLp+Hl927J7sWhRANCAAQZ2Jq7IQn/FYR3krhDupbvMqYD1ycf\n" +
        "ihRaDr0TrQD3RXkdQf51eU6tiVEW27mi5WN1yVp6kDKF62vNU9NMXIy5\n" +
        "-----END PRIVATE KEY-----\n";

    internal const string VectorKeyId = "sk_45bea211c2d5";
    internal const string VectorTarget = "/api/v1/messages/email";
    internal const string VectorTimestamp = "1763380800";
    internal const string VectorBody = "{\"from\":{\"email\":\"no-reply@sgh.in\"},\"to\":[{\"email\":\"priya@gmail.test\"}],\"subject\":\"Known-answer test\",\"text\":\"Test only.\"}";
    internal const string VectorBodySha256 = "953c6d38e8c84ff262466f96bcc01678bc58611b56201ca245b0ad642195e7e6";
    internal const string VectorSigningString = "POST\n/api/v1/messages/email\n1763380800\n953c6d38e8c84ff262466f96bcc01678bc58611b56201ca245b0ad642195e7e6";
    internal const string VectorSignature = "G0DnL6Mma2YJGdATtNJPa6w5vlDGvAMnypgFhpdfj-iuNzSZgUtTK2K5-GZ-erF6xpb7TcSvwyhOELNFpbq_qw";

    [Fact]
    public async Task TheKnownAnswerVector_IsAcceptedByAnjalsVerifier_AndRefusedWhenOneBodyByteChanges()
    {
        // The pieces, as Anjal computes them.
        byte[] body = Encoding.UTF8.GetBytes(VectorBody);
        Assert.Equal(VectorBodySha256, Convert.ToHexStringLower(SHA256.HashData(body)));
        Assert.Equal(VectorSigningString, AppApiService.SigningString("POST", VectorTarget, VectorTimestamp, body));
        Assert.Null(await this.svc.AddSigningKeyAsync(this.sgh, this.appId, VectorPublicKey));
        Assert.Equal(VectorKeyId, (await this.svc.AppOfAsync(this.sgh.Id, this.appId))!.SigningKeys.Single().Id);
        // The private half is the public half's pair (so the other side signs with the same key).
        using (ECDsa priv = ECDsa.Create())
        {
            priv.ImportFromPem(VectorPrivateKeyTestOnly);
            Assert.Equal(VectorPublicKey, priv.ExportSubjectPublicKeyInfoPem() + "\n");
        }
        Assert.Null(await this.svc.SaveAppApiSettingsAsync(this.sgh, this.appId, string.Empty, 200, 1000, string.Empty, signingRequired: true));
        AppApiService api = this.app.Services.GetRequiredService<AppApiService>();
        Func<DateTimeOffset> clock = api.Clock;
        api.Clock = () => DateTimeOffset.FromUnixTimeSeconds(long.Parse(VectorTimestamp, System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            HttpClient c = this.Client();
            // One body byte changed ("Test only." to "Test only!"): the '.' at 0-based offset 119, the 120th of the
            // body's 122 bytes (offset 120 is the closing quote; Sangam's tests, 25 Nov 2026). The same signature is refused.
            byte[] changed = Encoding.UTF8.GetBytes(VectorBody.Replace("only.", "only!", StringComparison.Ordinal));
            Assert.Equal(122, body.Length);
            Assert.Equal(body.Length, changed.Length);
            Assert.Equal((byte)'"', body[120]);
            Assert.Equal(119, Enumerable.Range(0, body.Length).Single(i => body[i] != changed[i]));
            using (HttpRequestMessage tampered = VectorRequest(changed))
            {
                HttpResponseMessage refused = await c.SendAsync(tampered);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                Assert.Equal("signature_invalid", await ErrorOf(refused));
            }
            // The vector itself: accepted, and the message taken.
            using HttpRequestMessage good = VectorRequest(body);
            HttpResponseMessage accepted = await c.SendAsync(good);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        finally
        {
            api.Clock = clock;
        }
    }

    private HttpRequestMessage VectorRequest(byte[] body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, VectorTarget.TrimStart('/'));
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("X-Api-Key", this.liveKey);
        req.Headers.Add("Anjal-Key-Id", VectorKeyId);
        req.Headers.Add("Anjal-Timestamp", VectorTimestamp);
        req.Headers.Add("Anjal-Signature", VectorSignature);
        return req;
    }
}
