using System.IO.Compression;
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
/// rc.29 (DES-22): attachments shown in the mail window, through the real webmail and the real viewer process
/// (started on this machine, as in development). A PDF's pages, text and Find; a Word document; a workbook; the
/// files inside a ZIP, with programs blocked (AC-17); a file's password kept for the session and dropped at
/// sign-out; and /preview no longer serving anything but pictures.
/// </summary>
public sealed class Rc29ViewerTests : IAsyncLifetime, IDisposable
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private static readonly string[] Arun = new[] { "arun@anjal.co.in" };

    private readonly string root = Path.Combine(Path.GetTempPath(), "anjal-rc29v-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryMessageStore store = new();
    private MaildirStore maildir = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

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
    public async Task APdf_ShowsItsPages_ItsText_AndFind_FromTheViewer()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Haemoglobin 13.2")));
        string reader = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("class=\"pdfview\"", reader, StringComparison.Ordinal);
        Assert.Contains("data-pages=\"1\"", reader, StringComparison.Ordinal);
        Assert.Contains("Page 1 of 1", reader, StringComparison.Ordinal);
        // The label on the box (owner, 15 Nov, option A): what kind of file it is, and how many pages.
        Assert.Equal("pdf: PDF document · 1 page", Tab(reader));

        using HttpResponseMessage page = await this.client.GetAsync($"message/{m.Id}/attachment/0/view/page/0?w=300");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("image/png", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("sandbox", page.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        byte[] png = await page.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, png[0]);

        string text = await this.client.GetStringAsync($"message/{m.Id}/attachment/0/view/text/0");
        Assert.Contains("Haemoglobin", text, StringComparison.Ordinal);
        string found = await this.client.GetStringAsync($"message/{m.Id}/attachment/0/view/find?q=Haemoglobin");
        Assert.Contains("\"Hits\":[{", found, StringComparison.Ordinal);
        string none = await this.client.GetStringAsync($"message/{m.Id}/attachment/0/view/find?q=");
        Assert.Equal("{\"Hits\":[]}", none);

        // A page that does not exist, and a mail that does not exist, give nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/view/page/7")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{Guid.NewGuid()}/attachment/0/view/page/0")).StatusCode);

        // rc.29: /preview serves pictures only; a PDF is the viewer's.
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/preview")).StatusCode);
    }

    [Fact]
    public async Task AFile_OpensInTheViewer_FromTodayAndSearch_AsFromAFolder()
    {
        // The owner, 16 Nov: a file opened from Today did not show the viewer; Search had the same gap.
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Haemoglobin 13.2")));
        foreach (string path in new[] { $"dashboard/today?open={m.Id}&preview=0", $"search?q=Files&open={m.Id}&preview=0" })
        {
            string page = WebUtility.HtmlDecode(await this.client.GetStringAsync(path));
            Assert.Contains("class=\"pdfview\"", page, StringComparison.Ordinal);
            Assert.Equal("pdf: PDF document · 1 page", Tab(page));
        }
    }

    [Fact]
    public async Task AWordFile_AndAWorkbook_ShowInTheMailWindow()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(
            ("summary.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Word()),
            ("results.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Workbook()));
        string raw = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0");
        string word = WebUtility.HtmlDecode(raw);
        Assert.Contains("class=\"wordview\"", word, StringComparison.Ordinal);
        Assert.Contains("Discharge summary", word, StringComparison.Ordinal);
        Assert.Contains("class=\"wtable\"", word, StringComparison.Ordinal);
        Assert.Contains("13.2", word, StringComparison.Ordinal);
        // The document's own markup is text, never the page's.
        Assert.DoesNotContain("<script>alert", raw, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)", raw, StringComparison.Ordinal);
        // AC-24: a run's bold and italic are shown; its text stays text inside them.
        Assert.Contains("<p>Give <strong>&lt;i&gt;Paracetamol</strong><em> twice</em></p>", raw, StringComparison.Ordinal);
        Assert.Equal("doc: Word document", Tab(word));

        string sheet = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=1"));
        Assert.Contains("class=\"sheetgrid\"", sheet, StringComparison.Ordinal);
        Assert.Contains("Creatinine", sheet, StringComparison.Ordinal);
        Assert.Contains(">0.9<", sheet, StringComparison.Ordinal);
        Assert.Contains("Results", sheet, StringComparison.Ordinal);
        Assert.Equal("xls: Excel workbook · 1 sheet", Tab(sheet));
    }

    [Fact]
    public async Task ACsv_IsATable_AndANupkg_IsListedLikeAZip()
    {
        // rc.31 (D4, D3): a CSV is a plain table whose cells are never evaluated; a .nupkg (a ZIP under another
        // name) is listed like a ZIP, with the label its own extension (D2).
        await this.SignInAsync();
        byte[] nupkg = Zip(("lib/net10.0/readme.txt", Encoding.UTF8.GetBytes("inside the package")));
        MessageRow m = await this.DeliverAsync(
            ("ward.csv", "text/csv", Encoding.UTF8.GetBytes("Bed,Name,Total\n1,\"Rao, K\",=SUM(A1:A9)\n")),
            ("Chuvadi.Pdf.3.34.0.nupkg", "application/octet-stream", nupkg));
        string raw = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0");
        string table = WebUtility.HtmlDecode(raw);
        Assert.Contains("class=\"sheetgrid\"", table, StringComparison.Ordinal);
        Assert.Contains(">Rao, K<", table, StringComparison.Ordinal);
        Assert.Contains(">=SUM(A1:A9)<", table, StringComparison.Ordinal);
        Assert.Equal("xls: Table (CSV) · 2 rows", Tab(table));
        Assert.Contains(">CSV</span>", table, StringComparison.Ordinal);
        Assert.Contains(">NUPKG</span>", table, StringComparison.Ordinal);

        string listing = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=1"));
        Assert.Contains("class=\"ziptable\"", listing, StringComparison.Ordinal);
        Assert.Contains("readme.txt", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZip_ListsItsFiles_AndOpensThem()
    {
        await this.SignInAsync();
        // A ZIP holding a program never arrives (the mail server refuses it, D-173); the viewer's own refusal of
        // programs inside is tested in Anjal.Viewer.Tests.
        byte[] zip = Zip(
            ("notes.txt", Encoding.UTF8.GetBytes("plain notes")),
            ("report.pdf", SmallPdf("Inside the archive")));
        MessageRow m = await this.DeliverAsync(("files.zip", "application/zip", zip));
        string listing = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("class=\"ziptable\"", listing, StringComparison.Ordinal);
        Assert.Contains("notes.txt", listing, StringComparison.Ordinal);
        Assert.Contains("report.pdf", listing, StringComparison.Ordinal);
        Assert.Contains("Programs are blocked and cannot be taken out.", listing, StringComparison.Ordinal);
        Assert.Equal("zip: ZIP archive · 2 files", Tab(listing));

        // A file inside downloads as it is; an entry that is not there gives nothing.
        using HttpResponseMessage notes = await this.client.GetAsync($"message/{m.Id}/attachment/0/zip?entry=0");
        Assert.Equal(HttpStatusCode.OK, notes.StatusCode);
        Assert.Equal("application/octet-stream", notes.Content.Headers.ContentType!.MediaType);
        Assert.Equal("plain notes", await notes.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/zip?entry=5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await this.client.GetAsync($"message/{m.Id}/attachment/0/zip/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/zip/9")).StatusCode);

        // The PDF inside opens in the same viewer, with the way back to the archive.
        string inner = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0&entry=1"));
        Assert.Contains("class=\"pdfview\"", inner, StringComparison.Ordinal);
        Assert.Contains("Back to the archive", inner, StringComparison.Ordinal);
        Assert.Contains("data-entry=\"1\"", inner, StringComparison.Ordinal);
        // A file inside says which archive it came from.
        Assert.Equal("pdf: PDF document · 1 page · in files.zip", Tab(inner));
        Assert.Contains("Inside the archive", await this.client.GetStringAsync($"message/{m.Id}/attachment/0/view/text/0?entry=1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockedZip_AsksForItsPassword_ShowsItsFiles_AndGivesNoneOut()
    {
        await this.SignInAsync();
        byte[] zip = Chuvadi.Zip.ZipWriter.ToBytes(
            w =>
            {
                w.AddBytes("scan-report.pdf", SmallPdf("Locked report"));
                w.AddBytes("notes.txt", Encoding.UTF8.GetBytes("private"));
            },
            new Chuvadi.Zip.ZipWriterOptions { Password = "lotus-42" });
        MessageRow m = await this.DeliverAsync(("locked.zip", "application/zip", zip));

        // Asked for, as a PDF's is: a form for the archive itself (no entry).
        string asked = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("This archive has a password.", asked, StringComparison.Ordinal);
        Assert.Contains("name=\"entry\" value=\"\"", asked, StringComparison.Ordinal);
        // Before the password, the label says only what the file is.
        Assert.Equal("zip: ZIP archive", Tab(asked));

        // A wrong one, then the right one.
        using (HttpResponseMessage wrong = await this.PostAsync($"message/{m.Id}/attachment/0/view/password", Token(asked), ("password", "tulip"), ("entry", string.Empty)))
        {
            Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        }
        string again = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("That password does not open it. Try again.", again, StringComparison.Ordinal);
        using (HttpResponseMessage right = await this.PostAsync($"message/{m.Id}/attachment/0/view/password", Token(again), ("password", "lotus-42"), ("entry", string.Empty)))
        {
            Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        }

        // Listed, with the notice, and no file offered for download one by one (AC-20).
        string listing = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0"));
        Assert.Contains("class=\"ziptable\"", listing, StringComparison.Ordinal);
        Assert.Contains("scan-report.pdf", listing, StringComparison.Ordinal);
        Assert.Contains("These files were not virus-scanned", listing, StringComparison.Ordinal);
        Assert.DoesNotContain($"/attachment/0/zip?entry=", listing, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await this.client.GetAsync($"message/{m.Id}/attachment/0/zip?entry=1")).StatusCode);

        // A file inside opens in the viewer, marked; the whole archive still downloads as it came.
        string inner = WebUtility.HtmlDecode(await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0&entry=0"));
        Assert.Contains("class=\"pdfview\"", inner, StringComparison.Ordinal);
        Assert.Contains("This file was not virus-scanned", inner, StringComparison.Ordinal);
        Assert.Contains("Locked report", await this.client.GetStringAsync($"message/{m.Id}/attachment/0/view/text/0?entry=0"), StringComparison.Ordinal);
        Assert.Equal(zip, await this.client.GetByteArrayAsync($"message/{m.Id}/attachment/0"));
    }

    [Fact]
    public async Task AFilesPassword_IsKeptForTheSession_AndDroppedAtSignOut()
    {
        await this.SignInAsync();
        MessageRow m = await this.DeliverAsync(("report.pdf", "application/pdf", SmallPdf("Locked or not")));
        ViewerAccess viewer = this.app.Services.GetRequiredService<ViewerAccess>();
        string page = await this.client.GetStringAsync($"folder/INBOX?open={m.Id}&preview=0");

        // The password form: kept, and the person sent back to the page - only a page of this webmail.
        using HttpResponseMessage sent = await this.PostAsync($"message/{m.Id}/attachment/0/view/password", Token(page), ("password", "lotus"), ("back", "//elsewhere.test/x"));
        Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
        Assert.Equal($"/message/{m.Id:D}", sent.Headers.Location!.ToString());
        Assert.Equal(1, viewer.SessionsKept);

        // Without the page's token, nothing is kept.
        using HttpResponseMessage bare = await this.client.PostAsync($"message/{m.Id}/attachment/0/view/password", new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "x" }));
        Assert.NotEqual(HttpStatusCode.OK, bare.StatusCode);

        using HttpResponseMessage signOut = await this.PostAsync("sign-out", Token(await this.client.GetStringAsync("folder/INBOX")));
        Assert.Equal(0, viewer.SessionsKept);
        Assert.Equal(HttpStatusCode.Redirect, (await this.client.GetAsync($"message/{m.Id}/attachment/0/view/page/0")).StatusCode);
    }

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

    // The label on the viewer's box as "kind: words · facts" (its markup and spacing dropped), or "" when there is none.
    private static string Tab(string page)
    {
        int start = page.IndexOf("class=\"pvtab pvtab-", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }
        int kindStart = start + "class=\"pvtab pvtab-".Length;
        string kind = page[kindStart..page.IndexOf('"', kindStart)];
        int body = page.IndexOf('>', start) + 1;
        int end = page.IndexOf("<div class=\"pvbody", body, StringComparison.Ordinal);
        string text = Regex.Replace(Regex.Replace(page[body..end], "<[^>]+>", " "), "\\s+", " ").Trim();
        return kind + ": " + text;
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

    // One A4 page with one line of text and a correct cross-reference table (as tools/viewer_check.py makes it).
    private static byte[] SmallPdf(string line)
    {
        string stream = "BT /F1 24 Tf 72 720 Td (" + line + ") Tj ET";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>\nstream\n" + stream + "\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            pdf.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // A Word file written by hand: a heading, a paragraph holding markup as text, a paragraph with a bold run that also
    // holds markup as text (AC-24), and a table.
    private static byte[] Word()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static string P(string style, string text) =>
            "<w:p>" + (style.Length > 0 ? "<w:pPr><w:pStyle w:val=\"" + style + "\"/></w:pPr>" : string.Empty) + "<w:r><w:t xml:space=\"preserve\">" + text + "</w:t></w:r></w:p>";
        static string Cell(string text) => "<w:tc><w:p><w:r><w:t>" + text + "</w:t></w:r></w:p></w:tc>";
        string body = P("Heading1", "Discharge summary") + P(string.Empty, "&lt;script&gt;alert(1)&lt;/script&gt;") +
            "<w:p><w:r><w:t xml:space=\"preserve\">Give </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>&lt;i&gt;Paracetamol</w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t xml:space=\"preserve\"> twice</w:t></w:r></w:p>" +
            "<w:tbl><w:tr>" + Cell("Test") + Cell("Result") + "</w:tr><w:tr>" + Cell("Hb") + Cell("13.2") + "</w:tr></w:tbl>" + P(string.Empty, "After the table");
        return Zip(
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")),
            ("_rels/.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")),
            ("word/document.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"" + W + "\"><w:body>" + body + "</w:body></w:document>")));
    }

    // A workbook written by hand: one sheet, "Results", with inline strings and numbers.
    private static byte[] Workbook()
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static string Text(string cell, string text) => "<c r=\"" + cell + "\" t=\"inlineStr\"><is><t>" + text + "</t></is></c>";
        string rows = "<row r=\"1\">" + Text("A1", "Test") + Text("B1", "Result") + "</row>" +
            "<row r=\"2\">" + Text("A2", "Creatinine") + "<c r=\"B2\"><v>0.9</v></c></row>";
        return Zip(
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>")),
            ("_rels/.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>")),
            ("xl/workbook.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"" + S + "\" xmlns:r=\"" + R + "\"><sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>")),
            ("xl/worksheets/sheet1.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"" + S + "\"><sheetData>" + rows + "</sheetData></worksheet>")));
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
