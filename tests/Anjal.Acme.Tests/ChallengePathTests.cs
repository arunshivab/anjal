namespace Anjal.Acme.Tests;

public class ChallengePathTests
{
    [Fact]
    public void OnlyTheChallengePath_IsAnswered()
    {
        string token = "tok-" + System.Guid.NewGuid().ToString("N");
        Http01ChallengeStore.Add(token, "key-authorization");
        try
        {
            Assert.Equal("key-authorization", Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix + token));
            Assert.Null(Http01ChallengeStore.Lookup("/" + token));
            Assert.Null(Http01ChallengeStore.Lookup("/.well-known/other/" + token));
            Assert.Null(Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix + token + "/extra"));
            Assert.Null(Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix));
            Assert.Null(Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix + "../" + token));
            Assert.Null(Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix + "unknown"));
        }
        finally
        {
            Http01ChallengeStore.Remove(token);
        }
        Assert.Null(Http01ChallengeStore.Lookup(Http01ChallengeStore.PathPrefix + token));
    }
}
