using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Analysis;

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
    /// <summary>Simple moving average of <see cref="Volume"/>; used to follow the gradual volume fade of a rounded climax.</summary>
    public double[] SmoothedVolume { get; }

    public double MedianVolume { get; }
    public double MedianTrPct { get; }

    /// <summary>
    /// When <paramref name="mirror"/> is true the series is price-inverted (price' = 1/price): High' = 1/Low,
    /// Low' = 1/High, Open' = 1/Open, Close' = 1/Close. Bar indexes and volumes are unchanged, so a bullish
    /// accumulation detector run on the mirrored series finds the bearish distribution pattern in the original.
    /// All prices must be &gt; 0 (check with <see cref="AllPricesPositive"/>).
    /// </summary>
    public PriceSeries(IReadOnlyList<DailyQuote> quotes, int smoothingBars, bool mirror = false, int volumeSmoothingBars = 5)
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
        SmoothedVolume = new double[Count];

        for (int i = 0; i < Count; i++)
        {
            var q = quotes[i];
            Dates[i] = q.TradeDate;
            double o = (double)q.OpenPrice, 
                h = (double)q.HighPrice, 
                l = (double)q.LowPrice, 
                c = (double)q.ClosePrice;
            
            if (mirror)
            {
                Open[i] = 1.0 / o;
                High[i] = 1.0 / l;
                Low[i] = 1.0 / h;
                Close[i] = 1.0 / c;
            }
            else
            {
                Open[i] = o; 
                High[i] = h; 
                Low[i] = l; 
                Close[i] = c;
            }
            Volume[i] = q.Volume;

            double prevClose = (i == 0 ? Close[i] : Close[i - 1]);
            double tr = Math.Max(High[i] - Low[i], Math.Max(Math.Abs(High[i] - prevClose), Math.Abs(Low[i] - prevClose)));
            TrPct[i] = (prevClose > 0 ? tr / prevClose : 0);
        }

        int k = Math.Max(1, smoothingBars);
        for (int i = 0; i < Count; i++)
            SmoothedTrPct[i] = Mean(TrPct, Math.Max(0, i - k + 1), i);

        int kv = Math.Max(1, volumeSmoothingBars);
        for (int i = 0; i < Count; i++)
            SmoothedVolume[i] = Mean(Volume, Math.Max(0, i - kv + 1), i);

        MedianVolume = Median(Volume);
        MedianTrPct = Median(TrPct);
    }

    public static bool AllPricesPositive(IReadOnlyList<DailyQuote> quotes)
    {
        foreach (var q in quotes)
        {
            if (q.OpenPrice <= 0 || q.HighPrice <= 0 || q.LowPrice <= 0 || q.ClosePrice <= 0) 
                return false;
        }

        return true;
    }

    public static double Mean(double[] a, int start, int toInclusive)
    {
        if (toInclusive < start) 
            return 0;
        double sum = 0;
        for (int i = start; i <= toInclusive; i++) 
            sum += a[i];
        
        return sum / (toInclusive - start + 1);
    }

    public static double Median(double[] a)
    {
        if (a.Length == 0) 
            return 0;
        var copy = (double[])a.Clone();
        Array.Sort(copy);
        
        int mid = copy.Length / 2;
        return (copy.Length % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) / 2.0);
    }

    /// <summary>Least-squares slope of y[start..toInclusive] against 0..n-1.</summary>
    public static double Slope(double[] y, int start, int toInclusive)
    {
        int n = toInclusive - start + 1;
        if (n < 2) 
            return 0;
        double sx = 0, 
            sy = 0, 
            sxy = 0, 
            sxx = 0;
        
        for (int i = 0; i < n; i++)
        {
            double v = y[start + i];
            sx += i; 
            sy += v; 
            sxy += i * v; 
            sxx += (double)i * i;
        }
        
        double den = n * sxx - sx * sx;
        
        return (den == 0 ? 0 : (n * sxy - sx * sy) / den);
    }

    /// <summary>
    /// Least-squares parabola y = c + b·x + a·x² through y[start..toInclusive] (x scaled to 0..1, y scaled by its mean
    /// so the result is independent of price level). <paramref name="a"/> &gt; 0 means a bowl (rounded bottom).
    /// <paramref name="vertexIndex"/> is the bar index of the parabola's turning point (may lie outside the window);
    /// <paramref name="r2"/> is the goodness of fit (1 = perfect).
    /// </summary>
    public static bool FitParabola(double[] y, int start, int toInclusive, out double a, out double vertexIndex, out double r2)
    {
        a = 0; vertexIndex = 0; r2 = 0;
        int n = toInclusive - start + 1;
        if (n < 5) 
            return false;

        double mean = Mean(y, start, toInclusive);
        if (mean <= 0) 
            return false;

        double s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / (n - 1);
            double x2 = x * x;
            double v = y[start + i] / mean;
            s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
            t0 += v; t1 += x * v; t2 += x2 * v;
        }

        double s0 = n;
        double det = Det3(s0, s1, s2, s1, s2, s3, s2, s3, s4);
        if (Math.Abs(det) < 1e-12) 
            return false;

        double c = Det3(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
        double b = Det3(s0, t0, s2, s1, t1, s3, s2, t2, s4) / det;
        double qa = Det3(s0, s1, t0, s1, s2, t1, s2, s3, t2) / det;
        if (Math.Abs(qa) < 1e-12) 
            return false;

        double meanV = t0 / n, ssRes = 0, ssTot = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / (n - 1);
            double v = y[start + i] / mean;
            double fit = c + b * x + qa * x * x;
            ssRes += (v - fit) * (v - fit);
            ssTot += (v - meanV) * (v - meanV);
        }

        r2 = ssTot > 0 ? 1.0 - ssRes / ssTot : 0;
        a = qa;
        vertexIndex = start + (-b / (2.0 * qa)) * (n - 1);
        return true;
    }

    private static double Det3(double a11, double a12, double a13,
                               double a21, double a22, double a23,
                               double a31, double a32, double a33) =>
        a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
}
