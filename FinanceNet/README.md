<p align="center">
  <img src="assets/icon-512.png" width="128" alt="Finance.Net logo" />
</p>

<h1 align="center">Finance.Net</h1>

<p align="center">
  <b>The quantitative finance, trading, risk, portfolio, market-intelligence and financial-AI platform for .NET 10</b><br/>
  Native C# with Rust accelerators and ILGPU — QuantLib, PyPortfolioOpt, pyfolio, Alphalens, Zipline/Backtrader, vectorbt, FinRL, OpenBB and FinGPT capabilities in one coherent library.
</p>

<p align="center">
  <i>Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil</i><br/>
  <a href="README.id.md">🇮🇩 Baca dalam Bahasa Indonesia</a> · <a href="docs/en/index.md">Documentation</a> · <a href="docs/id/index.md">Dokumentasi</a>
</p>

<p align="center">
  <img src="docs/screenshots/00-overview.png" alt="Finance.Net Gallery overview" width="900" />
</p>

---

## Why Finance.Net

- **One stack, one type system.** Bars, orders, order books, curves, portfolios and backtests share the
  same `Finance.Core` types, so a market-data feed plugs straight into an indicator, a risk model, an
  optimiser and a broker.
- **Fast and frugal.** Span-based APIs, SIMD statistics, O(n) rolling windows, parallel Monte Carlo with
  reproducible per-thread streams, optional Rust kernels and GPU pricing — with a managed fallback for
  everything.
- **Correct before clever.** Every module is tested against textbook reference values (Hull, Haug,
  QuantLib examples, scipy) or independent methods, not against its own output.
