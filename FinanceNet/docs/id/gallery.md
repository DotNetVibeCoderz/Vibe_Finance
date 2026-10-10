# Finance.Net Gallery

[← Indeks](index.md) · [English](../en/gallery.md)

Aplikasi desktop Avalonia yang menjalankan setiap modul secara interaktif. Aplikasi ini bisa berjalan
sepenuhnya offline dengan data sintetis, dan beralih ke data live (Binance, RSS Yahoo/Google News,
Reddit, LLM Anda) jika jaringan dan kredensial tersedia.

```bash
dotnet run --project samples/Finance.Gallery
dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots   # membuat ulang gambar-gambar ini
```

Mode screenshot merender setiap halaman secara headless dengan Avalonia.Headless + Skia pada 1440×900,
sehingga gambar tidak bergantung pada ukuran layar Anda. Agar percakapan asisten sungguhan ikut
tertangkap, setel `FINANCENET_LLM_KEYFILE` (atau `OPENAI_API_KEY` / `AZURE_OPENAI_*`) terlebih dulu.

## Ringkasan (Overview)

Halaman pembuka: peta modul, daftar paket, dan atribusi.

![Overview](../screenshots/00-overview.png)

## Pasar

### Live Market Dashboard

Daftar pantauan Binance (BTC, ETH, SOL, BNB, XRP, DOGE) dengan perubahan 24 jam dan sparkline, grafik
candlestick 1 menit dengan overlay EMA 20/50 dan panel RSI, serta statistik sesi (VWAP, volatilitas
terealisasi, sinyal RSI). Diperbarui setiap beberapa detik; klik sebuah tile untuk berganti simbol. Jika
offline, beralih ke `SyntheticProvider`. *Modul: MarketData, TimeSeries.*

![Live Market Dashboard](../screenshots/01-live-market-dashboard.png)

### Order Book Viewer

Price ladder level 2 dengan bar kedalaman, grafik kedalaman kumulatif, spread dalam basis poin,
imbalance 5 level teratas, mid price vs microprice dari waktu ke waktu, dan biaya menyapu book untuk
ukuran order yang makin besar. Diperbarui setiap detik. *Modul: MarketData, Core.*

![Order Book Viewer](../screenshots/02-order-book-viewer.png)

### Indicator Explorer

Jelajahi ke-144 entri katalog indikator berdasarkan kategori atau pencarian, atur setiap parameter
dengan slider, dan lihat hasilnya sebagai overlay harga atau di panel tersendiri, lengkap dengan nilai
terakhir. *Modul: TimeSeries.*

![Indicator Explorer](../screenshots/03-indicator-explorer.png)

## Kuantitatif

### Portfolio Optimizer

Estimasi return dan kovarians Ledoit-Wolf, lalu bandingkan portofolio max-Sharpe, min-volatilitas,
risk parity, HRP, Black-Litterman, dan CVaR di efficient frontier, lengkap dengan bobot, kontribusi
risiko, dan alokasi lembar saham diskrit. *Modul: Portfolio, Math.*

![Portfolio Optimizer](../screenshots/04-portfolio-optimizer.png)

### VaR Explorer

Portofolio empat aset dengan bobot dan tingkat keyakinan yang bisa diatur: histogram return dengan
penanda VaR/ES dan kurva normal, VaR/ES dengan lima metode, P&L pada skenario stress historis, fan chart
Monte Carlo satu tahun, dan backtest rolling dengan uji Kupiec, Christoffersen, serta traffic light
Basel. *Modul: Risk.*

![VaR Explorer](../screenshots/05-var-explorer.png)

### Greeks Explorer

Slider spot, strike, jatuh tempo, volatilitas, suku bunga, dan dividend yield langsung menggerakkan
harga dan semua Greeks; grafik menampilkan nilai vs spot dibanding payoff, profil Greek apa pun
sepanjang spot, dan peluruhan waktu; tabel engine membandingkan harga rumus tertutup, CRR, Monte Carlo,
dan trinomial (Amerika). *Modul: Pricing.*

![Greeks Explorer](../screenshots/06-greeks-explorer.png)

## Trading

### Strategy Builder

Editor aturan tanpa kode: susun kondisi masuk dan keluar dari Close, SMA, EMA, RSI, pita Bollinger
atau sebuah konstanta ("crosses above", "crosses below", "is above", "is below"), lalu tambahkan
stop-loss, take-profit dan ukuran posisi. Setiap perubahan langsung menjalankan ulang backtest
event-driven — sinyal di grafik candle, ekuitas vs buy-and-hold, statistik utama — dan membuat ulang
kelas `Strategy` C# yang setara, siap ditempel ke proyek. *Modul: Backtesting, TimeSeries.*

![Strategy Builder](../screenshots/07-strategy-builder.png)

### Backtesting Studio

Jalankan strategi bawaan (SMA crossover, RSI mean reversion, Bollinger breakout, rotasi momentum
multi-aset, buy & hold) pada data harian sintetis atau Yahoo live dengan komisi dan slippage yang
dapat diatur: ekuitas vs buy-and-hold, drawdown, log round-trip, titik eksekusi di grafik candle, dan
optimasi walk-forward atas grid SMA dengan ekuitas out-of-sample yang disambung. *Modul: Backtesting,
MarketData.*

![Backtesting Studio](../screenshots/08-backtesting-studio.png)

## Intelijen

### AI Financial Assistant

Percakapan dengan LLM ber-tool-calling yang tool-nya adalah fungsi Finance.Net yang sesungguhnya —
snapshot teknikal, VaR, dan pricing Black-Scholes. Setiap balasan menampilkan tool yang dijalankan;
panel samping mencatat setiap panggilan beserta argumennya. Konfigurasikan provider di formulir atau
lewat variabel lingkungan; kunci hanya disimpan di memori dan tidak pernah ditampilkan.
*Modul: LLM, Pricing, Risk, TimeSeries.*

![AI Financial Assistant](../screenshots/09-ai-financial-assistant.png)

### News Intelligence

RSS live Yahoo Finance dan Google News untuk sekumpulan ticker, di-de-duplikasi dengan MinHash, diberi
skor oleh leksikon keuangan, dan diklasifikasikan ke dalam event; gauge sentimen per ticker, jumlah
event, dan grafik sentimen harian berbobot peluruhan. Jika offline, memakai contoh judul berita.
*Modul: NewsAnalytics.*

![News Intelligence](../screenshots/10-news-intelligence.png)

### Social Sentiment

Postingan Reddit dari r/wallstreetbets, r/stocks, dan r/investing: ticker yang paling sering disebut,
jumlah sebutan vs sentimen berbobot engagement, jumlah sebutan per jam dengan z-score buzz dan ambang
lonjakan, serta postingan dengan engagement tertinggi. Jika Reddit memblokir akses tanpa autentikasi
(sering terjadi), halaman ini memakai postingan sintetis yang diberi label jelas. *Modul:
AlternativeData.*

![Social Sentiment](../screenshots/11-social-sentiment.png)

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
