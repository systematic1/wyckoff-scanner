namespace WyckoffScanner.Models;

public sealed class SymbolRecord
{
    public int SymbolId { get; set; }
    public string Ticker { get; set; } = "";
    public DateTime? BackfillCompletedAt { get; set; }
}

public sealed class DailyQuote
{
    public DateOnly TradeDate { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal? AdjClosePrice { get; set; }
    public long Volume { get; set; }
}

public sealed class JobRun
{
    public DateOnly RunDate { get; set; }
    public DateTime? LoaderCompletedAt { get; set; }
    public DateTime? AnalysisCompletedAt { get; set; }
    public DateTime? EmailSentAt { get; set; }
}

public enum JobStage { Loader, Analysis, Email }

/// <summary>Which direction of the Wyckoff cycle a result describes.</summary>
public enum WyckoffPattern
{
    None = 0,
    Accumulation = 1,
    Distribution = 2
}

/// <summary>
/// Stage reached by the pattern. 1-3 are accumulation (bullish); 4-6 are the mirror-image distribution stages (bearish).
/// The distribution value for a stage is always (accumulation value + 3).
/// </summary>
public enum WyckoffFlag
{
    None = 0,
    // Accumulation
    LikelyProgressing = 1,
    SpringDetected = 2,
    SosDetected = 3,
    // Distribution (reverse polarity)
    LikelyDistribution = 4,
    UpthrustDetected = 5,
    SowDetected = 6
}

/// <summary>
/// Raised by the loader when a symbol has been brought up to date (or could not be) so the analyzer can start on it
/// without waiting for the remaining symbols. <paramref name="DataChanged"/> is true when new quotes were stored.
/// </summary>
public sealed record SymbolUpdate(SymbolRecord Symbol, bool DataChanged);

public sealed class AnalysisResult
{
    public int SymbolId { get; set; }
    public string Ticker { get; set; } = "";
    public WyckoffFlag Flag { get; set; } = WyckoffFlag.None;
    public WyckoffPattern Pattern { get; set; } = WyckoffPattern.None;

    // For a distribution result these describe the mirror-image events:
    //   SellingExhaustion = buying climax, AutomaticRally = automatic reaction, Accumulation = distribution box(es),
    //   Spring = upthrust (UTAD), SignOfStrength = sign of weakness.
    public bool SellingExhaustion { get; set; }
    public bool AutomaticRally { get; set; }
    public bool Accumulation { get; set; }
    public int AccumulationCount { get; set; }
    public bool Spring { get; set; }
    public bool SignOfStrength { get; set; }

    public double? Support { get; set; }
    public double? Resistance { get; set; }
    public double LastClose { get; set; }
    public DateOnly LastDate { get; set; }
    public int BarsAnalyzed { get; set; }
    public string Details { get; set; } = "";

    public string FlagMessage => Flag switch
    {
        WyckoffFlag.LikelyProgressing => "Likely progressing",
        WyckoffFlag.SpringDetected => "Spring detected",
        WyckoffFlag.SosDetected => "SoS detected",
        WyckoffFlag.LikelyDistribution => "Distribution likely progressing",
        WyckoffFlag.UpthrustDetected => "Upthrust detected",
        WyckoffFlag.SowDetected => "SoW detected",
        _ => "None"
    };
}

/// <summary>Row read back from analysis_results for the e-mail report.</summary>
public sealed class FlaggedSymbol
{
    public string Ticker { get; set; } = "";
    public string Flag { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string FlagMessage { get; set; } = "";
    public decimal? SupportPrice { get; set; }
    public decimal? ResistancePrice { get; set; }
    public decimal? LastClose { get; set; }
    public string? Details { get; set; }
}
