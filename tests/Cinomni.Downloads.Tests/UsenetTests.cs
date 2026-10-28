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

        Assert.Equal(plain, Yenc.Decode(Wire(YencArticle(plain))));
        Assert.Throws<InvalidOperationException>(() => Yenc.Decode("not yenc"));
    }

    [Fact]
    public void Yenc_keeps_every_byte_value_of_a_part()
    {
        // yEnc shifts each byte by 42, so about half of what an article carries is above 127. A decoder
        // that reads the article as ASCII turns every one of those into '?' and still reports the right
        // length, which is how a video arrives whole and unplayable.
        var plain = AllByteValues();

        Assert.Equal(plain, Yenc.Decode(Wire(YencArticle(plain, $"crc32={ReferenceCrc32(plain):x8}"))));
    }

    [Fact]
    public void Yenc_refuses_a_part_whose_crc_is_not_the_one_it_states()
    {
        // "123456789" is the standard CRC-32 check string; its CRC is cbf43926.
        var plain = "123456789"u8.ToArray();

        Assert.Equal(plain, Yenc.Decode(Wire(YencArticle(plain, "crc32=cbf43926"))));
        Assert.Equal(plain, Yenc.Decode(Wire(YencArticle(plain, "crc32=CBF43926"))));
        Assert.Throws<InvalidOperationException>(() => Yenc.Decode(Wire(YencArticle(plain, "crc32=cbf43927"))));
    }

    [Fact]
    public void A_multi_part_article_is_checked_against_its_own_part_crc()
    {
        // On the last part crc32 is the whole file's and says nothing about this part. pcrc32 is the one
        // that does, and it must not be mistaken for crc32 because the name ends the same way.
        var plain = "123456789"u8.ToArray();
        const string part = "=ypart begin=1 end=9";

        Assert.Equal(plain, Yenc.Decode(Wire(YencArticle(plain, "part=1 pcrc32=cbf43926 crc32=deadbeef", part))));
        Assert.Throws<InvalidOperationException>(
            () => Yenc.Decode(Wire(YencArticle(plain, "part=1 pcrc32=00000000 crc32=cbf43926", part))));
    }

    [Fact]
    public async Task A_session_fetches_one_article_and_does_not_need_a_password_in_the_reply()
    {
        var article = await FetchAsync(YencArticle([1, 2, 3, 19]));

        Assert.Contains("=ybegin", article, StringComparison.Ordinal);
        Assert.Contains("=yend", article, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_delivers_every_byte_value_of_a_part()
    {
        // The first byte is 4, which encodes to '.', so the first data line travels dot-stuffed.
        var plain = AllByteValues();

        var article = await FetchAsync(YencArticle(plain, $"crc32={ReferenceCrc32(plain):x8}"));

        Assert.Equal(plain, Yenc.Decode(article));
    }

    private static async Task<string> FetchAsync(IReadOnlyList<string> body)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(() => ServeAsync(listener, body));
        var options = new UsenetOptions { Host = "127.0.0.1", Port = port, UseTls = false, Username = "member", Password = "secret" };

        await using var client = await NntpClient.ConnectAsync(options, CancellationToken.None);
        var article = await client.ArticleAsync("part@example", CancellationToken.None);

        await served;
        return article;
    }

    private static async Task ServeAsync(TcpListener listener, IReadOnlyList<string> body)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        // A server sends bytes, not text. Latin1 is the one-character-per-byte view of them.
        await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync());
        await writer.WriteLineAsync("381 more");
        var pass = await reader.ReadLineAsync();
        Assert.Equal("AUTHINFO PASS secret", pass);
        await writer.WriteLineAsync("281 ok");
        Assert.StartsWith("ARTICLE", await reader.ReadLineAsync());
        await writer.WriteLineAsync("220 article");
        await writer.WriteLineAsync("Subject: Film.mkv");
        await writer.WriteLineAsync();
        foreach (var line in body)
        {
            await writer.WriteLineAsync(line.StartsWith('.') ? "." + line : line);
        }

        await writer.WriteLineAsync(".");
    }

    /// <summary>1000 bytes that cover every value several times over and start with 4.</summary>
    private static byte[] AllByteValues() =>
        [.. Enumerable.Range(0, 1000).Select(i => (byte)((i + 4) % 256))];

    private static string Wire(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";

    /// <summary>A yEnc article body as the wire carries it: one character per byte.</summary>
    private static List<string> YencArticle(byte[] plain, string? trailer = null, string? partHeader = null)
    {
        var lines = new List<string> { $"=ybegin line=128 size={plain.Length} name=Film.mkv" };
        if (partHeader is not null)
        {
            lines.Add(partHeader);
        }

        lines.AddRange(EncodeLines(plain));
        lines.Add(trailer is null ? $"=yend size={plain.Length}" : $"=yend size={plain.Length} {trailer}");
        return lines;
    }

    private static IEnumerable<string> EncodeLines(byte[] plain)
    {
        const int lineLength = 128;
        var line = new StringBuilder();
        foreach (var value in plain)
        {
            var shifted = (byte)(value + 42);
            var critical = shifted is 0 or 10 or 13 or (byte)'=';
            if (line.Length + (critical ? 2 : 1) > lineLength)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (critical)
            {
                line.Append('=').Append((char)(byte)(shifted + 64));
            }
            else
            {
                line.Append((char)shifted);
            }
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    /// <summary>The bitwise CRC-32, written differently from the one under test so they can disagree.</summary>
    private static uint ReferenceCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
