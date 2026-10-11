using Finance.Backtesting;
using Finance.Core.Market;

namespace Finance.Portfolio.Tests;

/// <summary>
/// Order-book replay (latency, depth, FIFO queue) and corporate-action handling. Every scenario is built by hand
/// so the expected fills and cash follow from the definitions.
/// </summary>
public class BacktestingV2Tests
{
    private static readonly DateTime T0 = new(2025, 3, 3, 14, 30, 0);
    private static DateTime Ms(int ms) => T0.AddMilliseconds(ms);

    private static BookEvent Snap(int ms, (double P, double S)[] bids, (double P, double S)[] asks)
        => new(Ms(ms), BookEventKind.Snapshot, Bids: bids.Select(b => new BookLevel(b.P, b.S)).ToList(), Asks: asks.Select(a => new BookLevel(a.P, a.S)).ToList());

    private static BookEvent Upd(int ms, Side side, double price, double size) => new(Ms(ms), BookEventKind.Update, side, price, size);
    private static BookEvent Trade(int ms, Side aggressor, double price, double size) => new(Ms(ms), BookEventKind.Trade, aggressor, price, size);

    /// <summary>Runs a script: at the given event index, do something with the context.</summary>
    private sealed class Script(Dictionary<int, Action<BookReplayContext>> actions) : BookStrategy
    {
        private int _i;
        public override void OnEvent(BookReplayContext context, BookEvent e)
        {
            if (actions.TryGetValue(_i++, out var a)) a(context);
        }
    }

    private static readonly BookEvent Book0 = Snap(0, new[] { (99.0, 10.0), (98.0, 20.0) }, new[] { (100.0, 5.0), (101.0, 5.0) });

    [Fact]
    public void MarketOrderWalksTheDepth()
    {
        var replay = new OrderBookReplay("X") { InitialCash = 10_000, TakerFeeBps = 2 };
        var r = replay.Run(new Script(new() { [0] = c => c.SubmitMarket(Side.Buy, 8) }), new[] { Book0, Upd(1, Side.Buy, 99, 10) });
        Assert.Equal(new[] { (5.0, 100.0), (3.0, 101.0) }, r.Fills.Select(f => (f.Quantity, f.Price)));
        double notional = 5 * 100 + 3 * 101;
        Assert.Equal(8, r.FinalPosition);
        Assert.Equal(10_000 - notional - notional * 2e-4, r.FinalCash, 9);
        Assert.All(r.Fills, f => Assert.False(f.Maker));
    }

    [Fact]
    public void LatencyMeansTradingAgainstTheBookOnArrival()
    {
        // The 100 offer is lifted 5ms after our decision; with 10ms latency our order arrives to a 101 best ask.
        var events = new[] { Book0, Upd(5, Side.Sell, 100, 0), Upd(20, Side.Buy, 99, 11) };
        var fast = new OrderBookReplay("X").Run(new Script(new() { [0] = c => c.SubmitMarket(Side.Buy, 2) }), events);
        var slow = new OrderBookReplay("X", new ConstantLatency(TimeSpan.FromMilliseconds(10)))
            .Run(new Script(new() { [0] = c => c.SubmitMarket(Side.Buy, 2) }), events);
        Assert.Equal(100, fast.Fills.Single().Price);
        Assert.Equal(101, slow.Fills.Single().Price);
        Assert.Equal(Ms(10), slow.Fills.Single().Time);
    }

    [Fact]
    public void RestingOrderWaitsForTheQueueAhead()
    {
        // Join the 99 bid behind 10 lots. A 6-lot sell print leaves 4 ahead; a 7-lot print fills us for 3.
        var events = new[] { Book0, Trade(1, Side.Sell, 99, 6), Trade(2, Side.Sell, 99, 7), Trade(3, Side.Sell, 99, 1) };
        var replay = new OrderBookReplay("X") { MakerFeeBps = -0.5 };
        var r = replay.Run(new Script(new() { [0] = c => c.SubmitLimit(Side.Buy, 5, 99) }), events);
        Assert.Equal(new[] { 3.0, 1.0 }, r.Fills.Select(f => f.Quantity));
        Assert.All(r.Fills, f => { Assert.True(f.Maker); Assert.Equal(99, f.Price); });
        Assert.True(r.FeesPaid < 0);                                  // maker rebate
        Assert.Equal(1, r.Orders.Single().Remaining, 12);
    }

    [Fact]
    public void CancellationsAheadAdvanceTheQueueOnlyWhenAssumed()
    {
        // 6 of the 10 lots ahead are pulled, then a 5-lot print: optimistic fills 1, pessimistic fills none.
        var events = new[] { Book0, Upd(1, Side.Buy, 99, 4), Trade(2, Side.Sell, 99, 5) };
        var opt = new OrderBookReplay("X").Run(new Script(new() { [0] = c => c.SubmitLimit(Side.Buy, 5, 99) }), events);
        var pess = new OrderBookReplay("X") { CancelsAdvanceQueue = false }.Run(new Script(new() { [0] = c => c.SubmitLimit(Side.Buy, 5, 99) }), events);
        Assert.Equal(1, opt.FinalPosition, 12);
        Assert.Equal(0, pess.FinalPosition, 12);
    }

    [Fact]
    public void TradeThroughAndCrossedBookFillAtTheLimit()
    {
        var through = new OrderBookReplay("X").Run(
            new Script(new() { [0] = c => c.SubmitLimit(Side.Buy, 4, 99) }), new[] { Book0, Trade(1, Side.Sell, 98.5, 50) });
        Assert.Equal(4, through.FinalPosition);
        Assert.Equal(99, through.Fills.Single().Price);

        var crossed = new OrderBookReplay("X").Run(
            new Script(new() { [0] = c => c.SubmitLimit(Side.Sell, 2, 100.5) }), new[] { Book0, Upd(1, Side.Buy, 100.8, 3) });
        Assert.Equal(-2, crossed.FinalPosition);
        Assert.Equal(100.5, crossed.Fills.Single().Price);
    }

