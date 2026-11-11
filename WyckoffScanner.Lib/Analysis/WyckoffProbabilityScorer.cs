using System;
using System.Collections.Generic;
using System.Linq;

namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Direction being evaluated. Distribution uses the same evidence model as accumulation,
/// with the underlying measurements supplied in their natural (non-inverted) interpretation.
/// </summary>
public enum WyckoffDirection
{
    Accumulation,
    Distribution
}

/// <summary>
/// Input evidence for a Wyckoff score. Values are measured from the candidate structure.
/// Percentages are fractions (0.15 = 15%); ratios are multiples (1.3 = 1.3x).
/// Optional metrics may be left null when the detector does not calculate them.
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
    public bool PriceWithinOrNearRange { get; init; } = true;

    // Phase 4: spring / upthrust evidence.
    public bool SpringOrUpthrustDetected { get; init; }
    public double SpringPiercePct { get; init; }
    public int SpringRecoveryBars { get; init; }
    public int SpringAgeBars { get; init; }
    public bool SpringRecovered { get; init; }
    public bool StructureStillHolds { get; init; } = true;

    // Phase 5: sign of strength / sign of weakness evidence.
    public bool BreakoutDetected { get; init; }
    public double BreakoutVolumeMultiple { get; init; }
    /// <summary>Close location within the breakout candle: 0 = low, 1 = high.</summary>
    public double BreakoutCloseLocation { get; init; }
    public int BreakoutAgeBars { get; init; }
    public bool BreakoutStillHolding { get; init; }

    /// <summary>
    /// Set when later price action invalidates the candidate's defining structure.
    /// An invalidated candidate receives zero, regardless of its historical phase count.
    /// </summary>
    public bool Invalidated { get; init; }
}

/// <summary>Score for one candidate, including an auditable component breakdown.</summary>
public sealed record WyckoffProbabilityScore
{
    /// <summary>0..100 evidence score. This is not a statistically calibrated probability.</summary>
    public double Score { get; init; }
    public string Rating { get; init; } = "Insufficient evidence";
    public WyckoffDirection Direction { get; init; }
    public bool Invalidated { get; init; }
    public IReadOnlyList<WyckoffScoreComponent> Components { get; init; } = Array.Empty<WyckoffScoreComponent>();
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>One weighted contribution to the total score.</summary>
public sealed record WyckoffScoreComponent(
    string Name,
    double Points,
    double MaximumPoints,
    string Explanation);

/// <summary>
/// Deterministic, explainable Wyckoff evidence scoring.
/// The weights intentionally reward completion of later phases, while still giving partial
/// credit to an emerging structure. Calibrate the score against labelled historical examples
/// before interpreting it as a true probability.
/// </summary>
public static class WyckoffProbabilityScorer
{
    // Weights sum to 100. Later confirmation receives more weight than the initial climax.
    private const double ClimaxWeight = 20;
    private const double RallyWeight = 12;
    private const double RangeWeight = 20;
    private const double SpringWeight = 20;
    private const double BreakoutWeight = 28;

    /// <summary>Calculate a 0..100 evidence score from detector measurements.</summary>
    public static WyckoffProbabilityScore Score(WyckoffScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);

        if (input.Invalidated)
        {
            return new WyckoffProbabilityScore
            {
                Direction = input.Direction,
                Invalidated = true,
                Score = 0,
                Rating = "Invalidated",
                Explanation = "Later price action invalidated the candidate's defining structure.",
                Components = new[]
                {
                    new WyckoffScoreComponent("Invalidation", 0, 100,
                        "A confirmed structural failure overrides historical phase evidence.")
                }
            };
        }

        var parts = new List<WyckoffScoreComponent>
        {
            ScoreClimax(input),
            ScoreRally(input),
            ScoreTradingRange(input),
            ScoreSpring(input),
            ScoreBreakout(input)
        };

        // Sum weighted contributions. Age decay prevents old events from carrying full weight.
        double total = Math.Clamp(parts.Sum(p => p.Points), 0, 100);

        // A recent range that price has left without a valid breakout is less actionable.
        if (input.TradingRangeDetected && !input.PriceWithinOrNearRange &&
            !input.BreakoutStillHolding)
        {
            total *= 0.90;
        }

        total = Math.Round(Math.Clamp(total, 0, 100), 1);
        string rating = total switch
        {
            >= 85 => "Very strong Wyckoff evidence",
            >= 70 => "Strong Wyckoff evidence",
            >= 55 => "Moderate Wyckoff evidence",
            >= 35 => "Developing Wyckoff evidence",
            _ => "Weak / incomplete evidence"
        };

        string direction = input.Direction == WyckoffDirection.Accumulation
            ? "Accumulation" : "Distribution";

        return new WyckoffProbabilityScore
        {
            Direction = input.Direction,
            Score = total,
            Rating = rating,
            Components = parts,
            Explanation = $"{direction}: {rating.ToLowerInvariant()} ({total:F1}/100). " +
                "Treat this as a rule-based evidence score, not a calibrated statistical probability."
        };
    }

