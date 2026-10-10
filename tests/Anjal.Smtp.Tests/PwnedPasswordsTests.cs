using System.Net;
using System.Text;

namespace Anjal.Smtp.Tests;

/// <summary>
/// rc.15, item 35 (owner's decisions of 7 Oct 2026): the full leaked-password list from Have I
/// Been Pwned, kept on the server as a compact fingerprint file and refreshed every three
/// months. Built here from small stand-ins for the downloader's file and the range API.
/// </summary>
[Collection("Leaked passwords")]
public sealed class PwnedPasswordsTests : IDisposable
{
    // Real SHA-1 fingerprints: "password", and three made-up passwords whose fingerprints fall in
    // the first four ranges (00000 to 00003), so a four-range download can contain them.
    private const string PasswordSha1 = "5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8";
    private const string Test0 = "anjal-test-73748";
    private const string Test0Sha1 = "00000F226FFFB82731A4C10FD25A365DC2996FC3";
    private const string Test1 = "anjal-test-102804";
    private const string Test1Sha1 = "00001D4516BDA419868B0CC4FC13D593C5F58FF3";
    private const string Test3 = "anjal-test-374311";
    private const string Test3Sha1 = "00003766DD813FC2CA10D10DBCF832429745AA46";

    // Meets every rule and is on no built-in list: refused only because the full list holds it.
    private const string StrongButLeaked = "Zebra-Quilt-7731!";
    private const string StrongButLeakedSha1 = "1B1901C535FEF2EC0C699D43660A7A9DC594A47B";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "anjal-pwned-" + Guid.NewGuid().ToString("N"));

    public PwnedPasswordsTests() => Directory.CreateDirectory(this.dir);

    public void Dispose()
    {
        PwnedPasswords.Clear();
        PwnedPasswords.OnlineClient = null;
        PwnedPasswords.OnlineBaseUrl = PwnedPasswordBuilder.RangeApi;
        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", null);
        Environment.SetEnvironmentVariable("ANJAL_PWNED_ONLINE_MIN_COUNT", null);
        Directory.Delete(this.dir, recursive: true);
    }

    [Fact]
    public async Task TheDownloadersFile_BecomesACompactList_ThatFindsEveryLeakedPassword()
    {
        string path = Path.Combine(this.dir, "list.bin");
        string text = string.Join("\n", Test0Sha1 + ":3", Test1Sha1 + ":1", Test3Sha1 + ":9", StrongButLeakedSha1 + ":2", PasswordSha1 + ":9545824");
        long count = await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(text), path, minimumCount: 1);

        Assert.Equal(5, count);
        Assert.Equal(PwnedPasswordList.EntriesStart + (5 * 5), new FileInfo(path).Length);
        Assert.False(File.Exists(path + ".building"));
        PwnedPasswordList list = PwnedPasswordList.Open(path);
        Assert.Equal(5, list.Count);
        Assert.Equal(1, list.MinimumCount);
        Assert.True(DateTimeOffset.UtcNow - list.BuiltAt < TimeSpan.FromMinutes(5));
        Assert.True(list.Contains("password"));
        Assert.True(list.Contains(Test0));
        Assert.True(list.Contains(Test3));
        Assert.True(list.Contains(StrongButLeaked));
        Assert.False(list.Contains("Password"));
        Assert.False(list.Contains("a password nobody has leaked, 2026"));
    }

    [Fact]
    public async Task ByDefault_OnlyPasswordsSeenInThreeOrMoreLeaks_AreKept()
    {
        // Owner, 7 Oct 2026: Anjal serves apps, transactional mail and hospital systems, whose
        // sign-in has a second step; three or more leaks is enough.
        string path = Path.Combine(this.dir, "list.bin");
        string text = string.Join("\n", Test0Sha1 + ":3", Test1Sha1 + ":1", Test3Sha1 + ":2", StrongButLeakedSha1 + ":40", PasswordSha1 + ":9545824");
        Assert.Equal(3, await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(text), path));
        PwnedPasswordList list = PwnedPasswordList.Open(path);
        Assert.Equal(PwnedPasswordBuilder.DefaultMinimumCount, list.MinimumCount);
        Assert.True(list.Contains(Test0));
        Assert.True(list.Contains("password"));
        Assert.True(list.Contains(StrongButLeaked));
        Assert.False(list.Contains(Test1));
        Assert.False(list.Contains(Test3));
    }

    [Fact]
    public async Task AFileOutOfOrder_IsRefused_AndLeavesNothingBehind()
    {
        string path = Path.Combine(this.dir, "list.bin");
        string text = PasswordSha1 + ":5\n" + Test0Sha1 + ":5";
        await Assert.ThrowsAsync<InvalidDataException>(() => PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(text), path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".building"));
    }

    [Fact]
    public async Task AnIncompleteFile_IsNotUsed()
    {
        string path = Path.Combine(this.dir, "list.bin");
        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(Test0Sha1 + ":3\n" + Test1Sha1 + ":3"), path);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^4]);
        Assert.Throws<InvalidDataException>(() => PwnedPasswordList.Open(path));
    }

    [Fact]
    public void ARangeAnswer_GivesSortedFingerprints_AndSkipsPadding()
    {
        // Range 00000: each line is the other 35 hex digits and how often it was seen.
        string body = Test0Sha1[5..] + ":3\r\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:0\r\n0000000000000000000000000000000000A:12\r\n";
        ulong[] f = PwnedPasswordBuilder.ParseRange(0, body);
        Assert.Equal(2, f.Length);
        Assert.Equal(0UL, f[0]);
        Assert.Equal(PwnedPasswordList.Fingerprint(Test0), f[1]);
        Assert.Equal(PwnedPasswordList.Fingerprint(Test1), PwnedPasswordBuilder.ParseRange(1, Test1Sha1[5..] + ":1")[0]);
        Assert.Equal(0UL, Assert.Single(PwnedPasswordBuilder.ParseRange(0, body, minimumCount: 5)));
    }

    [Fact]
    public async Task TheRangeDownload_FetchesEveryRangeInOrder_AndWritesOneSortedList()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00000"] = Test0Sha1[5..] + ":3\r\n",
            ["00001"] = Test1Sha1[5..] + ":1\r\n",
            ["00002"] = string.Empty,
            ["00003"] = Test3Sha1[5..] + ":9\r\n",
        };
        var asked = new List<string>();
        using var http = new HttpClient(new StandIn(answers, asked));
        string path = Path.Combine(this.dir, "list.bin");
        var reports = new List<int>();

        long count = await PwnedPasswordBuilder.BuildFromApiAsync(http, path, minimumCount: 1, new SyncProgress(reports), parallel: 2, lastRange: 3, baseUrl: "https://range.test/range/");

        Assert.Equal(3, count);
        Assert.Equal(4, asked.Count);
        Assert.Equal(100, reports[^1]);
        PwnedPasswordList list = PwnedPasswordList.Open(path);
        Assert.True(list.Contains(Test0));
        Assert.True(list.Contains(Test1));
        Assert.True(list.Contains(Test3));
        Assert.False(list.Contains("password"));
    }

    [Fact]
    public async Task APasswordThatMeetsEveryRule_IsStillRefused_WhenItHasLeaked()
    {
        Assert.Null(PasswordPolicy.Check(StrongButLeaked));
        string path = Path.Combine(this.dir, "list.bin");
        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(StrongButLeakedSha1 + ":40"), path);

        PwnedPasswords.Use(path);
        Assert.True(PwnedPasswords.IsLeaked(StrongButLeaked));
        Assert.Contains("known data leak", PasswordPolicy.Check(StrongButLeaked), StringComparison.Ordinal);
        Assert.False(PasswordPolicy.Evaluate(StrongButLeaked).NotCommon);
        Assert.Null(PasswordPolicy.Check("Kestrel!Moss-4410"));

        PwnedPasswords.Clear();
        Assert.Null(PasswordPolicy.Check(StrongButLeaked));
    }

    [Fact]
    public async Task ANewListReplacingTheOld_IsPickedUp()
    {
        string path = Path.Combine(this.dir, "list.bin");
        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(Test0Sha1 + ":3"), path);
        PwnedPasswords.Use(path);
        Assert.Equal(1, PwnedPasswords.Current!.Count);

        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(Test0Sha1 + ":3\n" + Test1Sha1 + ":3"), path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        PwnedPasswords.Reload();
        Assert.Equal(2, PwnedPasswords.Current!.Count);
        Assert.True(PwnedPasswords.IsLeaked(Test1));
    }

    [Fact]
    public void TheRefreshStatus_IsWrittenAndReadBesideTheList()
    {
        string path = Path.Combine(this.dir, "list.bin");
        Assert.Null(PwnedRefreshStatus.Read(path));
        new PwnedRefreshStatus { State = "building", Percent = 42, Started = DateTimeOffset.UtcNow }.Write(path);
        PwnedRefreshStatus? s = PwnedRefreshStatus.Read(path);
        Assert.NotNull(s);
        Assert.Equal("building", s.State);
        Assert.Equal(42, s.Percent);
    }

    [Fact]
    public async Task Online_OnlyFiveCharactersAreSent_AndTheFullFingerprintIsMatchedHere()
    {
        // Owner, 7 Oct 2026: "both" - every new password is also checked online.
        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "both");
        var asked = new List<HttpRequestMessage>();
        string padded = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:0\r\n" + StrongButLeakedSha1[5..] + ":4\r\n";
        PwnedPasswords.OnlineClient = new HttpClient(new Recorder(padded, asked));
        PwnedPasswords.OnlineBaseUrl = "https://range.test/range/";

        Assert.Equal(PasswordPolicy.LeakedMessage, await PasswordPolicy.CheckAsync(StrongButLeaked));
        HttpRequestMessage only = Assert.Single(asked);
        Assert.Equal("https://range.test/range/" + StrongButLeakedSha1[..5], only.RequestUri!.ToString());
        Assert.True(only.Headers.Contains("Add-Padding"));
        Assert.DoesNotContain(StrongButLeakedSha1[5..], only.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);

        // A password whose fingerprint is not in the answer is accepted.
        Assert.Null(await PasswordPolicy.CheckAsync("Kestrel!Moss-4410"));
    }

    [Fact]
    public async Task Online_WhenTheServiceCannotBeReached_ThePasswordIsJudgedWithoutIt()
    {
        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "both");
        PwnedPasswords.OnlineClient = new HttpClient(new Unreachable());
        Assert.Null(await PwnedPasswords.IsLeakedOnlineAsync(StrongButLeaked));
        Assert.Null(await PasswordPolicy.CheckAsync(StrongButLeaked));

        // ...while the downloaded list still catches what it holds.
        string path = Path.Combine(this.dir, "list.bin");
        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(StrongButLeakedSha1 + ":40"), path);
        PwnedPasswords.Use(path);
        Assert.Equal(PasswordPolicy.LeakedMessage, await PasswordPolicy.CheckAsync(StrongButLeaked));
    }

    [Fact]
    public async Task DownloadMode_SendsNothing_AndOnlineMode_UsesNoFile()
    {
        var asked = new List<HttpRequestMessage>();
        PwnedPasswords.OnlineClient = new HttpClient(new Recorder(StrongButLeakedSha1[5..] + ":4", asked));
        string path = Path.Combine(this.dir, "list.bin");
        await PwnedPasswordBuilder.BuildFromTextAsync(new StringReader(StrongButLeakedSha1 + ":40"), path);
        PwnedPasswords.Use(path);

        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "download");
        Assert.Equal(PasswordPolicy.LeakedMessage, await PasswordPolicy.CheckAsync(StrongButLeaked));
        Assert.Empty(asked);

        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "online");
        Assert.False(PwnedPasswords.IsLeaked(StrongButLeaked));
        Assert.Equal(PasswordPolicy.LeakedMessage, await PasswordPolicy.CheckAsync(StrongButLeaked));
        Assert.Single(asked);

        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "off");
        Assert.Null(await PasswordPolicy.CheckAsync(StrongButLeaked));
        Assert.Single(asked);
    }

    [Fact]
    public async Task Online_TheFewestLeaks_CanBeRaised()
    {
        Environment.SetEnvironmentVariable("ANJAL_PWNED_MODE", "online");
        Environment.SetEnvironmentVariable("ANJAL_PWNED_ONLINE_MIN_COUNT", "10");
        PwnedPasswords.OnlineClient = new HttpClient(new Recorder(StrongButLeakedSha1[5..] + ":4", new List<HttpRequestMessage>()));
        Assert.False(await PwnedPasswords.IsLeakedOnlineAsync(StrongButLeaked));
        Environment.SetEnvironmentVariable("ANJAL_PWNED_ONLINE_MIN_COUNT", "3");
        Assert.True(await PwnedPasswords.IsLeakedOnlineAsync(StrongButLeaked));
    }

    private sealed class Recorder : HttpMessageHandler
    {
        private readonly string body;
        private readonly List<HttpRequestMessage> asked;

        public Recorder(string body, List<HttpRequestMessage> asked)
        {
            this.body = body;
            this.asked = asked;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.asked.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(this.body, Encoding.ASCII) });
        }
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host.");
    }

    private sealed class StandIn : HttpMessageHandler
    {
        private readonly Dictionary<string, string> answers;
        private readonly List<string> asked;

        public StandIn(Dictionary<string, string> answers, List<string> asked)
        {
            this.answers = answers;
            this.asked = asked;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string range = request.RequestUri!.AbsolutePath[^5..];
            lock (this.asked)
            {
                this.asked.Add(range);
            }
            return Task.FromResult(this.answers.TryGetValue(range, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.ASCII) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly List<int> reports;

        public SyncProgress(List<int> reports) => this.reports = reports;

        public void Report(int value) => this.reports.Add(value);
    }
}
