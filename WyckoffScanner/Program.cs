using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Data;

// Dapper: DateOnly <-> date, and snake_case columns -> PascalCase properties.
SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory   // so appsettings.json is found no matter the working directory
});

// Add WyckoffScanner.Lib services and setup
var (email, apiToUse) = AppStartup.ConfigureServices(builder.Configuration, builder.Services);

var serviceProvider = builder.Services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

logger.LogInformation($"============ Wyckoff scanner app started at {DateTime.Now} ===========");
logger.LogInformation($"Using market API: {apiToUse}");

if (!String.IsNullOrWhiteSpace(email))
    logger.LogInformation($"Email notifications sent to: {email}");

await builder.Build().RunAsync();
