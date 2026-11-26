using System.Globalization;
using Anjal.Viewing;
using Siaara;

namespace Anjal.Viewer;

/// <summary>
/// rc.30 (DES-22 §3.4 and §18.3; D-372, AC-19, D-386, D-366): DICOM from SIAARA v0.1.0-rc.1 - a file's preview, its
/// patient fields on request, and a ZIP's files grouped by patient, study and series. Every member used here was
/// checked against the package's XML docs. Run only in a single-use child: the toolkit's reads have no time limit
/// of their own, so the supervisor's deadline and memory stop are what bound them.
/// </summary>
public static class DicomJob
{
    /// <summary>The longest caption field kept (DES-22 §3.4: each cut at 256 characters).</summary>
    public const int MaxText = 256;

    /// <summary>rc.31 (lite DICOM): the most one file of a ZIP may weigh to be grouped.</summary>
    public const int MaxMemberBytes = 64 * 1024 * 1024;

    /// <summary>
    /// rc.31 (lite DICOM, SIAARA's values exactly, the coordinator 25 Nov 2026): the grouping's per-file budgets
    /// lowered to 50,000 elements, 32 MiB inflated and 64 MiB a file; the total budgets at SIAARA's defaults; the
    /// patient fields never read (D-366: patients are told apart without them).
    /// </summary>
    public static DicomGroupingOptions GroupingOptions => DicomGroupingOptions.Recommended with
    {
        MaxElements = 50_000,
        MaxInflatedSize = 32 * 1024 * 1024,
        MaxFileBytes = MaxMemberBytes,
        IncludePatientFields = false,
    };

    /// <summary>
    /// rc.31 (lite DICOM, SIAARA's values exactly): the preview's limits for a mail server's small memory - a 512 px
    /// picture with no strip, 50,000 elements, 32 MiB inflated or a frame, 14 million pixels, a 96 MiB decoder
    /// budget. The picture stays RGBA8.
    /// </summary>
    public static DicomPreviewOptions PreviewOptions => new()
    {
        MaxAttachmentBytes = 64 * 1024 * 1024,
        MaxBox = 512,
        MaxStripFrames = 0,
        MaxDepth = 16,
        MaxElements = 50_000,
        MaxInflatedSize = 32 * 1024 * 1024,
        MaxFrameBytes = 32 * 1024 * 1024,
        MaxPixels = 14_000_000,
        MaxTotalPixels = 14_000_000,
        FrameMemoryBudget = 96 * 1024 * 1024,
        MaxScans = 64,
        MaxCropFactor = 1,
        MaxValueBytes = 65_536,
    };

    /// <summary>Previews a DICOM file: the caption, the picture and, for several frames, the strip.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer: the caption as JSON, the picture as its first file and the strip as its second.</returns>
    public static JobAnswer Run(byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // Create never throws for bad input; its limits bound the memory, the child's deadline the time.
        DicomPreview p = DicomAttachmentPreview.Create(bytes, PreviewOptions);
        ct.ThrowIfCancellationRequested();
        var files = new List<byte[]>();
        int stripFrames = 0;
        if (p.Outcome == DicomPreviewOutcome.Ok && p.Image is not null)
        {
            files.Add(p.Image.ToPng());
            if (p.Strip is not null && p.StripFrames > 1)
            {
                files.Add(p.Strip.ToPng());
                stripFrames = p.StripFrames;
            }
        }
        var view = new DicomView
        {
            Outcome = p.Outcome.ToString(),
            Modality = Text(p.Modality),
            StudyDate = Text(p.StudyDate),
            Series = Text(p.SeriesDescription),
            Rows = p.Rows > 0 ? p.Rows.ToString(CultureInfo.InvariantCulture) : null,
            Columns = p.Columns > 0 ? p.Columns.ToString(CultureInfo.InvariantCulture) : null,
            Frames = p.Frames > 1 ? p.Frames.ToString(CultureInfo.InvariantCulture) : null,
            TransferSyntax = Text(p.TransferSyntaxName),
            Incomplete = p.Incomplete,
            Reason = Text(p.Reason),
            StripFrames = stripFrames,
        };
        return JobAnswer.Json(ViewJson.Write(view), files);
    }

    /// <summary>
    /// D-366: Patient's Name and Patient ID, read header-only with the preview's limits - only for Show patient
    /// details, which the webmail offers to the mailbox's own person and logs.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The fields, or "unsupported" when the bytes are not a readable DICOM file.</returns>
    public static JobAnswer Patient(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (DicomAttachmentPreview.ReadPatientFields(bytes, PreviewOptions) is not { } fields)
        {
            return JobAnswer.Status(ViewStatus.Unsupported, null);
        }
        return JobAnswer.Json(ViewJson.Write(new DicomPatient { Name = Text(PersonName(fields.Item1)), Id = Text(fields.Item2) }));
    }

