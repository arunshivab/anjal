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
        Assert.Equal("/dashboard", res.Headers.Location!.ToString());
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
        Assert.StartsWith("/sign-in?error=1&u=", bad.Headers.Location!.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("class=\"mrow unread\"", inboxHtml, System.StringComparison.Ordinal);

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
        // UX-07: from an open message too, the move comes back with its Undo.
        Assert.Matches("^/folder/INBOX\\?undo=[0-9a-f-]{36}&moved=1&to=Trash$", trash.Headers.Location!.ToString());
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
        string beforeHtml = await beforeTheme.Content.ReadAsStringAsync();
        Assert.Contains("data-theme=\"anjal-light\"", beforeHtml, System.StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\"", beforeHtml, System.StringComparison.Ordinal);
        string themeToken = await this.TokenFromAsync("settings");
        HttpResponseMessage themed = await this.PostFormAsync("settings/theme", themeToken, ("theme", "plum-dark"));
        Assert.Equal(HttpStatusCode.Redirect, themed.StatusCode);
        HttpResponseMessage afterTheme = await this.client.GetAsync("settings");
        Assert.Contains("data-theme=\"plum-dark\"", await afterTheme.Content.ReadAsStringAsync(), System.StringComparison.Ordinal);

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

    [Fact]
    public async System.Threading.Tasks.Task TheFrame_HasTheRailAndStatusBar_NoTopBar_AndTheRailFoldIsRemembered()
    {
        // rc.11 part A3 (SPEC-11 items 43, 44, 46).
        await this.LoginAsync();
        string inbox = await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync();
        // rc.15: until the person chooses, the rail folds itself on laptop screens; both buttons are drawn.
        Assert.Contains("class=\"rail auto\"", inbox, System.StringComparison.Ordinal);
        Assert.Contains("rf-open", inbox, System.StringComparison.Ordinal);
        Assert.Contains("rf-fold", inbox, System.StringComparison.Ordinal);
        Assert.Contains("class=\"statusbar\"", inbox, System.StringComparison.Ordinal);
        // rc.15: the script's own sentences come from the word list, and the draft state has its place (item 46).
        Assert.Contains("<script type=\"application/json\" id=\"anjal-words\">", inbox, System.StringComparison.Ordinal);
        Assert.Contains("data-st-draft hidden", inbox, System.StringComparison.Ordinal);
        Assert.Contains("data-shared=\"0\"", inbox, System.StringComparison.Ordinal);
        Assert.Contains("arun@anjal.co.in", inbox, System.StringComparison.Ordinal);
        Assert.Contains("class=\"st-signout\"", inbox, System.StringComparison.Ordinal);
        Assert.Contains("class=\"listsearch\"", inbox, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<header class=\"top\"", inbox, System.StringComparison.Ordinal);

        // Folding returns to the same page, and the next page remembers it.
        string token = await this.TokenFromAsync("folder/Sent");
        HttpResponseMessage fold = await this.PostFormAsync("settings/rail", token, ("folded", "1"), ("back", "/folder/Sent"));
        Assert.Equal(HttpStatusCode.Redirect, fold.StatusCode);
        Assert.Equal("/folder/Sent", fold.Headers.Location!.ToString());
        Assert.Contains("class=\"rail folded\"", await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
        Assert.True((await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.RailFolded);

        // A crafted return address elsewhere is refused: back to the inbox.
        token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage open = await this.PostFormAsync("settings/rail", token, ("folded", "0"), ("back", "//evil.example/steal"));
        Assert.Equal("/folder/INBOX", open.Headers.Location!.ToString());
        Assert.False((await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.RailFolded);
        Assert.True((await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.RailChosen);
        Assert.Contains("class=\"rail \"", await (await this.client.GetAsync("folder/INBOX")).Content.ReadAsStringAsync(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheList_FiltersUndoSoundPerPageScoreAndDayGroups()
    {
        // rc.11 part A4 (SPEC-11 items 7, 11, 14, 50, 57; UX-07).
        await this.LoginAsync();
        MessageRow first = await this.DeliverAsync("From: Meera <m@example.com>\r\nSubject: Read one\r\n\r\nx\r\n");
        await this.DeliverAsync("From: Ravi <r@example.com>\r\nSubject: Unread one\r\n\r\nx\r\n");
        await this.store.SetMessageFlagsAsync(first.Id, true, false, false, null);

        string all = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("Read one", all, System.StringComparison.Ordinal);
        Assert.Contains("Unread one", all, System.StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"mgroup-day\">Today</h2>", all, System.StringComparison.Ordinal);
        string unreadOnly = await this.client.GetStringAsync("folder/INBOX?show=unread");
        Assert.Contains("Unread one", unreadOnly, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Read one", unreadOnly, System.StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Unread</a>", unreadOnly, System.StringComparison.Ordinal);
        string readOnly = await this.client.GetStringAsync("folder/INBOX?show=read");
        Assert.Contains("Read one", readOnly, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Unread one", readOnly, System.StringComparison.Ordinal);

        // Delete, then Undo: the bar offers it, and the message comes back.
        string token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage deleted = await this.PostFormAsync("folder/INBOX/bulk", token, ("action", "trash"), ("id", first.Id.ToString()), ("page", "0"));
        string afterDelete = deleted.Headers.Location!.ToString();
        Assert.Contains("undo=", afterDelete, System.StringComparison.Ordinal);
        string withBar = await this.client.GetStringAsync(afterDelete.TrimStart('/'));
        Assert.Contains("class=\"undobar\"", withBar, System.StringComparison.Ordinal);
        Assert.Contains("1 moved to Trash.", withBar, System.StringComparison.Ordinal);
        Match undoToken = Regex.Match(withBar, "name=\"token\" value=\"([0-9a-f-]{36})\"");
        Assert.True(undoToken.Success);
        token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage undone = await this.PostFormAsync("folder/undo", token, ("token", undoToken.Groups[1].Value), ("back", "/folder/INBOX?page=0"));
        Assert.Equal("/folder/INBOX?page=0&undone=1", undone.Headers.Location!.ToString());
        FolderRow inbox = Assert.Single(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == FolderRow.Inbox);
        Assert.Equal(inbox.Id, (await this.store.GetMessageByIdAsync(first.Id))!.FolderId);

        // The sound, on by default, turned off; messages per page saved.
        Assert.Contains("data-sound=\"1\"", all, System.StringComparison.Ordinal);
        token = await this.TokenFromAsync("folder/INBOX");
        await this.PostFormAsync("settings/sound", token, ("on", "0"), ("back", "/folder/INBOX"));
        token = await this.TokenFromAsync("folder/INBOX");
        await this.PostFormAsync("settings/pagesize", token, ("size", "20"), ("back", "/folder/INBOX"));
        string after = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("data-sound=\"0\"", after, System.StringComparison.Ordinal);
        Assert.Contains("<option value=\"20\" selected", after, System.StringComparison.Ordinal);

        // Search shows the score too (item 57: "every message list").
        string search = await this.client.GetStringAsync("search?q=one&scope=all");
        Assert.Contains("class=\"score", search, System.StringComparison.Ordinal);
        Assert.Contains("class=\"mfolder\">Inbox</span>", search, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheLetterAndTheEnvelope_BlockPrintAndShowOriginal()
    {
        // rc.11 part A5 (SPEC-11 items 19, 20, 42; D-103).
        await this.LoginAsync();
        MessageRow m = await this.DeliverAsync("From: Meera Iyer <meera@partner.example>\r\nTo: Arun <arun@anjal.co.in>\r\nSubject: <script>alert(1)</script> Duty roster\r\n\r\nPlease see the roster.\r\n");
        string page = await this.client.GetStringAsync($"message/{m.Id}");

        // Item 42: the letter begins with the subject - nothing above it.
        Assert.Matches("<article class=\"letter\"[^>]*>\\s*<h1 class=\"lsubj\">", page);
        Assert.Contains("aria-label=\"Envelope\"", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<aside class=\"rail\"", page, System.StringComparison.Ordinal);   // no clash with the left rail
        // Item 19: the flag button carries its symbol. Item 20: name and address on one line.
        Assert.Matches("<span class=\"fav[^\"]*\" aria-hidden=\"true\" lang=\"ta\">\u0B85</span>Flag", page);
        Assert.Contains("<b>Meera Iyer</b><span class=\"eaddr\"> &lt;meera@partner.example&gt;</span>", page, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/message/" + m.Id + "/original\"", page, System.StringComparison.Ordinal);
        Assert.Contains("href=\"/message/" + m.Id + "/print\"", page, System.StringComparison.Ordinal);

        // Block sender: the rule is stored, the message stays where it is.
        string token = await this.TokenFromAsync($"message/{m.Id}");
        HttpResponseMessage blocked = await this.PostFormAsync($"message/{m.Id}/block", token);
        Assert.Equal($"/message/{m.Id}?blocked=meera%40partner.example", blocked.Headers.Location!.ToString());
        Assert.Contains("Future mail from meera@partner.example goes to Junk.", await this.client.GetStringAsync($"message/{m.Id}?blocked=meera%40partner.example"), System.StringComparison.Ordinal);
        Assert.Contains(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id), r => r.Pattern == "meera@partner.example" && r.Action == SenderRuleAction.Block);
        FolderRow inbox = Assert.Single(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == FolderRow.Inbox);
        Assert.Equal(inbox.Id, (await this.store.GetMessageByIdAsync(m.Id))!.FolderId);

        // Print: the strict policy, and the subject shown as text, never run.
        HttpResponseMessage print = await this.client.GetAsync($"message/{m.Id}/print");
        string printed = await print.Content.ReadAsStringAsync();
        Assert.StartsWith("default-src 'none'; script-src 'self';", print.Headers.GetValues("Content-Security-Policy").Single(), System.StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; Duty roster", printed, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)", printed, System.StringComparison.Ordinal);
        Assert.Contains("Please see the roster.", printed, System.StringComparison.Ordinal);
        Assert.Contains("data-autoprint", printed, System.StringComparison.Ordinal);

        // Show original: the raw message as text; a message that is not there shows nothing.
        string original = await this.client.GetStringAsync($"message/{m.Id}/original");
        Assert.Contains("Subject: &lt;script&gt;alert(1)&lt;/script&gt; Duty roster", original, System.StringComparison.Ordinal);
        Assert.Contains("class=\"rawtext\"", original, System.StringComparison.Ordinal);
        string missing = await this.client.GetStringAsync($"message/{System.Guid.NewGuid()}/original");
        Assert.DoesNotContain("class=\"rawtext\"", missing, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task ManyRecipients_CollapseAboveFive_WithTheSummaryAndYouFirst()
    {
        // rc.11 part A5 (SPEC-11 item 52, reading).
        await this.LoginAsync();
        string to = "Arun <arun@anjal.co.in>, " + string.Join(", ", System.Linq.Enumerable.Range(1, 4).Select(i => $"Colleague {i} <c{i}@anjal.co.in>"));
        MessageRow m = await this.DeliverAsync($"From: Board office <board@anjal.co.in>\r\nTo: {to}\r\nCc: Auditor <audit@firm.example>, Lab <lab@partner.example>\r\nSubject: Agenda\r\n\r\nx\r\n");
        string page = await this.client.GetStringAsync($"message/{m.Id}");
        Assert.Contains("7 people: 5 in the organisation, 2 outside", page, System.StringComparison.Ordinal);
        Assert.Matches("(?:\\+|&#x2B;)2 more", page);   // the page writes "+" as &#x2B;, shown as "+"
        Assert.Matches("<div class=\"echips\">\\s*<span class=\"echip \"[^>]*><i>To</i>You</span>", page);
        Assert.Contains("data-findperson", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Layouts_ThreePanesFocusAndListOnly_AndDensity()
    {
        // rc.11 part A6 (SPEC-11 items 44, 45, 50).
        await this.LoginAsync();
        MessageRow m = await this.DeliverAsync("From: Ravi <r@example.com>\r\nSubject: Ward rounds\r\n\r\nx\r\n");
        await this.DeliverAsync("From: Lab <l@example.com>\r\nSubject: Results\r\n\r\nx\r\n");

        // Three panes (the default): a row opens beside the list.
        string list = await this.client.GetStringAsync("folder/INBOX");
        string openLink = $"/folder/INBOX?page=0&amp;open={m.Id}";
        Assert.Contains($"href=\"{openLink}\"", list, System.StringComparison.Ordinal);
        Assert.Contains("2 unread", list, System.StringComparison.Ordinal);
        string three = await this.client.GetStringAsync($"folder/INBOX?page=0&open={m.Id}");
        Assert.Contains("class=\"fview layout-three has-open\"", three, System.StringComparison.Ordinal);
        Assert.Contains("<section class=\"listcol\">", three, System.StringComparison.Ordinal);
        Assert.Contains("<article class=\"letter\"", three, System.StringComparison.Ordinal);
        Assert.Contains("class=\"mrow open\" aria-current=\"true\" data-message=\"" + m.Id, three, System.StringComparison.Ordinal);
        Assert.Contains("1 unread", three, System.StringComparison.Ordinal);
        Assert.True((await this.store.GetMessageByIdAsync(m.Id))!.Seen);

        // Focus: the letter and the envelope only, with the way back; the rail stays.
        string token = await this.TokenFromAsync("folder/INBOX");
        await this.PostFormAsync("settings/layout", token, ("layout", "focus"), ("back", "/folder/INBOX"));
        string focus = await this.client.GetStringAsync($"folder/INBOX?page=0&open={m.Id}");
        Assert.Contains("class=\"fview layout-focus has-open\"", focus, System.StringComparison.Ordinal);
        Assert.Contains("class=\"eback\"", focus, System.StringComparison.Ordinal);
        Assert.Contains("class=\"rail ", focus, System.StringComparison.Ordinal);

        // List only: a row opens on its own page.
        token = await this.TokenFromAsync("folder/INBOX");
        await this.PostFormAsync("settings/layout", token, ("layout", "list"), ("back", "/folder/INBOX"));
        Assert.Contains($"href=\"/message/{m.Id}\"", await this.client.GetStringAsync("folder/INBOX"), System.StringComparison.Ordinal);

        // Density.
        token = await this.TokenFromAsync("folder/INBOX");
        await this.PostFormAsync("settings/layout", token, ("density", "compact"), ("back", "/folder/INBOX"));
        string compact = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("class=\"frame density-compact\"", compact, System.StringComparison.Ordinal);
        Assert.Equal("list", (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.Layout);
    }

    [Fact]
    public async System.Threading.Tasks.Task Settings_AppearanceAndLanguageAndTime_ApplyAtOnce()
    {
        // rc.11 part A9 (SPEC-11 items 36, 40, 41, 45; D-92).
        await this.LoginAsync();
        string appearance = await this.client.GetStringAsync("settings/appearance");
        Assert.Equal(21, Regex.Count(appearance, "type=\"radio\" name=\"colour\"")); // DES-11 D4: 21 colours
        Assert.Matches("value=\"anjal\" checked", appearance);
        Assert.Contains("Changes apply at once. Only you see them.", appearance, System.StringComparison.Ordinal);

        string token = await this.TokenFromAsync("settings/appearance");
        HttpResponseMessage saved = await this.PostFormAsync("settings/appearance", token, ("colour", "plum"), ("mode", "dark"), ("density", "compact"), ("layout", "focus"));
        Assert.Equal("/settings/appearance?saved=appearance", saved.Headers.Location!.ToString());
        Assert.Contains("data-theme=\"plum-dark\"", await this.client.GetStringAsync("folder/INBOX"), System.StringComparison.Ordinal);
        MailboxRow m = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!;
        Assert.Equal(("plum-dark", "compact", "focus"), (m.Theme, m.Density, m.Layout));

        string language = await this.client.GetStringAsync("settings/language");
        Assert.Matches("value=\"en\" checked", language);
        Assert.Matches("value=\"ta\"[^>]*disabled", language);
        Assert.Contains("coming soon", language, System.StringComparison.Ordinal);
        Assert.Matches("<option value=\"Asia/Kolkata\" selected[^>]*>\\(UTC(?:\\+|&#x2B;)05:30\\) Asia - Kolkata</option>", language);   // "+" is written &#x2B;

        // A language not switched on is refused even when forced (D-92); the rest is saved.
        token = await this.TokenFromAsync("settings/language");
        await this.PostFormAsync("settings/language", token, ("language", "ta"), ("timeZone", "Europe/London"), ("dateFormat", "yyyy-mm-dd"), ("weekStart", "sunday"));
        m = (await this.store.GetMailboxByIdAsync(this.mailbox.Id))!;
        Assert.Equal(("en", "Europe/London", "yyyy-mm-dd", "sunday"), (m.Language, m.TimeZone, m.DateFormat, m.WeekStart));
        string next = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("data-tz=\"Europe/London\"", next, System.StringComparison.Ordinal);
        Assert.Matches("(GMT|BST)</span>", next);
        Assert.Contains("<html lang=\"en\"", next, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheShortcutsPanel_IsOnEveryPage_AndTheKeysHaveTheirTargets()
    {
        // rc.11 part A10 (SPEC-11 item 38, UX-03).
        await this.LoginAsync();
        MessageRow m = await this.DeliverAsync("From: Ravi <r@example.com>\r\nSubject: Keys\r\n\r\nx\r\n");
        foreach (string page in new[] { "folder/INBOX", $"message/{m.Id}", "settings/appearance", "compose" })
        {
            string html = await this.client.GetStringAsync(page);
            Assert.Contains("id=\"shortcuts\" class=\"kbpanel\"", html, System.StringComparison.Ordinal);
            Assert.Contains("href=\"#shortcuts\"", html, System.StringComparison.Ordinal);
        }
        string reader = await this.client.GetStringAsync($"message/{m.Id}");
        Assert.Contains($"data-reader-id=\"{m.Id}\"", reader, System.StringComparison.Ordinal);
        Assert.Contains("data-kb=\"flag\"", reader, System.StringComparison.Ordinal);
        Assert.Contains("data-kb=\"delete\"", reader, System.StringComparison.Ordinal);
        Assert.Contains("data-send", await this.client.GetStringAsync("compose"), System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Archive_IsStandard_FromTheListWithUndo_AndFromTheEnvelope()
    {
        // rc.11 part A10c (D-106).
        await this.LoginAsync();
        MessageRow a = await this.DeliverAsync("From: Ravi <r@example.com>\r\nSubject: Done with this\r\n\r\nx\r\n");
        MessageRow b2 = await this.DeliverAsync("From: Lab <l@example.com>\r\nSubject: Also done\r\n\r\nx\r\n");
        string inbox = await this.client.GetStringAsync("folder/INBOX");
        Assert.Matches("href=\"/folder/Sent\"[\\s\\S]*href=\"/folder/Archive\"[\\s\\S]*href=\"/folder/Junk\"", inbox);
        Assert.Contains("name=\"action\" value=\"archive\"", inbox, System.StringComparison.Ordinal);

        string token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage archived = await this.PostFormAsync("folder/INBOX/bulk", token, ("action", "archive"), ("id", a.Id.ToString()), ("page", "0"));
        Assert.Contains("undo=", archived.Headers.Location!.ToString(), System.StringComparison.Ordinal);
        Assert.Contains("moved=1&to=Archive", archived.Headers.Location!.ToString(), System.StringComparison.Ordinal);
        FolderRow archive = Assert.Single(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == "Archive");
        Assert.Equal(archive.Id, (await this.store.GetMessageByIdAsync(a.Id))!.FolderId);

        string reading = await this.client.GetStringAsync($"message/{b2.Id}");
        Assert.Contains("data-kb=\"archive\"", reading, System.StringComparison.Ordinal);
        Assert.DoesNotContain("data-kb=\"archive\"", await this.client.GetStringAsync($"message/{a.Id}"), System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Welcome_ShownOnce_ThenTheTourOrSkip_AndRestartable()
    {
        // rc.11 part A10b (SPEC-11 item 5, D-105).
        await this.LoginAsync();
        string first = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("class=\"welcome\"", first, System.StringComparison.Ordinal);
        Assert.Contains("Welcome to Anjal", first, System.StringComparison.Ordinal);
        Assert.Contains("Restart the tour", first, System.StringComparison.Ordinal);
        Match steps = Regex.Match(first, "data-steps=\"([^\"]*)\"");
        Assert.True(steps.Success);
        Assert.Equal(10, Regex.Count(System.Net.WebUtility.HtmlDecode(steps.Groups[1].Value), "\"at\":"));

        string token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage around = await this.PostFormAsync("settings/welcome", token, ("tour", "1"));
        Assert.Equal("/folder/INBOX#tour", around.Headers.Location!.ToString());
        Assert.True((await this.store.GetMailboxByIdAsync(this.mailbox.Id))!.WelcomeDone);
        Assert.DoesNotContain("class=\"welcome\"", await this.client.GetStringAsync("folder/INBOX"), System.StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"welcome\"", await this.client.GetStringAsync("settings/appearance"), System.StringComparison.Ordinal);

        // Skip returns to the same page, and the answer is kept.
        token = await this.TokenFromAsync("folder/INBOX");
        HttpResponseMessage skip = await this.PostFormAsync("settings/welcome", token, ("back", "/folder/Sent"));
        Assert.Equal("/folder/Sent", skip.Headers.Location!.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task ThePhone_BarsSheetsPinnedSummaryAndActions()
    {
        // rc.11 part A11 (the approved phone boards; SPEC-11 item 20; D-97).
        await this.LoginAsync();
        await this.store.EnsureFolderAsync(this.mailbox.Id, "Suppliers");
        string to = "Arun <arun@anjal.co.in>, " + string.Join(", ", System.Linq.Enumerable.Range(1, 4).Select(i => $"Colleague {i} <c{i}@anjal.co.in>"));
        MessageRow m = await this.DeliverAsync($"From: Quality committee <quality@anjal.co.in>\r\nTo: {to}\r\nCc: Auditor <audit@firm.example>, Lab <lab@partner.example>\r\nSubject: Quality week\r\n\r\nx\r\n");

        string list = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("<header class=\"phonetop\">", list, System.StringComparison.Ordinal);
        Assert.Contains("class=\"pcompose\"", list, System.StringComparison.Ordinal);
        Assert.Contains("id=\"folders\" class=\"phonesheet\"", list, System.StringComparison.Ordinal);
        Assert.Matches("class=\"paccount\"[\\s\\S]*action=\"/sign-out\"", list);
        Assert.Contains("class=\"pgroup\">Your folders</span>", list, System.StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(list, "data-connectivity"));

        string reading = await this.client.GetStringAsync($"message/{m.Id}");
        Assert.Contains("class=\"phonesummary\" href=\"#envelope\"", reading, System.StringComparison.Ordinal);
        Assert.Contains("to you and 6 others, 2 outside.", reading, System.StringComparison.Ordinal);
        Assert.Contains("id=\"envelope\"", reading, System.StringComparison.Ordinal);
        Assert.Contains("Reply all (7)", reading, System.StringComparison.Ordinal);
        Assert.Contains("class=\"phoneback\" href=\"/folder/INBOX\"", reading, System.StringComparison.Ordinal);

        string three = await this.client.GetStringAsync($"folder/INBOX?page=0&open={m.Id}");
        Assert.Contains("class=\"focusbar phoneonly\"", three, System.StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AListedMessageWhoseFileIsMissing_SaysSo_NotThatItIsGone()
    {
        // Owner, 6 Oct: QA messages listed in the folder opened as "no longer here"
        // because their files were under another mail folder. Say what is true.
        await this.LoginAsync();
        MessageRow m = await this.DeliverAsync("From: Ravi <r@example.com>\r\nSubject: Listed but missing\r\n\r\nx\r\n");
        foreach (string f in System.IO.Directory.GetFiles(this.root, System.IO.Path.GetFileName(m.MaildirFile) + "*", System.IO.SearchOption.AllDirectories))
        {
            System.IO.File.Delete(f);
        }
        string missing = await (await this.client.GetAsync($"message/{m.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("stored copy cannot be read", missing, System.StringComparison.Ordinal);
        Assert.Contains("ANJAL_MAILDIR_ROOT", missing, System.StringComparison.Ordinal);
        Assert.DoesNotContain("That message is no longer here.", missing, System.StringComparison.Ordinal);
        string gone = await (await this.client.GetAsync($"message/{System.Guid.NewGuid()}")).Content.ReadAsStringAsync();
        Assert.Contains("That message is no longer here.", gone, System.StringComparison.Ordinal);
    }
}
