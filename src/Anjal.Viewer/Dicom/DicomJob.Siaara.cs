using System.Globalization;
using Anjal.Viewing;
using Siaara;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.4): the build with the Siaara package (<c>-p:Siaara=true</c>), written against the API in the
/// toolkit's integration note (commit d75fcbd). Not compiled, built or tested until the package is on the feed
/// (the coordinator's go, 12 Nov). When it arrives, every member used here is checked against its XML docs first:
/// in particular the type of <c>Strip</c>, which the note names but does not describe.
/// </summary>
public static partial class DicomJob
{
    private static partial bool AvailableCore() => true;

    private static partial JobAnswer? RunCore(byte[] bytes, CancellationToken ct)
    {
        // Create never throws for bad input; the size limits bound the work, the child's deadline the time.
        DicomPreview p = DicomAttachmentPreview.Create(bytes);
        var view = new DicomView
        {
            Outcome = p.Outcome.ToString(),
            Modality = Text(p.Modality),
            StudyDate = Text(p.StudyDate),
            Series = Text(p.SeriesDescription),
            Rows = Text(p.Rows),
            Columns = Text(p.Columns),
            Frames = Text(p.Frames),
            TransferSyntax = Text(p.TransferSyntaxName),
            Incomplete = p.Incomplete,
            Reason = Text(p.Reason),
        };
        var files = new List<byte[]>();
        if (p.Outcome == DicomPreviewOutcome.Ok && p.Image is not null)
        {
            files.Add(p.Image.ToPng());
            if (p.Strip is not null)
            {
                foreach (var tile in p.Strip)
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(tile.ToPng());
                }
            }
        }
        return JobAnswer.Json(ViewJson.Write(view), files);
    }

    private static string? Text(object? value) => value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}
