namespace WyckoffScanner.Configuration;

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string ConnectionString { get; set; } = "";
}

public sealed class YahooOptions
{
    public const string Section = "Yahoo";

    /// <summary>"Html" = scrape the history page (the URL in the spec); "ChartApi" = Yahoo's JSON chart endpoint.</summary>
    public string Source { get; set; } = "Html";

    public string QuoteBaseUrl { get; set; } = "https://finance.yahoo.com/quote/";
    public string ChartApiBaseUrl { get; set; } = "https://query1.finance.yahoo.com/v8/finance/chart/";

    /// <summary>Minimum minutes between two calls to Yahoo (spec: 3).</summary>
    public double DelayBetweenCallsSeconds { get; set; } = 180;

    /// <summary>Attempts per symbol per run (each attempt is a separate call and respects the delay).</summary>
    public int MaxAttemptsPerSymbol { get; set; } = 2;

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Random extra delay (0..N seconds) added to every gap so the cadence is not exactly 180s.</summary>
    public double JitterSeconds { get; set; } = 30;

    /// <summary>Pause after the first HTTP 429; doubles for each consecutive 429.</summary>
    public double RateLimitCooldownMinutes { get; set; } = 60;

    /// <summary>Upper bound for the (doubling) cooldown.</summary>
    public double MaxCooldownMinutes { get; set; } = 720;

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}

public sealed class ScheduleOptions
{
    public const string Section = "Schedule";

    /// <summary>Local start time (24h, "HH:mm") on each trading day.</summary>
    public string StartTime { get; set; } = "06:00";

    /// <summary>IANA (e.g. America/New_York) or Windows time zone id. Empty = machine local time.</summary>
    public string TimeZoneId { get; set; } = "America/New_York";

    /// <summary>Market holidays (yyyy-MM-dd). The app only runs Mon-Fri and skips these dates.</summary>
    public string[] Holidays { get; set; } = Array.Empty<string>();

    /// <summary>If a pipeline run throws, wait this long and resume.</summary>
    public int FailureRetryMinutes { get; set; } = 15;
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    public bool Enabled { get; set; } = false;
    
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;

    /// <summary>None | Auto | SslOnConnect | StartTls | StartTlsWhenAvailable</summary>
    public string SecureSocketMode { get; set; } = "StartTls";

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";

    /// <summary>One or more recipients separated by ',' or ';'.</summary>
    public string To { get; set; } = "";

    /// <summary>Send a "nothing flagged today" email when no symbols are flagged.</summary>
    public bool SendWhenEmpty { get; set; } = true;
}