    [Fact]
    public void CancelInFlightCanStillBeFilled()
    {
        // Cancel requested at event 1 with 10ms latency; a print through our price at 5ms fills us first.
        ReplayOrder? order = null;
        var replay = new OrderBookReplay("X", new ConstantLatency(TimeSpan.FromMilliseconds(10)));
        var events = new[] { Book0, Upd(12, Side.Buy, 98, 21), Upd(13, Side.Buy, 98, 22), Trade(16, Side.Sell, 98.9, 50), Upd(40, Side.Buy, 98, 23) };
        var r = replay.Run(new Script(new()
        {
            [0] = c => order = c.SubmitLimit(Side.Buy, 1, 99),        // live at 10ms
            [2] = c => c.Cancel(order!),                               // requested at 13ms, effective 23ms
        }), events);
        Assert.Equal(1, r.FinalPosition);
    }

    // ---------------- Corporate actions ----------------

    private static readonly DateTime D0 = new(2024, 6, 3);
    private static Bar Day(int d, double px) => new(D0.AddDays(d), px, px, px, px, 1_000_000);

    private sealed class BuyOnce(string symbol, double quantity) : Strategy
    {
        private bool _done;
        public override void OnBar(StrategyContext context, BarSlice slice)
        {
            if (_done) return;
            context.Buy(symbol, quantity);
            _done = true;
        }
    }

    [Fact]
    public void SplitRescalesThePositionAndKeepsEquityContinuous()
    {
        // Raw prices halve on the ex-date; 100 shares become 200 and equity does not jump.
        var bars = new[] { Day(0, 50), Day(1, 50), Day(2, 25), Day(3, 25) };
        var settings = new BacktestSettings { InitialCash = 10_000, CorporateActions = new[] { CorporateAction.Split("X", D0.AddDays(2), 2) } };
        var r = new BacktestEngine(settings).Run(new BuyOnce("X", 100), "X", bars);
        Assert.Equal(200, r.FinalPositions["X"].Quantity);
        Assert.Equal(25, r.FinalPositions["X"].AveragePrice, 12);
        Assert.Equal(r.Equity[1], r.Equity[2], 9);
    }

    [Fact]
    public void DividendIsPaidInCashOnTheExDate()
    {
        // Price drops by the 1.50 dividend on the ex-date; cash rises by 100 × 1.50, total equity unchanged.
        var bars = new[] { Day(0, 40), Day(1, 40), Day(2, 38.5), Day(3, 38.5) };
        var settings = new BacktestSettings { InitialCash = 10_000, CorporateActions = new[] { CorporateAction.Dividend("X", D0.AddDays(2), 1.5) } };
        var r = new BacktestEngine(settings).Run(new BuyOnce("X", 100), "X", bars);
        Assert.Equal(r.Cash[1] + 150, r.Cash[2], 9);
        Assert.Equal(r.Equity[1], r.Equity[2], 9);
    }

    [Fact]
    public void DelistingClosesThePositionAtTheDelistingReturn()
    {
        // X goes bankrupt (delisting return −100%); Y keeps the engine running.
        var data = new Dictionary<string, Bar[]>
        {
            ["X"] = new[] { Day(0, 20), Day(1, 20), Day(2, 18) },
            ["Y"] = new[] { Day(0, 10), Day(1, 10), Day(2, 10), Day(3, 10), Day(4, 10) },
        };
        var settings = new BacktestSettings { InitialCash = 10_000, Delistings = new[] { new Delisting("X", D0.AddDays(3), -1.0) } };
        var r = new BacktestEngine(settings).Run(new BuyOnce("X", 100), data);
        Assert.True(r.FinalPositions["X"].IsFlat);
        Assert.Equal(10_000 - 100 * 20, r.Equity[^1], 9);                // the whole position is lost
        Assert.Contains(r.Messages, m => m.Contains("Delisted X"));
    }

    [Fact]
    public void AdjusterProducesTotalReturnSeries()
    {
        var raw = new[] { Day(0, 40), Day(1, 40), Day(2, 38.5), Day(3, 19.25) };
        var adj = CorporateActionAdjuster.Adjust(raw, new[]
        {
            CorporateAction.Dividend("X", D0.AddDays(2), 1.5),
            CorporateAction.Split("X", D0.AddDays(3), 2),
        });
        // Dividend: earlier closes × (1 − 1.5/40) → flat total return; split: earlier bars ÷ 2.
        Assert.Equal(40 * (1 - 1.5 / 40) / 2, adj[1].Close, 12);
        Assert.Equal(38.5 / 2, adj[2].Close, 12);
        Assert.Equal(19.25, adj[3].Close, 12);
        Assert.Equal(1_000_000 * 2, adj[0].Volume, 6);
    }

    [Fact]
    public void PointInTimeUniverseReflectsHistoricalMembership()
    {
        var u = new PointInTimeUniverse(new[]
        {
            new UniverseMembership("AAA", new DateTime(2020, 1, 1)),
            new UniverseMembership("BBB", new DateTime(2020, 1, 1), new DateTime(2021, 6, 30)),   // removed (or delisted)
            new UniverseMembership("CCC", new DateTime(2022, 1, 1)),
        });
        Assert.Equal(new[] { "AAA", "BBB" }, u.Members(new DateTime(2021, 1, 1)));
        Assert.Equal(new[] { "AAA", "CCC" }, u.Members(new DateTime(2023, 1, 1)));
        Assert.False(u.IsMember("BBB", new DateTime(2021, 7, 1)));
    }
}
