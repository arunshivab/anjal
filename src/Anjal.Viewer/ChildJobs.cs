using Anjal.Viewing;

namespace Anjal.Viewer;

/// <summary>
/// rc.29 (DES-22 §4.1): the one job a child process does. A file inside an archive (AC-17) is unpacked here, in
/// memory, and then shown by the same viewer as a file attached on its own.
/// </summary>
public static class ChildJobs
{
    /// <summary>Does one job. Exceptions of the libraries become statuses; anything else is the caller's to catch.</summary>
    /// <param name="request">The request.</param>
    /// <param name="file">The attachment's bytes.</param>
    /// <param name="ct">Cancellation, ended shortly before the child's deadline.</param>
    /// <returns>The answer.</returns>
    public static JobAnswer Run(ViewRequest request, byte[] file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(file);
        if (request.Kind.StartsWith("test.", StringComparison.Ordinal) && Environment.GetEnvironmentVariable("ANJAL_VIEWER_TEST_HOOKS") == "1")
        {
            return TestHook(request.Kind);
        }
        switch (request.Kind)
        {
            case ViewKinds.ArchiveList:
                // The archive's own password: the attachment's for the attachment, the inner archive's for "3".
                ArchiveListing listing = string.IsNullOrEmpty(request.Entry)
                    ? ArchiveReader.List(file, null, new ArchivePasswords(request.Password, null))
                    : ArchiveReader.List(file, request.Entry, new ArchivePasswords(request.ArchivePassword, request.Password));
                return listing.Status == ViewStatus.Ok
                    ? JobAnswer.Json(ViewJson.Write(listing.View))
                    : JobAnswer.Status(listing.Status, PasswordPlace(listing.Status, listing.Place));
            case ViewKinds.ArchiveEntry:
                if (string.IsNullOrEmpty(request.Entry))
                {
                    return JobAnswer.Status(ViewStatus.Refused, null);
                }
                EntryResult entry = ArchiveReader.Entry(file, request.Entry, Passwords(request));
                if (entry.Bytes is null)
                {
                    return JobAnswer.Status(entry.Status, PasswordPlace(entry.Status, entry.Place));
                }
                // AC-20: a file from a locked archive was not virus-scanned; it is shown, never given out.
                if (entry.Locked)
                {
                    return JobAnswer.Status(ViewStatus.Refused, "locked");
                }
                return JobAnswer.File(entry.Bytes, ViewJson.Write(new FileOut { Name = entry.Name ?? string.Empty, Type = FileKinds.PictureType(entry.Bytes) ?? "application/octet-stream" }));
        }
        byte[] bytes = file;
        string name = request.Name ?? string.Empty;
        if (!string.IsNullOrEmpty(request.Entry))
        {
            EntryResult inner = ArchiveReader.Entry(file, request.Entry, Passwords(request));
            if (inner.Bytes is null)
            {
                return JobAnswer.Status(inner.Status, PasswordPlace(inner.Status, inner.Place));
            }
            bytes = inner.Bytes;
            name = inner.Name ?? string.Empty;
        }
        return request.Kind switch
        {
            ViewKinds.PdfInfo => PdfJobs.Info(bytes, request.Password, ct),
            ViewKinds.PdfPage => PdfJobs.Page(bytes, request.Password, request.Page, request.Width, ct),
            ViewKinds.PdfText => PdfJobs.Text(bytes, request.Password, request.Page, ct),
            ViewKinds.PdfFind => string.IsNullOrWhiteSpace(request.Query) ? JobAnswer.Json(ViewJson.Write(new FindResult())) : PdfJobs.Find(bytes, request.Password, request.Query, ct),
            ViewKinds.PdfClean => PdfJobs.Clean(bytes, request.Password, ct),
            ViewKinds.Word => WordJob.Run(bytes, request.Password, ct),
            ViewKinds.Sheet => SheetJob.Run(bytes, request.Password, request.Sheet, request.Culture, ct),
            ViewKinds.Picture => FileKinds.PictureType(bytes) is string type
                ? JobAnswer.File(bytes, ViewJson.Write(new FileOut { Name = name, Type = type }))
                : JobAnswer.Status(ViewStatus.Unsupported, null),
            ViewKinds.Dicom => DicomJob.Run(bytes, ct),
            _ => JobAnswer.Status(ViewStatus.Unsupported, null),
        };
    }

    // For a file inside an archive: the attachment's password, and the inner archive's for "3/5".
    private static ArchivePasswords Passwords(ViewRequest r) => new(r.ArchivePassword, r.InnerArchivePassword);

    // AC-20: when an archive asks for its password, the answer says which ("archive:" and its place, "" for the
    // attachment), so the page asks for the right one.
    private static string? PasswordPlace(string status, string? place) =>
        status is ViewStatus.NeedsPassword or ViewStatus.WrongPassword or ViewStatus.WrongOrDamaged && place is not null ? "archive:" + place
        : status == ViewStatus.Unsupported && place is not null ? "zip-method"
        : null;

    // Only for the viewer's own tests (the variable is never set on a server): a job that hangs past its deadline,
    // and one that grows without end, so the supervisor's stops can be tested for real.
    private static JobAnswer TestHook(string kind)
    {
        if (kind == "test.hang")
        {
            Thread.Sleep(Timeout.Infinite);
        }
        if (kind == "test.grow")
        {
            var held = new List<byte[]>();
            while (true)
            {
                byte[] block = new byte[16 * 1024 * 1024];
                for (int i = 0; i < block.Length; i += 4096)
                {
                    block[i] = 1;
                }
                held.Add(block);
                Thread.Sleep(20);
            }
        }
        return JobAnswer.Json("{}");
    }
}
