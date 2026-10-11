# Finance.Net — Roadmap / Rencana Pengembangan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · Made by Gravicode Studios, led by Kang Fadhil

The specification of record is [solution-design.md](solution-design.md). Day-to-day status lives in
[Progress.md](Progress.md); this file is direction.

Spesifikasi acuan adalah [solution-design.md](solution-design.md). Status harian ada di
[Progress.md](Progress.md); file ini berisi arah pengembangan.

---

## v0.1 — Foundation release (current / saat ini)

| Area | Scope |
|---|---|
| Foundation | `Finance.Core` (bars, orders, positions, order book, calendars, day counts, RNG, synthetic markets), `Finance.Math` (SIMD statistics, matrices with GraviNum interop, distributions, root finding, optimisers, interpolation, regression) |
| Data | `Finance.DataFrame`, `Finance.TimeSeries` (indicator catalogue), `Finance.MarketData` (Yahoo, Alpha Vantage, Polygon, Binance, Coinbase, CSV, synthetic, streaming), `Finance.AlternativeData` |
| Quant | `Finance.Pricing` (options, trees, Monte Carlo, Heston, SABR, curves, bonds, swaps, swaptions, futures, CDS, structured), `Finance.Risk` (VaR/CVaR family, backtests, stress, Monte Carlo, performance metrics, GARCH) |
| Portfolio & trading | `Finance.Portfolio` (mean-variance, Black-Litterman, risk parity, HRP, CVaR, discrete allocation), `Finance.Backtesting` (event-driven, vectorised, walk-forward), `Finance.Execution` (OMS, FIX 4.4, paper/Binance/IBKR brokers, algos, SOR, TCA) |
| AI | `Finance.ML` (features, labelling, models, RL incl. Gravicode.RLNet, factor research, XAI), `Finance.LLM` (OpenAI/Azure/Anthropic/Ollama/Hugging Face, tool calling), `Finance.NewsAnalytics` |
| Acceleration | `Finance.RustBridge` + `native/finance-native` (Rust kernels, managed fallback), `Finance.GPU` (ILGPU CUDA/OpenCL/CPU) |
| Apps | Finance.Net Gallery (Avalonia, 11 modules), console quickstart, 15 .NET Interactive notebooks, benchmarks |
| Docs | Bilingual (EN/ID) README and module guides with screenshots |

## v0.2 — Depth / Kedalaman

- ✅ **Indicators**: 248-entry catalogue — Hilbert-transform family and Ehlers filters, 60
  candlestick patterns, rolling volume profile and market-profile TPO (done 2026-10-11).
- ✅ **Pricing**: Dupire local volatility with an MC pricer, Hull-White / G2++ (exact simulation,
  closed-form bonds/caps, Jamshidian), Bermudan swaptions via LSMC, zero-coupon and YoY inflation swaps,
  cross-currency swaps with basis, ISDA-style CDS with upfront and CS01 (done 2026-10-11). Open: IMM
  date schedules for CDS, YoY convexity, MtM-resetting cross-currency swaps.
- ✅ **Risk**: filtered historical simulation (1-day and bootstrapped multi-period), EVT
  (peaks-over-threshold) VaR/ES, liquidity-adjusted VaR, historical incremental/component VaR for
  what-if trades, FRTB-SA delta aggregation with GIRR parameters (done 2026-10-11). Open: FRTB
  vega/curvature/DRC.
- ✅ **Portfolio**: interior-point QP, cost-aware rebalancing, turnover / sector / factor-exposure
  constraints, Michaud resampled frontier (done 2026-10-12).
- **Backtesting**: tick-level / order-book replay engine, latency models, corporate actions and
  survivorship-bias-free universes.

## v0.3 — Connectivity / Konektivitas

- FIX session persistence (message store, gap fill on reconnect), FIX 5.0 SP2 / FIXT.
- Interactive Brokers TWS socket API (in addition to Client Portal Web API).
- More venues: Alpaca, OANDA, Bybit, OKX; IDX data vendors for the Indonesian market.
- Economic calendar and fundamentals from additional providers (FRED, SEC EDGAR XBRL).

## v0.4 — Financial AI / AI Keuangan

- ✅ Local FinBERT inference through HF.Net (Gravicode.HFNet.GraviTransformers) instead of the hosted
  Inference API.
- PPO/SAC portfolio agents on Gravicode.RLNet with GPU training (Gravicode.RLNet.Gpu).
- Retrieval-augmented filings Q&A (10-K/10-Q, IDX annual reports) with citation checking.
- Temporal fusion transformer / N-BEATS forecasters.

## v0.5 — Acceleration / Akselerasi

- DirectML and Vulkan compute backends (ILGPU has neither today; needs a separate kernel path).
- ROCm/HIP native backend (today AMD GPUs are reached through OpenCL).
- Rust kernels for the full indicator catalogue and for HRP clustering; SIMD (AVX-512) paths.
- ✅ Publish native binaries for linux-x64, osx-arm64 alongside win-x64 (since 0.1.0, built in CI).

## Always / Selalu

- Every number in the docs is measured, with the configuration stated.
- Tests check against textbook or independently computed reference values, not against the
  implementation's own output.
- Bilingual documentation stays in sync with the code.
