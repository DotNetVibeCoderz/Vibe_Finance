# Finance.Pricing

[← Indeks](../index.md) · [English](../../en/modules/pricing.md) · Paket: `Gravicode.Finance.Pricing`

Penilaian derivatif dan pendapatan tetap ala QuantLib: opsi ekuitas (rumus tertutup, lattice, Monte
Carlo, volatilitas stokastik), kurva imbal hasil, obligasi, swap, swaption, forward, credit default swap,
dan produk terstruktur.

Namespace: `Finance.Pricing.Options`, `Finance.Pricing.Rates`, `Finance.Pricing.Credit`,
`Finance.Pricing.Structured`.

## Konsep utama

- **Black-Scholes-Merton** — nilai opsi Eropa dengan asumsi harga lognormal dan dividend yield
  kontinu *q*. **Greeks** adalah sensitivitasnya: delta (terhadap spot), gamma (sensitivitas delta),
  vega (volatilitas), theta (waktu), rho (suku bunga), ditambah orde kedua vanna, volga, dan charm.
- **Implied volatility** — volatilitas yang menghasilkan kembali harga pasar; dicari dengan root
  finding.
- **Lattice** — binomial tree (CRR) dan trinomial tree melangkahkan harga ke depan lalu menghitung nilai
  mundur sambil memeriksa exercise lebih awal di setiap simpul, sehingga bisa menangani opsi
  **Amerika**.
- **Monte Carlo** — simulasikan banyak jalur harga lalu rata-ratakan payoff yang didiskonto.
  *Antithetic variates* memasangkan setiap jalur dengan cerminannya; *control variate* (Asian geometrik
  yang punya rumus tertutup) menghapus sebagian besar galat untuk Asian aritmetik.
- **Heston / SABR** — model volatilitas stokastik yang menghasilkan volatility smile. Heston dihitung
  semi-analitik lewat fungsi karakteristiknya; SABR dengan rumus Hagan.
- **Kurva** — `YieldCurve` memetakan jatuh tempo ke zero rate. *Bootstrapping* menyelesaikan pilar demi
  pilar sehingga deposito, FRA, dan swap ternilai ulang dengan tepat.
- **Risiko obligasi** — *duration* adalah sensitivitas harga terhadap yield, *convexity* adalah
  kelengkungannya, *DV01* adalah perubahan harga untuk satu basis poin.
- **CDS** — kurva hazard rate (intensitas default) memberikan peluang bertahan; par spread menyamakan
  premium leg dengan protection leg.

## Opsi

```csharp
using Finance.Pricing.Options;

Greeks g = BlackScholes.Greeks(OptionType.Put, spot: 100, strike: 95, time: 0.25, rate: 0.04, dividendYield: 0.0, volatility: 0.30);
Console.WriteLine($"{g.Price:F4}  Δ {g.Delta:F4}  Γ {g.Gamma:F5}  vega/poin {g.VegaPerPoint:F4}  θ/hari {g.ThetaPerDay:F4}");

double iv = BlackScholes.ImpliedVolatility(OptionType.Call, price: 7.5, spot: 100, strike: 100, time: 0.5, rate: 0.04);
// NaN jika harga melanggar batas no-arbitrage

double f76 = Black76.Price(OptionType.Call, forward: 82, strike: 80, time: 0.5, rate: 0.05, volatility: 0.35);
double bach = Bachelier.Price(OptionType.Call, forward: 0.03, strike: 0.03, time: 1, rate: 0.03, normalVolatility: 0.008);

double american = LatticePricer.Binomial(OptionType.Put, ExerciseStyle.American, 100, 100, 1, 0.05, 0, 0.2, steps: 1000);
double tri = LatticePricer.Trinomial(OptionType.Put, ExerciseStyle.American, 100, 100, 1, 0.05, 0, 0.2);
```

### Monte Carlo

