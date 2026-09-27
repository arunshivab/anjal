using Anjal.Smtp;

namespace Anjal.Server.Tests;

/// <summary>v1.0.0-rc.8 (INC-01 P-5): delivery log lines say which server answered and whether the connection was encrypted.</summary>
public sealed class Rc8RouteLogTests
{
    [Fact]
    public void AnEncryptedAttempt_NamesTheServerAndTheCipher()
    {
        var r = new SendResult { Outcome = SendOutcome.PermanentFailure, RemoteHost = "mx.rediffmail.rediff.akadns.net", TransportTls = "TLSv1.2 TLS_DHE_RSA_WITH_AES_256_GCM_SHA384" };
        Assert.Equal(" via mx.rediffmail.rediff.akadns.net (TLSv1.2 TLS_DHE_RSA_WITH_AES_256_GCM_SHA384)", OutboundWorker.Route(r));
    }

    [Fact]
    public void AnUnencryptedAttempt_SaysSo_AndNoServerReachedSaysNothing()
    {
        Assert.Equal(" via mx1.example.org (unencrypted)", OutboundWorker.Route(new SendResult { RemoteHost = "mx1.example.org" }));
        Assert.Equal(string.Empty, OutboundWorker.Route(new SendResult { Message = "DNS error" }));
    }
}
