using System.Net.Sockets;

namespace Anjal.Viewing;

/// <summary>
/// rc.29 (DES-22 §4): the webmail's side of the viewer. Each request opens the socket, sends the request and the
/// file, and reads the answer. The viewer stops a child that runs past its job's deadline, so the wait here is
/// that deadline plus the time a request may spend in the queue.
/// </summary>
public sealed class ViewerClient
{
    private readonly string socketPath;
    private readonly TimeSpan queueWait;

    /// <summary>Creates a client for the viewer listening at a socket.</summary>
    /// <param name="socketPath">The Unix socket ("/run/anjal-viewer/viewer.sock").</param>
    /// <param name="queueWait">How long a request may wait in the viewer's queue before its own deadline starts.</param>
    public ViewerClient(string socketPath, TimeSpan? queueWait = null)
    {
        ArgumentNullException.ThrowIfNull(socketPath);
        this.socketPath = socketPath;
        this.queueWait = queueWait ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>The socket this client speaks to.</summary>
    public string SocketPath => this.socketPath;

    /// <summary>Asks the viewer to do one job on a file.</summary>
    /// <param name="request">The request.</param>
    /// <param name="file">The file's bytes (at most 64 MB).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer and its files; <see cref="ViewStatus.Unavailable"/> when the viewer is not there.</returns>
    public Task<ViewResult> AskAsync(ViewRequest request, ReadOnlyMemory<byte> file, CancellationToken ct = default) =>
        this.AskAsync(request, new[] { file }, ct);

    /// <summary>rc.29 part 2: asks with several files - the PDF being edited, the PDFs joined to it, the pictures its marks use.</summary>
    /// <param name="request">The job.</param>
    /// <param name="files">The files; the first is the attachment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    public async Task<ViewResult> AskAsync(ViewRequest request, IReadOnlyList<ReadOnlyMemory<byte>> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > ViewFrames.MaxFiles || files.Any(f => f.Length > ViewFrames.MaxFileBytes) || files.Sum(f => (long)f.Length) > ViewFrames.MaxFrameBytes - ViewFrames.MaxJsonBytes)
        {
            return ViewResult.Of(ViewStatus.TooLarge);
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ViewKinds.Deadline(request.Kind, request.Page) + this.queueWait + TimeSpan.FromSeconds(5));
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(this.socketPath), limit.Token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return ViewResult.Of(ViewStatus.Unavailable);
            }
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await ViewFrames.WriteAsync(stream, ViewJson.Write(request), files, limit.Token).ConfigureAwait(false);
            (string Json, List<byte[]> Files)? frame = await ViewFrames.ReadAsync(stream, limit.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return ViewResult.Of(ViewStatus.Failed);
            }
            return new ViewResult(ViewJson.ReadAnswer(frame.Value.Json), frame.Value.Files);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ViewResult.Of(ViewStatus.TooSlow);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            return ViewResult.Of(ViewStatus.Unavailable);
        }
        finally
        {
            socket.Dispose();
        }
    }
}

/// <summary>An answer from the viewer and the files that came with it.</summary>
/// <param name="Answer">The answer.</param>
/// <param name="Files">The files (pictures, a cleaned PDF, a file from an archive).</param>
public sealed record ViewResult(ViewAnswer Answer, IReadOnlyList<byte[]> Files)
{
    /// <summary>True when the job was done.</summary>
    public bool Ok => this.Answer.Status == ViewStatus.Ok;

    /// <summary>An answer with only a status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The result.</returns>
    public static ViewResult Of(string status) => new(new ViewAnswer { Status = status }, Array.Empty<byte[]>());
}
