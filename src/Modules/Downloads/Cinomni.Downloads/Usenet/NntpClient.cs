using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Cinomni.Downloads.Usenet;

/// <summary>
/// One NNTP session. Speaks only what a download needs: greeting, optional sign-in, and
/// <c>ARTICLE</c>. A refusal names the reply code, never the article id or the credential.
/// <para>
/// Every operation has a time limit. A server that goes quiet, or sends an article a line at a time,
/// would otherwise hold the one downloader for ever. Running out of time, losing the connection and a
/// <c>400</c> reply all surface as <see cref="NntpUnavailableException"/>: a later try can succeed. A
/// refusal is an <see cref="InvalidOperationException"/>: it will not.
/// </para>
/// </summary>
internal sealed class NntpClient : IAsyncDisposable
{
    /// <summary>
    /// How long connecting and signing in may take, and how long one article may take to arrive. An
    /// article is under a megabyte, so a server that needs longer than this to send one is not one to wait for.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Stream stream;
    private readonly TimeSpan timeout;

    private NntpClient(Stream stream, TimeSpan timeout)
    {
        this.stream = stream;
        this.timeout = timeout;
        // An article is bytes, not text, and yEnc uses all 256 values. Latin1 is the view that keeps one
        // character per byte; ASCII would turn every byte above 127 into '?' and keep the length.
        reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
    }

    public static Task<NntpClient> ConnectAsync(UsenetOptions options, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        return WithinAsync(limit, cancellationToken, token => OpenAsync(options, limit, token));
    }

    public Task<string> ArticleAsync(string messageId, CancellationToken cancellationToken) =>
        WithinAsync(timeout, cancellationToken, token => ReadArticleAsync(messageId, token));

    private static async Task<NntpClient> OpenAsync(UsenetOptions options, TimeSpan limit, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        Stream? stream = null;
        try
        {
            await tcp.ConnectAsync(options.Host, options.EffectivePort, cancellationToken);
            stream = tcp.GetStream();
            if (options.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = ssl;
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = options.Host }, cancellationToken);
            }

            var client = new NntpClient(stream, limit);
            await client.SignInAsync(options, cancellationToken);
            return client;
        }
        catch
        {
            // Nobody holds the session yet, so nobody else will close it, and a download that retries
            // every few seconds would leave one open socket behind per try.
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs one operation under the time limit. The caller's own cancellation stays a cancellation; only
    /// the limit running out, or the connection failing, is told apart as the server being unavailable.
    /// </summary>
    private static async Task<T> WithinAsync<T>(TimeSpan limit, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> operation)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        try
        {
            return await operation(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NntpUnavailableException("The Usenet server did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new NntpUnavailableException("The connection to the Usenet server was lost.", ex);
        }
    }

    private async Task SignInAsync(UsenetOptions options, CancellationToken cancellationToken)
    {
        await ExpectAsync(200, 201, cancellationToken);
        if (options.Username.Length == 0)
        {
            return;
        }

        await CommandAsync("AUTHINFO USER " + options.Username, cancellationToken);
        var signedIn = await ReadCodeAsync(cancellationToken);
        if (signedIn == 381)
        {
            await CommandAsync("AUTHINFO PASS " + options.Password, cancellationToken);
            await ExpectAsync(281, cancellationToken);
        }
        else if (signedIn != 281)
        {
            throw new InvalidOperationException($"The Usenet server refused the sign-in ({signedIn}).");
        }
    }

    private async Task<string> ReadArticleAsync(string messageId, CancellationToken cancellationToken)
    {
        var id = messageId.StartsWith('<') ? messageId : "<" + messageId + ">";
        await CommandAsync("ARTICLE " + id, cancellationToken);
        await ExpectAsync(220, cancellationToken);
        var body = new StringBuilder();
        while (true)
        {
            // A server that sends faster than the limit is read from the buffer, which does not look at the token.
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new NntpUnavailableException("The Usenet server closed the article.");
            if (line == ".")
            {
                break;
            }

            if (line.StartsWith("..", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            body.Append(line).Append("\r\n");
        }

        return body.ToString();
    }

    private async Task CommandAsync(string command, CancellationToken cancellationToken) =>
        await writer.WriteLineAsync(command.AsMemory(), cancellationToken);

    private async Task ExpectAsync(int first, CancellationToken cancellationToken) =>
        await ExpectAsync(first, first, cancellationToken);

    private async Task ExpectAsync(int first, int second, CancellationToken cancellationToken)
    {
        var code = await ReadCodeAsync(cancellationToken);
        if (code == first || code == second)
        {
            return;
        }

        // RFC 3977: 400 is "service not available", 403 an internal fault. Both are the server's bad
        // minute, not an answer about this command.
        throw code is 400 or 403
            ? new NntpUnavailableException($"The Usenet server is not available ({code}).")
            : new InvalidOperationException($"The Usenet server refused the command ({code}).");
    }

    private async Task<int> ReadCodeAsync(CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new NntpUnavailableException("The Usenet server closed the session.");
        return line.Length >= 3 && int.TryParse(line[..3], out var parsed) ? parsed : 0;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Courtesy only, and bounded: the session may be one the server stopped answering.
            using var quit = new CancellationTokenSource(QuitTimeout);
            await writer.WriteLineAsync("QUIT".AsMemory(), quit.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The session is being dropped either way, and it may already be. A failure here must not
            // replace the one that ended the session.
        }

        await stream.DisposeAsync();
    }
}
