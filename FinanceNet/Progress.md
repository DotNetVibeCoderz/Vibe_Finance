# Finance.Net — Progress / Kemajuan

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil

Last updated / Terakhir diperbarui: **2026-10-11** · Version **0.2.0**

## Summary / Ringkasan

| Item | Status |
|---|---|
| Libraries (18 packages) | ✅ all build, 0 warnings, 0 errors (`Finance.LLM.Local` added after 0.1.0) |
| Unit tests | ✅ **383 passed, 0 failed, 4 skipped** (skipped = live tests gated by env vars) |
| Rust crate `finance_native` | ✅ `cargo test` 15/15, loaded by Finance.RustBridge with managed fallback |
| Finance.Net Gallery (Avalonia) | ✅ 11 modules + overview, screenshots in `docs/screenshots` |
| Quickstart console sample | ✅ nine-section tour + HTML tearsheet |
| 15 .NET Interactive notebooks | ✅ all execute under tools/NotebookCheck (offline and --live) |
| Bilingual docs (EN/ID) | ✅ 22 pages per language in docs/en, docs/id |
| NuGet packaging | ✅ `Gravicode.Finance.*` 0.1.0 published to nuget.org (tag `financenet-v0.1.0`, CI run 38068541863) |

## Tests per project / Tes per proyek

| Test project | Modules | Result |
|---|---|---|
| Finance.Foundation.Tests | Core, Math | 50 / 50 |
| Finance.Analytics.Tests | DataFrame, TimeSeries, Visualization | 88 / 88 |
| Finance.Quant.Tests | Pricing, Risk | 70 / 70 |
| Finance.Portfolio.Tests | Portfolio, Backtesting | 50 / 50 |
| Finance.Data.Tests | MarketData, AlternativeData, Execution | 53 / 53 (+1 live, skipped) |
| Finance.AI.Tests | ML, LLM, LLM.Local, NewsAnalytics | 38 / 38 (+3 live, skipped) |
| Finance.Native.Tests | GPU, RustBridge | 34 / 34 |

Live checks run once by hand: Yahoo chart API ✅, Binance klines/depth/ticker ✅ (via
`data-api.binance.vision`), Azure OpenAI chat + tool calling + JSON ✅, hosted Hugging Face FinBERT ❌ (HTTP 402 —
the account has no Inference credits), **local FinBERT through HF.Net ✅** (`FINANCENET_FINBERT_LOCAL=1`;
PhraseBank-style positive/negative/neutral sentences classified correctly, ~100 ms per sentence).

## Modules / Modul

