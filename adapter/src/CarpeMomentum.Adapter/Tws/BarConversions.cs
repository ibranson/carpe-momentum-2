using Google.Protobuf.WellKnownTypes;
using ProtoBar = CarpeMomentum.Proto.V1.Bar;
using ProtoBarResolution = CarpeMomentum.Proto.V1.BarResolution;

namespace CarpeMomentum.Adapter.Tws;

// Helpers for translating between IBKR's IBApi types and our protobuf
// types. Lives in its own file because the conversion table is read-
// heavy and keeps the subscription classes uncluttered.
internal static class BarConversions
{
    // Convert an IBApi.Bar into a Proto.Bar.
    // Assumes IBKR was invoked with formatDate=2, which delivers
    // bar.Time as an epoch-seconds string (uniform for intraday + daily).
    public static ProtoBar ToProtoBar(
        string ticker,
        ProtoBarResolution resolution,
        IBApi.Bar bar,
        bool isPartial)
    {
        if (!long.TryParse(bar.Time, out var epochSeconds))
        {
            throw new InvalidOperationException(
                $"Unexpected IBKR bar time format (expected epoch seconds with formatDate=2): {bar.Time}");
        }

        return new ProtoBar
        {
            Ticker = ticker,
            OpenTime = Timestamp.FromDateTime(
                DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime),
            Resolution = resolution,
            OpenMicros = ToMicros(bar.Open),
            HighMicros = ToMicros(bar.High),
            LowMicros = ToMicros(bar.Low),
            CloseMicros = ToMicros(bar.Close),
            Volume = (long)bar.Volume,
            VwapMicros = ToMicros((double)bar.WAP),
            IsPartial = isPartial,
        };
    }

    // IBKR's barSizeSetting strings. Values must match exactly — IBKR
    // rejects anything not on this list. Source:
    //   https://interactivebrokers.github.io/tws-api/historical_bars.html
    public static string ToIbkrBarSize(ProtoBarResolution resolution) => resolution switch
    {
        ProtoBarResolution.Seconds10 => "10 secs",
        ProtoBarResolution.Minute1 => "1 min",
        ProtoBarResolution.Minute5 => "5 mins",
        ProtoBarResolution.Minute15 => "15 mins",
        ProtoBarResolution.Minute30 => "30 mins",
        ProtoBarResolution.Hour1 => "1 hour",
        ProtoBarResolution.Day1 => "1 day",
        _ => throw new ArgumentOutOfRangeException(
            nameof(resolution), resolution, "Unsupported BarResolution"),
    };

    // IBKR's durationStr expresses how far BACK from endDateTime to fetch.
    // Format: "<N> <unit>" where unit ∈ {S, D, W, M, Y}.
    // Pick the smallest unit that fits cleanly to maximize IBKR's
    // tolerance for sub-day windows.
    //
    // IBKR enforces upper limits per resolution (e.g. ~1 day of 10-sec
    // bars per call). For Phase 1 v1 we send what's asked; callers are
    // expected to chunk longer ranges themselves.
    public static string ComputeDuration(DateTime from, DateTime to, ProtoBarResolution resolution)
    {
        var span = to - from;
        if (span <= TimeSpan.Zero)
        {
            // Defensive minimum so IBKR doesn't reject the request.
            return "60 S";
        }

        // Sub-minute resolutions: use seconds (max ~86400 = 1 day).
        if (resolution == ProtoBarResolution.Seconds10)
        {
            var s = Math.Min((int)Math.Ceiling(span.TotalSeconds), 86_400);
            return $"{s} S";
        }

        // Minute-scale resolutions: prefer seconds for spans < 1 day,
        // days otherwise.
        if (resolution is ProtoBarResolution.Minute1
                       or ProtoBarResolution.Minute5
                       or ProtoBarResolution.Minute15
                       or ProtoBarResolution.Minute30
                       or ProtoBarResolution.Hour1)
        {
            if (span <= TimeSpan.FromDays(1))
            {
                return $"{(int)Math.Ceiling(span.TotalSeconds)} S";
            }
            var d = Math.Min((int)Math.Ceiling(span.TotalDays), 365);
            return $"{d} D";
        }

        // Daily: days up to a year, then years.
        if (resolution == ProtoBarResolution.Day1)
        {
            if (span <= TimeSpan.FromDays(365))
            {
                return $"{(int)Math.Ceiling(span.TotalDays)} D";
            }
            return $"{(int)Math.Ceiling(span.TotalDays / 365.0)} Y";
        }

        throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unsupported BarResolution");
    }

    // IBKR's endDateTime format: "yyyyMMdd HH:mm:ss" (UTC).
    // Pass an empty string when keepUpToDate=true (IBKR requires this).
    public static string FormatIbkrEndDateTime(DateTime endUtc) =>
        endUtc.ToUniversalTime().ToString("yyyyMMdd HH:mm:ss");

    private static long ToMicros(double price) =>
        (long)Math.Round(price * 1_000_000.0);
}
