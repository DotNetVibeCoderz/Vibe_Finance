using System.Globalization;

namespace Finance.Execution.Brokers;

/// <summary>
/// A TWS contract: the fields <c>placeOrder</c> sends ahead of the order. Use <see cref="Stock"/>,
/// <see cref="Forex"/> or <see cref="Future"/>; <see cref="ConId"/> (when known) lets TWS skip contract resolution.
/// </summary>
public sealed record TwsContract(string Symbol, string SecType, string Exchange, string Currency)
{
    public long ConId { get; init; }
    /// <summary>Futures expiry: <c>YYYYMM</c> (contract month) or <c>YYYYMMDD</c>.</summary>
    public string LastTradeDateOrContractMonth { get; init; } = "";
    public string Multiplier { get; init; } = "";
    public string PrimaryExchange { get; init; } = "";
    public string LocalSymbol { get; init; } = "";
    public string TradingClass { get; init; } = "";

    /// <summary>A stock (secType STK), SMART-routed by default.</summary>
    public static TwsContract Stock(string symbol, string exchange = "SMART", string currency = "USD", string primaryExchange = "")
        => new(symbol.ToUpperInvariant(), "STK", exchange, currency) { PrimaryExchange = primaryExchange };

    /// <summary>
    /// A currency pair (secType CASH on IDEALPRO): "EUR.USD", "EUR/USD" or "EURUSD" → symbol EUR, currency USD.
    /// </summary>
    public static TwsContract Forex(string pair, string exchange = "IDEALPRO")
    {
        var p = pair.Replace("/", ".").Replace("_", ".").Replace("-", ".").ToUpperInvariant();
        string[] parts = p.Contains('.') ? p.Split('.') : p.Length == 6 ? new[] { p[..3], p[3..] } : throw new ArgumentException($"Not a currency pair: {pair}.", nameof(pair));
        return new TwsContract(parts[0], "CASH", exchange, parts[1]);
    }

    /// <summary>A future (secType FUT) for an expiry <c>YYYYMM</c> / <c>YYYYMMDD</c>.</summary>
    public static TwsContract Future(string symbol, string lastTradeDateOrContractMonth, string exchange, string currency = "USD", string multiplier = "")
        => new(symbol.ToUpperInvariant(), "FUT", exchange, currency) { LastTradeDateOrContractMonth = lastTradeDateOrContractMonth, Multiplier = multiplier };
}

/// <summary>
/// The order part of <c>placeOrder</c>. <see cref="OrderType"/> is one of <c>MKT</c>, <c>LMT</c>, <c>STP</c>,
/// <c>STP LMT</c>; <see cref="LimitPrice"/> is TWS's lmtPrice and <see cref="AuxPrice"/> the stop price.
/// Every other placeOrder field is sent with its TWS API default.
/// </summary>
public sealed record TwsOrder(string Action, double TotalQuantity, string OrderType)
{
    public double? LimitPrice { get; init; }
    /// <summary>Stop (trigger) price of STP / STP LMT orders.</summary>
    public double? AuxPrice { get; init; }
    /// <summary>DAY, GTC or IOC.</summary>
    public string Tif { get; init; } = "DAY";
    /// <summary>Allow the order to trigger / fill outside regular trading hours.</summary>
    public bool OutsideRth { get; init; }
    /// <summary>Account to trade (required for multi-account logins).</summary>
    public string Account { get; init; } = "";
    public string OrderRef { get; init; } = "";
    /// <summary>False stages the order in TWS without transmitting it (bracket parents).</summary>
    public bool Transmit { get; init; } = true;
    public long ParentId { get; init; }
    /// <summary>Stop trigger method: 0 default, 1 double bid/ask, 2 last, 3 double last, 4 bid/ask, 7 last or bid/ask, 8 mid-point.</summary>
    public int TriggerMethod { get; init; }
    /// <summary>TWS default is true (dontUseAutoPriceForHedge).</summary>
    public bool DontUseAutoPriceForHedge { get; init; } = true;

