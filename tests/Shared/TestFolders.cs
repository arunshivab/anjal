namespace Anjal;

/// <summary>
/// Removes a test's temporary folder (CI, 11 Oct 2026). A background writer (a
/// certificate renewal, a watcher) can still be finishing a file when a test ends,
/// most often on the slower macOS runners, and a plain recursive delete then fails
/// with "Directory not empty" - failing a test whose checks all passed. This waits
/// and tries again; if the folder still will not go, the system's temporary-file
/// cleaner removes it later. Every test cleans up through here (SystemWideTests).
/// </summary>
internal static class TestFolders
{
    /// <summary>Delete the folder and everything in it, trying again while files are still being written.</summary>
    /// <param name="path">The folder.</param>
    public static void Delete(string path)
    {
        for (int attempt = 1; Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
