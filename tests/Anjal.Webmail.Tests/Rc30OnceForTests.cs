using Anjal.Webmail.Services;

namespace Anjal.Webmail.Tests;

/// <summary>
/// rc.30 (D-366): Show patient details is read and logged once for each showing, even when the page sets the
/// viewer's parameters again while the first read is still waiting (macOS CI, 24 Nov 2026: two log entries for one
/// click).
/// </summary>
public class Rc30OnceForTests
{
    [Fact]
    public async Task ASecondRun_WhileTheFirstStillWaits_GetsTheSameWork()
    {
        var once = new OnceFor<string?>();
        var gate = new TaskCompletionSource<string?>();
        int started = 0;
        Task<string?> first = once.Run("message/1/", () =>
        {
            started++;
            return gate.Task;
        });
        Task<string?> second = once.Run("message/1/", () =>
        {
            started++;
            return Task.FromResult<string?>("read again");
        });
        Assert.Same(first, second);
        gate.SetResult("TEST, PATIENT");
        Assert.Equal("TEST, PATIENT", await second);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task ARunAfterTheFirstEnded_GetsItsAnswer_WithoutWorkingAgain()
    {
        var once = new OnceFor<string?>();
        int started = 0;
        Func<Task<string?>> read = () =>
        {
            started++;
            return Task.FromResult<string?>("TEST, PATIENT");
        };
        Assert.Equal("TEST, PATIENT", await once.Run("message/1/", read));
        Assert.Equal("TEST, PATIENT", await once.Run("message/1/", read));
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task AnotherFile_IsReadForItself()
    {
        var once = new OnceFor<string?>();
        int started = 0;
        Func<Task<string?>> read = () =>
        {
            started++;
            return Task.FromResult<string?>("x");
        };
        await once.Run("message/1/", read);
        await once.Run("message/1/3", read);
        await once.Run("message/2/", read);
        Assert.Equal(3, started);
    }
}
