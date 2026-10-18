using WyckoffScanner.Models;

namespace WyckoffScanner.Analysis;

/// <summary>Array-based view of the quotes (doubles) plus a few derived series used by the detectors.</summary>
internal sealed class PriceSeries
{
    public int Count { get; }
    public DateOnly[] Dates { get; }
    public double[] Open { get; }
    public double[] High { get; }
    public double[] Low { get; }
    public double[] Close { get; }
    public double[] Volume { get; }

    /// <summary>True range as a fraction of the previous close (daily "price action size").</summary>
    public double[] TrPct { get; }
    /// <summary>Simple moving average of <see cref="TrPct"/> (smoothing bars).</summary>
    public double[] SmoothedTrPct { get; }

    public double MedianVolume { get; }
    public double MedianTrPct { get; }

    /// <summary>
    /// When <paramref name="mirror"/> is true the series is price-inverted (price' = 1/price): High' = 1/Low,
    /// Low' = 1/High, Open' = 1/Open, Close' = 1/Close. Bar indexes and volumes are unchanged, so a bullish
    /// accumulation detector run on the mirrored series finds the bearish distribution pattern in the original.
    /// All prices must be &gt; 0 (check with <see cref="AllPricesPositive"/>).
    /// </summary>
    public PriceSeries(IReadOnlyList<DailyQuote> quotes, int smoothingBars, bool mirror = false)
    {
        Count = quotes.Count;
        Dates = new DateOnly[Count];
        Open = new double[Count];
        High = new double[Count];
        Low = new double[Count];
        Close = new double[Count];
        Volume = new double[Count];
        TrPct = new double[Count];
        SmoothedTrPct = new double[Count];

        for (int i = 0; i < Count; i++)
        {
            var q = quotes[i];
            Dates[i] = q.TradeDate;
            double o = (double)q.OpenPrice, h = (double)q.HighPrice, l = (double)q.LowPrice, c = (double)q.ClosePrice;
            if (mirror)
            {
                Open[i] = 1.0 / o;
                High[i] = 1.0 / l;
                Low[i] = 1.0 / h;
                Close[i] = 1.0 / c;
            }
            else
            {
                Open[i] = o; High[i] = h; Low[i] = l; Close[i] = c;
            }
            Volume[i] = q.Volume;

            double prevClose = i == 0 ? Close[i] : Close[i - 1];
            double tr = Math.Max(High[i] - Low[i], Math.Max(Math.Abs(High[i] - prevClose), Math.Abs(Low[i] - prevClose)));
            TrPct[i] = prevClose > 0 ? tr / prevClose : 0;
        }

        int k = Math.Max(1, smoothingBars);
        for (int i = 0; i < Count; i++)
            SmoothedTrPct[i] = Mean(TrPct, Math.Max(0, i - k + 1), i);

        MedianVolume = Median(Volume);
        MedianTrPct = Median(TrPct);
    }

    public static bool AllPricesPositive(IReadOnlyList<DailyQuote> quotes)
    {
        foreach (var q in quotes)
            if (q.OpenPrice <= 0 || q.HighPrice <= 0 || q.LowPrice <= 0 || q.ClosePrice <= 0) return false;
        return true;
    }

    public static double Mean(double[] a, int start, int toInclusive)
    {
        if (toInclusive < start) return 0;
        double sum = 0;
        for (int i = start; i <= toInclusive; i++) sum += a[i];
        return sum / (toInclusive - start + 1);
    }

    public static double Median(double[] a)
    {
        if (a.Length == 0) return 0;
        var copy = (double[])a.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        return copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0;
    }

    /// <summary>Least-squares slope of y[start..toInclusive] against 0..n-1.</summary>
    public static double Slope(double[] y, int start, int toInclusive)
    {
        int n = toInclusive - start + 1;
        if (n < 2) return 0;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            double v = y[start + i];
            sx += i; sy += v; sxy += i * v; sxx += (double)i * i;
        }
        double den = n * sxx - sx * sx;
        return den == 0 ? 0 : (n * sxy - sx * sy) / den;
    }
}
