using System.Net;
using System.Net.Sockets;

namespace Anjal.Api.Tests;

/// <summary>
/// Hands out loopback TCP ports the OS reports as free. HttpListener
/// cannot bind port 0 itself, so each test probes with a throwaway
/// TcpListener and uses the port it was given. Fixed port ranges
/// collided with other services on shared CI runners.
/// </summary>
internal static class FreePort
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> Handed = new();

    public static int Next()
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                if (Handed.Add(port))
                {
                    return port;
                }
            }
            throw new InvalidOperationException("Could not find a free loopback port.");
        }
    }
}
