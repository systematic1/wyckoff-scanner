using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WyckoffScanner.Analysis;
using WyckoffScanner.Configuration;
using WyckoffScanner.Data;
using WyckoffScanner.Loading;
using WyckoffScanner.Notifications;
using WyckoffScanner.Scheduling;
using WyckoffScanner.YahooFinance;

// Dapper: DateOnly <-> date, and snake_case columns -> PascalCase properties.
SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory   // so appsettings.json is found no matter the working directory
});

// Secrets (DB password, SMTP password) are better kept out of appsettings.json:
//   dotnet user-secrets set "Database:ConnectionString" "..."     (Development)
//   or environment variables, e.g.  Email__Password=...             (any environment)
var cfg = builder.Configuration;
var services = builder.Services;

services.Configure<DatabaseOptions>(cfg.GetSection(DatabaseOptions.Section));
services.Configure<YahooOptions>(cfg.GetSection(YahooOptions.Section));
services.Configure<ScheduleOptions>(cfg.GetSection(ScheduleOptions.Section));
services.Configure<EmailOptions>(cfg.GetSection(EmailOptions.Section));
services.Configure<AnalyzerOptions>(cfg.GetSection(AnalyzerOptions.Section));

// Fail fast on missing essentials.
var email = cfg.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
if (string.IsNullOrWhiteSpace(cfg[$"{DatabaseOptions.Section}:ConnectionString"]))
    throw new InvalidOperationException("Database:ConnectionString is not configured.");
if (string.IsNullOrWhiteSpace(email.SmtpHost) || string.IsNullOrWhiteSpace(email.From) || string.IsNullOrWhiteSpace(email.To))
    throw new InvalidOperationException("Email:SmtpHost, Email:From and Email:To must be configured.");

// Data access
services.AddSingleton<DbConnectionFactory>();
services.AddSingleton<SymbolRepository>();
services.AddSingleton<QuoteRepository>();
services.AddSingleton<LoadLogRepository>();
services.AddSingleton<JobRunRepository>();
services.AddSingleton<AnalysisRepository>();
services.AddSingleton<ThrottleRepository>();

// Yahoo! Finance
void ConfigureYahooClient(IServiceProvider sp, HttpClient client)
{
    var o = sp.GetRequiredService<IOptions<YahooOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(o.UserAgent);
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml,application/json;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
}

HttpMessageHandler CreateHandler() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    UseCookies = true
};

services.AddHttpClient<YahooHtmlQuoteSource>(ConfigureYahooClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
services.AddHttpClient<YahooChartQuoteSource>(ConfigureYahooClient).ConfigurePrimaryHttpMessageHandler(CreateHandler);
services.AddSingleton<IQuoteSource>(sp =>
{
    var o = sp.GetRequiredService<IOptions<YahooOptions>>().Value;
    return o.Source.Equals("ChartApi", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<YahooChartQuoteSource>()
        : sp.GetRequiredService<YahooHtmlQuoteSource>();
});
services.AddSingleton<RequestThrottle>();

// App services
services.AddSingleton<TradingCalendar>();
services.AddSingleton<DataLoaderService>();
services.AddSingleton<WyckoffAnalyzer>();
services.AddSingleton<AnalysisService>();
services.AddSingleton<EmailNotifier>();
services.AddHostedService<DailyPipelineWorker>();

await builder.Build().RunAsync();
