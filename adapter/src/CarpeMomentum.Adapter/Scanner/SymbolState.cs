using CarpeMomentum.Proto.V1;

namespace CarpeMomentum.Adapter.Scanner;

// Per-symbol live state accumulated from IBKR market data callbacks
// plus enrichment data fetched lazily (avg daily volume, prev close).
// One instance per actively-subscribed candidate; lifetime is tied to
// the symbol's presence in the scanner snapshot set.
//
// Thread-safety: all mutators use the internal lock. Field reads in
// `ToPillarInputs` snapshot a consistent view under the same lock.
internal sealed class SymbolState
{
    private readonly object _lock = new();

    public string Ticker { get; }

    // Live tick state (from QuoteUpdate via reqMktData).
    private long? _lastPriceMicros;
    private long? _bidMicros;
    private long? _askMicros;
    private long? _todayVolume;
    private DateTime _lastUpdateUtc;

    // Enrichment data (from one-shot historical bars fetch).
    private long? _prevCloseMicros;        // yesterday's close
    private long? _avgDailyVolume;          // 30-day average

    // Scanner-supplied data (refreshed each scanner snapshot).
    private double? _scannerPercentGain;    // from TOP_PERC_GAIN's `distance`

    public SymbolState(string ticker)
    {
        Ticker = ticker;
    }

    public void ApplyTick(QuoteUpdate update)
    {
        lock (_lock)
        {
            if (update.HasBidMicros) _bidMicros = update.BidMicros;
            if (update.HasAskMicros) _askMicros = update.AskMicros;
            if (update.HasLastMicros) _lastPriceMicros = update.LastMicros;
            if (update.HasCumulativeVolume) _todayVolume = update.CumulativeVolume;
            _lastUpdateUtc = DateTime.UtcNow;
        }
    }

    public void SetScannerPercentGain(double? percentGain)
    {
        lock (_lock) { _scannerPercentGain = percentGain; }
    }

    public void SetEnrichment(long? prevCloseMicros, long? avgDailyVolume)
    {
        lock (_lock)
        {
            if (prevCloseMicros.HasValue) _prevCloseMicros = prevCloseMicros;
            if (avgDailyVolume.HasValue) _avgDailyVolume = avgDailyVolume;
        }
    }

    public DateTime LastUpdateUtc
    {
        get { lock (_lock) { return _lastUpdateUtc; } }
    }

    // Build the PillarInputs snapshot for evaluator consumption.
    // Caller passes `nowUtc` to avoid re-reading DateTime.UtcNow inside
    // the lock (and to support deterministic testing later).
    public PillarInputs ToPillarInputs(DateTime nowUtc)
    {
        lock (_lock)
        {
            return new PillarInputs
            {
                Ticker = Ticker,
                LastPriceMicros = _lastPriceMicros,
                PercentGain = ComputePercentGainLocked(),
                ShareFloat = null,                       // not in this session
                Rvol = ComputeRvolLocked(nowUtc),
                Catalyst = null,                          // not in this session
            };
        }
    }

    // Prefer the scanner-supplied %gain (sourced from the TOP_PERC_GAIN
    // snapshot — IBKR's own computation). Fall back to computing from
    // live last vs prev close, which works once both are known.
    private double? ComputePercentGainLocked()
    {
        if (_scannerPercentGain.HasValue) return _scannerPercentGain;
        if (!_lastPriceMicros.HasValue || !_prevCloseMicros.HasValue) return null;
        if (_prevCloseMicros.Value <= 0) return null;
        return (double)(_lastPriceMicros.Value - _prevCloseMicros.Value)
             / _prevCloseMicros.Value;
    }

    // Time-of-day-adjusted RVOL — naive form:
    //   today_volume / (avg_daily_volume × session_fraction_elapsed)
    //
    // session_fraction is the share of the 6.5h regular session that
    // has elapsed at `nowUtc`. Before market open it's 0 (returns null
    // since the denominator is 0); after market close it's 1.0.
    //
    // For a more accurate RVOL, future work would track minute-by-minute
    // historical volume profiles and compare today's cumulative to the
    // average cumulative at this same time-of-day across N sessions.
    private double? ComputeRvolLocked(DateTime nowUtc)
    {
        if (!_todayVolume.HasValue || !_avgDailyVolume.HasValue) return null;
        if (_avgDailyVolume.Value <= 0) return null;

        var fraction = SessionFractionElapsed(nowUtc);
        if (fraction <= 0) return null;

        var expectedSoFar = _avgDailyVolume.Value * fraction;
        if (expectedSoFar <= 0) return null;
        return _todayVolume.Value / expectedSoFar;
    }

    // Fraction of US regular trading session (9:30am-4:00pm ET, 390 min)
    // that has elapsed. Clamped to [0, 1]. Weekend/holiday detection is
    // intentionally NOT done here — caller is responsible for not
    // requesting RVOL outside trading sessions.
    private static double SessionFractionElapsed(DateTime nowUtc)
    {
        // Use Windows-style time zone id; on Linux this would be
        // "America/New_York". TimeZoneInfo.FindSystemTimeZoneById can
        // be cross-platform via the CLDR alias map in recent .NET.
        TimeZoneInfo et;
        try
        {
            et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }

        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, et);
        var open = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 9, 30, 0, DateTimeKind.Unspecified);
        var close = new DateTime(nowEt.Year, nowEt.Month, nowEt.Day, 16, 0, 0, DateTimeKind.Unspecified);

        if (nowEt < open) return 0;
        if (nowEt >= close) return 1.0;

        var elapsed = (nowEt - open).TotalMinutes;
        return Math.Clamp(elapsed / 390.0, 0.0, 1.0);
    }
}
