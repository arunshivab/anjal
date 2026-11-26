using System.Text;
using Anjal.Viewing;

namespace Anjal.Viewer.Tests;

/// <summary>
/// rc.30 (DES-22 §3.4, §18.3; D-372, AC-19, D-386, D-366): DICOM on SIAARA v0.1.0-rc.1 - a file's preview, its patient
/// details only when asked, a ZIP grouped by patient, study and series in scroll order, and hostile files said
/// plainly. The files are made up (TestDicom); no real patient's data.
/// </summary>
public sealed class DicomTests
{
    private static readonly int[] SeriesNumbers = { 1, 2 };
    private static readonly string[] Upward = { "ct-1.dcm", "ct-2.dcm", "ct-3.dcm" };

    [Fact]
    public void ASlice_IsPreviewed_WithItsCaption_AndNeverItsPatient()
    {
        JobAnswer a = DicomJob.Run(TestDicom.Slice("1", 1, 0, size: 64), CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, a.Answer.Status);
        DicomView view = ViewJson.ReadDicom(a.Answer.Json)!;
        Assert.Equal("Ok", view.Outcome);
        Assert.Equal("CT", view.Modality);
        Assert.Equal("Chest", view.Series);
        Assert.Equal("64", view.Rows);
        Assert.False(view.Incomplete);
        Assert.Equal("image/png", FileKinds.PictureType(Assert.Single(a.Files)));
        Assert.DoesNotContain("TEST", a.Answer.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfSeveralFrames_ShowsItsFirstFrame_WithNoStrip()
    {
        // rc.31 (lite DICOM, SIAARA's values): MaxStripFrames 0, so a file of several frames shows its first frame
        // and says how many it has; the strip is not drawn (rc.30 drew one).
        JobAnswer a = DicomJob.Run(TestDicom.Slice("1", 1, 0, size: 32, frames: 4), CancellationToken.None);
        DicomView view = ViewJson.ReadDicom(a.Answer.Json)!;
        Assert.Equal("4", view.Frames);
        Assert.Equal(0, view.StripFrames);
        Assert.Equal("image/png", FileKinds.PictureType(Assert.Single(a.Files)));
    }

    [Fact]
    public void PatientDetails_AreReadOnlyWhenAsked()
    {
        byte[] slice = TestDicom.Slice("1", 1, 0);
        DicomPatient who = ViewJson.ReadDicomPatient(ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomPatient }, slice, CancellationToken.None).Answer.Json)!;
        // Shown as people write it, not as the file stores it ("TEST^PATIENT").
        Assert.Equal("TEST, PATIENT", who.Name);
        Assert.Equal("TEST-0001", who.Id);
        Assert.Equal(ViewStatus.Unsupported, DicomJob.Patient(Encoding.ASCII.GetBytes("not a DICOM file")).Answer.Status);
    }

    [Theory]
    [InlineData("DOE^JANE^Q^Dr^Jr", "DOE, Dr JANE Q Jr")]
    [InlineData("DOE^JANE", "DOE, JANE")]
    [InlineData("DOE", "DOE")]
    [InlineData("^JANE", "JANE")]
    [InlineData("Yamada^Tarou=山田^太郎=やまだ^たろう", "Yamada, Tarou")]
    [InlineData("^^^^", "^^^^")]
    [InlineData("  ", null)]
    public void APersonName_IsShownAsPeopleWriteIt(string stored, string? shown)
    {
        Assert.Equal(shown, DicomJob.PersonName(stored));
    }

    [Fact]
    public void HostileFiles_AreSaidPlainly_AndNeverStopTheChild()
    {
        DicomView cut = ViewJson.ReadDicom(DicomJob.Run(TestDicom.Truncated(), CancellationToken.None).Answer.Json)!;
        Assert.True(cut.Incomplete || cut.Outcome != "Ok", cut.Outcome);
        DicomView deep = ViewJson.ReadDicom(DicomJob.Run(TestDicom.DeeplyNested(), CancellationToken.None).Answer.Json)!;
        Assert.NotEqual("Ok", deep.Outcome);
        Assert.Equal("NotDicom", ViewJson.ReadDicom(DicomJob.Run(Encoding.ASCII.GetBytes("plain text"), CancellationToken.None).Answer.Json)!.Outcome);
        byte[] marked = new byte[200];
        "DICM"u8.CopyTo(marked.AsSpan(128));
        Assert.NotEqual("Ok", ViewJson.ReadDicom(DicomJob.Run(marked, CancellationToken.None).Answer.Json)!.Outcome);
    }

