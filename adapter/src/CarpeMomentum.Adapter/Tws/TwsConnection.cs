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

    // Active subscription state.
    private readonly ConcurrentDictionary<int, QuoteSubscription> _quoteSubscriptions = new();

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
