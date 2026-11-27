using Microsoft.AspNetCore.Mvc;
using WyckoffScanner.Lib.Analysis;
using WyckoffScanner.Lib.Loading;
using WyckoffScanner.Lib.Models;

namespace WyckoffWebApi.Controllers;

[ApiController]
[Route("[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly DataLoaderService dataLoaderService;
    private readonly WyckoffAnalyzer analyzer;
    
    public AnalysisController(DataLoaderService dataLoaderService, WyckoffAnalyzer analyzer)
    {
        this.dataLoaderService = dataLoaderService;
        this.analyzer = analyzer;
    }
    
    [HttpPost("Symbol")]
    public async Task<IActionResult> RunAnalysis([FromBody] AnalysisRequest request)
    {
        DateOnly toDate = DateOnly.FromDateTime(DateTime.Now);
        DateOnly fromDate = DateOnly.FromDateTime(DateTime.Now.AddMonths(request.LookbackMonths));

        var (historyData, error) =
            await dataLoaderService.FetchWithRetryAsync(request.Symbol, fromDate, toDate, CancellationToken.None);

        if (error != null)
            return Problem(detail: error, title: "Data Fetch Error", statusCode: 500);

        if (historyData is not null && historyData.Count != 0)
        {
            AnalysisResult result = analyzer.Analyze(request.Symbol, historyData);
            return Ok(result);
        }
        else
            return NotFound(new { message = "Symbol Not Found" });
    }
}