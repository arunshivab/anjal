using System.Text;
using Anjal.Viewing;
using Siaara;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.31 (the owner's first-server check, 25 Nov 2026; the coordinator's decisions): CSV and TSV as a plain table with
/// its caps and nothing evaluated (D4), and lite DICOM - SIAARA's smaller limits, the ZIP's files read one at a time
/// from one opening, and every DICOM job in a child of its own at 320 MB.
/// </summary>
public sealed class Rc31Tests
{
    private static readonly string[] Formulas = { "=SUM(A1:A9)", "+1", "-2", "@cmd" };

    private static SheetView Csv(string text, string name = "data.csv") => CsvJob.Read(Encoding.UTF8.GetBytes(text), name, CancellationToken.None);

    private static string? Cell(SheetView v, int row, int column) => v.Rows.FirstOrDefault(r => r.N == row)?.Cells.FirstOrDefault(c => c.C == column)?.D;

    [Fact]
    public void ACsv_IsATable_WithQuotesCommasAndLineBreaksInsideCells()
    {
        SheetView v = Csv("Name,Ward,Note\r\n\"Rao, K\",ICU,\"said \"\"stable\"\"\"\nMehta,,\"two\nlines\"\n");
        Assert.Equal(3, v.Columns);
        Assert.Equal(3, v.Shown);
        Assert.Equal("Rao, K", Cell(v, 2, 1));
        Assert.Equal("said \"stable\"", Cell(v, 2, 3));
        Assert.Null(Cell(v, 3, 2));
        Assert.Equal("two\nlines", Cell(v, 3, 3));
        Assert.Empty(v.Notices);
        Assert.False(v.Cut);
    }

    [Fact]
    public void ACell_StartingWithAFormulaSign_IsShownAsWritten()
    {
        SheetView v = Csv(string.Join(",", Formulas) + "\n");
        for (int c = 0; c < Formulas.Length; c++)
        {
            Assert.Equal(Formulas[c], Cell(v, 1, c + 1));
        }
    }

    [Fact]
    public void TheSeparator_IsAComma_OrWhatTheFirstLineUses_AndATsvIsSplitAtTabs()
    {
        Assert.Equal("b", Cell(Csv("a;b;c\n1;2;3\n"), 1, 2));
        Assert.Equal("2", Cell(Csv("a\tb\n1\t2\n"), 2, 2));
        Assert.Equal("x,y", Cell(Csv("x,y\tz\n", "list.tsv"), 1, 1));
        // A comma beside one semicolon: the comma wins.
        Assert.Equal("a;b", Cell(Csv("a;b,c,d\n"), 1, 1));
    }

