using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.24: threads (item 68, brief 10a and 10f), Anjal's own alerts (item 86), whole-mail and
/// link checks (items 93 and 94), the recovery address (item 90) and the rc.23 follow-ups (item 95).
/// One test or more for each, named after what the person sees.
/// </summary>
[Collection("Operators")]
public sealed class Rc24Tests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ArunOnly = { "arun@sgh.in" };
    private static readonly string[] ChainAB = { "a1@lab.test", "b2@lab.test" };
    private static readonly string[] ChainB = { "b2@lab.test" };
    private static readonly string[] ChainABC = { "a1@lab.test", "b2@lab.test", "c3@lab.test" };
    private static readonly string[] Letters = { "A", "B", "C", "D" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc24-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private MailboxService svc = null!;
    private string baseAddress = string.Empty;
    private TenantRow sgh = new();
    private MailboxRow arun = new();

    public void Dispose()
    {
        foreach (HttpClient c in this.clients)
        {
            c.Dispose();
        }
    }

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.sgh = await this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.sgh.Id, Domain = "sgh.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        await this.Person("meera", "Meera Iyer");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.svc = this.app.Services.GetRequiredService<MailboxService>();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
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

    // ---- Item 68: threads (brief 10a, 10f) ----

    [Fact]
    public void Threads_MessageIdsAndTheReferencesChain_FollowRfc5322()
    {
        Assert.Equal(ChainAB, MailboxService.MessageIds(" <a1@lab.test>\r\n <b2@lab.test> junk <> "));
        Assert.Empty(MailboxService.MessageIds(null));
        // A reply carries the original's References, then the original's own id.
        Assert.Equal(ChainABC, MailboxService.ReferencesFor("<a1@lab.test> <b2@lab.test>", "<b2@lab.test>", "<c3@lab.test>"));
        // An original with no References: its In-Reply-To starts the chain.
        Assert.Equal(ChainAB, MailboxService.ReferencesFor(null, "<a1@lab.test>", "b2@lab.test"));
        Assert.Equal(ChainB, MailboxService.ReferencesFor(null, null, "<b2@lab.test>"));
        // The newest 20 are kept.
        string many = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"<m{i}@lab.test>"));
        IReadOnlyList<string> kept = MailboxService.ReferencesFor(many, null, "<last@lab.test>");
        Assert.Equal(20, kept.Count);
        Assert.Equal("last@lab.test", kept[^1]);
        Assert.Equal("m11@lab.test", kept[0]);
    }

    [Fact]
    public void Threads_LettersRunAToZ_ThenAA()
    {
        Assert.Equal("A", MailboxService.LetterOf(0));
        Assert.Equal("Z", MailboxService.LetterOf(25));
        Assert.Equal("AA", MailboxService.LetterOf(26));
        Assert.Equal("AB", MailboxService.LetterOf(27));
    }

    [Fact]
    public void Threads_ReplyToBoth_AddsTheOtherReplysPeopleOnce()
    {
        string cc = MailboxService.MergeAddresses("Ramesh <ramesh@lab.test>", "priya@lab.test", "Anil <anil@lab.test>, priya@lab.test, RAMESH@lab.test");
        Assert.Equal("priya@lab.test, Anil <anil@lab.test>", cc);
        Assert.Equal("Anil <anil@lab.test>", MailboxService.MergeAddresses("ramesh@lab.test", string.Empty, "Anil <anil@lab.test>"));
        Assert.Equal("x@lab.test", MailboxService.MergeAddresses("ramesh@lab.test", "x@lab.test", "ramesh@lab.test"));
    }

    [Fact]
    public async Task Threads_TheTreeComesFromTheHeadersOnly_NeverFromTheSubjectOrTime()
    {
        (MessageRow a, MessageRow b, MessageRow c, MessageRow d) = await this.SplitConversationAsync();
        ThreadView t = await this.svc.ThreadOfAsync(this.arun.Id, c);
        Assert.Equal(4, t.Mails.Count);
        Assert.Equal(Letters, t.Mails.Select(m => m.Letter));
        Assert.Null(t.Get(a.Id)!.ParentId);
        Assert.Equal(a.Id, t.Get(b.Id)!.ParentId);
        // C names A only in References: it answers A, not B, though B is just above it.
        Assert.Equal(a.Id, t.Get(c.Id)!.ParentId);
        // D carries no reply headers: placed by its time, "reply link unknown".
        Assert.True(t.Get(d.Id)!.LinkUnknown);
        Assert.Null(t.Get(d.Id)!.ParentId);
        Assert.True(t.Branches);
        Assert.Equal(a.Id, t.FirstSplit!.Id);
        Assert.Equal(new[] { b.Id }, t.AlsoReplied(c.Id).Select(m => m.Id));
        Assert.Equal(new[] { b.Id, c.Id }, t.RepliesTo(a.Id).Select(m => m.Id));
        Assert.True(t.Get(b.Id)!.MainLine);
        Assert.False(t.Get(c.Id)!.MainLine);
        Assert.Equal(1, t.Get(c.Id)!.Depth);
        Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id }, t.TreeOrder().Select(m => m.Id));
        // This conversation: everyone on any mail, in the order they joined; the files with who sent them.
        Assert.Equal("Ramesh", t.People[0].Name);
        Assert.Equal(2, t.People.Single(p => p.Address == "ramesh@lab.test").Mails);
        Assert.Equal(3, t.People.Single(p => p.Address == "anil@lab.test").JoinedAt);
        ThreadFile file = Assert.Single(t.Files);
        Assert.Equal("report.pdf", file.Name);
        Assert.Equal("Priya", file.From);
    }

    [Fact]
    public async Task Threads_TheReader_ShowsLettersReplyingToNotesTheTagAndTheEnvelopeSections()
    {
        (MessageRow a, MessageRow b, MessageRow c, MessageRow _) = await this.SplitConversationAsync();
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = WebUtility.HtmlDecode(await client.GetStringAsync($"message/{c.Id}"));
        Assert.Contains("data-thread", page, StringComparison.Ordinal);
        Assert.Contains("4 mails", page, StringComparison.Ordinal);
        Assert.Contains("class=\"lbody topen\"", page, StringComparison.Ordinal);
        // A was answered twice; C answers A, not B just above it.
        Assert.Contains("2 replies: B, C", page, StringComparison.Ordinal);
        Assert.Contains("↳ replying to A (Ramesh,", page, StringComparison.Ordinal);
        Assert.Contains("not to B", page, StringComparison.Ordinal);
        Assert.Contains("reply link unknown", page, StringComparison.Ordinal);
        // The envelope follows the open mail.
        Assert.Contains("In reply to", page, StringComparison.Ordinal);
        Assert.Contains("Also replied to the same mail", page, StringComparison.Ordinal);
        Assert.Contains("you haven't replied", page, StringComparison.Ordinal);
        Assert.Contains("This conversation", page, StringComparison.Ordinal);
        Assert.Contains("All files in this conversation", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/message/{a.Id}\"", page, StringComparison.Ordinal);
        // The branch warning before replying: Reply to both, Read B first, Reply to this one only.
        Assert.Contains($"replyall={c.Id}&also={b.Id}", page, StringComparison.Ordinal);
        Assert.Contains("Read B first", page, StringComparison.Ordinal);
        Assert.Contains("Reply to this one only", page, StringComparison.Ordinal);
        Assert.Contains("Reply to Anil", page, StringComparison.Ordinal);
        // The Reply tree switch: indented, and the replying-to notes are not needed there.
        string tree = WebUtility.HtmlDecode(await client.GetStringAsync($"message/{c.Id}?tree=1"));
        Assert.Contains("style=\"--d:1\"", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("↳ replying to", tree, StringComparison.Ordinal);
        Assert.Contains($"href=\"/message/{b.Id}?tree=1\"", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_ReplyToBoth_PutsTheOtherRepliersPeopleOnTheAnswer()
    {
        (MessageRow _, MessageRow b, MessageRow c, MessageRow _) = await this.SplitConversationAsync();
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string compose = WebUtility.HtmlDecode(await client.GetStringAsync($"compose?replyall={c.Id}&also={b.Id}"));
        Assert.Contains("priya@lab.test", compose, StringComparison.Ordinal);
        Assert.Contains("anil@lab.test", compose, StringComparison.Ordinal);
        // And the reply carries the whole chain, so the receiver can thread it too.
        Assert.Contains("name=\"references\" value=\"<a1@lab.test> <c3@lab.test>\"", compose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_ASentReply_WritesInReplyToAndTheFullReferencesChain()
    {
        (MessageRow _, MessageRow b, MessageRow _, MessageRow _) = await this.SplitConversationAsync();
        ComposePrefill prefill = (await this.svc.PrefillAsync(this.arun.Id, b.Id, MailboxService.PrefillKind.Reply))!;
        Assert.Equal("<a1@lab.test> <b2@lab.test>", prefill.References);
        Assert.Null(await this.svc.SendAsync(this.arun.Id, new ComposeRequest
        {
            To = prefill.To,
            Subject = prefill.Subject,
            Body = "Thanks",
            InReplyTo = prefill.InReplyTo,
            References = prefill.References,
        }));
        FolderRow sent = (await this.svc.GetFolderAsync(this.arun.Id, "Sent"))!;
        MessageRow mine = (await this.store.ListMessagesAsync(this.arun.Id, sent.Id, 1, 0))[0];
        ThreadView t = await this.svc.ThreadOfAsync(this.arun.Id, mine);
        ThreadMail own = t.Get(mine.Id)!;
        Assert.True(own.IsOwn);
        Assert.Equal(b.Id, own.ParentId);
        Assert.True(t.YouReplied(b.Id));
    }

    [Fact]
    public async Task Threads_GroupByConversation_IsOnByDefault_AndEachMailOnItsOwnTurnsItOff()
    {
        (MessageRow _, MessageRow _, MessageRow c, MessageRow _) = await this.SplitConversationAsync();
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string settings = await client.GetStringAsync("settings/mail");
        Assert.Contains("action=\"/settings/conversations\"", settings, StringComparison.Ordinal);
        string inbox = WebUtility.HtmlDecode(await client.GetStringAsync("folder/INBOX"));
        Assert.Equal(1, Regex.Count(inbox, "Results for Friday"));

        HttpResponseMessage res = await Post(client, "settings/conversations", Token(settings), ("group", "each"));
        Assert.Equal("/settings/mail?saved=mail", res.Headers.Location!.ToString());
        Assert.False((await this.svc.GetMailSettingsAsync(this.arun.Id)).GroupByConversation);
        inbox = WebUtility.HtmlDecode(await client.GetStringAsync("folder/INBOX"));
        Assert.Equal(4, Regex.Count(inbox, "Results for Friday"));
        string page = await client.GetStringAsync($"message/{c.Id}");
        Assert.DoesNotContain("data-thread", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_TheLook_HasTheMarginLineTheAmberBranchAndFoldedRows()
    {
        string css = await this.Client().GetStringAsync("app.css");
        Assert.Contains(".trows { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; --tbranch: #E8A317; }", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px;", css, StringComparison.Ordinal);
        Assert.Contains(".trow.unk { box-shadow: none; border-left: 3px dotted var(--ink-muted); }", css, StringComparison.Ordinal);
        Assert.Contains(".lbody.topen { outline: 2px solid var(--brand);", css, StringComparison.Ordinal);
    }

    // ---- Item 86: Anjal's own alerts (D-177, DEF-165, DEF-129, DEF-159) ----

    [Fact]
    public async Task Alerts_ByDefault_ComeAsOneDailyDigest_ThatReplacesTheLastOne()
    {
        await AsOperatorAsync(async () =>
        {
            Assert.Equal(MailboxService.AlertDigest, await this.svc.AlertModeAsync());
            // A Tuesday in the past (the mails themselves are stored at the real time). No drill recorded: a warning.
            DateTimeOffset early = new(2026, 1, 13, 8, 0, 0, TimeSpan.FromHours(5.5));
            await this.svc.OperatorMailAsync(this.root, early);
            Assert.DoesNotContain(this.Subjects(), s => s.Contains("No restore drill", StringComparison.Ordinal));
            Assert.DoesNotContain(this.Subjects(), s => s.StartsWith("Anjal daily digest", StringComparison.Ordinal));
            // From 09:00: one digest with what is open.
            await this.svc.OperatorMailAsync(this.root, early.AddHours(1.5));
            MessageRow first = Assert.Single(this.Mail(), m => m.Subject.StartsWith("Anjal daily digest, 13 Jan 2026", StringComparison.Ordinal));
            Assert.Equal(0, await this.svc.OperatorMailAsync(this.root, early.AddHours(3)));
            // The drill is recorded: it is resolved, and told in the next day's digest - which replaces the first.
            await this.svc.RecordDrillAsync(new RestoreDrill { Date = "2026-01-13", Passed = true, By = "arun@sgh.in" });
            await this.svc.OperatorMailAsync(this.root, early.AddHours(4));
            Assert.DoesNotContain(this.Subjects(), s => s.StartsWith("Anjal resolved", StringComparison.Ordinal));
            await this.svc.OperatorMailAsync(this.root, early.AddDays(1).AddHours(1.5));
            MessageRow second = Assert.Single(this.Mail(), m => m.Subject.StartsWith("Anjal daily digest, 14 Jan 2026", StringComparison.Ordinal));
            Assert.Equal("Trash", await this.FolderNameAsync(first));
            Assert.Equal(FolderRow.Inbox, await this.FolderNameAsync(second));
            ParsedText digest = await this.TextOfAsync(second);
            Assert.Contains("Resolved since the last digest:", digest.Text, StringComparison.Ordinal);
            Assert.Contains("No restore drill recorded", digest.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Alerts_ANewProblemMarkedBad_IsMailedAtOnce_EvenWithTheDigest()
    {
        await AsOperatorAsync(async () =>
        {
            // The last drill passed a year ago: overdue, which is bad.
            await this.svc.RecordDrillAsync(new RestoreDrill { Date = "2025-01-10", Passed = true, By = "arun@sgh.in" });
            await this.svc.OperatorMailAsync(this.root, new DateTimeOffset(2026, 1, 13, 7, 0, 0, TimeSpan.FromHours(5.5)));
            MessageRow alert = Assert.Single(this.Mail(), m => m.Subject.StartsWith("Anjal alert", StringComparison.Ordinal));
            Assert.Contains("Restore drill overdue", (await this.TextOfAsync(alert)).Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Alerts_TheOperatorMayStillChooseEveryAlert_AndTheChoiceIsOnServiceHealth()
    {
        await AsOperatorAsync(async () =>
        {
            HttpClient op = this.Client();
            await SignInAsync(op, "arun@sgh.in");
            string page = await op.GetStringAsync("ops/health");
            Assert.Contains("data-alert-mode", page, StringComparison.Ordinal);
            HttpResponseMessage res = await Post(op, "ops/alerts", Token(page), ("mode", "each"));
            Assert.Equal("/ops/health?saved=alerts", res.Headers.Location!.ToString());
            Assert.Equal(MailboxService.AlertEach, await this.svc.AlertModeAsync());
            await this.svc.OperatorMailAsync(this.root, new DateTimeOffset(2026, 1, 13, 7, 0, 0, TimeSpan.FromHours(5.5)));
            // Every alert as it happens: the warning about the drill too, which the digest would keep for 09:00.
            MessageRow alert = Assert.Single(this.Mail(), m => m.Subject.StartsWith("Anjal alert", StringComparison.Ordinal));
            Assert.Contains("No restore drill recorded", (await this.TextOfAsync(alert)).Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FromAnjal_AnjalsOwnMail_IsFiledUnderFromAnjal_AndHasItsOwnSectionOnToday()
    {
        Assert.Null(await this.svc.SendSystemMailAsync(this.arun, "arun@sgh.in", "Your data is ready to download", "The link works for 7 days."));
        MessageRow notice = this.Mail().Single(m => m.Subject == "Your data is ready to download");
        CategoryView category = (await this.svc.ListCategoriesAsync(this.arun.Id)).Single(c => c.Row.Name == MailboxService.FromAnjalCategory);
        Assert.Equal(category.Row.Id, notice.CategoryId);
        // The category is made once: a second mail uses it.
        Assert.Null(await this.svc.SendSystemMailAsync(this.arun, "arun@sgh.in", "A second notice", "x"));
        Assert.Single(await this.svc.ListCategoriesAsync(this.arun.Id), c => c.Row.Name == MailboxService.FromAnjalCategory);
        TodayView today = await this.svc.TodayAsync(this.arun.Id, ZonedClock.Default, DateTimeOffset.UtcNow);
        Assert.Equal(2, today.FromAnjal.Count);
        Assert.Empty(today.Lists);
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = WebUtility.HtmlDecode(await client.GetStringAsync("dashboard/today"));
        Assert.Contains("class=\"tdlists tdanjal\"", page, StringComparison.Ordinal);
        Assert.Contains("From Anjal · 2", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def159_MailBackFromSnooze_HasItsOwnSectionAboveTheRest()
    {
        MessageRow m = await this.DeliverAsync("From: Ramesh <ramesh@lab.test>\r\nTo: arun@sgh.in\r\nSubject: Calibration dates\r\n\r\nx\r\n", "ramesh@lab.test");
        Assert.Null(await this.svc.SnoozeAsync(this.arun.Id, m.Id, DateTimeOffset.UtcNow.AddMinutes(3)));
        Assert.True(await this.svc.WakeSnoozedAsync(DateTimeOffset.UtcNow.AddMinutes(10)) >= 1);
        TodayView today = await this.svc.TodayAsync(this.arun.Id, ZonedClock.Default, DateTimeOffset.UtcNow);
        Assert.Equal(m.Id, Assert.Single(today.Woken).Message.Id);
        Assert.DoesNotContain(today.NeedsYou, r => r.Message.Id == m.Id);
        Assert.Equal(m.Id, today.InOrder.First().Message.Id);
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = WebUtility.HtmlDecode(await client.GetStringAsync("dashboard/today"));
        Assert.Contains("Back from snooze · 1", page, StringComparison.Ordinal);
    }

    // ---- Items 93 and 94: encrypted archives, macros, the whole mail and its links (D-196, D-197) ----

    [Fact]
    public void Files_EncryptedArchivesAndMacros_AreFoundFromTheirStructureAlone()
    {
        byte[] plainZip = Zip(("notes.txt", "hello"u8.ToArray()));
        Assert.Equal(Anjal.Mailbox.FileRisk.None, Anjal.Mailbox.FileRisks.Inspect("notes.zip", plainZip));
        Assert.Equal(Anjal.Mailbox.FileRisk.Encrypted, Anjal.Mailbox.FileRisks.Inspect("notes.zip", Encrypt(plainZip)));
        Assert.Equal(Anjal.Mailbox.FileRisk.Macros, Anjal.Mailbox.FileRisks.Inspect("budget.xlsx", Zip(("xl/workbook.xml", "<x/>"u8.ToArray()), ("xl/vbaProject.bin", new byte[] { 1, 2, 3 }))));
        Assert.Equal(Anjal.Mailbox.FileRisk.Macros, Anjal.Mailbox.FileRisks.Inspect("budget.xlsm", plainZip));
        byte[] ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(new byte[512]).ToArray();
        Assert.Equal(Anjal.Mailbox.FileRisk.None, Anjal.Mailbox.FileRisks.Inspect("letter.doc", ole));
        Assert.Equal(Anjal.Mailbox.FileRisk.Macros, Anjal.Mailbox.FileRisks.Inspect("letter.doc", ole.Concat(Encoding.Unicode.GetBytes("_VBA_PROJECT")).ToArray()));
        Assert.Equal(Anjal.Mailbox.FileRisk.Encrypted, Anjal.Mailbox.FileRisks.Inspect("letter.docx", ole.Concat(Encoding.Unicode.GetBytes("EncryptedPackage")).ToArray()));
        // RAR 5 with its headers encrypted; 7-Zip with the AES coder.
        Assert.Equal(Anjal.Mailbox.FileRisk.Encrypted, Anjal.Mailbox.FileRisks.Inspect("scan.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 1, 2, 3, 4, 0x0C, 0x04, 0, 0 }));
        Assert.Equal(Anjal.Mailbox.FileRisk.Encrypted, Anjal.Mailbox.FileRisks.Inspect("scan.7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4, 9, 9, 0x06, 0xF1, 0x07, 0x01, 0 }));
        Assert.Equal(Anjal.Mailbox.FileRisk.None, Anjal.Mailbox.FileRisks.Inspect("photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF }));
    }

    [Fact]
    public async Task Files_TheReader_WarnsAboutALockedArchiveAndMacros_AndStillDeliversThem()
    {
        byte[] locked = Encrypt(Zip(("results.pdf", "%PDF-1.4"u8.ToArray())));
        byte[] macro = Zip(("word/document.xml", "<w/>"u8.ToArray()), ("word/vbaProject.bin", new byte[] { 7 }));
        string raw = "From: Ramesh <ramesh@lab.test>\r\nTo: arun@sgh.in\r\nSubject: The files\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n" +
            "--b\r\nContent-Type: text/plain\r\n\r\nAttached.\r\n" +
            "--b\r\nContent-Type: application/zip; name=\"results.zip\"\r\nContent-Disposition: attachment; filename=\"results.zip\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(locked) + "\r\n" +
            "--b\r\nContent-Type: application/msword; name=\"plan.docx\"\r\nContent-Disposition: attachment; filename=\"plan.docx\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(macro) + "\r\n--b--\r\n";
        MessageRow m = await this.DeliverAsync(raw, "ramesh@lab.test");
        MessageView view = (await this.svc.OpenAsync(this.arun.Id, m.Id, allowRemoteImages: false))!;
        Assert.Equal(2, view.FileRisks.Count);
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = WebUtility.HtmlDecode(await client.GetStringAsync($"message/{m.Id}"));
        Assert.Contains("data-file-risk=\"encrypted\"", page, StringComparison.Ordinal);
        Assert.Contains("results.zip is locked with a password", page, StringComparison.Ordinal);
        Assert.Contains("data-file-risk=\"macros\"", page, StringComparison.Ordinal);
        Assert.Contains("plan.docx contains macros", page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"frisk\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_SuspiciousLinks_AreKnownFromTheLinkAlone()
    {
        Assert.Equal(LinkChecker.MismatchWords, LinkChecker.Suspicion(new Uri("https://login.evil.example/sbi"), "www.onlinesbi.sbi"));
        Assert.Null(LinkChecker.Suspicion(new Uri("https://www.onlinesbi.sbi/login"), "onlinesbi.sbi"));
        Assert.Null(LinkChecker.Suspicion(new Uri("https://lab.test/report"), "Open the report"));
        Assert.Equal(LinkChecker.ShortenerWords, LinkChecker.Suspicion(new Uri("https://bit.ly/3xYz"), "Open"));
        Assert.Equal(LinkChecker.NumberWords, LinkChecker.Suspicion(new Uri("http://203.0.113.9/pay"), "Pay"));
        Assert.Equal(LinkChecker.LookalikeWords, LinkChecker.Suspicion(new Uri("https://xn--pypal-4ve.com/"), "Sign in"));
        Assert.Equal(LinkChecker.DownloadWords, LinkChecker.Suspicion(new Uri("https://files.example/invoice.exe"), "Invoice"));
    }

    [Fact]
    public async Task Links_AKnownDangerousOne_IsSwitchedOff_ASuspiciousOne_IsOutlined()
    {
        string list = Path.Combine(this.root + "-list.txt");
        await File.WriteAllTextAsync(list, "# known dangerous\nbad.example\nhttps://shared.example/phish/\n");
        try
        {
            var links = new LinkChecker(list, null);
            string html = "<p><a href=\"https://www.bad.example/x\">Track your parcel</a> and <a href=\"https://shared.example/phish/1\">this</a> and " +
                "<a href=\"https://shared.example/fine\">fine</a> and <a href=\"https://evil.example/\" title=\"x\">www.sbi.co.in</a> and <a href=\"mailto:a@b.c\">mail</a></p>";
            (string result, IReadOnlyList<LinkFinding> found) = await links.ApplyAsync(html, allOff: false);
            Assert.Equal(2, found.Count(f => f.Verdict == LinkVerdict.Bad));
            Assert.Contains("<span class=\"anjal-off\" title=\"Anjal switched this link off: www.bad.example is on the list of known dangerous addresses.\">Track your parcel</span>", result, StringComparison.Ordinal);
            Assert.DoesNotContain("https://www.bad.example", result, StringComparison.Ordinal);
            Assert.Contains("<a href=\"https://shared.example/fine\">fine</a>", result, StringComparison.Ordinal);
            Assert.Contains("data-anjal-warn=\"1\" title=\"The words show sbi.co.in, but the link goes to evil.example.\"", result, StringComparison.Ordinal);
            Assert.DoesNotContain("title=\"x\"", result, StringComparison.Ordinal);
            // The whole mail flagged: every web link off, mail links too.
            (string off, IReadOnlyList<LinkFinding> all) = await new LinkChecker(null, null).ApplyAsync(html, allOff: true);
            Assert.Equal(5, all.Count);
            Assert.DoesNotContain("<a ", off, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(list);
        }
    }

    [Fact]
    public async Task WholeMail_FlaggedByTheScanner_IsDeliveredWithAWarning_AndItsLinksOff()
    {
        var sink = new MailboxSink(this.store, this.maildir) { Scanner = new WholeMailScanner() };
        DeliveryResult result = await sink.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "parcel@courier.test",
            EnvelopeTo = ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes("From: Courier <parcel@courier.test>\r\nTo: arun@sgh.in\r\nSubject: Your parcel\r\nMIME-Version: 1.0\r\nContent-Type: text/html\r\n\r\n<p><a href=\"https://courier.test/track\">Track it</a></p>\r\n"),
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        FolderRow inbox = (await this.svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox))!;
        MessageRow m = (await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 1, 0))[0];
        MessageView view = (await this.svc.OpenAsync(this.arun.Id, m.Id, allowRemoteImages: false))!;
        Assert.Equal("Heuristics.Phishing.Email.SpoofedDomain", view.MailWarning);
        Assert.Contains("class=\"anjal-off\"", view.BodyHtml, StringComparison.Ordinal);
        HttpClient client = this.Client();
        await SignInAsync(client, "arun@sgh.in");
        string page = WebUtility.HtmlDecode(await client.GetStringAsync($"message/{m.Id}"));
        Assert.Contains("data-mail-warning", page, StringComparison.Ordinal);
        Assert.Contains("Anjal's virus scanner flagged this mail as dangerous.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeMail_TheWarningHeader_IsAnjalsAlone()
    {
        byte[] forged = Encoding.ASCII.GetBytes("From: a@b.test\r\nX-Anjal-Mail-Warning: fake\r\n  folded\r\nSubject: Hi\r\n\r\nBody\r\n");
        Assert.Equal("From: a@b.test\r\nSubject: Hi\r\n\r\nBody\r\n", Encoding.ASCII.GetString(Anjal.Mailbox.AttachmentPolicy.MarkWholeMail(forged, null)));
        Assert.Equal("X-Anjal-Mail-Warning: Phish.Test\r\nFrom: a@b.test\r\nSubject: Hi\r\n\r\nBody\r\n", Encoding.ASCII.GetString(Anjal.Mailbox.AttachmentPolicy.MarkWholeMail(forged, "Phish.Test")));
        byte[] clean = Encoding.ASCII.GetBytes("From: a@b.test\r\nSubject: Hi\r\n\r\nBody\r\n");
        Assert.Same(clean, Anjal.Mailbox.AttachmentPolicy.MarkWholeMail(clean, null));
    }

    // ---- Item 90: the recovery address, compulsory for individuals (D-198) ----

    [Fact]
    public async Task Recovery_AnIndividualWithoutAProvedAddress_IsAskedFirst_AndTheMailedLinkOpensTheMail()
    {
        (TenantRow _, MailboxRow priya) = await this.IndividualAsync();
        HttpClient client = this.Client();
        await SignInAsync(client, "priya.raman@anjalmail.test");
        HttpResponseMessage held = await client.GetAsync("folder/INBOX");
        Assert.Equal("/settings/recovery?required=1", held.Headers.Location!.ToString());
        string page = WebUtility.HtmlDecode(await client.GetStringAsync("settings/recovery?required=1"));
        Assert.Contains("Anjal does not use text messages.", page, StringComparison.Ordinal);
        Assert.Contains(">Later</button>", page, StringComparison.Ordinal);
        // A new address needs the password.
        HttpResponseMessage res = await Post(client, "settings/recovery/send", Token(page), ("recovery", "priya@home.test"), ("password", "wrong"));
        Assert.Contains("/settings/recovery?e=", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        res = await Post(client, "settings/recovery/send", Token(page), ("recovery", "priya@home.test"), ("password", "correct horse battery"));
        Assert.Equal("/settings/recovery?sent=1", res.Headers.Location!.ToString());
        (string m, string t) = this.RecoveryLink("priya@home.test");
        Assert.Equal(priya.Id.ToString("N"), m);
        // Still held until the link is used; the link page asks for one press.
        Assert.Equal("/settings/recovery?required=1", (await client.GetAsync("folder/INBOX")).Headers.Location!.ToString());
        HttpClient elsewhere = this.Client();
        string confirm = await elsewhere.GetStringAsync($"recovery/confirm?m={m}&t={t}");
        Assert.Contains("action=\"/auth/recovery/confirm\"", confirm, StringComparison.Ordinal);
        Assert.False((await this.svc.GetSecurityAsync(priya.Id)).RecoveryConfirmed);
        res = await Post(elsewhere, "auth/recovery/confirm", Token(confirm), ("m", m), ("t", t));
        Assert.Equal("/recovery/confirm?done=1", res.Headers.Location?.ToString() ?? res.StatusCode.ToString());
        Assert.True((await this.svc.GetSecurityAsync(priya.Id)).RecoveryConfirmed);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("folder/INBOX")).StatusCode);
        // The link works once.
        Assert.False(await this.svc.ConfirmRecoveryAsync(priya.Id, t));
    }

    [Fact]
    public async Task Recovery_LaterHoldsTheQuestionBack_ForSevenDaysOnly_AndOrganisationsAreNotAsked()
    {
        (TenantRow _, MailboxRow priya) = await this.IndividualAsync();
        HttpClient client = this.Client();
        await SignInAsync(client, "priya.raman@anjalmail.test");
        string page = await client.GetStringAsync("settings/recovery?required=1");
        HttpResponseMessage res = await Post(client, "settings/recovery/later", Token(page));
        Assert.Equal("/", res.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("folder/INBOX")).StatusCode);
        var doc = new SecurityDocument { RecoveryAskedAt = DateTimeOffset.UtcNow.AddDays(-6) };
        Assert.True(MailboxService.RecoveryLaterAllowed(doc, DateTimeOffset.UtcNow));
        doc.RecoveryAskedAt = DateTimeOffset.UtcNow.AddDays(-8);
        Assert.False(MailboxService.RecoveryLaterAllowed(doc, DateTimeOffset.UtcNow));
        // A person in an organisation is never held for it.
        Assert.False(await this.svc.RecoveryWaitingAsync(this.arun.Id));
        Assert.NotEqual("recovery", await this.svc.SignInDemandAsync(this.arun.Id));
    }

    [Fact]
    public async Task Recovery_AnIndividualMadeByUserManagement_IsSentTheLinkAtOnce()
    {
        (TenantRow individuals, MailboxRow _) = await this.IndividualAsync();
        (string? error, MailboxRow? made) = await this.svc.AddPersonAsync(individuals, "Anil", "Menon", "anil.menon", "anjalmail.test", "anil@home.test", "+91 98765 43210",
            new DateOnly(1990, 1, 1), "Lotus-Garden-42-Blue", string.Empty, false, "usermanagement@anjalmail.test", "https://mail.anjalmail.test");
        Assert.Null(error);
        (string m, string _) = this.RecoveryLink("anil@home.test");
        Assert.Equal(made!.Id.ToString("N"), m);
        Assert.Contains("https://mail.anjalmail.test/recovery/confirm?m=", Encoding.UTF8.GetString(this.store.Outbound.Last(o => o.EnvelopeTo == "anil@home.test").RawBytes), StringComparison.Ordinal);
    }

    // ---- Item 95: rc.23 follow-ups ----

    [Fact]
    public async Task Def168_TheServersOwnDomainAndADomainInUse_HaveNoRemove_AndSayWhy()
    {
        Assert.Null(await this.svc.SetAdminAsync(this.sgh, this.arun.Id, true));
        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun@sgh.in");
        // sgh.in has two mailboxes: no Remove, the reason instead.
        string page = WebUtility.HtmlDecode(await admin.GetStringAsync("org/domains?domain=sgh.in"));
        Assert.Contains("data-domain-kept", page, StringComparison.Ordinal);
        Assert.Contains("This domain cannot be removed.", page, StringComparison.Ordinal);
        Assert.Contains("Still in use: 2 mailbox(es) and 0 other address(es) at this domain.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Remove this domain</summary>", page, StringComparison.Ordinal);
        // The domain this server answers to (anjal.localhost here): never removable, even when empty.
        Assert.Null(await this.svc.AddTenantDomainAsync(this.sgh, "localhost"));
        DomainUse own = await this.svc.DomainUseAsync(this.sgh, "localhost");
        Assert.True(own.ServerDomain);
        Assert.False(own.Removable);
        page = WebUtility.HtmlDecode(await admin.GetStringAsync("org/domains?domain=localhost"));
        Assert.Contains("This server answers to that domain, so it cannot be removed here.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Remove this domain</summary>", page, StringComparison.Ordinal);
        // An empty domain of the organisation's own still offers Remove.
        Assert.Null(await this.svc.AddTenantDomainAsync(this.sgh, "clinic.sgh-group.in"));
        Assert.Contains(">Remove this domain</summary>", await admin.GetStringAsync("org/domains?domain=clinic.sgh-group.in"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Def169_TheConsoleSideList_SaysTheScannerIsRunning_OnEveryPage()
    {
        using var clamd = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        clamd.Start();
        int port = ((IPEndPoint)clamd.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        Task answering = AnswerVersionAsync(clamd, stop.Token);
        string? operators = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        string? scanner = Environment.GetEnvironmentVariable("ANJAL_CLAMAV");
        string root2 = this.root + "-clamd";
        WebApplication withScanner = null!;
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@sgh.in");
            Environment.SetEnvironmentVariable("ANJAL_CLAMAV", "127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            withScanner = Program.CreateApp(Array.Empty<string>(), this.store, new MaildirStore(root2, "test"), "anjal.localhost", "http://127.0.0.1:0");
            await withScanner.StartAsync();
            string address = withScanner.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
            var op = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
            this.clients.Add(op);
            await SignInAsync(op, "arun@sgh.in");
            foreach (string at in new[] { "ops", "ops/health", "ops/limits", "ops/virus" })
            {
                string page = await op.GetStringAsync(at);
                Assert.DoesNotContain("Scanner not answering", page, StringComparison.Ordinal);
                Assert.Contains("ClamAV running, nothing held", page, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", operators);
            Environment.SetEnvironmentVariable("ANJAL_CLAMAV", scanner);
            if (withScanner is not null)
            {
                await withScanner.StopAsync();
                await withScanner.DisposeAsync();
            }
            await stop.CancelAsync();
            clamd.Stop();
            try
            {
                await answering;
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
            if (Directory.Exists(root2))
            {
                TestFolders.Delete(root2);
            }
        }
    }

    [Fact]
    public void NamedAddresses_EveryCopyInTheRepository_MatchesTheTable()
    {
        // Lessons of 25 Oct 2026 (the postmaster address): a decided address lives in one table at the top of
        // DEPLOY.md, and every setting line or DNS record in the repository that names it must agree.
        string root = RepoRoot();
        string deploy = File.ReadAllText(Path.Combine(root, "deploy", "DEPLOY.md"));
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match row in Regex.Matches(deploy, "^\\| `?(ANJAL_[A-Z_]+|DMARC `rua=`)[^|]*\\| `([^`|]+@[^`|]+)` \\|", RegexOptions.Multiline))
        {
            table[row.Groups[1].Value.StartsWith("DMARC", StringComparison.Ordinal) ? "rua" : row.Groups[1].Value] = row.Groups[2].Value;
        }
        Assert.Equal("ops@anjal.co.in", table["ANJAL_POSTMASTER"]);
        Assert.Equal("ops@anjal.co.in", table["ANJAL_ACME_EMAIL"]);
        Assert.Equal("ops@anjal.co.in", table["rua"]);
        var wrong = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "deploy")).Concat(new[] { Path.Combine(root, "README.md") }))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach ((string key, string address) in table)
                {
                    string pattern = key == "rua" ? "rua=mailto:([^;\\s|`]+)" : "#?" + key + "=([^\\s`|]+)";
                    foreach (Match m in Regex.Matches(lines[i], pattern))
                    {
                        // The lockdown's report address is recorded once, as the "Old value" that section 9 replaces.
                        if (!string.Equals(m.Groups[1].Value, address, StringComparison.OrdinalIgnoreCase) && m.Groups[1].Value != "arunshiva_b@yahoo.com")
                        {
                            wrong.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + m.Value);
                        }
                    }
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }
        return dir!.FullName;
    }

    // ---- helpers ----

    // The individuals' tenant (anjalmail.test) and Priya, an individual from before rc.24: no proved recovery address.
    private async Task<(TenantRow Tenant, MailboxRow Priya)> IndividualAsync()
    {
        TenantRow individuals = await this.store.UpsertTenantAsync(new TenantRow { Slug = "anjalmail", DisplayName = "Anjal" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = individuals.Id, Domain = "anjalmail.test" });
        await TenantKind.WriteAsync(this.store, individuals.Id, true);
        MailboxRow priya = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = individuals.Id,
            LocalPart = "priya.raman",
            Domain = "anjalmail.test",
            DisplayName = "Priya Raman",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        await this.svc.SaveProfileAsync(priya.Id, new PersonProfile { FirstName = "Priya", LastName = "Raman", Individual = true });
        return (individuals, priya);
    }

    // The link in the last recovery mail queued for an outside address.
    private (string M, string T) RecoveryLink(string to)
    {
        OutboundMessage mail = this.store.Outbound.Last(o => o.EnvelopeTo == to);
        Match link = Regex.Match(Encoding.UTF8.GetString(mail.RawBytes), "/recovery/confirm\\?m=([0-9a-f]{32})&t=([A-Za-z0-9_-]+)");
        Assert.True(link.Success);
        return (link.Groups[1].Value, link.Groups[2].Value);
    }

    /// <summary>Flags only the whole message (it has a Subject line), never a single file.</summary>
    private sealed class WholeMailScanner : Anjal.Mailbox.IVirusScanner
    {
        public Task<Anjal.Mailbox.ScanResult> ScanAsync(byte[] data, CancellationToken ct = default) =>
            Task.FromResult(Encoding.ASCII.GetString(data).Contains("Subject:", StringComparison.Ordinal)
                ? new Anjal.Mailbox.ScanResult(Anjal.Mailbox.ScanVerdict.Infected, "Heuristics.Phishing.Email.SpoofedDomain")
                : new Anjal.Mailbox.ScanResult(Anjal.Mailbox.ScanVerdict.Clean, string.Empty));

        public Task<string?> VersionAsync(CancellationToken ct = default) => Task.FromResult<string?>("ClamAV test");
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                using Stream s = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression).Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    // The "encrypted" flag (bit 0) set on every local and central header, as a password-protected ZIP has it.
    private static byte[] Encrypt(byte[] zip)
    {
        byte[] b = (byte[])zip.Clone();
        for (int i = 0; i + 10 < b.Length; i++)
        {
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 3 && b[i + 3] == 4)
            {
                b[i + 6] |= 1;
            }
            if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 1 && b[i + 3] == 2)
            {
                b[i + 8] |= 1;
            }
        }
        return b;
    }

    private sealed record ParsedText(string Text);

    private List<string> Subjects() => this.Mail().Select(m => m.Subject).ToList();

    private List<MessageRow> Mail() => this.store.MailboxMessages.Where(m => m.MailboxId == this.arun.Id).ToList();

    private async Task<string> FolderNameAsync(MessageRow m) =>
        (await this.store.ListFoldersAsync(this.arun.Id)).Single(f => f.Id == this.store.MailboxMessages.Single(x => x.Id == m.Id).FolderId).Name;

    private async Task<ParsedText> TextOfAsync(MessageRow m)
    {
        MessageView view = (await this.svc.OpenAsync(this.arun.Id, m.Id, allowRemoteImages: false))!;
        return new ParsedText(WebUtility.HtmlDecode(Regex.Replace(view.BodyHtml, "<[^>]+>", " ")));
    }

    private static async Task AsOperatorAsync(Func<Task> body)
    {
        string? before = Environment.GetEnvironmentVariable("ANJAL_OPERATORS");
        try
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", "arun@sgh.in");
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANJAL_OPERATORS", before);
        }
    }

    /// <summary>A stand-in clamd: every connection is answered with a version line.</summary>
    private static async Task AnswerVersionAsync(System.Net.Sockets.TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using System.Net.Sockets.TcpClient c = await listener.AcceptTcpClientAsync(ct);
            using System.Net.Sockets.NetworkStream net = c.GetStream();
            byte[] buffer = new byte[256];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await net.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0)
                {
                    break;
                }
                read += n;
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                {
                    break;
                }
            }
            await net.WriteAsync(Encoding.ASCII.GetBytes("ClamAV 1.4.3/27800/Sat Oct 24 08:00:00 2026\0"), ct);
        }
    }

    /// <summary>
    /// A conversation that splits: Ramesh writes A; Priya answers A (B, with a file); Anil also answers A (C,
    /// naming A only in References); then Ramesh writes D with the same subject and no reply headers.
    /// </summary>
    private async Task<(MessageRow A, MessageRow B, MessageRow C, MessageRow D)> SplitConversationAsync()
    {
        MessageRow a = await this.DeliverAsync("From: Ramesh <ramesh@lab.test>\r\nTo: arun@sgh.in\r\nMessage-ID: <a1@lab.test>\r\nSubject: Results for Friday\r\n\r\nThe results are in.\r\n", "ramesh@lab.test");
        await Task.Delay(15);
        MessageRow b = await this.DeliverAsync(
            "From: Priya <priya@lab.test>\r\nTo: arun@sgh.in\r\nMessage-ID: <b2@lab.test>\r\nIn-Reply-To: <a1@lab.test>\r\nSubject: Re: Results for Friday\r\n" +
            "MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"zz\"\r\n\r\n--zz\r\nContent-Type: text/plain\r\n\r\nSee the report.\r\n" +
            "--zz\r\nContent-Type: application/pdf; name=\"report.pdf\"\r\nContent-Disposition: attachment; filename=\"report.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0xLjQK\r\n--zz--\r\n",
            "priya@lab.test");
        await Task.Delay(15);
        MessageRow c = await this.DeliverAsync("From: Anil <anil@lab.test>\r\nTo: arun@sgh.in\r\nCc: Ramesh <ramesh@lab.test>\r\nMessage-ID: <c3@lab.test>\r\nReferences: <a1@lab.test>\r\nSubject: Re: Results for Friday\r\n\r\nI disagree.\r\n", "anil@lab.test");
        await Task.Delay(15);
        MessageRow d = await this.DeliverAsync("From: Ramesh <ramesh@lab.test>\r\nTo: arun@sgh.in\r\nMessage-ID: <d4@lab.test>\r\nSubject: Re: Results for Friday\r\n\r\nOne more thing.\r\n", "ramesh@lab.test");
        return (a, b, c, d);
    }

    private async Task<MailboxRow> Person(string local, string name) =>
        await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private async Task<MessageRow> DeliverAsync(string raw, string envelopeFrom, string[]? to = null)
    {
        DeliveryResult result = await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = to ?? ArunOnly,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, FolderRow.Inbox);
        return (await this.store.ListMessagesAsync(this.arun.Id, inbox!.Id, 1, 0))[0];
    }

    private HttpClient Client()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(client);
        return client;
    }

    private static async Task SignInAsync(HttpClient client, string address, string password = "correct horse battery")
    {
        string html = await client.GetStringAsync("sign-in");
        HttpResponseMessage res = await Post(client, "auth/login", Token(html), ("address", address), ("password", password));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private static string Token(string html) => TokenRegex.Match(html).Groups[1].Value;

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string token, params (string Name, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(path, content);
    }
}