```csharp
var mc = new MonteCarloPricer(spot: 100, rate: 0.05, dividendYield: 0, volatility: 0.2,
    new MonteCarloSettings(Paths: 200_000, Steps: 252, Seed: 7, Antithetic: true));

MonteCarloResult euro = mc.European(OptionType.Call, strike: 100, time: 1);
MonteCarloResult asian = mc.AsianArithmetic(OptionType.Call, 100, 1, useControlVariate: true);
MonteCarloResult knockOut = mc.Barrier(OptionType.Call, BarrierType.UpAndOut, strike: 100, barrier: 130, time: 1);
MonteCarloResult lookback = mc.LookbackFloatingStrike(OptionType.Call, 1);
MonteCarloResult custom = mc.PathDependent(1, path => Math.Max(0, path[^1] - path[0]));   // payoff buatan sendiri

var (lo, hi) = euro.ConfidenceInterval95;
Console.WriteLine($"{euro.Price:F4} ± {euro.StandardError:F4}");
```

Jalur disimulasikan secara paralel; seed yang sama memberikan harga yang sama berapa pun jumlah thread-nya.

### Volatilitas stokastik dan permukaan volatilitas

```csharp
var heston = new HestonParameters(V0: 0.04, Kappa: 2.0, Theta: 0.04, Sigma: 0.5, Rho: -0.7);
double hp = HestonModel.Price(OptionType.Call, 100, 100, 1, 0.03, 0, heston);

var sabr = new SabrParameters(Alpha: 0.2, Beta: 0.5, Rho: -0.3, Nu: 0.4);
double smileVol = SabrModel.ImpliedVolatility(forward: 0.03, strike: 0.035, time: 2, sabr);
SabrParameters fitted = SabrModel.Calibrate(0.03, 2, strikes, marketVols, beta: 0.5);

var surface = VolatilitySurface.FromQuotes(spot: 100, rate: 0.03, dividendYield: 0, quotes);  // OptionQuote(Expiry, Strike, Type, Price)
double vol = surface.Volatility(expiry: 0.75, strike: 105);
var (k, v) = surface.Smile(0.5);
```

## Suku bunga

```csharp
using Finance.Pricing.Rates;

YieldCurve curve = CurveBootstrapper.Bootstrap(new ICurveInstrument[]
{
    new DepositInstrument(0.25, 0.050),
    new DepositInstrument(0.5, 0.051),
    new FraInstrument(0.5, 1.0, 0.052),
    new SwapInstrument(2, 0.048),
    new SwapInstrument(5, 0.045),
    new SwapInstrument(10, 0.044),
}, CurveInterpolation.LogLinearDiscount);

double df5 = curve.DiscountFactor(5), z5 = curve.ZeroRate(5), fwd = curve.ForwardRate(2, 3);
YieldCurve flat = YieldCurve.Flat(0.04), bumped = curve.Shifted(0.0001);

var ns = NelsonSiegelSvensson.FitNelsonSiegel(maturities, zeroRates);
double nsRate = ns.ZeroRate(7);
```

### Obligasi

```csharp
var bond = new FixedRateBond(new DateOnly(2020, 5, 15), new DateOnly(2030, 5, 15), couponRate: 0.045, frequency: 2);
var settle = new DateOnly(2024, 8, 1);
BondAnalytics a = bond.Analytics(yield: 0.05, settle);
Console.WriteLine($"clean {a.CleanPrice:F4} dirty {a.DirtyPrice:F4} bunga berjalan {a.AccruedInterest:F4}");
Console.WriteLine($"Macaulay {a.MacaulayDuration:F3} modified {a.ModifiedDuration:F3} convexity {a.Convexity:F2} DV01 {a.Dv01:F4}");

double ytm = bond.YieldToMaturity(cleanPrice: 98.25, settle);
double zspread = bond.ZSpread(curve, 98.25, settle);
double zcb = ZeroCouponBond.Price(faceValue: 100, yield: 0.045, maturity: 7);
double frn = new FloatingRateNote(Maturity: 5, QuotedMargin: 0.01).Price(curve);
```

### Swap, swaption, forward

