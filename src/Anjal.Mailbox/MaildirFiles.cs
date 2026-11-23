namespace Anjal.Mailbox;

/// <summary>
/// rc.30: renames and deletes of mail files that wait a moment while another process holds the file. On Windows a
/// file another process has open (an antivirus scan of a mail just written, the search indexer) cannot be renamed
/// or deleted until it lets go, and that moment used to come back to the webmail as a server error (found by the
/// owner's run of Rc23Tests on Windows, 20 Nov 2026). Only that case waits: any other error is thrown at once.
/// </summary>
public static class MaildirFiles
{
    /// <summary>How many times a rename or delete is tried before the error is passed on.</summary>
    public const int Attempts = 8;

    /// <summary>Renames a file, trying again for a moment while another process holds it.</summary>
    /// <param name="source">The file.</param>
    /// <param name="target">Its new path.</param>
    /// <param name="overwrite">Whether a file already at the new path is replaced.</param>
    public static void Move(string source, string target, bool overwrite)
    {
        Move(source, target, overwrite, System.IO.File.Move, System.Threading.Thread.Sleep);
    }

    /// <summary>Renames a file with the given rename and wait, so the waiting can be tested.</summary>
    /// <param name="source">The file.</param>
    /// <param name="target">Its new path.</param>
    /// <param name="overwrite">Whether a file already at the new path is replaced.</param>
    /// <param name="move">The rename.</param>
    /// <param name="wait">Waits the given number of milliseconds.</param>
    public static void Move(string source, string target, bool overwrite, System.Action<string, string, bool> move, System.Action<int> wait)
    {
        System.ArgumentNullException.ThrowIfNull(source);
        System.ArgumentNullException.ThrowIfNull(target);
        System.ArgumentNullException.ThrowIfNull(move);
        System.ArgumentNullException.ThrowIfNull(wait);
        Try(() => move(source, target, overwrite), wait);
    }

    /// <summary>Deletes a file, trying again for a moment while another process holds it.</summary>
    /// <param name="path">The file.</param>
    public static void Delete(string path)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        Try(() => System.IO.File.Delete(path), System.Threading.Thread.Sleep);
    }

    /// <summary>
    /// Whether an error says another process has the file open (Windows' sharing and lock violations, 32 and 33);
    /// a missing file or folder never is.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>True when waiting may help.</returns>
    public static bool IsHeldByAnother(System.IO.IOException error)
    {
        System.ArgumentNullException.ThrowIfNull(error);
        return error is not System.IO.FileNotFoundException and not System.IO.DirectoryNotFoundException
            && (error.HResult & 0xFFFF) is 32 or 33;
    }

    // Waits 25, 50, 75 ... ms between tries: about 0.7 s in all before the error is passed on.
    private static void Try(System.Action action, System.Action<int> wait)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (System.IO.IOException e) when (attempt < Attempts && IsHeldByAnother(e))
            {
                wait(attempt * 25);
            }
        }
    }
}
