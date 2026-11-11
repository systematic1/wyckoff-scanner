namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Central configuration for the Wyckoff heuristics. Percentages are fractions
/// (0.15 means 15%). Validate() is called by WyckoffAnalyzer so invalid settings
/// fail at startup/use rather than producing opaque false positives.
/// </summary>
public sealed class AnalyzerOptions
{
    public const string Section = "Analyzer";

    public int LookbackMonths { get; set; } = 12;
    public int MinBars { get; set; } = 60;
    public int PivotWindow { get; set; } = 5;
    public int MaxConcurrency { get; set; } = 4;

    // Distribution is detected by running the accumulation engine on reciprocal prices.
    public bool DetectDistribution { get; set; } = true;
    public double MinAdvancePct { get; set; } = 0.15;
    public double MinReactionPct { get; set; } = 0.06;

    // Sharp selling/buying climax.
    public int MaxDeclineLookbackBars { get; set; } = 60;
    public int MinDeclineBars { get; set; } = 5;
    public double MinDeclinePct { get; set; } = 0.15;
    public int SmoothingBars { get; set; } = 3;
    public double LargeSellingMultiple { get; set; } = 1.5;
    public int MinTailBars { get; set; } = 3;
    public double MaxExhaustionDecayRatio { get; set; } = 0.70;
    public double ClimaxVolumeMultiple { get; set; } = 1.3;

    // Rounded climax: gradual volume fade and curved price recovery.
    public bool DetectRoundedClimax { get; set; } = true;
    public int RoundedPivotWindow { get; set; } = 8;
    public int RoundedMinDeclineBars { get; set; } = 15;
    public int VolumeSmoothingBars { get; set; } = 5;
    public int RoundedMinTailBars { get; set; } = 8;
    public double RoundedClimaxVolumeMultiple { get; set; } = 1.2;
    public double RoundedMaxVolumeFadeRatio { get; set; } = 0.65;
    public double RoundedMaxDecelRatio { get; set; } = 0.60;
    public int RoundedRecoveryBars { get; set; } = 25;
    public int RoundedMinRecoveryBars { get; set; } = 8;
    public double RoundedMinRSquared { get; set; } = 0.60;
    public int RoundedVertexToleranceBars { get; set; } = 6;
    public int RoundedMaxRecoveryBars { get; set; } = 40;
    public double RoundedMinRecoveryPct { get; set; } = 0.05;

    // Automatic rally/reaction.
    public int AutoRallyMaxBars { get; set; } = 20;
    public double MinRallyPct { get; set; } = 0.06;
    public double MinRetraceOfDecline { get; set; } = 0.20;
    public double RallyVolumeMinRatio { get; set; } = 0.5;
    public double RallyVolumeMaxRatio { get; set; } = 1.5;

    // Accumulation/distribution trading range.
    public int MinAccumulationBars { get; set; } = 10;
    public double MaxAccumulationRangePct { get; set; } = 0.12;
    public double AccumulationVolumeRatio { get; set; } = 0.90;
    public double StructureBreakdownPct { get; set; } = 0.08;
    public int AccumulationMaxAgeBars { get; set; } = 20;

    // Spring/upthrust.
    public int SpringRangeLookbackBars { get; set; } = 40;
    public double SpringMaxRangePct { get; set; } = 0.15;
    public double TouchTolerancePct { get; set; } = 0.015;
    public int MinSupportTouches { get; set; } = 2;
    public int MinResistanceTouches { get; set; } = 2;
    public double MinSpringPiercePct { get; set; } = 0.003;
    public double MaxSpringPiercePct { get; set; } = 0.06;
    public int SpringRecoveryBars { get; set; } = 3;
    public int SpringMaxAgeBars { get; set; } = 30;

    // Sign of strength/weakness.
    public double BreakoutMarginPct { get; set; } = 0.005;
    public double SosVolumeMultiple { get; set; } = 1.3;
    public int SosMaxAgeBars { get; set; } = 10;
    public double SosHoldTolerancePct { get; set; } = 0.02;

