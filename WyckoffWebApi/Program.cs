using WyckoffScanner;
using WyckoffScanner.Lib.Configuration;

namespace WyckoffWebApi;

public class Program
{
    public static void Main(string[] args)
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json");
        var config = configBuilder.Build();
        
        var builder = WebApplication.CreateBuilder(args);
        
        // Add WebApi services to the container.
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<KeepAliveService>();
        
        // Add WyckoffScanner.Lib services and setup, including the hosted service
        var (email, apiToUse) = AppStartup.ConfigureServices(config, builder.Services);

        builder.Services.AddControllers(); 
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
    }
}