    public static TwsOrder Market(string action, double quantity) => new(action, quantity, "MKT");
    public static TwsOrder Limit(string action, double quantity, double limit) => new(action, quantity, "LMT") { LimitPrice = limit };
    public static TwsOrder Stop(string action, double quantity, double stop) => new(action, quantity, "STP") { AuxPrice = stop };
    public static TwsOrder StopLimit(string action, double quantity, double stop, double limit) => new(action, quantity, "STP LMT") { AuxPrice = stop, LimitPrice = limit };
}

/// <summary>An orderStatus message (incoming id 3).</summary>
public sealed record TwsOrderStatus(long OrderId, string Status, double Filled, double Remaining, double AverageFillPrice, long PermId,
    long ParentId, double LastFillPrice, int ClientId, string WhyHeld, double MarketCapPrice);

/// <summary>The leading fields of an openOrder message (incoming id 5): contract, order basics and ids.</summary>
public sealed record TwsOpenOrder(long OrderId, TwsContract Contract, string Action, double TotalQuantity, string OrderType,
    double? LimitPrice, double? AuxPrice, string Tif, string Account, int ClientId, long PermId, bool OutsideRth);

/// <summary>An execDetails message (incoming id 11). <see cref="Side"/> is BOT or SLD.</summary>
public sealed record TwsExecution(int RequestId, long OrderId, long ConId, string Symbol, string SecType, string Currency, string ExecId,
    string Time, string Account, string Exchange, string Side, double Shares, double Price, long PermId, int ClientId, double CumulativeQuantity,
    double AveragePrice, string OrderRef);

/// <summary>A commissionReport message (incoming id 59) for an execution.</summary>
public sealed record TwsCommissionReport(string ExecId, double Commission, string Currency, double RealizedPnl);

/// <summary>
/// The <c>placeOrder</c> (outgoing id 3) field list, in the order of the TWS API's <c>EClient.placeOrder</c>, for the
/// server versions this client negotiates (100..<see cref="TwsClient.MaxClientVersion"/>; the reference target is
/// 176). Fields introduced after server version 100 are guarded by the version that introduced them; everything
/// older is always present. Combo (BAG), algo, scale, hedge, delta-neutral, condition and PEG fields are sent empty
/// (their "absent" encoding), so only stock, forex and future contracts with MKT / LMT / STP / STP LMT are supported.
/// </summary>
public static class TwsPlaceOrder
{
    // Server versions (TWS API server_versions) that change placeOrder / cancelOrder within 100..176.
    public const int FractionalPositions = 101, PeggedToBenchmark = 102, ModelsSupport = 103, ExtOperator = 105,
        SoftDollarTier = 106, CashQty = 111, DecisionMaker = 138, MifidExecution = 139, AutoPriceForHedge = 141,
        OrderContainer = 145, DPegOrders = 148, PriceMgmtAlgo = 151, Duration = 158, PostToAts = 160,
        AutoCancelParent = 162, AdvancedOrderReject = 166, ManualOrderTime = 169, PegBestPegMidOffsets = 170,
        FaProfileDesupport = 177;

    /// <summary>The message VERSION sent before server version <see cref="OrderContainer"/> (for servers ≥ NOT_HELD).</summary>
    public const int MessageVersion = 45;

    private static readonly string[] OrderTypes = { "MKT", "LMT", "STP", "STP LMT" };
    private static readonly string[] Tifs = { "DAY", "GTC", "IOC" };

