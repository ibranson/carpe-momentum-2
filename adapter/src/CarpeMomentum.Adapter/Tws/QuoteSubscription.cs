using System.Threading.Channels;
using CarpeMomentum.Proto.V1;
using Google.Protobuf.WellKnownTypes;

namespace CarpeMomentum.Adapter.Tws;

// One active StreamQuotes subscription. Holds the accumulator state for
// a single ticker — IBKR delivers each tick field independently
// (tickPrice for prices, tickSize for sizes, tickGeneric for things like
// halted), so we merge them into a single QuoteUpdate before emitting.
//
// Each tick callback emits a QuoteUpdate containing ONLY the fields
// that changed in this tick (proto3 `optional` semantics) — the client
// merges into its own per-ticker state. This keeps wire volume down.
//
// IBKR tickPrice field codes (subset we map):
//   1 = BID,  2 = ASK,  4 = LAST,  6 = HIGH,  7 = LOW,  9 = CLOSE,  14 = OPEN
//
// tickSize field codes:
//   0 = BID_SIZE,  3 = ASK_SIZE,  5 = LAST_SIZE,  8 = VOLUME
//
// tickGeneric:
//   49 = HALTED (0/1)
internal sealed class QuoteSubscription
{
    private readonly string _ticker;
    private readonly ChannelWriter<QuoteUpdate> _writer;

    public QuoteSubscription(string ticker, ChannelWriter<QuoteUpdate> writer)
    {
        _ticker = ticker;
        _writer = writer;
    }

    public void OnTickPrice(int field, double price)
    {
        var update = new QuoteUpdate
        {
            Ticker = _ticker,
            Ts = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        var micros = ToMicros(price);
        switch (field)
        {
            case 1: update.BidMicros = micros; break;
            case 2: update.AskMicros = micros; break;
            case 4: update.LastMicros = micros; break;
            default: return;  // unmapped field — drop
        }

        _writer.TryWrite(update);
    }

    public void OnTickSize(int field, decimal size)
    {
        var update = new QuoteUpdate
        {
            Ticker = _ticker,
            Ts = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        var sizeLong = (long)size;
        switch (field)
        {
            case 0: update.BidSize = sizeLong; break;
            case 3: update.AskSize = sizeLong; break;
            case 5: update.LastSize = sizeLong; break;
            case 8: update.CumulativeVolume = sizeLong; break;
            default: return;
        }

        _writer.TryWrite(update);
    }

    public void OnTickGeneric(int field, double value)
    {
        // Reserved for HALTED (field 49) — Phase 1 surfaces this via
        // ScannerService.StreamHaltEvents, not QuoteUpdate. No-op for now.
        _ = field;
        _ = value;
    }

    public void OnError(int errorCode, string errorMsg)
    {
        // Could complete the channel with an exception, but for now we
        // log on the TwsConnection side and keep the subscription open;
        // many IBKR "errors" are transient (farm reconnects etc.).
        _ = errorCode;
        _ = errorMsg;
    }

    // Convert a double-precision price to int64 micros, rounding to
    // the nearest microcent. Prices outside the int64 range would
    // overflow but at $9.2 trillion micros = $9.2 quintillion a share,
    // this is not a real concern.
    private static long ToMicros(double price) =>
        (long)Math.Round(price * 1_000_000.0);
}
