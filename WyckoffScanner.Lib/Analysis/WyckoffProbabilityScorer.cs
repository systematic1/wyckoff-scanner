using System;
using System.Collections.Generic;
using System.Linq;

/*
 * SUGGESTED REVISIONS:

   Further improvement: lines 48–50 and 315
   
   The current range-width calculation uses a fixed percentage:
   
        double width = Clamp01(1.0 - x.TradingRangeWidthPct / 0.20);
   
   
   This is problematic because a 10% range can be narrow for one stock and very wide for another.
   
   I recommend adding an ATR-normalized range-width input to WyckoffScoreInput, for example:
   
        public double? TradingRangeWidthAtr { get; init; }
   
   
   Then use the range width relative to average true range rather than relying exclusively on a fixed percentage.
   
   This should make the scanner more consistent across stocks with different price levels and volatility characteristics.
   
   -------------------------------------------------------------------
   
   Location: lines 317–328, ScoreTradingRange()
   
   Current problem
   
   The current volume calculation rewards activity close to the median:

        double volume = Clamp01(
            1.0 - Math.Abs(x.TradingRangeVolumeMultiple - 1.0) / 1.75);
   
   
   This is too simplistic for Wyckoff analysis.
   
   Volume near the median does not necessarily indicate accumulation or distribution. High volume can indicate 
   absorption, while declining volume can indicate reduced selling or buying pressure. The price response to 
   that volume matters.
   
   Recommended change
   
   Add measurements for:
   - Volume trend within the trading range.
   - Price spread relative to volume.
   - Whether selling pressure is diminishing near support.
   - Whether buying pressure is diminishing near resistance.
   - Whether volume expands on moves in the direction of the developing pattern.
   
   For example, add optional input properties:
   
        public double? RangeVolumeTrendRatio { get; init; }
   
        public double? SupportEffortResultRatio { get; init; }
   
        public double? ResistanceEffortResultRatio { get; init; }
   
   
   These would need to be calculated by the detector using the underlying price and volume series.
   
   I would not simply replace the current formula with another fixed volume threshold. The effort-versus-result 
   measurements are the more important improvement.
   
   -----------------------------------------------------
   
   Further improvement: lines 387–406, ScoreBreakout()
   
   The current breakout scoring uses:
   
        double holding = x.BreakoutStillHolding ? 1.0 : 0.0;
        double quality = 0.40 * volume + 0.30 * close + 0.30 * holding;
   
   
   I recommend breaking the holding measurement into more detailed evidence:
   1. Initial breakout above resistance or below support.
   2. Number of subsequent closes maintaining the breakout.
   3. Whether price successfully retested the breakout level.
   4. Whether the retest occurred with reduced volume.
   5. Whether follow-through continued in the breakout direction.
   
   For example, add:
   
        public int BreakoutFollowThroughBars { get; init; }
   
        public bool BreakoutRetestSuccessful { get; init; }
   
        public double? BreakoutRetestVolumeMultiple { get; init; }
   
   
   These would allow the scoring method to distinguish an initial breakout from a confirmed breakout.
 */

// Revised version --- OVERZEALOUS AND OVERGENEROUS WITH SCORING
namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Direction being evaluated. Candle-location measurements are normalized internally so
/// that a strong accumulation breakout closes near the high and a strong distribution
/// breakdown closes near the low.
/// </summary>
public enum WyckoffDirection
{
    Accumulation,
    Distribution
}

/// <summary>
/// Input evidence for a Wyckoff candidate. Percentages are fractions (0.15 = 15%);
/// ratios are multiples (1.3 = 1.3x). Optional measurements may be null when the
/// detector does not calculate them.
///
/// IMPORTANT: Boolean detector outputs should be populated explicitly by the detector.
/// In particular, do not assume that a missing value means that a structure is valid.
/// </summary>
public sealed record WyckoffScoreInput
{
    public WyckoffDirection Direction { get; init; } = WyckoffDirection.Accumulation;

    // Phase 1: selling climax / buying climax evidence.
    public bool ClimaxDetected { get; init; }
    public double ClimaxMovePct { get; init; }
    public double ClimaxVolumeMultiple { get; init; } = 1.0;
    public double? ClimaxIntensityMultiple { get; init; }
    public double? ExhaustionFadeRatio { get; init; }
    public double? RoundedFitR2 { get; init; }
    public double? RoundedVolumeFadeRatio { get; init; }
    public int ClimaxAgeBars { get; init; }

