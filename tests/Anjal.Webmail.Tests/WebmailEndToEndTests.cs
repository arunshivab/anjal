using System.Net;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// Boots the real <see cref="Program.CreateApp"/> pipeline on an ephemeral
/// port with an in-memory store and drives it over HTTP the way a browser
/// would: cookies, antiforgery tokens, form posts, redirects.
/// </summary>
public sealed class WebmailEndToEndTests : IAsyncLifetime, System.IDisposable
{
    private static readonly string[] ArunRecipient = new[] { "arun@anjal.co.in" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-webmail-e2e-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow mailbox = new();

    public async System.Threading.Tasks.Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery"),
        });

        this.app = Program.CreateApp(System.Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new System.Uri(address.Replace("127.0.0.1:0", "127.0.0.1", System.StringComparison.Ordinal) + "/") };
    }

    public void Dispose()
    {
        this.client.Dispose();
    }

    public async System.Threading.Tasks.Task DisposeAsync()
    {
        this.client.Dispose();
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async System.Threading.Tasks.Task<string> TokenFromAsync(string path)
    {
        HttpResponseMessage page = await this.client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "antiforgery token not found in " + path);
        return m.Groups[1].Value;
    }

    private async System.Threading.Tasks.Task<HttpResponseMessage> PostFormAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        return await this.client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private async System.Threading.Tasks.Task LoginAsync()
    {
        string token = await this.TokenFromAsync("sign-in");
        HttpResponseMessage res = await this.PostFormAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/folder/INBOX", res.Headers.Location!.ToString());
    }

    private async System.Threading.Tasks.Task<MessageRow> DeliverAsync(string raw)
    {
        var sink = new MailboxSink(this.store, this.maildir);
        await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = ArunRecipient,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    [Fact]
    public async System.Threading.Tasks.Task Anonymous_IsRedirectedToLogin_AndBadPasswordIsRefused()
    {
        HttpResponseMessage home = await this.client.GetAsync("folder/INBOX");
        Assert.True(home.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.OK);
        if (home.StatusCode == HttpStatusCode.OK)
        {
            // Static SSR renders the redirect component; it must not show mailbox content.
            Assert.DoesNotContain("Sign out", await home.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        }

        HttpResponseMessage attachment = await this.client.GetAsync($"message/{System.Guid.NewGuid()}/attachment/0");
        Assert.Equal(HttpStatusCode.Redirect, attachment.StatusCode);
        Assert.Contains("/sign-in", attachment.Headers.Location!.ToString(), System.StringComparison.Ordinal);

        string token = await this.TokenFromAsync("sign-in");
        HttpResponseMessage bad = await this.PostFormAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "nope"));
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Equal("/sign-in?error=1", bad.Headers.Location!.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task StyleSheet_IsServedFromTheAssembly_NotTheWorkingDirectory()
    {
        HttpResponseMessage css = await this.client.GetAsync("app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".side", await css.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        HttpResponseMessage tokens = await this.client.GetAsync("tokens.css");
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        Assert.Contains("--brand:", await tokens.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        HttpResponseMessage font = await this.client.GetAsync("fonts/LiPi-Sans-Tamil.woff2");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("font/woff2", font.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", font.Headers.CacheControl!.ToString(), System.StringComparison.Ordinal);

        HttpResponseMessage logo = await this.client.GetAsync("logos/anjal-wordmark.svg");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType!.MediaType);

        HttpResponseMessage script = await this.client.GetAsync("app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("data-connectivity", await script.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        HttpResponseMessage missing = await this.client.GetAsync("fonts/nope.woff2");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("address", "arun@anjal.co.in"),
            new KeyValuePair<string, string>("password", "correct horse battery"),
        });
        HttpResponseMessage res = await this.client.PostAsync("auth/login", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Login_Read_Flag_Trash_Compose_Logout()
    {
        MessageRow delivered = await this.DeliverAsync(
            "From: Sender <sender@example.com>\r\nTo: arun@anjal.co.in\r\nSubject: Hello webmail\r\nMessage-ID: <w1@example.com>\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n\r\n<p>Body <b>bold</b></p><script>alert(1)</script>\r\n");

        await this.LoginAsync();

        // Inbox lists the message.
        HttpResponseMessage inbox = await this.client.GetAsync("folder/INBOX");
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        string inboxHtml = await inbox.Content.ReadAsStringAsync();
        Assert.Contains("Hello webmail", inboxHtml, System.StringComparison.Ordinal);
        Assert.Contains("arun@anjal.co.in", inboxHtml, System.StringComparison.Ordinal);
        Assert.Contains("class=\"unread\"", inboxHtml, System.StringComparison.Ordinal);

        // Open it: sanitised body inside srcdoc, marked seen.
        HttpResponseMessage open = await this.client.GetAsync($"message/{delivered.Id}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        string openHtml = await open.Content.ReadAsStringAsync();
        Assert.Contains("srcdoc=", openHtml, System.StringComparison.Ordinal);
        Assert.Contains("bold", openHtml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("alert(1)", openHtml, System.StringComparison.Ordinal);
        Assert.True((await this.store.GetMessageByIdAsync(delivered.Id))!.Seen);

        // Flag via the form on the message page.
        string token = TokenRegex.Match(openHtml).Groups[1].Value;
        HttpResponseMessage flag = await this.PostFormAsync($"message/{delivered.Id}/flag", token, ("back", $"/message/{delivered.Id}"));
        Assert.Equal(HttpStatusCode.Redirect, flag.StatusCode);
        Assert.True((await this.store.GetMessageByIdAsync(delivered.Id))!.Flagged);

        // Raw download.
        HttpResponseMessage raw = await this.client.GetAsync($"message/{delivered.Id}/raw.eml");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal("message/rfc822", raw.Content.Headers.ContentType!.MediaType);

        // Trash it.
        HttpResponseMessage trash = await this.PostFormAsync($"message/{delivered.Id}/move", token, ("folder", "Trash"), ("back", "/folder/INBOX"));
        Assert.Equal(HttpStatusCode.Redirect, trash.StatusCode);
        HttpResponseMessage trashPage = await this.client.GetAsync("folder/Trash");
        Assert.Contains("Hello webmail", await trashPage.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        // Compose with an attachment (multipart form).
        string composeToken = await this.TokenFromAsync("compose");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(composeToken), "__RequestVerificationToken");
        multipart.Add(new StringContent("Alice <alice@example.com>"), "to");
        multipart.Add(new StringContent(string.Empty), "cc");
        multipart.Add(new StringContent("From the browser"), "subject");
        multipart.Add(new StringContent("Hi Alice"), "body");
        var file = new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("csv,data"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "attachments", "data.csv");
        HttpResponseMessage sent = await this.client.PostAsync("compose", multipart);
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.ToString());

        var queued = await this.store.LeaseOutboundBatchAsync(10, System.DateTimeOffset.UtcNow.AddMinutes(1));
        OutboundMessage outbound = Assert.Single(queued);
        Assert.Equal("alice@example.com", outbound.EnvelopeTo);
        Assert.Contains("data.csv", System.Text.Encoding.ASCII.GetString(outbound.RawBytes), System.StringComparison.Ordinal);

        HttpResponseMessage sentPage = await this.client.GetAsync("folder/Sent?sent=1");
        string sentHtml = await sentPage.Content.ReadAsStringAsync();
        Assert.Contains("From the browser", sentHtml, System.StringComparison.Ordinal);
        Assert.Contains("queued for delivery", sentHtml, System.StringComparison.Ordinal);

        // Validation error round-trips to the compose page.
        string composeToken2 = await this.TokenFromAsync("compose");
        using var invalidForm = new MultipartFormDataContent();
        invalidForm.Add(new StringContent(composeToken2), "__RequestVerificationToken");
        invalidForm.Add(new StringContent("not-an-address"), "to");
        invalidForm.Add(new StringContent("x"), "subject");
        HttpResponseMessage invalid = await this.client.PostAsync("compose", invalidForm);
        Assert.Equal(HttpStatusCode.Redirect, invalid.StatusCode);
        Assert.Contains("/compose?error=", invalid.Headers.Location!.ToString(), System.StringComparison.Ordinal);

        // Logout.
        // Bulk actions from the list: the selection and the action button must
        // live in the same form, or nothing reaches the server.
        MessageRow second = await this.DeliverAsync("Subject: Second message\r\n\r\nbody\r\n");
        string inboxHtml2 = await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync();
        Assert.Contains($"name=\"id\" value=\"{second.Id}\"", inboxHtml2, System.StringComparison.Ordinal);
        Assert.DoesNotContain("form=\"bulkform\"", inboxHtml2, System.StringComparison.Ordinal);

        string bulkToken = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage markRead = await this.PostFormAsync("folder/INBOX/bulk", bulkToken, ("action", "read"), ("id", second.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, markRead.StatusCode);
        Assert.True((await this.store.GetMessageByIdAsync(second.Id))!.Seen, "mark read from the list must apply");

        string bulkToken2 = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage bulkTrash = await this.PostFormAsync("folder/INBOX/bulk", bulkToken2, ("action", "trash"), ("id", second.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, bulkTrash.StatusCode);
        FolderRow trashFolder = Assert.Single(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == "Trash");
        Assert.Equal(trashFolder.Id, (await this.store.GetMessageByIdAsync(second.Id))!.FolderId);

        // Flagging only makes sense where a message is being kept.
        await this.DeliverAsync("Subject: Still in the inbox\r\n\r\nbody\r\n");
        Assert.Contains("class=\"fav", await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        foreach (string folder in new[] { "Junk", "Drafts", "Trash" })
        {
            string html = await (await this.client.GetAsync($"folder/{folder}")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("class=\"fav", html, System.StringComparison.Ordinal);
        }

        // The theme lives on the root element and changes without a fresh sign-in.
        HttpResponseMessage beforeTheme = await this.client.GetAsync("settings");
        Assert.Contains("data-theme=\"paper\"", await beforeTheme.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        string themeToken = await this.TokenFromAsync("settings");
        HttpResponseMessage themed = await this.PostFormAsync("settings/theme", themeToken, ("theme", "midnight"));
        Assert.Equal(HttpStatusCode.Redirect, themed.StatusCode);
        HttpResponseMessage afterTheme = await this.client.GetAsync("settings");
        Assert.Contains("data-theme=\"midnight\"", await afterTheme.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

        // The cookie was re-issued by the theme change, so a form rendered
        // before it is stale; the reader is sent to sign in again, not a 500.
        HttpResponseMessage stale = await this.PostFormAsync("sign-out", composeToken2);
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Equal("/sign-in?expired=1", stale.Headers.Location!.ToString());

        string logoutToken = await this.TokenFromAsync("settings");
        HttpResponseMessage logout = await this.PostFormAsync("sign-out", logoutToken);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        HttpResponseMessage after = await this.client.GetAsync($"message/{delivered.Id}/raw.eml");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async System.Threading.Tasks.Task Attachment_IsServedAsOctetStreamDownload()
    {
        MessageRow delivered = await this.DeliverAsync(
            "Subject: att\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=B\r\n\r\n" +
            "--B\r\nContent-Type: text/plain\r\n\r\nbody\r\n" +
            "--B\r\nContent-Type: text/html; name=\"evil.html\"\r\nContent-Disposition: attachment; filename=\"evil.html\"\r\n\r\n<script>1</script>\r\n--B--\r\n");
        await this.LoginAsync();

        HttpResponseMessage res = await this.client.GetAsync($"message/{delivered.Id}/attachment/0");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("evil.html", res.Content.Headers.ContentDisposition!.ToString(), System.StringComparison.Ordinal);

        HttpResponseMessage missing = await this.client.GetAsync($"message/{delivered.Id}/attachment/9");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