    private static WyckoffScoreComponent ScoreClimax(WyckoffScoreInput x)
    {
        if (!x.ClimaxDetected)
            return Part("Climax", 0, ClimaxWeight, "No qualifying climax was detected.");

        // Larger climactic moves and above-normal volume strengthen the exhaustion hypothesis.
        double move = Clamp01(x.ClimaxMovePct / 0.30);
        double volume = Clamp01((x.ClimaxVolumeMultiple - 1.0) / 1.5);
        double quality = 0.55 * move + 0.30 * volume;

        // Sharp detectors can supply intensity/fade; rounded detectors can supply curve-fit/fade.
        if (x.ClimaxIntensityMultiple.HasValue)
            quality += 0.10 * Clamp01((x.ClimaxIntensityMultiple.Value - 1.0) / 2.0);
        if (x.ExhaustionFadeRatio.HasValue)
            quality += 0.05 * Clamp01(1.0 - x.ExhaustionFadeRatio.Value);
        if (x.RoundedFitR2.HasValue)
            quality += 0.10 * Clamp01((x.RoundedFitR2.Value - 0.40) / 0.60);
        if (x.RoundedVolumeFadeRatio.HasValue)
            quality += 0.10 * Clamp01(1.0 - x.RoundedVolumeFadeRatio.Value);

        // Normalize when rounded-only inputs add extra evidence so this phase never exceeds its weight.
        quality = Clamp01(quality);
        double age = AgeFactor(x.ClimaxAgeBars, halfLifeBars: 90);
        return Part("Climax", ClimaxWeight * quality * age, ClimaxWeight,
            $"Move {x.ClimaxMovePct:P1}, volume {x.ClimaxVolumeMultiple:F2}x median; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreRally(WyckoffScoreInput x)
    {
        if (!x.RallyDetected)
            return Part("Automatic rally / reaction", 0, RallyWeight, "No qualifying rally/reaction was detected.");

        // Retracement is useful, but an excessively large retracement can imply a trend reversal.
        double retrace = Clamp01(x.RallyRetraceRatio / 0.60);
        // Quiet-to-moderate volume is preferred; score peaks near 0.8x the baseline.
        double volume = Clamp01(1.0 - Math.Abs(x.RallyVolumeMultiple - 0.80) / 1.20);
        double age = AgeFactor(x.RallyAgeBars, halfLifeBars: 60);
        double quality = (0.65 * retrace + 0.35 * volume) * age;

        return Part("Automatic rally / reaction", RallyWeight * quality, RallyWeight,
            $"Retraced {x.RallyRetraceRatio:P0} of the climax move; volume {x.RallyVolumeMultiple:F2}x median; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreTradingRange(WyckoffScoreInput x)
    {
        if (!x.TradingRangeDetected)
            return Part("Trading range", 0, RangeWeight, "No qualifying accumulation/distribution range was detected.");

        // Reward meaningful duration, compact spread, and subdued participation.
        double duration = Clamp01(x.TradingRangeDurationBars / 40.0);
        double width = Clamp01(1.0 - x.TradingRangeWidthPct / 0.20);
        double volume = Clamp01(1.0 - x.TradingRangeVolumeMultiple / 1.50);
        double age = AgeFactor(x.TradingRangeAgeBars, halfLifeBars: 45);
        double location = x.PriceWithinOrNearRange ? 1.0 : 0.45;
        double quality = (0.30 * duration + 0.35 * width + 0.35 * volume) * age * location;

        return Part("Trading range", RangeWeight * quality, RangeWeight,
            $"{x.TradingRangeDurationBars} bars, width {x.TradingRangeWidthPct:P1}, volume {x.TradingRangeVolumeMultiple:F2}x median; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreSpring(WyckoffScoreInput x)
    {
        if (!x.SpringOrUpthrustDetected)
            return Part("Spring / upthrust", 0, SpringWeight, "No qualifying spring/upthrust was detected.");

        // Prefer a controlled penetration: a tiny probe may be noise; a deep one may be failure.
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
        double age = AgeFactor(x.SpringAgeBars, halfLifeBars: 30);
        double hold = x.StructureStillHolds ? 1.0 : 0.0;
        double quality = (0.35 * pierce + 0.40 * recovery + 0.25 * hold) * age;

        return Part("Spring / upthrust", SpringWeight * quality, SpringWeight,
            $"Pierce {x.SpringPiercePct:P2}, recovery {x.SpringRecoveryBars} bars, recovered={x.SpringRecovered}, structure holds={x.StructureStillHolds}; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent ScoreBreakout(WyckoffScoreInput x)
    {
        if (!x.BreakoutDetected)
            return Part("Sign of strength / weakness", 0, BreakoutWeight, "No qualifying breakout was detected.");

        // A high-volume breakout closing near its extreme is stronger than a weak close above resistance.
        double volume = Clamp01((x.BreakoutVolumeMultiple - 1.0) / 1.5);
        double close = Clamp01((x.BreakoutCloseLocation - 0.50) / 0.50);
        double age = AgeFactor(x.BreakoutAgeBars, halfLifeBars: 15);
        double holding = x.BreakoutStillHolding ? 1.0 : 0.20;
        double quality = (0.40 * volume + 0.30 * close + 0.30 * holding) * age;

        return Part("Sign of strength / weakness", BreakoutWeight * quality, BreakoutWeight,
            $"Breakout volume {x.BreakoutVolumeMultiple:F2}x, close location {x.BreakoutCloseLocation:P0}, holding={x.BreakoutStillHolding}; age factor {age:F2}.");
    }

    private static WyckoffScoreComponent Part(string name, double points, double max, string explanation) =>
        new(name, Math.Round(Math.Clamp(points, 0, max), 1), max, explanation);

    // Exponential decay gives older events progressively less influence without a hard cutoff.
    private static double AgeFactor(int ageBars, double halfLifeBars)
    {
        if (ageBars < 0) return 0;
        return Math.Pow(0.5, ageBars / halfLifeBars);
    }

    private static double Clamp01(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static void Validate(WyckoffScoreInput x)
    {
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
        NonNegative(x.SpringPiercePct, nameof(x.SpringPiercePct));
        NonNegative(x.BreakoutVolumeMultiple, nameof(x.BreakoutVolumeMultiple));
        Fraction(x.BreakoutCloseLocation, nameof(x.BreakoutCloseLocation));

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
            throw new ArgumentOutOfRangeException(name, "Value must be finite and between 0 and 1.");
    }
}