    // Phase 2: automatic rally / automatic reaction evidence.
    public bool RallyDetected { get; init; }
    public double RallyRetraceRatio { get; init; }
    public double RallyVolumeMultiple { get; init; }
    public int RallyAgeBars { get; init; }

    // Phase 3: accumulation / distribution trading-range evidence.
    public bool TradingRangeDetected { get; init; }
    public int TradingRangeDurationBars { get; init; }
    public double TradingRangeWidthPct { get; init; }
    public double TradingRangeVolumeMultiple { get; init; }
    public int TradingRangeAgeBars { get; init; }

    // Explicitly default false. The previous true default could award points when the
    // calling detector forgot to populate this property.
    public bool PriceWithinOrNearRange { get; init; }

    // Optional range-quality measurements. Null means unavailable, not zero quality.
    // Counts should represent meaningful tests, not every touch of a price level.
    public int? RangeSupportTests { get; init; }
    public int? RangeResistanceTests { get; init; }
    /// <summary>Ratio of recent to earlier average true range; below 1 indicates contraction.</summary>
    public double? TradingRangeVolatilityContractionRatio { get; init; }

    // Phase 4: spring / upthrust evidence.
    public bool SpringOrUpthrustDetected { get; init; }
    public double SpringPiercePct { get; init; }
    public int SpringRecoveryBars { get; init; }
    public int SpringAgeBars { get; init; }
    public bool SpringRecovered { get; init; }

    // Explicitly default false. A spring should not receive structural-hold credit unless
    // the detector has actually checked that the range remains valid.
    public bool StructureStillHolds { get; init; }

    // Phase 5: sign of strength / sign of weakness evidence.
    public bool BreakoutDetected { get; init; }
    public double BreakoutVolumeMultiple { get; init; }
    /// <summary>Close location within the breakout candle: 0 = low, 1 = high.</summary>
    public double BreakoutCloseLocation { get; init; }
    public int BreakoutAgeBars { get; init; }
    public bool BreakoutStillHolding { get; init; }

    /// <summary>
    /// Set when later price action invalidates the candidate's defining structure.
    /// An invalidated candidate receives zero scores.
    /// </summary>
    public bool Invalidated { get; init; }
}

/// <summary>
/// Scores for one candidate. Score is retained as the primary structure-quality score
/// for compatibility with callers that previously read Score. ConfirmationScore is
/// deliberately separate: a good, developing range need not already have a spring or
/// breakout. Neither score is a statistically calibrated probability.
/// </summary>
public sealed record WyckoffProbabilityScore
{
    /// <summary>Alias for StructureQualityScore; 0..100, not a probability.</summary>
    public double Score { get; init; }
    public double StructureQualityScore { get; init; }
    public double ConfirmationScore { get; init; }

    /// <summary>Approximate proportion of structure evidence categories actually supplied.</summary>
    public double StructureEvidenceCoverage { get; init; }
    /// <summary>Approximate proportion of confirmation phases detected.</summary>
    public double ConfirmationCoverage { get; init; }

    public string Rating { get; init; } = "Insufficient evidence";
    public WyckoffDirection Direction { get; init; }
    public bool Invalidated { get; init; }
    public IReadOnlyList<WyckoffScoreComponent> Components { get; init; } = Array.Empty<WyckoffScoreComponent>();
    public IReadOnlyList<WyckoffScoreComponent> ConfirmationComponents { get; init; } = Array.Empty<WyckoffScoreComponent>();
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>One weighted contribution to a score, with its maximum and explanation.</summary>
public sealed record WyckoffScoreComponent(
    string Name,
    double Points,
    double MaximumPoints,
    string Explanation);

/// <summary>
/// Explainable Wyckoff scoring with two distinct outputs:
/// 1) structure quality (climax, rally, range, and range integrity); and
/// 2) confirmation (spring/upthrust and sign of strength/weakness).
///
/// This avoids treating a not-yet-observed spring or breakout as proof that an otherwise
/// convincing trading range is poor. Scores and thresholds are heuristic starting points;
/// validate them against labelled, out-of-sample historical examples before using them
/// as probabilities or trading signals.
/// </summary>
public static class WyckoffProbabilityScorer
{
    // Structure weights. Missing/not-yet-applicable phases are excluded from the structure
    // denominator; StructureEvidenceCoverage reports how much of the model was observed.
    private const double ClimaxWeight = 25;
    private const double RallyWeight = 10;
    private const double RangeWeight = 45;
    private const double IntegrityWeight = 20;

