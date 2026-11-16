namespace Anjal.Viewer;

/// <summary>rc.29 (D-372): the build without the Siaara package - DICOM is not shown.</summary>
public static partial class DicomJob
{
    private static partial bool AvailableCore() => false;

    private static partial JobAnswer? RunCore(byte[] bytes, CancellationToken ct) => null;
}