- **Built on the Gravicode foundation**: [Gravicode.Science](https://www.nuget.org/packages/Gravicode.Science.GraviNum)
  (GraviNum linear algebra, GraviFrame data frames), HF.Net and Visual.Net, plus
  [Gravicode.RLNet](https://www.nuget.org/packages/Gravicode.RLNet) for deep reinforcement learning.

## Modules

| Package | What you get |
|---|---|
| `Gravicode.Finance.Core` | Bars, ticks, quotes, L2 order book, orders/fills/positions, instruments, money, day counts, tenors, business calendars (NYSE, IDX), reproducible RNG, synthetic markets |
| `Gravicode.Finance.Math` | SIMD statistics, covariance & Ledoit-Wolf, dense matrices (Cholesky, LU, eigen via GraviNum), normal / Student-t, Brent & Newton, projected-gradient / Nelder-Mead, splines, OLS / ridge |
| `Gravicode.Finance.DataFrame` | Columnar OHLCV frames, CSV, resampling, aligned multi-asset price panels, GraviFrame interop |
| `Gravicode.Finance.TimeSeries` | Returns, drawdowns, O(n) rolling statistics, **248-entry indicator catalogue** (188 indicators incl. Ehlers/Hilbert cycles and volume profile + 60 candlestick patterns), streaming indicators |
| `Gravicode.Finance.MarketData` | Yahoo Finance, Alpha Vantage, Polygon, Binance, Coinbase, Alpaca, OANDA, Bybit, OKX, IDX, FRED, SEC EDGAR XBRL, CSV, synthetic; caching, rate limiting, WebSocket streaming, economic calendar, fundamentals |
| `Gravicode.Finance.AlternativeData` | Social media (Reddit) sentiment & buzz, ESG analytics (WACI), supply-chain graphs (PageRank, shock contagion), weather (Open-Meteo, HDD/CDD) |
| `Gravicode.Finance.Pricing` | Black-Scholes-Merton + full Greeks, implied vol, Black-76, Bachelier, CRR/trinomial trees, Monte Carlo (Asian, barrier, lookback), Heston, SABR, vol surfaces, curve bootstrapping, Nelson-Siegel-Svensson, bonds, FRNs, swaps, swaptions, forwards, CDS, structured notes, autocallables |
| `Gravicode.Finance.Risk` | Historical / parametric / Cornish-Fisher / Monte Carlo VaR & ES, component VaR, Kupiec & Christoffersen backtests, stress testing, Monte Carlo portfolio simulation, pyfolio-style metrics, GARCH(1,1), EWMA |
| `Gravicode.Finance.Portfolio` | Mean-variance (max Sharpe, min vol, efficient return/risk), Black-Litterman, risk parity / budgeting, HRP, CVaR optimisation, max diversification, discrete allocation, rebalancing |
| `Gravicode.Finance.Backtesting` | Event-driven multi-asset engine, commission & slippage models, vectorised backtester, walk-forward optimisation, parameter sweeps, portfolio backtests, built-in strategies |
| `Gravicode.Finance.Execution` | OMS, paper broker, FIX 4.4 / 5.0 SP2 engine with persistent sessions, Binance, Alpaca, OANDA, Bybit, OKX & Interactive Brokers (Web API + TWS socket) adapters, TWAP / VWAP / POV / Iceberg, smart order routing, pre-trade risk, TCA |
| `Gravicode.Finance.ML` | Feature engineering, triple-barrier labelling, fractional differentiation, purged CV, linear/logistic/trees/forests/boosting, AR/ARIMA/Holt-Winters, Q-learning + **RLNet DQN/PPO** trading agents, factor models, Alphalens-style research, Kernel SHAP, PDP/ICE |
| `Gravicode.Finance.LLM` | OpenAI, Azure OpenAI, Anthropic, Ollama, Hugging Face clients; streaming; tool calling; `FinancialAssistant` agent loop; FinGPT-style tasks; FinBERT |
| `Gravicode.Finance.LLM.Local` | **Local FinBERT** run in-process through HF.Net — no hosted API or credits (separate so `Finance.LLM` stays dependency-free) |
| `Gravicode.Finance.LLM.Ocr` | **OCR for scanned reports** — line detection + TrOCR (HF.Net) and a PDF loader for filings Q&A (separate: it carries ImageSharp) |
| `Gravicode.Finance.NewsAnalytics` | RSS/Atom, finance sentiment lexicon, ticker/entity extraction, event classification, MinHash de-duplication, earnings-call analysis |
| `Gravicode.Finance.Visualization` | Dependency-free SVG charts (candles, lines, frontier, heatmaps, histograms) and self-contained HTML tearsheets |
| `Gravicode.Finance.GPU` | GPU Monte Carlo pricing, batch Black-Scholes, Monte Carlo VaR on CUDA / OpenCL / CPU (ILGPU), ROCm (HIP), Vulkan and Direct3D 12 |
| `Gravicode.Finance.RustBridge` | Rust `finance_native` kernels (all 248 catalogue indicators, HRP, VaR, covariance with AVX2/AVX-512, Black-Scholes, Monte Carlo) with an identical managed fallback |

## Quick start

```bash
dotnet add package Gravicode.Finance.Pricing
dotnet add package Gravicode.Finance.Risk
dotnet add package Gravicode.Finance.Portfolio
dotnet add package Gravicode.Finance.Backtesting
dotnet add package Gravicode.Finance.TimeSeries
```

```csharp
using Finance.Core.Market;
using Finance.Pricing.Options;
using Finance.Risk;
using Finance.TimeSeries.Indicators;
using Finance.Backtesting;
using Finance.Backtesting.Strategies;

// Offline, reproducible data (swap in YahooFinanceProvider / BinanceProvider for live data).
Bar[] bars = SyntheticMarket.GeometricBrownianBars(1000, startPrice: 100, annualDrift: 0.08, annualVolatility: 0.25);
double[] close = bars.Select(b => b.Close).ToArray();

// Indicators
double[] rsi = Ta.Rsi(close, 14);
var bands = Ta.Bollinger(close, 20, 2);

// Options
var g = BlackScholes.Greeks(OptionType.Call, spot: 100, strike: 105, time: 0.5, rate: 0.04, dividendYield: 0.01, volatility: 0.25);
Console.WriteLine($"Call {g.Price:F4}  Δ {g.Delta:F4}  Γ {g.Gamma:F5}  vega/pt {g.VegaPerPoint:F4}");

// Risk
double[] returns = close.Zip(close.Skip(1), (a, b) => b / a - 1).ToArray();
var var99 = ValueAtRisk.Historical(returns, 0.99);
Console.WriteLine($"1-day VaR 99%: {var99.ValueAtRisk:P2}, ES: {var99.ExpectedShortfall:P2}");

// Backtest
var result = new BacktestEngine().Run(new SmaCrossoverStrategy(20, 50, "DEMO"), "DEMO", bars);
Console.WriteLine(result.Statistics);
```

More: [docs/en/getting-started.md](docs/en/getting-started.md), the console tour in
[`samples/Finance.Quickstart`](samples/Finance.Quickstart) and 15 [notebooks](notebooks).

## Finance.Net Gallery

An Avalonia desktop app with eleven live modules (`dotnet run --project samples/Finance.Gallery`).

| | |
|---|---|
| ![Live market](docs/screenshots/01-live-market-dashboard.png) **Live Market Dashboard** — Binance watchlist, 1-minute candles, EMA/RSI | ![Order book](docs/screenshots/02-order-book-viewer.png) **Order Book Viewer** — L2 ladder, depth, imbalance, sweep cost |
| ![Indicators](docs/screenshots/03-indicator-explorer.png) **Indicator Explorer** — the whole 248-entry catalogue, live parameters | ![Portfolio](docs/screenshots/04-portfolio-optimizer.png) **Portfolio Optimizer** — 10 methods, frontier, risk contributions |
| ![VaR](docs/screenshots/05-var-explorer.png) **VaR Explorer** — five VaR methods, backtests, stress, Monte Carlo fan | ![Greeks](docs/screenshots/06-greeks-explorer.png) **Greeks Explorer** — BSM vs trees vs Monte Carlo |
| ![Strategy builder](docs/screenshots/07-strategy-builder.png) **Strategy Builder** — no-code rules → backtest → generated C# | ![Backtesting](docs/screenshots/08-backtesting-studio.png) **Backtesting Studio** — built-in strategies, costs, walk-forward |
| ![AI assistant](docs/screenshots/09-ai-financial-assistant.png) **AI Financial Assistant** — tool-calling LLM over Finance.Net functions | ![News](docs/screenshots/10-news-intelligence.png) **News Intelligence** — live RSS, sentiment, events |
| ![Social](docs/screenshots/11-social-sentiment.png) **Social Sentiment** — mentions, engagement-weighted sentiment, buzz | |

Screenshots are regenerated headlessly with
`dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots`.

## Performance

Measured with `dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick` on a 4-core 15 W
laptop (Intel UHD 620 over OpenCL for the GPU column), best of 7 alternating runs:

| Case | Managed | Rust | GPU |
|---|---|---|---|
| RSI(14), 1M prices | 11.3 ms | 8.9 ms | – |
| Black-Scholes + Greeks, 1M options | 100.6 ms | 103.1 ms | 85.0 ms |
| MC European, 4M paths | 54.3 ms | 41.7 ms | 19.6 ms |
| Historical VaR 99%, 1M returns | 14.1 ms | 5.4 ms | – |
| MC portfolio VaR, 10 assets × 200k | 131 ms | – | 54.5 ms |

Full table and caveats: [docs/en/benchmarks.md](docs/en/benchmarks.md).

## Building from source

```bash
dotnet build FinanceNet.slnx                 # also runs `cargo build --release` for the Rust kernels if cargo is installed
dotnet build FinanceNet.slnx -p:SkipRustBuild=true   # managed-only build
dotnet test FinanceNet.slnx
dotnet pack -c Release                       # → artifacts/packages
```

Requirements: .NET 10 SDK; optional Rust 1.80+ for native kernels; optional CUDA/OpenCL driver for GPU.

## Honest limitations

- The native ROCm/HIP backend has not been run on AMD hardware (none available during development); its kernel
  source was checked on the host. The `DirectML` backend runs Direct3D 12 compute shaders, not DirectML operators.
- Historical stress scenarios use stylised, rounded shocks for illustration, not regulatory use.
- Hosted FinBERT needs Hugging Face Inference credits; `Gravicode.Finance.LLM.Local` runs FinBERT in-process instead.
- Reddit's unauthenticated API is often rate-limited; the Gallery falls back to labelled synthetic posts.

## Project files

- [PLAN.md](PLAN.md) — roadmap · [Progress.md](Progress.md) — development progress
- [solution-design.md](solution-design.md) — design specification (Bahasa Indonesia)

## License

MIT © Gravicode Studios — *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
