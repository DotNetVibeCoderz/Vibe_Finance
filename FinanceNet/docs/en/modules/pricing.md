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
  CS01) but uses year fractions instead of business-day-adjusted IMM dates, with no T+1 step-in or cash
  settlement lag.
- Year-on-year inflation swaps use forward index ratios without a convexity adjustment; inflation curves
  have no seasonality or indexation lag. Cross-currency swaps have constant notionals (no MtM resets).
- Bermudan LSMC prices are slightly low-biased (sub-optimal exercise from a regressed rule), as with any
  Longstaff-Schwartz estimator.
- Tests check against Hull, Haug and Fang-Oosterlee reference values, put-call parity, finite
  differences and Monte Carlo vs closed form.

---

Made by Gravicode Studios, led by Kang Fadhil
