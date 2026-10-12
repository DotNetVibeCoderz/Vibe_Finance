# Finance.Pricing

[← Index](../index.md) · [Bahasa Indonesia](../../id/modules/pricing.md) · Package: `Gravicode.Finance.Pricing`

Derivatives and fixed-income pricing in the spirit of QuantLib: equity options (closed form,
lattices, Monte Carlo, stochastic volatility), yield curves, bonds, swaps, swaptions, forwards,
credit default swaps and structured notes.

Namespaces: `Finance.Pricing.Options`, `Finance.Pricing.Rates`, `Finance.Pricing.Credit`,
`Finance.Pricing.Structured`.

## Key concepts

- **Black-Scholes-Merton** — European option value under lognormal prices with continuous dividend
  yield *q*. The **Greeks** are sensitivities: delta (spot), gamma (delta's sensitivity), vega
  (volatility), theta (time), rho (rate), plus second-order vanna, volga and charm.
- **Implied volatility** — the volatility that reproduces a market price; found by root finding.
- **Lattices** — binomial (CRR) and trinomial trees step the price forward and roll values back,
  checking early exercise at each node, which handles **American** options.
- **Monte Carlo** — simulate many price paths and average the discounted payoff. *Antithetic
  variates* pair each path with its mirror; a *control variate* (the geometric Asian, which has a
  closed form) removes most of the error for arithmetic Asians.
- **Heston / SABR** — stochastic-volatility models that produce a volatility smile. Heston is
  priced semi-analytically through its characteristic function; SABR via Hagan's formula.
- **Curves** — a `YieldCurve` maps maturity to zero rate. *Bootstrapping* solves pillar by pillar
  so deposits, FRAs and swaps reprice exactly.
- **Bond risk** — *duration* is price sensitivity to yield, *convexity* its curvature, *DV01* the
  price change for one basis point.
- **CDS** — a hazard-rate (default intensity) curve gives survival probabilities; the par spread
  equates the premium leg with the protection leg.

## Options

```csharp
using Finance.Pricing.Options;

Greeks g = BlackScholes.Greeks(OptionType.Put, spot: 100, strike: 95, time: 0.25, rate: 0.04, dividendYield: 0.0, volatility: 0.30);
Console.WriteLine($"{g.Price:F4}  Δ {g.Delta:F4}  Γ {g.Gamma:F5}  vega/pt {g.VegaPerPoint:F4}  θ/day {g.ThetaPerDay:F4}");

double iv = BlackScholes.ImpliedVolatility(OptionType.Call, price: 7.5, spot: 100, strike: 100, time: 0.5, rate: 0.04);
// NaN when the price violates no-arbitrage bounds

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
MonteCarloResult custom = mc.PathDependent(1, path => Math.Max(0, path[^1] - path[0]));   // your own payoff

var (lo, hi) = euro.ConfidenceInterval95;
Console.WriteLine($"{euro.Price:F4} ± {euro.StandardError:F4}");
```

Paths are simulated in parallel; the same seed gives the same price regardless of thread count.

### Stochastic volatility and surfaces

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

## Rates

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

### Bonds

```csharp
var bond = new FixedRateBond(new DateOnly(2020, 5, 15), new DateOnly(2030, 5, 15), couponRate: 0.045, frequency: 2);
var settle = new DateOnly(2024, 8, 1);
BondAnalytics a = bond.Analytics(yield: 0.05, settle);
Console.WriteLine($"clean {a.CleanPrice:F4} dirty {a.DirtyPrice:F4} accrued {a.AccruedInterest:F4}");
Console.WriteLine($"Macaulay {a.MacaulayDuration:F3} modified {a.ModifiedDuration:F3} convexity {a.Convexity:F2} DV01 {a.Dv01:F4}");

double ytm = bond.YieldToMaturity(cleanPrice: 98.25, settle);
double zspread = bond.ZSpread(curve, 98.25, settle);
double zcb = ZeroCouponBond.Price(faceValue: 100, yield: 0.045, maturity: 7);
double frn = new FloatingRateNote(Maturity: 5, QuotedMargin: 0.01).Price(curve);
```

### Swaps, swaptions, forwards

```csharp
var swap = new InterestRateSwap(Notional: 10_000_000, FixedRate: 0.045, Maturity: 5, PayFixed: true);
Console.WriteLine($"NPV {swap.Npv(curve):N0}, par {swap.ParRate(curve):P3}, DV01 {swap.Dv01(curve):N0}");
// Dual-curve: swap.Npv(discountCurve, forecastCurve)

var swaption = new Swaption(Expiry: 1, Tenor: 5, Strike: 0.045, Payer: true, Notional: 10_000_000);
double black = swaption.PriceBlack(curve, volatility: 0.25);
double normal = swaption.PriceBachelier(curve, normalVolatility: 0.009);

double fwdPrice = Forwards.Price(spot: 100, rate: 0.05, time: 0.5, dividendYield: 0.02);
double fx = Forwards.FxForward(spot: 15_800, domesticRate: 0.06, foreignRate: 0.045, time: 0.25);   // e.g. USD/IDR
double repo = Forwards.ImpliedRepoRate(spot: 100, futures: 101.2, time: 0.25);
```

## Credit

```csharp
using Finance.Pricing.Credit;

var cds = new CreditDefaultSwap(Notional: 10_000_000, Spread: 0.012, Maturity: 5, Frequency: 4, Recovery: 0.4);
HazardCurve hazard = CreditDefaultSwap.BootstrapHazard(curve, new double[] { 1, 3, 5 }, new[] { 0.008, 0.010, 0.012 });
Console.WriteLine($"par {cds.ParSpread(curve, hazard):P3}, NPV {cds.Npv(curve, hazard):N0}, risky duration {cds.RiskyDuration(curve, hazard):F3}");
double pd5 = hazard.DefaultProbability(5);
double quick = CreditDefaultSwap.CreditTriangleHazard(0.012, 0.4);   // λ ≈ s / (1 − R)
```

## Short-rate models: Hull-White and G2++ (v0.2)

```csharp
var hw = GaussianShortRateModel.HullWhite(curve, meanReversion: 0.05, volatility: 0.01);
var g2 = GaussianShortRateModel.G2(curve, a: 0.5, sigma: 0.012, b: 0.05, eta: 0.008, rho: -0.6);

double zb   = hw.ZeroBond(t: 2, T: 7, state: new[] { 0.004 });      // P(t,T) given the factor state
double zbo  = hw.ZeroBondOption(OptionType.Call, expiry: 2, maturity: 7, strike: 0.83);
double cap  = g2.Cap(start: 1, maturity: 6, strike: 0.037, frequency: 4);

var swpn = new Swaption(Expiry: 1, Tenor: 5, Strike: 0.04, Payer: false);
double euro = hw.JamshidianSwaption(swpn);                          // one-factor closed form
MonteCarloResult berm = g2.BermudanSwaption(swpn, paths: 20_000);   // Longstaff-Schwartz LSMC
```

Both are one `GaussianShortRateModel` (Brigo-Mercurio Gn++): r = Σxᵢ + φ(t), fitted exactly to the
initial curve. The simulation is **exact**: it steps the factors and ∫r jointly from their Gaussian
covariance, so discount factors carry no time-step bias. Bermudan exercise values are the model's exact swap
values; continuation values are regressed on the swap value, its square and the factors.

## Local volatility (v0.2)

```csharp
var lv = new LocalVolatility(surface, spot: 100, rate: 0.03, dividendYield: 0.01);
double sigmaLoc = lv.Volatility(t: 0.75, s: 95);                    // Dupire, Gatheral total-variance form
MonteCarloResult put = lv.PriceEuropean(OptionType.Put, strike: 85, expiry: 1);
```

Under the local-vol diffusion every European reprices to its input implied vol; the tests check that by
Monte Carlo against Black-Scholes on a skewed smile. `Floored` counts evaluations where an arbitrageable
surface made the Dupire ratio non-positive.

## Inflation and cross-currency swaps (v0.2)

```csharp
var cpi = InflationCurve.FromZeroCouponSwaps(baseIndex: 310.2, new[] { 1.0, 2, 5, 10 }, new[] { 0.031, 0.028, 0.025, 0.024 });
var zcis = new ZeroCouponInflationSwap(10_000_000, FixedRate: 0.026, Maturity: 5);
var yoy  = new YearOnYearInflationSwap(10_000_000, FixedRate: 0.025, Maturity: 10);
double be = yoy.ParRate(discount, cpi);

var xccy = new CrossCurrencySwap(
    Domestic: new CrossCurrencyLeg(10_800_000, usdOis, usdSofr, Rate: 0),
    Foreign:  new CrossCurrencyLeg(10_000_000, eurDiscountInUsdCollateral, eurEstr, Rate: 0),
    Maturity: 5);
double basis = xccy.ParForeignSpread(spotFx: 1.08);                 // implied cross-currency basis
```

### YoY convexity

```csharp
var conv = new YoYConvexityModel(InflationVol: 0.012, NominalVol: 0.25, InflationNominalCorrelation: 0.4, InflationCorrelation: 0.9);
double beAdj = yoy.ParRate(discount, cpi, conv);
double ratio = yoy.ExpectedRatio(i: 5, discount, cpi, conv);       // E[I(t5)/I(t4)] under the t5-forward measure
```

`YoYConvexityModel` is the lognormal forward-CPI market model of Mercurio (2005) (Brigo-Mercurio, 2nd ed.,
§16.3): each forward CPI Iⱼ is lognormal with vol σ_I under its own Tⱼ-forward measure, the nominal forward Fᵢ
is lognormal with vol σ_N, and

E^{Tᵢ}[I(Tᵢ)/I(Tᵢ₋₁)] = Iᵢ(0)/Iᵢ₋₁(0) · exp(σ_I·Tᵢ₋₁·[ρ_{I,N}·σ_N·τFᵢ/(1+τFᵢ) − ρ_{I,I}·σ_I + σ_I]),

with ρ_{I,N} the CPI/nominal-rate correlation and ρ_{I,I} the correlation of consecutive forward CPIs (the nominal
forward is frozen at today's value in the drift). Nominal forwards come from the discount curve. Zero vols give
exactly the unadjusted price; the first period is never adjusted. Measured on the CPI curve above (the base level cancels in the ratios) with
`new YieldCurve({1,2,5,10}, {4.0%, 3.9%, 3.7%, 3.8%})` as discount: the 10Y annual YoY par rate moves from
241.697bp to 244.245bp (+2.55bp) with the parameters shown, and a 1mm 2.5% fixed payer from −6,786.89 to −4,704.35.

### Mark-to-market resetting cross-currency swaps

```csharp
var mtm = new MtmCrossCurrencySwap(
    Domestic: new CrossCurrencyLeg(0, usdOis, usdSofr, Rate: 0),          // resetting leg: notional set by the resets
    Foreign:  new CrossCurrencyLeg(10_000_000, eurDiscountInUsdCollateral, eurEstr, Rate: 0),
    Maturity: 5);                                                         // ResetDomestic: true (default)
double mtmBasis = mtm.ParForeignSpread(spotFx: 1.08);
foreach (var p in mtm.ResetSchedule(1.08))                                // forward FX, notional, exchange at p.End
    Console.WriteLine($"{p.Start:F2} {p.ForwardFx:F6} {p.ResettingNotional:N2} {p.NotionalExchange:N2}");
```

The resetting leg's notional for each period is the constant notional of the other leg converted at the forward
FX for the period start, X(t) = S·P_for(t)/P_dom(t), and the difference Nᵢ − Nᵢ₊₁ is exchanged at every reset
(Nₙ at maturity). On deterministic curves this is a strip of one-period constant-notional swaps, which is how the
tests check it. Measured with the USD curve above as `usdOis = usdSofr`, EUR `{1,5,10} → {2.5%, 2.6%, 2.8%}`
discounted 20bp lower, spot 1.08, per 1mm EUR: resets 1.080000, 1.084600, 1.089219, … 1.150043 (t = 4.75), exchanges
−4,599.77, −4,619.36, … and 1,150,043.29 at maturity. With zero basis on consistent curves the NPV is −3.1e-10. A
par-floater resetting leg (USD forecast = discount) gives exactly the constant-notional basis (−20.1253bp); with
the USD forecast 10bp over its discount curve the MtM basis is −10.0639bp against −10.3992bp constant-notional.

## Credit: ISDA-style CDS (v0.2)

```csharp
var cds = new IsdaCds(Notional: 10_000_000, Maturity: 5, Coupon: IsdaCds.InvestmentGradeCoupon, Recovery: 0.4);
double upfront = cds.UpfrontFromQuotedSpread(discount, quotedSpread: 0.0300);   // fraction of notional
double quoted  = cds.QuotedSpreadFromUpfront(discount, upfront);
double cs01    = cds.Cs01(discount, 0.0300);
```

The protection leg and accrual-on-default are integrated in closed form on every piece where hazard and
forward are constant, as in the ISDA CDS Standard Model; standard coupons and upfront ↔ quoted-spread
conversion go through a flat hazard curve.

### Dated CDS from a trade date and tenor (IMM schedule)

```csharp
var cds5y = IsdaCds.Standard(10_000_000, tradeDate: new DateOnly(2026, 10, 12), Tenor.Parse("5Y"),
    coupon: IsdaCds.InvestmentGradeCoupon, recovery: 0.4, calendar: BusinessCalendar.WeekendsOnly);
CdsSchedule s = cds5y.Schedule!;          // maturity, step-in, cash settlement, accrual periods
double points = cds5y.UpfrontFromQuotedSpread(discount, 0.0300);     // clean upfront at cash settlement
var hz = HazardCurve.Flat(cds5y.FlatHazardFromQuotedSpread(discount, 0.0300));
double cash = cds5y.CashSettlement(discount, hz);                     // N·(points − accrued)
DateOnly m = CdsDates.StandardMaturity(new DateOnly(2016, 3, 21), Tenor.Parse("5Y"));   // 2021-06-20
```

Conventions (ISDA CDS Standard Model, post-2009):

- **Roll dates** are the 20th of Mar/Jun/Sep/Dec (`CdsDates.NextImmDate` / `PreviousImmDate`).
- **Maturity** (`CdsDates.StandardMaturity`, QuantLib's `cdsMaturity` rule): anchor on the last IMM date on or
  before the trade date, then add tenor + 3M. With `CdsRollConvention.SemiAnnual2015` (default, since 20 Dec 2015)
  a June/December anchor moves back three months, so maturities roll only on 20 Mar and 20 Sep: trades from
  20 Mar to 19 Sep mature on 20 Jun, from 20 Sep to 19 Mar on 20 Dec. `Quarterly` is the pre-2015 rule.
- **Step-in** T+1 (calendar), **cash settlement** T+3 business days.
- **Accrual start**: the latest adjusted IMM date on or before step-in (full first coupon; the accrued, from
  accrual start to T inclusive, is rebated at settlement). If a weekend IMM date adjusts past step-in the previous
  IMM date is used.
- **Coupon dates** adjusted Following; Act/360 accrual; the last period accrues to maturity inclusive (end =
  unadjusted maturity + 1 day) and pays on the adjusted maturity.
- Protection runs from the trade date to maturity inclusive; model times are Act/365F from the trade date;
  survival for coupons and accrual-on-default is observed one day before each accrual date, as in the ISDA code.
- `Upfront` is the clean price at cash settlement, (protection − c·RPV01)/P(t_cs) + c·accrued; `ParSpread` divides
  by the clean RPV01, so a contract quoted at its coupon has zero points and settles −N·c·accrued.

Measured: traded Mon 12 Oct 2026, the 5Y matures 20 Dec 2031; accrual starts Mon 21 Sep 2026 (20 Sep is a
Sunday), 22 days accrued, cash settlement 15 Oct 2026, 21 periods (first 21 Sep → 21 Dec 2026, 91 days; last
22 Sep → 21 Dec 2031, 90 days, paid Mon 22 Dec 2031). On the log-linear curve `{1,2,5,10} → {4.0%, 3.9%, 3.7%, 3.8%}`
at a 300bp quote with a 100bp coupon, 10mm: points upfront 8.4057%, accrued 6,111.11, cash settlement
834,462.42, CS01 3,861.88. The year-fraction `new IsdaCds(10_000_000, 5)` gives 8.1563%, since the dated contract
runs 5.19 years.

## Structured products

```csharp
using Finance.Pricing.Structured;

var rc = new ReverseConvertible(FaceValue: 1000, CouponRate: 0.10, Maturity: 1, Strike: 100);
double rcValue = rc.Price(spot: 100, rate: 0.04, dividendYield: 0.01, volatility: 0.3);

var cpn = new CapitalProtectedNote(FaceValue: 1000, Maturity: 5, ProtectionLevel: 1.0, Participation: 0.6);
double cpnValue = cpn.Price(100, 0.04, 0.02, 0.2);

var auto = new AutocallableNote(1000, ObservationTimes: new[] { 0.5, 1.0, 1.5, 2.0 }, CouponPerPeriod: 0.04,
    AutocallLevel: 1.0, ProtectionBarrier: 0.6);
AutocallableResult res = auto.Price(spot: 100, rate: 0.04, dividendYield: 0.01, volatility: 0.25);
Console.WriteLine($"{res.Price:F2} ± {res.StandardError:F2}, P(loss) {res.CapitalLossProbability:P1}, expected life {res.ExpectedLife:F2}y");
```

`ReverseConvertible.FairCoupon(...)` and `CapitalProtectedNote.FairParticipation(...)` solve for
the term that prices the note at par.

## Notes and limitations

- Time is in **years**; rates are continuously compounded unless a method says otherwise (bond
  yields use street convention at the coupon frequency).
- The cubic-zero curve bootstrap iterates over all pillars, because a spline is non-local: adding a
  pillar moves the earlier segments.
- `CreditDefaultSwap` is a plain hazard-rate model on a weekly grid. `IsdaCds` follows the ISDA Standard
  Model's mathematics (exact integrals, flat forwards between curve nodes, standard coupons, upfront and
  CS01); its positional form uses year fractions, `IsdaCds.Standard` uses the dated IMM schedule with T+1 step-in
  and T+3 settlement. Holidays are those of the calendar passed in (weekends only by default); the 2009
  accrual-on-default "Markit fix" variant is not offered.
- Year-on-year convexity is optional (`YoYConvexityModel`, flat vols and correlations, nominal forward frozen
  in the drift); inflation curves have no seasonality or indexation lag. MtM cross-currency resets use
  deterministic forward FX (no FX/rate correlation convexity).
- Bermudan LSMC prices are slightly low-biased (sub-optimal exercise from a regressed rule), as with any
  Longstaff-Schwartz estimator.
- Tests check against Hull, Haug and Fang-Oosterlee reference values, put-call parity, finite
  differences and Monte Carlo vs closed form.

---

Made by Gravicode Studios, led by Kang Fadhil