```csharp
var swap = new InterestRateSwap(Notional: 10_000_000, FixedRate: 0.045, Maturity: 5, PayFixed: true);
Console.WriteLine($"NPV {swap.Npv(curve):N0}, par {swap.ParRate(curve):P3}, DV01 {swap.Dv01(curve):N0}");
// Dual-curve: swap.Npv(kurvaDiskonto, kurvaProyeksi)

var swaption = new Swaption(Expiry: 1, Tenor: 5, Strike: 0.045, Payer: true, Notional: 10_000_000);
double black = swaption.PriceBlack(curve, volatility: 0.25);
double normal = swaption.PriceBachelier(curve, normalVolatility: 0.009);

double fwdPrice = Forwards.Price(spot: 100, rate: 0.05, time: 0.5, dividendYield: 0.02);
double fx = Forwards.FxForward(spot: 15_800, domesticRate: 0.06, foreignRate: 0.045, time: 0.25);   // misalnya USD/IDR
double repo = Forwards.ImpliedRepoRate(spot: 100, futures: 101.2, time: 0.25);
```

## Kredit

```csharp
using Finance.Pricing.Credit;

var cds = new CreditDefaultSwap(Notional: 10_000_000, Spread: 0.012, Maturity: 5, Frequency: 4, Recovery: 0.4);
HazardCurve hazard = CreditDefaultSwap.BootstrapHazard(curve, new double[] { 1, 3, 5 }, new[] { 0.008, 0.010, 0.012 });
Console.WriteLine($"par {cds.ParSpread(curve, hazard):P3}, NPV {cds.Npv(curve, hazard):N0}, risky duration {cds.RiskyDuration(curve, hazard):F3}");
double pd5 = hazard.DefaultProbability(5);
double quick = CreditDefaultSwap.CreditTriangleHazard(0.012, 0.4);   // λ ≈ s / (1 − R)
```

## Model short-rate: Hull-White dan G2++ (v0.2)

```csharp
var hw = GaussianShortRateModel.HullWhite(curve, meanReversion: 0.05, volatility: 0.01);
var g2 = GaussianShortRateModel.G2(curve, a: 0.5, sigma: 0.012, b: 0.05, eta: 0.008, rho: -0.6);

double zb   = hw.ZeroBond(t: 2, T: 7, state: new[] { 0.004 });      // P(t,T) pada state faktor tertentu
double zbo  = hw.ZeroBondOption(OptionType.Call, expiry: 2, maturity: 7, strike: 0.83);
double cap  = g2.Cap(start: 1, maturity: 6, strike: 0.037, frequency: 4);

var swpn = new Swaption(Expiry: 1, Tenor: 5, Strike: 0.04, Payer: false);
double euro = hw.JamshidianSwaption(swpn);                          // rumus tertutup satu faktor
MonteCarloResult berm = g2.BermudanSwaption(swpn, paths: 20_000);   // LSMC Longstaff-Schwartz
```

Keduanya adalah satu `GaussianShortRateModel` (Gn++ Brigo-Mercurio): r = Σxᵢ + φ(t), dipasang tepat pada
kurva awal. Simulasinya **eksak**: faktor dan ∫r dilangkahkan bersama dari kovarians Gaussian-nya, sehingga
faktor diskonto bebas bias langkah waktu. Nilai exercise Bermudan adalah nilai swap eksak model; nilai
lanjutan diregresikan pada nilai swap, kuadratnya, dan faktor.

## Volatilitas lokal (v0.2)

```csharp
var lv = new LocalVolatility(surface, spot: 100, rate: 0.03, dividendYield: 0.01);
double sigmaLoc = lv.Volatility(t: 0.75, s: 95);                    // Dupire, bentuk total-variance Gatheral
MonteCarloResult put = lv.PriceEuropean(OptionType.Put, strike: 85, expiry: 1);
```

Di bawah difusi local-vol, setiap opsi Eropa kembali ke implied vol masukannya; tes memeriksanya dengan
Monte Carlo terhadap Black-Scholes pada smile yang miring. `Floored` menghitung evaluasi saat permukaan yang
mengandung arbitrase membuat rasio Dupire tidak positif.

## Swap inflasi dan lintas mata uang (v0.2)

