namespace WyckoffScanner.Lib.Models;

public class AnalysisRequest
{
    public string Symbol { get; set; }
    public int LookbackMonths { get; set; } = 12;
}