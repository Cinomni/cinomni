using Grpc.Net.Client;

namespace Cinomni.Downloads.Engine;

/// <summary>
/// Answers whether the libtorrent sidecar is reachable, without doing anything to it.
/// <para>
/// It opens the channel's transport rather than calling an RPC on purpose: a probe on a readiness path
/// is called on somebody else's schedule, and it must not add a torrent, list files, or move any
/// download's state. Connecting is the whole question — if the socket comes up, the process is there.
/// </para>
/// <para>
/// Downloads owns it because Downloads owns the sidecar. The Host consumes it as the composition root,
/// which is the only project allowed to see a module implementation; no other module can reach the
/// engine through this.
/// </para>
/// </summary>
public sealed class SidecarHealthProbe(GrpcChannel channel) : IDownloadEngineProbe
{
    /// <summary>
    /// Whether the transport comes up within <paramref name="timeout"/>. Never throws: an unreachable
    /// sidecar is an answer, not an error, and it must not be able to fail a probe with an exception
    /// whose message would name the address.
    /// </summary>
    public async Task<bool> IsReachableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            await channel.ConnectAsync(budget.Token);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
