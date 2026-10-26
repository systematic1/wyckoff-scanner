namespace WyckoffScanner.Lib.Analysis;

/// <summary>
/// How the climax resolved. Sharp = violent range expansion then a V-style bounce (original detector).
/// Rounded = volume fades gradually, the decline decelerates and price curves into a saucer/bowl (or dome, for distribution).
/// </summary>
public enum ClimaxStyle
{
    Sharp,
    Rounded
}

/// <param name="Style">Which detector produced this climax.</param>
/// <param name="VolumeFadeRatio">Rounded only: tail volume at the low divided by the peak (smoothed) volume. Lower = quieter.</param>
/// <param name="RoundnessR2">Rounded only: R-squared of the parabola fitted through the decline and the start of the recovery.</param>
public sealed record SellingExhaustion(
    int HighIndex, int PeakIndex, int LowIndex,
    double DeclinePct, double PeakIntensity, double EndIntensity,
    double ClimaxVolume, double ClimaxVolumeRatio,
    ClimaxStyle Style = ClimaxStyle.Sharp, double VolumeFadeRatio = 0, double RoundnessR2 = 0);

/// <param name="Gradual">True when this was found by the gradual-recovery detector (rounded climax) instead of a sharp bounce.</param>
public sealed record AutomaticRally(int Index, double High, double RallyPct, double RetraceOfDecline, double VolumeRatio, bool Gradual = false);

public sealed record AccumulationZone(int Start, int End, double Low, double High, double VolumeRatio);

public sealed record SpringInfo(int Index, int RecoveryIndex, double Support, double Resistance, double SpringLow);

public sealed record SosInfo(int Index, double Resistance, double VolumeRatio);
