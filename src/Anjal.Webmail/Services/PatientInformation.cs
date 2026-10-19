using System.Text.RegularExpressions;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.22 (item 50, design review 6k; owner 19 Oct 2026: personal information too): the warning in Compose. When a message to
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

    private static readonly string[] PersonalWords =
    {
        "aadhaar", "aadhar", "pan card", "pan number", "passport number", "salary", "payslip", "pay slip", "bank account", "account number",
        "ifsc", "credit card", "debit card", "password", "otp", "date of birth", "home address", "medical history",
    };

    private static readonly Regex[] PersonalPatterns = PersonalWords.Select(w => new Regex(@"\b" + Regex.Escape(w) + @"\b", RegexOptions.CultureInvariant)).ToArray();

    private static readonly string[] PersonalFileWords = { "aadhaar", "aadhar", "pan", "passport", "salary", "payslip", "payroll", "bank", "statement", "cv", "resume", "appraisal" };

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
        Found(text, Words, WordPatterns, reasons);
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

    /// <summary>
    /// Owner, 19 Oct 2026: what looks like someone's personal information - an Aadhaar, PAN or
    /// passport number, a phone number, bank details, pay, a password - in words, or none.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The text.</param>
    /// <param name="fileNames">The attachments' names.</param>
    /// <returns>The reasons; empty when nothing was found.</returns>
    public static IReadOnlyList<string> PersonalSigns(string? subject, string? body, IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        var reasons = new List<string>();
        string text = ((subject ?? string.Empty) + "\n" + (body ?? string.Empty)).ToLowerInvariant();
        if (Aadhaar().IsMatch(text))
        {
            reasons.Add("an Aadhaar number");
        }
        if (Pan().IsMatch(text))
        {
            reasons.Add("a PAN");
        }
        if (Phone().IsMatch(text))
        {
            reasons.Add("a phone number");
        }
        if (BankAccount().IsMatch(text))
        {
            reasons.Add("a bank account number");
        }
        Found(text, PersonalWords, PersonalPatterns, reasons);
        foreach (string f in fileNames)
        {
            string lower = (f ?? string.Empty).ToLowerInvariant();
            string stem = System.IO.Path.GetFileNameWithoutExtension(lower);
            if (PersonalFileWords.Any(w => Regex.IsMatch(stem, @"(^|[^a-z])" + Regex.Escape(w) + @"([^a-z]|$)")) || Aadhaar().IsMatch(lower) || Pan().IsMatch(lower))
            {
                reasons.Add("a file named " + f);
                break;
            }
        }
        return reasons;
    }

    private static void Found(string text, string[] words, Regex[] patterns, List<string> reasons)
    {
        var found = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            if (patterns[i].IsMatch(text))
            {
                found.Add("\"" + words[i] + "\"");
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
    }

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

    // "a/c no 1234567890", "account 12345678901" - 9 to 18 digits after the word.
    [GeneratedRegex(@"\b(a/c|acct|account)\s*(no|number|#)?\.?\s*[:#-]?\s*\d{9,18}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BankAccount();
}
