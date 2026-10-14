using System.Net;
using System.Net.Sockets;

namespace Anjal.Api.Tests;

/// <summary>
/// Hands out loopback TCP ports the OS reports as free. HttpListener
/// cannot bind port 0 itself, so each test probes with a throwaway
/// TcpListener and uses the port it was given. Fixed port ranges
/// collided with other services on shared CI runners.
/// rc.20 (the owner's laptop, 13-14 Oct 2026: "The process cannot access the file because it is
/// being used by another process"): on Windows the API server listens through the system's HTTP
/// service, which refuses ports that service has reserved even when a plain socket could take
/// them. Each port is therefore also tried with an HttpListener, the way the server will use it,
/// before it is handed out.
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
                if (Handed.Add(port) && HttpCanListen(port))
                {
                    return port;
                }
            }
            throw new InvalidOperationException("Could not find a free loopback port.");
        }
    }

    private static bool HttpCanListen(int port)
    {
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            http.Start();
            http.Stop();
            return true;
        }
        catch (HttpListenerException)
        {
            return false;
        }
    }
}
