using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Rule-based detector for both halves of the Wyckoff cycle.
///
///   Accumulation: selling exhaustion -> automatic rally   -> accumulation box(es) -> spring    -> sign of strength
///   Distribution: buying climax      -> automatic reaction -> distribution box(es) -> upthrust  -> sign of weakness
///
/// Both run through the same <see cref="PatternEngine"/>. Distribution is the reverse polarity: the engine is run on a
/// price-inverted copy of the series (see <see cref="PriceSeries"/>), so no detection rule is duplicated. If both
/// patterns are present the one whose latest event is more recent is reported.
///
/// Thread-safe: <see cref="Analyze"/> keeps all state in locals, so it can be called concurrently for different symbols.
/// All thresholds live in <see cref="AnalyzerOptions"/>.
/// </summary>
public sealed class WyckoffAnalyzer
{
    private readonly AnalyzerOptions _o;
    private readonly PatternEngine _accumulation;
    private readonly PatternEngine _distribution;

    public WyckoffAnalyzer(IOptions<AnalyzerOptions> options) 
    {
        _o = options.Value;
        _accumulation = new PatternEngine(_o);
        _distribution = new PatternEngine(_o.ForDistribution());
    }

    public AnalysisResult Analyze(string ticker, IReadOnlyList<DailyQuote> quotes)
    {
        var result = new AnalysisResult
        {
            Ticker = ticker, 
            BarsAnalyzed = quotes.Count
        };

        if (quotes.Count < _o.MinBars)
        {
            result.Details = $"Only {quotes.Count} bars available (minimum {_o.MinBars}).";
            return result;
        }

        var s = new PriceSeries(quotes, _o.SmoothingBars);
        result.LastClose = s.Close[^1];
        result.LastDate = s.Dates[^1];

        if (s.MedianVolume <= 0 || s.MedianTrPct <= 0)
        {
            result.Details = "Volume or price range data is empty/flat; cannot analyse.";
            return result;
        }

        Candidate? acc = _accumulation.FindBest(s);

        Candidate? dist = null;
        if (_o.DetectDistribution && PriceSeries.AllPricesPositive(quotes))
        {
            var mirrored = new PriceSeries(quotes, _o.SmoothingBars, mirror: true);
            if (mirrored.MedianVolume > 0 && mirrored.MedianTrPct > 0)
                dist = _distribution.FindBest(mirrored);
        }

        if (acc == null && dist == null)
        {
            result.Details = _o.DetectDistribution
                ? "No selling-exhaustion or buying-climax pattern found."
                : "No selling-exhaustion pattern found.";
            return result;
        }

        bool isDistribution = PreferDistribution(acc, dist);
        var best = (isDistribution ? dist! : acc!);

        result.Pattern = (isDistribution ? WyckoffPattern.Distribution : WyckoffPattern.Accumulation);
        result.Flag = (isDistribution && best.Flag != WyckoffFlag.None ? (WyckoffFlag)((int)best.Flag + 3) : best.Flag);
        result.SellingExhaustion = true;
        result.AutomaticRally = best.Rally != null;
        result.Accumulation = best.Zones.Count > 0;
        result.AccumulationCount = best.Zones.Count;
        result.Spring = best.Spring != null;
        result.SignOfStrength = best.Sos != null;

        if (!isDistribution)
        {
            if (best.Spring != null)
            {
                result.Support = best.Spring.Support;
                result.Resistance = best.Spring.Resistance;
            }
            else if (best.Zones.Count > 0)
            {
                result.Support = best.Zones.Min(z => z.Low);
                result.Resistance = best.Zones.Max(z => z.High);
            }
            result.Details = BuildAccumulationDetails(s, best);
        }
        else
        {
            // The candidate lives in inverted prices: support in the original = 1 / inverted resistance, and vice versa.
            if (best.Spring != null)
            {
                result.Support = 1.0 / best.Spring.Resistance;
                result.Resistance = 1.0 / best.Spring.Support;
            }
            else if (best.Zones.Count > 0)
            {
                result.Support = 1.0 / best.Zones.Max(z => z.High);
                result.Resistance = 1.0 / best.Zones.Min(z => z.Low);
            }
            result.Details = BuildDistributionDetails(s, best);
        }

        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // Choosing between the two patterns
    // ------------------------------------------------------------------------------------------------

    private static bool PreferDistribution(Candidate? acc, Candidate? dist)
    {
        if (dist == null) 
            return false;
        if (acc == null) 
            return true;

        bool accFlagged = acc.Flag != WyckoffFlag.None;
        bool distFlagged = dist.Flag != WyckoffFlag.None;
        if (accFlagged != distFlagged) 
            return distFlagged;

        if (accFlagged)
        {
            int ra = Recency(acc), rd = Recency(dist);
            if (ra != rd) 
                return rd > ra;            // the more recent structure describes where price is now
            return dist.Flag > acc.Flag;
        }

        return dist.Rank > acc.Rank;                 // neither flagged: report whichever progressed further
    }

    /// <summary>Index of the latest event of the candidate (SoS/SoW, else spring/upthrust, else last box).</summary>
    private static int Recency(Candidate c)
    {
        if (c.Sos != null) 
            return c.Sos.Index;
        if (c.Spring != null) 
            return c.Spring.Index;
        if (c.Zones.Count > 0) 
            return c.Zones[^1].End;
        return c.Exhaustion.LowIndex;
    }

    // ------------------------------------------------------------------------------------------------
    // Human-readable explanations
    // ------------------------------------------------------------------------------------------------

    private static string F(FormattableString fs) => fs.ToString(CultureInfo.InvariantCulture);

    private static string BuildAccumulationDetails(PriceSeries s, Candidate c)
    {
        var sb = new StringBuilder();
        var ex = c.Exhaustion;

        sb.Append("[Accumulation] ");
        sb.Append(F($"Selling exhaustion: fell {ex.DeclinePct:P1} from {s.High[ex.HighIndex]:F2} ({s.Dates[ex.HighIndex]:yyyy-MM-dd}) "));
        sb.Append(F($"to a low of {s.Low[ex.LowIndex]:F2} ({s.Dates[ex.LowIndex]:yyyy-MM-dd}); daily swings eased from "));
        sb.Append(F($"{ex.PeakIntensity:P1} to {ex.EndIntensity:P1}, climax volume {ex.ClimaxVolumeRatio:F1}x median. "));

        if (c.Rally != null)
            sb.Append(F($"Automatic rally: +{c.Rally.RallyPct:P1} to {c.Rally.High:F2} ({s.Dates[c.Rally.Index]:yyyy-MM-dd}) on {c.Rally.VolumeRatio:F2}x median volume. "));

        if (c.Zones.Count > 0)
        {
            sb.Append(F($"Accumulation boxes: {c.Zones.Count} ("));
            sb.Append(string.Join("; ", c.Zones.Select(z =>
                F($"{s.Dates[z.Start]:yyyy-MM-dd}..{s.Dates[z.End]:yyyy-MM-dd} {z.Low:F2}-{z.High:F2}, vol {z.VolumeRatio:F2}x"))));
            sb.Append("). ");
        }

        if (c.Spring != null)
            sb.Append(F($"Spring: {s.Dates[c.Spring.Index]:yyyy-MM-dd} low {c.Spring.SpringLow:F2} under support {c.Spring.Support:F2} (resistance {c.Spring.Resistance:F2}), recovered {s.Dates[c.Spring.RecoveryIndex]:yyyy-MM-dd}. "));

        if (c.Sos != null)
            sb.Append(F($"Sign of strength: {s.Dates[c.Sos.Index]:yyyy-MM-dd} close above {c.Sos.Resistance:F2} on {c.Sos.VolumeRatio:F1}x average volume. "));

        sb.Append(F($"Last close {s.Close[^1]:F2} ({s.Dates[^1]:yyyy-MM-dd})."));
        return sb.ToString();
    }

    /// <summary>
    /// Describes a distribution candidate. The candidate's price levels are in inverted space (1/price), so they are
    /// converted back; bar indexes are identical in both series, so dates and original prices come from <paramref name="s"/>.
    /// </summary>
    private static string BuildDistributionDetails(PriceSeries s, Candidate c)
    {
        var sb = new StringBuilder();
        var ex = c.Exhaustion;

        int startIdx = ex.HighIndex;      // inverted high  = lowest low before the run-up
        int climaxIdx = ex.LowIndex;      // inverted low   = highest high (buying climax)
        double startPrice = s.Low[startIdx];
        double climaxHigh = s.High[climaxIdx];
        double advance = climaxHigh / startPrice - 1.0;

        sb.Append("[Distribution] ");
        sb.Append(F($"Buying climax: rose {advance:P1} from {startPrice:F2} ({s.Dates[startIdx]:yyyy-MM-dd}) "));
        sb.Append(F($"to a high of {climaxHigh:F2} ({s.Dates[climaxIdx]:yyyy-MM-dd}); daily swings eased from "));
        sb.Append(F($"{ex.PeakIntensity:P1} to {ex.EndIntensity:P1}, climax volume {ex.ClimaxVolumeRatio:F1}x median. "));

        if (c.Rally != null)
        {
            double reactionLow = s.Low[c.Rally.Index];
            double reactionPct = (climaxHigh - reactionLow) / climaxHigh;
            sb.Append(F($"Automatic reaction: -{reactionPct:P1} to {reactionLow:F2} ({s.Dates[c.Rally.Index]:yyyy-MM-dd}) on {c.Rally.VolumeRatio:F2}x median volume. "));
        }

        if (c.Zones.Count > 0)
        {
            sb.Append(F($"Distribution boxes: {c.Zones.Count} ("));
            sb.Append(string.Join("; ", c.Zones.Select(z =>
                F($"{s.Dates[z.Start]:yyyy-MM-dd}..{s.Dates[z.End]:yyyy-MM-dd} {1.0 / z.High:F2}-{1.0 / z.Low:F2}, vol {z.VolumeRatio:F2}x"))));
            sb.Append("). ");
        }

        if (c.Spring != null)
        {
            double resistance = 1.0 / c.Spring.Support;
            double support = 1.0 / c.Spring.Resistance;
            double utHigh = 1.0 / c.Spring.SpringLow;
            sb.Append(F($"Upthrust: {s.Dates[c.Spring.Index]:yyyy-MM-dd} high {utHigh:F2} above resistance {resistance:F2} (support {support:F2}), closed back below {s.Dates[c.Spring.RecoveryIndex]:yyyy-MM-dd}. "));
        }

        if (c.Sos != null)
            sb.Append(F($"Sign of weakness: {s.Dates[c.Sos.Index]:yyyy-MM-dd} close below {1.0 / c.Sos.Resistance:F2} on {c.Sos.VolumeRatio:F1}x average volume. "));

        sb.Append(F($"Last close {s.Close[^1]:F2} ({s.Dates[^1]:yyyy-MM-dd})."));
        return sb.ToString();
    }
}
