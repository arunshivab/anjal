using Anjal.Smtp;

namespace Anjal.Spam.Tests;

/// <summary>
/// Decision 2B and DEF-059 (26 Sep 2026). On the production server the first
/// Gmail message waited 25 minutes and Outlook's 34; a sender was forgotten
/// after 36 idle hours and at every restart, and no deferral was logged, so a
/// delayed message could not be told from a lost one.
/// </summary>
public sealed class GreylistRememberTests : System.IDisposable
{
    private const string Ip = "203.0.113.10";
    private const string From = "colleague@example.com";
    private const string To = "arun@anjal.co.in";

    private readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anjal-grey-" + System.Guid.NewGuid().ToString("N"));

    public GreylistRememberTests() => System.IO.Directory.CreateDirectory(this.dir);

    public void Dispose() => System.IO.Directory.Delete(this.dir, recursive: true);

    [Fact]
    public void RemembersAPassedSenderFor35Days_ByDefault()
    {
        var clock = new Clock();
        var grey = new Greylist(new GreylistOptions(), () => clock.Now);
        PassOnce(grey, clock);

        clock.Now += System.TimeSpan.FromDays(34);
        Assert.True(grey.OnRcptTo(Ip, null, From, To).Allowed, "a sender who wrote 34 days ago is still remembered");

        clock.Now += System.TimeSpan.FromDays(36);
        clock.Now += System.TimeSpan.FromMinutes(11); // let the periodic sweep run
        Assert.False(grey.OnRcptTo(Ip, null, From, To).Allowed, "36 idle days later the sender is greylisted again");
    }

    [Fact]
    public void ARestartKeepsRememberedSenders_WhenAStateFileIsSet()
    {
        var clock = new Clock();
        string file = System.IO.Path.Combine(this.dir, "greylist.tsv");
        var before = new Greylist(new GreylistOptions { StateFile = file }, () => clock.Now);
        PassOnce(before, clock);
        before.Flush();

        var after = new Greylist(new GreylistOptions { StateFile = file }, () => clock.Now);
        Assert.True(after.OnRcptTo(Ip, null, From, To).Allowed, "remembered across the restart");
        Assert.Equal(1, after.Count);
    }

    [Fact]
    public void AnUnreadableOrForeignStateFile_IsSkippedLineByLine_NeverThrows()
    {
        var clock = new Clock();
        string file = System.IO.Path.Combine(this.dir, "greylist.tsv");
        long t = clock.Now.UtcTicks;
        System.IO.File.WriteAllText(file,
            "garbage line\n" +
            "k|a@x|b@y\tnot-a-number\t1\t1\n" +
            $"198.51.100.0/24|x@example.org|arun@anjal.co.in\t{t}\t{t}\t1\n");
        var logs = new System.Collections.Generic.List<string>();
        var grey = new Greylist(new GreylistOptions { StateFile = file, Log = logs.Add }, () => clock.Now);

        Assert.Equal(1, grey.Count);
        Assert.True(grey.OnRcptTo("198.51.100.7", null, "x@example.org", To).Allowed);
        Assert.Contains(logs, l => l.Contains("1 sender(s) remembered", System.StringComparison.Ordinal) && l.Contains("2 expired or unreadable", System.StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task ASenderThatPassesSpf_IsNotDelayed_AndItIsLogged()
    {
        var logs = new System.Collections.Generic.List<string>();
        var grey = new Greylist(new GreylistOptions
        {
            TrustedSender = (_, _, _) => System.Threading.Tasks.Task.FromResult(true),
            Log = logs.Add,
        });

        PolicyDecision d = await grey.OnRcptToAsync(Ip, null, From, To);

        Assert.True(d.Allowed);
        Assert.Equal(0, grey.Count);
        Assert.Contains(logs, l => l.Contains("not delayed", System.StringComparison.Ordinal) && l.Contains(From, System.StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task AFailedSenderCheck_FallsBackToGreylisting_NeverLosesMail()
    {
        var logs = new System.Collections.Generic.List<string>();
        var grey = new Greylist(new GreylistOptions
        {
            TrustedSender = (_, _, _) => throw new System.TimeoutException("DNS timed out"),
            Log = logs.Add,
        });

        PolicyDecision d = await grey.OnRcptToAsync(Ip, null, From, To);

        Assert.False(d.Allowed);
        Assert.Equal(451, d.ReplyCode);
        Assert.Contains(logs, l => l.Contains("sender check failed", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDeferralAndFirstPass_IsLogged_DEF059()
    {
        var clock = new Clock();
        var logs = new System.Collections.Generic.List<string>();
        var grey = new Greylist(new GreylistOptions { Log = logs.Add }, () => clock.Now);

        Assert.False(grey.OnRcptTo(Ip, null, From, To).Allowed);
        clock.Now += System.TimeSpan.FromMinutes(6);
        Assert.True(grey.OnRcptTo(Ip, null, From, To).Allowed);

        Assert.Contains(logs, l => l.StartsWith("Greylist: deferred " + Ip + " " + From + " -> " + To, System.StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("Greylist: passed after 6 min", System.StringComparison.Ordinal));
    }

    [Fact]
    public void TheStateFileIsWrittenAtMostOncePerInterval_AndOnFlush()
    {
        var clock = new Clock();
        string file = System.IO.Path.Combine(this.dir, "greylist.tsv");
        var grey = new Greylist(new GreylistOptions { StateFile = file, SaveInterval = System.TimeSpan.FromMinutes(1) }, () => clock.Now);

        grey.OnRcptTo(Ip, null, From, To);
        Assert.False(System.IO.File.Exists(file), "not yet: the interval has not passed");

        clock.Now += System.TimeSpan.FromSeconds(61);
        grey.OnRcptTo(Ip, null, From, To);
        Assert.True(System.IO.File.Exists(file), "written once the interval passed");

        grey.OnRcptTo("198.51.100.9", null, "b@example.net", To);
        grey.Flush();
        Assert.Equal(2, System.IO.File.ReadAllLines(file).Length);
        Assert.False(System.IO.File.Exists(file + ".tmp"), "no temporary file left behind");
    }

    private static void PassOnce(Greylist grey, Clock clock)
    {
        Assert.False(grey.OnRcptTo(Ip, null, From, To).Allowed);
        clock.Now += System.TimeSpan.FromMinutes(6);
        Assert.True(grey.OnRcptTo(Ip, null, From, To).Allowed);
    }

    private sealed class Clock
    {
        public System.DateTimeOffset Now { get; set; } = new(2026, 9, 26, 10, 0, 0, System.TimeSpan.Zero);
    }
}
