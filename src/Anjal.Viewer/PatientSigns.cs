using System.Text;
using System.Text.RegularExpressions;
using Anjal.Viewing;
using Chuvadi.Pdf.Content;
using Chuvadi.Pdf.Documents;
using Chuvadi.Pdf.Primitives;
using Chuvadi.Pdf.Text;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 part 2 (DES-22 §7.6): the patient and personal signs Anjal already knows (the patterns of the patient
/// warning, Anjal.Mailbox.PatientInformation) found in a PDF's text, each with its box on its page as shown - offered
/// as boxes to keep or drop, never redacted without the person.
/// </summary>
public static partial class PatientSigns
{
    /// <summary>At most this many signs are given back from one file.</summary>
    public const int MaxHits = 500;

    /// <summary>The signs found in a PDF, page by page.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="password">Its password, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Find(byte[] bytes, string? password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > PdfJobs.Limits.MaxInputBytes)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "input");
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            // D-405: the limits hold from the first byte read, the password probe included.
            using PdfWorkScope scope = PdfWorkScope.Enter(PdfJobs.Limits, ct);
            if (string.IsNullOrEmpty(password) && PdfDocument.RequiresPassword(stream))
            {
                return JobAnswer.Status(ViewStatus.NeedsPassword, null);
            }
            stream.Position = 0;
            using PdfDocument doc = PdfDocument.Open(stream, new PdfOpenOptions { Password = password, Limits = PdfJobs.Limits, RepairIfDamaged = true });
            var hits = new List<FindHit>();
            var extractor = new TextExtractor(doc.Objects);
            for (int p = 0; p < doc.PageCount && hits.Count < MaxHits; p++)
            {
                ct.ThrowIfCancellationRequested();
                var g = new PageGeometry(doc.Pages[p]);
                List<TextFragment> fragments = extractor.ExtractFragments(doc.Pages[p], ct)
                    .Where(f => !string.IsNullOrEmpty(f.Text) && double.IsFinite(f.X) && double.IsFinite(f.Y))
                    .ToList();
                foreach ((string sign, List<Box> boxes) in OnPage(fragments, g))
                {
                    hits.Add(new FindHit { Page = p, Sign = sign, Boxes = boxes });
                    if (hits.Count >= MaxHits)
                    {
                        break;
                    }
                }
            }
            return JobAnswer.Json(ViewJson.Write(new FindResult { Hits = hits, More = hits.Count >= MaxHits }));
        }
        catch (PdfPasswordRequiredException ex)
        {
            return JobAnswer.Status(ex.PasswordSupplied ? ViewStatus.WrongPassword : ViewStatus.NeedsPassword, null);
        }
        catch (PdfLimitExceededException ex)
        {
            return JobAnswer.Status(ViewStatus.TooLarge, "pdf " + ex.LimitName);
        }
        catch (PdfException ex)
        {
            return JobAnswer.Status(ViewStatus.Damaged, "pdf " + ex.GetType().Name);
        }
    }

    // The page's text joined in reading order, each pattern's matches mapped back to the runs they fall in, the
    // part of each run measured by its share of the run's letters.
    private static IEnumerable<(string Sign, List<Box> Boxes)> OnPage(List<TextFragment> fragments, PageGeometry g)
    {
        var text = new StringBuilder();
        var starts = new List<int>(fragments.Count);
        foreach (TextFragment f in fragments)
        {
            starts.Add(text.Length);
            text.Append(f.Text).Append(' ');
        }
        string lower = text.ToString().ToLowerInvariant();
        foreach ((string sign, Regex pattern) in Patterns)
        {
            foreach (Match m in pattern.Matches(lower))
            {
                var boxes = new List<Box>();
                for (int i = 0; i < fragments.Count; i++)
                {
                    TextFragment f = fragments[i];
                    int from = Math.Max(m.Index, starts[i]);
                    int to = Math.Min(m.Index + m.Length, starts[i] + f.Text.Length);
                    if (to <= from)
                    {
                        continue;
                    }
                    double size = double.IsFinite(f.FontSize) && f.FontSize > 0 ? f.FontSize : 10;
                    double width = double.IsFinite(f.Width) && f.Width > 0 ? f.Width : f.Text.Length * size * 0.5;
                    double x = f.X + (width * (from - starts[i]) / f.Text.Length);
                    double w = width * (to - from) / f.Text.Length;
                    // From a little below the baseline to the top of the letters, in the page's own space.
                    Box shown = g.ToShownBox(x, f.Y - (size * 0.25), w, size * 1.1);
                    boxes.Add(new Box
                    {
                        X = Math.Round(shown.X - PdfEdit.FindPadding, 2),
                        Y = Math.Round(shown.Y - PdfEdit.FindPadding, 2),
                        W = Math.Round(shown.W + (2 * PdfEdit.FindPadding), 2),
                        H = Math.Round(shown.H + (2 * PdfEdit.FindPadding), 2),
                    });
                }
                if (boxes.Count > 0)
                {
                    yield return (sign, boxes);
                }
            }
        }
    }

    // The patient warning's own patterns (Anjal.Mailbox.PatientInformation), on lower-case text.
    private static readonly (string Sign, Regex Pattern)[] Patterns =
    {
        ("record", RecordNumber()),
        ("aadhaar", Aadhaar()),
        ("pan", Pan()),
        ("phone", Phone()),
    };

    // MRN 2026-10443, UHID 00123456, IP 2211/26, a bare 8-digit hospital number after "no".
    [GeneratedRegex(@"\b(mrn|uhid|ip ?no|op ?no|hospital no|reg(istration)? no)\.?\s*[:#-]?\s*[a-z]?\d{3,}[-/]?\d*", RegexOptions.CultureInvariant)]
    private static partial Regex RecordNumber();

    // 12 digits in three groups of four (2345 6789 0123), the first digit 2-9, or after the word.
    [GeneratedRegex(@"\b[2-9]\d{3}[ -]\d{4}[ -]\d{4}\b|\baadha?ar\b\D{0,12}\d{4}\s?\d{4}\s?\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex Aadhaar();

    // ABCDE1234F.
    [GeneratedRegex(@"\b[a-z]{5}\d{4}[a-z]\b", RegexOptions.CultureInvariant)]
    private static partial Regex Pan();

    // An Indian mobile number: ten digits starting 6-9, with or without +91 and spaces.
    [GeneratedRegex(@"(?<!\d)(\+91[ -]?)?[6-9]\d{4}[ -]?\d{5}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();
}
