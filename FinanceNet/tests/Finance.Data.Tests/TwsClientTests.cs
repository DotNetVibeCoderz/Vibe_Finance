using System.Net;
using System.Net.Sockets;
using System.Text;
using Finance.Execution.Brokers;

namespace Finance.Data.Tests;

/// <summary>
/// The TWS socket protocol against a scripted fake server. The server side is written from the protocol
/// description (length-prefixed NUL-separated fields, "API\0" + version range, startApi = 71) using raw bytes, not
/// the client's own encoder, so the client is checked against the wire format rather than against itself.
/// </summary>
public class TwsClientTests
{
    [Fact]
    public void EncodesFieldsWithBigEndianLengthAndNulTerminators()
    {
        var bytes = TwsClient.EncodeMessage(49, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 5, (byte)'4', (byte)'9', 0, (byte)'1', 0 }, bytes);
        Assert.Equal(new[] { "61", "1", "", "x" }, TwsClient.DecodeFields(Encoding.ASCII.GetBytes("61\01\0\0x\0")));
    }

    private static byte[] Frame(params string[] fields)
    {
        var body = Encoding.ASCII.GetBytes(string.Concat(fields.Select(f => f + "\0")));
        var len = BitConverter.GetBytes(body.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);
        return len.Concat(body).ToArray();
    }

    private static async Task<string[]> ReadFrame(NetworkStream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);
        var body = new byte[BitConverter.ToInt32(len)];
        await s.ReadExactlyAsync(body, ct);
        var text = Encoding.ASCII.GetString(body);
        return (text.EndsWith('\0') ? text[..^1] : text).Split('\0');   // drop only the final terminator
    }

    [Fact]
    public async Task HandshakeAccountAndPositionsAgainstAFakeTws()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? handshake = null;
        string[]? startApi = null;
        var requests = new List<string>();

        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync(ct);
            var s = c.GetStream();
            var prefix = new byte[4];
            await s.ReadExactlyAsync(prefix, ct);                       // "API\0"
            var len = new byte[4];
            await s.ReadExactlyAsync(len, ct);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            var range = new byte[BitConverter.ToInt32(len)];
            await s.ReadExactlyAsync(range, ct);
            handshake = Encoding.ASCII.GetString(prefix) + Encoding.ASCII.GetString(range);
            await s.WriteAsync(Frame("176", "20251012 09:30:00 EST"), ct);

            startApi = await ReadFrame(s, ct);
            await s.WriteAsync(Frame("15", "1", "DU111,DU222"), ct);
            await s.WriteAsync(Frame("9", "1", "1001"), ct);
            await s.WriteAsync(Frame("4", "2", "-1", "2104", "Market data farm connection is OK:usfarm"), ct);

            while (true)
            {
                string[] req;
                try { req = await ReadFrame(s, ct); } catch { break; }
                lock (requests) requests.Add(string.Join("|", req));
                switch (req[0])
                {
                    case "49": await s.WriteAsync(Frame("49", "1", "1760261400"), ct); break;
                    case "61":
                        await s.WriteAsync(Frame("61", "3", "DU111", "265598", "AAPL", "STK", "", "0", "", "", "NASDAQ", "USD", "AAPL", "NMS", "150", "187.25"), ct);
                        await s.WriteAsync(Frame("61", "3", "DU222", "272093", "MSFT", "STK", "", "0", "", "", "NASDAQ", "USD", "MSFT", "NMS", "-20", "410.5"), ct);
                        await s.WriteAsync(Frame("62", "1"), ct);
                        break;
                    case "62":
                        string id = req[2];
                        await s.WriteAsync(Frame("63", "1", id, "DU111", "NetLiquidation", "1250000.5", "USD"), ct);
                        await s.WriteAsync(Frame("63", "1", id, "DU111", "BuyingPower", "4100000", "USD"), ct);
                        await s.WriteAsync(Frame("64", "1", id), ct);
                        break;
                }
            }
        }, ct);

        var notices = new List<TwsError>();
        await using (var tws = await TwsClient.ConnectAsync("127.0.0.1", port, clientId: 7, ct))
        {
            tws.Error += e => { lock (notices) notices.Add(e); };
            Assert.Equal("API\0v100..176", handshake);
            Assert.Equal(new[] { "71", "2", "7", "" }, startApi);
            Assert.Equal(176, tws.ServerVersion);
            Assert.Equal(1001, tws.NextValidOrderId);
            Assert.Equal(new[] { "DU111", "DU222" }, tws.ManagedAccounts);

            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760261400), await tws.GetCurrentTimeAsync(ct));

            var pos = await tws.GetPositionsAsync(ct);
            Assert.Equal(2, pos.Count);
            Assert.Equal(new TwsPosition("DU111", 265598, "AAPL", "STK", "USD", 150, 187.25), pos[0]);
            Assert.Equal(-20, pos[1].Quantity);

            var summary = await tws.GetAccountSummaryAsync(cancellationToken: ct);
            Assert.Equal("1250000.5", summary.Single(v => v.Tag == "NetLiquidation").Value);

            await tws.CancelOrderAsync(1001, ct);
            for (int i = 0; i < 50 && !requests.Contains("4|1|1001"); i++) await Task.Delay(20, ct);
        }
        listener.Stop();
        await server;
        Assert.Contains("4|1|1001", requests);
        Assert.Contains("64|1", requests);       // positions subscription cancelled after positionEnd
        Assert.Contains(requests, r => r.StartsWith("62|1|") && r.EndsWith("|All|NetLiquidation,TotalCashValue,BuyingPower"));
    }
}
