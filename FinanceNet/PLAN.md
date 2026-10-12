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
  cross-currency swaps with basis, ISDA-style CDS with upfront and CS01 (done 2026-10-11); IMM/semi-annual-roll CDS
  schedules, YoY convexity (Mercurio) and MtM-resetting cross-currency swaps (done 2026-10-13, 0.10.0).
- ✅ **Risk**: filtered historical simulation (1-day and bootstrapped multi-period), EVT
  (peaks-over-threshold) VaR/ES, liquidity-adjusted VaR, historical incremental/component VaR for
  what-if trades, FRTB-SA delta aggregation with GIRR parameters (done 2026-10-11); FRTB-SA GIRR vega and
  curvature, correlation scenarios and non-securitisation DRC (done 2026-10-13, 0.10.0). Open: built-in vega/curvature
  parameters for risk classes other than GIRR, securitisation DRC, residual risk add-on.
- ✅ **Portfolio**: interior-point QP, cost-aware rebalancing, turnover / sector / factor-exposure
  constraints, Michaud resampled frontier (done 2026-10-12).
- ✅ **Backtesting**: order-book replay with latency models and FIFO queue position, corporate actions
  (splits, dividends, adjuster), delistings and point-in-time universes (done 2026-10-12).

v0.2 is complete.

## v0.3 — Connectivity / Konektivitas

- ✅ FIX session persistence (file message store, recovery across restarts), FIX 5.0 SP2 over FIXT.1.1.
- ✅ Interactive Brokers TWS socket API (protocol, handshake, account monitoring; orders stay on the Web API).
- ✅ More venues: Alpaca, OANDA, Bybit, OKX (brokers and data); IDX via vendor plus exchange rules.
- ✅ Economic calendar and fundamentals: FRED (series, indicators, release calendar), SEC EDGAR XBRL.

v0.3 is complete (2026-10-12). TWS order placement (`TwsBroker`, server version 176) and Bybit/OKX conditional and
TP/SL orders followed in 0.10.0 (2026-10-13), tested against fakes and the documented payloads. Open: live
verification of the key-gated venues and of TWS orders against a paper account.

## v0.4 — Financial AI / AI Keuangan

- ✅ Local FinBERT inference through HF.Net (Gravicode.HFNet.GraviTransformers) instead of the hosted
  Inference API.
- ✅ PPO/SAC/TD3 portfolio agents on Gravicode.RLNet with GPU training (Gravicode.RLNet.Gpu):
  `PortfolioRlAgent`, continuous (softmax weights) and template environments, costs on turnover.
- ✅ Retrieval-augmented filings Q&A (10-K/10-Q from EDGAR, PDF annual reports incl. IDX) with citation checking:
  `FilingIndex` (BM25 + local embeddings, rank fusion), `CitationChecker`, `FilingsQa`; verified live on Apple's 10-K.
- ✅ Temporal fusion transformer / N-BEATS forecasters on GraviNum autodiff (quantiles, attention, variable
  selection; interpretable trend/seasonality stacks).

v0.4 is complete (2026-10-12). TFT static covariates and multi-series training, and OCR for scanned PDFs
(`Gravicode.Finance.LLM.Ocr`, TrOCR from HF.Net 0.10) followed in 0.10.0 (2026-10-13). Open: TFT forecasts for
series unseen in training; OCR skew correction, tables and two-column reading order.

## v0.5 — Acceleration / Akselerasi

- ✅ Vulkan compute backend: `finance_gpu` Rust crate (wgpu 24, WGSL with f64/i64), verified on Intel UHD 620.
- ✅ DirectML backend as Direct3D 12 compute (HLSL → DXIL SM 6.0, precompiled; any FP64 DX12 GPU or WARP), verified
  on Intel UHD 620 and WARP. DirectML's own operators are FP16/FP32 tensor ops, so the FP64 kernels use the D3D12 layer.
- ✅ ROCm/HIP native backend (hiprtc at run time); kernel source checked on the host, not yet run on AMD hardware.
- ✅ Rust kernels for the full indicator catalogue (248 entries, parity-tested) and for HRP clustering (identical
  dendrogram and weights); runtime-dispatched SIMD (baseline / AVX2 / AVX-512F, bit-identical results).
- ✅ Publish native binaries for linux-x64, osx-arm64 alongside win-x64 (since 0.1.0, built in CI; finance_gpu since 0.9.0).

v0.5 is complete (2026-10-12). In 0.10.0 (2026-10-13): persistent (grow-only) GPU buffers in every engine,
vectorised batch Black-Scholes from AVX2 up, and CI reports which SIMD level each runner executed. Open: run the HIP
backend on an AMD GPU; execute the AVX-512 path on a CPU that has it.

## Always / Selalu

- Every number in the docs is measured, with the configuration stated.
- Tests check against textbook or independently computed reference values, not against the
  implementation's own output.
- Bilingual documentation stays in sync with the code.
