namespace Anjal.Server;

/// <summary>
/// Composition root for the Anjal mail server host process.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entry point. In v0.1.0 this is a placeholder; future versions wire up
    /// the SMTP receiver, sender, HTTP API, and storage backend here.
    /// </summary>
    public static int Main()
    {
        System.Console.WriteLine("Anjal mail server v0.1.0 - scaffold only.");
        return 0;
    }
}
