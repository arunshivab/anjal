using System.Text.Json;
using Anjal.Store;

namespace Anjal.Webmail.Services;

/// <summary>
/// rc.18 (design review [8b]): "Did this help?" - a count of Yes and No per article, kept as the
/// service record "help-feedback" for the weekly review. Who answered is never kept.
/// </summary>
public static class HelpFeedback
{
    /// <summary>The service record that holds the counts.</summary>
    public const string Kind = "help-feedback";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Count one answer.</summary>
    /// <param name="records">The store.</param>
    /// <param name="articleId">The article.</param>
    /// <param name="helped">Yes (true) or No.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task CountAsync(IMessageStore records, string articleId, bool helped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(articleId);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, int[]> counts = await ReadAsync(records, ct).ConfigureAwait(false);
            int[] c = counts.TryGetValue(articleId, out int[]? found) && found.Length == 2 ? found : new int[2];
            c[helped ? 0 : 1]++;
            counts[articleId] = c;
            await records.SetServiceRecordAsync(Kind, JsonSerializer.Serialize(counts), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Every article's count: [yes, no].</summary>
    /// <param name="records">The store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    public static async Task<Dictionary<string, int[]>> ReadAsync(IMessageStore records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        string? json = await records.GetServiceRecordAsync(Kind, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int[]>(StringComparer.Ordinal);
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int[]>>(json) ?? new Dictionary<string, int[]>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int[]>(StringComparer.Ordinal);
        }
    }
}
