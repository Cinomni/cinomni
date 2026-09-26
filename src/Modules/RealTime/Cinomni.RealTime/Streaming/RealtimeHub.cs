using System.Collections.Concurrent;
using System.Threading.Channels;
using Cinomni.Kernel.Identifiers;
using Cinomni.Kernel.Security;
using Microsoft.Extensions.Logging;

namespace Cinomni.RealTime.Streaming;

/// <summary>
/// The live fan-out: open connections register here, and everything that wants to reach a browser
/// publishes here. In-process and deliberately unpersisted — a signal is only worth delivering while
/// someone is listening, and a client that reconnects re-reads the REST surface anyway, which is the
/// authority in every case.
/// <para>
/// Singleton, because a connection outlives any request scope. Publishing never blocks and never
/// throws: a slow or dead consumer must not be able to stall the outbox relay that is calling into it.
/// </para>
/// </summary>
public sealed class RealtimeHub(ILogger<RealtimeHub> logger)
{
    /// <summary>
    /// Per-connection buffer. Signals are idempotent invalidations, so dropping the oldest of a burst
    /// costs nothing: the newest still arrives and the client re-reads everything it needs.
    /// </summary>
    private const int ConnectionBuffer = 64;

    /// <summary>
    /// Ceiling on concurrent streams. A household needs a handful; anything past this is a client in a
    /// reconnect loop or an attempt to pin server memory, and both are better refused than absorbed.
    /// </summary>
    public const int MaxConnections = 64;

    /// <summary>
    /// Ceiling for one account. A person has a few tabs and devices; without this, one account could
    /// take every slot of <see cref="MaxConnections"/> and shut the rest of the household out.
    /// </summary>
    public const int MaxConnectionsPerAccount = 8;

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly Lock _admission = new();

    /// <summary>How many streams are open right now.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Whether any open stream belongs to an administrator — the audience of operator signals.</summary>
    public bool HasOperators => _connections.Values.Any(c => c.Reader.IsAdministrator);

    /// <summary>
    /// Opens a stream for one reader. Null when the hub is already at <see cref="MaxConnections"/>, or the
    /// reader's account at <see cref="MaxConnectionsPerAccount"/>, which the endpoint answers with 503
    /// rather than queueing.
    /// </summary>
    public RealtimeConnection? Connect(Viewer reader)
    {
        var connection = new Connection(
            Uuid7.New(),
            reader,
            Channel.CreateBounded<RealtimeMessage>(new BoundedChannelOptions(ConnectionBuffer)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }));

        // Counted and added under one lock: two connects racing past a check-then-add would both fit.
        lock (_admission)
        {
            if (_connections.Count >= MaxConnections)
            {
                logger.LogWarning("Refusing a realtime connection: {Count} are already open.", _connections.Count);
                return null;
            }

            if (_connections.Values.Count(c => c.Reader.UserId == reader.UserId) >= MaxConnectionsPerAccount)
            {
                logger.LogWarning(
                    "Refusing a realtime connection: the account already has {Count} open.", MaxConnectionsPerAccount);
                return null;
            }

            _connections[connection.Id] = connection;
        }

        return new RealtimeConnection(connection.Channel.Reader, () => Disconnect(connection.Id));
    }

    /// <summary>
    /// Delivers a message to every connection its audience includes. Best-effort by design: a full or
    /// closed channel is skipped, never awaited.
    /// </summary>
    public void Publish(RealtimeMessage message)
    {
        foreach (var connection in _connections.Values)
        {
            if (message.Audience.Includes(connection.Reader))
            {
                connection.Channel.Writer.TryWrite(message);
            }
        }
    }

    private void Disconnect(Guid id)
    {
        if (_connections.TryRemove(id, out var connection))
        {
            connection.Channel.Writer.TryComplete();
        }
    }

    private sealed record Connection(Guid Id, Viewer Reader, Channel<RealtimeMessage> Channel);
}

/// <summary>
/// One open stream: what to read from, and the disposal that unregisters it. Disposing is what keeps
/// the hub from leaking a connection per aborted request.
/// </summary>
public sealed class RealtimeConnection(ChannelReader<RealtimeMessage> reader, Action release) : IDisposable
{
    private bool _released;

    public ChannelReader<RealtimeMessage> Reader { get; } = reader;

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        release();
    }
}
