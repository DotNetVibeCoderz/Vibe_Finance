# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Finance.Net: a quantitative-finance platform for .NET 10 (QuantLib / PyPortfolioOpt / pyfolio / Alphalens /
Zipline / FinRL / FinGPT capabilities in C#), with Rust kernels and ILGPU acceleration. The spec of record is
`solution-design.md` (Bahasa Indonesia); the original brief is `FinanceNet.txt`. `PLAN.md` is the roadmap and
`Progress.md` tracks what is done — update `Progress.md` when work lands.

## Commands

The solution is **`FinanceNet.slnx`** (XML format). Target framework net10.0 (`global.json` rolls forward from 10.0.100).

```bash
dotnet build FinanceNet.slnx                         # runs `cargo build --release` for native/finance-native when cargo exists
dotnet build FinanceNet.slnx -p:SkipRustBuild=true   # managed only
dotnet test FinanceNet.slnx
dotnet test tests/Finance.Quant.Tests                # one test project
dotnet test tests/Finance.Quant.Tests --filter "FullyQualifiedName~BlackScholes"   # one class / test
dotnet pack -c Release                               # Gravicode.Finance.* → artifacts/packages (icon + README packed)

dotnet run --project samples/Finance.Gallery                                   # Avalonia gallery
dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots  # headless 1440×900 PNGs of every page
dotnet run --project samples/Finance.Quickstart
dotnet run --project tools/NotebookCheck            # compiles + runs every notebooks/*.dib C# cell
dotnet run -c Release --project benchmarks/Finance.Benchmarks -- --quick
dotnet run --project tools/IconGen -- assets         # regenerates the NuGet icon
cd native/finance-native && cargo test
```

Live tests are skipped unless enabled: `FINANCENET_LIVE=1` (Yahoo/Binance), `FINANCENET_LLM_KEYFILE=<key file>`
(Azure OpenAI etc.), `FINANCENET_HF_TOKENFILE=<token file>` (hosted FinBERT), `FINANCENET_FINBERT_LOCAL=1` (local FinBERT via
HF.Net, ~440 MB download). The Gallery's AI page also reads
`FINANCENET_LLM_KEYFILE` / `OPENAI_API_KEY` / `AZURE_OPENAI_*`.

## Layout

- `src/Finance.*` — 18 libraries, each its own package `Gravicode.<ProjectName>` (set in `Directory.Build.props`
  for anything under `src/`). Dependencies flow Core → Math → (DataFrame → TimeSeries) → domain modules;
  Backtesting, Portfolio, Execution, LLM, NewsAnalytics deliberately do **not** reference each other — hosts
  wire them together (e.g. the LLM assistant's tools are delegates registered by the Gallery).
- `tests/` — xunit.v3, one project per module group (Foundation, Analytics, Quant, Portfolio, Data, AI, Native).
- `native/finance-native` — Rust cdylib, C ABI `fnat_*`, no external crates. `Finance.RustBridge` P/Invokes it
  and falls back to `ManagedKernels` (identical algorithms and RNG streams) when the DLL is missing.
- `samples/Finance.Gallery` — Avalonia 12 app built in C# (only `App.axaml` is XAML). Pages in `Pages/`,
  registered in `Shell/PageRegistry.cs`; charts are hand-drawn controls in `Controls/`.
- `notebooks/` — 15 `.dib` files restoring from `../artifacts/packages` (run `dotnet pack` first) or nuget.org.
- `docs/en`, `docs/id` — parallel bilingual docs; screenshots in `docs/screenshots`.

Central package versions live in `Directory.Packages.props`. Foundation packages come from nuget.org:
Gravicode.Science.GraviNum/GraviFrame/GraviLearn 1.1.0, Gravicode.RLNet 0.1.0, Gravicode.HFNet.* 0.8.0.

## Things that will bite you

- **`Finance.Math`'s namespace is `Finance.Mathematics`, on purpose.** A `Finance.Math` namespace would make
  `Math.Sqrt` inside any `Finance.*` namespace resolve to it. For the same reason never declare a type named
  after a module segment (`Portfolio`, `Risk`, `DataFrame`, …) — use `PortfolioWeights`, `PricePanel`, etc.
- **Gallery: `Theme` collides with `StyledElement.Theme`** — the palette class is `Palette`. `Matrix` is
  ambiguous with `Avalonia.Matrix`; alias `using Matrix = Finance.Mathematics.Matrix;`.
- **Built-in strategies live in `Finance.Backtesting.Strategies`**, not `Finance.Backtesting`.
- **Screenshots must go through `--screenshot`** (Avalonia.Headless + Skia). Rendering the real window clips to
  the physical screen (this laptop is 1366×720).
- **Python edits on this machine default to cp1252 and CRLF.** Always read/write bytes and encode UTF-8
  explicitly; a cp1252 byte in a .cs file makes Roslyn decode the whole file as ANSI (mojibake, broken char
  literals).
- **Binance**: `api.binance.com` fails TLS from this network; providers default to the official mirrors
  `https://data-api.binance.vision` and `wss://data-stream.binance.vision/ws`. Coinbase also fails TLS here.
- **Reddit's unauthenticated JSON is usually blocked**; the Social Sentiment page falls back to labelled
  synthetic posts. Hosted FinBERT returns 402 without Hugging Face credits.
- **ILGPU kernels need `InternalsVisibleTo ILGPURuntime`** (in `Finance.GPU.csproj`), or the CPU accelerator
  cannot compile them. DirectML/Vulkan throw `NotSupportedException` by design.
- **Rust Monte Carlo uses 64 fixed RNG streams** so native, managed and any thread count agree bit-for-bit —
  don't make the stream count depend on core count.
- **Benchmarks: this is a 4-core 15 W laptop that throttles.** Alternate variants in one process and take the
  best of many runs; never compare timings across separate runs. Rust only clearly wins for VaR and
  multi-threaded Monte Carlo.
- The indicator catalogue is 248 entries (188 indicators + 60 patterns). `IndicatorCategory` values are
  public API: append new members at the end (as `Cycle` was), never insert — same for `CandlePattern`.
- Historical stress scenarios are stylised, rounded shocks (illustrative, not regulatory).

## Conventions

- Bilingual (EN + ID) documentation; credit line "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil"
  in docs and apps (`FinanceInfo.CreditIndonesian` / `CreditEnglish`).
- Tests compare against textbook/reference values or independent methods, never the implementation's own output.
- Gallery UI follows the dark palette in `samples/Finance.Gallery/Palette.cs`; build pages with the `Ui` helpers.

## Credentials (external files — never copy into the repo)

- NuGet API key: `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`
- LLM key file: `C:\Users\mifma\Documents\CodeSandbox\testkey.txt` · HF token: `C:\Users\mifma\Documents\CodeSandbox\HFToken.txt`

Read them only at run/test time via the env vars above; never print, log or commit their contents.
Publishing to nuget.org is outward-facing — confirm with the user first.
