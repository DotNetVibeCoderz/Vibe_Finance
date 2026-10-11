using Finance.Core.Market;
using Finance.Execution.Fix;

namespace Finance.Data.Tests;

/// <summary>Persistent FIX sessions (message store, recovery across restarts) and FIX 5.0 SP2 over FIXT.1.1.</summary>
public class FixPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "financenet-fix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void FileStoreSurvivesReopen()
    {
        var a = new FileFixMessageStore(_dir, "S1");
        a.NextSenderSeqNum = 7;
        a.NextTargetSeqNum = 4;
        var msg = new FixMessage(FixMsgType.NewOrderSingle).Set(FixTags.ClOrdID, "X1").EncodeString();
        a.Save(6, msg);

        var b = new FileFixMessageStore(_dir, "S1");
        Assert.Equal(7, b.NextSenderSeqNum);
        Assert.Equal(4, b.NextTargetSeqNum);
        Assert.Equal(msg, b.Get(1, 10).Single().Message);   // SOH-delimited content round-trips

        b.Reset();
        var c = new FileFixMessageStore(_dir, "S1");
        Assert.Equal(1, c.NextSenderSeqNum);
        Assert.Empty(c.Get(1, 100));
    }

    private (FixSession Init, FixSession Acc) Pair(bool reset)
    {
        var (a, b) = InMemoryFixTransport.CreatePair();
        var init = new FixSession(new FixSessionSettings("CLIENT", "BROKER", true) { ResetSeqNumOnLogon = reset }, a,
            store: new FileFixMessageStore(_dir, "CLIENT-BROKER"));
        var acc = new FixSession(new FixSessionSettings("BROKER", "CLIENT", false) { ResetSeqNumOnLogon = reset }, b,
            store: new FileFixMessageStore(_dir, "BROKER-CLIENT"));
        return (init, acc);
    }

    [Fact]
    public async Task RestartWithoutResetRecoversMessagesSentWhileThePeerWasDown()
    {
        var ct = TestContext.Current.CancellationToken;

        // Day 1: log on, deliver order A, then the acceptor goes away and order B is sent into the void.
        var (init, acc) = Pair(reset: true);
        var day1 = new TaskCompletionSource<FixMessage>();
        acc.MessageReceived += m => day1.TrySetResult(m);
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await Task.WhenAll(init.LoggedOn, acc.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("AAPL", 1), "A"), ct);
        Assert.Equal("A", (await day1.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)).Get(FixTags.ClOrdID));
        await acc.DisposeAsync();
        await init.DisposeAsync();

        // The initiator then crashed right after persisting order B (seq 3) and before it reached the wire;
        // the session persists before sending precisely so that this case is recoverable.
        var crashed = new FileFixMessageStore(_dir, "CLIENT-BROKER");
        var orderB = FixMapping.NewOrderSingle(Order.MarketBuy("AAPL", 2), "B")
            .Set(FixTags.MsgSeqNum, 3).Set(FixTags.SenderCompID, "CLIENT").Set(FixTags.TargetCompID, "BROKER")
            .Set(FixTags.SendingTime, DateTime.UtcNow);
        crashed.Save(3, orderB.EncodeString());
        crashed.NextSenderSeqNum = 4;

        // Day 2: fresh processes and transports, same stores, no sequence reset.
        var (init2, acc2) = Pair(reset: false);
        Assert.Equal(4, init2.NextOutgoingSeqNum);
        Assert.Equal(3, acc2.NextExpectedSeqNum);
        var recovered = new TaskCompletionSource<FixMessage>();
        acc2.MessageReceived += m => recovered.TrySetResult(m);
        await acc2.StartAsync(ct);
        await init2.StartAsync(ct);
        var b = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("B", b.Get(FixTags.ClOrdID));
        Assert.True(b.GetBool(FixTags.PossDupFlag));
        Assert.Equal(3, b.MsgSeqNum);
        await Task.WhenAll(init2.LoggedOn, acc2.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);
        // The logon (4) was gap-filled during the resend: both sides agree on the next number.
        for (int i = 0; i < 50 && acc2.NextExpectedSeqNum != init2.NextOutgoingSeqNum; i++) await Task.Delay(20, ct);
        Assert.Equal(init2.NextOutgoingSeqNum, acc2.NextExpectedSeqNum);
        await init2.DisposeAsync();
        await acc2.DisposeAsync();
    }

    [Fact]
    public async Task Fix50Sp2LogsOnOverFixt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (a, b) = InMemoryFixTransport.CreatePair();
        var init = new FixSession(FixSessionSettings.Fix50Sp2("CLIENT", "BROKER", true), a);
        var acc = new FixSession(FixSessionSettings.Fix50Sp2("BROKER", "CLIENT", false), b);
        var logons = new List<FixMessage>();
        acc.MessageLogged += (m, sent) => { if (!sent && m.MsgType == FixMsgType.Logon) lock (logons) logons.Add(m); };
        var got = new TaskCompletionSource<FixMessage>();
        acc.MessageReceived += m => got.TrySetResult(m);
        await acc.StartAsync(ct);
        await init.StartAsync(ct);
        await Task.WhenAll(init.LoggedOn, acc.LoggedOn).WaitAsync(TimeSpan.FromSeconds(5), ct);

        var logon = logons.Single();
        Assert.Equal("FIXT.1.1", logon.Get(FixTags.BeginString));
        Assert.Equal("9", logon.Get(FixTags.DefaultApplVerID));
        await init.SendAsync(FixMapping.NewOrderSingle(Order.MarketBuy("MSFT", 5), "Z1"), ct);
        var nos = await got.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("FIXT.1.1", nos.Get(FixTags.BeginString));
        Assert.Equal("Z1", nos.Get(FixTags.ClOrdID));
        await init.DisposeAsync();
        await acc.DisposeAsync();
    }
}