```csharp
var cpi = InflationCurve.FromZeroCouponSwaps(baseIndex: 310.2, new[] { 1.0, 2, 5, 10 }, new[] { 0.031, 0.028, 0.025, 0.024 });
var zcis = new ZeroCouponInflationSwap(10_000_000, FixedRate: 0.026, Maturity: 5);
var yoy  = new YearOnYearInflationSwap(10_000_000, FixedRate: 0.025, Maturity: 10);
double be = yoy.ParRate(discount, cpi);

var xccy = new CrossCurrencySwap(
    Domestic: new CrossCurrencyLeg(10_800_000, usdOis, usdSofr, Rate: 0),
    Foreign:  new CrossCurrencyLeg(10_000_000, eurDiscountInUsdCollateral, eurEstr, Rate: 0),
    Maturity: 5);
double basis = xccy.ParForeignSpread(spotFx: 1.08);                 // basis lintas mata uang tersirat
```

### Konveksitas YoY

```csharp
var conv = new YoYConvexityModel(InflationVol: 0.012, NominalVol: 0.25, InflationNominalCorrelation: 0.4, InflationCorrelation: 0.9);
double beAdj = yoy.ParRate(discount, cpi, conv);
double ratio = yoy.ExpectedRatio(i: 5, discount, cpi, conv);       // E[I(t5)/I(t4)] di bawah ukuran forward-t5
```

`YoYConvexityModel` adalah model pasar CPI-forward lognormal dari Mercurio (2005) (Brigo-Mercurio, edisi ke-2,
§16.3): setiap CPI forward Iⱼ lognormal dengan vol σ_I di bawah ukuran forward-Tⱼ miliknya, forward nominal Fᵢ
lognormal dengan vol σ_N, dan

E^{Tᵢ}[I(Tᵢ)/I(Tᵢ₋₁)] = Iᵢ(0)/Iᵢ₋₁(0) · exp(σ_I·Tᵢ₋₁·[ρ_{I,N}·σ_N·τFᵢ/(1+τFᵢ) − ρ_{I,I}·σ_I + σ_I]),

dengan ρ_{I,N} korelasi CPI/suku bunga nominal dan ρ_{I,I} korelasi CPI forward yang berurutan (forward nominal
dibekukan pada nilai hari ini di dalam drift). Forward nominal diambil dari kurva diskonto. Vol nol memberi harga
tanpa penyesuaian secara persis; periode pertama tidak pernah disesuaikan. Hasil pengukuran pada kurva CPI di atas
(level basis saling meniadakan dalam rasio) dengan diskonto `new YieldCurve({1,2,5,10}, {4.0%, 3.9%, 3.7%, 3.8%})`: par rate YoY tahunan 10Y bergeser
dari 241.697bp menjadi 244.245bp (+2.55bp) dengan parameter di atas, dan payer tetap 2.5% bernotional 1jt dari
−6,786.89 menjadi −4,704.35.

### Swap lintas mata uang dengan reset mark-to-market

```csharp
var mtm = new MtmCrossCurrencySwap(
    Domestic: new CrossCurrencyLeg(0, usdOis, usdSofr, Rate: 0),          // leg yang di-reset: notional ditentukan oleh reset
    Foreign:  new CrossCurrencyLeg(10_000_000, eurDiscountInUsdCollateral, eurEstr, Rate: 0),
    Maturity: 5);                                                         // ResetDomestic: true (default)
double mtmBasis = mtm.ParForeignSpread(spotFx: 1.08);
foreach (var p in mtm.ResetSchedule(1.08))                                // FX forward, notional, pertukaran di p.End
    Console.WriteLine($"{p.Start:F2} {p.ForwardFx:F6} {p.ResettingNotional:N2} {p.NotionalExchange:N2}");
```

Notional leg yang di-reset untuk setiap periode adalah notional konstan leg lainnya yang dikonversi dengan FX
forward pada awal periode, X(t) = S·P_for(t)/P_dom(t), dan selisih Nᵢ − Nᵢ₊₁ dipertukarkan pada setiap reset
(Nₙ saat jatuh tempo). Pada kurva deterministik ini sama dengan rangkaian swap notional konstan satu periode,
dan begitulah pengujian memeriksanya. Hasil pengukuran dengan kurva USD di atas sebagai `usdOis = usdSofr`, EUR
`{1,5,10} → {2.5%, 2.6%, 2.8%}` didiskonto 20bp lebih rendah, spot 1.08, per 1jt EUR: reset 1.080000, 1.084600,
1.089219, … 1.150043 (t = 4.75), pertukaran −4,599.77, −4,619.36, … dan 1,150,043.29 saat jatuh tempo. Dengan basis
nol pada kurva yang konsisten NPV-nya −3.1e-10. Leg reset berupa par floater (forecast USD = diskonto) memberi basis
yang persis sama dengan notional konstan (−20.1253bp); dengan forecast USD 10bp di atas kurva diskontonya, basis MtM
−10.0639bp dibanding −10.3992bp untuk notional konstan.

