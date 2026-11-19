namespace Anjal.Viewing;

/// <summary>
/// The parts of the PDF editor held back until chuvadi-pdf 3.35.1 (the library's security advisory, D-405 and
/// D-406, 17 Nov 2026): redaction, and changing the pages of a file with hidden layers (optional content). Both are
/// built and tested, and both are off in rc.29; they turn on in the release that moves to the patch packages. The
/// webmail sets them on every editor request it sends; the viewer refuses a job they do not allow.
/// </summary>
public sealed class EditSwitches
{
    /// <summary>Redact (boxes, Find words, Find patient details) may be used.</summary>
    public bool Redact { get; init; }

    /// <summary>The pages of a file with hidden layers may be turned, moved, removed, kept or joined.</summary>
    public bool LayerPages { get; init; }

    /// <summary>As rc.29 ships: both off until chuvadi-pdf 3.35.1 is in localpackages.</summary>
    public static EditSwitches Shipped { get; } = new();

    /// <summary>Both on, as they will be on chuvadi-pdf 3.35.1 (and for the tests of what they guard).</summary>
    public static EditSwitches All { get; } = new() { Redact = true, LayerPages = true };
}
