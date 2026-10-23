using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WyckoffScanner.Lib.AlternativeSources;
using WyckoffScanner.Lib.Analysis;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Data;
using WyckoffScanner.Lib.Loading;
using WyckoffScanner.Lib.Models;
using WyckoffScanner.Lib.Scheduling;
using WyckoffScanner.Lib.YahooFinance;

namespace WyckoffTester;

class TesterProgram
{
    private static readonly string ConnectionString =
        "Host=localhost;Port=5432;Database=wyckoff-scanner;Username=postgres;Password=S8r8s0t8";
    
    static async Task Main(string[] args)
    {
        var dbOptions = Options.Create(new DatabaseOptions() { ConnectionString = ConnectionString });
        var http = new HttpClient();
        var massiveOptions = new MassiveOptions()
        {
            ApiKey = "",
            BaseUrl = "",
            DelayBetweenCallsSeconds = 60,
            Enabled = true,
            MaxPages = 5000,
            RequestTimeoutSeconds = 30
        };
        var analyzerOptions = new AnalyzerOptions()
        {
            //...   
        };
        var yahooOptions = Options.Create(new YahooOptions());
        var dbFactory = new DbConnectionFactory(dbOptions);
        var source = new MassiveQuoteSource(http, Options.Create(massiveOptions), NullLogger<MassiveQuoteSource>.Instance);
        var throttle = new RequestThrottle(yahooOptions, new ThrottleRepository(dbFactory), NullLogger<RequestThrottle>.Instance);
        var symbolRepo = new SymbolRepository(dbFactory);
        var quoteRepo = new QuoteRepository(dbFactory);
        var loadLog = new LoadLogRepository(dbFactory);
        var analysisRepo = new AnalysisRepository(dbFactory);
        var calendar = new TradingCalendar(Options.Create(new ScheduleOptions()));
        var loader = new DataLoaderService(source, throttle, symbolRepo, quoteRepo, loadLog, calendar, yahooOptions, NullLogger<DataLoaderService>.Instance);
        var analyzer = new WyckoffAnalyzer(Options.Create(analyzerOptions));
        var analysisSvc = new AnalysisService(symbolRepo, quoteRepo, analysisRepo, analyzer, Options.Create(analyzerOptions), NullLogger<AnalysisService>.Instance);

        string symbol = string.Empty;
        
        Console.WriteLine("=========== Wyckoff Analysis Logic Tester ==========");

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter the symbol to analyze (leave blank to quit) >> ");
        
            symbol = Console.ReadLine() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
                break;

            IReadOnlyList<DailyQuote>? historyData = await LoadDataFromApi(symbol, loader);
            if (historyData is not null)
                await AnalyzeAndDisplayResults(symbol, historyData, analysisSvc);
        }
        
        Console.WriteLine();
    }

    static async Task<IReadOnlyList<DailyQuote>?> LoadDataFromApi(string symbol, DataLoaderService dataLoaderService)
    {
        DateOnly toDate = DateOnly.FromDateTime(DateTime.Now);
        DateOnly fromDate = DateOnly.FromDateTime(DateTime.Now.AddYears(-1));

        var (quoteData, error) =
            await dataLoaderService.FetchWithRetryAsync(symbol, fromDate, toDate, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(error))
            return quoteData;

        Console.WriteLine($"!! Data Loading Error: {error}");
        return null;
    }

    static async Task AnalyzeAndDisplayResults(string symbol, IReadOnlyList<DailyQuote> historyData, AnalysisService analysisService)
    {
        
    }
    
}