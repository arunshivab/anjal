using System.Text;
using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.28 (DES-18 §10, D-337) and rc.29 (AC-17, D-381, AC-20): archives read in the viewer process with Chuvadi.Zip -
/// the whole-archive budgets, each entry's own limits, two levels and no more, programs never given out, names in
/// UTF-8 or the DOS code page, locked archives opened with their password for viewing only, and methods Anjal
/// cannot unpack said plainly.
/// </summary>
public sealed class ArchiveTests
{
    private static readonly string[] TreeNames = { "Quality", "Audit", "Log.txt", "Agenda.pdf", "Minutes.docx", "Quality-old.txt", "Readme.txt" };
    private static readonly int[] TreeDepths = { 0, 1, 2, 1, 0, 0, 0 };
    private static readonly byte[] LinuxProgram = { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };

    [Fact]
    public void AnOrdinaryArchive_IsListed_WithFoldersAndSizes()
    {
        byte[] zip = TestFiles.ZipBytes(("reports/", null), ("reports/cbc.pdf", "%PDF-1.4 results"u8.ToArray()), ("notes.txt", "Call back on Tuesday."u8.ToArray()));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Null(view.Refused);
        Assert.False(view.Locked);
        Assert.Equal(3, view.Entries.Count);
        ArchiveItem folder = view.Entries[0];
        Assert.True(folder.Folder);
        ArchiveItem cbc = view.Entries[1];
        Assert.Equal("cbc.pdf", cbc.Name);
        Assert.Equal(1, cbc.Depth);
        Assert.Equal(FileKinds.Pdf, cbc.Opens);
        Assert.Null(view.Entries[2].Opens);
        EntryResult notes = ArchiveReader.Entry(zip, "2");
        Assert.Equal("notes.txt", notes.Name);
        Assert.False(notes.Locked);
        Assert.Equal("Call back on Tuesday.", Encoding.UTF8.GetString(notes.Bytes!));
    }

    [Fact]
    public void FoldersWithNoEntryOfTheirOwn_AreShown_AndEachFileFollowsItsFolder()
    {
        // Many tools write "Quality/Agenda.pdf" with no "Quality/" entry, and in any order.
        byte[] zip = TestFiles.ZipBytes(("Readme.txt", "x"u8.ToArray()), ("Quality/Agenda.pdf", "%PDF-1.4 a"u8.ToArray()), ("Minutes.docx", "x"u8.ToArray()),
            ("Quality/Audit/Log.txt", "x"u8.ToArray()), ("Quality-old.txt", "x"u8.ToArray()));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Equal(TreeNames, view.Entries.Select(e => e.Name));
        Assert.Equal(TreeDepths, view.Entries.Select(e => e.Depth));
        ArchiveItem quality = view.Entries[0];
        Assert.True(quality.Folder);
        Assert.True(quality.Index < 0);
        Assert.Equal(2, view.Entries.Count(e => e.Index < 0));

        // The files keep their own places in the archive, so they still open.
        ArchiveItem agenda = view.Entries.Single(e => e.Name == "Agenda.pdf");
        Assert.Equal(1, agenda.Index);
        Assert.Equal("Agenda.pdf", ArchiveReader.Entry(zip, "1").Name);
    }

    [Fact]
    public void Programs_AreBlocked_ByNameAndByTheirFirstBytes()
    {
        byte[] zip = TestFiles.ZipBytes(("setup.exe", new byte[] { 1, 2, 3, 4 }), ("photo.jpg", LinuxProgram), ("ok.txt", "fine"u8.ToArray()));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.True(view.Entries.Single(e => e.Name == "setup.exe").Blocked);
        Assert.True(view.Entries.Single(e => e.Name == "photo.jpg").Blocked);
        Assert.False(view.Entries.Single(e => e.Name == "ok.txt").Blocked);
        Assert.Null(ArchiveReader.Entry(zip, "0").Bytes);
        Assert.Null(ArchiveReader.Entry(zip, "1").Bytes);
        Assert.NotNull(ArchiveReader.Entry(zip, "2").Bytes);
    }

