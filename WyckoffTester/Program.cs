using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WyckoffScanner.Lib.AlternativeSources;
using WyckoffScanner.Lib.Analysis;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Data;
using WyckoffScanner.Lib.Loading;
using WyckoffScanner.Lib.Models;
using WyckoffScanner.Lib.Notifications;
using WyckoffScanner.Lib.Scheduling;
using WyckoffScanner.Lib.YahooFinance;

namespace WyckoffTester;

class TesterProgram
{
    private static IConfiguration Configuration;
    
    static async Task Main(string[] args)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json");
        Configuration = builder.Build();

        var dbOptions = Configuration.GetSection("Database").Get<DatabaseOptions>();
        var http = new HttpClient();
        var massiveOptions = Configuration.GetSection("Massive").Get<MassiveOptions>();
        var analyzerOptions = Configuration.GetSection("Analyzer").Get<AnalyzerOptions>();
        var yahooOptions = Options.Create(new YahooOptions());
        var dbFactory = new DbConnectionFactory(Options.Create(dbOptions!));
        var source = new MassiveQuoteSource(http, Options.Create(massiveOptions!), NullLogger<MassiveQuoteSource>.Instance);
        var throttle = new RequestThrottle(yahooOptions, new ThrottleRepository(dbFactory), NullLogger<RequestThrottle>.Instance);
        var symbolRepo = new SymbolRepository(dbFactory);
        var quoteRepo = new QuoteRepository(dbFactory);
        var loadLog = new LoadLogRepository(dbFactory);
        var calendar = new TradingCalendar(Options.Create(new ScheduleOptions()));
        var loader = new DataLoaderService(source, throttle, symbolRepo, quoteRepo, loadLog, calendar, yahooOptions, NullLogger<DataLoaderService>.Instance);
        var analyzer = new WyckoffAnalyzer(Options.Create(analyzerOptions!));

        var emailOptions = Configuration.GetSection("Email").Get<EmailOptions>();
        var notifier = new EmailNotifier(new AnalysisRepository(dbFactory), Options.Create(emailOptions), NullLogger<EmailNotifier>.Instance);
        
        string symbol = string.Empty;
        
        Console.WriteLine("=========== Wyckoff Analysis Logic Tester ==========");

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter the symbol to analyze (leave blank to quit) >> ");
        
            symbol = Console.ReadLine() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
                break;

            Console.WriteLine("Getting Quote Data...");
            symbol = symbol.ToUpper();
            IReadOnlyList<DailyQuote>? historyData = await LoadDataFromApi(symbol, loader, analyzerOptions!.LookbackMonths);
            if (historyData is not null && historyData.Count != 0)
            {
                var analysisResult = await AnalyzeAndDisplayResults(symbol, historyData, analyzer);
                //await EmailTest(notifier, analysisResult);
            }
            else 
                Console.WriteLine("No quote data was returned.");
        }
        
        Console.WriteLine();
    }

    static async Task<IReadOnlyList<DailyQuote>?> LoadDataFromApi(string symbol, 
        DataLoaderService dataLoaderService, int lookbackMonths)
    {
        DateOnly toDate = DateOnly.FromDateTime(DateTime.Now);
        DateOnly fromDate = DateOnly.FromDateTime(DateTime.Now.AddMonths(-lookbackMonths));

        var (quoteData, error) =
            await dataLoaderService.FetchWithRetryAsync(symbol, fromDate, toDate, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(error))
            return quoteData;

        Console.WriteLine($"!! Data Loading Error: {error}");
        return null;
    }

    static async Task<AnalysisResult> AnalyzeAndDisplayResults(string symbol, IReadOnlyList<DailyQuote> historyData, WyckoffAnalyzer analyzer)
    {
        AnalysisResult result = analyzer.Analyze(symbol, historyData);
        
        Console.WriteLine();

        Console.WriteLine("****************************************************");
        Console.WriteLine($"    Wyckoff Pattern Analysis Results for: {symbol}");
        Console.WriteLine("****************************************************");
        Console.WriteLine();

        if (result.Accumulation)
        {
            Console.WriteLine(result.Details);
            Console.WriteLine();

            WriteTextColoredByCondition(
                result.Pattern != WyckoffPattern.None,
                $"General Pattern Detected?             {result.Pattern.ToString()}");

            Console.WriteLine($"Patterns (number of areas):           {result.AccumulationCount}");
            
            WriteTextColoredByCondition(
                result.FlagMessage != "None",
                $"Phase Determination:                  {result.FlagMessage}");
            WriteTextColoredByCondition(
                result.SellingExhaustion,
                $"Selling/Buying Exhaustion Detected?   {(result.SellingExhaustion ? "Yes" : "No")}");
            WriteTextColoredByCondition(
                result.AutomaticRally,
                $"Automatic Rally/Reaction Detected?    {(result.AutomaticRally ? "Yes" : "No")}");
            WriteTextColoredByCondition(
                result.Spring,
                $"Spring/Upthrust Detected?             {(result.Spring ? "Yes" : "No")}");
            WriteTextColoredByCondition(
                result.SignOfStrength,
                $"Sign Of Strength/Weakness Detected?   {(result.SignOfStrength ? "Yes" : "No")}");

            Console.WriteLine($"Resistance Level:                     ${result.Resistance}");
            Console.WriteLine($"Support Level:                        ${result.Support}");
        }

        Console.WriteLine($"Number of bars analyzed:              {result.BarsAnalyzed}");
        Console.WriteLine($"Last Date Analyzed:                   {result.LastDate}");
        Console.WriteLine($"Last Close Price:                     ${result.LastClose}");
        Console.WriteLine();

        return result;
    }

    static void WriteTextColoredByCondition(bool condition, string text)
    {
        ConsoleColor defaultColor = Console.ForegroundColor;
        Console.ForegroundColor = condition ? ConsoleColor.DarkGreen : ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ForegroundColor = defaultColor;
    }

    static async Task EmailTest(EmailNotifier notifier, AnalysisResult result)
    {
        FlaggedSymbol flagged = new FlaggedSymbol()
        {
            Ticker = result.Ticker,
            Flag = result.Flag.ToString(),
            Details = result.Details,
            FlagMessage = result.FlagMessage,
            LastClose = (decimal)result.LastClose,
            Pattern = result.Pattern.ToString(),
            ResistancePrice = (decimal)(result.Resistance ?? 0d),
            SupportPrice =  (decimal)(result.Support ?? 0d)
        };
        
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var flagList = new List<FlaggedSymbol>();
        flagList.Add(flagged);
        
        await notifier.SendMessage(flagList.AsReadOnly(), today, CancellationToken.None);
    }
}