| Module | Done | Notes / Catatan |
|---|---|---|
| Finance.Core | ✅ | Bars, quotes, L2 book, orders/positions, day counts, tenors, NYSE/IDX calendars, xoshiro RNG, synthetic markets |
| Finance.Math | ✅ | SIMD stats, Ledoit-Wolf, Matrix (+GraviNum eigen), Normal/Student-t, Brent/Newton, projected gradient, Nelder-Mead, splines, OLS/ridge |
| Finance.DataFrame | ✅ | OhlcvFrame, CSV, resample, PricePanel, GraviFrame interop. Panel alignment is exact-timestamp inner join only |
| Finance.TimeSeries | ✅ | Returns, rolling O(n), streaming indicators; **catalogue 248 entries (188 indicators + 60 patterns)**, incl. Hilbert/Ehlers cycles, rolling volume profile and TPO |
| Finance.MarketData | ✅ | Yahoo, Alpha Vantage, Polygon, Binance, Coinbase, CSV, synthetic, cache, rate limiter, WebSocket, economic, fundamentals. Coinbase not verified live (TLS failure from this network) |
| Finance.AlternativeData | ✅ | Social (Reddit), ESG, supply chain, weather. Reddit unauthenticated access is usually blocked |
| Finance.Pricing | ✅ | BSM+Greeks, IV, Black-76, Bachelier, trees, MC, Heston, SABR, surfaces, curves, NSS, bonds, FRN, IRS, swaptions, forwards, CDS, structured, autocallable |
| Finance.Risk | ✅ | VaR/ES (7 methods incl. filtered HS and EVT/GPD), liquidity-adjusted and historical incremental/component VaR, FRTB-SA delta (GIRR), attribution, Kupiec/Christoffersen, stress (stylised scenarios), MC simulation, performance report, GARCH, EWMA |
| Finance.Portfolio | ✅ | MVO, Black-Litterman, risk parity, HRP, CVaR, heuristics, discrete allocation, rebalancing. CVaR untested above ~30 assets; bounded MVO is slow-ish |
| Finance.Backtesting | ✅ | Event-driven engine, costs, vectorised, sweeps, walk-forward, portfolio backtester, 5 strategies. Walk-forward OOS windows start flat |
| Finance.Execution | ✅ | OMS, paper broker, FIX 4.4 + session, Binance & IBKR adapters, TWAP/VWAP/POV/Iceberg, SOR, pre-trade risk, TCA. Binance WS and IBKR not tested live |
| Finance.ML | ✅ | Features, labelling, frac-diff, purged CV, linear/trees/forest/GBM, AR/ARIMA/Holt-Winters, Q-learning + **Gravicode.RLNet DQN/PPO**, factors, Alphalens-style research, SHAP, PDP/ICE |
| Finance.LLM | ✅ | OpenAI/Azure/Anthropic/Ollama/HF/DeepSeek, SSE streaming, tool calling, FinancialAssistant, FinGPT-style tasks, FinBERT (hosted) |
| Finance.LLM.Local | ✅ | FinBERT in-process via HF.Net 0.9.0 (0.8.1 gained legacy `pytorch_model.bin` support for ProsusAI/finbert). Separate package so Finance.LLM stays dependency-free |
| Finance.NewsAnalytics | ✅ | RSS/Atom, lexicon sentiment, entities, events, MinHash, earnings calls, per-ticker intelligence |
| Finance.Visualization | ✅ | SVG charts (dark/light), HTML tearsheets |
| Finance.GPU | ✅ | ILGPU CUDA/OpenCL/CPU: MC European/Asian, batch BSM, MC VaR. DirectML/Vulkan unavailable (ILGPU limitation) |
| Finance.RustBridge | ✅ | Rust kernels with identical managed fallback; MSBuild cargo integration; packs `runtimes/{win-x64,linux-x64,osx-arm64}/native` (built in CI) |

## Gallery

Overview, Live Market Dashboard, Order Book Viewer, Indicator Explorer, Portfolio Optimizer, VaR Explorer,
Greeks Explorer, Strategy Builder, Backtesting Studio, AI Financial Assistant, News Intelligence, Social
Sentiment — all verified by headless screenshot capture with live Binance, RSS and Azure OpenAI data.

## Benchmarks (measured: `--quick` and full BenchmarkDotNet suites)

See [docs/en/benchmarks.md](docs/en/benchmarks.md). Rust clearly wins for historical VaR (2.4-2.6×);
simple indicators and Black-Scholes are on par with managed code; multi-threaded Monte Carlo is
1.3× in `--quick` but tied in BenchmarkDotNet (throttling noise). The integrated GPU wins for Monte
Carlo pricing (2.2-2.8×) and Monte Carlo VaR (2.4×).

## Open items / Pekerjaan terbuka

- [x] Publish `Gravicode.Finance.*` 0.1.0 to nuget.org (2026-10-10)
- [x] Indicator catalogue to 200+ — 248 entries (2026-10-11)
- [x] Local FinBERT through HF.Net (2026-10-11) — `Gravicode.Finance.LLM.Local`, first published in 0.2.0
- [x] Linux/macOS native binaries for the Rust crate — CI matrix since 0.1.0
- [x] CI actions on current majors (checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8)
- [x] HF.Net 0.8.1 released (2026-10-11) with legacy `pytorch_model.bin` support; FinBERT loads `main`
- [x] ImageSharp off the dependency graph: HF.Net 0.9.0 moved ViT/CLIP to `GraviTransformers.Vision`; Finance.Net
  needs no audit suppression any more
- [x] Run the full BenchmarkDotNet suites (2026-10-11, results in docs/en/benchmarks.md)
- See [PLAN.md](PLAN.md) for v0.2+.
