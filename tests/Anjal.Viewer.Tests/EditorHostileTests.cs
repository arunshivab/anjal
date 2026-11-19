using System.Globalization;
using System.Text;
using Anjal.Viewing;
using Chuvadi.Pdf.Documents;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.29 part 2 (DES-22 §13, L-15): the editor's write paths run over hostile files. The library has not fuzzed its
/// write paths, so Anjal does: every step - check, arrange, and a save carrying every kind of change - over each
/// file, where the only outcomes allowed are a saved file that opens again, or a refusal in words; never an exception
/// out of the job (the child's deadline and memory cap are tested apart, in SupervisorTests).
/// </summary>
public sealed class EditorHostileTests
{
    private static readonly string[] Allowed =
    {
        ViewStatus.Ok, ViewStatus.NeedsPassword, ViewStatus.WrongPassword, ViewStatus.TooLarge, ViewStatus.TooSlow,
        ViewStatus.Damaged, ViewStatus.Refused, ViewStatus.Unsupported,
    };

    [Fact]
    public void EveryEditorStep_OverEveryHostileFile_EndsInAFileThatOpens_OrAWord()
    {
        foreach ((string name, byte[] file) in Corpus())
        {
            Outcome(name + " check", PdfEdit.Check(file, null, CancellationToken.None));
            Outcome(name + " arrange", PdfEdit.Arrange(new[] { file }, null, new EditPlan { Pages = { new PagePick { File = 0, Page = 0, Turn = 90 } } }, EditSwitches.All, CancellationToken.None));
            Outcome(name + " signs", PatientSigns.Find(file, null, CancellationToken.None));
            Outcome(name + " save", PdfEdit.Save(new[] { file }, null, Everything(), EditSwitches.All, CancellationToken.None));
        }
    }

    [Fact]
    public void ScriptsAndEmbeddedFiles_NeverLeaveInAFileAnjalMade()
    {
        byte[] active = Handmade(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 6 0 R /Names << /EmbeddedFiles << /Names [(x.exe) 7 0 R] >> /JavaScript << /Names [(a) 6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /AA << /O 6 0 R >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("BT /F1 24 Tf 72 720 Td (Has a script) Tj ET"),
            "<< /S /JavaScript /JS (app.alert('x')) >>",
            "<< /Type /Filespec /F (x.exe) /EF << /F 8 0 R >> >>",
            Stream("MZ not really a program"));
        foreach (EditPlan plan in new[] { new EditPlan { Numbers = new NumberSpec() }, new EditPlan { Redactions = { new RedactBox { Page = 0, X = 60, Y = 100, W = 200, H = 40 } } } })
        {
            JobAnswer saved = PdfEdit.Save(new[] { active }, null, plan, EditSwitches.All, CancellationToken.None);
            Assert.True(saved.Answer.Status == ViewStatus.Ok, saved.Answer.Status + " " + saved.Answer.Detail);
            string text = Encoding.Latin1.GetString(saved.Files[0]);
            Assert.DoesNotContain("/JavaScript", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/EmbeddedFile", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/OpenAction", text, StringComparison.Ordinal);
            Assert.DoesNotContain("app.alert", text, StringComparison.Ordinal);
            Assert.True(ViewJson.ReadEditReport(saved.Answer.Json)!.ActiveRemoved > 0);
        }
    }

    // Every outcome is one of the allowed words; a file given back opens again.
    private static void Outcome(string what, JobAnswer answer)
    {
        Assert.True(Array.IndexOf(Allowed, answer.Answer.Status) >= 0, what + ": " + answer.Answer.Status);
        if (answer.Answer.Status == ViewStatus.Ok && answer.Files.Count > 0 && answer.Files[0].Length > 4 && Encoding.ASCII.GetString(answer.Files[0], 0, 4) == "%PDF")
        {
            using PdfDocument doc = PdfDocument.Open(new MemoryStream(answer.Files[0]), new PdfOpenOptions { Limits = PdfJobs.Limits });
            Assert.True(doc.PageCount > 0, what + ": no pages");
        }
    }

    private static EditPlan Everything() => new()
    {
        Marks =
        {
            new EditMark { Kind = MarkKinds.Rectangle, Page = 0, X = 10, Y = 10, W = 50, H = 20 },
            new EditMark { Kind = MarkKinds.Text, Page = 0, X = 20, Y = 40, W = 200, H = 20, Text = "நன்றி checked" },
            new EditMark { Kind = MarkKinds.Signature, Page = 0, Points = { 30, 80, 60, 70, 90, 85 }, Strokes = { 0 } },
        },
        Redactions = { new RedactBox { Page = 0, X = 20, Y = 20, W = 100, H = 30 } },
        Watermark = new WatermarkSpec { Text = "COPY" },
        Numbers = new NumberSpec(),
    };

    private static IEnumerable<(string Name, byte[] File)> Corpus()
    {
        byte[] good = TestFiles.Pdf("A good page", "Another");
        yield return ("empty", Array.Empty<byte>());
        yield return ("not a pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nnothing here at all"));
        yield return ("cut in half", good.AsSpan(0, good.Length / 2).ToArray());
        yield return ("no cross-reference", Encoding.ASCII.GetBytes(Encoding.Latin1.GetString(good).Replace("xref", "yref", StringComparison.Ordinal)));
        yield return ("deep nesting", Handmade(
            "<< /Type /Catalog /Pages 2 0 R /Deep " + string.Concat(Enumerable.Repeat("[", 5000)) + string.Concat(Enumerable.Repeat("]", 5000)) + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>"));
        yield return ("huge page", Handmade(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 14400000 14400000] /Rotate 90 /CropBox [-5 -5 1e9 1e9] >>"));
        yield return ("many operators", Handmade(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>",
            Stream(string.Concat(Enumerable.Repeat("0 0 m 1 1 l S ", 300_000)))));
        yield return ("pages loop", Handmade(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>"));
        yield return ("locked", TestFiles.LockedPdf("s3cret", "Locked"));
    }

    private static string Stream(string content) => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream";

    // A PDF made by hand from its objects, with a correct cross-reference table.
    private static byte[] Handmade(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
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
            pdf.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
