using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §4.1): a child process - one job and then it exits. It warms the libraries, says it is ready
/// (one byte, "R"), reads one request from its input, answers on its output, and is gone. Nothing a library might
/// print can reach the answer: the console's text output goes nowhere, and the answer is written to the raw
/// output stream. Every exception is caught (AC-11): the libraries' become statuses in their jobs; anything else
/// becomes "failed", with the exception's type - never its message - on the error stream for the log.
/// </summary>
public static class ChildHost
{
    /// <summary>The byte a child writes when it is warm and ready for its job.</summary>
    public const byte Ready = (byte)'R';

    /// <summary>The prefix of the lines the supervisor passes on to the log; any other line from a child is dropped.</summary>
    public const string LogPrefix = "anjal-viewer: ";

    /// <summary>Runs the child.</summary>
    /// <param name="input">Where the request comes from.</param>
    /// <param name="output">Where the answer goes.</param>
    /// <param name="warm">Whether to warm the libraries first.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(Stream input, Stream output, bool warm = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        TextWriter log = Console.Error;
        Console.SetOut(TextWriter.Null);
        if (warm)
        {
            Warm();
        }
        output.WriteByte(Ready);
        await output.FlushAsync().ConfigureAwait(false);
        (string Json, List<byte[]> Files)? frame;
        try
        {
            frame = await ViewFrames.ReadAsync(input).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return 2;
        }
        if (frame is null)
        {
            return 0;
        }
        JobAnswer answer;
        try
        {
            ViewRequest request = ViewJson.ReadRequest(frame.Value.Json);
            // The library is asked to stop shortly before the supervisor would stop the process.
            using var cts = new CancellationTokenSource(ViewKinds.Deadline(request.Kind, request.Page) - TimeSpan.FromMilliseconds(500));
            answer = ChildJobs.Run(request, frame.Value.Files, cts.Token);
        }
        catch (OperationCanceledException)
        {
            answer = JobAnswer.Status(ViewStatus.TooSlow, "cancelled");
        }
#pragma warning disable CA1031 // AC-11: every exception is caught in the child, whatever a library throws.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = JobAnswer.Status(ViewStatus.Failed, ex.GetType().Name);
        }
        if (answer.Answer.Status != ViewStatus.Ok && answer.Answer.Detail is string detail)
        {
            await log.WriteLineAsync(LogPrefix + answer.Answer.Status + " (" + detail + ")").ConfigureAwait(false);
        }
        long total = answer.Files.Sum(f => (long)f.Length);
        if (answer.Files.Any(f => f.Length > ViewFrames.MaxFileBytes) || total > ViewFrames.MaxFrameBytes - ViewFrames.MaxJsonBytes)
        {
            answer = JobAnswer.Status(ViewStatus.TooLarge, "answer");
        }
        await ViewFrames.WriteAsync(output, ViewJson.Write(answer.Answer), answer.Files.Select(f => (ReadOnlyMemory<byte>)f).ToList()).ConfigureAwait(false);
        return 0;
    }

    // DES-22 §4.1: each library is used once before the child is offered, so the first page a person asks for is
    // not also the library's first (a first render can take a second cold).
    private static void Warm()
    {
        try
        {
            Chuvadi.Pdf.Authoring.PdfDocumentBuilder builder = Chuvadi.Pdf.Authoring.PdfDocumentBuilder.Create();
            builder.AddPage(new Chuvadi.Pdf.Authoring.PageSize(72, 72)).DrawRectangle(8, 8, 32, 32, Chuvadi.Pdf.Authoring.Colors.Black);
            byte[] pdf = builder.ToByteArray();
            _ = PdfJobs.Page(pdf, null, 0, 60, CancellationToken.None);
            _ = PdfJobs.Text(pdf, null, 0, CancellationToken.None);
        }
#pragma warning disable CA1031 // Warming is best effort: a failure here only makes the first job slower.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
