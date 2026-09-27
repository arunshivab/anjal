using System.Net;
using Anjal.Mailbox;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.7: HEAD is answered (it returned 405), and static files - never
/// pages - are compressed, each variant with its own ETag.
/// </summary>
public sealed class Rc7AssetDeliveryTests : IAsyncLifetime, System.IDisposable
{
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc7-assets-" + System.Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        this.app = Program.CreateApp(System.Array.Empty<string>(), new InMemoryMessageStore(), new MaildirStore(this.root, "test"), "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        this.client = new HttpClient(handler) { BaseAddress = new System.Uri(address.Replace("127.0.0.1:0", "127.0.0.1", System.StringComparison.Ordinal) + "/") };
    }

    public void Dispose() => this.client.Dispose();

    public async Task DisposeAsync()
    {
        this.client.Dispose();
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? acceptEncoding = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }
        return this.client.SendAsync(request);
    }

    [Fact]
    public async Task Head_OnAStylesheetAndOnAPage_Is200_WithHeadersAndNoBody()
    {
        HttpResponseMessage css = await this.SendAsync(HttpMethod.Head, "app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.True(css.Content.Headers.ContentLength > 0);
        Assert.Empty(await css.Content.ReadAsByteArrayAsync());
        HttpResponseMessage page = await this.SendAsync(HttpMethod.Head, "sign-in");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Empty(await page.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task Stylesheet_IsCompressed_AndDecompressesToTheOriginal(string coding)
    {
        byte[] original = await (await this.SendAsync(HttpMethod.Get, "app.css")).Content.ReadAsByteArrayAsync();
        HttpResponseMessage packed = await this.SendAsync(HttpMethod.Get, "app.css", coding);
        Assert.Equal(coding, Assert.Single(packed.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", packed.Headers.Vary);
        byte[] body = await packed.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length < original.Length);
        using var input = new System.IO.MemoryStream(body);
        using System.IO.Stream reader = coding == "br"
            ? new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress)
            : new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new System.IO.MemoryStream();
        await reader.CopyToAsync(output);
        Assert.Equal(original, output.ToArray());
        Assert.NotEqual((await this.SendAsync(HttpMethod.Get, "app.css")).Headers.ETag, packed.Headers.ETag);
    }

    [Fact]
    public async Task NoCompression_WhenNotAsked_OrRefused()
    {
        Assert.Empty((await this.SendAsync(HttpMethod.Get, "app.js")).Content.Headers.ContentEncoding);
        Assert.Empty((await this.SendAsync(HttpMethod.Get, "app.js", "br;q=0, gzip;q=0")).Content.Headers.ContentEncoding);
        Assert.Equal("gzip", Assert.Single((await this.SendAsync(HttpMethod.Get, "app.js", "br;q=0, gzip")).Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task Pages_AreNeverCompressed()
    {
        // Pages carry security tokens and mail; compressing them would open BREACH.
        HttpResponseMessage page = await this.SendAsync(HttpMethod.Get, "sign-in", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Empty(page.Content.Headers.ContentEncoding);
    }
}