    /// <summary>Builds the fields (message id first) of placeOrder for <paramref name="serverVersion"/>.</summary>
    public static object[] Fields(int serverVersion, long orderId, TwsContract c, TwsOrder o)
    {
        if (serverVersion < TwsClient.MinClientVersion || serverVersion > TwsClient.MaxClientVersion)
            throw new NotSupportedException($"placeOrder is encoded for server versions {TwsClient.MinClientVersion}..{TwsClient.MaxClientVersion}, not {serverVersion}.");
        if (c.SecType is not ("STK" or "CASH" or "FUT")) throw new NotSupportedException($"Security type {c.SecType} is not supported (STK, CASH, FUT).");
        if (Array.IndexOf(OrderTypes, o.OrderType) < 0) throw new NotSupportedException($"Order type {o.OrderType} is not supported (MKT, LMT, STP, STP LMT).");
        if (Array.IndexOf(Tifs, o.Tif) < 0) throw new NotSupportedException($"Time in force {o.Tif} is not supported (DAY, GTC, IOC).");
        if (o.Action is not ("BUY" or "SELL")) throw new ArgumentException("Action must be BUY or SELL.");
        if (!(o.TotalQuantity > 0)) throw new ArgumentOutOfRangeException(nameof(o), "Quantity must be positive.");
        if (o.OrderType is "LMT" or "STP LMT" && o.LimitPrice is null) throw new ArgumentException($"{o.OrderType} needs a limit price.");
        if (o.OrderType is "STP" or "STP LMT" && o.AuxPrice is null) throw new ArgumentException($"{o.OrderType} needs a stop (aux) price.");
        if (serverVersion < FractionalPositions && o.TotalQuantity != Math.Floor(o.TotalQuantity))
            throw new NotSupportedException($"Fractional quantities need server version {FractionalPositions}.");

        static object Max(double? v) => v is { } d ? d : "";      // sendMax: unset doubles travel as empty strings
        var f = new List<object> { 3 };                              // PLACE_ORDER
        if (serverVersion < OrderContainer) f.Add(MessageVersion);
        f.Add(orderId);

        // contract
        f.Add(c.ConId);
        f.Add(c.Symbol);
        f.Add(c.SecType);
        f.Add(c.LastTradeDateOrContractMonth);
        f.Add(0.0);                                                  // strike
        f.Add("");                                                   // right
        f.Add(c.Multiplier);
        f.Add(c.Exchange);
        f.Add(c.PrimaryExchange);
        f.Add(c.Currency);
        f.Add(c.LocalSymbol);
        f.Add(c.TradingClass);
        f.Add("");                                                   // secIdType
        f.Add("");                                                   // secId

        // main order fields
        f.Add(o.Action);
        f.Add(serverVersion >= FractionalPositions ? o.TotalQuantity.ToString("R", CultureInfo.InvariantCulture) : (object)(long)o.TotalQuantity);
        f.Add(o.OrderType);
        f.Add(Max(o.LimitPrice));
        f.Add(Max(o.AuxPrice));

