using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.22 (item 50, design review 6k): the patient-information warning in Compose. When a message to
/// someone outside the organisation looks like it carries patient information - a record number,
/// clinical words in the subject or body, or a file named like a report - the person is asked to
/// check before it goes. A warning, never a block: the person decides.
/// </summary>
public static partial class PatientInformation
{
    // Whole words or phrases only (\b): "ward" would match "forward", "case" would match "showcase".
    private static readonly string[] Words =
    {
        "discharge summary", "case sheet", "case summary", "medical record", "patient", "diagnosis", "prescription", "lab report", "lab results",
        "pathology", "histopathology", "biopsy", "radiology report", "ct scan", "mri", "pet-ct", "pet ct", "x-ray", "ultrasound", "ecg", "echo report",
        "treatment plan", "chemotherapy", "radiotherapy", "operation notes", "op notes", "referral", "ipd", "opd", "bed no",
        "mrn", "uhid", "hospital number", "ip number", "op number", "consent form", "death certificate", "fitness certificate", "sick leave certificate",
    };

    private static readonly Regex[] WordPatterns = Words.Select(w => new Regex(@"\b" + Regex.Escape(w) + @"\b", RegexOptions.CultureInvariant)).ToArray();

    private static readonly string[] FileWords = { "discharge", "prescription", "biopsy", "patient", "mrn", "uhid", "referral", "consent", "case-sheet", "case sheet", "lab-report", "lab report" };

    /// <summary>
    /// What in a message looks like patient information: the reasons, in words, or none. Checked only
    /// for mail to an address outside the organisation.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The text.</param>
    /// <param name="fileNames">The attachments' names.</param>
    /// <returns>The reasons; empty when nothing was found.</returns>
    public static IReadOnlyList<string> Signs(string? subject, string? body, IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var reasons = new List<string>();
        string text = ((subject ?? string.Empty) + "\n" + (body ?? string.Empty)).ToLowerInvariant();
        if (RecordNumber().IsMatch(text))
        {
            reasons.Add("a medical record number");
        }
        var found = new List<string>();
        for (int i = 0; i < Words.Length; i++)
        {
            if (WordPatterns[i].IsMatch(text))
            {
                found.Add("\"" + Words[i] + "\"");
            }
            if (found.Count == 3)
            {
                break;
            }
        }
        if (found.Count > 0)
        {
            reasons.Add(string.Join(", ", found));
        }
        foreach (string f in fileNames)
        {
            string lower = (f ?? string.Empty).ToLowerInvariant();
            if (FileWords.Any(w => lower.Contains(w, StringComparison.Ordinal)) || RecordNumber().IsMatch(lower))
            {
                reasons.Add("a file named " + f);
                break;
            }
        }
        return reasons;
    }

    // MRN 2026-10443, UHID 00123456, IP 2211/26, a bare 8-digit hospital number after "no".
    [GeneratedRegex(@"\b(mrn|uhid|ip ?no|op ?no|hospital no|reg(istration)? no)\.?\s*[:#-]?\s*[a-z]?\d{3,}[-/]?\d*", RegexOptions.CultureInvariant)]
    private static partial Regex RecordNumber();
}
