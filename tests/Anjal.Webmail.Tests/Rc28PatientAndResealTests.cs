using System.Net;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.28 (DES-18 §8, D-331): the patient warning sends only the mail the person was warned about, names
/// the sent mail's Message-ID, and records "Change the mail"; a mail taken back before it went is
/// recorded too. DES-18 §11 (D-264): what was kept before rc.25 is sealed to its organisation once,
/// the manifest chain still proves it, and an individual's original is left as it is.
/// </summary>
public sealed class Rc28PatientAndResealTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] Signs = { "patient", "priya@gmail.test", "the word patient" };
    private static readonly string[] OtherSigns = { "patient", "priya@gmail.test", "the word patient", "the word prescription" };
    private static readonly string[] ToArun = { "arun@anjal.co.in" };
    private static readonly string[] ToMeena = { "meena@anjalmail.com" };
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly Regex SealRegex = new("name=\"patientOk\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant);

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc28p-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private readonly List<HttpClient> clients = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private string baseAddress = string.Empty;
    private TenantRow tenant = new();
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
        this.maildir = new MaildirStore(Path.Combine(this.root, "mail"), "test");
        this.tenant = await this.store.UpsertTenantAsync(new TenantRow { Slug = "imagiqa", DisplayName = "Imagiqa", PostmasterMailbox = "arun@anjal.co.in" });
        await this.store.UpsertTenantDomainAsync(new TenantDomainRow { TenantId = this.tenant.Id, Domain = "anjal.co.in" });
        this.arun = await this.store.UpsertMailboxAsync(new MailboxRow
        {
            TenantId = this.tenant.Id,
            LocalPart = "arun",
            Domain = "anjal.co.in",
            DisplayName = "Arun Shiva B",
            PasswordPbkdf2 = Pbkdf2Hasher.Hash("correct horse battery", 1000),
        });
        this.app = Program.CreateApp(Array.Empty<string>(), this.store, this.maildir, "anjal.localhost", "http://127.0.0.1:0");
        await this.app.StartAsync();
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

    [Fact]
    public void TheSeal_OpensOnlyForTheSignsItWasMadeFor()
    {
        var protection = new EphemeralDataProtectionProvider();
        string seal = PatientWarningSeal.Seal(protection, Signs);
        Assert.True(PatientWarningSeal.Matches(protection, seal, Signs));
        Assert.False(PatientWarningSeal.Matches(protection, seal, OtherSigns));
        Assert.False(PatientWarningSeal.Matches(protection, "1", Signs));
        Assert.False(PatientWarningSeal.Matches(protection, string.Empty, Signs));
        Assert.False(PatientWarningSeal.Matches(new EphemeralDataProtectionProvider(), seal, Signs));
        Assert.Equal(Signs, PatientWarningSeal.Open(protection, seal));
        Assert.Empty(PatientWarningSeal.Open(protection, "not a seal"));
        Assert.DoesNotContain("priya", seal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAnyway_SendsOnlyWhatWasWarnedAbout_AndNamesTheMail_ChangeIsRecorded()
    {
        HttpClient c = this.Client();
        await SignInAsync(c);

        // 1. The first Send stops at the warning, and the mail waits as a draft.
        HttpResponseMessage first = await ComposeAsync(c, "Discharge summary for the patient", "The patient goes home today.", seal: null);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        string warned = first.Headers.Location!.OriginalString;
        Assert.Contains("patient=", warned, StringComparison.Ordinal);
        Assert.Single(await this.Log("webmail.patient-warning.shown"));

        // 2. The warning's page carries the seal; something new in the mail brings the warning back (C).
        string seal = SealRegex.Match(await c.GetStringAsync(warned.TrimStart('/'))).Groups[1].Value;
        Assert.NotEqual(string.Empty, seal);
        Assert.NotEqual("1", seal);
        HttpResponseMessage changed = await ComposeAsync(c, "Discharge summary for the patient", "The patient goes home today. Her prescription is attached.", WebUtility.HtmlDecode(seal));
        Assert.Contains("patient=", changed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(2, (await this.Log("webmail.patient-warning.shown")).Count);
        Assert.Empty(await this.Log("webmail.patient-warning.sent"));

        // 3. "Change the mail" (D): recorded, back to the draft, nothing sent.
        string seal2 = SealRegex.Match(await c.GetStringAsync(changed.Headers.Location!.OriginalString.TrimStart('/'))).Groups[1].Value;
        HttpResponseMessage back = await ComposeAsync(c, "Discharge summary for the patient", "The patient goes home today. Her prescription is attached.", WebUtility.HtmlDecode(seal2), ("patientChanged", "1"));
        Assert.StartsWith("/draft/", back.Headers.Location!.OriginalString, StringComparison.Ordinal);
        AuditEvent change = Assert.Single(await this.Log("webmail.patient-warning.changed"));
        Assert.Equal("gmail.test", change.Subject);

        // 4. The same mail, sent anyway: it goes, and the line names its Message-ID (B).
        HttpResponseMessage sent = await ComposeAsync(c, "Discharge summary for the patient", "The patient goes home today. Her prescription is attached.", WebUtility.HtmlDecode(seal2));
        Assert.Equal("/folder/Sent?sent=1", sent.Headers.Location!.OriginalString);
        AuditEvent line = Assert.Single(await this.Log("webmail.patient-warning.sent"));
        Assert.Matches("· <[0-9a-f]{32}@anjal\\.co\\.in>$", line.Detail);
        Assert.DoesNotContain("goes home", line.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMailTakenBack_BeforeItWent_IsRecordedWithItsMessageId()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        var request = new ComposeRequest { To = "priya@gmail.test", Subject = "Rota", Body = "See you at 10." };
        (string? error, Guid? held) = await svc.HoldAsync(this.arun.Id, request, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(error);
        Assert.NotNull(await svc.UnholdAsync(this.arun.Id, held!.Value, "arun@anjal.co.in"));
        AuditEvent back = Assert.Single(await this.Log("webmail.send.taken-back"));
        Assert.Equal("arun@anjal.co.in", back.Actor);
        Assert.Equal(request.SentMessageId, back.Subject);
    }

    [Fact]
    public async Task OldEvidence_IsSealedOnce_ToItsOrganisation_AndTheChainStillProvesIt()
    {
        string evidenceRoot = Path.Combine(this.root, "evidence");
        var vault = new EvidenceVault(evidenceRoot);
        DateTimeOffset kept = DateTimeOffset.UtcNow.AddDays(-20);
        var plain = new EvidenceRecorder(this.store, vault);
        Guid org = await plain.RecordInboundAsync(new InboundEvidence { RawBytes = "Subject: Rota\r\n\r\nSee you at 10.\r\n"u8.ToArray(), EnvelopeFrom = "priya@gmail.test", EnvelopeTo = ToArun, ReceivedAt = kept });
        Guid individual = await plain.RecordInboundAsync(new InboundEvidence { RawBytes = "Subject: Hello\r\n\r\nHi.\r\n"u8.ToArray(), EnvelopeFrom = "priya@gmail.test", EnvelopeTo = ToMeena, ReceivedAt = kept });
        var worker = new EvidenceWorker(this.store, vault, () => DateTimeOffset.UtcNow);
        await worker.RunOnceAsync();
        EvidenceRow before = (await this.store.GetEvidenceAsync(org))!;

        var keys = new FixedKeys(MailKeys.NewKeyPair());
        var resealer = new EvidenceResealer(this.store, vault, keys, this.store);

        // A dry run counts and changes nothing.
        ResealResult dry = await resealer.RunAsync(dryRun: true);
        Assert.Equal(1, dry.Sealed);
        Assert.Equal(1, dry.Individuals);
        Assert.Equal(before.Sha256, (await this.store.GetEvidenceAsync(org))!.Sha256);

        ResealResult done = await resealer.RunAsync(dryRun: false);
        Assert.Equal(1, done.Sealed);
        EvidenceRow after = (await this.store.GetEvidenceAsync(org))!;
        Assert.Equal(before.Sha256, after.ResealedFromSha256);
        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.EndsWith(".r1.eml", after.Path, StringComparison.Ordinal);
        Assert.True(MailEnvelope.IsEnvelope(await vault.ReadAsync(after.Path)));
        Assert.False(vault.Exists(before.Path));
        Assert.Contains(before.Path, await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(evidenceRoot, "purge-lists")).Single()), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(evidenceRoot, "manifests"), "reseal-*.txt"));

        // The individual's original is as it was; the chain of manifests still proves every original.
        Assert.Null((await this.store.GetEvidenceAsync(individual))!.ResealedAt);
        Assert.Empty(await worker.VerifyAsync());

        // A second run finds nothing to do.
        ResealResult again = await resealer.RunAsync(dryRun: false);
        Assert.Equal(0, again.Sealed);
        Assert.Equal(1, again.AlreadySealed);
    }

    [Fact]
    public async Task QueuedMailFromBefore_IsListed_AndSealedQueuedMailIsNot()
    {
        OutboundMessage old = await this.store.EnqueueOutboundAsync(new OutboundMessage { EnvelopeFrom = "arun@anjal.co.in", EnvelopeTo = "priya@gmail.test", RawBytes = "Subject: Rota\r\n\r\nHi\r\n"u8.ToArray() });
        Assert.Contains(old.Id, await this.store.ListUnsealedQueuedOutboundAsync(10));
        await this.store.ReplaceOutboundRawAsync(old.Id, OutboundCipher.Seal("Subject: Rota\r\n\r\nHi\r\n"u8.ToArray(), MailKeys.NewKeyPair().Public, null));
        Assert.DoesNotContain(old.Id, await this.store.ListUnsealedQueuedOutboundAsync(10));
    }

    [Fact]
    public async Task Swipes_StartAsArchiveSnoozeRead_AreSaved_AndReachTheList_ButOnlyFromTheList()
    {
        MailboxService svc = this.app.Services.GetRequiredService<MailboxService>();
        MailSettings first = await svc.GetMailSettingsAsync(this.arun.Id);
        Assert.Equal("archive", first.SwipeLeft);
        Assert.Equal("snooze", first.SwipeLeftLong);
        Assert.Equal("read", first.SwipeRight);

        Assert.NotNull(await svc.SetSwipesAsync(this.arun.Id, "delete-everything", "snooze", "read"));
        Assert.NotNull(await svc.SetSwipesAsync(this.arun.Id, "archive", null, "read"));
        Assert.Equal("archive", (await svc.GetMailSettingsAsync(this.arun.Id)).SwipeLeft);

        HttpClient c = this.Client();
        await SignInAsync(c);
        string token = Token(await c.GetStringAsync("settings/mail"));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["left"] = "trash",
            ["leftLong"] = "none",
            ["right"] = "flag",
        });
        HttpResponseMessage saved = await c.PostAsync("settings/swipes", form);
        Assert.Equal("/settings/mail?saved=mail", saved.Headers.Location!.OriginalString);
        MailSettings now = await svc.GetMailSettingsAsync(this.arun.Id);
        Assert.Equal(("trash", "none", "flag"), (now.SwipeLeft, now.SwipeLeftLong, now.SwipeRight));

        string list = await c.GetStringAsync("folder/INBOX");
        Assert.Contains("data-swipe-left=\"trash\"", list, StringComparison.Ordinal);
        Assert.Contains("data-swipe-long=\"none\"", list, StringComparison.Ordinal);
        Assert.Contains("data-swipe-right=\"flag\"", list, StringComparison.Ordinal);

        using var noToken = new FormUrlEncodedContent(new Dictionary<string, string> { ["left"] = "archive", ["leftLong"] = "snooze", ["right"] = "read" });
        Assert.NotEqual(HttpStatusCode.Redirect, (await c.PostAsync("settings/swipes", noToken)).StatusCode);
        Assert.Equal("trash", (await svc.GetMailSettingsAsync(this.arun.Id)).SwipeLeft);
    }

    private async Task<IReadOnlyList<AuditEvent>> Log(string action) =>
        (await this.store.ListAuditAsync(100)).Where(e => e.Action == action).ToList();

    private static async Task<HttpResponseMessage> ComposeAsync(HttpClient c, string subject, string body, string? seal, params (string Key, string Value)[] more)
    {
        string token = Token(await c.GetStringAsync("compose"));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("priya@gmail.test"), "to" },
            { new StringContent(subject), "subject" },
            { new StringContent(body), "body" },
        };
        if (seal is not null)
        {
            form.Add(new StringContent(seal), "patientOk");
        }
        foreach ((string key, string value) in more)
        {
            form.Add(new StringContent(value), key);
        }
        return await c.PostAsync("compose", form);
    }

    private static async Task SignInAsync(HttpClient c)
    {
        string token = Token(await c.GetStringAsync("sign-in"));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["address"] = "arun",
            ["password"] = "correct horse battery",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("auth/login", form)).StatusCode);
    }

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }

    private HttpClient Client()
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(this.baseAddress + "/") };
        this.clients.Add(c);
        return c;
    }

    // The organisation of anjal.co.in has one recovery key; everyone else is an individual.
    private sealed class FixedKeys : IEvidenceKeys
    {
        private readonly byte[] publicKey;

        public FixedKeys((byte[] Public, byte[] Private) pair) => this.publicKey = pair.Public;

        public Task<IReadOnlyList<byte[]>> ForAsync(string? sender, IReadOnlyCollection<string> recipients, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<byte[]>>(recipients.Append(sender ?? string.Empty).Any(a => a.EndsWith("@anjal.co.in", StringComparison.OrdinalIgnoreCase)) ? new[] { this.publicKey } : Array.Empty<byte[]>());
    }
}
