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
/// rc.18 (design review section 11, owner 11-12 Oct 2026): the help guide and Lajna, inside Anjal -
/// it answers from the guide and cites it, proposes a setting only with consent, finds the person's
/// own mail, writes a draft (naming who covered last time from the person's own Sent mail), and
/// explains a delivery. It never reaches another person's mail.
/// </summary>
public sealed class Rc18HelpAndLajnaTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] ToArun = { "arun@anjal.co.in" };
    private static readonly string[] ToMeera = { "meera@anjal.co.in" };
    private static readonly int[] NoOnce = { 0, 1 };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc18-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;
    private MailboxRow arun = new();
    private MailboxRow meera = new();

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun Shiva", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        this.meera = await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "anjal.co.in", DisplayName = "Meera Iyer", PasswordPbkdf2 = Pbkdf2Hasher.Hash("another long password", 1000) });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "rao", Domain = "anjal.co.in", DisplayName = "Dr Rao", PasswordPbkdf2 = Pbkdf2Hasher.Hash("a third long password", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string token = await this.TokenAsync("sign-in");
        Assert.Equal(HttpStatusCode.Redirect, (await this.PostAsync("auth/login", token, ("address", "arun@anjal.co.in"), ("password", "correct horse battery"))).StatusCode);
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
    public void TheGuide_HoldsItsPartsAndArticles_AndEveryLinkInItLeadsSomewhere()
    {
        HelpGuide guide = HelpGuide.Load();
        Assert.Equal(12, guide.Parts.Count);
        Assert.True(guide.Articles.Count >= 50, guide.Articles.Count + " articles");
        Assert.Equal(guide.Articles.Count, guide.Articles.Select(a => a.Id).Distinct().Count());
        Assert.All(guide.Articles, a =>
        {
            Assert.NotNull(guide.Part(a.Part));
            Assert.NotEmpty(a.Steps);
            Assert.All(a.Related, r => Assert.NotNull(guide.Get(r)));
            Assert.True(a.Show is null || a.Show.Path.StartsWith('/'), a.Id);
        });
        Assert.All(guide.Parts, p => Assert.Contains(guide.Articles, a => a.Part == p.Id));
        Assert.Equal("two-step", guide.Search("How do I turn on two-step?")[0].Article.Id);
        Assert.Equal("theme", guide.Search("dark mode colours")[0].Article.Id);
        Assert.Equal("send-later", guide.Search("How do I send later?")[0].Article.Id);
        Assert.Empty(guide.Search("how do I"));
    }

    [Fact]
    public async Task TheHelpPages_ShowPartsArticlesAndShowMe_AndCountTheAnswers()
    {
        string home = await this.client.GetStringAsync(new Uri("help", UriKind.Relative));
        Assert.Contains("How can we help?", home, StringComparison.Ordinal);
        Assert.Contains("href=\"/help/part/security\"", home, StringComparison.Ordinal);
        Assert.Contains("Still stuck?", home, StringComparison.Ordinal);
        Assert.Contains("Arun Shiva (arun@anjal.co.in)", home, StringComparison.Ordinal);   // the administrator
        string article = await this.client.GetStringAsync(new Uri("help/two-step", UriKind.Relative));
        Assert.Contains("<h1 id=\"h-title\">Turn on two-step sign-in</h1>", article, StringComparison.Ordinal);
        Assert.Contains("class=\"helpsteps\"", article, StringComparison.Ordinal);
        Assert.Contains("href=\"/settings/security#ring=", article, StringComparison.Ordinal);
        Assert.Contains("Set by your organisation", article, StringComparison.Ordinal);
        string found = await this.client.GetStringAsync(new Uri("help?q=" + Uri.EscapeDataString("schedule a message"), UriKind.Relative));
        Assert.Contains("href=\"/help/send-later\"", found, StringComparison.Ordinal);

        string token = await this.TokenAsync("help/two-step");
        HttpResponseMessage no = await this.PostAsync("help/feedback", token, ("id", "two-step"), ("helped", "0"));
        Assert.Contains("thanks=1", no.Headers.Location!.ToString(), StringComparison.Ordinal);
        Dictionary<string, int[]> counts = await HelpFeedback.ReadAsync(this.store);
        Assert.Equal(NoOnce, counts["two-step"]);
    }

    [Fact]
    public async Task EveryShowMe_LeadsToAPageWithThatControl()
    {
        // "Show me in Anjal" rings a real control: each article's page has it.
        HelpGuide guide = HelpGuide.Load();
        var missing = new List<string>();
        foreach (HelpArticle a in guide.Articles.Where(a => a.Show is not null))
        {
            string html = await this.client.GetStringAsync(new Uri(a.Show!.Path.TrimStart('/'), UriKind.Relative));
            Match cls = Regex.Match(a.Show.At, @"\.([a-z][\w-]*)");
            string token = cls.Success ? cls.Groups[1].Value : a.Show.At.Split(' ', '.', '[')[0];
            if (!Regex.IsMatch(html, "class=\"(?:[^\"]* )?" + Regex.Escape(token) + "(?: [^\"]*)?\"") && !html.Contains("<" + token, StringComparison.Ordinal))
            {
                missing.Add(a.Id + ": " + a.Show.At + " on " + a.Show.Path);
            }
        }
        Assert.True(missing.Count == 0, "Show me finds nothing: " + string.Join(" | ", missing));
    }

    [Fact]
    public async Task Lajna_AnswersFromTheGuide_AndCitesIt()
    {
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("How do I turn on two-step?") + "&page=/folder/INBOX", UriKind.Relative));
        Assert.Contains("data-lajna-answer", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<nav class=\"rail", page, StringComparison.Ordinal);       // the answer alone, for the panel
        Assert.Contains("class=\"ljcite\" href=\"/help/two-step\"", page, StringComparison.Ordinal);
        Assert.Contains("class=\"ljsteps\"", page, StringComparison.Ordinal);
        string inbox = await this.client.GetStringAsync(new Uri("folder/INBOX", UriKind.Relative));
        Assert.Contains("data-lajna-open", inbox, StringComparison.Ordinal);
        Assert.Contains("id=\"lajna\"", inbox, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_ProposesASetting_AndChangesNothingUntilYes()
    {
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Turn on dark mode") + "&page=/folder/INBOX", UriKind.Relative));
        Assert.Contains("class=\"ljconsent\"", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/settings/theme\"", page, StringComparison.Ordinal);
        Assert.Matches("name=\"theme\" value=\"[a-z]+-dark\"", page);
        Assert.DoesNotContain("-dark", (await this.store.GetMailboxByIdAsync(this.arun.Id))!.Theme, StringComparison.Ordinal);
        string sound = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("turn off the new mail sound") + "&page=/folder/INBOX", UriKind.Relative));
        Assert.Contains("action=\"/settings/sound\"", sound, StringComparison.Ordinal);
        Assert.Contains("name=\"on\" value=\"0\"", sound, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_FindsThePersonsOwnMail_AndNeverAnothers()
    {
        await this.DeliverAsync(ToArun, "From: Meera Iyer <meera@anjal.co.in>\r\nSubject: Duty roster for Monday\r\n\r\nSee the roster.\r\n");
        await this.DeliverAsync(ToArun, "From: Meera Iyer <meera@anjal.co.in>\r\nSubject: Lunch\r\n\r\nAt one?\r\n");
        await this.DeliverAsync(ToMeera, "From: Rao <rao@anjal.co.in>\r\nSubject: Meera's own mail\r\n\r\nNot for Arun.\r\n");
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Get me the last 5 mails from Meera") + "&page=/", UriKind.Relative));
        Assert.Contains("class=\"ljmails\"", page, StringComparison.Ordinal);
        Assert.Contains("Duty roster for Monday", page, StringComparison.Ordinal);
        Assert.Contains("Lunch", page, StringComparison.Ordinal);
        string rao = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("show mails from Rao") + "&page=/", UriKind.Relative));
        Assert.DoesNotContain("Meera's own mail", rao, StringComparison.Ordinal);
    }

    [Fact]
    public void ALetterToADepartment_GreetsItByItsWholeName()
    {
        // Screen pictures, 12 Oct 2026: "Dear Human," to Human resources <hr@...>.
        LajnaDraft hr = LajnaLetters.Write("hr@anjal.co.in", "Human resources", "leave for my sister's wedding, 6-10 Nov", true, null, "Arun Shiva B", string.Empty, "normal");
        Assert.StartsWith("Dear Human resources team,", hr.Body, StringComparison.Ordinal);
        LajnaDraft person = LajnaLetters.Write("meera@anjal.co.in", "Meera Iyer", "the roster", false, null, "Arun Shiva B", string.Empty, "normal");
        Assert.StartsWith("Dear Meera,", person.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_FindsMailFromSomeoneNeverSavedAsAContact()
    {
        // Screen check, 12 Oct 2026: "the last 5 mails from Meera" found nobody when Meera only wrote in.
        await this.DeliverAsync(ToArun, "From: Vivek Nair <vivek@clinic.test>\r\nSubject: Biopsy slides sent\r\n\r\nSent by courier.\r\n");
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Get me the last 5 mails from Vivek") + "&page=/", UriKind.Relative));
        Assert.Contains("class=\"ljmails\"", page, StringComparison.Ordinal);
        Assert.Contains("Biopsy slides sent", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_WritesALeaveDraft_NamingWhoCoveredLastTime_FromThePersonsOwnSentMail()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        MessageRow old = await this.DeliverAsync(ToArun, "From: Arun Shiva <arun@anjal.co.in>\r\nTo: Meera Iyer <meera@anjal.co.in>\r\nSubject: Leave 3-5 Aug\r\n\r\nI will be on leave from 3 to 5 August. Dr Rao will cover my patients while I am away.\r\n");
        await svc.MoveAsync(this.arun.Id, old.Id, "Sent");
        LajnaCover? cover;
        using (Anjal.Store.MailKeyring ring = TestMail.Ring(this.store))
        using (Anjal.Store.MailKeyring.Use(ring))
        {
            cover = await svc.WhoCoveredAsync(this.arun.Id);
        }
        Assert.NotNull(cover);
        Assert.Equal("Dr Rao", cover!.Name);
        Assert.False(cover.Copied);

        string page = WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Write to Meera asking for leave for my sister's wedding, 6-10 Nov") + "&page=/", UriKind.Relative)));
        Assert.Contains("class=\"ljdraft\"", page, StringComparison.Ordinal);
        Assert.Contains("Leave request: 6–10 Nov", page, StringComparison.Ordinal);
        Assert.Contains("my sister's wedding", page, StringComparison.Ordinal);
        Assert.Contains("as Dr Rao kindly did last time", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/compose?to=meera%40anjal.co.in&subject=", page, StringComparison.Ordinal);
        string tamil = WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Write to Meera asking for leave for my sister's wedding, 6-10 Nov") + "&style=ta&page=/", UriKind.Relative)));
        Assert.Contains("விடுப்பு கோரிக்கை: 6–10 Nov", tamil, StringComparison.Ordinal);
    }

    [Fact]
    public void WhoCovered_IsReadFromThePersonsOwnWords()
    {
        (string, string)[] colleagues = { ("rao@anjal.co.in", "Dr Rao"), ("meera@anjal.co.in", "Meera Iyer") };
        Assert.Equal("Dr Rao", MailboxService.CoverName("Dr Rao will cover my patients while I am away.", colleagues));
        Assert.Equal("Meera Iyer", MailboxService.CoverName("In my absence please contact Meera for anything urgent.", colleagues));
        Assert.Equal("Meera Iyer", MailboxService.CoverName("Please contact meera@anjal.co.in while I am away.", colleagues));
        Assert.Null(MailboxService.CoverName("I will be away next week.", colleagues));
        Assert.Null(MailboxService.CoverName("I will cover the ward round.", colleagues));
    }

    [Fact]
    public void ALeaveDraft_ComesInFourStyles_AndAPlainNoteToo()
    {
        LajnaCover rao = new("Dr Rao", DateTimeOffset.UtcNow, "Leave", false);
        LajnaDraft normal = LajnaLetters.Write("hr@anjal.co.in", "hr@anjal.co.in", "for leave for my sister's wedding, 6-10 Nov", true, rao, "Arun Shiva", "Imagiqa", "normal");
        Assert.Equal("Leave request: 6–10 Nov", normal.Subject);
        Assert.StartsWith("Dear HR team,", normal.Body, StringComparison.Ordinal);
        Assert.Contains("leave from 6 to 10 Nov for my sister's wedding.", normal.Body, StringComparison.Ordinal);
        Assert.EndsWith("Arun Shiva\nImagiqa", normal.Body, StringComparison.Ordinal);
        Assert.Contains("Yours sincerely", LajnaLetters.Write("hr@anjal.co.in", "HR", "for leave, 6-10 Nov", true, null, "Arun", string.Empty, "formal").Body, StringComparison.Ordinal);
        Assert.StartsWith("Hi ", LajnaLetters.Write("meera@anjal.co.in", "Meera Iyer", "for leave on 14 Oct", true, null, "Arun", string.Empty, "short").Body, StringComparison.Ordinal);
        LajnaDraft note = LajnaLetters.Write("meera@anjal.co.in", "Meera Iyer", "about the duty roster for Monday", false, null, "Arun", string.Empty, "normal");
        Assert.Equal("About the duty roster for Monday", note.Subject);
        Assert.StartsWith("Dear Meera,", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_ExplainsWhatHappenedToAMessageThePersonSent()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        Assert.Null(await svc.SendAsync(this.arun.Id, new ComposeRequest { To = "friend@gmail.test", Subject = "The report", Body = "Attached soon." }));
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("Why didn't my mail to friend@gmail.test arrive?") + "&page=/", UriKind.Relative));
        Assert.Contains("class=\"ljstory\"", page, StringComparison.Ordinal);
        Assert.Contains("The report", page, StringComparison.Ordinal);
        Assert.Contains("It is waiting to be sent.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lajna_SaysWhenItIsNotSure()
    {
        string page = await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString("qwerty zxcvb") + "&page=/", UriKind.Relative));
        Assert.Contains("I am not sure.", page, StringComparison.Ordinal);
        Assert.Contains("Arun Shiva (arun@anjal.co.in)", page, StringComparison.Ordinal);
    }

    private async Task<MessageRow> DeliverAsync(string[] to, string raw)
    {
        await new MailboxSink(this.store, this.maildir).DeliverAsync(new DeliveryContext
        {
            EnvelopeFrom = "s@x.test",
            EnvelopeTo = to,
            RawBytes = System.Text.Encoding.UTF8.GetBytes(raw),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private async Task<string> TokenAsync(string path)
    {
        string html = await (await this.client.GetAsync(new Uri(path, UriKind.Relative))).Content.ReadAsStringAsync();
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
        using var content = new FormUrlEncodedContent(form);
        return await this.client.PostAsync(new Uri(path, UriKind.Relative), content);
    }
}
