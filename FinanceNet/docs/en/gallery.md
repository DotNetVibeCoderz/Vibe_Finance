# Finance.Net Gallery

[← Index](index.md) · [Bahasa Indonesia](../id/gallery.md)

An Avalonia desktop application that exercises every module interactively. It runs fully offline on
synthetic data and switches to live data (Binance, Yahoo/Google News RSS, Reddit, your LLM) when the
network and credentials are available.

```bash
dotnet run --project samples/Finance.Gallery
dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots   # regenerate these images
```

The screenshot mode renders each page headlessly with Avalonia.Headless + Skia at 1440×900, so the
images do not depend on your screen size. To include a real assistant conversation, set
`FINANCENET_LLM_KEYFILE` (or `OPENAI_API_KEY` / `AZURE_OPENAI_*`) first.

## Overview

The landing page: the module map, package list and the attribution.

![Overview](../screenshots/00-overview.png)

## Markets

### Live Market Dashboard

A Binance watchlist (BTC, ETH, SOL, BNB, XRP, DOGE) with 24-hour change and sparklines, a
1-minute candlestick chart with EMA 20/50 overlays and an RSI pane, and session statistics (VWAP,
realised volatility, RSI signal). Refreshes every few seconds; click a tile to switch symbol. Falls
back to `SyntheticProvider` offline. *Modules: MarketData, TimeSeries.*

![Live Market Dashboard](../screenshots/01-live-market-dashboard.png)

### Order Book Viewer

A level-2 price ladder with depth bars, cumulative depth chart, spread in basis points, top-5-level
imbalance, mid price vs microprice over time, and the cost of sweeping the book for increasing
sizes. Refreshes every second. *Modules: MarketData, Core.*

![Order Book Viewer](../screenshots/02-order-book-viewer.png)

### Indicator Explorer

Browse all 248 entries of the indicator catalogue by category or search, adjust each parameter
with a slider and see the result as a price overlay or in its own pane, with the latest values.
*Module: TimeSeries.*

![Indicator Explorer](../screenshots/03-indicator-explorer.png)

## Quant

### Portfolio Optimizer

Estimate returns and a Ledoit-Wolf covariance, then compare max-Sharpe, min-volatility, risk
parity, HRP, Black-Litterman and CVaR portfolios on the efficient frontier, with weights, risk
contributions and a discrete share allocation. *Modules: Portfolio, Math.*

![Portfolio Optimizer](../screenshots/04-portfolio-optimizer.png)

### VaR Explorer

A four-asset book with adjustable weights and confidence: a return histogram with VaR/ES markers
and a normal fit, VaR/ES by five methods, P&L under the historical stress scenarios, a one-year
Monte Carlo fan chart, and a rolling backtest with Kupiec, Christoffersen and the Basel traffic
light. *Module: Risk.*

![VaR Explorer](../screenshots/05-var-explorer.png)

### Greeks Explorer

Sliders for spot, strike, expiry, volatility, rate and dividend yield drive the price and all
Greeks live; charts show value vs spot against the payoff, any Greek's profile across spot, and
time decay; an engine table compares closed form, CRR, Monte Carlo and trinomial (American) prices.
*Module: Pricing.*

![Greeks Explorer](../screenshots/06-greeks-explorer.png)

## Trading

### Strategy Builder

A no-code rule editor: build an entry and an exit condition from Close, SMA, EMA, RSI, Bollinger
bands or a constant ("crosses above", "crosses below", "is above", "is below"), add stop-loss,
take-profit and position size. Every change re-runs the event-driven backtest — signals on the
candle chart, equity vs buy-and-hold, key statistics — and regenerates the equivalent C#
`Strategy` class ready to paste into a project. *Modules: Backtesting, TimeSeries.*

![Strategy Builder](../screenshots/07-strategy-builder.png)

### Backtesting Studio

Run the built-in strategies (SMA crossover, RSI mean reversion, Bollinger breakout, multi-asset
momentum rotation, buy & hold) on synthetic or live Yahoo daily data with adjustable commission and
slippage: equity vs buy-and-hold, drawdown, round-trip log, fills on the candle chart, and a
walk-forward optimisation of an SMA grid with stitched out-of-sample equity. *Modules: Backtesting,
MarketData.*

![Backtesting Studio](../screenshots/08-backtesting-studio.png)

## Intelligence

### AI Financial Assistant

A chat with a tool-calling LLM whose tools are live Finance.Net functions — technical snapshot,
VaR and Black-Scholes pricing. Each reply shows which tools ran; the side panel logs every call with
its arguments. Configure the provider in the form or through environment variables; keys are kept
in memory only and never shown. *Modules: LLM, Pricing, Risk, TimeSeries.*

![AI Financial Assistant](../screenshots/09-ai-financial-assistant.png)

### News Intelligence

Live Yahoo Finance and Google News RSS for a list of tickers, de-duplicated with MinHash, scored by
the finance lexicon and classified into events; per-ticker sentiment gauges, event counts and a
decay-weighted daily sentiment chart. Falls back to sample headlines offline.
*Module: NewsAnalytics.*

![News Intelligence](../screenshots/10-news-intelligence.png)

### Social Sentiment

Reddit posts from r/wallstreetbets, r/stocks and r/investing: most-mentioned tickers, mentions vs
engagement-weighted sentiment, hourly mention counts with a buzz z-score and spike threshold, and the
highest-engagement posts. When Reddit blocks unauthenticated access (common), the page uses clearly
labelled synthetic posts. *Module: AlternativeData.*

![Social Sentiment](../screenshots/11-social-sentiment.png)

---

Made by Gravicode Studios, led by Kang Fadhil
