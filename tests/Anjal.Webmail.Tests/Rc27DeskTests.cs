using System.Net;
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
/// rc.27 (item 70, DES-17 §2): shared desks through the real pipeline - a conversation arriving is
/// Not assigned, assigning and taking follow the desk's rule, a reply from the desk makes it Waiting
/// and names its writer, a new mail reopens a done item with the same person, notes are never sent,
/// "is replying", Today's "Assigned to you", the working week, and giving mail back.
/// </summary>
[Collection("Operators")]
public sealed class Rc27DeskTests : IAsyncLifetime, IDisposable
{
    private static readonly int[] WeekDays = { 1, 2, 3, 4, 5 };
    private static readonly string[] DeskAddress = { "reception@anjal.co.in" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc27d-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
    private MailboxRow arun = new();
    private MailboxRow meera = new();
    private MailboxRow ravi = new();
    private Guid desk;

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
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.Person("arun", "Arun Shiva B");
        this.meera = await this.Person("meera.iyer", "Meera Iyer");
        this.ravi = await this.Person("ravi", "Ravi Kumar");
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        this.baseAddress = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        MailboxService svc = this.Svc;
        (string? _, Guid? made) = await svc.CreateSharedMailboxAsync(this.tenant, "reception@anjal.co.in", "Reception");
        this.desk = made!.Value;
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, this.desk, this.meera.Id, "send"));
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, this.desk, this.ravi.Id, "send"));
        Assert.Null(await svc.SetSharedRightAsync(this.tenant, this.desk, this.arun.Id, "read"));
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

    private MailboxService Svc => this.app.Services.GetRequiredService<MailboxService>();

    // The service called directly reads the desk's mail as its members do: with the desk's key.
    private IDisposable Keys() => MailKeyring.Use(TestMail.Ring(this.store));

    [Fact]
    public void DueAt_CountsWorkingDaysAndHours_InTheWeek()
    {
        var week = new WorkWeek(); // Mon-Sat 09:00-18:00, India
        // Saturday 17:00 + one working day = Monday 17:00 (Sunday is not counted).
        DateTimeOffset sat5 = new(2026, 10, 31, 17, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 17, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.DueAt(sat5, "1d", week));
        // Arriving on Sunday counts from Monday's opening.
        DateTimeOffset sun = new(2026, 11, 1, 11, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.DueAt(sun, "1d", week));
        // Four working hours from 16:00 run over closing time into the next morning.
        DateTimeOffset fri4 = new(2026, 10, 30, 16, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 11, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.DueAt(fri4, "4h", week));
        // A Monday-to-Friday organisation skips Saturday too.
        var fiveDays = new WorkWeek { Days = new List<int> { 1, 2, 3, 4, 5 } };
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.FromHours(5.5)), MailboxService.DueAt(fri4, "1d", fiveDays));
        Assert.Null(MailboxService.DueAt(fri4, "none", week));
        // The wait is counted the same way: Saturday 17:00 to Monday 10:00 is two working hours.
        Assert.Equal(120, MailboxService.WorkingMinutes(sat5, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(5.5)), week));
        Assert.Equal(0, MailboxService.WorkingMinutes(sun, sun.AddHours(5), week));
        Assert.Equal(9 * 60, MailboxService.WorkingMinutes(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(5.5)), new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.FromHours(5.5)), week));
        Assert.NotNull(new WorkWeek { From = "18:00", To = "09:00" }.Problem());
    }

    [Fact]
    public async Task Conversation_IsAssigned_Replied_Done_AndReopenedByNewMail()
    {
        using IDisposable keys = this.Keys();
        MessageRow first = await this.ArriveAsync("Bill for October", "Please check my bill.");
        IReadOnlyDictionary<Guid, DeskState> states = await this.Svc.DeskStatesAsync(this.desk, new[] { first });
        Assert.Equal("unassigned", states[first.Id].State);
        Assert.NotNull(states[first.Id].Due);

        // A read-only member may assign by default (D-304); only a member who may send can be given it.
        HttpClient arunC = await this.InDeskAsync("arun");
        string page = await arunC.GetStringAsync("folder/INBOX?open=" + first.Id);
        Assert.Contains("Assigned to", page, StringComparison.Ordinal);
        HttpResponseMessage assigned = await Post(arunC, "desk/act", Token(page), ("message", first.Id.ToString()), ("action", "assign"), ("to", this.ravi.Id.ToString()));
        Assert.DoesNotContain("deskerr", assigned.Headers.Location!.ToString(), StringComparison.Ordinal);
        HttpResponseMessage refused = await Post(arunC, "desk/act", Token(page), ("message", first.Id.ToString()), ("action", "assign"), ("to", this.arun.Id.ToString()));
        Assert.Contains("deskerr", refused.Headers.Location!.ToString(), StringComparison.Ordinal);
        HttpResponseMessage take = await Post(arunC, "desk/act", Token(page), ("message", first.Id.ToString()), ("action", "take"));
        Assert.Contains("deskerr", take.Headers.Location!.ToString(), StringComparison.Ordinal);

        states = await this.Svc.DeskStatesAsync(this.desk, new[] { first });
        Assert.Equal("open", states[first.Id].State);
        Assert.Equal(this.ravi.Id, states[first.Id].AssigneeId);

        // Ravi's Today shows it.
        HttpClient raviC = this.Client();
        await SignInAsync(raviC, "ravi", "correct horse battery");
        string today = await raviC.GetStringAsync("today");
        Assert.Contains("Assigned to you", today, StringComparison.Ordinal);
        Assert.Contains("Bill for October", today, StringComparison.Ordinal);

        // Ravi replies from the desk: it waits for the patient, and the history and Sent name him.
        string opened = await PostFollow(raviC, "desk/open", Token(today), ("desk", this.desk.ToString()), ("message", first.Id.ToString()));
        Assert.Contains("Desk", opened, StringComparison.Ordinal);
        HttpResponseMessage sent = await PostMultipart(raviC, "compose", Token(opened), ("to", "patient@example.org"), ("subject", "Re: Bill for October"), ("body", "We have checked it."));
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.ToString());
        states = await this.Svc.DeskStatesAsync(this.desk, new[] { first });
        Assert.Equal("waiting", states[first.Id].State);
        MessageRow sentCopy = this.store.Seen().Last(m => m.MailboxId == this.desk && m.Subject.StartsWith("Re:", StringComparison.Ordinal));
        Assert.Equal(this.ravi.Id, sentCopy.WrittenBy);
        string sentList = await raviC.GetStringAsync("folder/Sent");
        Assert.Contains("Written by Ravi Kumar", sentList, StringComparison.Ordinal);
        DeskPanel? panel = await this.Svc.DeskPanelAsync(this.desk, this.ravi.Id, first);
        Assert.Contains(panel!.History, l => l.Kind == "replied" && l.Actor == "Ravi Kumar");

        // Done; then the patient writes again: Open, with Ravi still.
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, first.Id, "done", null, null));
        Assert.Equal("done", (await this.Svc.DeskStatesAsync(this.desk, new[] { first }))[first.Id].State);
        MessageRow again = await this.ArriveAsync("RE: Bill for October", "Still wrong.");
        DeskState reopened = (await this.Svc.DeskStatesAsync(this.desk, new[] { again }))[again.Id];
        Assert.Equal("open", reopened.State);
        Assert.Equal(this.ravi.Id, reopened.AssigneeId);
        panel = await this.Svc.DeskPanelAsync(this.desk, this.ravi.Id, again);
        Assert.Contains(panel!.History, l => l.Kind == "reopened" && l.Actor.Length == 0);
        Assert.Contains(await this.store.ListAuditAsync(100), e => e.Action == "desk.replied");
    }

    [Fact]
    public async Task TwoPeople_WithTheSameSubject_AreTwoPiecesOfWork()
    {
        using IDisposable keys = this.Keys();
        MessageRow meena = await this.ArriveAsync("Appointment", "For my father.", "meena@example.org");
        MessageRow suresh = await this.ArriveAsync("Appointment", "For me.", "suresh@example.org");
        MessageRow meenaAgain = await this.ArriveAsync("Re: Appointment", "Any time after 4?", "MEENA@example.org");
        Assert.Equal(2, (await this.store.ListDeskItemsAsync(this.desk)).Count);
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, meena.Id, "take", null, null));
        IReadOnlyDictionary<Guid, DeskState> states = await this.Svc.DeskStatesAsync(this.desk, new[] { meena, suresh, meenaAgain });
        Assert.Equal("open", states[meena.Id].State);
        Assert.Equal("open", states[meenaAgain.Id].State);
        Assert.Equal("unassigned", states[suresh.Id].State);
        // The desk's reply to Suresh closes his wait, not Meena's.
        await this.Svc.NoteDeskReplyAsync(this.desk, this.meera.Id, "Re: Appointment", "Suresh K <suresh@example.org>", string.Empty, null);
        states = await this.Svc.DeskStatesAsync(this.desk, new[] { meena, suresh });
        Assert.Equal("open", states[meena.Id].State);
        Assert.Equal("waiting", states[suresh.Id].State);
        // The desk's list shows both conversations, not one row for the subject (review, 28 Oct).
        HttpClient raviC = await this.InDeskAsync("ravi");
        string list = await raviC.GetStringAsync("folder/INBOX");
        Assert.Contains("data-desk-state=\"open\"", list, StringComparison.Ordinal);
        Assert.Contains("data-desk-state=\"waiting\"", list, StringComparison.Ordinal);
        // A reply that answers a mail closes that mail's wait, whatever its subject or address (Reply-To, an edited subject).
        MessageRow form = await this.ArriveAsync("Contact form", "Please call me.", "forms@example.net");
        await this.Svc.NoteDeskReplyAsync(this.desk, this.meera.Id, "About your call", "kumar@example.com", "<" + form.MessageId + ">", null);
        Assert.Equal("waiting", (await this.Svc.DeskStatesAsync(this.desk, new[] { form }))[form.Id].State);
        // Opening a desk is a form post only: a link cannot switch the session.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await raviC.GetAsync("desk/open?desk=" + this.desk + "&message=" + meena.Id)).StatusCode);
    }

    [Fact]
    public async Task AnEncryptedDesk_IsLeftAlone_WhenItsKeyIsNotHere()
    {
        await SignInAsync(this.Client(), "arun", "correct horse battery");
        using (MailKeyring.Use(TestMail.Ring(this.store)))
        {
            await this.Svc.SyncKeysAsync(this.tenant, this.arun.Id);
        }
        MessageRow m = await this.ArriveAsync("Lab report", "Attached.");
        // Without the desk's key the subject reads as empty: no stray item, and the person is told why.
        Assert.Equal("This mail cannot be read here. Sign in with your password and try again.",
            await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "take", null, null));
        Assert.Empty(await this.Svc.DeskStatesAsync(this.desk, new[] { m }));
        using IDisposable keys = this.Keys();
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "take", null, null));
        Assert.Single(await this.store.ListDeskItemsAsync(this.desk));
    }

    [Fact]
    public async Task DeskOnlyForSenders_RefusesAReadOnlyMembersAssign()
    {
        using IDisposable keys = this.Keys();
        MessageRow m = await this.ArriveAsync("Appointment", "Can I come on Monday?");
        Assert.Null(await this.Svc.SetDeskSettingsAsync(this.tenant, this.desk, "send", "4h", null));
        Assert.NotNull(await this.Svc.DeskActAsync(this.desk, this.arun.Id, m.Id, "assign", this.meera.Id, null));
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "assign", this.meera.Id, null));
        // Only the one who has it, or a manager, may mark it done.
        Assert.NotNull(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "done", null, null));
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.meera.Id, m.Id, "done", null, null));
    }

    [Fact]
    public async Task Notes_AreNeverSent_AndOnlyTheirWriterWithdrawsThem()
    {
        // The organisation's keys are made at an administrator's password sign-in, and the desk's with them.
        await SignInAsync(this.Client(), "arun", "correct horse battery");
        using (MailKeyring.Use(TestMail.Ring(this.store)))
        {
            await this.Svc.SyncKeysAsync(this.tenant, this.arun.Id);
        }
        Assert.NotNull(await this.store.GetMailboxKeysAsync(this.desk));
        using IDisposable keys = this.Keys();
        MessageRow m = await this.ArriveAsync("Lab report", "Report attached.");
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.meera.Id, m.Id, "note", null, "Patient called twice - @Ravi please ring back"));
        DeskPanel panel = (await this.Svc.DeskPanelAsync(this.desk, this.ravi.Id, m))!;
        DeskNote note = Assert.Single(panel.Notes);
        Assert.Equal("Patient called twice - @Ravi please ring back", note.Text);
        // Kept sealed, like the mail: the stored note does not hold its words (D-308).
        DeskItemRow item = Assert.Single(await this.store.ListDeskItemsAsync(this.desk));
        DeskNoteRow stored = Assert.Single(await this.store.ListDeskNotesAsync(item.Id));
        Assert.DoesNotContain("called twice", stored.Body, StringComparison.Ordinal);
        // Ravi was mentioned: his desk mark rose.
        Assert.True(await this.Svc.DeskStampAsync(this.ravi.Id) > 0);
        // A forward or a reply from the desk never carries the note.
        ComposePrefill? forward = await this.Svc.PrefillAsync(this.desk, m.Id, MailboxService.PrefillKind.Forward);
        Assert.DoesNotContain("called twice", forward!.Body, StringComparison.Ordinal);
        Assert.NotNull(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "withdraw", null, note.Id.ToString()));
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.meera.Id, m.Id, "withdraw", null, note.Id.ToString()));
        panel = (await this.Svc.DeskPanelAsync(this.desk, this.ravi.Id, m))!;
        Assert.Null(Assert.Single(panel.Notes).Text);
        Assert.Contains(panel.History, l => l.Kind == "note-withdrawn");
    }

    [Fact]
    public async Task Replying_IsShown_AndAsksBeforeASecondReply()
    {
        using IDisposable keys = this.Keys();
        MessageRow m = await this.ArriveAsync("Discharge summary", "Please send it.");
        await this.Svc.SetReplyingAsync(this.desk, this.meera.Id, m.Id, true);
        Assert.Equal("Meera Iyer", (await this.Svc.OtherReplyingAsync(this.desk, this.ravi.Id, m))?.Name);
        Assert.Null(await this.Svc.OtherReplyingAsync(this.desk, this.meera.Id, m));
        HttpClient raviC = await this.InDeskAsync("ravi");
        string page = await raviC.GetStringAsync("folder/INBOX?open=" + m.Id);
        Assert.Contains("Meera Iyer is replying", page, StringComparison.Ordinal);
        Assert.Contains("Leave it to Meera Iyer", page, StringComparison.Ordinal);
        await this.Svc.SetReplyingAsync(this.desk, this.meera.Id, m.Id, false);
        Assert.Null(await this.Svc.OtherReplyingAsync(this.desk, this.ravi.Id, m));
    }

    [Fact]
    public async Task TakingAwayTheRight_GivesTheMailBack()
    {
        using IDisposable keys = this.Keys();
        MessageRow m = await this.ArriveAsync("Insurance", "Claim form.");
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.meera.Id, m.Id, "take", null, null));
        Assert.Null(await this.Svc.SetSharedRightAsync(this.tenant, this.desk, this.meera.Id, "read"));
        DeskState state = (await this.Svc.DeskStatesAsync(this.desk, new[] { m }))[m.Id];
        Assert.Equal("unassigned", state.State);
        DeskPanel panel = (await this.Svc.DeskPanelAsync(this.desk, this.ravi.Id, m))!;
        Assert.Contains(panel.History, l => l.Kind == "returned" && l.Other == "Meera Iyer");
        Assert.Contains(await this.store.ListAuditAsync(50), e => e.Action == "desk.returned" && e.Detail == "1 from meera.iyer@anjal.co.in");
    }

    [Fact]
    public async Task DeskTabs_ListTheirConversations()
    {
        using IDisposable keys = this.Keys();
        MessageRow a = await this.ArriveAsync("First question", "a");
        MessageRow b = await this.ArriveAsync("Second question", "b");
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, a.Id, "take", null, null));
        HttpClient raviC = await this.InDeskAsync("ravi");
        string mine = await raviC.GetStringAsync("folder/INBOX?tab=mine");
        Assert.Contains("First question", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("Second question", mine, StringComparison.Ordinal);
        string unassigned = await raviC.GetStringAsync("folder/INBOX?tab=unassigned");
        Assert.Contains("Second question", unassigned, StringComparison.Ordinal);
        Assert.DoesNotContain("First question", unassigned, StringComparison.Ordinal);
        Assert.Contains("data-desk-state=\"open\"", await raviC.GetStringAsync("folder/INBOX"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkWeek_IsTheOrganisations_AndADeskMaySetItsOwn()
    {
        // A member who does not manage the desk cannot change it.
        HttpClient raviC = await this.InDeskAsync("ravi");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(raviC, "desk/settings/" + this.desk, Token(await raviC.GetStringAsync("folder/INBOX")), ("assignBy", "send"), ("target", "1d"), ("week", "org"))).StatusCode);

        HttpClient admin = this.Client();
        await SignInAsync(admin, "arun", "correct horse battery");
        string page = await admin.GetStringAsync("org/shared?sel=" + this.desk);
        Assert.Contains("Working week", page, StringComparison.Ordinal);
        Assert.Contains("Reply due within", page, StringComparison.Ordinal);
        // rc.27 (item 72.1, D-316): the desk's storage as a meter, in words too.
        Assert.Contains("class=\"smeter", page, StringComparison.Ordinal);
        HttpResponseMessage saved = await Post(admin, "org/work-week", Token(page), ("day", "1"), ("day", "2"), ("day", "3"), ("day", "4"), ("day", "5"), ("from", "08:00"), ("until", "16:00"), ("zone", "Asia/Kolkata"));
        Assert.Equal("/org/shared?saved=week", saved.Headers.Location!.ToString());
        WorkWeek week = await this.Svc.WorkWeekAsync(this.tenant.Id);
        Assert.Equal(WeekDays, week.Days);
        Assert.Equal("08:00", week.From);
        HttpResponseMessage own = await Post(admin, "desk/settings/" + this.desk, Token(page), ("assignBy", "any"), ("target", "2h"), ("week", "own"),
            ("day", "0"), ("day", "1"), ("day", "2"), ("day", "3"), ("day", "4"), ("day", "5"), ("day", "6"), ("from", "00:00"), ("until", "23:59"), ("zone", "Asia/Kolkata"));
        Assert.Contains("saved=desk", own.Headers.Location!.ToString(), StringComparison.Ordinal);
        SharedMailbox info = (await this.Svc.SharedInfoAsync((await this.store.GetMailboxByIdAsync(this.desk))!))!;
        Assert.Equal(7, info.OwnWeek!.Days.Count);
        Assert.Equal("2h", info.ReplyTarget);
    }

    [Fact]
    public async Task AssignedMail_PopsUpForItsPerson_WhenTheyChose()
    {
        using IDisposable keys = this.Keys();
        MessageRow m = await this.ArriveAsync("Report request", "x");
        long before = await this.Svc.DeskStampAsync(this.meera.Id);
        Assert.Null(await this.Svc.DeskActAsync(this.desk, this.ravi.Id, m.Id, "assign", this.meera.Id, null));
        Assert.True(await this.Svc.DeskStampAsync(this.meera.Id) > before);
        Assert.Null(await this.Svc.DeskPopUpAsync(this.meera.Id, ZonedClock.Default));
        await this.Svc.SetDesktopNotifyAsync(this.meera.Id, true, "needs");
        Assert.StartsWith("/desk/open?desk=" + this.desk, await this.Svc.DeskPopUpAsync(this.meera.Id, ZonedClock.Default), StringComparison.Ordinal);
        await this.Svc.SetDesktopNotifyAsync(this.meera.Id, true, "needs", desks: "none");
        Assert.Null(await this.Svc.DeskPopUpAsync(this.meera.Id, ZonedClock.Default));
    }

    private async Task<MessageRow> ArriveAsync(string subject, string text, string from = "patient@example.org")
    {
        string raw = $"From: Patient <{from}>\r\nTo: reception@anjal.co.in\r\nSubject: {subject}\r\nMessage-ID: <{Guid.NewGuid():N}@example.org>\r\n\r\n{text}\r\n";
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = from,
            EnvelopeTo = DeskAddress,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.Seen().Last(m => m.MailboxId == this.desk);
    }

    private async Task<HttpClient> InDeskAsync(string user)
    {
        HttpClient c = this.Client();
        await SignInAsync(c, user, "correct horse battery");
        string inbox = await c.GetStringAsync("folder/INBOX");
        HttpResponseMessage opened = await Post(c, "shared/open", Token(inbox), ("id", this.desk.ToString()));
        Assert.Equal("/folder/INBOX", opened.Headers.Location!.ToString());
        return c;
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return c.PostAsync(path, new FormUrlEncodedContent(form));
    }

    private static Task<HttpResponseMessage> PostMultipart(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach ((string key, string value) in fields)
        {
            form.Add(new StringContent(value), key);
        }
        return c.PostAsync(path, form);
    }

    private static async Task<string> PostFollow(HttpClient c, string path, string token, params (string Key, string Value)[] fields)
    {
        HttpResponseMessage res = await Post(c, path, token, fields);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return await c.GetStringAsync(res.Headers.Location!.ToString().TrimStart('/'));
    }

    private static async Task SignInAsync(HttpClient c, string user, string password)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        HttpResponseMessage res = await Post(c, "auth/login", token, ("address", user), ("password", password));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private Task<MailboxRow> Person(string local, string name) =>
        this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = local,
            Domain = "anjal.co.in",
            DisplayName = name,
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }
}
