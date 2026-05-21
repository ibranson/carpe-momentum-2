using System.Collections.Concurrent;
using System.Threading.Channels;
using CarpeMomentum.Proto.V1;
using IBApi;
using Microsoft.Extensions.Options;

namespace CarpeMomentum.Adapter.Tws;

// Wraps the IBKR IBApi EClientSocket + EReader threading model and
// exposes typed, async-friendly methods for gRPC services to call.
//
// Singleton — IBKR allows only ONE connection per (host, port, clientId)
// tuple. Multiple gRPC clients all share this single TwsConnection.
//
// The EWrapper interface (90 callback methods) is implemented in the
// partial class file TwsConnection.EWrapper.cs. Only the methods we
// actually consume in Phase 1 do meaningful work; the rest are no-ops
// or trace-log placeholders for later phases.
public sealed partial class TwsConnection : EWrapper, IDisposable
{
    private readonly TwsConnectionOptions _options;
    private readonly ILogger<TwsConnection> _logger;
    private readonly EReaderMonitorSignal _signal;
    private readonly EClientSocket _client;

    // Active subscription state — keyed by IBKR reqId so EWrapper
    // callbacks can route to the right consumer.
    private readonly ConcurrentDictionary<int, QuoteSubscription> _quoteSubscriptions = new();
    private readonly ConcurrentDictionary<int, BarsSubscription> _barSubscriptions = new();
    private readonly ConcurrentDictionary<int, AggregatedBarsSubscription> _realtimeBarSubscriptions = new();
    private readonly ConcurrentDictionary<int, ScannerSnapshotSubscription> _scannerSubscriptions = new();

    private EReader? _reader;
    private Thread? _readerThread;

    private int _nextRequestId;
    private int _nextValidOrderId;
    private volatile bool _isConnected;
    private TaskCompletionSource<bool>? _connectedTcs;

