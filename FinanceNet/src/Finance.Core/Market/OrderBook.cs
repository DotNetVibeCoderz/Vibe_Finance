namespace Finance.Core.Market;

/// <summary>A price level of an order book.</summary>
public readonly record struct BookLevel(double Price, double Size);

/// <summary>
/// Level-2 order book. Bids are kept best (highest) first, asks best (lowest) first.
/// </summary>
public sealed class OrderBook
{
    private readonly List<BookLevel> _bids = new();
    private readonly List<BookLevel> _asks = new();

    public OrderBook(string symbol, DateTime time = default)
    {
        Symbol = symbol;
        Time = time;
    }

    public string Symbol { get; }
    public DateTime Time { get; set; }
    public IReadOnlyList<BookLevel> Bids => _bids;
    public IReadOnlyList<BookLevel> Asks => _asks;

    public double BestBid => _bids.Count > 0 ? _bids[0].Price : double.NaN;
    public double BestAsk => _asks.Count > 0 ? _asks[0].Price : double.NaN;
    public double Mid => (BestBid + BestAsk) * 0.5;
    public double Spread => BestAsk - BestBid;

    /// <summary>Replaces the bid side; levels are sorted into book order.</summary>
    public void SetBids(IEnumerable<BookLevel> levels)
    {
        _bids.Clear();
        _bids.AddRange(levels.Where(l => l.Size > 0));
        _bids.Sort((a, b) => b.Price.CompareTo(a.Price));
    }

    /// <summary>Replaces the ask side; levels are sorted into book order.</summary>
    public void SetAsks(IEnumerable<BookLevel> levels)
    {
        _asks.Clear();
        _asks.AddRange(levels.Where(l => l.Size > 0));
        _asks.Sort((a, b) => a.Price.CompareTo(b.Price));
    }

    /// <summary>Applies an incremental update; a size of zero removes the level.</summary>
    public void Update(Side side, double price, double size)
    {
        var list = side == Side.Buy ? _bids : _asks;
        int i = list.FindIndex(l => l.Price == price);
        if (i >= 0)
        {
            if (size <= 0) list.RemoveAt(i);
            else list[i] = new BookLevel(price, size);
            return;
        }
        if (size <= 0) return;
        int pos = 0;
        while (pos < list.Count && (side == Side.Buy ? list[pos].Price > price : list[pos].Price < price)) pos++;
        list.Insert(pos, new BookLevel(price, size));
    }

    /// <summary>Total size on a side within the first <paramref name="levels"/> levels.</summary>
    public double Depth(Side side, int levels = int.MaxValue)
    {
        var list = side == Side.Buy ? _bids : _asks;
        double s = 0;
        for (int i = 0; i < list.Count && i < levels; i++) s += list[i].Size;
        return s;
    }

    /// <summary>(bid depth - ask depth) / (bid depth + ask depth) over the first levels; in [-1, 1].</summary>
    public double Imbalance(int levels = 5)
    {
        double b = Depth(Side.Buy, levels), a = Depth(Side.Sell, levels);
        return a + b > 0 ? (b - a) / (a + b) : 0;
    }

    /// <summary>
    /// Volume-weighted price to fill <paramref name="quantity"/> by walking the opposite side.
    /// Returns NaN when the book is too thin.
    /// </summary>
    public double SweepPrice(Side side, double quantity)
    {
        var list = side == Side.Buy ? _asks : _bids;
        double remaining = quantity, cost = 0;
        foreach (var l in list)
        {
            double take = System.Math.Min(remaining, l.Size);
            cost += take * l.Price;
            remaining -= take;
            if (remaining <= 0) return cost / quantity;
        }
        return double.NaN;
    }
}
