using System.Security.Cryptography;

namespace Anjal.Examples.DkimKeygen;

/// <summary>
/// Generates an RSA-2048 keypair for DKIM signing.
///
/// Usage:
///   dotnet run --project examples/Anjal.DkimKeygen -- &lt;domain&gt; &lt;selector&gt; [output-dir]
///
/// Writes <c>{selector}.private.pem</c> (PKCS#8) and <c>{selector}.public.pem</c>
/// to the output directory (default: current directory), and prints the
/// DNS TXT record to publish at <c>{selector}._domainkey.{domain}</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: dotnet run --project examples/Anjal.DkimKeygen -- <domain> <selector> [output-dir]");
            return 1;
        }
        string domain = args[0];
        string selector = args[1];
        string outDir = args.Length >= 3 ? args[2] : Environment.CurrentDirectory;

        Directory.CreateDirectory(outDir);
        string privatePath = Path.Combine(outDir, $"{selector}.private.pem");
        string publicPath = Path.Combine(outDir, $"{selector}.public.pem");

        using RSA rsa = RSA.Create(2048);
        string privatePem = rsa.ExportPkcs8PrivateKeyPem();
        string publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(privatePath, privatePem);
        File.WriteAllText(publicPath, publicPem);

        // For the DNS TXT, we need just the base64-encoded SubjectPublicKeyInfo
        // (the "p=" value), without the PEM armor.
        byte[] publicDer = rsa.ExportSubjectPublicKeyInfo();
        string publicBase64 = Convert.ToBase64String(publicDer);

        Console.WriteLine($"Generated RSA-2048 keypair:");
        Console.WriteLine($"  private key: {privatePath}");
        Console.WriteLine($"  public key:  {publicPath}");
        Console.WriteLine();
        Console.WriteLine($"DNS TXT record to publish at: {selector}._domainkey.{domain}");
        Console.WriteLine();
        Console.WriteLine("  v=DKIM1; k=rsa; p=" + publicBase64);
        Console.WriteLine();
        Console.WriteLine("Anjal env-var configuration:");
        Console.WriteLine($"  ANJAL_DKIM_MODE=required");
        Console.WriteLine($"  ANJAL_DKIM_DOMAIN={domain}");
        Console.WriteLine($"  ANJAL_DKIM_SELECTOR={selector}");
        Console.WriteLine($"  ANJAL_DKIM_KEY_PATH={privatePath}");
        Console.WriteLine();
        Console.WriteLine("Or upload via API:");
        Console.WriteLine($"  POST /api/dkim-keys");
        Console.WriteLine("  {");
        Console.WriteLine($"    \"domain\": \"{domain}\",");
        Console.WriteLine($"    \"selector\": \"{selector}\",");
        Console.WriteLine("    \"privateKeyPem\": \"<contents of " + privatePath + ">\"");
        Console.WriteLine("  }");
        return 0;
    }
}
