using System.Security.Cryptography;
using Anjal.Viewing;
using Chuvadi.Pdf.Documents;
using Chuvadi.Pdf.Forms;
using Chuvadi.Pdf.Graphics;
using Chuvadi.Pdf.Operations;
using Chuvadi.Pdf.Rendering;
using Chuvadi.Pdf.Text;

namespace Anjal.Viewer.Tests;

/// <summary>rc.29 part 2 (DES-22 §7, §13): the PDF editor's work in the viewer, run in the test process.</summary>
public sealed class EditorTests
{
    private static readonly int[] Turns = { 0, 90, 180, 270 };

    private static readonly string[] JoinedOrder = { "Three", "One", "Other B" };

    private static readonly string[] SignKinds = { "aadhaar", "pan", "phone", "record" };

    private static readonly string[] SecondOnly = { "Two" };

    private static readonly int[] SecondPage = { 1 };

    private static readonly string[] Corners = { "#ff0000", "#00c000", "#0000ff", "#ff00ff" };

    [Fact]
    public void Marks_LandWhereTheyWereDrawn_InEveryTurn_WithAndWithoutACrop()
    {
        // §7.4 (L-02): PlaceMany draws in the page's unrotated user space, so each overlay is placed through the
        // library's display transform (3.34.0). A mark in each corner of the page as shown must land in that corner.
        foreach (int turn in Turns)
        {
            foreach (bool crop in new[] { false, true })
            {
                byte[] pdf = Turned(TestFiles.Pdf("Corner test"), turn, crop);
                var shown = ShownSize(pdf);
                var marks = new List<EditMark>();
                (double X, double Y)[] at = { (10, 10), (shown.W - 40, 10), (10, shown.H - 40), (shown.W - 40, shown.H - 40) };
                for (int c = 0; c < 4; c++)
                {
                    marks.Add(new EditMark { Kind = MarkKinds.Highlight, Page = 0, X = at[c].X, Y = at[c].Y, W = 30, H = 30, Color = Corners[c] });
                }
                byte[] saved = Saved(pdf, new EditPlan { Marks = marks });
                using PdfDocument doc = PdfDocument.Open(new MemoryStream(saved));
                PixelBuffer px = new PageRasterizer(doc.Objects, new RenderOptions { Dpi = 72, ApplyPageRotation = true }).Rasterize(doc.Pages[0]);
                Assert.Equal(Math.Round(shown.W), px.Width, 1.0);
                for (int c = 0; c < 4; c++)
                {
                    (byte b, byte g, byte r, byte _) = px.GetPixelBgra((int)at[c].X + 15, (int)at[c].Y + 15);
                    string seen = $"#{r:x2}{g:x2}{b:x2}";
                    // A highlight is its colour at 35% over the white page.
                    Assert.True(Near(seen, Blended(Corners[c], 0.35)), $"turn {turn}, crop {crop}, corner {c}: expected {Blended(Corners[c], 0.35)}, saw {seen}");
                }
            }
        }
    }

    [Fact]
    public void TheViewersShownPage_IsTheLibrarysDisplaySpace_InEveryTurn_WithAndWithoutACrop()
    {
        // The viewer places text, Find and the patient signs with PageGeometry; the editor writes through the
        // library's display transform (3.34.0, D-335). They must agree, or a found box would be redacted elsewhere.
        foreach (int turn in Turns)
        {
            foreach (bool crop in new[] { false, true })
            {
                using PdfDocument doc = PdfDocument.Open(new MemoryStream(Turned(TestFiles.Pdf("Agree"), turn, crop)));
                PdfPage page = doc.Pages[0];
                var g = new PageGeometry(page);
                Transform toUser = PdfEdit.DisplayToUser(page);
                foreach ((double x, double y) in new[] { (0.0, 0.0), (g.ShownWidth, 0.0), (0.0, g.ShownHeight), (123.0, 45.0) })
                {
                    PointF lib = toUser.TransformPoint(new PointF(x, g.ShownHeight - y));
                    (double ux, double uy) = g.ToUser(x, y);
                    Assert.True(Math.Abs(lib.X - ux) < 0.01 && Math.Abs(lib.Y - uy) < 0.01, $"turn {turn}, crop {crop}, ({x}, {y}): library ({lib.X}, {lib.Y}), viewer ({ux}, {uy})");
                }
            }
        }
    }

