namespace Anjal.Spam.Tests;

public class BoundedTableTests
{
    private sealed class Clock
    {
        public System.DateTimeOffset Now { get; set; } = new(2026, 9, 21, 10, 0, 0, System.TimeSpan.Zero);
    }

    [Fact]
    public async System.Threading.Tasks.Task RateLimiter_UnderAFloodOfAddresses_StaysBounded()
    {
        var clock = new Clock();
        var limiter = new RateLimiter(new RateLimitOptions { MaxEntries = 100 }, () => clock.Now);
        for (int i = 0; i < 1000; i++)
        {
            clock.Now = clock.Now.AddMilliseconds(1);
            await limiter.OnConnectAsync($"198.51.{i / 250}.{i % 250}");
        }
        Assert.True(limiter.TrackedAddresses <= 100, $"tracked {limiter.TrackedAddresses}");
    }

    [Fact]
    public async System.Threading.Tasks.Task Greylist_UnderAFloodOfTriplets_StaysBounded()
    {
        var clock = new Clock();
        var grey = new Greylist(new GreylistOptions { MaxEntries = 100 }, () => clock.Now);
        for (int i = 0; i < 1000; i++)
        {
            clock.Now = clock.Now.AddMilliseconds(1);
            await grey.OnRcptToAsync($"203.0.{i / 250}.{i % 250}", null, $"s{i}@x.test", "r@y.test");
        }
        Assert.True(grey.Count <= 100, $"remembered {grey.Count}");
    }
}
