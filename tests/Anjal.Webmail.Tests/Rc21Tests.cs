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
/// rc.21 (owner, 15 Oct 2026, with a screenshot of Lajna answering "I am not sure" twice: "Chat bot
/// mail drafting is not working properly"; a language model is for next year - "improve it as much
/// as you can within this"): Lajna reads a request to write a mail the way people type it.
/// </summary>
public sealed class Rc21Tests : IAsyncLifetime, IDisposable
{
    private const string Nafees = "I want to sent a leave letter to my child Nafees's principal saying he is on leave from 17th october to 19th october";
    private const string Hr = "i want to sent a leave letter to hr from 20th october to 22nd october";

    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc21-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public void Dispose() => this.client?.Dispose();

    public async Task InitializeAsync()
    {
        this.maildir = new MaildirStore(this.root, "test");
        TenantRow tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = tenant.Id, Domain = "anjal.co.in" });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "arun", Domain = "anjal.co.in", DisplayName = "Arun Shiva", PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000) });
        await this.store.UpsertMailboxAsync(new MailboxRow { TenantId = tenant.Id, LocalPart = "meera", Domain = "anjal.co.in", DisplayName = "Meera Iyer", PasswordPbkdf2 = Pbkdf2Hasher.Hash("another long password", 1000) });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
        string address = this.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        this.client = new HttpClient(handler) { BaseAddress = new Uri(address + "/") };
        string html = await (await this.client.GetAsync(new Uri("sign-in", UriKind.Relative))).Content.ReadAsStringAsync();
        string token = TokenRegex.Match(html).Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", token), new("address", "arun@anjal.co.in"), new("password", "correct horse battery") };
        using var content = new FormUrlEncodedContent(form);
        Assert.Equal(HttpStatusCode.Redirect, (await this.client.PostAsync(new Uri("auth/login", UriKind.Relative), content)).StatusCode);
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
    public async Task TheOwnersTwoRequests_BothBecomeDrafts()
    {
        string school = await this.AskAsync(Nafees);
        Assert.Contains("class=\"ljdraft\"", school, StringComparison.Ordinal);
        Assert.Contains("Leave of absence: Nafees, 17–19 October", school, StringComparison.Ordinal);
        Assert.Contains("Dear Principal,", school, StringComparison.Ordinal);
        Assert.Contains("my son Nafees will be absent from school from 17 to 19 October.", school, StringComparison.Ordinal);
        Assert.Contains("Kindly grant him leave for these days.", school, StringComparison.Ordinal);
        Assert.Contains("I do not know Principal's address", school, StringComparison.Ordinal);

        string hr = await this.AskAsync(Hr);
        Assert.Contains("class=\"ljdraft\"", hr, StringComparison.Ordinal);
        Assert.Contains("Leave request: 20–22 October", hr, StringComparison.Ordinal);
        Assert.Contains("Dear HR team,", hr, StringComparison.Ordinal);
        Assert.Contains("I would like to request leave from 20 to 22 October.", hr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftToSomeoneKnown_StillFindsTheirAddress()
    {
        string page = await this.AskAsync("can you draft an email to meera saying the meeting is moved to 4 pm");
        Assert.Contains("class=\"ljdraft\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/compose?to=meera%40anjal.co.in&subject=", page, StringComparison.Ordinal);
        Assert.Contains("I would like to let you know that the meeting is moved to 4 pm.", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Nafees, "Principal", "leave", "17–19 October")]
    [InlineData(Hr, "hr", "leave", "20–22 October")]
    [InlineData("write a mail to HR asking for leave on Friday", "HR", "leave", "Friday")]
    [InlineData("Write to Meera asking for leave for my sister's wedding, 6-10 Nov", "Meera", "leave", "6–10 Nov")]
    [InlineData("please send a mail to arun@anjal.co.in asking him to send the report", "arun@anjal.co.in", "ask", "")]
    [InlineData("email hr asking for a salary certificate", "hr", "ask", "")]
    [InlineData("Leave application to my manager for 3 days from next Monday because of my father's surgery", "manager", "leave", "3 days from next Monday")]
    [InlineData("help me write a formal leave letter to the principal of Nafees's school as he has fever, on 21/10", "Principal", "leave", "21 Oct")]
    [InlineData("send leave letter to my son's class teacher for tomorrow as he is unwell", "Class Teacher", "leave", "tomorrow")]
    [InlineData("pls write a short note to the HR team about the duty roster", "HR team", "about", "")]
    [InlineData("I need to send a mail to Meera thanking her for the help", "Meera", "thank", "")]
    [InlineData("could you write to Meera that I will be late today", "Meera", "tell", "today")]
    [InlineData("draft a leave mail to my manager from oct 28 to nov 2", "manager", "leave", "28 Oct–2 Nov")]
    [InlineData("write an application to hr for leave from 17/10/2026 to 19/10/2026", "hr", "leave", "17–19 Oct")]
    [InlineData("I want to send a mail to hr saying I won't be able to come on Monday", "hr", "leave", "Monday")]
    [InlineData("write leave letter for my daughter Aisha to her class teacher for 2 days from tomorrow due to fever", "Class Teacher", "leave", "2 days from tomorrow")]
    [InlineData("Please write a letter to HR requesting 2 days leave on 23rd and 24th October for my brother's marriage", "HR", "leave", "23 and 24 October")]
    [InlineData("Leave letter to class teacher, my son Kabir has fever today", "Class Teacher", "leave", "today")]
    [InlineData("need leave letter to hr for tomorrow", "hr", "leave", "tomorrow")]
    [InlineData("Inform my team that office is closed tomorrow", "team", "tell", "tomorrow")]
    [InlineData("ask Meera to send the report by Friday", "Meera", "ask", "Friday")]
    [InlineData("remind Rao about the meeting at 4", "Rao", "remind", "")]
    [InlineData("let Priya know that the patient is discharged", "Priya", "tell", "")]
    [InlineData("compose email to vendor@abc.com requesting quotation for 10 laptops", "vendor@abc.com", "request", "")]
    public void Lajna_ReadsTheRequest(string words, string who, string kind, string days)
    {
        LajnaAsk? ask = LajnaRequest.Parse(words);
        Assert.NotNull(ask);
        Assert.Equal(who, ask!.Who);
        Assert.Equal(kind, ask.Kind);
        Assert.Equal(days, ask.Dates.Display);
    }

    [Theory]
    [InlineData("make everything smaller")]
    [InlineData("turn off the new mail sound")]
    [InlineData("How do I send a mail to many people?")]
    [InlineData("Why didn't my mail to friend@gmail.test arrive?")]
    [InlineData("show mails from Rao")]
    [InlineData("email signature for work")]
    [InlineData("How do I turn on two-step?")]
    [InlineData("tell me how to add a signature")]
    public void OtherQuestions_AreNotTakenForADraft(string words) => Assert.Null(LajnaRequest.Parse(words));

    [Fact]
    public void ASchoolLeaveLetter_ComesInEveryStyle()
    {
        LajnaAsk ask = LajnaRequest.Parse("help me write a formal leave letter to the principal of Nafees's school as he has fever, on 21/10")!;
        Assert.Equal("formal", ask.Tone);
        LajnaDraft formal = LajnaLetters.Write(string.Empty, ask.Who, ask, null, "Arun Shiva", string.Empty, "formal");
        Assert.Equal("Leave of absence: Nafees, 21 Oct", formal.Subject);
        Assert.Contains("my son Nafees will not be able to attend school on 21 Oct, as he has fever.", formal.Body, StringComparison.Ordinal);
        Assert.Contains("kindly grant him leave for the said period.", formal.Body, StringComparison.Ordinal);
        Assert.Contains("Nafees will be absent on 21 Oct, as he has fever. Please excuse him.", LajnaLetters.Write(string.Empty, ask.Who, ask, null, "Arun", string.Empty, "short").Body, StringComparison.Ordinal);
        Assert.Contains("என் மகன் Nafees 21 Oct அன்று பள்ளிக்கு வர இயலாது", LajnaLetters.Write(string.Empty, ask.Who, ask, null, "Arun", string.Empty, "ta").Body, StringComparison.Ordinal);

        LajnaAsk teacher = LajnaRequest.Parse("send leave letter to my son's class teacher for tomorrow as he is unwell")!;
        LajnaDraft normal = LajnaLetters.Write(string.Empty, teacher.Who, teacher, null, "Arun", string.Empty, "normal");
        Assert.StartsWith("Dear Class Teacher,", normal.Body, StringComparison.Ordinal);
        Assert.Contains("my son will be absent from school tomorrow, as he is unwell.", normal.Body, StringComparison.Ordinal);
        Assert.Contains("Kindly grant him leave for the day.", normal.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ANote_SaysWhatWasAsked()
    {
        LajnaAsk report = LajnaRequest.Parse("please send a mail to arun@anjal.co.in asking him to send the report")!;
        Assert.Contains("Could you please send the report?", LajnaLetters.Write("arun@anjal.co.in", "Arun Shiva", report, null, "Meera", string.Empty, "normal").Body, StringComparison.Ordinal);
        LajnaAsk salary = LajnaRequest.Parse("email hr asking for a salary certificate")!;
        LajnaDraft certificate = LajnaLetters.Write(string.Empty, salary.Who, salary, null, "Arun", string.Empty, "normal");
        Assert.Equal("Request: a salary certificate", certificate.Subject);
        Assert.Contains("I would like to request a salary certificate.", certificate.Body, StringComparison.Ordinal);
        LajnaAsk thanks = LajnaRequest.Parse("I need to send a mail to Meera thanking her for the help")!;
        Assert.Contains("Thank you for the help.", LajnaLetters.Write("meera@anjal.co.in", "Meera Iyer", thanks, null, "Arun", string.Empty, "normal").Body, StringComparison.Ordinal);
        LajnaAsk surgery = LajnaRequest.Parse("Leave application to my manager for 3 days from next Monday because of my father's surgery")!;
        Assert.Contains("I would like to request leave for 3 days from next Monday due to my father's surgery.", LajnaLetters.Write(string.Empty, surgery.Who, surgery, null, "Arun", string.Empty, "normal").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGreeting_FitsWhoItIsFor()
    {
        static string Dear(string words, string to = "") => LajnaLetters.Write(to, LajnaRequest.Parse(words)!.Who, LajnaRequest.Parse(words)!, null, "Arun", string.Empty, "normal").Body.Split('\n')[0];
        Assert.Equal("Dear Dr Rao,", Dear("write to Dr Rao that I will cover his OPD on Monday"));
        Assert.Equal("Dear Rao,", Dear("remind Rao about the meeting at 4"));
        Assert.Equal("Dear HR team,", Dear("need leave letter to hr for tomorrow"));
        Assert.Equal("Dear Hospital Administrator,", Dear("I have to write a mail to the hospital administrator requesting a duty change on Saturday and Sunday"));
        Assert.Equal("Dear Meera and Rao,", Dear("send mail to Meera and Rao saying the OT is booked for Monday"));
        Assert.Equal("Dear Accounts team,", Dear("write a mail to accounts team asking when salary will be credited"));
    }

    [Fact]
    public void TheKindOfLeave_AndBeingUnwell_AreKept()
    {
        LajnaAsk casual = LajnaRequest.Parse("write a casual leave application to hr for 26 oct")!;
        LajnaDraft draft = LajnaLetters.Write(string.Empty, casual.Who, casual, null, "Arun", string.Empty, "normal");
        Assert.Equal("Casual leave request: 26 Oct", draft.Subject);
        Assert.Contains("I would like to request casual leave on 26 Oct.", draft.Body, StringComparison.Ordinal);
        LajnaAsk sick = LajnaRequest.Parse("send a mail to my manager saying I'm sick today and won't come")!;
        Assert.Contains("I would like to request leave today, as I am unwell.", LajnaLetters.Write(string.Empty, sick.Who, sick, null, "Arun", string.Empty, "normal").Body, StringComparison.Ordinal);
        LajnaAsk kabir = LajnaRequest.Parse("Leave letter to class teacher, my son Kabir has fever today")!;
        Assert.Contains("my son Kabir will be absent from school today, as he is unwell.", LajnaLetters.Write(string.Empty, kabir.Who, kabir, null, "Arun", string.Empty, "normal").Body, StringComparison.Ordinal);
    }

    private async Task<string> AskAsync(string words) =>
        WebUtility.HtmlDecode(await this.client.GetStringAsync(new Uri("lajna/answer?q=" + Uri.EscapeDataString(words) + "&page=/", UriKind.Relative)));
}
