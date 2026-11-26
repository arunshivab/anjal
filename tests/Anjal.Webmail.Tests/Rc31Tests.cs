using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Anjal.Viewing;
using Anjal.Webmail.Services;
using Microsoft.AspNetCore.Http;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.31 (the owner's first-server check, 25 Nov 2026): the file label as the real extension at one size (D1, D2),
/// a ZIP under another name listed like one (D3), CSV shown as a table (D4), a busy viewer answered "try again" so the
/// editor's pages come back (D6), and the hand cursor on every control (D9).
/// </summary>
public sealed class Rc31Tests
{
    private static readonly (string Name, string? Type, string Tag, string Kind)[] Tags =
    {
        ("report.pdf", "application/pdf", "PDF", "pdf"),
        ("letter.docx", null, "DOCX", "doc"),
        ("old.doc", null, "DOC", "doc"),
        ("sheet.xlsx", null, "XLSX", "xls"),
        ("list.csv", "text/csv", "CSV", "xls"),
        ("deck.pptx", null, "PPTX", "ppt"),
        ("Chuvadi.Pdf.3.34.0.nupkg", null, "NUPKG", "other"),
        ("photo.JPEG", null, "JPEG", "img"),
        ("study.zip", null, "ZIP", "zip"),
        ("scan.dcm", null, "DCM", "dicom"),
        ("DICOMDIR", null, "DICOM", "dicom"),
        ("IM0001", "application/dicom", "DCM", "dicom"),
        ("noname", "application/pdf", "PDF", "pdf"),
        ("README", null, "FILE", "other"),
        ("archive.tar.gz", null, "GZ", "zip"),
        ("notes.markdown", null, "MARKD", "other"),
    };

    private static readonly string[] OwnZips = { "a.zip", "a.docx", "a.XLSX", "a.pptx", "a.odt", "a.epub" };

    [Fact]
    public void TheLabel_IsTheRealExtensionInCapitals_UpToFiveLetters()
    {
        foreach ((string name, string? type, string tag, string kind) in Tags)
        {
            Assert.Equal((tag, kind), FileTag.Of(name, type));
        }
    }

    [Fact]
    public void TheBrowsersLabel_FollowsTheSameRule()
    {
        // app.js's fileTag is run in Node on the same names; the two must give the same tag and kind. Node is on every
        // CI runner; where it is missing the browser's rule is still checked by the screen checks.
        string? node = Which("node");
        if (node is null)
        {
            return;
        }
        string js = File.ReadAllText(Path.Combine(Root(), "src", "Anjal.Webmail", "wwwroot", "app.js"));
        Match m = Regex.Match(js, @"var fileTag = function \(name, type\) \{.*?\n  \};", RegexOptions.Singleline);
        Assert.True(m.Success, "fileTag was not found in app.js");
        var script = new StringBuilder(m.Value).Append('\n');
        foreach ((string name, string? type, _, _) in Tags)
        {
            script.Append("console.log(fileTag(").Append(System.Text.Json.JsonSerializer.Serialize(name)).Append(", ").Append(System.Text.Json.JsonSerializer.Serialize(type ?? string.Empty)).Append(").join(' '));\n");
        }
        string file = Path.Combine(Path.GetTempPath(), "anjal-filetag-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(file, script.ToString());
        try
        {
            using Process p = Process.Start(new ProcessStartInfo(node, new[] { file }) { RedirectStandardOutput = true, UseShellExecute = false })!;
            string[] lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            p.WaitForExit();
            Assert.Equal(Tags.Length, lines.Length);
            for (int i = 0; i < Tags.Length; i++)
            {
                Assert.Equal(Tags[i].Tag + " " + Tags[i].Kind, lines[i].TrimEnd('\r'));
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void EveryLabel_IsOneSize()
    {
        string css = File.ReadAllText(Path.Combine(Root(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        Match rule = Regex.Match(css, @"\.ftag, \.attrow \.ftag, \.pvfile \.ftag \{([^}]*)\}");
        Assert.True(rule.Success);
        Assert.Contains("flex: 0 0 42px", rule.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("width: 42px", rule.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("height: 28px", rule.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AZipUnderAnotherName_IsListedLikeOne_ButOfficeFilesKeepTheirViewers()
    {
        byte[] zip = [(byte)'P', (byte)'K', 3, 4, 20, 0];
        byte[] empty = [(byte)'P', (byte)'K', 5, 6, 0, 0];
        Assert.True(MailboxService.LooksLikeZip("Chuvadi.Pdf.3.34.0.nupkg", zip));
        Assert.True(MailboxService.LooksLikeZip("bundle", empty));
        Assert.True(MailboxService.LooksLikeZip("app.jar", zip));
        foreach (string own in OwnZips)
        {
            Assert.False(MailboxService.LooksLikeZip(own, zip));
        }
        Assert.False(MailboxService.LooksLikeZip("a.nupkg", "%PDF"u8));
        Assert.False(MailboxService.LooksLikeZip("a.nupkg", "PK"u8));
        Assert.Equal(MailboxService.ZipType, MailboxService.PreviewType(new AttachmentView { FileName = "x.nupkg", ZipInside = true }));
        // Before the DICOM rule, which claims a file with no extension.
        Assert.Equal(MailboxService.ZipType, MailboxService.PreviewType(new AttachmentView { FileName = "bundle", ZipInside = true }, dicom: true));
        Assert.Null(MailboxService.PreviewType(new AttachmentView { FileName = "x.nupkg" }));
        Assert.Equal(MailboxService.WordType, MailboxService.PreviewType(new AttachmentView { FileName = "x.docx" }));
    }

    [Fact]
    public void ACsvOrTsv_IsShownAsATable()
    {
        Assert.Equal(MailboxService.TableType, MailboxService.PreviewType(new AttachmentView { FileName = "ward.csv", ContentType = "text/plain" }));
        Assert.Equal(MailboxService.TableType, MailboxService.PreviewType(new AttachmentView { FileName = "ward.tsv" }));
        Assert.Equal(MailboxService.TableType, MailboxService.PreviewType(new AttachmentView { FileName = "export", ContentType = "text/csv" }));
    }

    [Fact]
    public void ABusyViewer_IsAnsweredTryAgain_NotNotFound()
    {
        var http = new DefaultHttpContext();
        IResult busy = ViewerEndpoints.NotShown(http, new ViewResult(new ViewAnswer { Status = ViewStatus.Busy }, Array.Empty<byte[]>()));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(busy).StatusCode);
        Assert.Equal("1", http.Response.Headers.RetryAfter.ToString());
        IResult gone = ViewerEndpoints.NotShown(new DefaultHttpContext(), new ViewResult(new ViewAnswer { Status = ViewStatus.Damaged }, Array.Empty<byte[]>()));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(gone).StatusCode);
    }

    [Fact]
    public void ThePagesText_IsAskedForOneAtATime_AndABrokenPageIsAskedForAgain()
    {
        string js = File.ReadAllText(Path.Combine(Root(), "src", "Anjal.Webmail", "wwwroot", "app.js"));
        Assert.Contains("var textQueue = [], textBusy = false;", js, StringComparison.Ordinal);
        Assert.Contains("r.status === 503", js, StringComparison.Ordinal);
        Assert.Contains("img.closest(\".pdfview, .pvbody\")", js, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryControl_ShowsTheHand_AndAButtonsWordsCannotBeSelected()
    {
        string css = File.ReadAllText(Path.Combine(Root(), "src", "Anjal.Webmail", "wwwroot", "app.css"));
        Assert.Contains("summary, [role=\"button\"] { cursor: pointer; user-select: none; -webkit-user-select: none; }", css, StringComparison.Ordinal);
        Assert.Contains("border: 0; cursor: pointer; user-select: none;", css, StringComparison.Ordinal);
        Match btn = Regex.Match(css, @"\n\.btn \{([^}]*)\}");
        Assert.True(btn.Success);
        Assert.Contains("cursor: pointer", btn.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("user-select: none", btn.Groups[1].Value, StringComparison.Ordinal);
    }

    private static string? Which(string program)
    {
        string[] names = OperatingSystem.IsWindows() ? new[] { program + ".exe", program + ".cmd" } : new[] { program };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (string n in names)
            {
                string full = Path.Combine(dir, n);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }

    private static string Root()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Directory.Build.props")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("the repository's root");
        }
        return dir;
    }
}
