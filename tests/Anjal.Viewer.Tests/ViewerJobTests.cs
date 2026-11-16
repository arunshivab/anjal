using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>rc.29 (DES-22 §3, §5): the viewer's jobs, run in the test process (the child process is tested apart).</summary>
public sealed class ViewerJobTests
{
    private static readonly string[] WordOrder = { "h1", "p", "li", "li", "table", "p" };

    private static readonly string[] FormattedOrder = { "h1", "p", "p", "p", "table", "p" };

    private static readonly string[] CellRuns = { "13.2", "\n", "rechecked" };

    private static readonly string[] JoinedRuns = { "Para", "cet", "\n", "amol" };

    [Fact]
    public void APdf_IsDrawn_ItsTextPlaced_AndFound()
    {
        byte[] pdf = TestFiles.Pdf("Haemoglobin 13.2 MRN 4471", "Second page");
        JobAnswer info = PdfJobs.Info(pdf, null, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, info.Answer.Status);
        PdfInfo shape = ViewJson.ReadPdfInfo(info.Answer.Json)!;
        Assert.Equal(2, shape.PageCount);
        Assert.Equal(595, shape.Pages[0].W, 0);
        Assert.Equal(842, shape.Pages[0].H, 0);

        JobAnswer page = PdfJobs.Page(pdf, null, 0, 400, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, page.Answer.Status);
        Assert.Equal("image/png", FileKinds.PictureType(page.Files[0]));

        PageText text = ViewJson.ReadPageText(PdfJobs.Text(pdf, null, 0, CancellationToken.None).Answer.Json)!;
        TextRun run = Assert.Single(text.Runs);
        Assert.Contains("MRN 4471", run.T, StringComparison.Ordinal);
        // The builder places text 72 pt from the top; its baseline sits one line (12 pt) lower.
        Assert.InRange(run.Y, 72, 90);
        Assert.Equal(72, run.X, 0);

        FindResult found = ViewJson.ReadFind(PdfJobs.Find(pdf, null, "MRN", CancellationToken.None).Answer.Json)!;
        FindHit hit = Assert.Single(found.Hits);
        Assert.Equal(0, hit.Page);
        Box box = Assert.Single(hit.Boxes);
        Assert.InRange(box.Y, 65, 90);
    }

