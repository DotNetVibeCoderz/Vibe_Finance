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
  Standard Model (integral eksak, forward datar antar node kurva, kupon standar, upfront dan CS01) tetapi
  memakai fraksi tahun alih-alih tanggal IMM yang disesuaikan hari kerja, tanpa step-in T+1 atau jeda
  settlement tunai.
- Swap inflasi year-on-year memakai rasio indeks forward tanpa penyesuaian konveksitas; kurva inflasi tidak
  memodelkan musiman atau lag indeksasi. Swap lintas mata uang memakai notional konstan (tanpa reset MtM).
- Harga Bermudan LSMC sedikit bias ke bawah (exercise sub-optimal dari aturan hasil regresi), seperti
  estimator Longstaff-Schwartz pada umumnya.
- Pengujian membandingkan dengan nilai acuan Hull, Haug, dan Fang-Oosterlee, put-call parity, beda
  hingga (finite difference), serta Monte Carlo vs rumus tertutup.

---

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