    [Fact]
    public void AZipOfSlices_IsGroupedByPatientStudyAndSeries_InScrollOrder()
    {
        byte[] junkDicomdir = new byte[400];
        "DICM"u8.CopyTo(junkDicomdir.AsSpan(128));
        byte[] zip = TestFiles.ZipBytes(
            ("study/", null),
            ("study/ct-3.dcm", TestDicom.Slice("1", 3, 30)),
            ("study/ct-1.dcm", TestDicom.Slice("1", 1, 10)),
            ("study/notes.txt", Encoding.UTF8.GetBytes("Seen in clinic.")),
            ("study/ct-2.dcm", TestDicom.Slice("1", 2, 20)),
            ("study/mr-2.dcm", TestDicom.Slice("2", 2, 5, modality: "MR", description: "Head")),
            ("study/mr-1.dcm", TestDicom.Slice("2", 1, 0, modality: "MR", description: "Head")),
            ("DICOMDIR", junkDicomdir));
        JobAnswer a = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomGroup }, zip, CancellationToken.None);
        Assert.Equal(ViewStatus.Ok, a.Answer.Status);
        DicomStudyView study = ViewJson.ReadStudy(a.Answer.Json)!;
        DicomPatientGroup patient = Assert.Single(study.Patients);
        Assert.Equal(1, patient.Number);
        DicomStudyGroup one = Assert.Single(patient.Studies);
        Assert.Equal(2, one.Series.Count);
        DicomSeriesGroup ct = Assert.Single(one.Series, s => s.Modality == "CT");
        DicomSeriesGroup mr = Assert.Single(one.Series, s => s.Modality == "MR");
        Assert.Equal(3, ct.Slices);
        Assert.Equal(2, mr.Slices);
        Assert.Equal("Head", mr.Description);
        Assert.Equal(SeriesNumbers, one.Series.Select(s => s.Number).Order().ToArray());

        // Scroll order: by position along the slices' normal, not the ZIP's order (ct-3, ct-1, ct-2).
        ArchiveView listing = ViewJson.ReadArchive(ChildJobs.Run(new ViewRequest { Kind = ViewKinds.ArchiveList }, zip, CancellationToken.None).Answer.Json)!;
        string[] names = ct.Entries.Select(place => listing.Entries.Single(e => e.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) == place).Name).ToArray();
        Assert.True(names.SequenceEqual(Upward) || names.SequenceEqual(Upward.Reverse()), string.Join(",", names));

        // Each slice opens in the single-file preview by its place.
        JobAnswer slice = ChildJobs.Run(new ViewRequest { Kind = ViewKinds.Dicom, Entry = ct.Entries[0] }, zip, CancellationToken.None);
        Assert.Equal("Ok", ViewJson.ReadDicom(slice.Answer.Json)!.Outcome);

        // No patient's name or ID anywhere; the broken DICOMDIR is said, not hidden.
        Assert.DoesNotContain("TEST", a.Answer.Json, StringComparison.Ordinal);
        Assert.Contains(study.Issues, i => i.Kind.StartsWith("Dicomdir", StringComparison.Ordinal));
        Assert.Equal(6, study.Files);
        Assert.Null(study.Budget);
        Assert.Equal(0, study.Incomplete);

        // A slice cut short is counted as incomplete.
        byte[] cut = TestFiles.ZipBytes(("a.dcm", TestDicom.Slice("1", 1, 0)), ("b.dcm", TestDicom.Slice("1", 2, 5)[..400]));
        DicomStudyView partial = ViewJson.ReadStudy(ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomGroup }, cut, CancellationToken.None).Answer.Json)!;
        Assert.True(partial.Incomplete + partial.Issues.Sum(i => i.Count) >= 1, a.Answer.Json);
    }

    [Fact]
    public void TheGrouping_TakesOnlyTheWholeZip_AndSaysWhenThereIsNothingToGroup()
    {
        byte[] zip = TestFiles.ZipBytes(("a.dcm", TestDicom.Slice("1", 1, 0)));
        Assert.Equal(ViewStatus.Refused, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomGroup, Entry = "0" }, zip, CancellationToken.None).Answer.Status);
        Assert.Equal(ViewStatus.Unsupported, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomGroup }, TestFiles.Zip(("a.txt", "x")), CancellationToken.None).Answer.Status);
        Assert.NotEqual(ViewStatus.Ok, ChildJobs.Run(new ViewRequest { Kind = ViewKinds.DicomGroup }, Encoding.ASCII.GetBytes("not a zip"), CancellationToken.None).Answer.Status);
        Assert.False(DicomJob.GroupingOptions.IncludePatientFields);
    }
}