    /// <summary>
    /// A DICOM Person Name as people write it: "FAMILY^Given^Middle^Prefix^Suffix" becomes "FAMILY, Prefix Given Middle
    /// Suffix", from its first group (the ones after "=" are the same name in other scripts or spellings).
    /// </summary>
    /// <param name="stored">The name as the file stores it.</param>
    /// <returns>The name to show, or null for none.</returns>
    public static string? PersonName(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }
        string[] parts = stored.Split('=')[0].Split('^').Select(p => p.Trim()).ToArray();
        string Part(int i) => i < parts.Length ? parts[i] : string.Empty;
        string rest = string.Join(" ", new[] { Part(3), Part(1), Part(2), Part(4) }.Where(p => p.Length > 0));
        string name = Part(0).Length == 0 ? rest : rest.Length == 0 ? Part(0) : Part(0) + ", " + rest;
        return name.Length == 0 ? stored.Trim() : name;
    }

    /// <summary>
    /// AC-19: the ZIP's own DICOM files grouped by patient, study and series, with its DICOMDIR when it has one.
    /// rc.31 (lite DICOM): the archive is opened once; each file's size is checked against 64 MiB first, then it is
    /// copied through one reused buffer with the archive viewer's caps and given to the grouping (which reads it
    /// header-only and keeps none of it) before the next overwrites it.
    /// </summary>
    /// <param name="zip">The attachment.</param>
    /// <param name="passwords">The archive's password, when it is locked.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The grouping, or the status that says why there is none.</returns>
    public static JobAnswer Group(byte[] zip, ArchivePasswords passwords, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArchiveListing listing = ArchiveReader.List(zip, null, passwords);
        if (!listing.Listed)
        {
            return JobAnswer.Status(listing.Status == ViewStatus.Ok ? ViewStatus.Refused : listing.Status, null);
        }
        List<ArchiveItem> dicom = listing.View.Entries.Where(e => e.Opens == FileKinds.Dicom && !e.Folder && e.Index >= 0).ToList();
        if (dicom.Count == 0)
        {
            return JobAnswer.Status(ViewStatus.Unsupported, null);
        }
        using var grouping = new DicomStudyGrouping(GroupingOptions);
        var places = new List<string>();
        int skipped = 0;
        string opened = ArchiveReader.EachUnpacked(zip, listing, dicom, passwords.Outer, MaxMemberBytes, (item, given, bytes) =>
        {
            if (!given)
            {
                skipped++;
                return;
            }
            if (string.Equals(item.Name, "DICOMDIR", StringComparison.OrdinalIgnoreCase))
            {
                grouping.AddDicomdir(bytes);
                return;
            }
            // The member's path is its name: what a DICOMDIR's File IDs are matched against.
            grouping.Add(item.Path, bytes);
            places.Add(item.Index.ToString(CultureInfo.InvariantCulture));
        }, ct);
        if (opened != ViewStatus.Ok)
        {
            return JobAnswer.Status(opened, null);
        }
        DicomGroupingResult result = grouping.Result;
        var view = new DicomStudyView
        {
            Files = dicom.Count,
            FilesRead = (int)Math.Min(int.MaxValue, result.FilesRead),
            Ungrouped = result.Ungrouped.Count,
            Incomplete = result.Instances.Count(i => Lost(i.ReadLoss)),
            Budget = result.BudgetReached == DicomGroupingLimit.None ? null : result.BudgetReached.ToString(),
            Skipped = skipped,
            Issues = result.Issues.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => new DicomIssueCount { Kind = g.Key.ToString(), Count = g.Count() }).ToList(),
            Patients = Patients(result, places),
        };
        return JobAnswer.Json(ViewJson.Write(view));
    }

    // The tree, patients by number: a series lists its slices as the archive's places, in the toolkit's order.
    private static List<DicomPatientGroup> Patients(DicomGroupingResult result, List<string> places)
    {
        var patients = new List<DicomPatientGroup>();
        int seriesNumber = 0;
        foreach (DicomGroupedPatient patient in result.Patients)
        {
            var studies = new List<DicomStudyGroup>();
            foreach (DicomGroupedStudy study in patient.Studies)
            {
                var series = new List<DicomSeriesGroup>();
                foreach (DicomGroupedSeries s in study.Series)
                {
                    List<DicomGroupedInstance> slices = s.Instances.Where(i => i.File >= 0 && i.File < places.Count && !i.IsDuplicate).ToList();
                    if (slices.Count == 0)
                    {
                        continue;
                    }
                    series.Add(new DicomSeriesGroup
                    {
                        Number = ++seriesNumber,
                        Modality = Text(s.Modality?.Display),
                        Description = Text(s.SeriesDescription?.Display),
                        Slices = slices.Count,
                        Frames = slices.Sum(i => Math.Max(1, i.NumberOfFrames ?? 1)),
                        Entries = slices.Select(i => places[i.File]).ToList(),
                    });
                }
                if (series.Count > 0)
                {
                    studies.Add(new DicomStudyGroup { Date = Text(study.StudyDate?.Display), Series = series });
                }
            }
            if (studies.Count > 0)
            {
                patients.Add(new DicomPatientGroup { Number = patients.Count + 1, Studies = studies });
            }
        }
        return patients;
    }

    // A header read that lost something: bytes skipped, the input cut short, or recovered. The grouping reads every
    // file header-only on purpose, so that bit (DicomReadLoss.HeaderOnly, set on every grouped file in rc.1) is not a
    // loss.
    private static bool Lost(int readLoss) => ((DicomReadLoss)readLoss & ~DicomReadLoss.HeaderOnly) != DicomReadLoss.None;

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > MaxText ? value[..MaxText] : value;
}
