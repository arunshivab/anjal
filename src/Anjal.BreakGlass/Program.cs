using System.Security.Cryptography;
using Anjal.Store;

namespace Anjal.BreakGlass;

/// <summary>
/// rc.25 (DES-15 §6.3, D-240, D-241, OPS-16): Anjal's break-glass key, on an offline computer only.
/// <list type="bullet">
/// <item><c>make</c> - make the key: prints the line for the server's settings and the three paper shares.</item>
/// <item><c>check</c> - the yearly drill: two shares rebuild the key, and it matches the line.</item>
/// <item><c>open</c> - on an organisation's written request: two shares open that organisation's sealed
/// recovery secret, printed as its recovery sheet.</item>
/// </list>
/// Secrets are typed in when asked, never on the command line, so they stay out of the shell's history.
/// </summary>
internal static class Program
{
    private static readonly string[] Custodians = { "Share 1 - the owner", "Share 2 - Sthakitha", "Share 3 - the bank locker" };

    /// <summary>The tool.</summary>
    /// <param name="args">The command.</param>
    /// <returns>0 when done, 1 when refused, 2 for a wrong command.</returns>
    public static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        try
        {
            return command switch
            {
                "make" => Make(),
                "check" => Check(),
                "open" => Open(),
                _ => Usage(),
            };
        }
        catch (CryptographicException ex)
        {
            Console.Error.WriteLine("Refused: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("Anjal break-glass key (OPS-16). Run on an offline computer only.");
        Console.WriteLine("  Anjal.BreakGlass make    make the key: the line for the server, and three paper shares");
        Console.WriteLine("  Anjal.BreakGlass check   rebuild the key from two shares and check it against the line");
        Console.WriteLine("  Anjal.BreakGlass open    open an organisation's sealed recovery secret (its written request first)");
        return 2;
    }

    private static int Make()
    {
        (byte[] pub, byte[] priv) = MailKeys.NewKeyPair();
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = MailKeys.Wrap(priv, MailKeys.RecoveryWrapKey(secret));
        IReadOnlyList<byte[]> shares = SecretShares.Split(secret);
        Console.WriteLine("Anjal break-glass key, made " + DateTimeOffset.UtcNow.ToString("d MMM yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine("Fingerprint: " + MailKeys.PublicKeyIdText(pub));
        Console.WriteLine();
        Console.WriteLine("1. The line for the server's settings (" + BreakGlassLine.Setting + "). It opens nothing alone; keep a copy with each share:");
        Console.WriteLine();
        Console.WriteLine(BreakGlassLine.Write(pub, wrapped));
        Console.WriteLine();
        Console.WriteLine("2. The shares. Print each on its own sheet; any two rebuild the key, one alone reveals nothing:");
        for (int i = 0; i < shares.Count; i++)
        {
            Console.WriteLine();
            Console.WriteLine(Custodians[i]);
            Console.WriteLine("  " + PaperForm.Write(shares[i]));
        }
        CryptographicOperations.ZeroMemory(priv);
        CryptographicOperations.ZeroMemory(secret);
        foreach (byte[] s in shares)
        {
            CryptographicOperations.ZeroMemory(s);
        }
        return 0;
    }

    private static int Check()
    {
        if (Rebuild() is not (byte[] pub, byte[] priv))
        {
            return 1;
        }
        Console.WriteLine("The two shares rebuild the key, and it matches the line. Fingerprint: " + MailKeys.PublicKeyIdText(pub));
        CryptographicOperations.ZeroMemory(priv);
        return 0;
    }

    private static int Open()
    {
        if (Rebuild() is not (byte[] _, byte[] priv))
        {
            return 1;
        }
        Console.Write("The organisation's sealed recovery secret (from the Anjal console): ");
        string sealedText = (Console.ReadLine() ?? string.Empty).Trim();
        byte[] sealedSecret;
        try
        {
            sealedSecret = MailEnvelope.FromBase64Url(sealedText);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("Refused: that is not a sealed secret.");
            return 1;
        }
        using var ring = new MailKeyring();
        ring.AddPrivateKey(priv);
        CryptographicOperations.ZeroMemory(priv);
        byte[]? secret = MailEnvelope.Open(sealedSecret, ring);
        if (secret is null)
        {
            Console.Error.WriteLine("Refused: it was not sealed to this break-glass key.");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("The organisation's recovery sheet. Give it to its authorised signatory, in person; its administrator types it");
        Console.WriteLine("under Organisation > Encryption > Use the recovery sheet, and then prints a new sheet, which retires this one:");
        Console.WriteLine();
        Console.WriteLine("  " + PaperForm.Write(secret));
        CryptographicOperations.ZeroMemory(secret);
        return 0;
    }

    // Two shares and the line give the private key, checked against the line's public key.
    private static (byte[] Public, byte[] Private)? Rebuild()
    {
        Console.Write("The line from the server's settings: ");
        if (BreakGlassLine.Read(Console.ReadLine()) is not (byte[] pub, byte[] wrapped))
        {
            Console.Error.WriteLine("Refused: that is not a break-glass line.");
            return null;
        }
        byte[]? a = AskShare("First share: ");
        byte[]? b = a is null ? null : AskShare("Second share: ");
        if (a is null || b is null)
        {
            return null;
        }
        if (a[0] == b[0])
        {
            Console.Error.WriteLine("Refused: two different shares are needed.");
            return null;
        }
        byte[] secret = SecretShares.Combine(a, b);
        byte[]? priv = MailKeys.Unwrap(wrapped, MailKeys.RecoveryWrapKey(secret));
        CryptographicOperations.ZeroMemory(secret);
        if (priv is null || !MailKeys.PublicOf(priv).AsSpan().SequenceEqual(pub))
        {
            Console.Error.WriteLine("Refused: these shares do not open this key.");
            return null;
        }
        return (pub, priv);
    }

    private static byte[]? AskShare(string prompt)
    {
        Console.Write(prompt);
        byte[]? share = PaperForm.Read(Console.ReadLine() ?? string.Empty);
        if (share is null || share.Length != 33 || share[0] is < 1 or > 3)
        {
            Console.Error.WriteLine("Refused: that share does not check. Look at each group of four; the last group finds a mistyped character.");
            return null;
        }
        return share;
    }
}
