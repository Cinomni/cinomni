using System.Net;
using System.Net.Sockets;
using Cinomni.Kernel.Diagnostics;

namespace Cinomni.Kernel.Net;

/// <summary>
/// The hardened outbound handler every module uses to talk to a third party. The
/// classification rules live in <see cref="SsrfGuard"/>; this is the transport that applies them.
/// <para>
/// It was the same forty lines copied into five module registrations. One copy is the security surface:
/// five copies is five places a fix has to land, and the one that is missed is the one that ships.
/// Behaviour is unchanged from those copies — redirects off, ambient proxy off, a bounded connect
/// timeout, DNS resolved here so the guard sees the address actually connected to (anti-rebinding), and
/// the same <see cref="IOException"/> when nothing public answers.
/// </para>
/// </summary>
public static class SsrfSafeHttpHandler
{
    /// <summary>
    /// Builds a handler that refuses any non-public destination.
    /// </summary>
    /// <param name="connectTimeout">How long a single TCP connect may take.</param>
    /// <param name="module">
    /// The calling module, from <see cref="CinomniTelemetry.Modules"/>. Used only as a metric tag, so an
    /// operator can tell an indexer misconfiguration from a webhook one.
    /// </param>
    public static SocketsHttpHandler Create(TimeSpan connectTimeout, string module) => new()
    {
        // A redirect is chosen by the remote side, so following one hands destination selection to the
        // party we are guarding against. The callers that legitimately need to follow one re-validate
        // the new URL themselves. It also keeps credentials home: SocketsHttpHandler strips only
        // Authorization on a redirect, so a custom key header (OpenSubtitles' Api-Key) would follow one
        // to another host.
        AllowAutoRedirect = false,
        ConnectTimeout = connectTimeout,
        // Never delegate destination selection to an ambient proxy (HTTP_PROXY/system): it would
        // connect to the proxy — which the callback below would validate as public — while the real
        // origin (e.g. an internal metadata host) travels in the request, bypassing the guard.
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) =>
            ConnectToPublicAddressAsync(context, module, cancellationToken),
    };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        string module,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var target = Array.Find(addresses, ip => !SsrfGuard.IsBlockedAddress(ip));
        if (target is null)
        {
            NetworkGuardMetrics.RecordRejection(NetworkGuardMetrics.Reasons.BlockedAddress, module);

            // The host is named in the message on purpose — an operator debugging a refused indexer
            // needs it — and just as deliberately never becomes a metric tag. See NetworkGuardMetrics.
            throw new IOException($"Refusing to connect to '{host}': no public address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