    public TwsConnection(IOptions<TwsConnectionOptions> options, ILogger<TwsConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);
    }

    public bool IsConnected => _isConnected;
    public int NextValidOrderId => Volatile.Read(ref _nextValidOrderId);

    // Establish the TWS socket and start the reader thread.
    // Returns once IBKR sends back nextValidId (the auth-complete signal).
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_isConnected)
        {
            _logger.LogDebug("ConnectAsync called while already connected");
            return;
        }

        _logger.LogInformation(
            "Connecting to TWS at {Host}:{Port} as clientId {ClientId}",
            _options.Host, _options.Port, _options.ClientId);

        _connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _client.eConnect(_options.Host, _options.Port, _options.ClientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "eConnect threw — is TWS running on {Host}:{Port}?", _options.Host, _options.Port);
            throw;
        }

        _reader = new EReader(_client, _signal);
        _reader.Start();

        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "TWS-Reader",
        };
        _readerThread.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));

        try
        {
            await _connectedTcs.Task.WaitAsync(timeoutCts.Token);
            _isConnected = true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "TWS did not send nextValidId within {Seconds}s — connection assumed dead",
                _options.ConnectTimeoutSeconds);
            try { _client.eDisconnect(); } catch { /* swallow during cleanup */ }
            throw new TimeoutException("TWS connect timed out");
        }
    }

    public Task DisconnectAsync()
    {
        if (!_isConnected) return Task.CompletedTask;

        _logger.LogInformation("Disconnecting from TWS");
        _isConnected = false;
        try { _client.eDisconnect(); } catch (Exception ex) { _logger.LogDebug(ex, "eDisconnect threw"); }
        _readerThread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    // Subscribe to real-time quotes for a single ticker. Tick callbacks
    // are accumulated into QuoteUpdate messages and written to the
    // supplied channel. Cancellation of `ct` cancels the IBKR subscription
    // and removes the subscription state.
    public Task SubscribeQuotesAsync(
        string ticker,
        bool includeExtendedHours,
        ChannelWriter<QuoteUpdate> writer,
        CancellationToken ct)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("TWS is not connected.");
        }

        var reqId = Interlocked.Increment(ref _nextRequestId);
        var contract = BuildUsStockContract(ticker);
        var subscription = new QuoteSubscription(ticker, writer);

        _quoteSubscriptions[reqId] = subscription;

        // genericTickList: 233 = RTVolume, 236 = halted. Comma-separated.
        // For Phase 1 v1 we keep this empty (just the base tick stream).
        var genericTickList = "";

        _logger.LogDebug("reqMktData id={ReqId} ticker={Ticker} extHrs={Ext}", reqId, ticker, includeExtendedHours);

        ct.Register(() =>
        {
            _logger.LogDebug("Cancelling reqMktData id={ReqId} ticker={Ticker}", reqId, ticker);
            try { _client.cancelMktData(reqId); } catch { /* socket may already be closed */ }
            _quoteSubscriptions.TryRemove(reqId, out _);
        });

        _client.reqMktData(
            tickerId: reqId,
            contract: contract,
            genericTickList: genericTickList,
            snapshot: false,
            regulatorySnaphsot: false,
            mktDataOptions: null);

        return Task.CompletedTask;
    }

    // One-shot historical bars fetch. Awaits IBKR's historicalData/
    // historicalDataEnd callback pair and returns the full list.
    //
    // IBKR enforces per-resolution data limits (e.g. ~1 day of 10-sec
    // bars per call, ~1 month of 1-min bars). Larger ranges return a
    // truncated result rather than an error.
    public Task<IReadOnlyList<Proto.V1.Bar>> GetHistoricalBarsAsync(
        string ticker,
        Proto.V1.BarResolution resolution,
        DateTime from,
        DateTime to,
        bool includeExtendedHours,
        CancellationToken ct)
    {
        if (!_isConnected) throw new InvalidOperationException("TWS is not connected.");

        var reqId = Interlocked.Increment(ref _nextRequestId);
        var contract = BuildUsStockContract(ticker);
        var subscription = BarsSubscription.ForHistoricalOneShot(ticker, resolution);
        _barSubscriptions[reqId] = subscription;

        var endDateTime = BarConversions.FormatIbkrEndDateTime(to);
        var duration = BarConversions.ComputeDuration(from, to, resolution);
        var barSize = BarConversions.ToIbkrBarSize(resolution);

        _logger.LogDebug(
            "reqHistoricalData (one-shot) id={ReqId} ticker={Ticker} barSize={BarSize} duration={Duration} end={End}",
            reqId, ticker, barSize, duration, endDateTime);

        ct.Register(() =>
        {
            try { _client.cancelHistoricalData(reqId); } catch { /* swallow */ }
            _barSubscriptions.TryRemove(reqId, out _);
        });

        _client.reqHistoricalData(
            tickerId: reqId,
            contract: contract,
            endDateTime: endDateTime,
            durationStr: duration,
            barSizeSetting: barSize,
            whatToShow: "TRADES",
            useRTH: includeExtendedHours ? 0 : 1,
            formatDate: 2,         // epoch seconds — uniform parsing
            keepUpToDate: false,
            chartOptions: null);

        return subscription.CompletionTask!
            .ContinueWith(t =>
            {
                _barSubscriptions.TryRemove(reqId, out _);
                return t.Result;
            }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Live bar stream. Dispatches based on resolution:
    //   • 10s through 1h → reqRealTimeBars + adapter-side aggregation.
    //     Supports extended hours.
    //   • Day1 → reqHistoricalData(keepUpToDate=true). RTH only (daily
    //     bars are session-bounded anyway, so the limitation doesn't bite).
    public Task SubscribeRealTimeBarsAsync(
        string ticker,
        Proto.V1.BarResolution resolution,
        bool emitPartialBars,
        bool includeExtendedHours,
        ChannelWriter<Proto.V1.Bar> writer,
        CancellationToken ct)
    {
        if (!_isConnected) throw new InvalidOperationException("TWS is not connected.");

        if (resolution == Proto.V1.BarResolution.Day1)
        {
            return SubscribeRealTimeBarsViaHistoricalAsync(ticker, resolution, emitPartialBars, writer, ct);
        }

        return SubscribeRealTimeBarsViaAggregationAsync(
            ticker, resolution, emitPartialBars, includeExtendedHours, writer, ct);
    }

    // 10s through 1h: IBKR delivers 5-second realtime bars via the
    // `realtimeBar` EWrapper callback (extended-hours capable). The
    // adapter buckets them into the target resolution.
    private Task SubscribeRealTimeBarsViaAggregationAsync(
        string ticker,
        Proto.V1.BarResolution resolution,
        bool emitPartialBars,
        bool includeExtendedHours,
        ChannelWriter<Proto.V1.Bar> writer,
        CancellationToken ct)
    {
        var reqId = Interlocked.Increment(ref _nextRequestId);
        var contract = BuildUsStockContract(ticker);
        var subscription = new AggregatedBarsSubscription(ticker, resolution, writer, emitPartialBars);
        _realtimeBarSubscriptions[reqId] = subscription;

        _logger.LogDebug(
            "reqRealTimeBars id={ReqId} ticker={Ticker} resolution={Resolution} extHours={Ext}",
            reqId, ticker, resolution, includeExtendedHours);

        ct.Register(() =>
        {
            try { _client.cancelRealTimeBars(reqId); } catch { /* swallow */ }
            _realtimeBarSubscriptions.TryRemove(reqId, out _);
        });

        _client.reqRealTimeBars(
            tickerId: reqId,
            contract: contract,
            barSize: 5,                  // only valid value for reqRealTimeBars
            whatToShow: "TRADES",
            useRTH: !includeExtendedHours,   // bool here, NOT int (cf. reqHistoricalData)
            realTimeBarsOptions: null);

        return Task.CompletedTask;
    }

    // Day1: aggregating 17280 five-second bars per session is silly, so
    // use IBKR's native daily bars via reqHistoricalData(keepUpToDate=true).
    // Note IBKR enforces useRTH=1 here — extended hours flag is ignored.
    private Task SubscribeRealTimeBarsViaHistoricalAsync(
        string ticker,
        Proto.V1.BarResolution resolution,
        bool emitPartialBars,
        ChannelWriter<Proto.V1.Bar> writer,
        CancellationToken ct)
    {
        var reqId = Interlocked.Increment(ref _nextRequestId);
        var contract = BuildUsStockContract(ticker);
        var subscription = BarsSubscription.ForLiveStream(ticker, resolution, writer, emitPartialBars);
        _barSubscriptions[reqId] = subscription;

        var barSize = BarConversions.ToIbkrBarSize(resolution);
        _logger.LogDebug(
            "reqHistoricalData (keepUpToDate) id={ReqId} ticker={Ticker} barSize={BarSize}",
            reqId, ticker, barSize);

        ct.Register(() =>
        {
            try { _client.cancelHistoricalData(reqId); } catch { /* swallow */ }
            _barSubscriptions.TryRemove(reqId, out _);
        });

        _client.reqHistoricalData(
            tickerId: reqId,
            contract: contract,
            endDateTime: "",
            durationStr: "5 D",          // 5-day seed for daily bars
            barSizeSetting: barSize,
            whatToShow: "TRADES",
            useRTH: 1,
            formatDate: 2,
            keepUpToDate: true,
            chartOptions: null);

        return Task.CompletedTask;
    }

    // Subscribe to an IBKR market scanner. The default scan is
    // TOP_PERC_GAIN with Ross-Cameron price/volume filters applied — this
    // narrows the universe to symbols the 5-Pillar evaluator might pass.
    //
    // IBKR re-runs the scan periodically (~30s) and the cycle delivers
    // a fresh batch of scannerData callbacks followed by scannerDataEnd.
    // Each batch arrives on the writer as a snapshot list.
    public Task SubscribeScannerAsync(
        ChannelWriter<IReadOnlyList<Scanner.ScannerCandidate>> writer,
        CancellationToken ct)
    {
        if (!_isConnected) throw new InvalidOperationException("TWS is not connected.");

        var reqId = Interlocked.Increment(ref _nextRequestId);
        var subscription = new ScannerSnapshotSubscription(writer);
        _scannerSubscriptions[reqId] = subscription;

        var scannerSub = new IBApi.ScannerSubscription
        {
            Instrument = "STK",
            LocationCode = "STK.US.MAJOR",   // major US exchanges (NYSE, NASDAQ, AMEX)
            ScanCode = "TOP_PERC_GAIN",
            NumberOfRows = 50,

            // Ross-Cameron base filters (also enforced by the evaluator,
            // but pre-filtering at the scanner reduces noise).
            AbovePrice = 1.0,
            BelowPrice = 20.0,
            AboveVolume = 1_000_000,
        };

        _logger.LogInformation(
            "reqScannerSubscription id={ReqId} scan=TOP_PERC_GAIN rows={Rows}",
            reqId, scannerSub.NumberOfRows);

        ct.Register(() =>
        {
            try { _client.cancelScannerSubscription(reqId); } catch { /* swallow */ }
            _scannerSubscriptions.TryRemove(reqId, out _);
        });

        _client.reqScannerSubscription(
            reqId,
            scannerSub,
            scannerSubscriptionOptions: (List<TagValue>?)null,
            scannerSubscriptionFilterOptions: (List<TagValue>?)null);
        return Task.CompletedTask;
    }

    // Construct a Contract for a US-listed common stock. SMART routing
    // lets IBKR pick the venue; for OTC, a future overload should set
    // Exchange=OTC and PrimaryExch as appropriate.
    private static Contract BuildUsStockContract(string ticker) => new()
    {
        Symbol = ticker.Trim().ToUpperInvariant(),
        SecType = "STK",
        Exchange = "SMART",
        Currency = "USD",
    };

    private void ReaderLoop()
    {
        while (_client.IsConnected())
        {
            _signal.waitForSignal();
            try
            {
                _reader?.processMsgs();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TWS reader processMsgs threw");
            }
        }
        _logger.LogDebug("TWS reader thread exiting");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
