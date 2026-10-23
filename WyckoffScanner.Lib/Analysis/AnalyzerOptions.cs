namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// Every threshold used by the Wyckoff heuristics. All are overridable in appsettings.json ("Analyzer" section).
/// Percentages are fractions (0.15 = 15%). "Median" baselines are medians over the analysed window.
/// </summary>
public sealed class AnalyzerOptions
{
    public const string Section = "Analyzer";

    public int LookbackMonths { get; set; } = 12;
    public int MinBars { get; set; } = 60;

    /// <summary>Bars on each side that must be higher for a bar to count as a swing low (SC candidate).</summary>
    public int PivotWindow { get; set; } = 5;

    /// <summary>Maximum number of symbols analysed in parallel while the loader is still running.</summary>
    public int MaxConcurrency { get; set; } = 4;

    // ---- Distribution (reverse polarity) -----------------------------------------------------------
    /// <summary>Also look for distribution: buying climax -> automatic reaction -> distribution box -> upthrust -> sign of weakness.</summary>
    public bool DetectDistribution { get; set; } = true;
    /// <summary>Buying climax: minimum run-up, measured as (climax high / start low - 1). Mirror of <see cref="MinDeclinePct"/>.</summary>
    public double MinAdvancePct { get; set; } = 0.15;
    /// <summary>Automatic reaction: minimum drop from the climax high, as a fraction of that high. Mirror of <see cref="MinRallyPct"/>.</summary>
    public double MinReactionPct { get; set; } = 0.06;

    // ---- 1. Selling exhaustion (selling climax) ------------------------------------------------
    public int MaxDeclineLookbackBars { get; set; } = 60;
    public int MinDeclineBars { get; set; } = 5;
    /// <summary>Minimum peak-to-low decline of the selling leg.</summary>
    public double MinDeclinePct { get; set; } = 0.15;
    /// <summary>Bars used to smooth the daily true-range% ("selling intensity").</summary>
    public int SmoothingBars { get; set; } = 3;
    /// <summary>Peak intensity must be at least this multiple of the median daily range ("large selling").</summary>
    public double LargeSellingMultiple { get; set; } = 1.5;
    /// <summary>Bars between the intensity peak and the low (the "gradually decreasing" tail).</summary>
    public int MinTailBars { get; set; } = 3;
    /// <summary>Intensity at the low must be at most this fraction of the peak intensity.</summary>
    public double MaxExhaustionDecayRatio { get; set; } = 0.70;
    /// <summary>Volume around the intensity peak must be this multiple of median volume (0 disables).</summary>
    public double ClimaxVolumeMultiple { get; set; } = 1.3;

    // ---- 2. Automatic rally ---------------------------------------------------------------------
    public int AutoRallyMaxBars { get; set; } = 20;
    public double MinRallyPct { get; set; } = 0.06;
    /// <summary>Rally must recover at least this fraction of the preceding decline.</summary>
    public double MinRetraceOfDecline { get; set; } = 0.20;
    /// <summary>"Moderate volume": average rally volume vs median volume must fall inside this band...</summary>
    public double RallyVolumeMinRatio { get; set; } = 0.5;
    public double RallyVolumeMaxRatio { get; set; } = 1.5;
    // ...and also be lower than the climax volume.

    // ---- 3. Accumulation (consolidation on low volume) --------------------------------------------
    public int MinAccumulationBars { get; set; } = 10;
    /// <summary>(highest high - lowest low) / lowest low of the box.</summary>
    public double MaxAccumulationRangePct { get; set; } = 0.12;
    /// <summary>Average volume in the box / median volume must be at or below this.</summary>
    public double AccumulationVolumeRatio { get; set; } = 0.90;
    /// <summary>A close this far below the selling-climax low invalidates the structure.</summary>
    public double StructureBreakdownPct { get; set; } = 0.08;
    /// <summary>For "Likely progressing" the latest accumulation box must have ended within this many bars.</summary>
    public int AccumulationMaxAgeBars { get; set; } = 20;

    // ---- 4. Spring --------------------------------------------------------------------------------
    public int SpringRangeLookbackBars { get; set; } = 40;
    public double SpringMaxRangePct { get; set; } = 0.15;
    public double TouchTolerancePct { get; set; } = 0.015;
    public int MinSupportTouches { get; set; } = 2;
    public int MinResistanceTouches { get; set; } = 2;
    /// <summary>Spring bar must trade at least this far below support...</summary>
    public double MinSpringPiercePct { get; set; } = 0.003;
    /// <summary>...but not further than this (deeper = breakdown, not spring).</summary>
    public double MaxSpringPiercePct { get; set; } = 0.06;
    /// <summary>Price must close back above support within this many bars after the spring bar.</summary>
    public int SpringRecoveryBars { get; set; } = 3;
    public int SpringMaxAgeBars { get; set; } = 30;

    // ---- 5. Sign of strength ----------------------------------------------------------------------
    public double BreakoutMarginPct { get; set; } = 0.005;
    /// <summary>Breakout-bar volume vs the prior 20-bar average.</summary>
    public double SosVolumeMultiple { get; set; } = 1.3;
    public int SosMaxAgeBars { get; set; } = 10;
    /// <summary>Latest close must still be at least resistance * (1 - tolerance).</summary>
    public double SosHoldTolerancePct { get; set; } = 0.02;

    /// <summary>
    /// Options for the distribution pass. Distribution is detected by running the same detectors on a price-inverted
    /// series (price' = 1/price: highs become lows, up-bars become down-bars, volume is unchanged). Percentage
    /// thresholds that are measured against a different base in the inverted series are converted here so that the
    /// user-facing numbers keep their natural meaning (e.g. a 15% run-up, a 6% reaction, a 0.3% upthrust).
    /// </summary>
    public AnalyzerOptions ForDistribution()
    {
        var m = (AnalyzerOptions)MemberwiseClone();
        m.MinDeclinePct = MinAdvancePct / (1 + MinAdvancePct);
        m.MinRallyPct = MinReactionPct / (1 - MinReactionPct);
        m.StructureBreakdownPct = StructureBreakdownPct / (1 + StructureBreakdownPct);
        m.MinSpringPiercePct = MinSpringPiercePct / (1 + MinSpringPiercePct);
        m.MaxSpringPiercePct = MaxSpringPiercePct / (1 + MaxSpringPiercePct);
        m.BreakoutMarginPct = BreakoutMarginPct / (1 - BreakoutMarginPct);
        m.SosHoldTolerancePct = SosHoldTolerancePct / (1 + SosHoldTolerancePct);
        return m;
    }
}
