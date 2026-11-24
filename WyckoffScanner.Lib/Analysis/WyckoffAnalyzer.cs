using System;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Digests;
using WyckoffScanner.Lib.Models;

namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Rule-based detector for both halves of the Wyckoff cycle.
///
///   Accumulation: selling exhaustion -> automatic rally   -> accumulation box(es) -> spring    -> sign of strength
///   Distribution: buying climax      -> automatic reaction -> distribution box(es) -> upthrust  -> sign of weakness
///
/// Each climax can be SHARP (violent range expansion, quick bounce/reaction) or ROUNDED (volume fades gradually and
/// price traces a saucer at the bottom / dome at the top). Both styles are detected; the style is reported in
/// <see cref="AnalysisResult.Details"/>.
///
/// Both halves run through the same <see cref="PatternEngine"/>. Distribution is the reverse polarity: the engine is run on a
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
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value ?? throw new ArgumentException(
            "Analyzer options must be configured.", nameof(options));
        _o = configured.Clone();
        _o.Validate();

        // PatternEngine is stateless; the two instances safely share immutable-in-practice
        // configuration after construction and can be used concurrently by Analyze calls.
        _accumulation = new PatternEngine(_o);
        _distribution = new PatternEngine(_o.ForDistribution());
    }

    public AnalysisResult Analyze(string ticker, IReadOnlyList<DailyQuote> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);

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

        var s = new PriceSeries(quotes, _o.SmoothingBars, volumeSmoothingBars: _o.VolumeSmoothingBars);
        result.LastClose = s.Close[^1];
        result.LastDate = s.Dates[^1];

        if (s.MedianVolume <= 0 || s.MedianTrPct <= 0)
        {
            result.Details = "Volume or price range data is empty/flat; cannot analyse.";
            return result;
        }

        // Pivot-based climax candidates are confirmed only after PivotWindow bars to the
        // right (or RoundedPivotWindow for rounded lows). This is deliberate for historical
        // confirmation, but callers should not interpret the pivot date as a real-time signal
        // available on that same date.
        Candidate? acc = _accumulation.FindBest(s);

        Candidate? dist = null;
        if (_o.DetectDistribution && PriceSeries.AllPricesPositive(quotes))
        {
            var mirrored = new PriceSeries(quotes, _o.SmoothingBars, mirror: true, volumeSmoothingBars: _o.VolumeSmoothingBars);
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

        double priceRangeLow = best.Zones.Count > 0 
            ? (isDistribution
                ? best.Zones.Min(x => 1.0 / x.High)
                : best.Zones.Min(x => x.Low)
            ) : 0.0;
        double priceRangeHigh = best.Zones.Count > 0 
            ? (isDistribution
                ? best.Zones.Max(x => 1.0 / x.Low)
                : best.Zones.Max(x => x.High)
            ) : 0.0;
        double breakoutRange = best.Sos != null
            ? s.High[best.Sos.Index] - s.Low[best.Sos.Index]
            : 0.0;
        double breakoutCloseLoc = breakoutRange > 0.0
            ? (isDistribution
                ? (s.High[best.Sos?.Index ?? 0] - s.Close[best.Sos?.Index ?? 0]) / (breakoutRange + 0.0001)
                : (s.Close[best.Sos?.Index ?? 0] - s.Low[best.Sos?.Index ?? 0]) / (breakoutRange + 0.0001)
            )
            : 0.5;
        
        
        // Call the new probability scorer logic
        var scoreInput = new WyckoffScoreInput
        {
            Direction = isDistribution ? WyckoffDirection.Distribution : WyckoffDirection.Accumulation,

            ClimaxDetected = best.ClimaxConfirmed,    //*** true,
            ClimaxMovePct = best.Exhaustion.DeclinePct,
            ClimaxVolumeMultiple = best.Exhaustion.ClimaxVolumeRatio,
            //*** ClimaxIntensityMultiple = best.Exhaustion.PeakIntensity / s.MedianTrPct,
            ClimaxIntensityMultiple = best.ClimaxConfirmed ? best.Exhaustion.PeakIntensity / s.MedianTrPct : 0.0,
            ExhaustionFadeRatio = best.Exhaustion.EndIntensity / (best.Exhaustion.PeakIntensity + 0.0001),
            ClimaxAgeBars = s.Count - 1 - best.Exhaustion.LowIndex,

            RallyDetected = best.Rally != null,
            RallyRetraceRatio = best.Rally?.RetraceOfDecline ?? 0.0,
            RallyVolumeMultiple = best.Rally?.VolumeRatio ?? 0.0,
            RallyAgeBars = best.Rally != null
                ? s.Count - 1 - best.Rally?.Index ?? 0
                : 0,

            TradingRangeDetected = best.Zones.Any(),
            TradingRangeDurationBars = best.Zones.Count > 0
                ? best.Zones.Max(x => x.End) - best.Zones.Min(x => x.Start) + 1
                : 0,
            TradingRangeWidthPct = best.Zones.Count > 0
                ? (best.Zones.Max(x => x.High) - best.Zones.Min(x => x.Low)) / best.Zones.Min(x => x.Low)
                : 0,
            TradingRangeVolumeMultiple = best.Zones.Count > 0 
                ? best.Zones.Average(x => x.VolumeRatio)
                : 0,
            TradingRangeAgeBars = best.Zones.Count > 0
                ? s.Count - 1 - best.Zones.Max(x => x.End)
                : 0,
            PriceWithinOrNearRange = 
                s.Close[^1] >= priceRangeLow * (1.0 - _o.PriceRangeTolerance) &&
                s.Close[^1] <= priceRangeHigh * (1.0 + _o.PriceRangeTolerance),

            SpringOrUpthrustDetected = best.Spring != null,
            SpringPiercePct = best.Spring != null
                ? Math.Max(0, (best.Spring.Support - best.Spring.SpringLow) / (best.Spring.Support + 0.0001))
                : 0,
            SpringRecoveryBars = best.Spring != null
                ? best.Spring.RecoveryIndex - best.Spring.Index
                : 0,
            SpringAgeBars = best.Spring != null
                ? s.Count - 1 - best.Spring.Index
                : 0,
            SpringRecovered = best.Spring != null && 
                              (isDistribution 
                              ? s.Close[best.Spring.RecoveryIndex] < 1.0 / (best.Spring.Support + 0.0001)
                              : s.Close[best.Spring.RecoveryIndex] > best.Spring.Support),
            StructureStillHolds = best.Spring != null &&
                                  SpringStructureStillHolds(
                                      s,
                                      best.Spring,
                                      isDistribution,
                                      _o.SpringStructureCloseTolerancePct,
                                      _o.SpringStructureIntradayTolerancePct),

            BreakoutDetected = best.Sos != null,
            BreakoutVolumeMultiple = best.Sos?.VolumeRatio ?? 0.0,
            BreakoutCloseLocation = breakoutCloseLoc,
            BreakoutAgeBars = best.Sos != null ? s.Count - 1 - best.Sos?.Index ?? 0 : 0,
            BreakoutStillHolding = best.Sos != null &&
                                   IsBreakoutStillHolding(
                                       s,
                                       best.Sos.Index,
                                       best.Sos.Resistance,
                                       isDistribution,
                                       _o.BreakoutHoldingTolerancePct),
            
            RoundedFitR2 = best.Exhaustion.RoundnessR2,
            RoundedVolumeFadeRatio = best.Exhaustion.VolumeFadeRatio
        };

        result.Pattern = (isDistribution ? WyckoffPattern.Distribution : WyckoffPattern.Accumulation);
        result.Flag = (isDistribution && best.Flag != WyckoffFlag.None ? (WyckoffFlag)((int)best.Flag + 3) : best.Flag);
        result.SellingExhaustion = best.ClimaxConfirmed;   //*** true;
        result.AutomaticRally = best.Rally != null;
        result.Accumulation = best.Zones.Count > 0;
        result.AccumulationCount = best.Zones.Count;
        result.Spring = best.Spring != null;
        result.SignOfStrength = best.Sos != null;
        result.ProbabilityScore = WyckoffProbabilityScorer.Score(scoreInput);

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

    /// <summary>Sentence fragment describing how the climax behaved (sharp swings vs. rounded volume fade), shared by both directions.</summary>
    private static string ClimaxBehaviour(SellingExhaustion ex) =>
        ex.Style == ClimaxStyle.Rounded
            ? F($"rounded, decelerating move; volume peaked at {ex.ClimaxVolumeRatio:F1}x median and faded to {ex.VolumeFadeRatio:P0} of that peak into the turn (bowl fit R2 {ex.RoundnessR2:F2}). ")
            : F($"daily swings eased from {ex.PeakIntensity:P1} to {ex.EndIntensity:P1}, climax volume {ex.ClimaxVolumeRatio:F1}x median. ");

    private static string BuildAccumulationDetails(PriceSeries s, Candidate c)
    {
        var sb = new StringBuilder();
        var ex = c.Exhaustion;

        //*** IMPROVEMENT:
        /*
        sb.Append("[Accumulation] ");
        sb.Append(F($"Selling exhaustion ({(ex.Style == ClimaxStyle.Rounded ? "rounded" : "sharp")}): fell {ex.DeclinePct:P1} from {s.High[ex.HighIndex]:F2} ({s.Dates[ex.HighIndex]:yyyy-MM-dd}) "));
        sb.Append(F($"to a low of {s.Low[ex.LowIndex]:F2} ({s.Dates[ex.LowIndex]:yyyy-MM-dd}); "));
        sb.Append(ClimaxBehaviour(ex));
        */
        sb.Append(c.ClimaxConfirmed ? "[Accumulation] " : "[Developing accumulation] ");
        if (c.ClimaxConfirmed)
        {
            sb.Append(F($"Selling exhaustion ({(ex.Style == ClimaxStyle.Rounded ? "rounded" : "sharp")}): fell {ex.DeclinePct:P1} from {s.High[ex.HighIndex]:F2} ({s.Dates[ex.HighIndex]:yyyy-MM-dd}) "));
            sb.Append(F($"to a low of {s.Low[ex.LowIndex]:F2} ({s.Dates[ex.LowIndex]:yyyy-MM-dd}); "));
            sb.Append(ClimaxBehaviour(ex));
        }
        else 
            sb.Append("Trading range detected independently of a confirmed selling climax/automatic rally; treat as a developing candidate. ");
        //***

        if (c.Rally != null)
        {
            string label = c.Rally.Gradual ? "Gradual recovery" : "Automatic rally";
            sb.Append(F($"{label}: +{c.Rally.RallyPct:P1} to {c.Rally.High:F2} ({s.Dates[c.Rally.Index]:yyyy-MM-dd}) on {c.Rally.VolumeRatio:F2}x median volume. "));
        }

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

        //*** IMPROVEMENT:
        /*
        sb.Append("[Distribution] ");
        sb.Append(F($"Buying climax ({(ex.Style == ClimaxStyle.Rounded ? "rounded" : "sharp")}): rose {advance:P1} from {startPrice:F2} ({s.Dates[startIdx]:yyyy-MM-dd}) "));
        sb.Append(F($"to a high of {climaxHigh:F2} ({s.Dates[climaxIdx]:yyyy-MM-dd}); "));
        sb.Append(ClimaxBehaviour(ex));
        */
        sb.Append(c.ClimaxConfirmed ? "[Distribution] " : "[Developing distribution] ");
        if (c.ClimaxConfirmed)
        {
            sb.Append(F($"Buying climax ({(ex.Style == ClimaxStyle.Rounded ? "rounded" : "sharp")}): rose {advance:P1} from {startPrice:F2} ({s.Dates[startIdx]:yyyy-MM-dd}) "));
            sb.Append(F($"to a high of {climaxHigh:F2} ({s.Dates[climaxIdx]:yyyy-MM-dd}); "));
            sb.Append(ClimaxBehaviour(ex));
        }
        else 
            sb.Append("Trading range detected independently of a confirmed buying climax/automatic reaction; treat as a developing candidate. ");
        //***
        
        if (c.Rally != null)
        {
            double reactionLow = s.Low[c.Rally.Index];
            double reactionPct = (climaxHigh - reactionLow) / climaxHigh;
            string label = c.Rally.Gradual ? "Gradual reaction" : "Automatic reaction";
            sb.Append(F($"{label}: -{reactionPct:P1} to {reactionLow:F2} ({s.Dates[c.Rally.Index]:yyyy-MM-dd}) on {c.Rally.VolumeRatio:F2}x median volume. "));
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
    
    // ==============================================================================
    
    /// <summary>
    /// Determines whether the structure established by a spring or upthrust
    /// remains valid after the recovery bar.
    ///
    /// Accumulation:
    ///   Invalid if a subsequent close breaks below support by more than
    ///   the closing tolerance, or an intraday low breaks support by more
    ///   than the intraday tolerance.
    ///
    /// Distribution:
    ///   Invalid if a subsequent close breaks above resistance by more than
    ///   the closing tolerance, or an intraday high breaks resistance by
    ///   more than the intraday tolerance.
    ///
    /// The recovery bar itself is excluded because its intraday penetration
    /// is part of the spring/upthrust event being evaluated.
    /// </summary>
    private static bool SpringStructureStillHolds(
        PriceSeries s,
        SpringInfo spring,
        bool isDistribution,
        double closeTolerancePct,
        double intradayTolerancePct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(spring);

        if (closeTolerancePct < 0.0 ||
            intradayTolerancePct < 0.0 ||
            closeTolerancePct >= 1.0 ||
            intradayTolerancePct >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(closeTolerancePct),
                "Structural tolerances must be between 0 and 1.");
        }

        int recoveryIndex = spring.RecoveryIndex;

        // Invalid event indexes cannot establish a valid structure.
        if (recoveryIndex < spring.Index ||
            recoveryIndex < 0 ||
            recoveryIndex >= s.Count)
        {
            return false;
        }

        // Convert the structural level to original-price coordinates.
        // Accumulation: spring.Support is the original support.
        // Distribution: reciprocal spring.Support is original resistance.
        double structuralLevel = isDistribution
            ? 1.0 / spring.Support
            : spring.Support;

        if (!double.IsFinite(structuralLevel) || structuralLevel <= 0.0)
            return false;

        // Examine every bar following recovery, including historical bars
        // that occurred before the latest bar.
        for (int i = recoveryIndex + 1; i < s.Count; i++)
        {
            if (isDistribution)
            {
                // Distribution structure fails if price moves sufficiently
                // above established resistance.
                double closeFailureLevel =
                    structuralLevel * (1.0 + closeTolerancePct);

                double intradayFailureLevel =
                    structuralLevel * (1.0 + intradayTolerancePct);

                if (s.Close[i] > closeFailureLevel)
                    return false;

                if (s.High[i] > intradayFailureLevel)
                    return false;
            }
            else
            {
                // Accumulation structure fails if price moves sufficiently
                // below established support.
                double closeFailureLevel =
                    structuralLevel * (1.0 - closeTolerancePct);

                double intradayFailureLevel =
                    structuralLevel * (1.0 - intradayTolerancePct);

                if (s.Close[i] < closeFailureLevel)
                    return false;

                if (s.Low[i] < intradayFailureLevel)
                    return false;
            }
        }

        return true;
    }
    
    /// <summary>
    /// Determines whether a detected sign-of-strength/sign-of-weakness
    /// breakout has remained valid through the latest available bar.
    ///
    /// Accumulation:
    ///   Breakout above resistance.
    ///   Invalid if a subsequent close falls sufficiently below resistance.
    ///
    /// Distribution:
    ///   Breakout below support.
    ///   Invalid if a subsequent close rises sufficiently above support.
    ///
    /// The breakout bar itself is excluded because its closing price
    /// establishes the initial breakout.
    ///
    /// Every subsequent closing price is examined, not just the latest.
    /// </summary>
    private static bool IsBreakoutStillHolding(
        PriceSeries s,
        int breakoutIndex,
        double breakoutLevel,
        bool isDistribution,
        double tolerancePct)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (tolerancePct < 0.0 || tolerancePct >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerancePct));
        }

        if (breakoutIndex < 0 || breakoutIndex >= s.Count)
            return false;

        // Convert the candidate's structural level into original-price
        // coordinates.
        //
        // Accumulation: resistance is already in original-price space.
        // Distribution: inverted resistance represents original support.
        double level = isDistribution
            ? 1.0 / breakoutLevel
            : breakoutLevel;

        if (!double.IsFinite(level) || level <= 0.0)
            return false;

        // Examine every closing price after the breakout.
        for (int i = breakoutIndex + 1; i < s.Count; i++)
        {
            if (isDistribution)
            {
                // Distribution fails if price closes back above support.
                double failureLevel = level * (1.0 + tolerancePct);

                if (s.Close[i] > failureLevel)
                    return false;
            }
            else
            {
                // Accumulation fails if price closes back below resistance.
                double failureLevel = level * (1.0 - tolerancePct);

                if (s.Close[i] < failureLevel)
                    return false;
            }
        }

        return true;
    }

}