    [Fact]
    public void ALockedPdf_AsksForItsPassword_AndSaysWhenItIsWrong()
    {
        byte[] pdf = TestFiles.LockedPdf("pw-1234", "Secret page");
        Assert.Equal(ViewStatus.NeedsPassword, PdfJobs.Info(pdf, null, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.WrongPassword, PdfJobs.Info(pdf, "wrong", CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Ok, PdfJobs.Info(pdf, "pw-1234", CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void NotAPdf_IsDamaged_NeverAnException()
    {
        JobAnswer a = PdfJobs.Info("this is not a PDF at all"u8.ToArray(), null, CancellationToken.None);
        Assert.NotEqual(ViewStatus.Ok, a.Answer.Status);
    }

    [Fact]
    public void AWordFile_IsText_InTheDocumentsOrder_TablesIncluded()
    {
        WordView view = ViewJson.ReadWord(WordJob.Run(TestFiles.Word(), null, CancellationToken.None).Answer.Json)!;
        Assert.Equal(WordOrder, view.Blocks.Select(b => b.K).ToArray());
        Assert.Equal(1, view.Blocks[2].L);
        Assert.Equal(2, view.Blocks[3].L);
        Assert.Equal("13.2", view.Blocks[4].Rows![1][1]);
        Assert.Equal("After the table", view.Blocks[5].T);
        // Text from the file stays text: the webmail escapes it when it is shown.
        Assert.Equal("Plain paragraph <script>", view.Blocks[1].T);
        // A file with no styles, numbering or theme part (many tools write none) is read whole: the formatting falls
        // back to the defaults, and nothing is reported as skipped, so no "partly shown" notice (AC-24).
        Assert.Empty(view.Skipped);
        Assert.DoesNotContain(ViewNotices.Partial, view.Notices);
    }

    [Fact]
    public void StyleIds_GiveHeadingsAndListLevels_ByTheirNamesOnly()
    {
        Assert.Equal("h3", WordJob.Block("Heading3", "x").K);
        Assert.Equal("h1", WordJob.Block("Title", "x").K);
        Assert.Equal(4, WordJob.Block("ListNumber4", "x").L);
        Assert.Equal("p", WordJob.Block("MyOwnStyle", "x").K);
        Assert.Equal("p", WordJob.Block("Heading9", "x").K);
    }

    [Fact]
    public void ASheet_ShowsItsCells_MergesAndTabs()
    {
        SheetView view = ViewJson.ReadSheet(SheetJob.Run(TestFiles.Workbook(), null, 0, "en-IN", CancellationToken.None).Answer.Json)!;
        Assert.Equal(2, view.Sheets.Count);
        Assert.True(view.Sheets[1].Hidden);
        Assert.Equal("Name", view.Rows[0].Cells[0].D);
        // A workbook with no <dimension> (the library's own writer writes none): every column still shows.
        Assert.Contains(view.Rows[0].Cells, c => c.C == 2 && c.D == "Value");
        Assert.Contains(view.Rows[0].Cells, c => c.C == 4 && c.D == "Merged");
        Assert.Contains(view.Rows[1].Cells, c => c.C == 2 && c.D == "3");
        Assert.True(view.Columns >= 4, "columns: " + view.Columns);
        Assert.Equal(view.Columns, view.Widths.Count);
        Assert.Contains("D1:E1", view.Merges);
        Assert.Equal(3, view.Total);
    }

    [Fact]
    public void AWordFile_ShowsTheLibrarysEffectiveFormatting_RunByRun()
    {
        // AC-24: bold through a character style based on another, italic from the paragraph style, the run's own
        // underline, strikethrough, lowered and raised text and capitals; hidden text and a tracked deletion are not shown.
        WordView view = ViewJson.ReadWord(WordJob.Run(TestFiles.FormattedWord(), null, CancellationToken.None).Answer.Json)!;
        Assert.Equal(FormattedOrder, view.Blocks.Select(b => b.K).ToArray());
        Assert.Null(view.Blocks[0].R);
        List<WordRun> runs = view.Blocks[1].R!;
        Assert.Equal("Give Paracetamol twice; not with food. Old dose CO2 and m2 urgent", string.Concat(runs.Select(r => r.T)));
        Assert.True(runs.Single(r => r.T == "Paracetamol").B);
        Assert.True(runs.Single(r => r.T == "not").U);
        Assert.True(runs.Single(r => r.T == "Old dose").S);
        Assert.Equal("sub", runs.Single(r => r.T == "2" && r.P == "sub").P);
        Assert.Contains(runs, r => r.T == "2" && r.P == "sup");
        Assert.Equal("caps", runs.Single(r => r.T == "urgent").C);
        Assert.True(runs.Single(r => r.T == "Give ").IsPlain());
        Assert.DoesNotContain(runs, r => r.T.Contains("secret", StringComparison.Ordinal) || r.T.Contains("removed", StringComparison.Ordinal));
        Assert.All(view.Blocks[2].R!, r => Assert.True(r.I));
        Assert.Null(view.Blocks[3].R);
        WordBlock table = view.Blocks[4];
        Assert.Equal("13.2\nrechecked", table.Rows![1][1]);
        Assert.True(table.RunRows![0][0]!.Single().B);
        Assert.Null(table.RunRows[1][0]);
        List<WordRun> cell = table.RunRows[1][1]!;
        Assert.Equal(CellRuns, cell.Select(r => r.T).ToArray());
        Assert.True(cell[2].I);
        Assert.Equal("After the table", view.Blocks[5].T);
        Assert.Empty(view.Skipped);
    }

    [Fact]
    public void NeighbouringRuns_ThatLookTheSame_AreOne_AndALineBreakStaysItsOwn()
    {
        var plain = new Chuvadi.Docs.Model.TextFormat();
        var bold = new Chuvadi.Docs.Model.TextFormat { Bold = true };
        var runs = new List<Chuvadi.Docs.Model.Run> { new("Pa", bold), new("ra", bold), new("cet", plain), new("\n", plain), new("amol", plain) };
        List<WordRun> shown = WordJob.Runs(runs, new[] { bold, bold, plain, plain, plain });
        Assert.Equal(JoinedRuns, shown.Select(r => r.T).ToArray());
    }

    [Theory]
    [InlineData("en-IN", "01/11/2026", "01/11/2026 18:00", "9:30:00")]
    [InlineData("hi-IN", "1/11/2026", "1/11/2026 18:00", "9:30:00")]
    [InlineData(null, "11/01/2026", "11/01/2026 18:00", "9:30:00")]
    public void ASheetsDatesAndTimes_ShowInTheReadersOwnShortForm(string? culture, string date, string dateTime, string time)
    {
        // AC-22: on Sheets 2.0.5 the time showed as ######## and formats 14 and 22 as their raw code (11-01-26). On
        // 2.1.0 format 14 is the reader's own short date (day first in India), 22 adds the time, and 21 is h:mm:ss.
        // No culture is Excel's en-US.
        SheetView view = ViewJson.ReadSheet(SheetJob.Run(TestFiles.DatesAndTimes(), null, 0, culture, CancellationToken.None).Answer.Json)!;
        List<SheetCell> cells = view.Rows[0].Cells;
        Assert.Equal(date, cells.Single(c => c.C == 1).D);
        Assert.Equal(dateTime, cells.Single(c => c.C == 2).D);
        Assert.Equal(time, cells.Single(c => c.C == 3).D);
        // The time's stored value is the time alone, not Excel's day zero (30 Dec 1899); here it equals what is shown.
        Assert.Null(cells.Single(c => c.C == 3).V);
        Assert.Equal("2026-11-01 18:00:00", cells.Single(c => c.C == 2).V);
    }

    [Fact]
    public void ASheet_PastTwoThousandRows_ShowsTwoThousand_AndSaysHowManyThereAre()
    {
        SheetView view = ViewJson.ReadSheet(SheetJob.Run(TestFiles.Workbook(2600), null, 0, null, CancellationToken.None).Answer.Json)!;
        Assert.Equal(2000, view.Shown);
        Assert.True(view.Cut);
        Assert.True(view.Rows.Count <= 2000);
    }

    [Fact]
    public void UsedRange_GivesTheLastColumnAndRow()
    {
        Assert.Equal((11, 250), SheetJob.UsedEnd("A1:K250"));
        Assert.Equal((27, 3), SheetJob.UsedEnd("AA3"));
        Assert.Equal((0, 0), SheetJob.UsedEnd(null));
    }

    [Fact]
    public void AFileInsideAnArchive_OpensInTheSameViewer()
    {
        byte[] zip = TestFiles.ZipBytes(("reports/", null), ("reports/lab.pdf", TestFiles.Pdf("Inside the archive")));
        ArchiveView list = ArchiveReader.List(zip).View;
        ArchiveItem pdf = list.Entries.Single(e => e.Name == "lab.pdf");
        Assert.Equal(FileKinds.Pdf, pdf.Opens);
        JobAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfText, Entry = pdf.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) }, zip, CancellationToken.None);
        Assert.Contains("Inside the archive", ViewJson.ReadPageText(a.Answer.Json)!.Runs[0].T, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramRenamedPdf_InsideAnArchive_IsBlocked_AndNeverOpened()
    {
        byte[] zip = TestFiles.ZipBytes(("report.pdf", TestFiles.WindowsProgram()));
        ArchiveItem e = Assert.Single(ArchiveReader.List(zip).View.Entries);
        Assert.True(e.Blocked);
        Assert.Null(e.Opens);
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfInfo, Entry = "0" }, zip, CancellationToken.None).Answer.Status);
        Assert.Null(ArchiveReader.Entry(zip, "0").Bytes);
    }
}
