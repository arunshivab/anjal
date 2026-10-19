using System.Text;

namespace Anjal.Mailbox.Tests;

/// <summary>
/// rc.22.2: the real clamd protocol. These run only where a clamd socket exists (the developer's
/// machine, or a CI job that installs clamav-daemon); elsewhere they pass without checking anything,
/// and say so, rather than pretend. Set ANJAL_TEST_CLAMAV to the socket path or host:port.
/// </summary>
public sealed class ClamdScannerTests
{
    // The EICAR test file, assembled so no scanner flags this source file itself.
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS" + "-TEST-FILE!$H+H*");

    private static string? Endpoint
    {
        get
        {
            string? set = Environment.GetEnvironmentVariable("ANJAL_TEST_CLAMAV");
            if (!string.IsNullOrWhiteSpace(set))
            {
                return set;
            }
            return File.Exists("/var/run/clamav/clamd.ctl") ? "/var/run/clamav/clamd.ctl" : null;
        }
    }

    [Fact]
    public async Task ARealClamd_SaysCleanForText_AndNamesTheEicarFile()
    {
        if (Endpoint is not string endpoint)
        {
            return;   // no clamd here
        }
        var scanner = new ClamdScanner(endpoint);
        ScanResult clean = await scanner.ScanAsync(Encoding.UTF8.GetBytes("Dear Dr Rao, the quotation is attached.\r\n"));
        Assert.Equal(ScanVerdict.Clean, clean.Verdict);
        ScanResult found = await scanner.ScanAsync(Eicar);
        Assert.Equal(ScanVerdict.Infected, found.Verdict);
        Assert.Contains("Eicar", found.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARealClamd_AnswersTheVersionQuestion_EvenWhenVersionIsDisabled()
    {
        // ClamAV 1.5 ships with EnableVersionCommand false: the daemon says "COMMAND UNAVAILABLE" and
        // then PING must stand in, or the console would show a working scanner as down.
        if (Endpoint is not string endpoint)
        {
            return;
        }
        string? version = await new ClamdScanner(endpoint).VersionAsync();
        Assert.NotNull(version);
        Assert.Contains("ClamAV", version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoClamdAtAll_IsUnavailable_NotAnException()
    {
        var scanner = new ClamdScanner("127.0.0.1:1");
        ScanResult result = await scanner.ScanAsync(Eicar);
        Assert.Equal(ScanVerdict.Unavailable, result.Verdict);
        Assert.Null(await scanner.VersionAsync());
    }
}
