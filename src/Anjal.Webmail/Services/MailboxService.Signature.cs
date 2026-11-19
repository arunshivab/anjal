namespace Anjal.Webmail.Services;

/// <summary>A signature drawn on the editor's pad, kept as its strokes (DES-22 §7.5, D-365).</summary>
public sealed class DrawnSignature
{
    /// <summary>The points, as x and y in turn, in the pad's own space.</summary>
    public List<double> Points { get; set; } = new();

    /// <summary>Where each stroke starts in <see cref="Points"/> (as a point's number).</summary>
    public List<int> Strokes { get; set; } = new();

    /// <summary>The pad's width.</summary>
    public double W { get; set; }

    /// <summary>The pad's height.</summary>
    public double H { get; set; }
}

/// <summary>
/// rc.29 part 2 (DES-22 §6.2, §7.5): a person's own drawn signature, kept for reuse only when they ask, sealed to
/// their own key like their contacts and templates, never shown to an administrator, removable in Settings.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The mailbox document that holds the saved signature.</summary>
    public const string DrawnSignatureKind = "drawn-signature";

    /// <summary>The most points a saved signature may hold.</summary>
    public const int MaxSignaturePoints = 20000;

    /// <summary>The person's saved signature, or null.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The signature, or null.</returns>
    public Task<DrawnSignature?> DrawnSignatureAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.ReadDocumentAsync<DrawnSignature>(mailboxId, DrawnSignatureKind, ct);

    /// <summary>Keeps the person's signature for next time, sealed; false when it is not a signature.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="signature">The signature.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it was kept.</returns>
    public async Task<bool> SaveDrawnSignatureAsync(Guid mailboxId, DrawnSignature signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Points.Count is < 4 or > MaxSignaturePoints * 2 || signature.Points.Count % 2 != 0
            || signature.W is <= 0 or > 4000 || signature.H is <= 0 or > 4000
            || signature.Points.Any(v => !double.IsFinite(v) || v < -1 || v > 4001)
            || signature.Strokes.Count > MaxSignaturePoints || signature.Strokes.Any(s => s < 0 || s * 2 >= signature.Points.Count))
        {
            return false;
        }
        await this.WriteDocumentAsync(mailboxId, DrawnSignatureKind, signature, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Forgets the person's saved signature.</summary>
    /// <param name="mailboxId">The mailbox.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task RemoveDrawnSignatureAsync(Guid mailboxId, CancellationToken ct = default) =>
        this.store.SetMailboxDocumentAsync(mailboxId, DrawnSignatureKind, string.Empty, ct);
}
