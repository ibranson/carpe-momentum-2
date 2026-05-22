using System.Collections.Concurrent;
using System.Threading.Channels;
using CarpeMomentum.Adapter.Tws;
using CarpeMomentum.Proto.V1;
using Google.Protobuf.WellKnownTypes;

namespace CarpeMomentum.Adapter.Scanner;

// Orchestrates the StreamQualifyingSymbols pipeline.
//
// Inputs:  IBKR scanner snapshots (every ~30s) + per-candidate real-time
//          market data ticks + per-candidate one-shot historical bars.
// Output:  a channel of QualityUpdate messages, one per (symbol, eval),
//          debounced to at most one per symbol per ~500ms.
//
// Lifetime: one instance per active gRPC stream. Disposed when the
// stream cancels. The coordinator owns:
//
//   • one scanner subscription
//   • N market data subscriptions (one per candidate in current snapshot)
//   • N historical-bar fetches (one-shot per new candidate)
//   • per-symbol SymbolState
//
// Coordinating scanner snapshots vs live ticks: the scanner refresh
// drives which symbols are subscribed; live ticks drive the actual
// QualityUpdate emissions. Snapshot changes also trigger an immediate
// emission per symbol so the UI sees a row appear even before its
// first live tick arrives.
internal sealed class ScannerEnrichmentCoordinator : IAsyncDisposable
{
    private readonly TwsConnection _tws;
    private readonly PillarEvaluator _evaluator;
    private readonly ChannelWriter<QualityUpdate> _output;
    private readonly ILogger _logger;

    private readonly TimeSpan _minEmitInterval = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _avgVolLookback = TimeSpan.FromDays(45);  // ~30 trading days

    // Per-symbol state.
    private readonly ConcurrentDictionary<string, SymbolState> _states = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _symbolCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastEmitUtc = new();

