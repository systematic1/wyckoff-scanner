using Microsoft.Extensions.Hosting;

namespace WyckoffScanner;

public class KeepAliveService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _pingUrl;
    private readonly int _intervalMsecs;

    public KeepAliveService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _pingUrl = configuration.GetValue<string>("PingUrl") ?? "?";
        _intervalMsecs = configuration.GetValue<int>("IntervalMinutes") * 60000;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                HttpClient client = _httpClientFactory.CreateClient();
                await client.GetAsync(_pingUrl);
                
                Console.WriteLine("Pinged successfully.");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            
            await Task.Delay(_intervalMsecs, stoppingToken);
        }
    }
}