    [Fact]
    public void ABombAcrossTheArchive_IsRefused_BeforeAnythingIsUnpacked()
    {
        byte[] zip = TestFiles.ZipBytes(("zeros.bin", new byte[20 * 1024 * 1024]));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Equal(ArchiveRefusals.TooPacked, view.Refused);
        Assert.Null(ArchiveReader.Entry(zip, "0").Bytes);
    }

    [Fact]
    public void ABombInOneEntry_IsNotUnpacked_EvenWhenTheWholeArchivePasses()
    {
        // Random bytes pull the whole archive's ratio under 100:1; the zeros alone are far above it.
        byte[] noise = new byte[400 * 1024];
        new Random(7).NextBytes(noise);
        byte[] zip = TestFiles.ZipBytes(("noise.bin", noise), ("zeros.pdf", new byte[30 * 1024 * 1024]));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Null(view.Refused);
        Assert.Null(ArchiveReader.Entry(zip, "1").Bytes);
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfInfo, Entry = "1" }, zip, CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void MoreThanTwoThousandEntries_AreRefused()
    {
        var entries = Enumerable.Range(0, 2001).Select(i => ("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), (byte[]?)new byte[] { 1 })).ToArray();
        Assert.Equal(ArchiveRefusals.TooMany, ArchiveReader.List(TestFiles.ZipBytes(entries)).View.Refused);
    }

    [Fact]
    public void AnEntryWhoseHeaderLies_IsNotUnpackedPastWhatItSaid()
    {
        byte[] zip = TestFiles.ZipBytes(("a.txt", Encoding.ASCII.GetBytes(new string('x', 5000))));
        // The central directory's "uncompressed size" (offset 24 in the record) is made smaller than the data.
        int cd = FindLast(zip, 0x02014b50);
        BitConverter.GetBytes(10).CopyTo(zip, cd + 24);
        // Whatever comes back is never more than the 10 bytes the header claims.
        Assert.True(ArchiveReader.Entry(zip, "0").Bytes is not byte[] body || body.Length <= 10);
    }

    [Fact]
    public void PathsThatClimbOut_AreOnlyText_AndADownloadUsesTheLastName()
    {
        byte[] zip = TestFiles.ZipBytes(("../../etc/passwd", "x"u8.ToArray()), ("/abs/file.txt", "y"u8.ToArray()));
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Equal(2, view.Entries.Count);
        Assert.DoesNotContain(view.Entries, e => e.Folder);
        Assert.Equal("passwd", view.Entries.Single(e => e.Index == 0).Name);
        Assert.Equal("passwd", ArchiveReader.Entry(zip, "0").Name);
        Assert.Equal("file.txt", ArchiveReader.Entry(zip, "1").Name);
    }

    [Fact]
    public void Names_AreReadAsUtf8_OrInTheDosCodePage()
    {
        byte[] zip = TestFiles.ZipBytes(("QrztebriefQ.txt", "a"u8.ToArray()), ("நோயாளி.txt", "b"u8.ToArray()));
        // "Ärztebrief" written the old way: code page 437 bytes and no UTF-8 flag.
        Replace(zip, "QrztebriefQ"u8.ToArray(), new byte[] { 0x8E, (byte)'r', (byte)'z', (byte)'t', (byte)'e', (byte)'b', (byte)'r', (byte)'i', (byte)'e', (byte)'f', 0x82 });
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Equal("Ärztebriefé.txt", view.Entries[0].Name);
        Assert.Equal("நோயாளி.txt", view.Entries[1].Name);
        Assert.Equal(128, DosCodePage.HighCount);
    }

    [Fact]
    public void AnArchiveInsideTheArchive_IsListed_AndAThirdLevelIsAPlainEntry()
    {
        byte[] third = TestFiles.ZipBytes(("deep.txt", "deep"u8.ToArray()));
        byte[] second = TestFiles.ZipBytes(("lab.pdf", TestFiles.Pdf("Second level")), ("third.zip", third));
        byte[] outer = TestFiles.ZipBytes(("second.zip", second));
        ArchiveView top = ArchiveReader.List(outer).View;
        Assert.Equal(FileKinds.Archive, top.Entries[0].Opens);
        ArchiveView inner = ArchiveReader.List(outer, "0").View;
        Assert.Null(inner.Refused);
        Assert.Equal(FileKinds.Pdf, inner.Entries[0].Opens);
        Assert.Null(inner.Entries[1].Opens);
        Assert.Equal(ArchiveRefusals.TooDeep, ArchiveReader.List(outer, "0/1").View.Refused);
        JobAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfText, Entry = "0/0" }, outer, CancellationToken.None);
        Assert.Contains("Second level", ViewJson.ReadPageText(a.Answer.Json)!.Runs[0].T, StringComparison.Ordinal);
        Assert.NotNull(ArchiveReader.Entry(outer, "0/1").Bytes);
    }

