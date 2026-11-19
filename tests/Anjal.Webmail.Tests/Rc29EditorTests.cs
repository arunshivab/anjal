using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Viewing;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.29 part 2 (DES-22 §7): the PDF editor through the real webmail and the real viewer process. The viewer's Edit
/// button, the editor's bar and each panel from its address; Save putting a new file on a reply, a forward or a draft
/// with the original untouched; the pages arranged and joined with the mail's other PDF; the kept signature.
/// </summary>
public sealed class Rc29EditorTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };
    private static readonly string[] Panels = { "markup", "sign", "redact", "pages", "stamp" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc29e-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public void Dispose() => this.client?.Dispose();

    private ViewerAccess Viewer => this.app.Services.GetRequiredService<ViewerAccess>();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow
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
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task TheViewer_OffersEdit_AndTheEditorShowsEachPanelFromItsAddress()
    {
        // Every panel, Redact among them, as it will be on chuvadi-pdf 3.35.1 (D-405); as rc.29 ships, further down.
        this.Viewer.Switches = EditSwitches.All;
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Haemoglobin 13.2")));
        string viewer = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Match edit = Regex.Match(viewer, "class=\"btn btn-s pdfedit-go nophone\" href=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(edit.Success);
        Assert.StartsWith($"/message/{m.Id}?preview=0&edit=1&back=", edit.Groups[1].Value, StringComparison.Ordinal);

        foreach (string tool in Panels)
        {
            string page = WebUtility.HtmlDecode(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1&tool={tool}"));
            Assert.Contains("data-pdfedit=\"/message/" + m.Id + "/attachment/0/edit\"", page, StringComparison.Ordinal);
            // The tool asked for is open; every other panel is hidden.
            Assert.Matches("data-edit-tool=\"" + tool + "\"[^>]*aria-pressed=\"true\"", page);
            foreach (string other in Panels)
            {
                bool hidden = Regex.IsMatch(page, "data-edit-panel=\"" + other + "\" hidden", RegexOptions.CultureInvariant);
                Assert.Equal(other != tool, hidden);
            }
        }
        string any = await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1&tool=nonsense");
        Assert.Matches("data-edit-tool=\"markup\"[^>]*aria-pressed=\"true\"", any);
        // The picture-not-a-digital-signature words (AC-04) are in the Sign panel.
        Assert.Contains("not a digital signature", any, StringComparison.Ordinal);

        // As rc.29 ships (D-405): no Redact tool, no Redact panel, no found-boxes line; its address opens Mark up.
        this.Viewer.Switches = EditSwitches.Shipped;
        string shipped = await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1&tool=redact");
        Assert.DoesNotContain("data-edit-tool=\"redact\"", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edit-panel=\"redact\"", shipped, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edit-foundbar", shipped, StringComparison.Ordinal);
        Assert.Matches("data-edit-tool=\"markup\"[^>]*aria-pressed=\"true\"", shipped);
        Assert.DoesNotMatch("data-edit-panel=\"markup\" hidden", shipped);
    }

    [Fact]
    public async Task AsRc29Ships_RedactionIsRefused_AndAFileWithHiddenLayersKeepsItsPages()
    {
        // D-405, D-406 (the library's security advisory, 17 Nov 2026): held back until chuvadi-pdf 3.35.1.
        Assert.Same(EditSwitches.Shipped, this.Viewer.Switches);
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Patient MRN 4471")), ("layered.pdf", "application/pdf", SmallPdf("Layered", true)));
        string token = Token(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1"));
        const string Redact = "{\"Redactions\":[{\"Page\":0,\"X\":0,\"Y\":0,\"W\":595,\"H\":842}]}";
        using HttpResponseMessage redact = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", Redact), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        Assert.Equal(HttpStatusCode.Conflict, redact.StatusCode);
        Assert.Equal("Redacting is not available yet. Nothing was saved.", JsonElement.Parse(await redact.Content.ReadAsStringAsync()).GetProperty("problem").GetString());
        // Find patient details gives nothing.
        Assert.Equal("{\"Hits\":[]}", await this.client.GetStringAsync($"message/{m.Id}/attachment/0/edit/signs"));

        // The layered file: the Pages panel says why, and the pages are not changed - by the file's own Save, by
        // arranging, or by joining it to the plain one.
        string page = WebUtility.HtmlDecode(await this.client.GetStringAsync($"message/{m.Id}?preview=1&edit=1&tool=pages"));
        Assert.Contains("data-edit-layers", page, StringComparison.Ordinal);
        Assert.Contains("This file has hidden layers, so its pages cannot be turned, moved, removed or joined here yet.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-edit-turn=", page, StringComparison.Ordinal);
        const string Layers = "This file, or one joined to it, has hidden layers, so its pages cannot be changed here yet. Nothing was saved.";
        const string Turn = "{\"Pages\":[{\"File\":0,\"Page\":0,\"Turn\":90}]}";
        using HttpResponseMessage turned = await this.PostAsync($"message/{m.Id}/attachment/1/edit/save", token, ("plan", Turn), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        Assert.Equal(Layers, JsonElement.Parse(await turned.Content.ReadAsStringAsync()).GetProperty("problem").GetString());
        using HttpResponseMessage arranged = await this.PostAsync($"message/{m.Id}/attachment/1/edit/arrange", token, ("plan", Turn), ("join", string.Empty), ("entry", string.Empty));
        Assert.Equal(Layers, JsonElement.Parse(await arranged.Content.ReadAsStringAsync()).GetProperty("problem").GetString());
        const string Join = "{\"Pages\":[{\"File\":0,\"Page\":0,\"Turn\":0},{\"File\":1,\"Page\":-1,\"Turn\":0}]}";
        using HttpResponseMessage joined = await this.PostAsync($"message/{m.Id}/attachment/0/edit/arrange", token, ("plan", Join), ("join", "1"), ("entry", string.Empty));
        Assert.Equal(Layers, JsonElement.Parse(await joined.Content.ReadAsStringAsync()).GetProperty("problem").GetString());
        // The plain file's pages still change, and a mark on the layered file is saved.
        using HttpResponseMessage plain = await this.PostAsync($"message/{m.Id}/attachment/0/edit/arrange", token, ("plan", Turn), ("join", string.Empty), ("entry", string.Empty));
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync());
        const string Mark = "{\"Marks\":[{\"Kind\":\"rect\",\"Page\":0,\"X\":60,\"Y\":60,\"W\":120,\"H\":40,\"Color\":\"#d32f2f\",\"Line\":2,\"Size\":12}]}";
        using HttpResponseMessage marked = await this.PostAsync($"message/{m.Id}/attachment/1/edit/save", token, ("plan", Mark), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        Assert.True(marked.IsSuccessStatusCode, await marked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Save_PutsANewFileOnAReply_AndTheOriginalStaysAsItCame()
    {
        await this.SignInAsync();
        byte[] original = SmallPdf("Patient MRN 4471");
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", original));
        string token = Token(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1"));
        const string Plan = "{\"Marks\":[{\"Kind\":\"rect\",\"Page\":0,\"X\":60,\"Y\":60,\"W\":120,\"H\":40,\"Color\":\"#d32f2f\",\"Line\":2,\"Size\":12}],\"Numbers\":{\"Format\":\"Page {n} of {N}\",\"Where\":\"bottom-centre\",\"Start\":1,\"Size\":10}}";
        using HttpResponseMessage saved = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "reply"));
        string body = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.IsSuccessStatusCode, body);
        Guid draft = Guid.Parse(JsonElement.Parse(body).GetProperty("go").GetString()!["/draft/".Length..]);

        // The draft is sealed to the mailbox's key: read through the webmail, as the person reads it.
        string compose = WebUtility.HtmlDecode(await this.client.GetStringAsync($"draft/{draft}"));
        Assert.Contains("report (edited).pdf", compose, StringComparison.Ordinal);
        Assert.Contains("value=\"Re: Files\"", compose, StringComparison.Ordinal);
        Assert.Contains("lab@x.test", compose, StringComparison.Ordinal);
        byte[] made = await this.client.GetByteArrayAsync($"message/{draft}/attachment/0");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(made, 0, 4));
        Assert.NotEqual(Hash(original), Hash(made));
        // §6.3: the mail's own attachment is byte for byte as it came.
        Assert.Equal(Hash(original), Hash(await this.client.GetByteArrayAsync($"message/{m.Id}/attachment/0")));
    }

    [Fact]
    public async Task ARedaction_IsShownBeforeTheFileIsAttached_AndTheSameFileIsAttached()
    {
        // Redact as it will be on chuvadi-pdf 3.35.1 (D-405: off as rc.29 ships).
        this.Viewer.Switches = EditSwitches.All;
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Patient MRN 4471")), ("other.pdf", "application/pdf", SmallPdf("Other")));
        string token = Token(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1"));
        // A box over the whole page: the words go, and the person reads that before anything is attached.
        const string Plan = "{\"Redactions\":[{\"Page\":0,\"X\":0,\"Y\":0,\"W\":595,\"H\":842}]}";
        using HttpResponseMessage first = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        string body = await first.Content.ReadAsStringAsync();
        Assert.True(first.IsSuccessStatusCode, body);
        JsonElement review = JsonElement.Parse(body);
        Assert.False(review.TryGetProperty("go", out _));
        string kept = review.GetProperty("kept").GetString()!;
        string line = Assert.Single(review.GetProperty("review").EnumerateArray()).GetString()!;
        Assert.Matches("^Removed on page 1: [0-9]+ characters\\.$", line);

        // The kept file is for this attachment only, and a key never made finds nothing.
        using HttpResponseMessage elsewhere = await this.PostAsync($"message/{m.Id}/attachment/1/edit/save", token, ("kept", kept), ("entry", string.Empty), ("go", "draft"));
        Assert.Equal(HttpStatusCode.Conflict, elsewhere.StatusCode);
        using HttpResponseMessage unknown = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("kept", new string('a', 32)), ("entry", string.Empty), ("go", "draft"));
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);

        using HttpResponseMessage attach = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("kept", kept), ("entry", string.Empty), ("go", "draft"));
        string attached = await attach.Content.ReadAsStringAsync();
        Assert.True(attach.IsSuccessStatusCode, attached);
        Guid draft = Guid.Parse(JsonElement.Parse(attached).GetProperty("go").GetString()!["/draft/".Length..]);
        byte[] made = await this.client.GetByteArrayAsync($"message/{draft}/attachment/0");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(made, 0, 4));
        Assert.Equal(-1, Encoding.Latin1.GetString(made).IndexOf("4471", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Forward_CarriesTheMailsOtherFiles_AndArrangeJoinsItsOtherPdf()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("one.pdf", "application/pdf", SmallPdf("First file")), ("two.pdf", "application/pdf", SmallPdf("Second file")));
        string token = Token(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1"));

        // The pages as they will be: this file's page, then every page of the mail's other PDF (page -1 spelled out).
        using HttpResponseMessage arranged = await this.PostAsync($"message/{m.Id}/attachment/0/edit/arrange", token,
            ("plan", "{\"Pages\":[{\"File\":0,\"Page\":0,\"Turn\":90},{\"File\":1,\"Page\":-1}]}"), ("join", "1"), ("entry", string.Empty));
        string json = await arranged.Content.ReadAsStringAsync();
        Assert.True(arranged.IsSuccessStatusCode, json);
        JsonElement result = JsonElement.Parse(json);
        string key = result.GetProperty("key").GetString()!;
        Assert.Equal(2, result.GetProperty("pages").GetArrayLength());
        Assert.Equal(1, result.GetProperty("pages")[1].GetProperty("file").GetInt32());
        // Turned a quarter: the first page is now wider than it is tall.
        Assert.True(result.GetProperty("pages")[0].GetProperty("w").GetDouble() > result.GetProperty("pages")[0].GetProperty("h").GetDouble());
        using HttpResponseMessage picture = await this.client.GetAsync($"message/{m.Id}/attachment/0/edit/{key}/page/1?w=300");
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/png", picture.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Second file", await this.client.GetStringAsync($"message/{m.Id}/attachment/0/edit/{key}/text/1"), StringComparison.Ordinal);
        // A key that is not one, or one this session never made, gives nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/edit/{new string('a', 32)}/page/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/edit/..%2Fx/page/0")).StatusCode);

        using HttpResponseMessage saved = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token,
            ("plan", "{\"Watermark\":{\"Text\":\"COPY\",\"Size\":60,\"Angle\":45,\"Opacity\":0.2,\"Color\":\"#808080\"}}"), ("join", string.Empty), ("entry", string.Empty), ("go", "forward"));
        string body = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.IsSuccessStatusCode, body);
        Guid draft = Guid.Parse(JsonElement.Parse(body).GetProperty("go").GetString()!["/draft/".Length..]);
        string compose = WebUtility.HtmlDecode(await this.client.GetStringAsync($"draft/{draft}"));
        foreach (string name in ForwardNames)
        {
            Assert.Contains(name, compose, StringComparison.Ordinal);
        }
        Assert.Contains("value=\"Fwd: Files\"", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanThatDoesNotFit_OrNoToken_SavesNothing()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Only page")));
        string token = Token(await this.client.GetStringAsync($"message/{m.Id}?preview=0&edit=1"));
        int before = this.store.MailboxMessages.Count;
        using HttpResponseMessage bad = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", "{\"Marks\":[{\"Kind\":\"rect\",\"Page\":9}]}"), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        Assert.Contains("Nothing was saved", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using HttpResponseMessage how = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", "{}"), ("join", string.Empty), ("entry", string.Empty), ("go", "send"));
        Assert.Equal(HttpStatusCode.BadRequest, how.StatusCode);
        using HttpResponseMessage self = await this.PostAsync($"message/{m.Id}/attachment/0/edit/save", token, ("plan", "{}"), ("join", "0"), ("entry", string.Empty), ("go", "draft"));
        Assert.NotEqual(HttpStatusCode.OK, self.StatusCode);
        using HttpResponseMessage bare = await this.client.PostAsync($"message/{m.Id}/attachment/0/edit/save", new FormUrlEncodedContent(new Dictionary<string, string> { ["plan"] = "{}", ["go"] = "draft" }));
        Assert.NotEqual(HttpStatusCode.OK, bare.StatusCode);
        Assert.Equal(before, this.store.MailboxMessages.Count);
    }

    [Fact]
    public async Task TheDrawnSignature_IsKeptOnlyWhenAsked_ShownInSettings_AndRemoved()
    {
        await this.SignInAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await this.client.GetAsync("settings/drawn-signature")).StatusCode);
        string settings = WebUtility.HtmlDecode(await this.client.GetStringAsync("settings/mail"));
        Assert.Contains("None kept.", settings, StringComparison.Ordinal);
        string token = Token(settings);
        using HttpResponseMessage kept = await this.PostAsync("settings/drawn-signature", token, ("signature", "{\"Points\":[10,10,40,30,80,12],\"Strokes\":[0],\"W\":480,\"H\":160}"));
        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        string back = await this.client.GetStringAsync("settings/drawn-signature");
        Assert.Contains("\"Points\":[10,10,40,30,80,12]", back, StringComparison.Ordinal);
        Assert.Contains("Kept for signing PDFs", WebUtility.HtmlDecode(await this.client.GetStringAsync("settings/mail")), StringComparison.Ordinal);
        using HttpResponseMessage odd = await this.PostAsync("settings/drawn-signature", token, ("signature", "{\"Points\":[1],\"W\":0,\"H\":0}"));
        Assert.Equal(HttpStatusCode.BadRequest, odd.StatusCode);
        using HttpResponseMessage removed = await this.PostAsync("settings/drawn-signature/remove", token);
        Assert.Equal("/settings/mail?saved=drawnremoved", removed.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await this.client.GetAsync("settings/drawn-signature")).StatusCode);
    }

    [Fact]
    public async Task TheEditedFile_AnswersAnotherMail_JoinsADraft_AndGoesBackOnItsDraft()
    {
        // The owner's check, 17 Nov 2026: A wrote on the 1st, B sent a file on the 5th; B's file, edited, answers A.
        await this.SignInAsync();
        MessageRow a = await this.DeliverFromAsync("a@x.test", "Meeting on Friday");
        MessageRow b = await this.DeliverFromAsync("b@x.test", "The report", ("report.pdf", "application/pdf", SmallPdf("Report")));
        string token = Token(await this.client.GetStringAsync($"message/{b.Id}?preview=0&edit=1"));
        const string Plan = "{\"Marks\":[{\"Kind\":\"rect\",\"Page\":0,\"X\":60,\"Y\":60,\"W\":120,\"H\":40,\"Color\":\"#d32f2f\",\"Line\":2,\"Size\":12}]}";

        // The picker offers both mails, narrows by words, and never offers a draft as a mail to answer.
        string offered = await this.client.GetStringAsync("mail/pick?q=");
        Assert.Contains("Meeting on Friday", offered, StringComparison.Ordinal);
        Assert.Contains("The report", offered, StringComparison.Ordinal);
        Assert.DoesNotContain("The report", await this.client.GetStringAsync("mail/pick?q=Meeting"), StringComparison.Ordinal);

        // Reply to another mail with it: To A, "Re: Meeting on Friday", threaded under A's mail, the edited file on it.
        using HttpResponseMessage answered = await this.PostAsync($"message/{b.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "replyto"), ("target", a.Id.ToString()));
        string answeredBody = await answered.Content.ReadAsStringAsync();
        Assert.True(answered.IsSuccessStatusCode, answeredBody);
        Guid reply = Guid.Parse(JsonElement.Parse(answeredBody).GetProperty("go").GetString()!["/draft/".Length..]);
        string compose = WebUtility.HtmlDecode(await this.client.GetStringAsync($"draft/{reply}"));
        Assert.Contains("value=\"Re: Meeting on Friday\"", compose, StringComparison.Ordinal);
        Assert.Equal("a@x.test", ToOf(compose));
        Assert.Contains("report (edited).pdf", compose, StringComparison.Ordinal);
        Assert.Contains("name=\"inReplyTo\" value=\"" + a.MessageId.Trim('<', '>'), compose, StringComparison.Ordinal);

        // A reply to a draft, or with no mail chosen, is refused: nothing saved.
        using HttpResponseMessage toDraft = await this.PostAsync($"message/{b.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "replyto"), ("target", reply.ToString()));
        Assert.False(toDraft.IsSuccessStatusCode);
        using HttpResponseMessage noTarget = await this.PostAsync($"message/{b.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "replyto"));
        Assert.False(noTarget.IsSuccessStatusCode);

        // The drafts picker offers that draft; Add to a draft puts a second file on it, keeping the first.
        Assert.Contains(reply.ToString("D"), await this.client.GetStringAsync("mail/pick?drafts=1"), StringComparison.Ordinal);
        using HttpResponseMessage added = await this.PostAsync($"message/{b.Id}/attachment/0/edit/save", token, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "adddraft"), ("target", reply.ToString()));
        string addedBody = await added.Content.ReadAsStringAsync();
        Assert.True(added.IsSuccessStatusCode, addedBody);
        Guid joined = Guid.Parse(JsonElement.Parse(addedBody).GetProperty("go").GetString()!["/draft/".Length..]);
        string joinedPage = WebUtility.HtmlDecode(await this.client.GetStringAsync($"draft/{joined}"));
        Assert.Equal(2, Regex.Count(joinedPage, "class=\"catname\" title=\"report \\(edited\\)\\.pdf\""));
        Assert.Contains("value=\"Re: Meeting on Friday\"", joinedPage, StringComparison.Ordinal);

        // A file on a draft, edited there: it goes back in its own place, named once, and the draft opens on it.
        string draftToken = Token(await this.client.GetStringAsync($"draft/{joined}?view=0&edit=1"));
        using HttpResponseMessage back = await this.PostAsync($"message/{joined}/attachment/0/edit/save", draftToken, ("plan", Plan), ("join", string.Empty), ("entry", string.Empty), ("go", "ondraft"));
        string backBody = await back.Content.ReadAsStringAsync();
        Assert.True(back.IsSuccessStatusCode, backBody);
        string go = JsonElement.Parse(backBody).GetProperty("go").GetString()!;
        Assert.EndsWith("?view=0", go, StringComparison.Ordinal);
        string again = WebUtility.HtmlDecode(await this.client.GetStringAsync(go[1..]));
        Assert.Equal(2, Regex.Count(again, "class=\"catname\" title=\"report \\(edited\\)\\.pdf\""));
        Assert.DoesNotContain("(edited) (edited)", again, StringComparison.Ordinal);
        Assert.Contains("data-dfview", again, StringComparison.Ordinal);
        Assert.Contains("?view=0&edit=1", again, StringComparison.Ordinal);
        Assert.Contains("Save it on this draft", WebUtility.HtmlDecode(await this.client.GetStringAsync(go[1..] + "&edit=1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMailForwardedAsAFile_IsDownloadedFromCompose_AsTheMailItself()
    {
        // Found by the screen checks (17 Nov): the whole mail carried as a file has no attachment number of its
        // own, so its Download is the mail's own .eml, never "attachment/-1".
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Report")));
        string compose = await this.client.GetStringAsync($"compose?forwardatt={m.Id}");
        Assert.Contains($"href=\"/message/{m.Id}/raw.eml\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("/attachment/-1", compose, StringComparison.Ordinal);
        using HttpResponseMessage raw = await this.client.GetAsync($"message/{m.Id}/raw.eml");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
    }

    [Fact]
    public async Task ADraft_BecomesAReplyLater_AndItsFilesAreViewedOverIt()
    {
        await this.SignInAsync();
        MessageRow a = await this.DeliverFromAsync("a@x.test", "Meeting on Friday");
        MessageRow b = await this.DeliverFromAsync("b@x.test", "The report", ("report.pdf", "application/pdf", SmallPdf("Report")));
        string token = Token(await this.client.GetStringAsync($"message/{b.Id}?preview=0&edit=1"));
        using HttpResponseMessage made = await this.PostAsync($"message/{b.Id}/attachment/0/edit/save", token, ("plan", "{\"Numbers\":{\"Format\":\"{n}\",\"Where\":\"bottom-centre\",\"Start\":1,\"Size\":10}}"), ("join", string.Empty), ("entry", string.Empty), ("go", "draft"));
        Guid draft = Guid.Parse(JsonElement.Parse(await made.Content.ReadAsStringAsync()).GetProperty("go").GetString()!["/draft/".Length..]);

        // New mail with it: no one to send to yet, and the choice to make it a reply.
        string page = WebUtility.HtmlDecode(await this.client.GetStringAsync($"draft/{draft}"));
        Assert.Contains("data-reply-instead", page, StringComparison.Ordinal);
        string draftToken = Token(page);

        // View on its file: the draft is saved as written, then shown with the file open over it.
        using HttpResponseMessage viewed = await this.PostFormAsync("draft", draftToken, ("draftId", draft.ToString()), ("to", string.Empty), ("subject", "For A"), ("body", "Please see the report."), ("carryFrom", draft.ToString()), ("carry", "0"), ("view", "report (edited).pdf"));
        Assert.Equal(HttpStatusCode.Redirect, viewed.StatusCode);
        string at = viewed.Headers.Location!.OriginalString;
        Assert.EndsWith("?view=0", at, StringComparison.Ordinal);
        string over = WebUtility.HtmlDecode(await this.client.GetStringAsync(at.TrimStart('/')));
        Assert.Contains("data-dfview", over, StringComparison.Ordinal);
        Assert.Contains("value=\"For A\"", over, StringComparison.Ordinal);
        Guid saved = Guid.Parse(at["/draft/".Length..at.IndexOf('?', StringComparison.Ordinal)]);

        // Reply to a mail instead: A's mail, a reply to all; what was written and the file stay, threaded under A.
        using HttpResponseMessage instead = await this.PostFormAsync("draft", draftToken, ("draftId", saved.ToString()), ("to", string.Empty), ("subject", "For A"), ("body", "Please see the report."), ("carryFrom", saved.ToString()), ("carry", "0"), ("replyto", a.Id.ToString()), ("replyall", "1"));
        Assert.Equal(HttpStatusCode.Redirect, instead.StatusCode);
        string reply = WebUtility.HtmlDecode(await this.client.GetStringAsync(instead.Headers.Location!.OriginalString.TrimStart('/')));
        Assert.Contains("value=\"Re: Meeting on Friday\"", reply, StringComparison.Ordinal);
        Assert.Equal("a@x.test", ToOf(reply));
        Assert.Contains("Please see the report.", reply, StringComparison.Ordinal);
        Assert.Contains("report (edited).pdf", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("data-reply-instead", reply, StringComparison.Ordinal);

        // A mail that is not a draft is never shown as one; nor is a file that is not there.
        Assert.DoesNotContain("data-dfview", await this.client.GetStringAsync($"draft/{a.Id}?view=0"), StringComparison.Ordinal);
        Assert.DoesNotContain("data-dfview", await this.client.GetStringAsync($"draft/{saved}?view=7"), StringComparison.Ordinal);
    }

    private static readonly string[] ForwardNames = { "one (edited).pdf", "two.pdf" };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private async Task SignInAsync()
    {
        string token = Token(await (await this.client.GetAsync("sign-in")).Content.ReadAsStringAsync());
        HttpResponseMessage res = await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
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

    // The compose form as the browser sends it (it carries files, so it is multipart).
    private async Task<HttpResponseMessage> PostFormAsync(string path, string token, params (string Key, string Value)[] fields)
    {
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach ((string k, string v) in fields)
        {
            form.Add(new StringContent(v), k);
        }
        return await this.client.PostAsync(path, form);
    }

    // What the compose page's To box holds.
    private static string ToOf(string html)
    {
        Match to = Regex.Match(html, "id=\"to\" name=\"to\" type=\"text\" value=\"([^\"]*)\"", RegexOptions.CultureInvariant);
        Assert.True(to.Success, "no To box on the page");
        return to.Groups[1].Value;
    }

    private static string Token(string html)
    {
        Match match = TokenRegex.Match(html);
        Assert.True(match.Success, "no antiforgery token on the page");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private Task<MessageRow> DeliverAsync(params (string Name, string Type, byte[] Body)[] files) => this.DeliverFromAsync("lab@x.test", "Files", files);

    private async Task<MessageRow> DeliverFromAsync(string from, string subject, params (string Name, string Type, byte[] Body)[] files)
    {
        var raw = new StringBuilder("From: " + from + "\r\nSubject: " + subject + "\r\nMessage-ID: <" + Guid.NewGuid().ToString("N") + "@x.test>\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nsee attached\r\n");
        foreach ((string name, string type, byte[] body) in files)
        {
            raw.Append("--b\r\nContent-Type: ").Append(type).Append("; name=").Append(name)
                .Append("\r\nContent-Disposition: attachment; filename=").Append(name)
                .Append("\r\nContent-Transfer-Encoding: base64\r\n\r\n")
                .Append(Convert.ToBase64String(body, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }
        raw.Append("--b--\r\n");
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = from,
            EnvelopeTo = Arun,
            RawBytes = Encoding.UTF8.GetBytes(raw.ToString()),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    // One A4 page with one line of text and a correct cross-reference table (as tools/viewer_check.py makes it).
    private static byte[] SmallPdf(string line) => SmallPdf(line, false);

    // A one-page PDF; with layers, one hidden layer declared (optional content, D-406).
    private static byte[] SmallPdf(string line, bool layers)
    {
        string stream = "BT /F1 24 Tf 72 720 Td (" + line + ") Tj ET";
        List<string> objects = new()
        {
            layers ? "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> >>" : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>\nstream\n" + stream + "\nendstream",
        };
        if (layers)
        {
            objects.Add("<< /Type /OCG /Name (Hidden notes) >>");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            pdf.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
