namespace CarpeMomentum.Adapter.Tws;

// Aggregates IBKR's fixed 5-second realtime bars (the only granularity
// reqRealTimeBars supports) into a configurable target resolution.
//
// Stateful — one instance per active subscription.
//
// Bucket boundary = floor(unixTime / bucketSeconds) * bucketSeconds.
// When a new 5-sec bar arrives in a NEW bucket, the previous bucket is
// considered closed and returned as `completed`. The in-progress current
// bucket is always returned so callers that opt into partial-bar emission
// can publish sub-bucket updates.
//
// Why per-instance state instead of a shared service: each subscription
// has its own (ticker, resolution) and IBKR delivers 5-sec bars per-
// reqId, so aggregation is naturally per-subscription.
internal sealed class BarAggregator
{
    private readonly int _bucketSeconds;

    private long? _bucketStart;
    private double _open;
    private double _high;
    private double _low;
    private double _close;
    private long _volume;
    private double _vwapWeightedSum;  // Σ(WAP_i × Volume_i)

    public BarAggregator(int bucketSeconds)
    {
        if (bucketSeconds < 5 || bucketSeconds % 5 != 0)
        {
            throw new ArgumentException(
                "bucketSeconds must be a positive multiple of 5 (IBKR realtime bar period).",
                nameof(bucketSeconds));
        }
        _bucketSeconds = bucketSeconds;
    }

    public int BucketSeconds => _bucketSeconds;

    public readonly struct AggregatedBar
    {
        public long UnixStart { get; init; }
        public double Open { get; init; }
        public double High { get; init; }
        public double Low { get; init; }
        public double Close { get; init; }
        public long Volume { get; init; }
        public double Vwap { get; init; }
    }

    // Process a new 5-sec bar from IBKR.
    // Returns:
    //  • completed: a bar that just closed (because this 5-sec bar
    //    started a new bucket). null if we're still inside the same bucket.
    //  • current:  snapshot of the in-progress bucket (always non-null
    //    after the first call).
    public (AggregatedBar? Completed, AggregatedBar Current) AddBar(
        long unixTime,
        double open, double high, double low, double close,
        decimal volume, decimal wap)
    {
        var bucket = (unixTime / _bucketSeconds) * _bucketSeconds;
        AggregatedBar? completed = null;

        if (_bucketStart is null)
        {
            StartBucket(bucket, open, high, low, close, volume, wap);
        }
        else if (bucket != _bucketStart.Value)
        {
            // Bucket crossover — snapshot the closing bucket, start fresh.
            completed = Snapshot();
            StartBucket(bucket, open, high, low, close, volume, wap);
        }
        else
        {
            // Same bucket — update.
            if (high > _high) _high = high;
            if (low < _low) _low = low;
            _close = close;
            _volume += (long)volume;
            _vwapWeightedSum += (double)wap * (double)volume;
        }

        return (completed, Snapshot());
    }

    private void StartBucket(long bucketStart, double open, double high, double low, double close,
        decimal volume, decimal wap)
    {
        _bucketStart = bucketStart;
        _open = open;
        _high = high;
        _low = low;
        _close = close;
        _volume = (long)volume;
        _vwapWeightedSum = (double)wap * (double)volume;
    }

    private AggregatedBar Snapshot() => new()
    {
        UnixStart = _bucketStart!.Value,
        Open = _open,
        High = _high,
        Low = _low,
        Close = _close,
        Volume = _volume,
        // No-volume periods (e.g. illiquid pre-market) — fall back to close.
        Vwap = _volume > 0 ? _vwapWeightedSum / _volume : _close,
    };
}