    // Confirmation is a separate 0..100 scale, not added to structure quality.
    private const double SpringWeight = 45;
    private const double BreakoutWeight = 55;

    /// <summary>Calculate structure quality and confirmation scores from detector evidence.</summary>
    public static WyckoffProbabilityScore Score(WyckoffScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        if (input.Invalidated)
        {
            var invalidation = new[]
            {
                new WyckoffScoreComponent("Invalidation", 0, 100,
                    "A confirmed structural failure overrides historical phase evidence.")
            };

            return new WyckoffProbabilityScore
            {
                Direction = input.Direction,
                Invalidated = true,
                Score = 0,
                StructureQualityScore = 0,
                ConfirmationScore = 0,
                StructureEvidenceCoverage = 0,
                ConfirmationCoverage = 0,
                Rating = "Invalidated",
                Explanation = "Later price action invalidated the candidate's defining structure.",
                Components = invalidation,
                ConfirmationComponents = invalidation
            };
        }

        var structureParts = new List<WyckoffScoreComponent>();
        var confirmationParts = new List<WyckoffScoreComponent>();

        // A phase that has not occurred is not automatically negative evidence. Only
        // detected phases contribute to the structure score; coverage prevents a high
        // score based on one isolated input from appearing as fully supported evidence.
        if (input.ClimaxDetected)
            structureParts.Add(ScoreClimax(input));
        if (input.RallyDetected)
            structureParts.Add(ScoreRally(input));
        if (input.TradingRangeDetected)
        {
            structureParts.Add(ScoreTradingRange(input));
            structureParts.Add(ScoreRangeIntegrity(input));
        }

        if (input.SpringOrUpthrustDetected)
            confirmationParts.Add(ScoreSpring(input));
        if (input.BreakoutDetected)
            confirmationParts.Add(ScoreBreakout(input));

        double structureWeightObserved = structureParts.Sum(p => p.MaximumPoints);
        double structurePoints = structureParts.Sum(p => p.Points);

        // All structure components have a combined maximum weight of 100.
        double structureScore = Math.Clamp(structurePoints, 0, 100);

        // Coverage uses the full theoretical structure weight. A score of 90 with 45%
        // coverage means the available evidence is strong, but the detector supplied only
        // part of the expected structural picture.
        double structureCoverage = structureWeightObserved;

        double confirmationWeightObserved = confirmationParts.Sum(p => p.MaximumPoints);
        double confirmationPoints = confirmationParts.Sum(p => p.Points);
        /*double confirmationScore = confirmationWeightObserved > 0
            ? 100.0 * confirmationPoints / confirmationWeightObserved
            : 0.0;
        double confirmationCoverage = confirmationWeightObserved / 100.0 * 100.0;*/
        double confirmationScore = Math.Clamp(confirmationPoints, 0, 100);
        double confirmationCoverage = confirmationWeightObserved;
        
        structureScore = Math.Round(Math.Clamp(structureScore, 0, 100), 1);
        confirmationScore = Math.Round(Math.Clamp(confirmationScore, 0, 100), 1);

        string rating = structureScore switch
        {
            >= 85 => "Very strong structure",
            >= 70 => "Strong structure",
            >= 55 => "Moderate structure",
            >= 35 => "Developing structure",
            _ => "Weak / incomplete structure"
        };

        /* SUGGESTION:
         
        bool hasClimaxAndRally =
            input.ClimaxDetected && input.RallyDetected;

        bool hasValidRange =
            input.TradingRangeDetected && input.PriceWithinOrNearRange;

        bool hasSpringConfirmation =
            input.SpringOrUpthrustDetected &&
            input.SpringRecovered &&
            input.StructureStillHolds;

        bool hasBreakoutConfirmation =
            input.BreakoutDetected &&
            input.BreakoutStillHolding;
        
        // Then use these conditions to filter out false levels reporting
        */
        
        string direction = input.Direction == WyckoffDirection.Accumulation
            ? "Accumulation" : "Distribution";

        string explanation = $"{direction}: {rating.ToLowerInvariant()} " +
            $"(structure {structureScore:F1}/100; confirmation {confirmationScore:F1}/100). " +
            $"Structure evidence coverage {structureCoverage:F0}%; confirmation coverage {confirmationCoverage:F0}%. " +
            "These are rule-based evidence scores, not calibrated statistical probabilities.";

        return new WyckoffProbabilityScore
        {
            Direction = input.Direction,
            Score = structureScore, // Preserve the legacy Score property as the useful developing-structure score.
            StructureQualityScore = structureScore,
            ConfirmationScore = confirmationScore,
            StructureEvidenceCoverage = structureCoverage,
            ConfirmationCoverage = confirmationCoverage,
            Rating = rating,
            Components = structureParts,
            ConfirmationComponents = confirmationParts,
            Explanation = explanation
        };
    }

