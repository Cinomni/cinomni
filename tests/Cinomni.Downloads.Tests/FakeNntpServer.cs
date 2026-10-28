using System.Net;
using System.Net.Sockets;
using System.Text;
using Cinomni.Downloads.Usenet;

namespace Cinomni.Downloads.Tests;

/// <summary>What the fake server does with one connection.</summary>
public enum ConnectionBehavior
{
    /// <summary>Greets and answers <c>ARTICLE</c> until the client quits.</summary>
    Serve,

    /// <summary>Closes the socket as soon as it is accepted.</summary>
    Drop,

    /// <summary>Greets with <c>400</c>, as a server that is about to close does.</summary>
    Unavailable,

    /// <summary>Accepts and says nothing.</summary>
    Silent,

    /// <summary>Greets, reads what it is asked, and never answers.</summary>
    Stall,

    /// <summary>Answers <c>ARTICLE</c> with <c>220</c> and then sends one short line at a time, for ever.</summary>
    Trickle,

    /// <summary>Serves one article and then closes the socket.</summary>
    ServeOneThenDrop,
}

/// <summary>
/// A loopback NNTP server for the tests: it holds articles by message id, and each connection is
/// answered the way <see cref="Behavior"/> says, counted from zero.
/// </summary>
internal sealed class FakeNntpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, IReadOnlyList<string>> articles = new(StringComparer.Ordinal);
    private readonly Task accepting;
    private int connections;

    public FakeNntpServer()
    {
        listener.Start();
        accepting = Task.Run(AcceptAsync);
    }

    public Func<int, ConnectionBehavior> Behavior { get; set; } = _ => ConnectionBehavior.Serve;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref connections);

    public UsenetOptions Options => new() { Host = "127.0.0.1", Port = Port, UseTls = false };

    public void Add(string messageId, IReadOnlyList<string> body) => articles[messageId] = body;

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await accepting;
        stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                var index = Interlocked.Increment(ref connections) - 1;
                _ = Task.Run(() => ConverseAsync(tcp, index));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The test is over.
        }
    }

    private async Task ConverseAsync(TcpClient tcp, int index)
    {
        using (tcp)
        {
            var behavior = Behavior(index);
            if (behavior == ConnectionBehavior.Drop)
            {
                return;
            }

            try
            {
                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                // A server sends bytes, not text. Latin1 is the one-character-per-byte view of them.
                await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                switch (behavior)
                {
                    case ConnectionBehavior.Silent:
                        await Task.Delay(Timeout.Infinite, stop.Token);
                        return;
                    case ConnectionBehavior.Unavailable:
                        await writer.WriteLineAsync("400 service temporarily unavailable");
                        return;
                }

                await writer.WriteLineAsync("200 ready");
                await AnswerAsync(reader, writer, behavior);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client went away, or the test is over.
            }
        }
    }

    private async Task AnswerAsync(StreamReader reader, StreamWriter writer, ConnectionBehavior behavior)
    {
        while (await reader.ReadLineAsync(stop.Token) is { } command)
        {
            if (command.StartsWith("QUIT", StringComparison.Ordinal))
            {
                return;
            }

            if (behavior == ConnectionBehavior.Stall)
            {
                continue;
            }

            if (behavior == ConnectionBehavior.Trickle)
            {
                await writer.WriteLineAsync("220 article");
                while (true)
                {
                    await writer.WriteLineAsync("x");
                    await Task.Delay(50, stop.Token);
                }
            }

            var id = command.StartsWith("ARTICLE <", StringComparison.Ordinal) ? command["ARTICLE <".Length..^1] : null;
            if (id is null || !articles.TryGetValue(id, out var body))
            {
                await writer.WriteLineAsync(id is null ? "500 what" : "430 no such article");
                continue;
            }

            await writer.WriteLineAsync("220 article");
            await writer.WriteLineAsync("Subject: Film.mkv");
            await writer.WriteLineAsync();
            foreach (var line in body)
            {
                await writer.WriteLineAsync(line.StartsWith('.') ? "." + line : line);
            }

            await writer.WriteLineAsync(".");
            if (behavior == ConnectionBehavior.ServeOneThenDrop)
            {
                return;
            }
        }
    }
}
