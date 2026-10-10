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
/// rc.12 through the real pipeline: "Keep this draft?" (item 17), the row's
/// own hover actions (D-116), the one-line list (D-117) and the fuller
/// formatting bar (item 15).
/// </summary>
public sealed class Rc12ComposeAndRowTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc12-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow mailbox = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
    }

    public async Task DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(path)).Content.ReadAsStringAsync();
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token on " + path);
        return m.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        foreach ((string k, string v) in fields)
        {
            form.Add(new KeyValuePair<string, string>(k, v));
        }
        return await this.client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private async Task SignInAsync()
    {
        string token = await this.TokenAsync("sign-in");
        HttpResponseMessage res = await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<MessageRow> DeliverAsync(string raw, string envelopeFrom = "s@x.test")
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = Arun,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private async Task<Guid> SaveDraftAsync(string subject)
    {
        string token = await this.TokenAsync("compose");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("half typed"), "to");
        form.Add(new StringContent(subject), "subject");
        form.Add(new StringContent("Some words"), "body");
        HttpResponseMessage res = await this.client.PostAsync("draft", form);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        string location = res.Headers.Location!.ToString();
        Match m = Regex.Match(location, "/draft/([0-9a-f-]{36})", RegexOptions.CultureInvariant);
        Assert.True(m.Success, location);
        return Guid.Parse(m.Groups[1].Value);
    }

    [Fact]
    public async Task KeepThisDraft_Discard_RemovesOnlyADraft()
    {
        await this.SignInAsync();
        MessageRow inboxMail = await this.DeliverAsync("From: a@x.test\r\nSubject: Keep me\r\n\r\nx\r\n");
        Guid draft = await this.SaveDraftAsync("Half written");
        Assert.Contains(this.store.MailboxMessages, m => m.Id == draft);

        string token = await this.TokenAsync("compose");
        HttpResponseMessage res = await this.PostAsync("draft/discard", token, ("draftId", draft.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/folder/INBOX", res.Headers.Location!.ToString());
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == draft);

        // A message that is not a draft is never removed this way.
        await this.PostAsync("draft/discard", token, ("draftId", inboxMail.Id.ToString()));
        Assert.Contains(this.store.MailboxMessages, m => m.Id == inboxMail.Id);

        // Nothing saved yet, or a malformed id: simply back to INBOX.
        res = await this.PostAsync("draft/discard", token, ("draftId", string.Empty));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public async Task KeepThisDraft_IsPageMarkup_AndCancelAsksFirst()
    {
        await this.SignInAsync();
        string html = await this.client.GetStringAsync("compose");
        Assert.Contains("data-keepdraft", html, StringComparison.Ordinal);
        Assert.Contains("Keep this draft?", html, StringComparison.Ordinal);
        Assert.Contains("formaction=\"/draft/discard\"", html, StringComparison.Ordinal);
        Assert.Contains("data-keepediting", html, StringComparison.Ordinal);
        Assert.Contains("data-cancel", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardNeedsTheToken()
    {
        await this.SignInAsync();
        Guid draft = await this.SaveDraftAsync("Still here");
        await this.client.PostAsync("draft/discard", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("draftId", draft.ToString()) }));
        Assert.Contains(this.store.MailboxMessages, m => m.Id == draft);
    }

    [Fact]
    public async Task RowActions_ActOnThatMessageAlone_AndCanBeUndone()
    {
        await this.SignInAsync();
        MessageRow a = await this.DeliverAsync("From: a@x.test\r\nSubject: A\r\n\r\nx\r\n");
        MessageRow b = await this.DeliverAsync("From: a@x.test\r\nSubject: B\r\n\r\nx\r\n");
        string html = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("formaction=\"/folder/INBOX/bulk?one=" + a.Id + "\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"rowacts\"", html, StringComparison.Ordinal);

        // B is ticked too, but the row's own Archive acts on A alone.
        string token = await this.TokenAsync("folder/INBOX");
        HttpResponseMessage res = await this.PostAsync("folder/INBOX/bulk?one=" + a.Id, token, ("action", "archive"), ("id", b.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        string location = res.Headers.Location!.ToString();
        Assert.Contains("undo=", location, StringComparison.Ordinal);
        FolderRow archive = (await this.store.ListFoldersAsync(this.mailbox.Id)).Single(f => f.Name == "Archive");
        FolderRow inbox = (await this.store.ListFoldersAsync(this.mailbox.Id)).Single(f => f.Name == FolderRow.Inbox);
        Assert.Equal(archive.Id, this.store.MailboxMessages.Single(m => m.Id == a.Id).FolderId);
        Assert.Equal(inbox.Id, this.store.MailboxMessages.Single(m => m.Id == b.Id).FolderId);

        // Read and unread from the row.
        await this.PostAsync("folder/INBOX/bulk?one=" + b.Id, token, ("action", "read"));
        Assert.True(this.store.MailboxMessages.Single(m => m.Id == b.Id).Seen);
    }

    [Fact]
    public async Task RowActions_InTrash_OfferRestoreOnly()
    {
        await this.SignInAsync();
        MessageRow a = await this.DeliverAsync("From: a@x.test\r\nSubject: A\r\n\r\nx\r\n");
        string token = await this.TokenAsync("folder/INBOX");
        await this.PostAsync("folder/INBOX/bulk", token, ("action", "trash"), ("id", a.Id.ToString()));
        string html = await this.client.GetStringAsync("folder/Trash");
        Match acts = Regex.Match(html, "<span class=\"rowacts\">(.*?)</span>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(acts.Success);
        Assert.Contains("value=\"restore\"", acts.Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"archive\"", acts.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormatBar_HasTheFullSet_WithTrueNamedColours()
    {
        await this.SignInAsync();
        string html = await this.client.GetStringAsync("compose");
        foreach (string cmd in FormatCommands)
        {
            Assert.Contains("data-cmd=\"" + cmd + "\"", html, StringComparison.Ordinal);
        }
        Assert.Contains("data-cmd-select=\"fontSize\"", html, StringComparison.Ordinal);
        Assert.Contains("title=\"Crimson\"", html, StringComparison.Ordinal);
        Assert.Contains("--sw:#dc143c", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compose_IsTheLetterAndTheEnvelope_BesideTheInbox()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: In the list\r\n\r\nx\r\n");
        string html = await this.client.GetStringAsync("compose");
        Assert.Contains("class=\"fview layout-three has-open writing\"", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("class=\"cletter\"", StringComparison.Ordinal) < html.IndexOf("class=\"cenv\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("data-compose", StringComparison.Ordinal) < html.IndexOf("In the list", StringComparison.Ordinal), "compose comes first in the page");
        foreach (string piece in ComposePieces)
        {
            Assert.Contains(piece, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Reply_FromTheThreePaneView_OpensDocked_AndReplyAllAsksWhenManyWouldGetIt()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: Q <q@x.test>\r\nTo: arun@anjal.co.in, a@x.test, b@x.test, c@x.test, d@x.test, e@x.test\r\nSubject: Agenda\r\n\r\nx\r\n");
        string view = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}");
        Assert.Contains($"open={m.Id}&amp;reply={m.Id}", view, StringComparison.Ordinal);
        Assert.Contains("Reply to all 6 people?", view, StringComparison.Ordinal);
        Assert.Contains("Reply to the sender only", view, StringComparison.Ordinal);
        string docked = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&reply={m.Id}");
        Assert.Contains("data-docked", docked, StringComparison.Ordinal);
        Assert.Contains("Re: Agenda", docked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewPages_Render()
    {
        await this.SignInAsync();
        Assert.Contains("Mail still being delivered", await this.client.GetStringAsync("outbox"), StringComparison.Ordinal);
        Assert.Contains("Merge duplicates", await this.client.GetStringAsync("contacts"), StringComparison.Ordinal);
        Assert.Contains("Folders and rules", await this.client.GetStringAsync("settings/rules?rule=new"), StringComparison.Ordinal);
        Assert.Contains("Has an attachment", await this.client.GetStringAsync("search?q=x&attach=1"), StringComparison.Ordinal);
        Guid draft = await this.SaveDraftAsync("For a template");
        string picker = await this.client.GetStringAsync($"draft/{draft}?templates=1&t=greet/festival&festival=Pongal");
        Assert.Contains("Choose a template", picker, StringComparison.Ordinal);
        Assert.Contains("Fill in the blanks", picker, StringComparison.Ordinal);
        Assert.Contains("Pongal", picker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormlessPosts_NeedThePagesToken()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: a@x.test\r\nSubject: Guarded\r\n\r\nx\r\n");
        foreach (string path in new[] { $"message/{m.Id}/block", $"message/{m.Id}/trust-sender", "contacts/merge", $"held/{m.Id}/send", $"folder/INBOX/unreadall" })
        {
            HttpResponseMessage res = await this.client.PostAsync(path, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));
            // Refused: the session-ended page, never the action's own result.
            Assert.True(res.StatusCode != HttpStatusCode.Redirect || res.Headers.Location!.ToString().Contains("sign-in", StringComparison.Ordinal), path);
        }
        Assert.Empty(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));   // the block did not happen
    }

    [Fact]
    public async Task Preview_ShowsImagesAndPdf_InThePage_NeverSvg()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: a@x.test\r\nSubject: Files\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nsee\r\n" +
            "--b\r\nContent-Type: image/png; name=p.png\r\nContent-Disposition: attachment; filename=p.png\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n" +
            "--b\r\nContent-Type: image/svg+xml; name=s.svg\r\nContent-Disposition: attachment; filename=s.svg\r\nContent-Transfer-Encoding: base64\r\n\r\nPHN2Zz4=\r\n--b--\r\n");
        HttpResponseMessage png = await this.client.GetAsync($"message/{m.Id}/attachment/0/preview");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.Contains("sandbox", png.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/1/preview")).StatusCode);
        string reader = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0");
        Assert.Contains("Back to the letter", reader, StringComparison.Ordinal);
    }

    private static readonly string[] ComposePieces =
    {
        "class=\"csubject\"", "data-chip-template", "data-outside-warning", "data-drop", "data-later", "data-merge", "name=\"templates\" value=\"1\"",
        "data-keepdraft", "Send later",
    };

    private static readonly string[] FormatCommands =
    {
        "bold", "italic", "underline", "strikeThrough", "foreColor", "hiliteColor", "justifyLeft", "justifyCenter", "justifyRight",
        "insertUnorderedList", "insertOrderedList", "outdent", "indent", "link", "quote", "insertText", "blockBg", "mailBg", "clear",
    };
}
