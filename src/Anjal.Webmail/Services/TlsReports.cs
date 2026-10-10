using System.IO.Compression;
using System.Text.Json;

namespace Anjal.Webmail.Services;

/// <summary>One policy's part of a TLS report: how many deliveries to the domain were encrypted, and how many failed (rc.15, item 59).</summary>
/// <param name="Reporter">The receiver that sent the report, for example "Google Inc.".</param>
/// <param name="Domain">The domain the deliveries were to.</param>
/// <param name="Policy">The policy checked: "sts" (MTA-STS), "tlsa" (DANE) or "no-policy-found".</param>
/// <param name="Successful">Sessions that were encrypted as the policy asks.</param>
/// <param name="Failed">Sessions that failed.</param>
/// <param name="Failures">Why they failed, for example "certificate-expired", joined with commas.</param>
public sealed record TlsReportRow(string Reporter, string Domain, string Policy, long Successful, long Failed, string Failures);

/// <summary>
/// Reading SMTP TLS reports (RFC 8460; rc.15, item 59): the JSON receivers
/// send - plain or gzipped - about the mail they tried to deliver to a
/// domain, as reports to the address in its <c>_smtp._tls</c> record.
/// Nothing is fetched; sizes are capped, so a hostile report cannot exhaust memory.
/// </summary>
public static class TlsReports
{
    /// <summary>The largest report read, after decompression.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>True for an attachment that can be a TLS report: .json or .json.gz, or a tlsrpt, JSON or gzip type.</summary>
    /// <param name="fileName">Its file name.</param>
    /// <param name="contentType">Its type.</param>
    /// <returns>Whether to try it.</returns>
    public static bool MayBeReport(string? fileName, string? contentType)
    {
        string f = (fileName ?? string.Empty).ToLowerInvariant();
        string t = (contentType ?? string.Empty).ToLowerInvariant();
        return f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".json.gz", StringComparison.Ordinal) || f.EndsWith(".gz", StringComparison.Ordinal)
            || t.Contains("tlsrpt", StringComparison.Ordinal) || t.Contains("json", StringComparison.Ordinal) || t.Contains("gzip", StringComparison.Ordinal);
    }

    /// <summary>Read a TLS report from an attachment; null when it is not one.</summary>
    /// <param name="bytes">The attachment.</param>
    /// <returns>Its rows, or null.</returns>
    public static IReadOnlyList<TlsReportRow>? Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        byte[]? json = Unpack(bytes);
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("policies", out JsonElement policies) || policies.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            string reporter = Str(root, "organization-name");
            var rows = new List<TlsReportRow>();
            foreach (JsonElement p in policies.EnumerateArray())
            {
                JsonElement policy = p.TryGetProperty("policy", out JsonElement pe) ? pe : default;
                JsonElement summary = p.TryGetProperty("summary", out JsonElement se) ? se : default;
                var failures = new List<string>();
                if (p.TryGetProperty("failure-details", out JsonElement details) && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement d in details.EnumerateArray())
                    {
                        string type = Str(d, "result-type");
                        if (type.Length > 0 && !failures.Contains(type))
                        {
                            failures.Add(type);
                        }
                    }
                }
                rows.Add(new TlsReportRow(
                    reporter,
                    Str(policy, "policy-domain").ToLowerInvariant(),
                    Str(policy, "policy-type"),
                    Num(summary, "total-successful-session-count"),
                    Num(summary, "total-failure-session-count"),
                    string.Join(", ", failures.Take(5))));
            }
            return rows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? Math.Max(0, n) : 0;

    private static byte[]? Unpack(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                byte[] buffer = new byte[81920];
                int n;
                while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > MaxBytes)
                    {
                        return null;
                    }
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
            return bytes.Length <= MaxBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
