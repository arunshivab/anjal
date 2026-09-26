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
/// Regression tests for the defects raised in QA phase A1 (the webmail demo)
/// and fixed in v0.17.0. Each test reproduces what was observed, through the
/// real pipeline: sign-in, antiforgery, form posts and the rendered HTML.
/// </summary>
public sealed class A1FixesTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-a1-" + Guid.NewGuid().ToString("N"));
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

    private const string WithAttachment =
        "From: Lab <lab@hospital.example>\r\nSubject: Report attached\r\nMIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nPlease find the report.\r\n" +
        "--b1\r\nContent-Type: text/csv; name=\"report.csv\"\r\nContent-Disposition: attachment; filename=\"report.csv\"\r\n\r\ncase,result\r\n--b1--\r\n";

    private async Task<HttpResponseMessage> ComposeAsync(string to, string subject, byte[]? file = null)
    {
        string token = await this.TokenAsync("compose");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(to), "to" },
            { new StringContent(subject), "subject" },
            { new StringContent("Body text."), "body" },
        };
        if (file is not null)
        {
            form.Add(new ByteArrayContent(file), "attachments", "scan.pdf");
        }
        return await this.client.PostAsync("compose", form);
    }

    [Fact]
    public async Task DEF020_TheHiddenAttribute_AlwaysHides()
    {
        string css = await this.client.GetStringAsync("app.css");
        Assert.Contains("[hidden] { display: none !important; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF005_RowsWithAttachments_AreMarked_OthersAreNot()
    {
        await this.SignInAsync();
        await this.DeliverAsync(WithAttachment);
        await this.DeliverAsync("From: a@x.test\r\nSubject: Plain one\r\n\r\nNo files.\r\n");
        string html = await this.client.GetStringAsync("folder/INBOX");
        Assert.Equal(1, Regex.Count(html, "data-attachment", RegexOptions.CultureInvariant));
    }

    [Fact]
    public async Task DEF009_SentShowsTheRecipient_UnderATo_Heading()
    {
        await this.SignInAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await this.ComposeAsync("Dr Rao <rao@hospital.example>", "Referral")).StatusCode);
        string html = await this.client.GetStringAsync("folder/Sent");
        Assert.Contains("<th class=\"c-from withdot\">To</th>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"sname\">Dr Rao</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"sname\">Arun</span>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEF-011 was closed as not a defect: அ is the deliberate brand glyph for
    /// the flag (owner's decision in the v0.17.0 review).
    /// </summary>
    [Fact]
    public async Task DEF011_TheFlagControl_KeepsTheBrandGlyph()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: One\r\n\r\nx\r\n");
        string html = await this.client.GetStringAsync("folder/INBOX");
        Match button = Regex.Match(html, "<button[^>]*class=\"fav[^\"]*\"[^>]*>(.*?)</button>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(button.Success);
        Assert.Equal("அ", button.Groups[1].Value.Trim());
    }

    [Fact]
    public async Task DEF013_ADeletedMessage_Is404_WithTheFriendlyPage()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: a@x.test\r\nSubject: Gone\r\n\r\nx\r\n");
        await this.store.DeleteMessageAsync(m.Id);
        HttpResponseMessage res = await this.client.GetAsync($"message/{m.Id}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("no longer here", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF018_AnInventedFolder_Is404_ButAnUnusedSystemFolderIsNot()
    {
        await this.SignInAsync();
        HttpResponseMessage invented = await this.client.GetAsync("folder/Invented");
        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);
        Assert.Contains("No folder called Invented", await invented.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await this.client.GetAsync("folder/Trash")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await this.client.GetAsync("folder/INBOX")).StatusCode);
    }

    [Fact]
    public async Task DEF022_WithNoFromHeader_TheRailShowsTheEnvelopeSender()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("Subject: YOU HAVE WON\r\n\r\nclaim now\r\n", envelopeFrom: "prize@lottery-winner.test");
        string html = await this.client.GetStringAsync($"message/{m.Id}");
        Assert.Contains("prize@lottery-winner.test", html, StringComparison.Ordinal);
        Assert.Contains("envelope sender; the message has no From header", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF007_SaveDraft_Confirms()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("compose");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("rao@hospital.example"), "to" },
            { new StringContent("Half written"), "subject" },
        };
        HttpResponseMessage saved = await this.client.PostAsync("draft", form);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        string location = saved.Headers.Location!.ToString();
        Assert.Contains("saved=1", location, StringComparison.Ordinal);
        string html = await this.client.GetStringAsync(location.TrimStart('/'));
        Assert.Contains("Draft saved.", html, StringComparison.Ordinal);
        Assert.Contains(">Draft saved</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF012_AFormPostedAfterTheSessionEnded_SaysSoOnSignIn()
    {
        HttpResponseMessage res = await this.client.PostAsync("settings/name", new FormUrlEncodedContent(new Dictionary<string, string> { ["displayName"] = "x" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/sign-in?unsaved=1", res.Headers.Location!.ToString());
        string html = await this.client.GetStringAsync("sign-in?unsaved=1");
        Assert.Contains("that change was not saved", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF027_MarkAllRead_RequiresTheAntiforgeryToken()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: Unread one\r\n\r\nx\r\n");

        // Forged: no token. Nothing changes.
        await this.client.PostAsync("folder/INBOX/readall", new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));
        Assert.Contains(this.store.MailboxMessages, m => !m.Seen);

        // Genuine: with the page's token. Everything is read.
        string token = await this.TokenAsync("folder/INBOX");
        await this.PostAsync("folder/INBOX/readall", token);
        Assert.DoesNotContain(this.store.MailboxMessages, m => !m.Seen);
    }

    [Fact]
    public async Task DEF023_TrashRestoresPurgesAndEmpties_AndPurgeNeverTouchesMailOutsideTrash()
    {
        await this.SignInAsync();
        MessageRow a = await this.DeliverAsync("From: a@x.test\r\nSubject: A\r\n\r\nx\r\n");
        MessageRow b = await this.DeliverAsync("From: a@x.test\r\nSubject: B\r\n\r\nx\r\n");
        MessageRow c = await this.DeliverAsync("From: a@x.test\r\nSubject: C\r\n\r\nx\r\n");
        MessageRow keep = await this.DeliverAsync("From: a@x.test\r\nSubject: Keep in INBOX\r\n\r\nx\r\n");

        string token = await this.TokenAsync("folder/INBOX");
        await this.PostAsync("folder/INBOX/bulk", token, ("action", "trash"), ("id", a.Id.ToString()), ("id", b.Id.ToString()), ("id", c.Id.ToString()));
        string trashHtml = await this.client.GetStringAsync("folder/Trash");
        Assert.Contains("Restore to INBOX", trashHtml, StringComparison.Ordinal);
        Assert.Contains("Empty Trash", trashHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"trash\"", trashHtml, StringComparison.Ordinal);

        token = await this.TokenAsync("folder/Trash");
        await this.PostAsync("folder/Trash/bulk", token, ("action", "restore"), ("id", a.Id.ToString()));
        await this.PostAsync("folder/Trash/bulk", token, ("action", "purge"), ("id", b.Id.ToString()), ("id", keep.Id.ToString()));

        Assert.Contains(this.store.MailboxMessages, m => m.Id == a.Id);
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == b.Id);
        Assert.Contains(this.store.MailboxMessages, m => m.Id == keep.Id); // not in Trash: untouched
        FolderRow inbox = (await this.store.ListFoldersAsync(this.mailbox.Id)).Single(f => f.Name == FolderRow.Inbox);
        Assert.Equal(inbox.Id, this.store.MailboxMessages.Single(m => m.Id == a.Id).FolderId);

        // Empty Trash needs the token too, then removes what is left.
        await this.client.PostAsync("folder/Trash/empty", new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));
        Assert.Contains(this.store.MailboxMessages, m => m.Id == c.Id);
        await this.PostAsync("folder/Trash/empty", token);
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == c.Id);
        Assert.Contains(this.store.MailboxMessages, m => m.Id == keep.Id);
    }

    [Fact]
    public async Task DEF016_ASentMessageWithAnAttachment_IsRecordedAsHavingOne()
    {
        await this.SignInAsync();
        await this.ComposeAsync("rao@hospital.example", "Scan", new byte[2048]);
        MessageRow sent = this.store.MailboxMessages.Single(m => m.Subject == "Scan");
        Assert.True(sent.HasAttachments);
        MailboxActivity activity = await this.store.GetActivityAsync(this.mailbox.Id, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(1, activity.SentWithAttachments);
    }

    [Fact]
    public async Task DEF014_And_DEF017_TheBrowserScript_MatchesTheMarkup()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: One\r\n\r\nx\r\n");
        string html = await this.client.GetStringAsync("folder/INBOX");
        string js = await this.client.GetStringAsync("app.js");

        // Mark all read hides the badge and rewrites the heading's count.
        Assert.Contains("data-count data-total=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("badge.hidden = true", js, StringComparison.Ordinal);

        // '#' uses the row checkbox and the bulk form's Trash button - both present.
        Assert.DoesNotContain("form[data-trash]", js, StringComparison.Ordinal);
        Assert.Contains("data-rowcheck", html, StringComparison.Ordinal);
        Assert.Matches("<button[^>]*value=\"trash\"[^>]*data-bulkaction", html);
    }

    [Fact]
    public async Task DEF008_SettingsListsBlockedSenders_AndRemovingOneWorks()
    {
        await this.SignInAsync();
        string empty = await this.client.GetStringAsync("settings/senders");
        Assert.Contains("No sender rules yet", empty, StringComparison.Ordinal);

        MessageRow m = await this.DeliverAsync("From: news@spammer.test\r\nSubject: Offer\r\n\r\nbuy\r\n", envelopeFrom: "news@spammer.test");
        string token = await this.TokenAsync("folder/INBOX");
        await this.PostAsync("folder/INBOX/bulk", token, ("action", "spam"), ("id", m.Id.ToString()));

        string html = await this.client.GetStringAsync("settings/senders");
        Assert.Contains("news@spammer.test", html, StringComparison.Ordinal);
        Assert.Contains("Always to Junk", html, StringComparison.Ordinal);

        SenderRuleRow rule = Assert.Single(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));
        token = await this.TokenAsync("settings/senders");
        HttpResponseMessage removed = await this.PostAsync("settings/senders/delete", token, ("ruleId", rule.Id.ToString()));
        Assert.Equal("/settings/senders?saved=senderremoved", removed.Headers.Location!.ToString());
        Assert.Empty(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));
    }

    private async Task<HttpResponseMessage> PostMultipartAsync(string path, string token, IEnumerable<(string Name, string Value)> fields, (string FileName, byte[] Bytes)? file = null)
    {
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach ((string name, string value) in fields)
        {
            form.Add(new StringContent(value), name);
        }
        if (file is not null)
        {
            form.Add(new ByteArrayContent(file.Value.Bytes), "attachments", file.Value.FileName);
        }
        return await this.client.PostAsync(path, form);
    }

    private string SentRaw(string subject)
    {
        MessageRow sent = this.store.MailboxMessages.Single(m => m.Subject == subject);
        FolderRow folder = this.store.ListFoldersAsync(this.mailbox.Id).GetAwaiter().GetResult().Single(f => f.Id == sent.FolderId);
        Assert.Equal("Sent", folder.Name);
        string dir = Path.Combine(this.root, "imagiqa", this.mailbox.Address, FolderRow.MaildirNameFor(folder.Name));
        string file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Single(f => f.Contains(Path.GetFileName(sent.MaildirFile), StringComparison.Ordinal));
        return File.ReadAllText(file);
    }

    [Fact]
    public async Task DEF006_ForwardCarriesTheOriginalsAttachments_UnlessUnticked()
    {
        await this.SignInAsync();
        MessageRow original = await this.DeliverAsync(WithAttachment);

        string page = await this.client.GetStringAsync($"compose?forward={original.Id}");
        Assert.Contains("From the original message:", page, StringComparison.Ordinal);
        Assert.Contains("report.csv", page, StringComparison.Ordinal);
        Assert.Contains($"name=\"carryFrom\" value=\"{original.Id}\"", page, StringComparison.Ordinal);

        string token = await this.TokenAsync($"compose?forward={original.Id}");
        await this.PostMultipartAsync("compose", token, new[] { ("to", "rao@hospital.example"), ("subject", "Fwd: kept"), ("carryFrom", original.Id.ToString()), ("carry", "0") });
        string kept = this.SentRaw("Fwd: kept");
        Assert.Contains("filename=\"report.csv\"", kept, StringComparison.Ordinal);
        Assert.True(this.store.MailboxMessages.Single(m => m.Subject == "Fwd: kept").HasAttachments);

        token = await this.TokenAsync($"compose?forward={original.Id}");
        await this.PostMultipartAsync("compose", token, new[] { ("to", "rao@hospital.example"), ("subject", "Fwd: dropped"), ("carryFrom", original.Id.ToString()) });
        Assert.DoesNotContain("report.csv", this.SentRaw("Fwd: dropped"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF029_ADraftKeepsItsAttachment_ThroughReopenAndSend()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("compose");
        HttpResponseMessage saved = await this.PostMultipartAsync("draft", token, new[] { ("to", "rao@hospital.example"), ("subject", "Scan for review") }, ("scan.pdf", new byte[] { 1, 2, 3, 4 }));
        string draftPath = saved.Headers.Location!.ToString().Split('?')[0].TrimStart('/');
        Guid draftId = Guid.Parse(draftPath.AsSpan("draft/".Length));

        string reopened = await this.client.GetStringAsync(draftPath);
        Assert.Contains("Already attached:", reopened, StringComparison.Ordinal);
        Assert.Contains("scan.pdf", reopened, StringComparison.Ordinal);

        token = await this.TokenAsync(draftPath);
        await this.PostMultipartAsync("compose", token, new[] { ("to", "rao@hospital.example"), ("subject", "Scan for review"), ("draftId", draftId.ToString()), ("carryFrom", draftId.ToString()), ("carry", "0") });
        Assert.Contains("filename=\"scan.pdf\"", this.SentRaw("Scan for review"), StringComparison.Ordinal);
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == draftId);
    }

    [Fact]
    public async Task DEF006_AnotherMailboxsAttachment_CannotBeCarried()
    {
        TenantRow other = await this.store.UpsertTenantAsync(new TenantRow { Slug = "othertenant" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = other.Id, Domain = "other.test" });
        MailboxRow victim = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = other.Id, LocalPart = "victim", Domain = "other.test" });
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "lab@hospital.example",
            EnvelopeTo = new[] { victim.Address },
            RawBytes = System.Text.Encoding.UTF8.GetBytes(WithAttachment),
        });
        MessageRow theirs = this.store.MailboxMessages.Last(m => m.MailboxId == victim.Id);

        await this.SignInAsync();
        string token = await this.TokenAsync("compose");
        await this.PostMultipartAsync("compose", token, new[] { ("to", "me@elsewhere.example"), ("subject", "Exfiltrate"), ("carryFrom", theirs.Id.ToString()), ("carry", "0") });
        Assert.DoesNotContain("report.csv", this.SentRaw("Exfiltrate"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF015_SearchFindsWordsInsideBodies_ButNotInsideAttachments()
    {
        await this.SignInAsync();
        await this.DeliverAsync(WithAttachment);
        await this.DeliverAsync("From: a@x.test\r\nSubject: Newsletter\r\nContent-Type: text/html\r\n\r\n<p>The new <b>toolbar</b> is ready.</p>\r\n");
        await this.ComposeAsync("rao@hospital.example", "Referral note");

        Assert.Contains("Report attached", await this.client.GetStringAsync("search?q=Please+find"), StringComparison.Ordinal);
        Assert.Contains("Newsletter", await this.client.GetStringAsync("search?q=toolbar"), StringComparison.Ordinal);
        Assert.Contains("Referral note", await this.client.GetStringAsync("search?q=Body+text&scope=all"), StringComparison.Ordinal);
        // "case,result" is only inside report.csv: attachments are not searched.
        Assert.Contains("Nothing matched", await this.client.GetStringAsync("search?q=case%2Cresult"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF021_TheMessageFrame_LoadsLiPiSans_FromThisServerOnly()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: a@x.test\r\nSubject: Tamil\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nநோயாளி அறிக்கை\r\n");
        string html = WebUtility.HtmlDecode(await this.client.GetStringAsync($"message/{m.Id}"));
        string origin = this.client.BaseAddress!.GetLeftPart(UriPartial.Authority);

        Assert.Contains($"font-src data: {origin};", html, StringComparison.Ordinal);
        Assert.Equal(11, Regex.Count(html, "@font-face", RegexOptions.CultureInvariant));
        Assert.Contains($"url(\"{origin}/fonts/LiPi-Sans-Tamil.woff2\")", html, StringComparison.Ordinal);
        Assert.Contains("font-family:'LiPi Sans',system-ui", html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(\"/fonts/", html, StringComparison.Ordinal); // no relative url left: it would resolve nowhere in the frame

        HttpResponseMessage font = await this.client.GetAsync("fonts/LiPi-Sans-Tamil.woff2");
        Assert.Equal("*", font.Headers.GetValues("Access-Control-Allow-Origin").Single());
        HttpResponseMessage css = await this.client.GetAsync("app.css");
        Assert.False(css.Headers.Contains("Access-Control-Allow-Origin")); // only fonts are shared
    }

    [Fact]
    public async Task Review1_And2_TheClipHasItsOwnColumn_InFoldersAndSearch_AndDatesAreCompact()
    {
        await this.SignInAsync();
        await this.DeliverAsync(WithAttachment);
        await this.DeliverAsync("From: a@x.test\r\nSubject: No files\r\n\r\nx\r\n");
        string inbox = await this.client.GetStringAsync("folder/INBOX");
        // A separate cell before the subject, so every subject starts at the same place.
        Assert.Matches("<td class=\"c-att\">\\s*<span data-attachment[^>]*>.*?</span>\\s*</td>\\s*<td class=\"c-subject\">", inbox);
        Assert.Matches("<td class=\"c-att\">\\s*</td>\\s*<td class=\"c-subject\">", inbox);   // empty slot on the other row
        string subjectCells = string.Join("", Regex.Matches(inbox, "<td class=\"c-subject\">.*?</td>", RegexOptions.Singleline).Select(m => m.Value));
        Assert.DoesNotContain("data-attachment", subjectCells, StringComparison.Ordinal);
        Assert.Contains("<th class=\"c-att\"><span class=\"sr-only\">Attachments</span></th>", inbox, StringComparison.Ordinal);

        string search = await this.client.GetStringAsync("search?q=Report&scope=all");
        Assert.Matches("<td class=\"c-att\">\\s*<span data-attachment", search);
        Assert.DoesNotMatch("<td class=\"c-time num\">\\d{1,2} \\w{3} \\d{4}</td>", search);
    }

    [Fact]
    public async Task Review3_TheToolbarIsOnePieceOfPageMarkup_HiddenUntilTheEditorStarts()
    {
        await this.SignInAsync();
        foreach (string page in new[] { "compose", "settings/signature" })
        {
            string html = await this.client.GetStringAsync(page);
            Match bar = Regex.Match(html, "<div class=\"rte-bar\"[^>]*data-rte-bar[^>]*hidden[^>]*>(.*?)</div>", RegexOptions.Singleline);
            Assert.True(bar.Success, "toolbar markup on " + page);
            foreach (string label in new[] { "Bold", "Italic", "Underline", "Bulleted list", "Numbered list", "Insert a link", "Quote", "Clear formatting" })
            {
                Assert.Contains($"aria-label=\"{label}\"", bar.Groups[1].Value, StringComparison.Ordinal);
            }
            Assert.Equal(3, Regex.Count(bar.Groups[1].Value, "class=\"rte-sep\""));
        }
        string js = await this.client.GetStringAsync("app.js");
        Assert.DoesNotContain("createElement(\"button\")", js, StringComparison.Ordinal);
        Assert.Contains("queryCommandState", js, StringComparison.Ordinal);      // active formatting is shown
        string css = await this.client.GetStringAsync("app.css");
        Assert.Contains(".rte-ed.rte-canvas { resize: none; border: 0; box-shadow: none;", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review6_HeadersAreAlignedWithTheirColumns()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: One\r\n\r\nx\r\n");
        string html = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("<th class=\"c-from withdot\">From</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"c-time right\">Received</th>", html, StringComparison.Ordinal);
        Assert.Contains("<th class=\"c-size right\">Size</th>", html, StringComparison.Ordinal);
        string css = await this.client.GetStringAsync("app.css");
        Assert.Contains("table.ml th.c-from.withdot { padding-left: 28px; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review4_And_DEF030_JunkAndTrashOfferNoCategory_AndReportSpamNoLongerHidesBehindCategories()
    {
        await this.SignInAsync();
        await this.DeliverAsync("From: a@x.test\r\nSubject: One\r\n\r\nx\r\n");
        string inbox = await this.client.GetStringAsync("folder/INBOX");
        Assert.Contains("value=\"categorise\"", inbox, StringComparison.Ordinal);   // six defaults exist...
        Assert.Contains("value=\"spam\"", inbox, StringComparison.Ordinal);         // ...and Report spam is still there
        Assert.DoesNotContain("value=\"categorise\"", await this.client.GetStringAsync("folder/Junk"), StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"categorise\"", await this.client.GetStringAsync("folder/Trash"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review3_AttachmentsComeRightAfterDetails_AndTheCategoryShowsUnderTheSubject()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(WithAttachment);
        await this.client.GetStringAsync("folder/INBOX"); // the default categories are created on first listing
        CategoryRow clinical = (await this.store.ListCategoriesAsync(this.mailbox.TenantId, this.mailbox.Id)).First(c => c.Name == "Clinical");
        await this.store.SetMessageCategoryAsync(this.mailbox.Id, m.Id, clinical.Id);
        string html = await this.client.GetStringAsync($"message/{m.Id}");
        int details = html.IndexOf(">Details</h2>", StringComparison.Ordinal);
        int attachments = html.IndexOf(">1 attachment</h2>", StringComparison.Ordinal);
        int checks = html.IndexOf(">Checks</h2>", StringComparison.Ordinal);
        int category = html.IndexOf(">Category</h2>", StringComparison.Ordinal);
        Assert.True(details < attachments && attachments < checks && checks < category, $"order: {details} {attachments} {checks} {category}");
        Assert.Matches("<p class=\"readcat\"><span class=\"chip slot-\\d+\"><i></i>Clinical</span></p>", html);
    }

    [Fact]
    public async Task Review7_SenderRulesCanBeCreatedInSettings()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("settings");
        CategoryRow billing = (await this.store.ListCategoriesAsync(this.mailbox.TenantId, this.mailbox.Id)).First(c => c.Name == "Billing");

        Assert.Contains("saved=senderadded", (await this.PostAsync("settings/senders/add", token, ("pattern", "@spam-domain.example"), ("rule", "block"))).Headers.Location!.ToString(), StringComparison.Ordinal);
        await this.PostAsync("settings/senders/add", token, ("pattern", "Accounts@Vendor.example"), ("rule", "cat:" + billing.Id));
        HttpResponseMessage bad = await this.PostAsync("settings/senders/add", token, ("pattern", "not a pattern"), ("rule", "block"));
        Assert.Contains("error=", bad.Headers.Location!.ToString(), StringComparison.Ordinal);
        HttpResponseMessage foreignCategory = await this.PostAsync("settings/senders/add", token, ("pattern", "x@y.example"), ("rule", "cat:" + Guid.NewGuid()));
        Assert.Contains("error=", foreignCategory.Headers.Location!.ToString(), StringComparison.Ordinal);

        SenderRuleRow block = Assert.Single(await this.store.ListMailboxSenderRulesAsync(this.mailbox.Id));
        Assert.Equal("@spam-domain.example", block.Pattern);
        CategoryRuleRow filing = Assert.Single(await this.store.ListCategoryRulesAsync(this.mailbox.Id));
        Assert.Equal("accounts@vendor.example", filing.Pattern);
        Assert.Equal(billing.Id, filing.CategoryId);
    }

    [Fact]
    public async Task Review8_SettingsHasSections_EachAtItsOwnAddress()
    {
        await this.SignInAsync();
        string profile = await this.client.GetStringAsync("settings");
        foreach (string s in new[] { "profile", "appearance", "categories", "senders", "signature", "password" })
        {
            Assert.Contains($"href=\"/settings/{s}\"", profile, StringComparison.Ordinal);
        }
        Assert.Contains("<h2>Display name</h2>", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>Password</h2>", profile, StringComparison.Ordinal);
        string signature = await this.client.GetStringAsync("settings/signature");
        Assert.Contains("<h2>Signature</h2>", signature, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>Display name</h2>", signature, StringComparison.Ordinal);
    }

    private async Task SaveSignatureAsync(string html, string text = "")
    {
        string token = await this.TokenAsync("settings/signature");
        HttpResponseMessage res = await this.PostAsync("settings/signature", token, ("signatureHtml", html), ("signatureText", text));
        Assert.Equal("/settings/signature?saved=signature", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Signature_IsSanitised_AndStartsNewMessagesAndReplies_ButNotReopenedDrafts()
    {
        await this.SignInAsync();
        await this.SaveSignatureAsync("<b>Dr Arun</b><br>Apulki<script>alert(1)</script><img src=x onerror=alert(2)>");
        (string sigHtml, string sigText) = await this.store.GetSignatureAsync(this.mailbox.Id);
        Assert.DoesNotContain("script", sigHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", sigHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Dr Arun\nApulki", sigText);

        string fresh = WebUtility.HtmlDecode(await this.client.GetStringAsync("compose"));
        Assert.Contains("-- \nDr Arun\nApulki", fresh, StringComparison.Ordinal);
        Assert.Contains("<b>Dr Arun</b>", fresh, StringComparison.Ordinal);

        MessageRow original = await this.DeliverAsync("From: Lab <lab@hospital.example>\r\nSubject: Result\r\nMessage-ID: <r1@hospital.example>\r\n\r\nHb 13.2\r\n");
        string reply = WebUtility.HtmlDecode(await this.client.GetStringAsync($"compose?reply={original.Id}"));
        Assert.Contains("<blockquote>Hb 13.2</blockquote>", reply, StringComparison.Ordinal);
        Assert.True(reply.IndexOf("Dr Arun", StringComparison.Ordinal) < reply.IndexOf("Hb 13.2", StringComparison.Ordinal), "signature sits above the quote");

        // A draft already carries its signature; reopening must not add another.
        string token = await this.TokenAsync("compose");
        HttpResponseMessage saved = await this.PostMultipartAsync("draft", token, new[] { ("to", "x@y.example"), ("subject", "Once"), ("body", "text\n\n-- \nDr Arun"), ("bodyHtml", "<div>text</div><div data-signature=\"\">-- <br><b>Dr Arun</b></div>") });
        string draft = WebUtility.HtmlDecode(await this.client.GetStringAsync(saved.Headers.Location!.ToString().TrimStart('/')));
        Assert.Equal(1, Regex.Count(draft[draft.IndexOf("name=\"bodyHtml\"", StringComparison.Ordinal)..], "Dr Arun"));
    }

    [Fact]
    public async Task FormattedMail_GoesAsHtmlWithAPlainPart_DerivedServerSide_AndSanitised()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("compose");
        await this.PostMultipartAsync("compose", token, new[]
        {
            ("to", "rao@hospital.example"), ("subject", "Formatted"),
            ("body", "WHATEVER THE BROWSER CLAIMS"),
            ("bodyHtml", "<p>Hello <b>world</b></p><ul><li>one</li><li>two</li></ul><script>alert(1)</script><a href=\"javascript:alert(2)\">x</a>"),
        });
        string raw = this.SentRaw("Formatted");
        Assert.Contains("multipart/alternative", raw, StringComparison.Ordinal);
        Assert.Contains("text/html", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WHATEVER THE BROWSER CLAIMS", raw, StringComparison.Ordinal); // plain part comes from the HTML
        Anjal.Mime.MimeMessage parsed = Anjal.Mime.MimeParser.Parse(System.Text.Encoding.UTF8.GetBytes(raw));
        var alt = Assert.IsType<Anjal.Mime.MimeMultipart>(parsed.Body);
        string plain = ((Anjal.Mime.MimePart)alt.Parts[0]).GetBodyAsText();
        Assert.Contains("Hello world", plain, StringComparison.Ordinal);
        Assert.Contains("• one", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheEditor_MailStaysPlainText()
    {
        await this.SignInAsync();
        await this.ComposeAsync("rao@hospital.example", "Plain only");
        string raw = this.SentRaw("Plain only");
        Assert.DoesNotContain("multipart/alternative", raw, StringComparison.Ordinal);
        Assert.Contains("text/plain", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftsFormatting_ComesBackIntoTheEditor()
    {
        await this.SignInAsync();
        string token = await this.TokenAsync("compose");
        HttpResponseMessage saved = await this.PostMultipartAsync("draft", token, new[] { ("to", "x@y.example"), ("subject", "Rich draft"), ("bodyHtml", "<p>Keep <u>this</u></p>") });
        string draft = WebUtility.HtmlDecode(await this.client.GetStringAsync(saved.Headers.Location!.ToString().TrimStart('/')));
        Assert.Contains("<u>this</u>", draft, StringComparison.Ordinal);
        Assert.Contains("data-rte", draft, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF033_AssetsAreFingerprinted_SoAnUpgradeReachesEveryBrowserAtOnce()
    {
        string page = await this.client.GetStringAsync("sign-in");
        foreach (string asset in new[] { "tokens.css", "app.css", "app.js" })
        {
            string fp = Anjal.Webmail.Services.StaticAssets.Fingerprint(asset)!;
            Assert.Matches("^[0-9a-f]{12}$", fp);
            Assert.Contains($"/{asset}?v={fp}\"", page, StringComparison.Ordinal);

            HttpResponseMessage versioned = await this.client.GetAsync($"{asset}?v={fp}");
            Assert.Contains("immutable", versioned.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            Assert.Equal($"\"{fp}\"", versioned.Headers.ETag!.Tag);

            HttpResponseMessage bare = await this.client.GetAsync(asset);
            Assert.True(bare.Headers.CacheControl!.NoCache, asset + " without a fingerprint must be revalidated");

            // The fault: a tag from an older build used to be answered "unchanged".
            using var stale = new HttpRequestMessage(HttpMethod.Get, asset);
            stale.Headers.TryAddWithoutValidation("If-None-Match", "\"0.17.0\"");
            Assert.Equal(HttpStatusCode.OK, (await this.client.SendAsync(stale)).StatusCode);
            using var current = new HttpRequestMessage(HttpMethod.Get, asset);
            current.Headers.TryAddWithoutValidation("If-None-Match", $"\"{fp}\"");
            Assert.Equal(HttpStatusCode.NotModified, (await this.client.SendAsync(current)).StatusCode);
        }
        Assert.True((await this.client.GetAsync("fonts/lipi.css")).Headers.CacheControl!.NoCache);
        Assert.Contains("immutable", (await this.client.GetAsync("fonts/LiPi-Sans-Tamil.woff2")).Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_TheFilePickerMatchesTheOtherButtons()
    {
        string css = await this.client.GetStringAsync("app.css");
        Assert.Contains(".railfile::file-selector-button {", css, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--radius-pill); border: 1px solid var(--line); background: var(--surface-200);", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF037_MailToALocalMailbox_IsDeliveredDirectly_NotQueuedOutbound()
    {
        MailboxRow colleague = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.mailbox.TenantId, LocalPart = "colleague", Domain = "anjal.co.in" });
        await this.SignInAsync();
        await this.ComposeAsync("colleague@anjal.co.in, doctor@hospital.example, nobody@anjal.co.in", "Ward round");

        MessageRow arrived = this.store.MailboxMessages.Single(m => m.MailboxId == colleague.Id && m.Subject == "Ward round");
        FolderRow inbox = (await this.store.ListFoldersAsync(colleague.Id)).Single(f => f.Id == arrived.FolderId);
        Assert.Equal(FolderRow.Inbox, inbox.Name);
        IReadOnlyList<OutboundMessage> queued = await this.store.LeaseOutboundBatchAsync(100, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.DoesNotContain(queued, q => q.EnvelopeTo == "colleague@anjal.co.in");
        Assert.Contains(queued, q => q.EnvelopeTo == "doctor@hospital.example");
        Assert.Contains(queued, q => q.EnvelopeTo == "nobody@anjal.co.in"); // not a mailbox: left to the MTA, which may route it to a webhook
        Assert.Contains("Ward round", string.Join("", this.store.MailboxMessages.Where(m => m.MailboxId == this.mailbox.Id).Select(m => m.Subject)), StringComparison.Ordinal); // Sent copy
    }

    [Fact]
    public async Task DEF038_TheRailShowsThisServersVerdicts_NeverASendersClaim()
    {
        await this.SignInAsync();
        MessageRow trusted = await this.DeliverAsync(
            "Received: from out.example ([192.0.2.1])\r\n\tby mx.anjal.test with ESMTPS id 1;\r\n\tMon, 21 Sep 2026 10:00:00 +0000\r\n" +
            "Authentication-Results: mx.anjal.test; spf=pass smtp.mailfrom=bank.example; dkim=none; dmarc=fail header.from=bank.example\r\n" +
            "From: Bank <alerts@bank.example>\r\nSubject: Verify your account\r\n\r\nclick\r\n");
        string html = await this.client.GetStringAsync($"message/{trusted.Id}");
        Assert.Contains("SPF pass", html, StringComparison.Ordinal);
        Assert.Contains("DKIM none", html, StringComparison.Ordinal);
        Assert.Contains("DMARC fail", html, StringComparison.Ordinal);
        Assert.Contains("may be an impersonation", html, StringComparison.Ordinal);

        // Only a claim under another name (the sender's own): ignored. The score
        // header is the spam filter's, which every message from outside passes
        // through; since rc.5 the page tells checked mail from unchecked mail.
        MessageRow forged = await this.DeliverAsync(
            "Received: from out.example ([192.0.2.1])\r\n\tby mx.anjal.test with ESMTPS id 2;\r\n\tMon, 21 Sep 2026 10:00:00 +0000\r\n" +
            "X-Anjal-Spam-Score: 0\r\nX-Anjal-Spam-Reasons: none\r\n" +
            "Authentication-Results: attacker.example; spf=pass; dkim=pass; dmarc=pass\r\n" +
            "From: Boss <boss@hospital.example>\r\nSubject: Pay this invoice\r\n\r\nnow\r\n");
        string forgedHtml = await this.client.GetStringAsync($"message/{forged.Id}");
        Assert.DoesNotContain("DMARC pass", forgedHtml, StringComparison.Ordinal);
        Assert.Contains("Sender checks: not recorded", forgedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF040_WhenTheStoreIsDown_ThePageExplains_AndGivesAReference()
    {
        // The webmail's own store, made to fail the way a database outage does.
        using var broken = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = this.client.BaseAddress };
        IFullStore down = System.Reflection.DispatchProxy.Create<IFullStore, ThrowingStore>();
        WebApplication app = Program.CreateApp(Array.Empty<string>(), down, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address + "/") };
            // Signing in reads the store; the sign-in page alone does not.
            string form = await client.GetStringAsync("sign-in");
            string token = TokenRegex.Match(form).Groups[1].Value;
            HttpResponseMessage res = await client.PostAsync("auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["address"] = "arun@anjal.co.in",
                ["password"] = "correct horse battery",
            }));
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            string html = await res.Content.ReadAsStringAsync();
            Assert.Contains("Anjal is briefly unavailable", html, StringComparison.Ordinal);
            Assert.Contains("quote this reference", html, StringComparison.Ordinal);
            Assert.Matches("<b>[0-9a-f]{12}</b>", html);
            foreach (string leak in new[] { "Npgsql", "connection refused", "at Anjal.", "C:\\", "/home/", "Exception" })
            {
                Assert.DoesNotContain(leak, html, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
            Assert.True(res.Headers.Contains("Content-Security-Policy"), "the error page keeps the security headers");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task DEF010_AnUploadWellOverTheLimit_GetsTheMessage_NotACutConnection()
    {
        await this.SignInAsync();
        HttpResponseMessage res = await this.ComposeAsync("rao@hospital.example", "Too big", new byte[40 * 1024 * 1024]);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("18%20MB", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DEF060_ThePhoneListLayout_IsNotUndoneByALaterRule()
    {
        // On phones each message took about four stacked bands because desktop
        // rules for the list cells came later in the file than the phone
        // layout and silently won. The rc.5 block must stay the last word.
        string css = await this.client.GetStringAsync("app.css");
        int block = css.IndexOf("DEF-060: on phones each message", StringComparison.Ordinal);
        Assert.True(block > 0, "the DEF-060 block is in the stylesheet");
        string after = css[block..];
        Assert.Contains("table.ml tbody tr > td { display: block; padding: 0 !important; border: 0 !important;", after, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(^|\n)\s*table\.ml td\s*\{", RegexOptions.CultureInvariant), after);
        Assert.DoesNotMatch(new Regex(@"(^|\n)\s*\.c-(cb|score)\s*\{[^}]*padding", RegexOptions.CultureInvariant), after);
    }

    [Fact]
    public async Task Rc5_ReadingFitsTheWindow_WithTheMessageFrameStillSandboxed()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync("From: a@x.test\r\nSubject: Fit\r\n\r\nShort.\r\n");
        string html = await this.client.GetStringAsync($"message/{m.Id}");
        // The body frame keeps a sandbox that grants nothing: no scripts, no
        // same-origin access, however the empty attribute is written out.
        Match frame = Regex.Match(html, "<iframe[^>]*class=\"bodyframe\"[^>]*>", RegexOptions.CultureInvariant);
        Assert.True(frame.Success, "the message body frame is rendered");
        Assert.Matches(new Regex(@"\ssandbox(=""\s*"")?[\s>]", RegexOptions.CultureInvariant), frame.Value);
        Assert.DoesNotContain("allow-", frame.Value, StringComparison.Ordinal);
        Assert.Contains("font-size:14px", html, StringComparison.Ordinal);

        string css = await this.client.GetStringAsync("app.css");
        Assert.Contains(".reading { height: calc(100vh - 61px);", css, StringComparison.Ordinal);
    }
}

/// <summary>
/// DEF-028: one person's Report spam or Not spam must affect only their own
/// mailbox. DEF-008: those decisions are visible in Settings and reversible.
/// </summary>
public sealed class PersonalSenderRulesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rules-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxSink sink;

    public PersonalSenderRulesTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.sink = new MailboxSink(this.store, this.maildir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task<(TenantRow Tenant, MailboxRow Arun, MailboxRow Colleague)> SeedAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "apulki" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "apulki.test" });
        MailboxRow arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "apulki.test" });
        MailboxRow colleague = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "colleague", Domain = "apulki.test" });
        return (tenant, arun, colleague);
    }

    private async Task<string> FolderOfLatestAsync(MailboxRow mailbox, string from)
    {
        await this.sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = from,
            EnvelopeTo = new[] { mailbox.Address },
            RawBytes = System.Text.Encoding.ASCII.GetBytes($"From: {from}\r\nSubject: Hello\r\nMessage-ID: <{Guid.NewGuid():N}@x>\r\nDate: Mon, 21 Sep 2026 10:00:00 +0000\r\n\r\nbody\r\n"),
        });
        MessageRow latest = this.store.MailboxMessages.Last(m => m.MailboxId == mailbox.Id);
        return (await this.store.ListFoldersAsync(mailbox.Id)).Single(f => f.Id == latest.FolderId).Name;
    }

    [Fact]
    public async Task ReportSpam_ByOnePerson_DoesNotJunkThatSenderForColleagues()
    {
        (_, MailboxRow arun, MailboxRow colleague) = await this.SeedAsync();
        await this.store.UpsertMailboxSenderRuleAsync(arun.Id, "referrals@city-hospital.test", SenderRuleAction.Block);

        Assert.Equal("Junk", await this.FolderOfLatestAsync(arun, "referrals@city-hospital.test"));
        Assert.Equal(FolderRow.Inbox, await this.FolderOfLatestAsync(colleague, "referrals@city-hospital.test"));
    }

    [Fact]
    public async Task NotSpam_ByOnePerson_DoesNotLetASenderPastTheFilterForColleagues()
    {
        (TenantRow tenant, MailboxRow arun, MailboxRow colleague) = await this.SeedAsync();
        // The administrator blocks a domain for everyone...
        await this.store.UpsertSenderRuleAsync(new SenderRuleRow { TenantId = tenant.Id, Pattern = "@phish.test", Action = SenderRuleAction.Block });
        // ...and one person allows one address from it for themselves.
        await this.store.UpsertMailboxSenderRuleAsync(arun.Id, "billing@phish.test", SenderRuleAction.Allow);

        Assert.Equal(FolderRow.Inbox, await this.FolderOfLatestAsync(arun, "billing@phish.test"));
        Assert.Equal("Junk", await this.FolderOfLatestAsync(colleague, "billing@phish.test"));
    }

    [Fact]
    public async Task APersonalRule_CanOnlyBeRemovedByItsOwnMailbox()
    {
        (_, MailboxRow arun, MailboxRow colleague) = await this.SeedAsync();
        SenderRuleRow rule = await this.store.UpsertMailboxSenderRuleAsync(arun.Id, "x@y.test", SenderRuleAction.Block);

        Assert.False(await this.store.DeleteMailboxSenderRuleAsync(colleague.Id, rule.Id));
        Assert.Single(await this.store.ListMailboxSenderRulesAsync(arun.Id));
        Assert.True(await this.store.DeleteMailboxSenderRuleAsync(arun.Id, rule.Id));
        Assert.Empty(await this.store.ListMailboxSenderRulesAsync(arun.Id));
    }
}

/// <summary>
/// DEF-031 and DEF-032: HTML mail as real clients send it - full documents
/// with a doctype and a head carrying meta and link elements.
/// </summary>
public class RealWorldHtmlTests
{
    private const string OutlookStyle =
        "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\">" +
        "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width\"><link rel=\"stylesheet\" href=\"https://x.example/a.css\">" +
        "<style>p{margin:0}</style></head><body><p>Dear Dr Arun,</p><p>The MRI slot is <b>confirmed</b>.</p></body></html>";

    [Fact]
    public void DEF032_AHeadWithMetaAndLink_DoesNotBlankTheBody()
    {
        string clean = Anjal.Webmail.Services.HtmlSanitizer.Sanitize(OutlookStyle);
        Assert.Contains("Dear Dr Arun,", clean, StringComparison.Ordinal);
        Assert.Contains("<b>confirmed</b>", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<meta", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("margin:0", clean, StringComparison.Ordinal); // style blocks still go, with their content
    }

    [Theory]
    [InlineData("<input type=\"text\"><p>after input</p>", "after input")]
    [InlineData("<embed src=\"x.swf\"><p>after embed</p>", "after embed")]
    [InlineData("<base href=\"https://evil.example/\"><p>after base</p>", "after base")]
    [InlineData("<meta charset=\"utf-8\" /><p>after self-closing meta</p>", "after self-closing meta")]
    public void DEF032_AStrayVoidElement_NeverSwallowsWhatFollows(string html, string survives)
    {
        string clean = Anjal.Webmail.Services.HtmlSanitizer.Sanitize(html);
        Assert.Contains(survives, clean, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><p>x</p>")]
    [InlineData("<?xml version=\"1.0\"?><p>x</p>")]
    public void DEF031_DeclarationsAreRemoved_NotShownAsText(string html)
    {
        string clean = Anjal.Webmail.Services.HtmlSanitizer.Sanitize(html);
        Assert.Equal("<p>x</p>", clean);
    }

    [Fact]
    public void ScriptsAndFramesStillLoseTheirContent()
    {
        string clean = Anjal.Webmail.Services.HtmlSanitizer.Sanitize("<script>alert(1)</script><iframe>inner</iframe><p>ok</p>");
        Assert.Equal("<p>ok</p>", clean);
    }
}

/// <summary>A store whose every call fails, as during a database outage.</summary>
public class ThrowingStore : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException("connection refused: 127.0.0.1:5432");
}
