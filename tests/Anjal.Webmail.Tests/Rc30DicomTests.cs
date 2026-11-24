using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Anjal.Mailbox;
using Anjal.Smtp;
using Anjal.Store;
using Anjal.Viewer.Tests;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.30 (DES-22 §3.4, §18.3; D-372, AC-19, D-386, D-366): DICOM through the real webmail and viewer, on SIAARA
/// v0.1.0-rc.1 - off unless switched on; a file's picture and caption with no patient's name or ID; Show patient
/// details for the mailbox's own person, logged; a ZIP of slices as a study and a series to scroll through.
/// </summary>
public sealed class Rc30DicomTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc30d-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    private ViewerAccess Viewer => this.app.Services.GetRequiredService<ViewerAccess>();

    public void Dispose() => this.client?.Dispose();

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
    public async Task Dicom_IsOff_UnlessSwitchedOn()
    {
        Assert.False(this.Viewer.DicomOn);
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("scan.dcm", "application/dicom", TestDicom.Slice("1", 1, 0)), ("study.zip", "application/zip", Study()));
        string reader = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.DoesNotContain("class=\"dicomview\"", reader, StringComparison.Ordinal);
        using HttpResponseMessage picture = await this.client.GetAsync($"message/{m.Id}/attachment/0/view/dicom/0");
        Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
        string zip = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=1"));
        Assert.Contains("class=\"pvzip\"", zip, StringComparison.Ordinal);
        Assert.DoesNotContain("View as a study", zip, StringComparison.Ordinal);
        string study = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=1&study=1"));
        Assert.DoesNotContain("dicomstudy", study, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFile_ShowsItsPictureAndCaption_AndItsPatientOnlyWhenAsked_Logged()
    {
        this.Viewer.DicomOn = true;
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("scan.dcm", "application/dicom", TestDicom.Slice("1", 1, 0, size: 64, frames: 3)));
        string reader = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("class=\"dicomview\"", reader, StringComparison.Ordinal);
        Assert.Contains("A preview, not for diagnosis.", reader, StringComparison.Ordinal);
        Assert.Contains($"/message/{m.Id}/attachment/0/view/dicom/0", reader, StringComparison.Ordinal);
        Assert.Contains($"/message/{m.Id}/attachment/0/view/dicom/1", reader, StringComparison.Ordinal);
        Assert.Contains("CT · ", reader, StringComparison.Ordinal);
        Assert.Contains("3 frames", reader, StringComparison.Ordinal);
        Assert.Contains("Show patient details", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("PATIENT", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("TEST-0001", reader, StringComparison.Ordinal);
        foreach (int part in new[] { 0, 1 })
        {
            using HttpResponseMessage picture = await this.client.GetAsync($"message/{m.Id}/attachment/0/view/dicom/{part}");
            Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
            Assert.Equal("image/png", picture.Content.Headers.ContentType!.MediaType);
        }
        Assert.Empty(await this.PatientLogAsync());

        string shown = WebUtility.HtmlDecode(await this.client.GetStringAsync(PatientLink(reader, $"folder/INBOX?open={m.Id}&preview=0")));
        Assert.Contains("TEST, PATIENT", shown, StringComparison.Ordinal);
        Assert.Contains("TEST-0001", shown, StringComparison.Ordinal);
        Assert.Contains("Shown to you only, and logged.", shown, StringComparison.Ordinal);
        AuditEvent logged = Assert.Single(await this.PatientLogAsync());
        Assert.Equal($"{m.Id:D}/0", logged.Subject);
        Assert.DoesNotContain("TEST", logged.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZipOfSlices_IsAStudy_AndASeriesScrollsOneSliceAtATime()
    {
        this.Viewer.DicomOn = true;
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("study.zip", "application/zip", Study()));
        string listing = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("View as a study", listing, StringComparison.Ordinal);

        string study = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0&study=1"));
        Assert.Contains("class=\"dicomstudy\"", study, StringComparison.Ordinal);
        Assert.Contains("Patient 1", study, StringComparison.Ordinal);
        Assert.Contains("3 slices", study, StringComparison.Ordinal);
        Assert.Contains("2 slices", study, StringComparison.Ordinal);
        Assert.Contains("Head", study, StringComparison.Ordinal);
        Assert.DoesNotContain("PATIENT", study, StringComparison.Ordinal);
        Match first = Regex.Match(study, "href=\"([^\"]*&study=1&series=(\\d+)&slice=1)\"");
        Assert.True(first.Success);

        string raw = await this.client.GetStringAsync(WebUtility.HtmlDecode(first.Groups[1].Value).TrimStart('/'));
        string series = WebUtility.HtmlDecode(raw);
        Assert.Contains("data-dicom-series", series, StringComparison.Ordinal);
        Assert.Contains("Slice 1 of", series, StringComparison.Ordinal);
        Assert.Contains("Back to the study", series, StringComparison.Ordinal);
        string entries = WebUtility.HtmlDecode(Regex.Match(raw, "data-entries=\"([^\"]+)\"").Groups[1].Value);
        string[] places = System.Text.Json.JsonSerializer.Deserialize<string[]>(entries)!;
        Assert.InRange(places.Length, 2, 3);
        // Each slice is asked for on its own, when it is shown.
        foreach (string place in places)
        {
            using HttpResponseMessage slice = await this.client.GetAsync($"message/{m.Id}/attachment/0/view/dicom/0?entry={place}");
            Assert.Equal("image/png", slice.Content.Headers.ContentType!.MediaType);
        }
        string last = WebUtility.HtmlDecode(await this.client.GetStringAsync(first.Groups[1].Value.TrimStart('/').Replace("&slice=1", "&slice=99", StringComparison.Ordinal)));
        Assert.Contains($"Slice {places.Length} of {places.Length}", last, StringComparison.Ordinal);

        // Show patient details on the series: the slice's own patient, logged with the file's place.
        string who = WebUtility.HtmlDecode(await this.client.GetStringAsync(PatientLink(series, first.Groups[1].Value.TrimStart('/'))));
        Assert.Contains("TEST, PATIENT", who, StringComparison.Ordinal);
        Assert.Equal($"{m.Id:D}/0/{places[0]}", Assert.Single(await this.PatientLogAsync()).Subject);
    }

    // The page's own "Show patient details" link, followed as a person clicks it (owner, 20 Nov 2026: a bracket left
    // "&patient=1" out of the link, and only typing the address worked); it is the page's address with "&patient=1".
    private static string PatientLink(string page, string expected)
    {
        Match link = Regex.Match(page, "<a [^>]*href=\"([^\"]+)\"[^>]*>Show patient details</a>");
        Assert.True(link.Success);
        string href = WebUtility.HtmlDecode(link.Groups[1].Value);
        Assert.EndsWith("&patient=1", href, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", href, StringComparison.Ordinal);
        foreach (string part in expected.Split('?')[1].Split('&'))
        {
            Assert.Contains(part, href, StringComparison.Ordinal);
        }
        return href.TrimStart('/');
    }

    private async Task<List<AuditEvent>> PatientLogAsync() =>
        (await this.store.ListAuditAsync(100)).Where(a => a.Action == "webmail.dicom.patient-details").ToList();

    // A CT series of three slices out of order, an MR series of two, and a note.
    private static byte[] Study() => Zip(
        ("study/ct-3.dcm", TestDicom.Slice("1", 3, 30)),
        ("study/ct-1.dcm", TestDicom.Slice("1", 1, 10)),
        ("study/notes.txt", Encoding.UTF8.GetBytes("Seen in clinic.")),
        ("study/ct-2.dcm", TestDicom.Slice("1", 2, 20)),
        ("study/mr-1.dcm", TestDicom.Slice("2", 1, 0, modality: "MR", description: "Head")),
        ("study/mr-2.dcm", TestDicom.Slice("2", 2, 5, modality: "MR", description: "Head")));

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

    private static string Token(string html)
    {
        Match m = TokenRegex.Match(html);
        Assert.True(m.Success, "no antiforgery token");
        return m.Groups[1].Value;
    }

    private async Task<MessageRow> DeliverAsync(params (string Name, string Type, byte[] Body)[] files)
    {
        var raw = new StringBuilder("From: lab@x.test\r\nSubject: Files\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nsee attached\r\n");
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
            EnvelopeFrom = "lab@x.test",
            EnvelopeTo = Arun,
            RawBytes = Encoding.UTF8.GetBytes(raw.ToString()),
        });
        return this.store.MailboxMessages[this.store.MailboxMessages.Count - 1];
    }

    private static byte[] Zip(params (string Name, byte[] Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] body) in entries)
            {
                using Stream s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(body);
            }
        }
        return ms.ToArray();
    }
}
