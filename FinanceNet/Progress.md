# Finance.Net — Progress / Kemajuan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Last updated / Terakhir diperbarui: **2026-10-10** · Version **0.1.0**

## Summary / Ringkasan

| Item | Status |
|---|---|
| Libraries (17 modules) | ✅ all build, 0 errors |
| Unit tests | ✅ **331 passed, 0 failed, 3 skipped** (skipped = live tests gated by env vars) |
| Rust crate `finance_native` | ✅ `cargo test` 15/15, loaded by Finance.RustBridge with managed fallback |
| Finance.Net Gallery (Avalonia) | ✅ 11 modules + overview, screenshots in `docs/screenshots` |
| Quickstart console sample | ✅ nine-section tour + HTML tearsheet |
| 15 .NET Interactive notebooks | ✅ all execute under tools/NotebookCheck (offline and --live) |
| Bilingual docs (EN/ID) | ✅ 22 pages per language in docs/en, docs/id |
| NuGet packaging | ✅ metadata, icon, README, symbols; **not yet published** |

## Tests per project / Tes per proyek

| Test project | Modules | Result |
|---|---|---|
| Finance.Foundation.Tests | Core, Math | 50 / 50 |
| Finance.Analytics.Tests | DataFrame, TimeSeries, Visualization | 51 / 51 |
| Finance.Quant.Tests | Pricing, Risk | 56 / 56 |
| Finance.Portfolio.Tests | Portfolio, Backtesting | 50 / 50 |
| Finance.Data.Tests | MarketData, AlternativeData, Execution | 53 / 53 (+1 live, skipped) |
| Finance.AI.Tests | ML, LLM, NewsAnalytics | 37 / 37 (+2 live, skipped) |
| Finance.Native.Tests | GPU, RustBridge | 34 / 34 |

Live checks run once by hand: Yahoo chart API ✅, Binance klines/depth/ticker ✅ (via
`data-api.binance.vision`), Azure OpenAI chat + tool calling + JSON ✅, Hugging Face FinBERT ❌ (HTTP 402 —
the account has no Inference credits).

## Modules / Modul

| Module | Done | Notes / Catatan |
|---|---|---|
| Finance.Core | ✅ | Bars, quotes, L2 book, orders/positions, day counts, tenors, NYSE/IDX calendars, xoshiro RNG, synthetic markets |
| Finance.Math | ✅ | SIMD stats, Ledoit-Wolf, Matrix (+GraviNum eigen), Normal/Student-t, Brent/Newton, projected gradient, Nelder-Mead, splines, OLS/ridge |
| Finance.DataFrame | ✅ | OhlcvFrame, CSV, resample, PricePanel, GraviFrame interop. Panel alignment is exact-timestamp inner join only |
| Finance.TimeSeries | ✅ | Returns, rolling O(n), streaming indicators; **catalogue 144 entries (111 + 33 patterns) — target 200+ open** |
| Finance.MarketData | ✅ | Yahoo, Alpha Vantage, Polygon, Binance, Coinbase, CSV, synthetic, cache, rate limiter, WebSocket, economic, fundamentals. Coinbase not verified live (TLS failure from this network) |
| Finance.AlternativeData | ✅ | Social (Reddit), ESG, supply chain, weather. Reddit unauthenticated access is usually blocked |
| Finance.Pricing | ✅ | BSM+Greeks, IV, Black-76, Bachelier, trees, MC, Heston, SABR, surfaces, curves, NSS, bonds, FRN, IRS, swaptions, forwards, CDS, structured, autocallable |
| Finance.Risk | ✅ | VaR/ES (5 methods), attribution, Kupiec/Christoffersen, stress (stylised scenarios), MC simulation, performance report, GARCH, EWMA |
| Finance.Portfolio | ✅ | MVO, Black-Litterman, risk parity, HRP, CVaR, heuristics, discrete allocation, rebalancing. CVaR untested above ~30 assets; bounded MVO is slow-ish |
| Finance.Backtesting | ✅ | Event-driven engine, costs, vectorised, sweeps, walk-forward, portfolio backtester, 5 strategies. Walk-forward OOS windows start flat |
| Finance.Execution | ✅ | OMS, paper broker, FIX 4.4 + session, Binance & IBKR adapters, TWAP/VWAP/POV/Iceberg, SOR, pre-trade risk, TCA. Binance WS and IBKR not tested live |
| Finance.ML | ✅ | Features, labelling, frac-diff, purged CV, linear/trees/forest/GBM, AR/ARIMA/Holt-Winters, Q-learning + **Gravicode.RLNet DQN/PPO**, factors, Alphalens-style research, SHAP, PDP/ICE |
| Finance.LLM | ✅ | OpenAI/Azure/Anthropic/Ollama/HF/DeepSeek, SSE streaming, tool calling, FinancialAssistant, FinGPT-style tasks, FinBERT (hosted) |
| Finance.NewsAnalytics | ✅ | RSS/Atom, lexicon sentiment, entities, events, MinHash, earnings calls, per-ticker intelligence |
| Finance.Visualization | ✅ | SVG charts (dark/light), HTML tearsheets |
| Finance.GPU | ✅ | ILGPU CUDA/OpenCL/CPU: MC European/Asian, batch BSM, MC VaR. DirectML/Vulkan unavailable (ILGPU limitation) |
| Finance.RustBridge | ✅ | Rust kernels with identical managed fallback; MSBuild cargo integration; packs `runtimes/win-x64/native` |

## Gallery

Overview, Live Market Dashboard, Order Book Viewer, Indicator Explorer, Portfolio Optimizer, VaR Explorer,
Greeks Explorer, Strategy Builder, Backtesting Studio, AI Financial Assistant, News Intelligence, Social
Sentiment — all verified by headless screenshot capture with live Binance, RSS and Azure OpenAI data.

## Benchmarks (`--quick`, measured)

See [docs/en/benchmarks.md](docs/en/benchmarks.md). Rust clearly wins for historical VaR (2.6×) and
multi-threaded Monte Carlo (1.3×); simple indicators and Black-Scholes are on par with managed code. The
integrated GPU wins for Monte Carlo pricing (2.8×) and Monte Carlo VaR (2.4×).

## Open items / Pekerjaan terbuka

- [ ] Publish `Gravicode.Finance.*` 0.1.0 to nuget.org (awaiting owner confirmation)
- [ ] Indicator catalogue to 200+
- [ ] Local FinBERT through HF.Net (no hosted credits needed)
- [ ] Linux/macOS native binaries for the Rust crate
- [ ] Run the full BenchmarkDotNet suites (only `--quick` measured so far)
- See [PLAN.md](PLAN.md) for v0.2+.
