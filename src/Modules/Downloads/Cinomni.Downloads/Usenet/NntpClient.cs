using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Cinomni.Downloads.Usenet;

/// <summary>
/// One NNTP session. Speaks only what a download needs: greeting, optional sign-in, and
/// <c>ARTICLE</c>. A refusal names the reply code, never the article id or the credential.
/// </summary>
internal sealed class NntpClient : IAsyncDisposable
{
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Stream stream;

    private NntpClient(Stream stream)
    {
        this.stream = stream;
        // An article is bytes, not text, and yEnc uses all 256 values. Latin1 is the view that keeps one
        // character per byte; ASCII would turn every byte above 127 into '?' and keep the length.
        reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: -1, leaveOpen: true);
        writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
    }

    public static async Task<NntpClient> ConnectAsync(UsenetOptions options, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(options.Host, options.EffectivePort, cancellationToken);
        Stream stream = tcp.GetStream();
        if (options.UseTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(options.Host);
            stream = ssl;
        }

        var client = new NntpClient(stream);
        await client.ExpectAsync(200, 201, cancellationToken);
        if (options.Username.Length > 0)
        {
            await client.CommandAsync("AUTHINFO USER " + options.Username, cancellationToken);
            var signedIn = await client.ReadCodeAsync(cancellationToken);
            if (signedIn == 381)
            {
                await client.CommandAsync("AUTHINFO PASS " + options.Password, cancellationToken);
                await client.ExpectAsync(281, cancellationToken);
            }
            else if (signedIn != 281)
            {
                throw new InvalidOperationException($"The Usenet server refused the sign-in ({signedIn}).");
            }
        }

        return client;
    }

    public async Task<string> ArticleAsync(string messageId, CancellationToken cancellationToken)
    {
        var id = messageId.StartsWith('<') ? messageId : "<" + messageId + ">";
        await CommandAsync("ARTICLE " + id, cancellationToken);
        await ExpectAsync(220, cancellationToken);
        var body = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("The Usenet server closed the article.");
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

    private async Task CommandAsync(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(command);
    }

    private async Task ExpectAsync(int first, CancellationToken cancellationToken) =>
        await ExpectAsync(first, first, cancellationToken);

    private async Task ExpectAsync(int first, int second, CancellationToken cancellationToken)
    {
        var code = await ReadCodeAsync(cancellationToken);
        if (code != first && code != second)
        {
            throw new InvalidOperationException($"The Usenet server refused the command ({code}).");
        }
    }

    private async Task<int> ReadCodeAsync(CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("The Usenet server closed the session.");
        return line.Length >= 3 && int.TryParse(line[..3], out var parsed) ? parsed : 0;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await writer.WriteLineAsync("QUIT");
        }
        catch (IOException)
        {
            // The session is being dropped either way.
        }

        await stream.DisposeAsync();
    }
}
