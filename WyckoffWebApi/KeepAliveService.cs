namespace WyckoffWebApi;

public class KeepAliveService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _pingUrl;
    private readonly int _intervalMsecs;

    public KeepAliveService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        
        var webApiConfig = configuration.GetSection("WebApi");
        _pingUrl = webApiConfig.GetValue<string>("PingUrl") ?? "?";
        _intervalMsecs = webApiConfig.GetValue<int>("IntervalMinutes") * 60000;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                HttpClient client = _httpClientFactory.CreateClient();
                await client.GetAsync(_pingUrl, stoppingToken);
                
                Console.WriteLine("Ping successful.");
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