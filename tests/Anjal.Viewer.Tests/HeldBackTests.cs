using System.IO.Compression;
using System.Text;
using Anjal.Viewing;
using Chuvadi.Pdf.Documents;
using Chuvadi.Pdf.Primitives;

namespace Anjal.Viewer.Tests;

/// <summary>
/// The library's security advisory (D-405, D-406, 17 Nov 2026): what rc.29 holds back until chuvadi-pdf 3.35.1 -
/// redaction, and changing the pages of a file with hidden layers - refused as it ships and working when switched
/// on; and the receivers' DMARC and TLS reports read in the viewer, not the webmail.
/// </summary>
public sealed class HeldBackTests
{
    [Fact]
    public void Redaction_IsRefusedAsRc29Ships_AndWorksWhenSwitchedOn()
    {
        byte[] pdf = TestFiles.Pdf("Patient MRN 4471203");
        var plan = new EditPlan { Redactions = { new RedactBox { Page = 0, X = 0, Y = 0, W = 595, H = 842 } } };

        JobAnswer off = PdfEdit.Save(new[] { pdf }, null, plan, EditSwitches.Shipped, CancellationToken.None);
        Assert.Equal(ViewStatus.Refused, off.Answer.Status);
        Assert.Equal(EditRefusals.RedactOff, ViewJson.ReadEditReport(off.Answer.Json)!.Refused);
        Assert.Empty(off.Files);

        JobAnswer on = PdfEdit.Save(new[] { pdf }, null, plan, EditSwitches.All, CancellationToken.None);
        Assert.True(on.Answer.Status == ViewStatus.Ok, on.Answer.Status + " " + on.Answer.Detail);
        Assert.Single(on.Files);

        // Find patient details belongs to Redact: the child gives nothing unless the webmail allows it.
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSigns }, pdf, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSigns, Switches = EditSwitches.Shipped }, pdf, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Ok, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSigns, Switches = EditSwitches.All }, pdf, CancellationToken.None).Answer.Status);

        // A request with no switches (an older webmail, or none set) is the shipped one.
        JobAnswer none = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSave, Plan = plan }, pdf, CancellationToken.None);
        Assert.Equal(ViewStatus.Refused, none.Answer.Status);
    }

    [Fact]
    public void AFileWithHiddenLayers_IsFound_AndKeepsItsPages_UntilSwitchedOn()
    {
        byte[] plain = TestFiles.Pdf("Plain");
        byte[] layered = Layered(TestFiles.Pdf("Layered"));
        Assert.True(ViewJson.ReadEditCheck(PdfEdit.Check(layered, null, CancellationToken.None).Answer.Json)!.HasLayers);
        Assert.False(ViewJson.ReadEditCheck(PdfEdit.Check(plain, null, CancellationToken.None).Answer.Json)!.HasLayers);

        var turn = new EditPlan { Pages = { new PagePick { File = 0, Page = 0, Turn = 90 } } };
        foreach (JobAnswer refused in new[]
        {
            PdfEdit.Arrange(new[] { layered }, null, turn, EditSwitches.Shipped, CancellationToken.None),
            PdfEdit.Save(new[] { layered }, null, turn, EditSwitches.Shipped, CancellationToken.None),
            // A plain file with a layered one joined to it.
            PdfEdit.Save(new[] { plain, layered }, null, new EditPlan { Joined = 1, JoinedPasswords = { null } }, EditSwitches.Shipped, CancellationToken.None),
        })
        {
            Assert.Equal(ViewStatus.Refused, refused.Answer.Status);
            Assert.Equal(EditRefusals.Layers, ViewJson.ReadEditReport(refused.Answer.Json)!.Refused);
            Assert.Empty(refused.Files);
        }

        // Mark up on a layered file is not a page change, nor is the page list as it is.
        var mark = new EditPlan { Marks = { new EditMark { Kind = MarkKinds.Highlight, Page = 0, X = 20, Y = 20, W = 40, H = 20 } } };
        Assert.Equal(ViewStatus.Ok, PdfEdit.Save(new[] { layered }, null, mark, EditSwitches.Shipped, CancellationToken.None).Answer.Status);
        var same = new EditPlan { Pages = { new PagePick { File = 0, Page = 0, Turn = 0 } }, Marks = mark.Marks };
        Assert.Equal(ViewStatus.Ok, PdfEdit.Save(new[] { layered }, null, same, EditSwitches.Shipped, CancellationToken.None).Answer.Status);

        // A plain file's pages still change as rc.29 ships; a layered file's when switched on.
        Assert.Equal(ViewStatus.Ok, PdfEdit.Arrange(new[] { plain }, null, turn, EditSwitches.Shipped, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Ok, PdfEdit.Arrange(new[] { layered }, null, turn, EditSwitches.All, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Ok, PdfEdit.Save(new[] { layered }, null, turn, EditSwitches.All, CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void ThePageListAsItIs_IsNotAPageChange()
    {
        Assert.False(new EditPlan().ChangesPages(3));
        Assert.False(new EditPlan { Pages = { new PagePick { Page = 0 }, new PagePick { Page = 1 } } }.ChangesPages(2));
        Assert.True(new EditPlan { Pages = { new PagePick { Page = 1 }, new PagePick { Page = 0 } } }.ChangesPages(2));
        Assert.True(new EditPlan { Pages = { new PagePick { Page = 0 } } }.ChangesPages(2));
        Assert.True(new EditPlan { Pages = { new PagePick { Page = 0, Turn = 180 } } }.ChangesPages(1));
        Assert.True(new EditPlan { Joined = 1 }.ChangesPages(1));
    }

    [Fact]
    public void TheReceiversReports_AreReadInTheViewer()
    {
        const string xml = "<?xml version=\"1.0\"?><feedback><report_metadata><org_name>google.com</org_name><report_id>7</report_id>"
            + "<date_range><begin>1760000000</begin><end>1760086400</end></date_range></report_metadata>"
            + "<policy_published><domain>anjal.co.in</domain></policy_published>"
            + "<record><row><source_ip>192.0.2.10</source_ip><count>12</count><policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated></row>"
            + "<identifiers><header_from>anjal.co.in</header_from></identifiers></record></feedback>";
        JobAnswer dmarc = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DmarcReport }, Gzip(Encoding.UTF8.GetBytes(xml)), CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, dmarc.Answer.Status);
        DmarcReport report = ViewJson.ReadDmarc(dmarc.Answer.Json)!;
        Assert.Equal("anjal.co.in", report.Domain);
        DmarcRow row = Assert.Single(report.Rows);
        Assert.Equal(12, row.Count);
        Assert.True(row.Passed);

        const string json = "{\"organization-name\":\"Google Inc.\",\"policies\":[{\"policy\":{\"policy-type\":\"sts\",\"policy-domain\":\"anjal.co.in\"},"
            + "\"summary\":{\"total-successful-session-count\":41,\"total-failure-session-count\":2}}]}";
        JobAnswer tls = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.TlsReport }, Gzip(Encoding.UTF8.GetBytes(json)), CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, tls.Answer.Status);
        TlsReportRow t = Assert.Single(ViewJson.ReadTls(tls.Answer.Json)!);
        Assert.Equal(41, t.Successful);

        // What is not a report, or a hostile one, is said plainly and nothing more.
        Assert.Equal(ViewStatus.Unsupported, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DmarcReport }, Encoding.UTF8.GetBytes("<html/>"), CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Unsupported, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.TlsReport }, new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x01 }, CancellationToken.None).Answer.Status);
        byte[] deep = Encoding.UTF8.GetBytes("<feedback>" + string.Concat(Enumerable.Repeat("<a>", 50_000)) + "</feedback>");
        Assert.Equal(ViewStatus.Unsupported, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DmarcReport }, deep, CancellationToken.None).Answer.Status);
    }

    // The same PDF with one hidden layer declared (ISO 32000 §8.11.4: /OCProperties with its OCGs and a default
    // configuration that turns the layer off).
    private static byte[] Layered(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Intern("Type"), PdfName.Intern("OCG"));
        ocg.Set(PdfName.Intern("Name"), new PdfString("Hidden notes", false));
        var off = new PdfDictionary();
        off.Set(PdfName.Intern("OFF"), new PdfArray(new PdfPrimitive[] { ocg }));
        var props = new PdfDictionary();
        props.Set(PdfName.Intern("OCGs"), new PdfArray(new PdfPrimitive[] { ocg }));
        props.Set(PdfName.Intern("D"), off);
        doc.Catalog.Set(PdfName.Intern("OCProperties"), props);
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(bytes);
        }
        return packed.ToArray();
    }
}
