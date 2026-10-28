using System.Net;
using System.Net.Sockets;
using System.Text;
using Cinomni.Downloads.Usenet;

namespace Cinomni.Downloads.Tests;

public sealed class UsenetTests
{
    [Fact]
    public void An_nzb_names_the_quoted_file_and_orders_its_segments()
    {
        const string xml = """
            <nzb>
              <file subject="&quot;Film.mkv&quot; yEnc (1/2)">
                <segments>
                  <segment bytes="4" number="2">part-2@example</segment>
                  <segment bytes="4" number="1">part-1@example</segment>
                </segments>
              </file>
            </nzb>
            """;

        var document = NzbDocumentParser.Parse(xml);

        Assert.Equal("Film.mkv", document.Files[0].Name);
        Assert.Equal([1, 2], document.Files[0].Segments.Select(segment => segment.Number));
        Assert.Null(NzbPolicy.Refusal(document));
    }

    [Fact]
    public void A_path_in_a_file_name_is_not_a_file()
    {
        const string xml = """
            <nzb>
              <file subject="&quot;../etc/passwd&quot; yEnc (1/1)">
                <segments><segment bytes="1" number="1">x@example</segment></segments>
              </file>
            </nzb>
            """;

        Assert.Throws<InvalidOperationException>(() => NzbDocumentParser.Parse(xml));
    }

    [Fact]
    public void A_packed_release_is_refused_before_anything_is_fetched()
    {
        const string xml = """
            <nzb>
              <file subject="&quot;Film.rar&quot; yEnc (1/1)">
                <segments><segment bytes="1" number="1">x@example</segment></segments>
              </file>
              <file subject="&quot;Film.mkv&quot; yEnc (1/1)">
                <segments><segment bytes="1" number="1">y@example</segment></segments>
              </file>
            </nzb>
            """;

        Assert.Equal(NzbPolicy.Packed, NzbPolicy.Refusal(NzbDocumentParser.Parse(xml)));
    }

    [Fact]
    public void Yenc_round_trips_a_part_and_rejects_a_short_one()
    {
        var plain = new byte[] { 1, 2, 3, 19 };
        var article = "=ybegin line=128 size=4 name=Film.mkv\r\n" + Encode(plain) + "\r\n=yend size=4\r\n";

        Assert.Equal(plain, Yenc.Decode(article));
        Assert.Throws<InvalidOperationException>(() => Yenc.Decode("not yenc"));
    }

    [Fact]
    public async Task A_session_fetches_one_article_and_does_not_need_a_password_in_the_reply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(() => ServeAsync(listener));
        var options = new UsenetOptions { Host = "127.0.0.1", Port = port, UseTls = false, Username = "member", Password = "secret" };

        await using var client = await NntpClient.ConnectAsync(options, CancellationToken.None);
        var article = await client.ArticleAsync("part@example", CancellationToken.None);

        Assert.Contains("=ybegin", article, StringComparison.Ordinal);
        await served;
    }

    private static async Task ServeAsync(TcpListener listener)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync());
        await writer.WriteLineAsync("381 more");
        var pass = await reader.ReadLineAsync();
        Assert.Equal("AUTHINFO PASS secret", pass);
        await writer.WriteLineAsync("281 ok");
        Assert.StartsWith("ARTICLE", await reader.ReadLineAsync());
        await writer.WriteLineAsync("220 article");
        await writer.WriteLineAsync("=ybegin line=128 size=1 name=a.mkv");
        await writer.WriteLineAsync(".");
        await writer.WriteLineAsync("=yend size=1");
        await writer.WriteLineAsync(".");
    }

    private static string Encode(byte[] plain)
    {
        var encoded = new StringBuilder();
        foreach (var value in plain)
        {
            var shifted = (byte)(value + 42);
            if (shifted is 0 or 10 or 13 or (byte)'=')
            {
                encoded.Append('=').Append((char)(shifted + 64));
            }
            else
            {
                encoded.Append((char)shifted);
            }
        }

        return encoded.ToString();
    }
}
