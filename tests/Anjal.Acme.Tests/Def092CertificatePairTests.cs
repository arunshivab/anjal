using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Anjal.Acme.Tests;

/// <summary>
/// DEF-092: a renewal writes the new key, then the new certificate. Loaded in
/// between, the pair does not match; that must read as "not ready yet" (null),
/// so the server keeps its current certificate, never as an error escaping into
/// a connection being set up.
/// </summary>
public class Def092CertificatePairTests
{
    [Fact]
    public void AKeyThatDoesNotMatchTheCertificate_ReadsAsNotReadyYet_NotAnError()
    {
        string dir = Path.Combine(Path.GetTempPath(), "anjal-def092-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using ECDsa certKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", certKey, HashAlgorithmName.SHA256);
            using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var store = new CertificateStore(dir);
            File.WriteAllText(store.FullChainPath, cert.ExportCertificatePem());
            File.WriteAllText(store.CertificateKeyPath, otherKey.ExportPkcs8PrivateKeyPem());

            Assert.Null(store.LoadCertificate());

            // Once the matching key is there, the pair loads.
            File.WriteAllText(store.CertificateKeyPath, certKey.ExportPkcs8PrivateKeyPem());
            using X509Certificate2? loaded = store.LoadCertificate();
            Assert.NotNull(loaded);
            Assert.True(loaded!.HasPrivateKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