    /// <summary>
    /// Validate settings that could otherwise cause invalid indexing, divide-by-zero,
    /// or nonsensical thresholds. This intentionally does not attempt to tune strategy
    /// values; it only enforces safe numeric domains.
    /// </summary>
    public void Validate()
    {
        Positive(LookbackMonths, nameof(LookbackMonths));
        Positive(MinBars, nameof(MinBars));
        Positive(PivotWindow, nameof(PivotWindow));
        Positive(MaxConcurrency, nameof(MaxConcurrency));
        Positive(MaxDeclineLookbackBars, nameof(MaxDeclineLookbackBars));
        Positive(MinDeclineBars, nameof(MinDeclineBars));
        Positive(SmoothingBars, nameof(SmoothingBars));
        Positive(MinTailBars, nameof(MinTailBars));
        Positive(RoundedPivotWindow, nameof(RoundedPivotWindow));
        Positive(RoundedMinDeclineBars, nameof(RoundedMinDeclineBars));
        Positive(VolumeSmoothingBars, nameof(VolumeSmoothingBars));
        Positive(RoundedMinTailBars, nameof(RoundedMinTailBars));
        Positive(RoundedRecoveryBars, nameof(RoundedRecoveryBars));
        Positive(RoundedMinRecoveryBars, nameof(RoundedMinRecoveryBars));
        Positive(RoundedVertexToleranceBars, nameof(RoundedVertexToleranceBars));
        Positive(RoundedMaxRecoveryBars, nameof(RoundedMaxRecoveryBars));
        Positive(AutoRallyMaxBars, nameof(AutoRallyMaxBars));
        Positive(MinAccumulationBars, nameof(MinAccumulationBars));
        Positive(AccumulationMaxAgeBars, nameof(AccumulationMaxAgeBars));
        Positive(SpringRangeLookbackBars, nameof(SpringRangeLookbackBars));
        Positive(MinSupportTouches, nameof(MinSupportTouches));
        Positive(MinResistanceTouches, nameof(MinResistanceTouches));
        Positive(SpringRecoveryBars, nameof(SpringRecoveryBars));
        Positive(SpringMaxAgeBars, nameof(SpringMaxAgeBars));
        Positive(SosMaxAgeBars, nameof(SosMaxAgeBars));

        Positive(MinAdvancePct, nameof(MinAdvancePct));
        Fraction(MinReactionPct, nameof(MinReactionPct));
        Fraction(MinDeclinePct, nameof(MinDeclinePct));
        Fraction(MaxExhaustionDecayRatio, nameof(MaxExhaustionDecayRatio));
        Fraction(RoundedMaxVolumeFadeRatio, nameof(RoundedMaxVolumeFadeRatio));
        Fraction(RoundedMaxDecelRatio, nameof(RoundedMaxDecelRatio));
        Fraction(RoundedMinRSquared, nameof(RoundedMinRSquared));
        Fraction(RoundedMinRecoveryPct, nameof(RoundedMinRecoveryPct));
        Fraction(MinRallyPct, nameof(MinRallyPct));
        Fraction(MinRetraceOfDecline, nameof(MinRetraceOfDecline));
        Fraction(MaxAccumulationRangePct, nameof(MaxAccumulationRangePct));
        Positive(AccumulationVolumeRatio, nameof(AccumulationVolumeRatio));
        Fraction(StructureBreakdownPct, nameof(StructureBreakdownPct));
        Fraction(SpringMaxRangePct, nameof(SpringMaxRangePct));
        Fraction(TouchTolerancePct, nameof(TouchTolerancePct));
        Fraction(MinSpringPiercePct, nameof(MinSpringPiercePct));
        Fraction(MaxSpringPiercePct, nameof(MaxSpringPiercePct));
        Fraction(BreakoutMarginPct, nameof(BreakoutMarginPct));
        Fraction(SosHoldTolerancePct, nameof(SosHoldTolerancePct));

        Positive(RallyVolumeMinRatio, nameof(RallyVolumeMinRatio));
        Positive(RallyVolumeMaxRatio, nameof(RallyVolumeMaxRatio));
        Positive(SosVolumeMultiple, nameof(SosVolumeMultiple));
        Positive(LargeSellingMultiple, nameof(LargeSellingMultiple));
        Positive(RoundedClimaxVolumeMultiple, nameof(RoundedClimaxVolumeMultiple));
        Positive(RoundedMinRSquared, nameof(RoundedMinRSquared));
        if (ClimaxVolumeMultiple < 0 || !double.IsFinite(ClimaxVolumeMultiple))
            throw new ArgumentOutOfRangeException(nameof(ClimaxVolumeMultiple));
        if (RallyVolumeMaxRatio < RallyVolumeMinRatio)
            throw new ArgumentException("RallyVolumeMaxRatio must be >= RallyVolumeMinRatio.");
        if (MaxSpringPiercePct <= MinSpringPiercePct)
            throw new ArgumentException("MaxSpringPiercePct must be greater than MinSpringPiercePct.");
        if (RoundedMinRecoveryBars > RoundedMaxRecoveryBars)
            throw new ArgumentException("RoundedMinRecoveryBars must be <= RoundedMaxRecoveryBars.");
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, "Value must be greater than zero.");
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and greater than zero.");
    }

    private static void Fraction(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value >= 1)
            throw new ArgumentOutOfRangeException(name, "Fraction must be finite and in [0, 1).");
    }

    /// <summary>
    /// Create a private configuration snapshot so callers changing their bound options
    /// after analyzer construction cannot change an in-flight analysis.
    /// </summary>
    public AnalyzerOptions Clone() => (AnalyzerOptions)MemberwiseClone();

    /// <summary>
    /// Reciprocal-price transformation used for distribution. The conversions preserve
    /// the natural percentage interpretation of the original (non-inverted) market.
    /// </summary>
    public AnalyzerOptions ForDistribution()
    {
        Validate();
        var m = Clone();
        m.MinDeclinePct = MinAdvancePct / (1 + MinAdvancePct);
        m.MinRallyPct = MinReactionPct / (1 - MinReactionPct);
        m.RoundedMinRecoveryPct = RoundedMinRecoveryPct / (1 - RoundedMinRecoveryPct);
        m.StructureBreakdownPct = StructureBreakdownPct / (1 + StructureBreakdownPct);
        m.MinSpringPiercePct = MinSpringPiercePct / (1 + MinSpringPiercePct);
        m.MaxSpringPiercePct = MaxSpringPiercePct / (1 + MaxSpringPiercePct);
        m.BreakoutMarginPct = BreakoutMarginPct / (1 - BreakoutMarginPct);
        m.SosHoldTolerancePct = SosHoldTolerancePct / (1 + SosHoldTolerancePct);
        return m;
    }
}