## Kredit: CDS gaya ISDA (v0.2)

```csharp
var cds = new IsdaCds(Notional: 10_000_000, Maturity: 5, Coupon: IsdaCds.InvestmentGradeCoupon, Recovery: 0.4);
double upfront = cds.UpfrontFromQuotedSpread(discount, quotedSpread: 0.0300);   // fraksi notional
double quoted  = cds.QuotedSpreadFromUpfront(discount, upfront);
double cs01    = cds.Cs01(discount, 0.0300);
```

Leg proteksi dan accrual-on-default diintegrasikan dalam bentuk tertutup pada setiap potongan dengan hazard
dan forward konstan, seperti pada ISDA CDS Standard Model; kupon standar dan konversi upfront ↔ quoted spread
melalui kurva hazard datar.

### CDS bertanggal dari tanggal transaksi dan tenor (jadwal IMM)

```csharp
var cds5y = IsdaCds.Standard(10_000_000, tradeDate: new DateOnly(2026, 10, 12), Tenor.Parse("5Y"),
    coupon: IsdaCds.InvestmentGradeCoupon, recovery: 0.4, calendar: BusinessCalendar.WeekendsOnly);
CdsSchedule s = cds5y.Schedule!;          // jatuh tempo, step-in, settlement tunai, periode accrual
double points = cds5y.UpfrontFromQuotedSpread(discount, 0.0300);     // upfront clean pada settlement tunai
var hz = HazardCurve.Flat(cds5y.FlatHazardFromQuotedSpread(discount, 0.0300));
double cash = cds5y.CashSettlement(discount, hz);                     // N·(points − accrued)
DateOnly m = CdsDates.StandardMaturity(new DateOnly(2016, 3, 21), Tenor.Parse("5Y"));   // 2021-06-20
```

Konvensi (ISDA CDS Standard Model, pasca-2009):

- **Tanggal roll** adalah tanggal 20 Mar/Jun/Sep/Des (`CdsDates.NextImmDate` / `PreviousImmDate`).
- **Jatuh tempo** (`CdsDates.StandardMaturity`, aturan `cdsMaturity` QuantLib): berjangkar pada tanggal IMM
  terakhir pada atau sebelum tanggal transaksi, lalu ditambah tenor + 3M. Dengan `CdsRollConvention.SemiAnnual2015`
  (default, sejak 20 Des 2015) jangkar Juni/Desember mundur tiga bulan, sehingga jatuh tempo hanya bergulir pada
  20 Mar dan 20 Sep: transaksi 20 Mar s.d. 19 Sep jatuh tempo 20 Jun, 20 Sep s.d. 19 Mar jatuh tempo 20 Des.
  `Quarterly` adalah aturan pra-2015.
- **Step-in** T+1 (hari kalender), **settlement tunai** T+3 hari kerja.
- **Awal accrual**: tanggal IMM tersesuaikan terakhir pada atau sebelum step-in (kupon pertama penuh; accrued,
  dari awal accrual sampai T inklusif, dikembalikan saat settlement). Jika tanggal IMM di akhir pekan tergeser
  melewati step-in, tanggal IMM sebelumnya yang dipakai.
- **Tanggal kupon** disesuaikan Following; accrual Act/360; periode terakhir ber-accrual sampai jatuh tempo
  inklusif (akhir = jatuh tempo tanpa penyesuaian + 1 hari) dan dibayar pada jatuh tempo tersesuaikan.
- Proteksi berlaku dari tanggal transaksi sampai jatuh tempo inklusif; waktu model Act/365F dari tanggal
  transaksi; survival untuk kupon dan accrual-on-default diamati satu hari sebelum setiap tanggal accrual, seperti
  pada kode ISDA.