    [Fact]
    public void ALockedArchive_AsksForItsPassword_ThenShowsItsFiles_ButGivesNoneOut()
    {
        byte[] zip = TestFiles.LockedZip("lotus", ("results/lab.pdf", TestFiles.Pdf("Locked and shown")), ("notes.txt", "Private notes"u8.ToArray()));

        // No password: asked for, for the attachment itself ("archive:" and no place).
        ArchiveListing none = ArchiveReader.List(zip);
        Assert.Equal(ViewStatus.NeedsPassword, none.Status);
        Assert.Equal(string.Empty, none.Place);
        ViewAnswer asked = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveList }, zip, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.NeedsPassword, asked.Status);
        Assert.Equal("archive:", asked.Detail);

        // A wrong one: said so, nothing listed.
        Assert.Equal(ViewStatus.WrongPassword, ArchiveReader.List(zip, null, new ArchivePasswords("tulip", null)).Status);
        Assert.Equal(ViewStatus.WrongPassword, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveList, Password = "tulip" }, zip, CancellationToken.None).Answer.Status);

        // The right one: listed, marked locked, and its files open in the viewers.
        ArchiveListing open = ArchiveReader.List(zip, null, new ArchivePasswords("lotus", null));
        Assert.True(open.Listed);
        Assert.True(open.View.Locked);
        ArchiveItem pdf = open.View.Entries.Single(e => e.Name == "lab.pdf");
        Assert.Equal(FileKinds.Pdf, pdf.Opens);
        JobAnswer text = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfText, Entry = pdf.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), ArchivePassword = "lotus" }, zip, CancellationToken.None);
        Assert.Contains("Locked and shown", ViewJson.ReadPageText(text.Answer.Json)!.Runs[0].T, StringComparison.Ordinal);
        EntryResult notes = ArchiveReader.Entry(zip, "1", new ArchivePasswords("lotus", null));
        Assert.True(notes.Locked);
        Assert.Equal("Private notes", Encoding.UTF8.GetString(notes.Bytes!));

        // Never downloaded one by one: the files were not virus-scanned (AC-20).
        ViewAnswer download = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = "1", ArchivePassword = "lotus" }, zip, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.Refused, download.Status);
        Assert.Equal("locked", download.Detail);

        // A file inside, asked for without the archive's password: the archive's password is asked for.
        ViewAnswer inner = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfInfo, Entry = "0" }, zip, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.NeedsPassword, inner.Status);
        Assert.Equal("archive:", inner.Detail);
    }

    [Fact]
    public void ALockedArchiveInsideAnArchive_AsksForItsOwnPassword_AndStaysViewOnly()
    {
        byte[] locked = TestFiles.LockedZip("inner-pass", ("scan.pdf", TestFiles.Pdf("Second level, locked")));
        byte[] outer = TestFiles.ZipBytes(("locked.zip", locked), ("open.txt", "plain"u8.ToArray()));
        Assert.False(ArchiveReader.List(outer).View.Locked);
        ViewAnswer asked = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveList, Entry = "0" }, outer, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.NeedsPassword, asked.Status);
        Assert.Equal("archive:0", asked.Detail);
        ArchiveListing listed = ArchiveReader.List(outer, "0", new ArchivePasswords(null, "inner-pass"));
        Assert.True(listed.Listed);
        Assert.True(listed.View.Locked);
        JobAnswer text = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.PdfText, Entry = "0/0", InnerArchivePassword = "inner-pass" }, outer, CancellationToken.None);
        Assert.Contains("Second level, locked", ViewJson.ReadPageText(text.Answer.Json)!.Runs[0].T, StringComparison.Ordinal);
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = "0/0", InnerArchivePassword = "inner-pass" }, outer, CancellationToken.None).Answer.Status);
        // The plain file beside it still downloads.
        Assert.Equal(ViewStatus.Ok, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = "1" }, outer, CancellationToken.None).Answer.Status);
    }

    [Fact]
    public void AnEntryPackedWithAMethodAnjalCannotUnpack_IsSaidPlainly()
    {
        byte[] zip = TestFiles.ZipBytes(("old.bin", Encoding.ASCII.GetBytes(new string('y', 300))), ("ok.txt", "fine"u8.ToArray()));
        // The first entry's method made BZip2 (12) in its local header and in the central directory.
        BitConverter.GetBytes((ushort)12).CopyTo(zip, FindFirst(zip, 0x04034b50) + 8);
        BitConverter.GetBytes((ushort)12).CopyTo(zip, FindFirst(zip, 0x02014b50) + 10);
        ArchiveView view = ArchiveReader.List(zip).View;
        Assert.Null(view.Refused);
        ArchiveItem old = view.Entries.Single(e => e.Name == "old.bin");
        Assert.True(old.Unsupported);
        Assert.Null(old.Opens);
        Assert.False(view.Entries.Single(e => e.Name == "ok.txt").Unsupported);
        ViewAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveEntry, Entry = "0" }, zip, CancellationToken.None).Answer;
        Assert.Equal(ViewStatus.Unsupported, a.Status);
        Assert.Equal("zip-method", a.Detail);
    }

    [Fact]
    public void TheLibrarysCaps_AreItsUntrustedPreset_LoweredToAnjals()
    {
        Chuvadi.Zip.ZipExtractionLimits limits = ArchiveReader.Limits(ArchiveReader.MaxEntries);
        Assert.Equal(ArchiveReader.MaxEntries, limits.MaxEntries);
        Assert.Equal(ArchiveReader.MaxEntryBytes, limits.MaxEntryBytes);
        Assert.Equal(ArchiveReader.MaxExpanded, limits.MaxTotalBytes);
        Assert.Equal(ArchiveReader.MaxRatio, limits.MaxCompressionRatio);
        Assert.Equal(Chuvadi.Zip.ZipExtractionLimits.Untrusted.MaxPathDepth, limits.MaxPathDepth);
        Assert.Equal(Chuvadi.Zip.ZipExtractionLimits.Untrusted.MaxCentralDirectoryBytes, limits.MaxCentralDirectoryBytes);
    }

    [Fact]
    public void NotAnArchive_IsUnreadable()
    {
        Assert.Equal(ArchiveRefusals.Unreadable, ArchiveReader.List("not a zip"u8.ToArray()).View.Refused);
    }

    private static int FindLast(byte[] b, uint signature)
    {
        for (int i = b.Length - 4; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(b, i) == signature)
            {
                return i;
            }
        }
        throw new InvalidOperationException("not found");
    }

    private static int FindFirst(byte[] b, uint signature)
    {
        for (int i = 0; i + 4 <= b.Length; i++)
        {
            if (BitConverter.ToUInt32(b, i) == signature)
            {
                return i;
            }
        }
        throw new InvalidOperationException("not found");
    }

    private static void Replace(byte[] b, byte[] from, byte[] to)
    {
        for (int i = 0; i + from.Length <= b.Length; i++)
        {
            if (b.AsSpan(i, from.Length).SequenceEqual(from))
            {
                to.CopyTo(b, i);
            }
        }
    }
}
