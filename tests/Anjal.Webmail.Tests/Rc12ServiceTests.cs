using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>rc.12 services: contacts, templates, send later and undo, Send one each, the Outbox, conversations, folders and rules, search filters.</summary>
public sealed class Rc12ServiceTests : System.IDisposable
{
    private static readonly string[] Arun = { "arun@anjal.co.in" };
    private static readonly string[] ThreePeople = { "joseph@partnerlab.example", "meera@anjal.co.in", "nobody@x.example" };
    private static readonly string[] FestivalBlanks = { "Festival", "Name", "Festival line", "Your name" };

    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-rc12-svc-" + System.Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly MaildirStore maildir;
    private readonly MailboxService svc;
    private MailboxRow mailbox = new();

    public Rc12ServiceTests()
    {
        this.maildir = new MaildirStore(this.root, "test");
        this.svc = new MailboxService(this.store, this.store, this.maildir, "anjal.localhost");
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(this.root))
        {
            System.IO.Directory.Delete(this.root, recursive: true);
        }
    }

    private async Task SeedAsync()
    {
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "imagiQa" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.mailbox = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
    }

    private async Task<MessageRow> DeliverAsync(string raw)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = Arun,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[^1];
    }

    private async Task<string> FolderOfAsync(Guid id) =>
        (await this.store.ListFoldersAsync(this.mailbox.Id)).Single(f => f.Id == this.store.MailboxMessages.Single(m => m.Id == id).FolderId).Name;

    // ---------------- Contacts ----------------

    [Fact]
    public async Task Contacts_SaveRefusesADuplicate_ImportAddsOnlyWhatIsMissing_AndExportReadsBack()
    {
        await this.SeedAsync();
        Assert.Null(await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { FirstName = "Joseph", Address = "joseph@partnerlab.example" }));
        Assert.NotNull(await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { Address = "JOSEPH@partnerlab.example" }));
        Assert.NotNull(await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { Address = "not an address" }));

        ImportResult r = await this.svc.ImportContactsAsync(this.mailbox.Id,
            "First name,Last name,Email,Company,Phone\r\nJoseph,Mathew,joseph@partnerlab.example,Partner Lab,\r\nMeera,Iyer,meera@anjal.co.in,,99\r\n,,broken,,\r\n");
        Assert.Equal(new ImportResult(1, 1, 1), r);
        Contact joseph = (await this.svc.FindContactAsync(this.mailbox.Id, "joseph@partnerlab.example"))!;
        Assert.Equal("Joseph", joseph.FirstName);       // kept as typed
        Assert.Equal("Mathew", joseph.LastName);        // added: it was missing
        Assert.Equal("Partner Lab", joseph.Organisation);

        string vcf = MailboxService.ContactsToVCard(await this.svc.ListContactsAsync(this.mailbox.Id));
        await this.svc.DeleteContactAsync(this.mailbox.Id, joseph.Id);
        ImportResult back = await this.svc.ImportContactsAsync(this.mailbox.Id, vcf);
        Assert.Equal(1, back.Added);
        Assert.Equal("Mathew", (await this.svc.FindContactAsync(this.mailbox.Id, "joseph@partnerlab.example"))!.LastName);

        string csv = MailboxService.ContactsToCsv(new[] { new Contact { FirstName = "=SUM(A1)", Address = "x@y.example", Notes = "a, \"b\"" } });
        Assert.Contains("'=SUM(A1)", csv, StringComparison.Ordinal);   // a spreadsheet will not run it
        Assert.Contains("\"a, \"\"b\"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_ComeFromContactsFirst_ThenColleagues()
    {
        await this.SeedAsync();
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = this.mailbox.TenantId, LocalPart = "meera", Domain = "anjal.co.in", DisplayName = "Meera Iyer" });
        await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { FirstName = "Joseph", LastName = "Mathew", Address = "joseph@partnerlab.example" });
        IReadOnlyDictionary<string, string> names = await this.svc.NamesForAsync(this.mailbox.Id, ThreePeople);
        Assert.Equal("Joseph Mathew", names["joseph@partnerlab.example"]);
        Assert.Equal("Meera Iyer", names["meera@anjal.co.in"]);
        Assert.False(names.ContainsKey("nobody@x.example"));
        IReadOnlyList<ContactSuggestion> suggested = await this.svc.SuggestContactsAsync(this.mailbox.Id, "jos");
        Assert.Equal("joseph@partnerlab.example", suggested[0].Address);
        Assert.Equal("Joseph Mathew", suggested[0].Name);
    }

    // ---------------- Templates ----------------

    [Fact]
    public void TheStarterSet_Is62Templates_In9Groups_EachWithAtLeastFive()
    {
        Assert.Equal(62, TemplateCatalogue.All.Count);
        Assert.Equal(9, TemplateCatalogue.Groups.Count);
        foreach (string g in TemplateCatalogue.Groups)
        {
            Assert.True(TemplateCatalogue.All.Count(t => t.Group == g) >= 5, g);
        }
        Assert.Equal(TemplateCatalogue.All.Count, TemplateCatalogue.All.Select(t => t.Key).Distinct().Count());
        Assert.Equal(FestivalBlanks, TemplateCatalogue.BlanksIn(TemplateCatalogue.Find("greet/festival")!.Subject + "\n" + TemplateCatalogue.Find("greet/festival")!.Body));
    }

    [Fact]
    public async Task UsingATemplate_FillsWhatAnjalKnows_KeepsRecipients_AndLeavesTheRestPlain()
    {
        await this.SeedAsync();
        await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { FirstName = "Meera", Address = "meera@anjal.co.in" });
        Guid draft = (await this.svc.SaveDraftAsync(this.mailbox.Id, new ComposeRequest { To = "meera@anjal.co.in", Subject = "x" }))!.Value;
        MailTemplate festival = TemplateCatalogue.Find("greet/festival")!;
        IReadOnlyDictionary<string, string> known = await this.svc.KnownBlanksAsync(this.mailbox.Id, "meera@anjal.co.in", "Onam");
        Assert.Equal("Meera", known["Name"]);
        Assert.Equal("Arun Shiva", known["Your name"]);
        Guid used = (await this.svc.ApplyTemplateAsync(this.mailbox.Id, draft, festival, known))!.Value;
        ComposeRequest after = (await this.svc.LoadDraftAsync(this.mailbox.Id, used))!;
        Assert.Equal("meera@anjal.co.in", after.To);
        Assert.Equal("Happy Onam, Meera", after.Subject);
        Assert.Contains("May Onam bring you", after.Body, StringComparison.Ordinal);
        Assert.Contains("anjal-mailbg", after.BodyHtml, StringComparison.Ordinal);   // the suggested background travels

        MailTemplate leave = TemplateCatalogue.Find("hr/leave-manager")!;
        Assert.Contains("{From date}", TemplateCatalogue.Fill(leave.Subject, new Dictionary<string, string>()), StringComparison.Ordinal);

        Assert.Null(await this.svc.SaveOwnTemplateAsync(this.mailbox.Id, "Weekly note", "Week {Week}", "Hello {Name}"));
        MailTemplate mine = Assert.Single(await this.svc.ListOwnTemplatesAsync(this.mailbox.Id));
        Assert.Equal(MailboxService.OwnTemplatesGroup, mine.Group);
        Assert.Same(festival, await this.svc.FindTemplateAsync(this.mailbox.Id, festival.Key));
    }

    // ---------------- Send later, undo ----------------

    [Fact]
    public async Task Held_WaitsInScheduled_UndoPutsItInDrafts_AndItsTimeSendsIt()
    {
        await this.SeedAsync();
        DateTimeOffset at = DateTimeOffset.UtcNow.AddHours(5);
        (string? error, Guid? held) = await this.svc.HoldAsync(this.mailbox.Id, new ComposeRequest { To = "meera@anjal.co.in", Subject = "Later", Body = "Hi" }, at);
        Assert.Null(error);
        Assert.Equal(MailboxService.ScheduledFolder, await this.FolderOfAsync(held!.Value));
        MessageRow row = this.store.MailboxMessages.Single(m => m.Id == held);
        Assert.Equal(at.ToUnixTimeSeconds(), MailboxService.SendTimeOf(row)!.Value.ToUnixTimeSeconds());

        // Not yet due: nothing is sent.
        Assert.Equal(0, await this.svc.SendDueAsync(DateTimeOffset.UtcNow, rescan: true));
        Assert.Equal(MailboxService.ScheduledFolder, await this.FolderOfAsync(held.Value));

        Guid draft = (await this.svc.UnholdAsync(this.mailbox.Id, held.Value))!.Value;
        Assert.Equal("Drafts", await this.FolderOfAsync(draft));
        Assert.Null(await this.svc.UnholdAsync(this.mailbox.Id, held.Value));   // not waiting any more

        (string? e2, Guid? soon) = await this.svc.HoldAsync(this.mailbox.Id, new ComposeRequest { To = "meera@anjal.co.in", Subject = "Soon", Body = "Hi" }, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Null(e2);
        Assert.Equal(1, await this.svc.SendDueAsync(DateTimeOffset.UtcNow, rescan: false));
        Assert.DoesNotContain(this.store.MailboxMessages, m => m.Id == soon);
        Assert.Contains(this.store.MailboxMessages, m => m.Subject == "Soon" && this.store.ListFoldersAsync(this.mailbox.Id).Result.Single(f => f.Id == m.FolderId).Name == "Sent");
    }

    [Fact]
    public async Task Held_IsCheckedFirst_SoItNeverFailsForAReasonThatCouldBeToldAtOnce()
    {
        await this.SeedAsync();
        (string? error, Guid? held) = await this.svc.HoldAsync(this.mailbox.Id, new ComposeRequest { To = "not an address", Subject = "x" }, DateTimeOffset.UtcNow.AddHours(1));
        Assert.NotNull(error);
        Assert.Null(held);
    }

    [Fact]
    public async Task SendLaterChoices_AreNineInTheMorning_InThePersonsZone()
    {
        ZonedClock ist = ZonedClock.For("Asia/Kolkata", null);
        // Tuesday 6 Oct 2026, 13:00 IST.
        DateTimeOffset now = new(2026, 10, 6, 7, 30, 0, TimeSpan.Zero);
        (DateTimeOffset tomorrow, DateTimeOffset monday) = MailboxService.SendLaterChoices(ist, now);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(5.5)), tomorrow);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(5.5)), monday);
    }

    // ---------------- Send one each ----------------

    [Fact]
    public async Task SendOneEach_MakesOneCopyPerPerson_WithTheirBlanks_AndColleagueWhenThereIsNoName()
    {
        await this.SeedAsync();
        await this.svc.SaveContactAsync(this.mailbox.Id, new Contact { FirstName = "Ravi", Organisation = "Kulkarni Instruments", Address = "ravi@supplier.example" });
        var request = new ComposeRequest
        {
            To = "ravi@supplier.example, info@other.example",
            Subject = "Happy Diwali, {First name}",
            Body = "Dear {First name}, everyone at {Organisation}",
        };
        (string? error, int count) = await this.svc.SendOneEachAsync(this.mailbox.Id, request, null, DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(error);
        Assert.Equal(2, count);
        var held = this.store.MailboxMessages.Where(m => m.Subject.StartsWith("Happy Diwali", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, held.Count);
        Assert.Contains(held, m => m.Subject == "Happy Diwali, Ravi" && m.ToHeader == "ravi@supplier.example");
        Assert.Contains(held, m => m.Subject == "Happy Diwali, colleague" && m.ToHeader == "info@other.example");
        Assert.All(held, m => Assert.DoesNotContain(",", m.ToHeader, StringComparison.Ordinal));   // no one sees another's address
    }

    [Fact]
    public async Task SendOneEach_ReadsAList_AndKeepsToTheDailyLimit()
    {
        await this.SeedAsync();
        const string list = "Email,First name,Ward\r\na@x.example,Anil,ICU\r\nb@x.example,,Ward 3\r\nbad,Zed,\r\n";
        IReadOnlyList<MergePerson> people = await this.svc.MergePeopleAsync(this.mailbox.Id, string.Empty, list);
        Assert.Equal(2, people.Count);
        Assert.Equal("ICU", people[0].Values["Ward"]);
        Assert.Equal("Dear Anil (ICU)", MailboxService.MergeFill("Dear {First name} ({Ward})", people[0]));

        string many = string.Join(", ", Enumerable.Range(0, MailboxService.DefaultDailyMergeLimit + 1).Select(i => $"p{i}@x.example"));
        (string? error, int count) = await this.svc.SendOneEachAsync(this.mailbox.Id, new ComposeRequest { To = many, Subject = "x", Body = "y" }, null, DateTimeOffset.UtcNow);
        Assert.NotNull(error);
        Assert.Equal(0, count);
    }

    // ---------------- Outbox ----------------

    [Fact]
    public async Task Outbox_ListsMailStillBeingDelivered_AndCanCancelOrRetryOnlyYourOwn()
    {
        await this.SeedAsync();
        Assert.Null(await this.svc.SendAsync(this.mailbox.Id, new ComposeRequest { To = "joseph@partnerlab.example", Subject = "Calibration", Body = "Dates?" }));
        OutboxItem item = Assert.Single(await this.svc.ListOutboxAsync(this.mailbox.Id));
        Assert.Equal("joseph@partnerlab.example", item.To);
        Assert.Equal("Calibration", item.Subject);
        Assert.Contains("Dates?", item.Text, StringComparison.Ordinal);

        Assert.False(await this.store.CancelOutboundAsync(item.Id, "someone@else.example"));
        Assert.True(await this.svc.RetryOutboxAsync(this.mailbox.Id, item.Id));
        Assert.True(await this.svc.CancelOutboxAsync(this.mailbox.Id, item.Id));
        Assert.Empty(await this.svc.ListOutboxAsync(this.mailbox.Id));
        Assert.Equal(OutboundMessage.CancelledBySender, this.store.Outbound.Single(o => o.Id == item.Id).LastError);
    }

    // ---------------- Conversations ----------------

    [Fact]
    public async Task Conversations_GroupReplies_AcrossFolders()
    {
        Assert.Equal("agenda for friday", MailboxService.ConversationKey("RE: Fwd:  Agenda   for Friday"));
        Assert.Equal("agenda for friday", MailboxService.ConversationKey("Re[2]: Agenda for Friday"));
        await this.SeedAsync();
        MessageRow first = await this.DeliverAsync("From: q@anjal.co.in\r\nSubject: Agenda for Friday\r\n\r\nx\r\n");
        await this.DeliverAsync("From: m@anjal.co.in\r\nSubject: Re: Agenda for Friday\r\n\r\nx\r\n");
        await this.DeliverAsync("From: z@anjal.co.in\r\nSubject: Something else\r\n\r\nx\r\n");
        IReadOnlyList<ConversationItem> conv = await this.svc.ConversationOfAsync(this.mailbox.Id, first);
        Assert.Equal(2, conv.Count);
        Assert.Equal(1, await this.svc.MarkConversationReadAsync(this.mailbox.Id, first.Id) - 1);
    }

    // ---------------- Folders, rules, unread ----------------

    [Fact]
    public async Task OwnFolders_AreCreatedAndRemovedOnlyWhenEmpty_AndStandardNamesAreRefused()
    {
        await this.SeedAsync();
        Assert.Null(await this.svc.CreateFolderAsync(this.mailbox.Id, "Suppliers"));
        Assert.NotNull(await this.svc.CreateFolderAsync(this.mailbox.Id, "suppliers"));
        Assert.NotNull(await this.svc.CreateFolderAsync(this.mailbox.Id, "Sent"));
        MessageRow m = await this.DeliverAsync("From: a@x.example\r\nSubject: Quote\r\n\r\nx\r\n");
        await this.svc.MoveAsync(this.mailbox.Id, m.Id, "Suppliers");
        Assert.NotNull(await this.svc.DeleteFolderAsync(this.mailbox.Id, "Suppliers"));
        await this.svc.MoveAsync(this.mailbox.Id, m.Id, FolderRow.Inbox);
        Assert.Null(await this.svc.DeleteFolderAsync(this.mailbox.Id, "Suppliers"));
        Assert.DoesNotContain(await this.store.ListFoldersAsync(this.mailbox.Id), f => f.Name == "Suppliers");
        Assert.NotNull(await this.svc.DeleteFolderAsync(this.mailbox.Id, "Archive"));
    }

    [Fact]
    public async Task Rules_AreCheckedWhenSaved_TriedOnRecentMail_AndAppliedOnlyWhenAsked()
    {
        await this.SeedAsync();
        Assert.NotNull(await this.svc.SaveRuleAsync(this.mailbox.Id, new MailRule { Name = "", Conditions = { new RuleCondition { Field = "from", Value = "a" } }, MoveTo = "Archive" }));
        Assert.NotNull(await this.svc.SaveRuleAsync(this.mailbox.Id, new MailRule { Name = "x" }));
        Assert.NotNull(await this.svc.SaveRuleAsync(this.mailbox.Id, new MailRule { Name = "x", Conditions = { new RuleCondition { Field = "from", Value = "a" } }, MoveTo = "Sent" }));
        MessageRow report = await this.DeliverAsync("From: noreply-dmarc-support@google.com\r\nSubject: Report domain: anjal.co.in\r\n\r\nx\r\n");
        await this.DeliverAsync("From: a@x.example\r\nSubject: Hello\r\n\r\nx\r\n");
        MailRule dmarc = MailboxService.DmarcRule();
        Assert.Null(await this.svc.SaveRuleAsync(this.mailbox.Id, dmarc));
        RuleTrial trial = await this.svc.TryRuleAsync(this.mailbox.Id, dmarc, apply: false);
        Assert.Equal(1, trial.Matches);
        Assert.Equal(FolderRow.Inbox, await this.FolderOfAsync(report.Id));
        await this.svc.TryRuleAsync(this.mailbox.Id, dmarc, apply: true);
        Assert.Equal("DMARC reports", await this.FolderOfAsync(report.Id));
        Assert.True(this.store.MailboxMessages.Single(m => m.Id == report.Id).Seen);
    }

    [Fact]
    public async Task MarkFolderUnread_AndFilteredSearch()
    {
        await this.SeedAsync();
        MessageRow a = await this.DeliverAsync("From: Lab <lab@x.example>\r\nSubject: Report\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nsee\r\n--b\r\nContent-Type: application/pdf; name=r.pdf\r\nContent-Disposition: attachment; filename=r.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0=\r\n--b--\r\n");
        await this.DeliverAsync("From: Accounts <acc@x.example>\r\nSubject: Report too\r\n\r\nx\r\n");
        FolderRow inbox = (await this.svc.GetFolderAsync(this.mailbox.Id, FolderRow.Inbox))!;
        await this.svc.MarkFolderReadAsync(this.mailbox.Id, inbox.Id);
        Assert.Equal(2, await this.svc.MarkFolderUnreadAsync(this.mailbox.Id, inbox.Id));

        (IReadOnlyList<MessageRow> byWho, long n1) = await this.svc.SearchFilteredAsync(this.mailbox.Id, null, "report", "lab", null, null, false, 0, 50);
        Assert.Equal(1, n1);
        Assert.Equal(a.Id, byWho[0].Id);
        (IReadOnlyList<MessageRow> _, long n2) = await this.svc.SearchFilteredAsync(this.mailbox.Id, null, "report", string.Empty, null, null, true, 0, 50);
        Assert.Equal(1, n2);
        (IReadOnlyList<MessageRow> _, long n3) = await this.svc.SearchFilteredAsync(this.mailbox.Id, null, "report", string.Empty, DateTimeOffset.UtcNow.AddDays(1), null, false, 0, 50);
        Assert.Equal(0, n3);
    }

    // ---------------- Preview and pictures ----------------

    [Fact]
    public void Preview_IsOnlyForRasterImagesAndPdf()
    {
        Assert.Equal("image/png", MailboxService.PreviewType(new AttachmentView { FileName = "a.png" }));
        Assert.Equal("application/pdf", MailboxService.PreviewType(new AttachmentView { FileName = "x.bin", ContentType = "application/pdf" }));
        Assert.Null(MailboxService.PreviewType(new AttachmentView { FileName = "a.svg", ContentType = "image/svg+xml" }));
        Assert.Null(MailboxService.PreviewType(new AttachmentView { FileName = "a.html", ContentType = "text/html" }));
    }

    [Fact]
    public void MailBackgroundPictures_SurviveTheSanitiser_RemotePicturesDoNot()
    {
        string pic = MailBackgrounds.Pictures[0].Picture;
        string kept = HtmlSanitizer.Sanitize($"<div style=\"background-color:#fffbf0;background-image:url({pic});padding:20px\">Hi</div>");
        Assert.Contains("background-image:url(data:image/png;base64,", kept, StringComparison.Ordinal);
        Assert.Contains("padding:20px", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("url(http", HtmlSanitizer.Sanitize("<div style=\"background-image:url(https://track.example/p.png)\">x</div>"), StringComparison.Ordinal);
        Assert.DoesNotContain("svg", HtmlSanitizer.Sanitize("<div style=\"background-image:url(data:image/svg+xml;base64,PHN2Zz4=)\">x</div>"), StringComparison.Ordinal);
        string huge = "data:image/png;base64," + new string('A', HtmlSanitizer.MaxInlinePicture + 10);
        Assert.DoesNotContain("background-image", HtmlSanitizer.Sanitize($"<div style=\"background-image:url({huge})\">x</div>"), StringComparison.Ordinal);
        foreach ((string name, string colour, string picture) in MailBackgrounds.Pictures)
        {
            Assert.True(picture.Length < HtmlSanitizer.MaxInlinePicture, name);
            Assert.StartsWith("#", colour, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DiscardDraft_RemovesOnlyDrafts()
    {
        await this.SeedAsync();
        MessageRow inbox = await this.DeliverAsync("From: a@x.example\r\nSubject: Keep\r\n\r\nx\r\n");
        Guid draft = (await this.svc.SaveDraftAsync(this.mailbox.Id, new ComposeRequest { To = "a@x.example", Subject = "d" }))!.Value;
        Assert.False(await this.svc.DiscardDraftAsync(this.mailbox.Id, inbox.Id));
        Assert.True(await this.svc.DiscardDraftAsync(this.mailbox.Id, draft));
        Assert.False(await this.svc.DiscardDraftAsync(this.mailbox.Id, Guid.NewGuid()));
    }
}