    [Fact]
    public void TheText_IsUtf8WithOrWithoutItsMark_ElseWindows1252()
    {
        byte[] marked = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("நோயாளி,Café\n")];
        SheetView v = CsvJob.Read(marked, "a.csv", CancellationToken.None);
        Assert.Equal("நோயாளி", Cell(v, 1, 1));
        Assert.Equal("Café", Cell(v, 1, 2));
        // 0xE9 alone is not UTF-8: Windows-1252, with its curly quotes and euro sign from 0x80 to 0x9F.
        byte[] legacy = [(byte)'C', (byte)'a', (byte)'f', 0xE9, (byte)',', 0x93, (byte)'x', 0x94, (byte)',', 0x80, (byte)'\n'];
        SheetView w = CsvJob.Read(legacy, "a.csv", CancellationToken.None);
        Assert.Equal("Café", Cell(w, 1, 1));
        Assert.Equal("“x”", Cell(w, 1, 2));
        Assert.Equal("€", Cell(w, 1, 3));
    }

    [Fact]
    public void PastItsCaps_TheFirstPartIsShown_WithAPlainNote()
    {
        var many = new StringBuilder();
        for (int i = 1; i <= CsvJob.MaxRows + 50; i++)
        {
            many.Append(i).Append('\n');
        }
        SheetView rows = Csv(many.ToString());
        Assert.Equal(CsvJob.MaxRows, rows.Shown);
        Assert.Equal(CsvJob.MaxRows, rows.Rows.Count);
        Assert.Contains(ViewNotices.TableCut, rows.Notices);

        SheetView wide = Csv(string.Join(",", Enumerable.Range(1, CsvJob.MaxColumns + 5)) + "\n");
        Assert.Equal(CsvJob.MaxColumns, wide.Columns);
        Assert.Equal(CsvJob.MaxColumns, wide.Rows[0].Cells.Count);
        Assert.Contains(ViewNotices.TableCut, wide.Notices);

        SheetView longCell = Csv(new string('x', CsvJob.MaxCellChars + 10) + ",end\n");
        Assert.Equal(CsvJob.MaxCellChars, Cell(longCell, 1, 1)!.Length);
        Assert.Equal("end", Cell(longCell, 1, 2));
        Assert.Contains(ViewNotices.TableCut, longCell.Notices);

        byte[] big = new byte[CsvJob.MaxBytes + 100];
        Array.Fill(big, (byte)'a');
        SheetView large = CsvJob.Read(big, "big.csv", CancellationToken.None);
        Assert.Equal(CsvJob.MaxCellChars, Cell(large, 1, 1)!.Length);
        // The file is not read past its caps, so how much more there is is not said: the note says what was cut.
        Assert.False(large.Cut);
        Assert.Contains(ViewNotices.TableCut, large.Notices);
    }

    [Fact]
    public void ACsv_ThroughTheChildsJobs_AnswersATable_AndTheKindsAgree()
    {
        JobAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.Csv, Name = "ward.csv" }, Encoding.UTF8.GetBytes("Bed,Name\n1,Rao\n"), CancellationToken.None);
        SheetView v = ViewJson.ReadSheet(a.Answer.Json)!;
        Assert.Equal("Rao", Cell(v, 2, 2));
        Assert.Equal(FileKinds.Table, FileKinds.Of("ward.csv", null));
        Assert.Equal(FileKinds.Table, FileKinds.Of("ward.TSV", null));
        Assert.Equal(FileKinds.Table, FileKinds.Of("export", "text/csv"));
        Assert.Equal(8, ViewKinds.Deadline(ViewKinds.Csv).TotalSeconds);
    }

    [Fact]
    public void ACsvInsideAZip_OpensAsATable()
    {
        byte[] zip = TestFiles.ZipBytes(("list.csv", "a,b\n1,2\n"u8.ToArray()));
        ArchiveItem item = ArchiveReader.List(zip).View.Entries.Single();
        Assert.Equal(FileKinds.Table, item.Opens);
        JobAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.Csv, Entry = "0" }, zip, CancellationToken.None);
        Assert.Equal("2", Cell(ViewJson.ReadSheet(a.Answer.Json)!, 2, 2));
    }

    [Fact]
    public void LiteDicom_UsesSiaarasValuesExactly()
    {
        DicomPreviewOptions p = DicomJob.PreviewOptions;
        Assert.Equal(64L * 1024 * 1024, p.MaxAttachmentBytes);
        Assert.Equal(512, p.MaxBox);
        Assert.Equal(0, p.MaxStripFrames);
        Assert.Equal(16, p.MaxDepth);
        Assert.Equal(50_000, p.MaxElements);
        Assert.Equal(32L * 1024 * 1024, p.MaxInflatedSize);
        Assert.Equal(32L * 1024 * 1024, p.MaxFrameBytes);
        Assert.Equal(14_000_000, p.MaxPixels);
        Assert.Equal(14_000_000, p.MaxTotalPixels);
        Assert.Equal(96L * 1024 * 1024, p.FrameMemoryBudget);
        Assert.Equal(64, p.MaxScans);
        Assert.Equal(1, p.MaxCropFactor);
        Assert.Equal(65_536, p.MaxValueBytes);
        DicomGroupingOptions g = DicomJob.GroupingOptions;
        DicomGroupingOptions r = DicomGroupingOptions.Recommended;
        Assert.Equal(50_000, g.MaxElements);
        Assert.Equal(32L * 1024 * 1024, g.MaxInflatedSize);
        Assert.Equal(64L * 1024 * 1024, g.MaxFileBytes);
        Assert.False(g.IncludePatientFields);
        // The total budgets stay at SIAARA's defaults.
        Assert.Equal(r.MaxFiles, g.MaxFiles);
        Assert.Equal(r.MaxTotalBytes, g.MaxTotalBytes);
        Assert.Equal(r.MaxTotalInflated, g.MaxTotalInflated);
        Assert.Equal(r.MaxResultBytes, g.MaxResultBytes);
    }

    [Fact]
    public void AZipsFiles_AreReadFromOneOpening_OneAtATime_TheTooLargeSkipped()
    {
        byte[] small = TestDicom.Slice("1", 1, 0);
        byte[] zip = TestFiles.ZipBytes(("a.dcm", small), ("b.dcm", TestDicom.Slice("1", 2, 5, size: 64)), ("c.txt", "note"u8.ToArray()));
        ArchiveListing listing = ArchiveReader.List(zip);
        var seen = new List<(string, bool, int)>();
        string status = ArchiveReader.EachUnpacked(zip, listing, listing.View.Entries, null, small.Length, (item, given, bytes) => seen.Add((item.Name, given, bytes.Length)), CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, status);
        Assert.Equal(("a.dcm", true, small.Length), seen[0]);
        // b.dcm is larger than the cap given here: skipped before anything is unpacked.
        Assert.False(seen[1].Item2);
        Assert.Equal(("c.txt", true, 4), seen[2]);
    }
}
