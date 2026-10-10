namespace Finance.Core.Market;

/// <summary>
/// Reproducible synthetic market data, so samples, tests and the Gallery run offline.
/// </summary>
public static class SyntheticMarket
{
    /// <summary>
    /// Daily OHLCV bars following geometric Brownian motion, skipping weekends.
    /// </summary>
    /// <param name="count">Number of bars.</param>
    /// <param name="startPrice">First open.</param>
    /// <param name="annualDrift">Annual drift (mu).</param>
    /// <param name="annualVolatility">Annual volatility (sigma).</param>
    /// <param name="seed">RNG seed.</param>
    /// <param name="start">First bar date (defaults to 2020-01-01).</param>
    public static Bar[] GeometricBrownianBars(int count, double startPrice = 100, double annualDrift = 0.08,
        double annualVolatility = 0.25, ulong seed = 42, DateTime? start = null)
    {
        Guard.Positive(count);
        Guard.Positive(startPrice);
        var rng = new FastRandom(seed);
        var bars = new Bar[count];
        double dt = 1.0 / 252, price = startPrice;
        double drift = (annualDrift - 0.5 * annualVolatility * annualVolatility) * dt;
        double vol = annualVolatility * System.Math.Sqrt(dt);
        var date = start ?? new DateTime(2020, 1, 1);
        for (int i = 0; i < count; i++)
        {
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
            double open = price;
            double close = open * System.Math.Exp(drift + vol * rng.NextGaussian());
            double intraday = vol * 0.6;
            double high = System.Math.Max(open, close) * System.Math.Exp(System.Math.Abs(rng.NextGaussian()) * intraday);
            double low = System.Math.Min(open, close) * System.Math.Exp(-System.Math.Abs(rng.NextGaussian()) * intraday);
            double volume = System.Math.Round(1_000_000 * System.Math.Exp(0.3 * rng.NextGaussian()) * (1 + 20 * System.Math.Abs(close / open - 1)));
            bars[i] = new Bar(date, open, high, low, close, volume);
            price = close;
            date = date.AddDays(1);
        }
        return bars;
    }

    /// <summary>
    /// Correlated daily close prices for several assets (one row per day, one column per asset),
    /// generated through a Cholesky factor of the correlation matrix.
    /// </summary>
    public static double[][] CorrelatedPrices(int days, double[] drifts, double[] vols, double[,] correlation, ulong seed = 7, double startPrice = 100)
    {
        int n = drifts.Length;
        Guard.SameLength(n, vols.Length, "drifts and vols");
        var l = CholeskyLower(correlation, n);
        var rng = new FastRandom(seed);
        var prices = new double[days][];
        var current = Enumerable.Repeat(startPrice, n).ToArray();
        var z = new double[n];
        var e = new double[n];
        double dt = 1.0 / 252;
        for (int t = 0; t < days; t++)
        {
            for (int i = 0; i < n; i++) z[i] = rng.NextGaussian();
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int k = 0; k <= i; k++) s += l[i, k] * z[k];
                e[i] = s;
            }
            var row = new double[n];
            for (int i = 0; i < n; i++)
            {
                current[i] *= System.Math.Exp((drifts[i] - 0.5 * vols[i] * vols[i]) * dt + vols[i] * System.Math.Sqrt(dt) * e[i]);
                row[i] = current[i];
            }
            prices[t] = row;
        }
        return prices;
    }

    /// <summary>A symmetric limit order book around <paramref name="mid"/>.</summary>
    public static OrderBook OrderBook(string symbol, double mid, int levels = 15, double tick = 0.01, ulong seed = 3)
    {
        var rng = new FastRandom(seed);
        var book = new OrderBook(symbol, DateTime.UtcNow);
        var bids = new List<BookLevel>(levels);
        var asks = new List<BookLevel>(levels);
        for (int i = 0; i < levels; i++)
        {
            double depthBias = 1 + i * 0.35;
            bids.Add(new BookLevel(System.Math.Round(mid - tick * (i + 1), 6), System.Math.Round(100 * depthBias * (0.5 + rng.NextDouble()))));
            asks.Add(new BookLevel(System.Math.Round(mid + tick * (i + 1), 6), System.Math.Round(100 * depthBias * (0.5 + rng.NextDouble()))));
        }
        book.SetBids(bids);
        book.SetAsks(asks);
        return book;
    }

    private static double[,] CholeskyLower(double[,] a, int n)
    {
        var l = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = a[i, j];
                for (int k = 0; k < j; k++) s -= l[i, k] * l[j, k];
                if (i == j)
                {
                    if (s <= 0) throw new ArgumentException("Correlation matrix is not positive definite.");
                    l[i, i] = System.Math.Sqrt(s);
                }
                else l[i, j] = s / l[j, j];
            }
        }
        return l;
    }
}
