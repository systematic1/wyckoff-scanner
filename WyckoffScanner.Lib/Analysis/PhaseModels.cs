namespace WyckoffScanner.Lib.Analysis;

public sealed record SellingExhaustion(
    int HighIndex, int PeakIndex, int LowIndex,
    double DeclinePct, double PeakIntensity, double EndIntensity,
    double ClimaxVolume, double ClimaxVolumeRatio);

public sealed record AutomaticRally(int Index, double High, double RallyPct, double RetraceOfDecline, double VolumeRatio);

public sealed record AccumulationZone(int Start, int End, double Low, double High, double VolumeRatio);

public sealed record SpringInfo(int Index, int RecoveryIndex, double Support, double Resistance, double SpringLow);

public sealed record SosInfo(int Index, double Resistance, double VolumeRatio);
