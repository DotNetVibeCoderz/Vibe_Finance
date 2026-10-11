# Notebooks

[← Index](index.md) · [Bahasa Indonesia](../id/notebooks.md)

Fifteen **Polyglot Notebooks** (`.dib`, .NET Interactive) in `notebooks/` walk through the library
with runnable C# cells.

## Running them

1. Install **VS Code** with the **Polyglot Notebooks** extension (it brings the .NET Interactive
   kernel).
2. Build the packages once: `dotnet pack -c Release` (writes `artifacts/packages`).
3. Open a `.dib` file and *Run All*. The first cell adds `../artifacts/packages` as a package source
   and references the `Gravicode.Finance.*` packages it needs; once the packages are on nuget.org the
   same cell restores from there.

Every notebook runs offline on synthetic data; cells that use the network say so and fall back.
`dotnet run --project tools/NotebookCheck` compiles and runs every C# cell of every notebook, which
is how they are kept working.

## The notebooks

| # | Notebook | What you learn |
|---|---|---|
| 01 | `01_Market_Data.dib` | Bars and OHLCV frames, CSV round trip, multi-asset panels, providers, optional live data |
| 02 | `02_Indicators.dib` | The 248-entry catalogue, trend/momentum/volatility indicators, candlestick patterns, streaming indicators, Rust acceleration |
| 03 | `03_Portfolio.dib` | Return and risk estimators, mean-variance and the frontier, Black-Litterman, risk-based portfolios, CVaR, discrete allocation |
| 04 | `04_Risk.dib` | Fat-tailed returns, VaR and ES, VaR backtesting, volatility models, portfolio risk attribution, performance report |
| 05 | `05_Options.dib` | Black-Scholes-Merton and Greeks, early exercise, Monte Carlo exotics, stochastic volatility and smiles, GPU Monte Carlo |
| 06 | `06_Backtesting.dib` | Event-driven strategies, trades on the chart, parameter sweep and walk-forward, vectorised research, HRP portfolio backtest |
| 07 | `07_Machine_Learning.dib` | Features and labels, random forest with purged CV, SHAP explanations, reinforcement learning with RLNet, forecasting |
| 08 | `08_NLP.dib` | Lexicon sentiment, near-duplicate detection, parsing RSS, earnings-call analysis, optional LLM and FinBERT |
| 09 | `09_Sentiment.dib` | A synthetic news flow with a hidden signal, a news sentiment index, social media and buzz |
| 10 | `10_Alternative_Data.dib` | ESG analytics, supply-chain graphs, weather analytics, optional real weather data |
| 11 | `11_Factor_Research.dib` | A universe with a planted factor, Alphalens-style tear sheet, factor regressions |
| 12 | `12_HFT.dib` | Reading the order book, smart order routing, execution algorithms and TCA, pre-trade risk and FIX |
| 13 | `13_Crypto.dib` | Optional real Binance data, crypto risk, momentum rotation backtest |
| 14 | `14_FixedIncome.dib` | Curve bootstrapping, bond analytics, swaps and swaptions, credit default swaps |
| 15 | `15_StressTesting.dib` | Historical scenarios, custom and reverse stress, Monte Carlo futures |

---

Made by Gravicode Studios, led by Kang Fadhil
