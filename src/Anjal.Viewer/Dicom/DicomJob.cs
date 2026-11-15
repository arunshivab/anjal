using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §3.4, D-372): a DICOM file's preview, from the Siaara package. Until a Siaara package is on the
/// feed the code that calls it is not compiled (it is built with <c>-p:Siaara=true</c> once it is), and the switch
/// that offers DICOM in the webmail stays off: this answers "unsupported".
/// </summary>
public static partial class DicomJob
{
    /// <summary>True when this build carries the Siaara preview.</summary>
    public static bool Available => AvailableCore();

    /// <summary>Previews a DICOM file.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Run(byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return RunCore(bytes, ct) ?? JobAnswer.Status(ViewStatus.Unsupported, "dicom off");
    }

    private static partial bool AvailableCore();

    private static partial JobAnswer? RunCore(byte[] bytes, CancellationToken ct);
}
