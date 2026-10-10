# Getting started

[← Index](index.md) · [Bahasa Indonesia](../id/getting-started.md)

## Requirements

- **.NET 10 SDK** (10.0.100 or later; `global.json` rolls forward to the latest feature band).
- **Rust** (optional, stable toolchain with `cargo`) to build the native accelerator. Without it
  everything still works: `Finance.RustBridge` falls back to managed code with identical results.
- **Windows, Linux or macOS.** The Gallery uses Avalonia and runs on all three.

## Install from NuGet

```bash
dotnet new console -n MyQuant && cd MyQuant
dotnet add package Gravicode.Finance.Pricing
dotnet add package Gravicode.Finance.Risk
```

## Your first program

The program below prices an option, checks it against a binomial tree, and measures the
Value-at-Risk of a synthetic price history. It needs no network access.

```csharp
using Finance.Core.Market;
using Finance.Pricing.Options;
using Finance.Risk;

// 1. Black-Scholes-Merton price and Greeks of a 6-month at-the-money call.
var g = BlackScholes.Greeks(OptionType.Call, spot: 100, strike: 100, time: 0.5,
                            rate: 0.04, dividendYield: 0.01, volatility: 0.25);
Console.WriteLine($"Price {g.Price:F4}  Delta {g.Delta:F4}  Vega/pt {g.VegaPerPoint:F4}  Theta/day {g.ThetaPerDay:F4}");

// 2. Same contract on a 500-step CRR tree, as American and European exercise.
double euro = LatticePricer.Binomial(OptionType.Call, ExerciseStyle.European, 100, 100, 0.5, 0.04, 0.01, 0.25);
double amer = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 100, 100, 0.5, 0.04, 0.01, 0.25);
Console.WriteLine($"Tree (European call) {euro:F4}, American put {amer:F4}");

// 3. 99% one-day VaR and Expected Shortfall of a synthetic stock.
Bar[] bars = SyntheticMarket.GeometricBrownianBars(1000, startPrice: 100, annualDrift: 0.08, annualVolatility: 0.30);
var returns = new double[bars.Length - 1];
for (int i = 1; i < bars.Length; i++) returns[i - 1] = bars[i].Close / bars[i - 1].Close - 1;

VarEstimate hist = ValueAtRisk.Historical(returns, 0.99);
VarEstimate cf = ValueAtRisk.CornishFisher(returns, 0.99);
Console.WriteLine($"Historical VaR {hist.ValueAtRisk:P2}, ES {hist.ExpectedShortfall:P2}; Cornish-Fisher VaR {cf.ValueAtRisk:P2}");
```

VaR and ES are reported as **positive loss fractions**: `0.025` means "lose 2.5% of the
portfolio value". Use `ValueAtRiskAmount(portfolioValue)` for a currency amount.

## Build from source

```bash
git clone <repository> FinanceNet && cd FinanceNet
dotnet build FinanceNet.slnx
```

The solution file is `FinanceNet.slnx` (the XML solution format of .NET 10).

### The Rust accelerator

`src/Finance.RustBridge` runs `cargo build --release` on `native/finance-native` during its build
when `cargo` is on the `PATH`, and copies `finance_native.dll` (`.so` / `.dylib` on Linux/macOS) next
to the output. To build the crate yourself:

```bash
cd native/finance-native
cargo build --release
cargo test
```

To skip the Rust step (for example on a machine without Rust), pass:

```bash
dotnet build FinanceNet.slnx -p:SkipRustBuild=true
```

With the property set, an already-built binary in `native/finance-native/target/release` is still
copied if present. A missing `cargo` only prints a message; it never fails the build. At run time
`RustAccelerator.IsAvailable` tells you whether the native library loaded. Set
`FINANCE_NATIVE_DISABLE=1` to force the managed path, or `FINANCE_NATIVE_PATH` to point at a
specific library file.

## Run the tests

```bash
dotnet test FinanceNet.slnx
```

Tests are split by layer:

| Project | Covers |
|---|---|
| `tests/Finance.Foundation.Tests` | Core, Math |
| `tests/Finance.Analytics.Tests` | DataFrame, TimeSeries, Visualization |
| `tests/Finance.Quant.Tests` | Pricing, Risk |
| `tests/Finance.Portfolio.Tests` | Portfolio, Backtesting |
| `tests/Finance.Data.Tests` | MarketData, AlternativeData, Execution |
| `tests/Finance.AI.Tests` | ML, LLM, NewsAnalytics |
| `tests/Finance.Native.Tests` | GPU, RustBridge |

Run a single project, class or test with a filter:

```bash
dotnet test tests/Finance.Quant.Tests
dotnet test tests/Finance.Quant.Tests --filter "FullyQualifiedName~BlackScholes"
```

### Live tests

Unit tests never touch the network. A few *live* tests are skipped unless you opt in:

| Variable | Enables |
|---|---|
| `FINANCENET_LIVE=1` | Real Yahoo Finance and Binance requests (`Finance.Data.Tests`) |
| `FINANCENET_LLM_KEYFILE=<path>` | Real chat-completion and tool-calling calls (`Finance.AI.Tests`) |
| `FINANCENET_HF_TOKENFILE=<path>` | Hugging Face FinBERT inference (`Finance.AI.Tests`) |

Hugging Face's hosted inference returns HTTP 402 when the account has no inference credits; the
FinBERT live test reports that and skips.

## Configure an LLM

`LlmSettings.FromEnvironment()` (used by the Gallery and the tests) looks for, in order:

1. `FINANCENET_LLM_KEYFILE` — a key file (see below).
2. `FINANCENET_LLM_PROVIDER` with `FINANCENET_LLM_API_KEY`, `FINANCENET_LLM_ENDPOINT`,
   `FINANCENET_LLM_MODEL`. The provider is one of `OpenAI`, `AzureOpenAI`, `Anthropic`, `Ollama`,
   `HuggingFace`, `DeepSeek`, `OpenAICompatible`.
3. `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT` (+ `AZURE_OPENAI_DEPLOYMENT`).
4. `OPENAI_API_KEY` (+ `OPENAI_MODEL`).
5. `ANTHROPIC_API_KEY` (+ `ANTHROPIC_MODEL`).

A key file is plain text made of sections: a provider title line followed by `key: value` lines.

```text
Azure OpenAI
apikey: <your key>
endpoint: https://<resource>.openai.azure.com/
model: <deployment-name>
```

`LlmSettings.ToString()` redacts the key, so settings are safe to log. Keep key files **outside**
the repository.

## Run the apps

```bash
# Avalonia desktop Gallery (12 pages)
dotnet run --project samples/Finance.Gallery

# Render every Gallery page to PNG headlessly (1440×900), then exit
dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots

# Console tour of the library
dotnet run --project samples/Finance.Quickstart

# Benchmarks: quick stopwatch table, or full BenchmarkDotNet suites
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --filter *
```

## Notebooks

The `notebooks/` folder holds 15 `.dib` notebooks for **Polyglot Notebooks** (.NET Interactive) in
VS Code. They restore the `Gravicode.Finance.*` packages from `artifacts/packages` (create it with
`dotnet pack -c Release`) or from nuget.org. See [Notebooks](notebooks.md).

## Next steps

- Browse the [module guides](index.md#module-guides).
- Open the [Gallery](gallery.md) to see every module working interactively.

---

Made by Gravicode Studios, led by Kang Fadhil
