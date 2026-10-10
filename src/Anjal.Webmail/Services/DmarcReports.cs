using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Anjal.Webmail.Services;

/// <summary>One row of a DMARC aggregate report: one sending address's mail for the domain (rc.15, item 32).</summary>
/// <param name="SourceIp">The address that sent it.</param>
/// <param name="Count">How many messages.</param>
/// <param name="HeaderFrom">The domain in the From line.</param>
/// <param name="Disposition">What the receiver did: none, quarantine or reject.</param>
/// <param name="Dkim">DKIM, as DMARC judged it: pass or fail.</param>
/// <param name="Spf">SPF, as DMARC judged it: pass or fail.</param>
public sealed record DmarcRow(string SourceIp, long Count, string HeaderFrom, string Disposition, string Dkim, string Spf)
{
    /// <summary>True when DMARC passed: DKIM or SPF passed and was aligned.</summary>
    public bool Passed => this.Dkim == "pass" || this.Spf == "pass";
}

/// <summary>A DMARC aggregate report (RFC 7489 appendix C): who reported, for which domain and days, and its rows.</summary>
/// <param name="Reporter">The receiver that sent it, for example "google.com".</param>
/// <param name="ReportId">Its id.</param>
/// <param name="Domain">The domain it is about.</param>
/// <param name="Begin">The start of the period it covers.</param>
/// <param name="End">The end of the period it covers.</param>
/// <param name="Rows">Its rows.</param>
public sealed record DmarcReport(string Reporter, string ReportId, string Domain, DateTimeOffset Begin, DateTimeOffset End, IReadOnlyList<DmarcRow> Rows);

/// <summary>
/// Reading DMARC aggregate reports (rc.15, items 31 to 33): the XML as
/// receivers send it, plain, gzipped or zipped. Nothing is fetched; the
/// XML may not declare a DTD; sizes are capped, so a hostile report cannot
/// exhaust memory.
/// </summary>
public static class DmarcReports
{
    /// <summary>The largest report read, after decompression.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>True for an attachment that can be a report: .xml, .xml.gz, .gz or .zip, or an XML, gzip or zip type.</summary>
    /// <param name="fileName">Its file name.</param>
    /// <param name="contentType">Its type.</param>
    /// <returns>Whether to try it.</returns>
    public static bool MayBeReport(string? fileName, string? contentType)
    {
        string f = (fileName ?? string.Empty).ToLowerInvariant();
        string t = (contentType ?? string.Empty).ToLowerInvariant();
        return f.EndsWith(".xml", StringComparison.Ordinal) || f.EndsWith(".gz", StringComparison.Ordinal) || f.EndsWith(".zip", StringComparison.Ordinal)
            || t.Contains("xml", StringComparison.Ordinal) || t.Contains("gzip", StringComparison.Ordinal) || t.Contains("zip", StringComparison.Ordinal);
    }

    /// <summary>Read a report from an attachment; null when it is not one.</summary>
    /// <param name="bytes">The attachment.</param>
    /// <returns>The report, or null.</returns>
    public static DmarcReport? Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[]? xml = Unpack(bytes);
        if (xml is null)
        {
            return null;
        }
        try
        {
            return Parse(xml);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? Unpack(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                return ReadCapped(gz);
            }
            if (bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > MaxBytes)
                {
                    return null;
                }
                using Stream s = entry.Open();
                return ReadCapped(s);
            }
            return bytes.Length <= MaxBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static byte[]? ReadCapped(Stream s)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[81920];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + n > MaxBytes)
            {
                return null;
            }
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static DmarcReport? Parse(byte[] xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaxBytes,
        };
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(xml), settings))
        {
            doc.Load(reader);
        }
        XmlElement? root = doc.DocumentElement;
        if (root is null || root.LocalName != "feedback")
        {
            return null;
        }
        string reporter = Text(root, "report_metadata/org_name");
        string id = Text(root, "report_metadata/report_id");
        string domain = Text(root, "policy_published/domain").ToLowerInvariant();
        DateTimeOffset begin = Epoch(Text(root, "report_metadata/date_range/begin"));
        DateTimeOffset end = Epoch(Text(root, "report_metadata/date_range/end"));
        var rows = new List<DmarcRow>();
        foreach (XmlElement record in root.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "record"))
        {
            long count = long.TryParse(Text(record, "row/count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long c) ? Math.Max(0, c) : 0;
            rows.Add(new DmarcRow(
                Text(record, "row/source_ip"),
                count,
                Text(record, "identifiers/header_from").ToLowerInvariant(),
                Text(record, "row/policy_evaluated/disposition").ToLowerInvariant(),
                Text(record, "row/policy_evaluated/dkim").ToLowerInvariant(),
                Text(record, "row/policy_evaluated/spf").ToLowerInvariant()));
        }
        return domain.Length == 0 ? null : new DmarcReport(reporter.Length > 0 ? reporter : "unknown", id, domain, begin, end, rows);
    }

    private static string Text(XmlElement from, string path)
    {
        XmlElement? at = from;
        foreach (string step in path.Split('/'))
        {
            at = at?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == step);
        }
        return at?.InnerText.Trim() ?? string.Empty;
    }

    private static DateTimeOffset Epoch(string s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) && t > 0 && t < 32503680000
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : DateTimeOffset.MinValue;
}