    [Fact]
    public void Pages_AreTurned_Removed_Reordered_AndJoined()
    {
        byte[] pdf = TestFiles.Pdf("One", "Two", "Three");
        byte[] other = TestFiles.Pdf("Other A", "Other B");
        string originalHash = Hash(pdf);
        var plan = new EditPlan
        {
            Joined = 1,
            Pages =
            {
                new PagePick { File = 0, Page = 2 },
                new PagePick { File = 0, Page = 0, Turn = 90 },
                new PagePick { File = 1, Page = 1 },
            },
        };
        JobAnswer answer = PdfEdit.Save(new[] { pdf, other }, null, plan, EditSwitches.All, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, answer.Answer.Status);
        EditReport report = ViewJson.ReadEditReport(answer.Answer.Json)!;
        Assert.Equal(3, report.Pages);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(answer.Files[0]));
        Assert.Equal(JoinedOrder, Texts(doc));
        Assert.Equal(90, doc.Pages[1].Rotate);
        // §6.3: the original is only read.
        Assert.Equal(originalHash, Hash(pdf));
    }

    [Fact]
    public void Arrange_GivesThePagesAsTheyWillBe_ForTheViewerToShow()
    {
        byte[] pdf = TestFiles.Pdf("One", "Two");
        JobAnswer answer = PdfEdit.Arrange(new[] { pdf }, null, new EditPlan { Pages = { new PagePick { File = 0, Page = 1, Turn = 270 } } }, EditSwitches.All, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, answer.Answer.Status);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(answer.Files[0]));
        Assert.Equal(SecondOnly, Texts(doc));
        Assert.Equal(270, doc.Pages[0].Rotate);
        // The viewer's own jobs then show it.
        Assert.Equal(ViewStatus.Ok, PdfJobs.Info(answer.Files[0], null, CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void Text_InEveryScript_IsWrittenAndReadsBackAsWritten()
    {
        byte[] pdf = TestFiles.Pdf("Report");
        const string tamil = "நோயாளி அறிக்கை";
        const string hindi = "रोगी रिपोर्ट";
        byte[] saved = Saved(pdf, new EditPlan
        {
            Marks =
            {
                new EditMark { Kind = MarkKinds.Text, Page = 0, X = 72, Y = 200, W = 300, H = 30, Text = tamil, Size = 14 },
                new EditMark { Kind = MarkKinds.Text, Page = 0, X = 72, Y = 260, W = 300, H = 30, Text = hindi, Size = 14 },
            },
        });
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(saved));
        string text = new TextExtractor(doc.Objects).ExtractText(doc.Pages[0]);
        Assert.Contains(tamil, text, StringComparison.Ordinal);
        Assert.Contains(hindi, text, StringComparison.Ordinal);
        Assert.Contains("Report", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWatermark_AndPageNumbers_AreOnEveryPage()
    {
        byte[] pdf = Turned(TestFiles.Pdf("First", "Second"), 90, crop: true);
        byte[] saved = Saved(pdf, new EditPlan
        {
            Watermark = new WatermarkSpec { Text = "COPY", Size = 50, Angle = 30, Opacity = 0.3 },
            Numbers = new NumberSpec { Format = "Page {n} of {N}", Where = NumberPlaces.BottomCentre },
        });
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(saved));
        var extractor = new TextExtractor(doc.Objects);
        for (int i = 0; i < 2; i++)
        {
            string text = extractor.ExtractText(doc.Pages[i]);
            Assert.Contains("COPY", text, StringComparison.Ordinal);
            Assert.Contains($"Page {i + 1} of 2", text, StringComparison.Ordinal);
        }
        // The numbers sit at the bottom of the page as shown, inside the crop, on a turned page.
        var g = new PageGeometry(doc.Pages[0]);
        var number = new TextExtractor(doc.Objects).ExtractFragments(doc.Pages[0]).First(f => f.Text.Contains("Page", StringComparison.Ordinal));
        (double _, double y) = g.ToShown(number.X, number.Y);
        Assert.InRange(y, g.ShownHeight - 40, g.ShownHeight);
    }

    [Fact]
    public void Redaction_RemovesTheWords_TheOutline_AndTheMetadata()
    {
        byte[] pdf = WithOutline(TestFiles.Pdf("Patient MRN 4471 seen today", "Second page"), "MRN 4471 notes");
        FindResult found = ViewJson.ReadFind(PdfJobs.Find(pdf, null, "MRN 4471", CancellationToken.None).Answer.Json)!;
        Box hit = Assert.Single(Assert.Single(found.Hits).Boxes);
        byte[] saved = Saved(pdf, new EditPlan
        {
            Redactions =
            {
                new RedactBox { Page = 0, X = hit.X - PdfEdit.FindPadding, Y = hit.Y - PdfEdit.FindPadding, W = hit.W + (2 * PdfEdit.FindPadding), H = hit.H + (2 * PdfEdit.FindPadding) },
                // A box over the empty foot of the second page: a note, not a refusal.
                new RedactBox { Page = 1, X = 100, Y = 700, W = 120, H = 40 },
            },
        }, out EditReport report);
        Assert.Equal(2, report.Redacted);
        // What was removed, shown before the file is attached (3.34.0 ApplyWithReport): "MRN 4471" is 8 characters.
        RedactedPage first = Assert.Single(report.Removed);
        Assert.Equal(0, first.Page);
        Assert.InRange(first.Characters, 8, 10);
        Assert.Equal(0, first.Pictures);
        Assert.Equal(SecondPage, report.NothingUnder);
        Assert.True(report.OutlineRemoved);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(saved));
        string text = new TextExtractor(doc.Objects).ExtractText(doc.Pages[0]);
        Assert.DoesNotContain("4471", text, StringComparison.Ordinal);
        Assert.Contains("Patient", text, StringComparison.Ordinal);
        Assert.Contains("Second page", new TextExtractor(doc.Objects).ExtractText(doc.Pages[1]), StringComparison.Ordinal);
        // §7.6 (D-375): no outline, no /Info, no XMP in the final bytes.
        Assert.Empty(OutlineReader.GetOutlines(doc));
        Assert.Null(doc.XmpMetadata);
        Assert.True(doc.Info is null || doc.Info.Count == 0);
        Assert.Equal(-1, System.Text.Encoding.Latin1.GetString(saved).IndexOf("MRN 4471", StringComparison.Ordinal));
    }

    [Fact]
    public void PatientSigns_AreFound_WithTheirBoxes_AndRedactCleanly()
    {
        byte[] pdf = TestFiles.Pdf("MRN 2026-10443 Phone 98765 43210", "Aadhaar 2345 6789 0123 and PAN ABCDE1234F");
        FindResult found = ViewJson.ReadFind(PatientSigns.Find(pdf, null, CancellationToken.None).Answer.Json)!;
        Assert.Equal(SignKinds, found.Hits.Select(h => h.Sign!).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, found.Hits.Count(h => h.Page == 0));
        var boxes = found.Hits.SelectMany(h => h.Boxes.Select(b => new RedactBox { Page = h.Page, X = b.X, Y = b.Y, W = b.W, H = b.H })).ToList();
        byte[] saved = Saved(pdf, new EditPlan { Redactions = boxes });
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(saved));
        var extractor = new TextExtractor(doc.Objects);
        string all = extractor.ExtractText(doc.Pages[0]) + extractor.ExtractText(doc.Pages[1]);
        foreach (string gone in new[] { "10443", "43210", "6789", "1234F" })
        {
            Assert.DoesNotContain(gone, all, StringComparison.Ordinal);
        }
        Assert.Contains("and", all, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedFile_IsSavedOnlyWhenThePersonAcceptsThatTheResultHasNoPassword()
    {
        byte[] pdf = TestFiles.LockedPdf("s3cret", "Locked page");
        var marks = new List<EditMark> { new() { Kind = MarkKinds.Rectangle, Page = 0, X = 50, Y = 50, W = 50, H = 50 } };
        JobAnswer refused = PdfEdit.Save(new[] { pdf }, "s3cret", new EditPlan { Marks = marks }, EditSwitches.All, CancellationToken.None);
        Assert.Equal(ViewStatus.Refused, refused.Answer.Status);
        Assert.Equal(EditRefusals.Password, ViewJson.ReadEditReport(refused.Answer.Json)!.Refused);

        JobAnswer saved = PdfEdit.Save(new[] { pdf }, "s3cret", new EditPlan { Marks = marks, AcceptNoPassword = true }, EditSwitches.All, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, saved.Answer.Status);
        Assert.False(PdfDocument.RequiresPassword(new MemoryStream(saved.Files[0])));

        EditCheck check = ViewJson.ReadEditCheck(PdfEdit.Check(pdf, "s3cret", CancellationToken.None).Answer.Json)!;
        Assert.True(check.Locked);
        Assert.Empty(check.Signatures);
    }

    [Fact]
    public void APlanThatDoesNotFit_IsRefused_NeverSaved()
    {
        byte[] pdf = TestFiles.Pdf("Only page");
        EditPlan[] bad =
        {
            new() { Marks = { new EditMark { Kind = MarkKinds.Rectangle, Page = 3 } } },
            new() { Marks = { new EditMark { Kind = "script", Page = 0 } } },
            new() { Marks = { new EditMark { Kind = MarkKinds.Rectangle, Page = 0, Color = "red" } } },
            new() { Marks = { new EditMark { Kind = MarkKinds.Picture, Page = 0, Picture = 0, W = 10, H = 10 } } },
            new() { Pages = { new PagePick { File = 0, Page = 5 } } },
            new() { Pages = { new PagePick { File = 0, Page = 0, Turn = 45 } } },
            new() { Redactions = { new RedactBox { Page = 0, W = -1, H = 5 } } },
            new() { Watermark = new WatermarkSpec { Text = string.Empty } },
            new() { Numbers = new NumberSpec { Where = "middle" } },
        };
        foreach (EditPlan plan in bad)
        {
            JobAnswer answer = PdfEdit.Save(new[] { pdf }, null, plan, EditSwitches.All, CancellationToken.None);
            Assert.Equal(ViewStatus.Refused, answer.Answer.Status);
            Assert.Equal(EditRefusals.Plan, ViewJson.ReadEditReport(answer.Answer.Json)!.Refused);
            Assert.Empty(answer.Files);
        }
    }

    [Fact]
    public void APicture_AndADrawnSignature_ArePlaced()
    {
        byte[] pdf = TestFiles.Pdf("Signed here");
        byte[] png = TinyPng();
        var strokes = new EditMark
        {
            Kind = MarkKinds.Signature,
            Page = 0,
            Color = "#1a237e",
            Line = 2,
            Points = { 100, 400, 140, 380, 180, 410, 220, 390, 120, 420, 200, 420 },
            Strokes = { 0, 4 },
        };
        JobAnswer answer = PdfEdit.Save(new[] { pdf, png }, null, new EditPlan
        {
            Marks = { strokes, new EditMark { Kind = MarkKinds.Picture, Page = 0, X = 300, Y = 380, W = 40, H = 40, Picture = 0 } },
        }, EditSwitches.All, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, answer.Answer.Status);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(answer.Files[0]));
        PixelBuffer px = new PageRasterizer(doc.Objects, new RenderOptions { Dpi = 72 }).Rasterize(doc.Pages[0]);
        (byte b, byte g, byte r, byte _) = px.GetPixelBgra(320, 400);
        Assert.True(r > 200 && g < 60 && b < 60, $"the picture's red: saw {r},{g},{b}");
        // The signature's ink: blue, in the box its strokes span (100..220 by 380..420), none outside it nearby.
        int ink = 0;
        for (int y = 375; y <= 425; y++)
        {
            for (int x = 95; x <= 225; x++)
            {
                (byte ib, byte ig, byte ir, byte _) = px.GetPixelBgra(x, y);
                if (ib > ir + 40 && ir < 160)
                {
                    ink++;
                }
            }
        }
        Assert.True(ink > 150, $"the signature's ink: {ink} pixels");
    }

    [Fact]
    public void ThroughTheChildsDoor_TheJobsAreFound()
    {
        byte[] pdf = TestFiles.Pdf("Door");
        JobAnswer check = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfEditCheck }, new[] { pdf }, CancellationToken.None);
        Assert.Equal(1, ViewJson.ReadEditCheck(check.Answer.Json)!.PageCount);
        JobAnswer save = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSave, Plan = new EditPlan { Numbers = new NumberSpec() } }, new[] { pdf }, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, save.Answer.Status);
        // A PDF inside a ZIP is edited as a new PDF; the ZIP is only read (D-382).
        byte[] zip = TestFiles.ZipBytes(("report.pdf", pdf));
        JobAnswer inner = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSave, Entry = "0", Plan = new EditPlan { Numbers = new NumberSpec() } }, new[] { zip }, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, inner.Answer.Status);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(inner.Files[0], 0, 4));
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfSave }, new[] { pdf }, CancellationToken.None).Answer.Status);
    }

    private static byte[] Saved(byte[] pdf, EditPlan plan) => Saved(pdf, plan, out _);

    private static byte[] Saved(byte[] pdf, EditPlan plan, out EditReport report)
    {
        JobAnswer answer = PdfEdit.Save(new[] { pdf }, null, plan, EditSwitches.All, CancellationToken.None);
        Assert.True(answer.Answer.Status == ViewStatus.Ok, $"save: {answer.Answer.Status} {answer.Answer.Detail}");
        report = ViewJson.ReadEditReport(answer.Answer.Json)!;
        return answer.Files[0];
    }

    // A PDF whose pages are turned, and when asked cropped to a smaller box that does not start at the origin.
    private static byte[] Turned(byte[] pdf, int turn, bool crop)
    {
        using PdfDocument source = PdfDocument.Open(new MemoryStream(pdf));
        byte[] turned;
        using (var o = new MemoryStream())
        {
            if (turn == 0)
            {
                source.Save(o);
            }
            else
            {
                PageOperations.RotatePages(o, source, turn);
            }
            turned = o.ToArray();
        }
        if (!crop)
        {
            return turned;
        }
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(turned));
        foreach (PdfPage p in doc.Pages)
        {
            p.CropBox = new PdfRectangle(40, 60, 500, 760);
        }
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    private static (double W, double H) ShownSize(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        var g = new PageGeometry(doc.Pages[0]);
        return (g.ShownWidth, g.ShownHeight);
    }

    private static byte[] WithOutline(byte[] pdf, string title)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        using var output = new MemoryStream();
        OutlineWriter.Apply(output, doc, new[] { new OutlineEntry(title, 0) });
        return output.ToArray();
    }

    private static string[] Texts(PdfDocument doc)
    {
        var extractor = new TextExtractor(doc.Objects);
        return Enumerable.Range(0, doc.PageCount).Select(i => extractor.ExtractText(doc.Pages[i]).Trim()).ToArray();
    }

    private static bool Near(string seen, string want)
    {
        int Channel(string hex, int at) => Convert.ToInt32(hex.Substring(at, 2), 16);
        return Enumerable.Range(0, 3).All(i => Math.Abs(Channel(seen, 1 + (2 * i)) - Channel(want, 1 + (2 * i))) < 24);
    }

    private static string Blended(string hex, double opacity)
    {
        int Over(int at) => (int)Math.Round(255 - (opacity * (255 - Convert.ToInt32(hex.Substring(at, 2), 16))));
        return $"#{Over(1):x2}{Over(3):x2}{Over(5):x2}";
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // A 4 by 4 red PNG.
    private static byte[] TinyPng() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGP4z8AARwzEcQCukw/x0F8jngAAAABJRU5ErkJggg==");
}