        // extended order fields
        f.Add(o.Tif);
        f.Add("");                                                   // ocaGroup
        f.Add(o.Account);
        f.Add("");                                                   // openClose
        f.Add(0);                                                    // origin (customer)
        f.Add(o.OrderRef);
        f.Add(o.Transmit);
        f.Add(o.ParentId);
        f.Add(false);                                                // blockOrder
        f.Add(false);                                                // sweepToFill
        f.Add(0);                                                    // displaySize
        f.Add(o.TriggerMethod);
        f.Add(o.OutsideRth);
        f.Add(false);                                                // hidden
        // (BAG combo legs would go here)
        f.Add("");                                                   // sharesAllocation (deprecated)
        f.Add(0.0);                                                  // discretionaryAmt
        f.Add("");                                                   // goodAfterTime
        f.Add("");                                                   // goodTillDate
        f.Add("");                                                   // faGroup
        f.Add("");                                                   // faMethod
        f.Add("");                                                   // faPercentage
        if (serverVersion < FaProfileDesupport) f.Add("");           // faProfile
        if (serverVersion >= ModelsSupport) f.Add("");               // modelCode
        f.Add(0);                                                    // shortSaleSlot
        f.Add("");                                                   // designatedLocation
        f.Add(-1);                                                   // exemptCode
        f.Add(0);                                                    // ocaType
        f.Add("");                                                   // rule80A
        f.Add("");                                                   // settlingFirm
        f.Add(false);                                                // allOrNone
        f.Add("");                                                   // minQty
        f.Add("");                                                   // percentOffset
        f.Add(false);                                                // eTradeOnly (deprecated)
        f.Add(false);                                                // firmQuoteOnly (deprecated)
        f.Add("");                                                   // nbboPriceCap (deprecated)
        f.Add(0);                                                    // auctionStrategy
        f.Add("");                                                   // startingPrice
        f.Add("");                                                   // stockRefPrice
        f.Add("");                                                   // delta
        f.Add("");                                                   // stockRangeLower
        f.Add("");                                                   // stockRangeUpper
        f.Add(false);                                                // overridePercentageConstraints
        f.Add("");                                                   // volatility
        f.Add("");                                                   // volatilityType
        f.Add("");                                                   // deltaNeutralOrderType
        f.Add("");                                                   // deltaNeutralAuxPrice
        f.Add(false);                                                // continuousUpdate
        f.Add("");                                                   // referencePriceType
        f.Add("");                                                   // trailStopPrice
        f.Add("");                                                   // trailingPercent
        f.Add("");                                                   // scaleInitLevelSize
        f.Add("");                                                   // scaleSubsLevelSize
        f.Add("");                                                   // scalePriceIncrement
        f.Add("");                                                   // scaleTable
        f.Add("");                                                   // activeStartTime
        f.Add("");                                                   // activeStopTime
        f.Add("");                                                   // hedgeType
        f.Add(false);                                                // optOutSmartRouting
        f.Add("");                                                   // clearingAccount
        f.Add("");                                                   // clearingIntent
        f.Add(false);                                                // notHeld
        f.Add(false);                                                // deltaNeutralContract present
        f.Add("");                                                   // algoStrategy
        f.Add("");                                                   // algoId
        f.Add(false);                                                // whatIf
        f.Add("");                                                   // orderMiscOptions
        f.Add(false);                                                // solicited
        f.Add(false);                                                // randomizeSize
        f.Add(false);                                                // randomizePrice
        if (serverVersion >= PeggedToBenchmark)
        {
            f.Add(0);                                                // conditions count
            f.Add("");                                               // adjustedOrderType
            f.Add("");                                               // triggerPrice
            f.Add("");                                               // lmtPriceOffset
            f.Add("");                                               // adjustedStopPrice
            f.Add("");                                               // adjustedStopLimitPrice
            f.Add("");                                               // adjustedTrailingAmount
            f.Add(0);                                                // adjustableTrailingUnit
        }
        if (serverVersion >= ExtOperator) f.Add("");                 // extOperator
        if (serverVersion >= SoftDollarTier) { f.Add(""); f.Add(""); }   // softDollarTier name, value
        if (serverVersion >= CashQty) f.Add("");                     // cashQty
        if (serverVersion >= DecisionMaker) { f.Add(""); f.Add(""); }    // mifid2DecisionMaker, mifid2DecisionAlgo
        if (serverVersion >= MifidExecution) { f.Add(""); f.Add(""); }   // mifid2ExecutionTrader, mifid2ExecutionAlgo
        if (serverVersion >= AutoPriceForHedge) f.Add(o.DontUseAutoPriceForHedge);
        if (serverVersion >= OrderContainer) f.Add(false);           // isOmsContainer
        if (serverVersion >= DPegOrders) f.Add(false);               // discretionaryUpToLimitPrice
        if (serverVersion >= PriceMgmtAlgo) f.Add("");               // usePriceMgmtAlgo (unset)
        if (serverVersion >= Duration) f.Add("");                    // duration
        if (serverVersion >= PostToAts) f.Add("");                   // postToAts
        if (serverVersion >= AutoCancelParent) f.Add(false);         // autoCancelParent
        if (serverVersion >= AdvancedOrderReject) f.Add("");         // advancedErrorOverride
        if (serverVersion >= ManualOrderTime) f.Add("");             // manualOrderTime
        if (serverVersion >= PegBestPegMidOffsets && c.Exchange == "IBKRATS") f.Add("");   // minTradeQty (PEG BEST/MID offsets not used)
        return f.ToArray();
    }

    /// <summary>cancelOrder (outgoing id 4): VERSION 1, the order id, and manualOrderCancelTime from server version 169.</summary>
    public static object[] CancelFields(int serverVersion, long orderId)
        => serverVersion >= ManualOrderTime ? new object[] { 4, 1, orderId, "" } : new object[] { 4, 1, orderId };
}