    public ScannerEnrichmentCoordinator(
        TwsConnection tws,
        PillarEvaluator evaluator,
        ChannelWriter<QualityUpdate> output,
        ILogger logger)
    {
        _tws = tws;
        _evaluator = evaluator;
        _output = output;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Channel for scanner snapshots; one item per snapshot refresh.
        var scannerChannel = Channel.CreateUnbounded<IReadOnlyList<ScannerCandidate>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        // Channel for live tick updates; one item per QuoteUpdate.
        // All per-symbol subscriptions write to this single channel —
        // the consumer dispatches by `QuoteUpdate.Ticker`.
        var ticksChannel = Channel.CreateUnbounded<QuoteUpdate>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        await _tws.SubscribeScannerAsync(scannerChannel.Writer, ct);

        // Spawn the tick consumer.
        var tickConsumer = Task.Run(() => ConsumeTicksAsync(ticksChannel.Reader, ct), ct);

        try
        {
            await foreach (var snapshot in scannerChannel.Reader.ReadAllAsync(ct))
            {
                await ApplySnapshotAsync(snapshot, ticksChannel.Writer, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — gRPC stream cancelled.
        }
        finally
        {
            // Cancel all per-symbol subs so the IBKR side releases them.
            foreach (var cts in _symbolCts.Values) cts.Cancel();
            ticksChannel.Writer.TryComplete();
            try { await tickConsumer; } catch { /* swallow */ }
            _output.TryComplete();
        }
    }

    // Diff the current snapshot against active subscriptions.
    // Subscribe new symbols, unsubscribe those that fell out, refresh
    // scanner-supplied %gain on still-present ones.
    private async Task ApplySnapshotAsync(
        IReadOnlyList<ScannerCandidate> snapshot,
        ChannelWriter<QuoteUpdate> ticksWriter,
        CancellationToken ct)
    {
        var inSnapshot = new HashSet<string>(snapshot.Count);
        foreach (var c in snapshot) inSnapshot.Add(c.Ticker);

        // Subscribe new + refresh existing.
        foreach (var candidate in snapshot)
        {
            var state = _states.GetOrAdd(candidate.Ticker, t => new SymbolState(t));
            state.SetScannerPercentGain(candidate.PercentGain);

            if (!_symbolCts.ContainsKey(candidate.Ticker))
            {
                await SubscribeSymbolAsync(candidate.Ticker, ticksWriter, ct);
            }
        }

        // Unsubscribe falling-out symbols.
        // Note: no debounce here (a symbol that re-enters next snapshot
        // gets re-subscribed). For v1 the IBKR per-symbol churn cost is
        // acceptable; future optimization: hold dropped symbols for
        // one extra snapshot cycle before unsub.
        foreach (var ticker in _symbolCts.Keys)
        {
            if (!inSnapshot.Contains(ticker))
            {
                UnsubscribeSymbol(ticker);
            }
        }

        // Trigger an immediate emit per symbol so the UI sees the new
        // snapshot composition even before live ticks arrive.
        foreach (var candidate in snapshot)
        {
            await EmitIfWarrantedAsync(candidate.Ticker, ct);
        }

        _logger.LogDebug(
            "Snapshot applied: {Count} candidates, active subs={SubCount}",
            snapshot.Count, _symbolCts.Count);
    }

    private async Task SubscribeSymbolAsync(
        string ticker,
        ChannelWriter<QuoteUpdate> ticksWriter,
        CancellationToken ct)
    {
        var symCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_symbolCts.TryAdd(ticker, symCts))
        {
            symCts.Dispose();
            return;  // race — already subscribed
        }

        _logger.LogDebug("Subscribing market data for {Ticker}", ticker);

        // Live ticks. We use a shared writer for all symbols; the
        // consumer dispatches by Ticker.
        await _tws.SubscribeQuotesAsync(
            ticker,
            includeExtendedHours: true,
            ticksWriter,
            symCts.Token);

        // Fire-and-forget historical fetch for enrichment data.
        _ = FetchEnrichmentAsync(ticker, symCts.Token);
    }

    private void UnsubscribeSymbol(string ticker)
    {
        _logger.LogDebug("Unsubscribing market data for {Ticker}", ticker);
        if (_symbolCts.TryRemove(ticker, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        _states.TryRemove(ticker, out _);
        _lastEmitUtc.TryRemove(ticker, out _);
    }

    // One-shot fetch of ~30 daily bars per symbol. We extract:
    //   • avg daily volume (mean of bar volumes)
    //   • prev close (most recent bar's close, with a guard against
    //     IBKR returning today's in-progress bar)
    //
    // Failure is non-fatal — the symbol's RVOL and gain (when computed
    // from prev close) just stay null until a future retry.
    private async Task FetchEnrichmentAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var to = DateTime.UtcNow;
            var from = to - _avgVolLookback;
            var bars = await _tws.GetHistoricalBarsAsync(
                ticker, BarResolution.Day1, from, to, includeExtendedHours: false, ct);

            if (bars.Count == 0) return;

            long sumVolume = 0;
            foreach (var b in bars) sumVolume += b.Volume;
            var avgDailyVolume = sumVolume / bars.Count;

            // Prev close = most recent bar whose open_time is strictly
            // before today's date in UTC. Avoids picking up today's
            // partial bar if IBKR returns one.
            var todayUtc = DateTime.UtcNow.Date;
            long? prevCloseMicros = null;
            for (var i = bars.Count - 1; i >= 0; i--)
            {
                if (bars[i].OpenTime.ToDateTime() < todayUtc)
                {
                    prevCloseMicros = bars[i].CloseMicros;
                    break;
                }
            }

            if (_states.TryGetValue(ticker, out var state))
            {
                state.SetEnrichment(prevCloseMicros, avgDailyVolume);
            }

            _logger.LogDebug(
                "Enrichment for {Ticker}: avgDailyVol={AvgVol} prevClose={PrevClose}",
                ticker, avgDailyVolume, prevCloseMicros);
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enrichment fetch failed for {Ticker}", ticker);
        }
    }

    private async Task ConsumeTicksAsync(ChannelReader<QuoteUpdate> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var quote in reader.ReadAllAsync(ct))
            {
                if (_states.TryGetValue(quote.Ticker, out var state))
                {
                    state.ApplyTick(quote);
                    await EmitIfWarrantedAsync(quote.Ticker, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    // Debounced emission. Skips if we emitted for this ticker within
    // _minEmitInterval. TOCTOU race between concurrent callers is
    // acknowledged — worst case is an occasional duplicate emit, which
    // is harmless (idempotent client-side merge by ticker+ts).
    private async Task EmitIfWarrantedAsync(string ticker, CancellationToken ct)
    {
        if (!_states.TryGetValue(ticker, out var state)) return;

        var now = DateTime.UtcNow;
        if (_lastEmitUtc.TryGetValue(ticker, out var lastEmit)
            && now - lastEmit < _minEmitInterval)
        {
            return;
        }

        var inputs = state.ToPillarInputs(now);
        var eval = _evaluator.Evaluate(inputs);

        var update = new QualityUpdate
        {
            Ticker = ticker,
            Ts = Timestamp.FromDateTime(now),

            // Input echo (0 = unknown convention for these fields).
            LastPriceMicros = inputs.LastPriceMicros ?? 0,
            PercentGain = inputs.PercentGain ?? 0,
            ShareFloat = inputs.ShareFloat ?? 0,
            Rvol = inputs.Rvol ?? 0,

            // Per-pillar strengths (-1 = unknown sentinel).
            PriceStrength = eval.PriceStrength ?? -1,
            GainStrength = eval.GainStrength ?? -1,
            ShareFloatStrength = eval.ShareFloatStrength ?? -1,
            RvolStrength = eval.RvolStrength ?? -1,
            CatalystStrength = eval.CatalystStrength ?? -1,

            SetupQuality = eval.SetupQuality,
            Trend = Trend.Unspecified,
            QualityCrossover = false,
        };

        try
        {
            await _output.WriteAsync(update, ct);
            _lastEmitUtc[ticker] = now;
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (ChannelClosedException) { /* normal during shutdown */ }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var cts in _symbolCts.Values)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
        _symbolCts.Clear();
        _states.Clear();
        _lastEmitUtc.Clear();
        return ValueTask.CompletedTask;
    }
}