- `Upfront` adalah harga clean pada settlement tunai, (proteksi − c·RPV01)/P(t_cs) + c·accrued; `ParSpread`
  membagi dengan RPV01 clean, sehingga kontrak yang dikuotasi sama dengan kuponnya memiliki points nol dan
  di-settle −N·c·accrued.

Hasil pengukuran: transaksi Senin 12 Okt 2026, 5Y jatuh tempo 20 Des 2031; accrual mulai Senin 21 Sep 2026
(20 Sep hari Minggu), accrued 22 hari, settlement tunai 15 Okt 2026, 21 periode (pertama 21 Sep → 21 Des 2026,
91 hari; terakhir 22 Sep → 21 Des 2031, 90 hari, dibayar Senin 22 Des 2031). Pada kurva log-linear
`{1,2,5,10} → {4.0%, 3.9%, 3.7%, 3.8%}` dengan kuotasi 300bp dan kupon 100bp, 10jt: points upfront 8.4057%, accrued
6,111.11, settlement tunai 834,462.42, CS01 3,861.88. Bentuk fraksi tahun `new IsdaCds(10_000_000, 5)` memberi
8.1563%, karena kontrak bertanggal berjalan 5.19 tahun.

## Produk terstruktur

```csharp
using Finance.Pricing.Structured;

var rc = new ReverseConvertible(FaceValue: 1000, CouponRate: 0.10, Maturity: 1, Strike: 100);
double rcValue = rc.Price(spot: 100, rate: 0.04, dividendYield: 0.01, volatility: 0.3);

var cpn = new CapitalProtectedNote(FaceValue: 1000, Maturity: 5, ProtectionLevel: 1.0, Participation: 0.6);
double cpnValue = cpn.Price(100, 0.04, 0.02, 0.2);

var auto = new AutocallableNote(1000, ObservationTimes: new[] { 0.5, 1.0, 1.5, 2.0 }, CouponPerPeriod: 0.04,
    AutocallLevel: 1.0, ProtectionBarrier: 0.6);
AutocallableResult res = auto.Price(spot: 100, rate: 0.04, dividendYield: 0.01, volatility: 0.25);
Console.WriteLine($"{res.Price:F2} ± {res.StandardError:F2}, P(rugi) {res.CapitalLossProbability:P1}, umur harapan {res.ExpectedLife:F2} th");
```

`ReverseConvertible.FairCoupon(...)` dan `CapitalProtectedNote.FairParticipation(...)` mencari nilai
yang membuat produk bernilai par.

## Catatan dan batasan

- Waktu dalam satuan **tahun**; suku bunga dimajemukkan secara kontinu kecuali disebutkan lain (yield
  obligasi memakai konvensi pasar sesuai frekuensi kupon).
- Bootstrap kurva cubic-zero melakukan iterasi atas semua pilar, karena spline bersifat non-lokal:
  menambah pilar menggeser segmen sebelumnya.
- `CreditDefaultSwap` adalah model hazard rate biasa pada grid mingguan. `IsdaCds` mengikuti matematika ISDA
  Standard Model (integral eksak, forward datar antar node kurva, kupon standar, upfront dan CS01); bentuk
  posisionalnya memakai fraksi tahun, `IsdaCds.Standard` memakai jadwal IMM bertanggal dengan step-in T+1 dan
  settlement T+3. Hari libur mengikuti kalender yang diberikan (default hanya akhir pekan); varian "Markit fix"
  2009 untuk accrual-on-default tidak disediakan.
- Konveksitas year-on-year bersifat opsional (`YoYConvexityModel`, vol dan korelasi datar, forward nominal
  dibekukan di dalam drift); kurva inflasi tidak memodelkan musiman atau lag indeksasi. Reset swap lintas mata
  uang MtM memakai FX forward deterministik (tanpa konveksitas korelasi FX/suku bunga).
- Harga Bermudan LSMC sedikit bias ke bawah (exercise sub-optimal dari aturan hasil regresi), seperti
  estimator Longstaff-Schwartz pada umumnya.
- Pengujian membandingkan dengan nilai acuan Hull, Haug, dan Fang-Oosterlee, put-call parity, beda
  hingga (finite difference), serta Monte Carlo vs rumus tertutup.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
