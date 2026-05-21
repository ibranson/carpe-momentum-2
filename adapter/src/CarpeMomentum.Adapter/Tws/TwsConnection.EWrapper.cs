using IBApi;

namespace CarpeMomentum.Adapter.Tws;

// EWrapper implementation — IBKR delivers all asynchronous data via
// these 90 callback methods. We implement the full interface here for
// completeness, but only the methods marked "Phase 1" do meaningful
// work; everything else is a no-op or trace-log placeholder that
// future phases will replace.
//
// Method names use IBKR's camelCase convention (matching the interface),
// not C# PascalCase. SuppressMessage to silence the analyzer.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Style", "IDE1006:Naming Styles",
    Justification = "EWrapper interface uses camelCase per IBKR convention.")]
public sealed partial class TwsConnection
{
    // =====================================================================
    // Connection lifecycle — Phase 1
    // =====================================================================

    public void connectAck()
    {
        _logger.LogDebug("TWS connectAck (socket open; awaiting nextValidId)");
    }

    public void nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _nextValidOrderId, orderId);
        _logger.LogInformation("TWS connected. Next valid order id: {OrderId}", orderId);
        _connectedTcs?.TrySetResult(true);
    }

    public void managedAccounts(string accountsList)
    {
        _logger.LogInformation("TWS managed accounts: {Accounts}", accountsList);
    }

    public void connectionClosed()
    {
        _isConnected = false;
        _logger.LogWarning("TWS connection closed");
        // If a connect attempt is still pending, fail it so the
        // hosted service's retry loop can advance instead of hanging
        // on the 10s timeout. TrySet* is a no-op if already resolved.
        _connectedTcs?.TrySetException(
            new InvalidOperationException("TWS connection closed before auth completed"));
        // Auto-reconnect itself is wired up by TwsHostedService when AutoReconnect=true.
    }

    public void currentTime(long time) =>
        _logger.LogTrace("TWS currentTime {Time}", time);

    // =====================================================================
    // Errors — Phase 1
    // =====================================================================

    public void error(Exception e)
    {
        _logger.LogError(e, "TWS error (exception)");
        // IBApi's EClientSocket.eConnect catches socket failures and
        // dispatches them here instead of throwing back to our caller.
        // Surface the failure on the pending connect TCS so ConnectAsync
        // returns control to the retry loop without waiting for the timeout.
        _connectedTcs?.TrySetException(e);
    }

    public void error(string str)
    {
        _logger.LogError("TWS error: {Message}", str);
    }

    public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // Info codes 2100-2199 are non-fatal status messages from IBKR
        // (market data farm connection state, etc.). Logged quietly.
        var level = errorCode is >= 2100 and < 2200
            ? LogLevel.Debug
            : LogLevel.Warning;

        _logger.Log(level, "TWS [{Code}] (id={Id}) {Msg}", errorCode, id, errorMsg);

        // Route subscription-specific errors.
        if (id > 0 && _quoteSubscriptions.TryGetValue(id, out var sub))
        {
            sub.OnError(errorCode, errorMsg);
        }

        // Hard connect failures (502 = couldn't connect, 504 = not connected).
        if (errorCode is 502 or 504)
        {
            _connectedTcs?.TrySetException(
                new InvalidOperationException($"TWS connect failed ({errorCode}): {errorMsg}"));
        }
    }

    // =====================================================================
    // Tick data — Phase 1 (StreamQuotes)
    // =====================================================================

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (_quoteSubscriptions.TryGetValue(tickerId, out var sub))
        {
            sub.OnTickPrice(field, price);
        }
    }

    public void tickSize(int tickerId, int field, decimal size)
    {
        if (_quoteSubscriptions.TryGetValue(tickerId, out var sub))
        {
            sub.OnTickSize(field, (decimal)size);
        }
    }

    public void tickGeneric(int tickerId, int field, double value)
    {
        if (_quoteSubscriptions.TryGetValue(tickerId, out var sub))
        {
            sub.OnTickGeneric(field, value);
        }
    }

    public void tickString(int tickerId, int field, string value) =>
        _logger.LogTrace("tickString id={Id} field={Field} value={Value}", tickerId, field, value);

    public void tickSnapshotEnd(int tickerId) =>
        _logger.LogTrace("tickSnapshotEnd id={Id}", tickerId);

    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) =>
        _logger.LogTrace(
            "tickReqParams id={Id} minTick={MinTick} bbo={Bbo} perms={Perms}",
            tickerId, minTick, bboExchange, snapshotPermissions);

    public void marketDataType(int reqId, int marketDataType) =>
        _logger.LogTrace("marketDataType id={Id} type={Type}", reqId, marketDataType);

    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints,
        double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact,
        double dividendsToLastTradeDate) { /* not used in Phase 1 */ }

    public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility,
        double delta, double optPrice, double pvDividend, double gamma, double vega, double theta,
        double undPrice) { /* options — not in v1 scope */ }

    // =====================================================================
    // Account / Portfolio — Phase 1 (later sessions: OrderService)
    // =====================================================================

    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue,
        double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void position(string account, Contract contract, decimal pos, double avgCost) { }
    public void positionEnd() { }
    public void positionMulti(int requestId, string account, string modelCode, Contract contract, decimal pos, double avgCost) { }
    public void positionMultiEnd(int requestId) { }
    public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int requestId) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }

    // =====================================================================
    // Orders — Phase 1 (later sessions: OrderService)
    // =====================================================================

    public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice,
        int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }

    // =====================================================================
    // Contract details — Phase 1 (later sessions: SymbolMetadata)
    // =====================================================================

    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId,
        string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }

    // =====================================================================
    // Historical data — to be wired up in next session (GetHistoricalBars)
    // =====================================================================

    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone,
        HistoricalSession[] sessions) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }

    // =====================================================================
    // Real-time bars — next session (StreamRealTimeBars)
    // =====================================================================

    public void realtimeBar(int reqId, long date, double open, double high, double low, double close,
        decimal volume, decimal WAP, int count) { }

    // =====================================================================
    // Market depth (Level 2) — later session (StreamLevel2)
    // =====================================================================

    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side,
        double price, decimal size, bool isSmartDepth) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }

    // =====================================================================
    // Tick-by-tick — later session (high-frequency tape)
    // =====================================================================

    public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
        TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize,
        decimal askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }

    // =====================================================================
    // News — later session (NewsService)
    // =====================================================================

    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId,
        string headline, string extraData) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }

    // =====================================================================
    // Scanner — later session (ScannerService)
    // =====================================================================

    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance,
        string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }

    // =====================================================================
    // Fundamental / metadata — not in v1 scope
    // =====================================================================

    public void fundamentalData(int reqId, string data) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void replaceFAEnd(int reqId, string text) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void userInfo(int reqId, string whiteBrandingId) { }
}
