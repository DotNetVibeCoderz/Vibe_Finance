# Notebook

[← Indeks](index.md) · [English](../en/notebooks.md)

Lima belas **Polyglot Notebook** (`.dib`, .NET Interactive) di folder `notebooks/` memandu Anda
menjelajahi library dengan sel C# yang bisa langsung dijalankan.

## Cara menjalankan

1. Pasang **VS Code** beserta ekstensi **Polyglot Notebooks** (sudah termasuk kernel .NET Interactive).
2. Bangun paketnya sekali: `dotnet pack -c Release` (menulis ke `artifacts/packages`).
3. Buka file `.dib` lalu pilih *Run All*. Sel pertama menambahkan `../artifacts/packages` sebagai sumber
   paket dan mereferensikan paket `Gravicode.Finance.*` yang dibutuhkan; begitu paket tersedia di
   nuget.org, sel yang sama akan memulihkan paket dari sana.

Semua notebook bisa berjalan offline dengan data sintetis; sel yang memakai jaringan menyebutkannya dan
punya fallback. `dotnet run --project tools/NotebookCheck` mengompilasi dan menjalankan setiap sel C# di
semua notebook — begitulah notebook dijaga agar tetap berfungsi.

## Daftar notebook

| # | Notebook | Yang dipelajari |
|---|---|---|
| 01 | `01_Market_Data.dib` | Bar dan frame OHLCV, simpan-baca CSV, panel multi-aset, provider, opsional data live |
| 02 | `02_Indicators.dib` | Katalog 248 entri, indikator tren/momentum/volatilitas, pola candlestick, indikator streaming, akselerasi Rust |
| 03 | `03_Portfolio.dib` | Estimator return dan risiko, mean-variance dan frontier, Black-Litterman, portofolio berbasis risiko, CVaR, alokasi diskrit |
| 04 | `04_Risk.dib` | Return berekor tebal, VaR dan ES, backtesting VaR, model volatilitas, atribusi risiko portofolio, laporan kinerja |
| 05 | `05_Options.dib` | Black-Scholes-Merton dan Greeks, exercise lebih awal, eksotik Monte Carlo, volatilitas stokastik dan smile, Monte Carlo GPU |
| 06 | `06_Backtesting.dib` | Strategi event-driven, trade di grafik, parameter sweep dan walk-forward, riset vektor, backtest portofolio HRP |
| 07 | `07_Machine_Learning.dib` | Fitur dan label, random forest dengan purged CV, penjelasan SHAP, reinforcement learning dengan RLNet, peramalan |
| 08 | `08_NLP.dib` | Sentimen leksikon, deteksi nyaris-duplikat, membaca RSS, analisis earnings call, opsional LLM dan FinBERT |
| 09 | `09_Sentiment.dib` | Arus berita sintetis dengan sinyal tersembunyi, indeks sentimen berita, media sosial dan buzz |
| 10 | `10_Alternative_Data.dib` | Analitik ESG, graf rantai pasok, analitik cuaca, opsional data cuaca nyata |
| 11 | `11_Factor_Research.dib` | Semesta dengan faktor yang ditanam, tear sheet ala Alphalens, regresi faktor |
| 12 | `12_HFT.dib` | Membaca order book, smart order routing, algoritma eksekusi dan TCA, risiko pra-transaksi dan FIX |
| 13 | `13_Crypto.dib` | Opsional data Binance nyata, risiko kripto, backtest rotasi momentum |
| 14 | `14_FixedIncome.dib` | Bootstrapping kurva, analitik obligasi, swap dan swaption, credit default swap |
| 15 | `15_StressTesting.dib` | Skenario historis, stress kustom dan reverse stress, masa depan Monte Carlo |

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
