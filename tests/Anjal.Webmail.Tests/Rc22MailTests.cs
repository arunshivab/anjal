using System.Text;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// v1.0.0-rc.22, the mail features (owner, 18 Oct 2026; mockups 12-23): forwarding by rule as a
/// copy, the away reply once per sender, snooze and wake, blocking that discards silently, extra
/// and group addresses, the virus verdicts, the patient-information warning, Inbox sections, and
/// the sign-in page's branding fields.
/// </summary>
public sealed class Rc22MailTests : IDisposable
{
    private static readonly string[] ArunOnly = { "arun@sgh.in" };
    private static readonly string[] HelpOnly = { "help@sgh.in" };
    private static readonly string[] NoFiles = Array.Empty<string>();
    private static readonly string[] ReportFile = { "discharge-summary.pdf" };
    private static readonly string[] Payroll = { "payroll-oct.xlsx" };
    private static readonly string[] Doctors = { "doctors@sgh.in" };
    private static readonly string[] UnreadFlagged = { "Unread", "Flagged" };
    private static readonly string[] HelpAlias = { "help@sgh.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc22m-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private readonly TenantRow sgh;
    private readonly MailboxRow arun;
    private readonly MailboxRow meera;

    public Rc22MailTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.sgh = this.store.UpsertTenantAsync(new TenantRow { Slug = "sgh", DisplayName = "Sri Ganesh Hospital", PostmasterMailbox = "arun@sgh.in" }).GetAwaiter().GetResult();
        this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.sgh.Id, Domain = "sgh.in" }).GetAwaiter().GetResult();
        this.arun = this.Person("arun", "Arun Shiva B");
        this.meera = this.Person("meera", "Meera Iyer");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            TestFolders.Delete(this.root);
        }
    }

    [Fact]
    public async Task ARule_ForwardsACopy_AndKeepsTheOriginal_AndTheCopyIsNeverForwardedMoreThanThreeTimes()
    {
        // Owner: forwarding is by rule only, always keeps a copy; organisations only when the administrator allows outside.
        Assert.NotNull(await this.svc.SaveRuleAsync(this.arun.Id, new MailRule { Name = "Lab", Conditions = { new RuleCondition { Field = "from", Value = "lab@" } }, ForwardTo = "arun.home@gmail.test" }));
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { ForwardingOutside = true, Nicknames = false, AwayOutside = true });
        Assert.Null(await this.svc.SaveRuleAsync(this.arun.Id, new MailRule { Name = "Lab", Conditions = { new RuleCondition { Field = "from", Value = "lab@" } }, ForwardTo = "arun.home@gmail.test" }));

        await this.DeliverAsync("From: Lab <lab@path.test>\r\nSubject: Results\r\n\r\nHb 12.1\r\n", ArunOnly);
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, "INBOX");
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, inbox!.Id));
        IReadOnlyList<OutboundMessage> sent = await this.store.ListOutboundForSenderAsync("arun@sgh.in");
        OutboundMessage copy = Assert.Single(sent);
        Assert.Equal("arun.home@gmail.test", copy.EnvelopeTo);
        string raw = Encoding.UTF8.GetString(copy.RawBytes);
        Assert.Contains("Resent-From: <arun@sgh.in>", raw, StringComparison.Ordinal);
        Assert.Contains(MailboxSink.ForwardedHeader + ": arun@sgh.in", raw, StringComparison.Ordinal);

        // A copy that has already gone round three times stops here.
        string hopped = string.Concat(Enumerable.Repeat(MailboxSink.ForwardedHeader + ": x@y.test\r\n", 3)) + "From: Lab <lab@path.test>\r\nSubject: Loop\r\n\r\nx\r\n";
        await this.DeliverAsync(hopped, ArunOnly);
        Assert.Single(await this.store.ListOutboundForSenderAsync("arun@sgh.in"));
    }

    [Fact]
    public async Task TheAwayReply_GoesOncePerSender_WithAnEmptyEnvelope_AndNeverToAutomaticMail()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Null(await this.svc.SaveAwayAsync(this.arun.Id, new AwayReply { On = true, From = now.AddHours(-1), Until = now.AddDays(2), Subject = "Away until Friday", Text = "Back on Friday.", ReplyTo = "everyone" }));
        Assert.NotNull(await this.svc.AwayActiveAsync(this.arun.Id));

        await this.DeliverAsync("From: Priya <priya@gmail.test>\r\nSubject: Hello\r\n\r\nHi\r\n", ArunOnly, "priya@gmail.test");
        await this.DeliverAsync("From: Priya <priya@gmail.test>\r\nSubject: Again\r\n\r\nHi again\r\n", ArunOnly, "priya@gmail.test");
        await this.DeliverAsync("From: List <news@list.test>\r\nSubject: Weekly\r\nPrecedence: bulk\r\nList-Id: <news.list.test>\r\n\r\n...\r\n", ArunOnly, "bounce@list.test");

        IReadOnlyList<OutboundMessage> replies = await this.store.ListOutboundForSenderAsync(string.Empty);
        OutboundMessage reply = Assert.Single(replies);
        Assert.Equal("priya@gmail.test", reply.EnvelopeTo);
        string raw = Encoding.UTF8.GetString(reply.RawBytes);
        Assert.Contains("Auto-Submitted: auto-replied", raw, StringComparison.Ordinal);
        Assert.Contains("Subject: Away until Friday", raw, StringComparison.Ordinal);

        // Until is enforced, and Turn off ends it.
        Assert.NotNull(await this.svc.SaveAwayAsync(this.arun.Id, new AwayReply { On = true, From = now, Until = now.AddDays(-1), Subject = "x", Text = "y" }));
        await this.svc.SaveAwayAsync(this.arun.Id, new AwayReply { On = false });
        Assert.Null(await this.svc.AwayActiveAsync(this.arun.Id));
    }

    [Fact]
    public async Task Snooze_MovesTheMessageOut_AndWakeBringsItBackOnTop_MarkedBack()
    {
        MessageRow first = await this.DeliverAsync("From: a@x.test\r\nSubject: First\r\n\r\n1\r\n", ArunOnly);
        await Task.Delay(5);
        MessageRow second = await this.DeliverAsync("From: b@x.test\r\nSubject: Second\r\n\r\n2\r\n", ArunOnly);
        DateTimeOffset? tomorrow = MailboxService.SnoozeTime(ZonedClock.Default, DateTimeOffset.UtcNow, "tomorrow");
        Assert.True(tomorrow > DateTimeOffset.UtcNow);
        Assert.Null(await this.svc.SnoozeAsync(this.arun.Id, first.Id, DateTimeOffset.UtcNow.AddMinutes(2)));

        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, "INBOX");
        FolderRow? snoozed = await this.svc.GetFolderAsync(this.arun.Id, MailboxService.SnoozedFolder);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, inbox!.Id));
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, snoozed!.Id));
        Assert.Single((await this.svc.SnoozeOfAsync(this.arun.Id)).Sleeping);
        Assert.NotNull(await this.svc.SnoozeAsync(this.arun.Id, second.Id, DateTimeOffset.UtcNow.AddYears(2)));

        int woken = await this.svc.WakeSnoozedAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(1, woken);
        IReadOnlyList<MessageRow> rows = await this.store.ListMessagesAsync(this.arun.Id, inbox.Id, 10, 0);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == first.Id && !r.Seen);
        Assert.Contains(first.Id, await this.svc.WokenAsync(this.arun.Id));
        // The folder shows woken mail on top, under the same id (links and audit lines still point at it).
        IReadOnlyList<MessageRow> onTop = await this.svc.WokenRowsAsync(this.arun.Id, inbox.Id);
        Assert.Equal(first.Id, Assert.Single(onTop).Id);
        Assert.Equal(0, await this.store.CountMessagesAsync(this.arun.Id, snoozed.Id));
    }

    [Fact]
    public async Task ABlockedSender_IsDiscardedSilently_AndCounted()
    {
        MessageRow first = await this.DeliverAsync("From: Spam <spam@junk.test>\r\nSubject: Buy\r\n\r\nx\r\n", ArunOnly, "spam@junk.test");
        Assert.Equal("spam@junk.test", await this.svc.BlockSenderAsync(this.arun.Id, first.Id));
        DeliveryResult result = await this.DeliverResultAsync("From: Spam <spam@junk.test>\r\nSubject: Buy again\r\n\r\nx\r\n", ArunOnly, "spam@junk.test");
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, null));
        IReadOnlyDictionary<string, int> counts = await this.svc.DiscardCountsAsync(this.arun.Id);
        Assert.Equal(1, counts["spam@junk.test"]);
        Assert.Contains(await this.svc.ListSenderRulesAsync(this.arun.Id), r => r.Pattern == "spam@junk.test" && r.Action == SenderRuleAction.Discard);
    }

    [Fact]
    public async Task AnAlias_DeliversToItsPerson_AndAGroup_ReachesEveryMember_OrgOnlyWhenSaid()
    {
        Assert.Null(await this.svc.AddAliasAsync(this.sgh, "help", "sgh.in", this.arun.Id, canSend: true));
        Assert.NotNull(await this.svc.AddAliasAsync(this.sgh, "meera", "sgh.in", this.arun.Id, canSend: false));
        Assert.Null(await this.svc.SaveGroupAsync(this.sgh, "doctors", "sgh.in", new List<Guid> { this.arun.Id, this.meera.Id }, orgOnly: true));
        Assert.Equal(HelpAlias, await this.svc.SendAsAddressesAsync(this.arun.Id));

        await this.DeliverAsync("From: p@x.test\r\nSubject: To help\r\n\r\nx\r\n", HelpOnly, "p@x.test");
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, null));

        DeliveryResult outside = await this.DeliverResultAsync("From: p@x.test\r\nSubject: To doctors\r\n\r\nx\r\n", Doctors, "p@x.test");
        Assert.NotEqual(DeliveryOutcome.Accepted, outside.Outcome);
        DeliveryResult inside = await this.DeliverResultAsync("From: meera@sgh.in\r\nSubject: To doctors\r\n\r\nx\r\n", Doctors, "meera@sgh.in");
        Assert.Equal(DeliveryOutcome.Accepted, inside.Outcome);
        Assert.Equal(2, await this.store.CountMessagesAsync(this.arun.Id, null));
        Assert.Equal(1, await this.store.CountMessagesAsync(this.meera.Id, null));
        Assert.True(await this.svc.RemoveGroupAsync(this.sgh, "doctors@sgh.in"));
        Assert.True(await this.svc.RemoveAliasAsync(this.sgh, "help@sgh.in"));
    }

    [Fact]
    public async Task Files_AreJudged_ProgramsRefused_InfectedRemoved_HeldWhenTheScannerIsDown()
    {
        const string head = "From: a@x.test\r\nSubject: Files\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n--b1\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n";
        string program = head + "--b1\r\nContent-Type: application/octet-stream; name=\"setup.exe\"\r\nContent-Disposition: attachment; filename=\"setup.exe\"\r\n\r\nMZ\r\n--b1--\r\n";
        DeliveryResult refused = await this.DeliverResultAsync(program, ArunOnly, "a@x.test", new FakeScanner(ScanVerdict.Clean));
        Assert.Equal(DeliveryOutcome.PermanentFailure, refused.Outcome);
        Assert.Contains("Programs and scripts", refused.ReplyText, StringComparison.Ordinal);

        string report = head + "--b1\r\nContent-Type: application/pdf; name=\"report.pdf\"\r\nContent-Disposition: attachment; filename=\"report.pdf\"\r\n\r\n%PDF\r\n--b1--\r\n";
        Assert.Equal(DeliveryOutcome.Accepted, (await this.DeliverResultAsync(report, ArunOnly, "a@x.test", new FakeScanner(ScanVerdict.Infected, "Eicar-Test-Signature"))).Outcome);
        MessageRow row = this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
        Assert.Contains("attachment removed", row.Subject, StringComparison.Ordinal);
        Assert.False(row.HasAttachments);

        // rc.25 (D-242): with the scanner down, mail with files is answered "try later"; nothing is kept.
        DeliveryResult later = await this.DeliverResultAsync(report, ArunOnly, "a@x.test", new FakeScanner(ScanVerdict.Unavailable));
        Assert.Equal(DeliveryOutcome.TransientFailure, later.Outcome);
        Assert.Contains("try again later", later.ReplyText, StringComparison.Ordinal);
        Assert.Null(await this.svc.GetFolderAsync(this.arun.Id, AttachmentPolicy.QuarantineFolder));
        // The rc.22 quarantine (still there for mail held before rc.25) when the sink is set to hold.
        Assert.Equal(DeliveryOutcome.Accepted, (await this.DeliverResultAsync(report, ArunOnly, "a@x.test", new FakeScanner(ScanVerdict.Unavailable), holdNothing: false)).Outcome);
        FolderRow? held = await this.svc.GetFolderAsync(this.arun.Id, AttachmentPolicy.QuarantineFolder);
        Assert.NotNull(held);
        Assert.Equal(1, await this.store.CountMessagesAsync(this.arun.Id, held!.Id));
        IReadOnlyList<HeldMessage> heldList = await this.svc.HeldMessagesAsync();
        Assert.Single(heldList);
        Assert.Contains("report.pdf", heldList[0].Files);

        // Scanning again, with the scanner back, delivers it as on arrival and empties the quarantine.
        MailboxService back = new(this.store, this.store, this.maildir, "anjal.localhost") { Scanner = new FakeScanner(ScanVerdict.Clean) };
        Assert.Equal(1, await back.RescanHeldAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, await this.store.CountMessagesAsync(this.arun.Id, held.Id));
        FolderRow? inbox = await this.svc.GetFolderAsync(this.arun.Id, "INBOX");
        Assert.Equal(2, await this.store.CountMessagesAsync(this.arun.Id, inbox!.Id));
    }

    [Fact]
    public async Task ThePatientWarning_NamesWhatItSaw_OnlyForOutsideAddresses()
    {
        Assert.Empty(await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "meera@sgh.in", Subject = "Discharge summary MRN 2026-10443", Body = "Diagnosis attached." }));
        IReadOnlyList<string> reasons = await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "dr.rao@gmail.test", Subject = "Discharge summary MRN 2026-10443", Body = "Diagnosis attached." });
        Assert.Contains(reasons, r => r.Contains("record number", StringComparison.Ordinal));
        Assert.Empty(await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "dr.rao@gmail.test", Subject = "Lunch", Body = "Friday?" }));
        Assert.Empty(PatientInformation.Signs("Lunch", "Friday?", NoFiles));
        Assert.Contains(PatientInformation.Signs("Hi", "see file", ReportFile), r => r.Contains("discharge-summary.pdf", StringComparison.Ordinal));
        // The kind comes first, then the outside address.
        Assert.Equal("patient", reasons[0]);
        Assert.Equal("dr.rao@gmail.test", reasons[1]);
        // Whole words only: "forward" is not "ward", "showcase" is not "case".
        Assert.Empty(PatientInformation.Signs("Please forward the showcase deck", "toward Friday", NoFiles));
    }

    [Fact]
    public async Task PersonalInformation_IsNamed_AndTheOrganisationCanAskAboutEveryFileGoingOutside()
    {
        // Owner, 19 Oct 2026: Aadhaar, PAN, phone, bank account, pay and passwords are personal information.
        IReadOnlyList<string> aadhaar = PatientInformation.PersonalSigns("Details", "Aadhaar 2345 6789 0123, PAN ABCDE1234F, call 98400 12345", NoFiles);
        Assert.Contains("an Aadhaar number", aadhaar);
        Assert.Contains("a PAN", aadhaar);
        Assert.Contains("a phone number", aadhaar);
        Assert.Contains(PatientInformation.PersonalSigns("Salary revision", "Your payslip is attached.", NoFiles), r => r.Contains("\"salary\"", StringComparison.Ordinal));
        Assert.Contains(PatientInformation.PersonalSigns("Hi", "see file", Payroll), r => r.Contains("payroll-oct.xlsx", StringComparison.Ordinal));
        Assert.Empty(PatientInformation.PersonalSigns("Lunch on Friday?", "Order no 2026-10-19 placed at 12:30, bill 1,250.", NoFiles));

        IReadOnlyList<string> personal = await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "hr.vendor@gmail.test", Subject = "Bank details", Body = "A/c no 123456789012, IFSC HDFC0001234" });
        Assert.Equal("personal", personal[0]);
        IReadOnlyList<string> both = await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "hr.vendor@gmail.test", Subject = "Patient's Aadhaar", Body = "2345 6789 0123" });
        Assert.Equal("both", both[0]);

        // Files outside: nothing until the organisation switches it on, then every file is asked about.
        var withFile = new ComposeRequest { To = "vendor@gmail.test", Subject = "Quotation", Body = "Attached." };
        withFile.Attachments.Add(("quote.pdf", "application/pdf", new byte[] { 1 }));
        Assert.Empty(await this.svc.PatientWarningAsync(this.arun.Id, withFile));
        await this.svc.SaveMailPolicyAsync(this.sgh.Id, new OrgMailPolicy { AskBeforeFilesOutside = true });
        IReadOnlyList<string> files = await this.svc.PatientWarningAsync(this.arun.Id, withFile);
        Assert.Equal("files", files[0]);
        Assert.Equal("vendor@gmail.test", files[1]);
        Assert.Equal("quote.pdf", files[2]);
        // Inside the organisation, and mail without files, stay unasked.
        Assert.Empty(await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "meera@sgh.in", Subject = "Quotation", Body = "Attached." }));
        Assert.Empty(await this.svc.PatientWarningAsync(this.arun.Id, new ComposeRequest { To = "vendor@gmail.test", Subject = "Quotation", Body = "No file." }));
    }

    [Fact]
    public async Task InboxSections_AreOffByDefault_StartWithUnreadAndFlagged_AndMatchTheirSearches()
    {
        Assert.Empty(await this.svc.SectionViewsAsync(this.arun.Id));
        await this.svc.SetSectionsOnAsync(this.arun.Id, true);
        InboxSections s = await this.svc.SectionsOfAsync(this.arun.Id);
        Assert.True(s.On);
        Assert.Equal(UnreadFlagged, s.List.Select(x => x.Name));
        Assert.Null(await this.svc.SaveSectionAsync(this.arun.Id, -1, "From the lab", "from:lab@ has:attachment", 3));
        Assert.NotNull(await this.svc.SaveSectionAsync(this.arun.Id, -1, "", "x", 3));

        MessageRow lab = await this.DeliverAsync("From: lab@path.test\r\nSubject: Results\r\n\r\nx\r\n", ArunOnly, "lab@path.test");
        await this.DeliverAsync("From: p@x.test\r\nSubject: Other\r\n\r\nx\r\n", ArunOnly, "p@x.test");
        await this.store.SetMessageFlagsAsync(lab.Id, seen: true, flagged: true, answered: false, null);
        IReadOnlyList<SectionView> views = await this.svc.SectionViewsAsync(this.arun.Id);
        Assert.Equal(3, views.Count);
        Assert.Equal(1, views[0].Total);
        Assert.Equal("Other", views[0].Rows[0].Subject);
        Assert.Equal("Results", views[1].Rows[0].Subject);
        Assert.Equal(0, views[2].Total);
        await this.svc.RemoveSectionAsync(this.arun.Id, 2);
        Assert.Equal(2, (await this.svc.SectionsOfAsync(this.arun.Id)).List.Count);
        Assert.True(MailboxService.SectionMatches("newer:7d results", lab, DateTimeOffset.UtcNow));
        Assert.False(MailboxService.SectionMatches("is:unread", lab, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task TheSignInPage_CarriesTheOrganisationsDesignColourWelcomeAndHelpLine()
    {
        await this.svc.WriteTenantDocumentAsync(this.sgh.Id, MailboxService.BrandingKind, new Branding { Design = 7, Colour = "#8A1538", Welcome = "Caring since 1986.", HelpLine = "IT desk: 020 2611 0100" });
        OrganisationIdentity? org = await this.svc.OrganisationForHostAsync("mail.sgh.in");
        Assert.NotNull(org);
        Assert.Equal(7, org!.Design);
        Assert.Equal("#8a1538", org.Colour);
        Assert.Equal("Caring since 1986.", org.Welcome);
        Assert.Equal("IT desk: 020 2611 0100", org.HelpLine);
        Assert.Equal(string.Empty, Branding.CleanColour("red"));
        Assert.Equal(1, SignInDesigns.Clean(11));
        Assert.Equal(10, SignInDesigns.Names.Count);
    }

    private MailboxRow Person(string local, string name) =>
        this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.sgh.Id,
            LocalPart = local,
            Domain = "sgh.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        }).GetAwaiter().GetResult();

    private async Task<MessageRow> DeliverAsync(string raw, string[] to, string envelopeFrom = "s@x.test")
    {
        DeliveryResult result = await this.DeliverResultAsync(raw, to, envelopeFrom);
        Assert.Equal(DeliveryOutcome.Accepted, result.Outcome);
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private async Task<DeliveryResult> DeliverResultAsync(string raw, string[] to, string envelopeFrom, IVirusScanner? scanner = null, bool holdNothing = true) =>
        await new MailboxSink(this.store, this.maildir) { Scanner = scanner, HoldNothing = holdNothing }.DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = to,
            RawBytes = Encoding.UTF8.GetBytes(raw),
        });

    private sealed class FakeScanner : IVirusScanner
    {
        private readonly ScanVerdict verdict;
        private readonly string detail;

        public FakeScanner(ScanVerdict verdict, string detail = "")
        {
            this.verdict = verdict;
            this.detail = detail;
        }

        public Task<ScanResult> ScanAsync(byte[] bytes, CancellationToken ct = default) => Task.FromResult(new ScanResult(this.verdict, this.detail));

        public Task<string?> VersionAsync(CancellationToken ct = default) => Task.FromResult<string?>(this.verdict == ScanVerdict.Unavailable ? null : "ClamAV 1.4.1/test");
    }
}