    private static WyckoffScoreComponent ScoreClimax(WyckoffScoreInput x)
    {
        // Normalize by the weights of metrics actually supplied. This prevents the basic
        // move/volume model from being capped at 90% merely because optional metrics are null.
        double weighted = 0;
        double availableWeight = 0;

        AddEvidence(ref weighted, ref availableWeight, 0.55,
            Clamp01(x.ClimaxMovePct / 0.30));
        AddEvidence(ref weighted, ref availableWeight, 0.35,
            Clamp01((x.ClimaxVolumeMultiple - 1.0) / 1.5));

        if (x.ClimaxIntensityMultiple.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.10,
                Clamp01((x.ClimaxIntensityMultiple.Value - 1.0) / 2.0));
        if (x.ExhaustionFadeRatio.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.05,
                Clamp01(1.0 - x.ExhaustionFadeRatio.Value));
        if (x.RoundedFitR2.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.10,
                Clamp01((x.RoundedFitR2.Value - 0.40) / 0.60));
        if (x.RoundedVolumeFadeRatio.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.10,
                Clamp01(1.0 - x.RoundedVolumeFadeRatio.Value));

        double quality = availableWeight > 0 ? weighted / availableWeight : 0;

        // If a valid trading range now exists and remains intact, an older climax can still
        // explain the structure. The 0.75 floor is a heuristic; calibrate it with history.
        double age = AgeFactor(x.ClimaxAgeBars, halfLifeBars: 90,
            retainedFloor: x.TradingRangeDetected && x.PriceWithinOrNearRange ? 0.40 : 0.0);

        return Part("Climax", ClimaxWeight * quality * age, ClimaxWeight,
            $"Move {x.ClimaxMovePct:P1}, volume {x.ClimaxVolumeMultiple:F2}x median; " +
            $"available climax metrics {availableWeight:P0}; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreRally(WyckoffScoreInput x)
    {
        // A preferred retracement band is more realistic than rewarding ever-larger
        // retracements. This triangular curve is a heuristic, not a universal Wyckoff rule.
        double retrace = x.RallyRetraceRatio switch
        {
            <= 0.0 => 0.0,
            < 0.50 => x.RallyRetraceRatio / 0.50,
            <= 0.70 => 1.0,
            < 1.10 => 1.0 - ((x.RallyRetraceRatio - 0.70) / 0.40),
            _ => 0.0
        };

        // Moderate/quiet volume is preferred, but this should not be treated as a hard rule.
        double volume = Clamp01(1.0 - Math.Abs(x.RallyVolumeMultiple - 0.80) / 1.50);
        double quality = 0.65 * retrace + 0.35 * volume;
        double age = AgeFactor(x.RallyAgeBars, halfLifeBars: 60,
            retainedFloor: x.TradingRangeDetected && x.PriceWithinOrNearRange ? 0.40 : 0.0);

        return Part("Automatic rally / reaction", RallyWeight * quality * age, RallyWeight,
            $"Retraced {x.RallyRetraceRatio:P0} of climax move; volume {x.RallyVolumeMultiple:F2}x median; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreTradingRange(WyckoffScoreInput x)
    {
        // These thresholds remain provisional because the current input contract does not
        // include ATR-normalized range width. A later detector change should supply that.
        double duration = Clamp01(x.TradingRangeDurationBars / 60.0);
        double width = Clamp01(1.0 - x.TradingRangeWidthPct / 0.20);

        // Avoid assuming that the quietest possible range is always best. A range with
        // normal activity can be valid; unusually high activity needs effort/result context
        // before it can be classified as absorption or distribution.
        double volume = Clamp01(1.0 - Math.Abs(x.TradingRangeVolumeMultiple - 1.0) / 1.75);

        // Optional volatility contraction contributes only when the detector provides it.
        double baseQuality = 0.30 * duration + 0.35 * width + 0.35 * volume;
        if (x.TradingRangeVolatilityContractionRatio.HasValue)
        {
            double contraction = Clamp01(1.0 - x.TradingRangeVolatilityContractionRatio.Value);
            baseQuality = 0.85 * baseQuality + 0.15 * contraction;
        }

        double age = AgeFactor(x.TradingRangeAgeBars, halfLifeBars: 45,
            retainedFloor: x.PriceWithinOrNearRange ? 0.40 : 0.0);
        //double location = x.PriceWithinOrNearRange ? 1.0 : 0.45;
        double quality = baseQuality * age; // * location;

        return Part("Trading range", RangeWeight * quality, RangeWeight,
            $"{x.TradingRangeDurationBars} bars, width {x.TradingRangeWidthPct:P1}, " +
            $"volume {x.TradingRangeVolumeMultiple:F2}x median, price within/near range={x.PriceWithinOrNearRange}; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreRangeIntegrity(WyckoffScoreInput x)
    {
        // A detected range is not automatically a valid range. Use its current location
        // plus available test counts. Missing test counts are omitted and remaining evidence
        // is normalized rather than silently treated as zero.
        
        /*double weighted = 0;
        double availableWeight = 0;
        AddEvidence(ref weighted, ref availableWeight, 0.40,
            x.PriceWithinOrNearRange ? 1.0 : 0.0);

        if (x.RangeSupportTests.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.30,
                Clamp01(x.RangeSupportTests.Value / 3.0));
        if (x.RangeResistanceTests.HasValue)
            AddEvidence(ref weighted, ref availableWeight, 0.30,
                Clamp01(x.RangeResistanceTests.Value / 3.0));

        double quality = availableWeight > 0 ? weighted / availableWeight : 0;*/
        
        double locationQuality = x.PriceWithinOrNearRange ? 1.0 : 0.0;

        double supportQuality = x.RangeSupportTests.HasValue
            ? Clamp01(x.RangeSupportTests.Value / 3.0)
            : 0.0;

        double resistanceQuality = x.RangeResistanceTests.HasValue
            ? Clamp01(x.RangeResistanceTests.Value / 3.0)
            : 0.0;

        double quality =
            0.20 * locationQuality +
            0.40 * supportQuality +
            0.40 * resistanceQuality;
        return Part("Range integrity / tests", IntegrityWeight * quality, IntegrityWeight,
            $"Price within/near range={x.PriceWithinOrNearRange}; " +
            $"support tests={FormatOptional(x.RangeSupportTests)}, resistance tests={FormatOptional(x.RangeResistanceTests)}.");
    }

    private static WyckoffScoreComponent ScoreSpring(WyckoffScoreInput x)
    {
        // Keep the existing percentage bands until the detector can supply ATR-normalized
        // penetration. These fixed bands are stock-volatility dependent and should be calibrated.
        double pierce = x.SpringPiercePct switch
        {
            < 0.003 => 0.25,
            <= 0.020 => 1.0,
            <= 0.040 => 0.75,
            <= 0.060 => 0.35,
            _ => 0.0
        };
        double recovery = x.SpringRecovered
            ? Clamp01(1.0 - Math.Max(0, x.SpringRecoveryBars - 1) / 5.0)
            : 0.0;
        double hold = x.StructureStillHolds ? 1.0 : 0.0;
        double quality = 0.35 * pierce + 0.40 * recovery + 0.25 * hold;
        double age = AgeFactor(x.SpringAgeBars, halfLifeBars: 30);

        return Part("Spring / upthrust", SpringWeight * quality * age, SpringWeight,
            $"Pierce {x.SpringPiercePct:P2}, recovery {x.SpringRecoveryBars} bars, " +
            $"recovered={x.SpringRecovered}, structure holds={x.StructureStillHolds}; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreBreakout(WyckoffScoreInput x)
    {
        double volume = Clamp01((x.BreakoutVolumeMultiple - 1.0) / 1.5);

        // The original formula rewarded closes near the high for both directions. Invert
        // candle location for distribution so closes near the low receive the stronger score.
        double directionalCloseLocation = x.Direction == WyckoffDirection.Accumulation
            ? x.BreakoutCloseLocation
            : 1.0 - x.BreakoutCloseLocation;
        double close = Clamp01((directionalCloseLocation - 0.50) / 0.50);

        // A failed breakout should not retain an arbitrary 20% of the holding contribution.
        // It still receives some quality from its initial volume/close, but no holding credit.
        double holding = x.BreakoutStillHolding ? 1.0 : 0.0;
        double quality = 0.40 * volume + 0.30 * close + 0.30 * holding;
        double age = AgeFactor(x.BreakoutAgeBars, halfLifeBars: 15);

        return Part("Sign of strength / weakness", BreakoutWeight * quality * age, BreakoutWeight,
            $"Breakout volume {x.BreakoutVolumeMultiple:F2}x, directional close location {directionalCloseLocation:P0}, " +
            $"holding={x.BreakoutStillHolding}; age factor {age:F2}.");
    }

    private static void AddEvidence(ref double weighted, ref double availableWeight,
        double weight, double quality)
    {
        weighted += weight * Clamp01(quality);
        availableWeight += weight;
    }

    private static string FormatOptional(int? value) => value.HasValue ? value.Value.ToString() : "not supplied";

    private static WyckoffScoreComponent Part(string name, double points, double max, string explanation) =>
        new(name, Math.Round(Math.Clamp(points, 0, max), 1), max, explanation);

    // Exponential age decay is softened when the event remains part of a currently valid
    // range. The floor is intentionally explicit and configurable in code for easy review.
    private static double AgeFactor(int ageBars, double halfLifeBars, double retainedFloor = 0.0)
    {
        if (ageBars < 0) return 0;
        double factor = Math.Pow(0.5, ageBars / halfLifeBars);
        return Math.Max(retainedFloor, factor);
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static void Validate(WyckoffScoreInput x)
    {
        if (!Enum.IsDefined(typeof(WyckoffDirection), x.Direction))
            throw new ArgumentOutOfRangeException(nameof(x.Direction), "Unknown Wyckoff direction.");

        NonNegative(x.ClimaxMovePct, nameof(x.ClimaxMovePct));
        NonNegative(x.ClimaxVolumeMultiple, nameof(x.ClimaxVolumeMultiple));
        OptionalNonNegative(x.ClimaxIntensityMultiple, nameof(x.ClimaxIntensityMultiple));
        OptionalFraction(x.ExhaustionFadeRatio, nameof(x.ExhaustionFadeRatio));
        OptionalFraction(x.RoundedFitR2, nameof(x.RoundedFitR2));
        OptionalFraction(x.RoundedVolumeFadeRatio, nameof(x.RoundedVolumeFadeRatio));
        NonNegative(x.RallyRetraceRatio, nameof(x.RallyRetraceRatio));
        NonNegative(x.RallyVolumeMultiple, nameof(x.RallyVolumeMultiple));
        NonNegative(x.TradingRangeWidthPct, nameof(x.TradingRangeWidthPct));
        NonNegative(x.TradingRangeVolumeMultiple, nameof(x.TradingRangeVolumeMultiple));
        OptionalNonNegative(x.TradingRangeVolatilityContractionRatio, nameof(x.TradingRangeVolatilityContractionRatio));
        NonNegative(x.SpringPiercePct, nameof(x.SpringPiercePct));
        NonNegative(x.BreakoutVolumeMultiple, nameof(x.BreakoutVolumeMultiple));
        Fraction(x.BreakoutCloseLocation, nameof(x.BreakoutCloseLocation));

        if ((x.RangeSupportTests.HasValue && x.RangeSupportTests.Value < 0) ||
            (x.RangeResistanceTests.HasValue && x.RangeResistanceTests.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(x), "Range test counts cannot be negative.");
        if (x.ClimaxAgeBars < 0 || x.RallyAgeBars < 0 || x.TradingRangeAgeBars < 0 ||
            x.SpringAgeBars < 0 || x.BreakoutAgeBars < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Event ages cannot be negative.");
        if (x.TradingRangeDurationBars < 0 || x.SpringRecoveryBars < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Durations cannot be negative.");
    }

    private static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and non-negative.");
    }

    private static void OptionalNonNegative(double? value, string name)
    {
        if (value.HasValue) NonNegative(value.Value, name);
    }

    private static void OptionalFraction(double? value, string name)
    {
        if (value.HasValue) Fraction(value.Value, name);
    }

    private static void Fraction(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(name, $"Value {value} must be finite and between 0 and 1.");
    }
}
