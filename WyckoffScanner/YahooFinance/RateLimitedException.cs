using System.Net;

namespace WyckoffScanner.YahooFinance;

/// <summary>Yahoo answered HTTP 429 (Too Many Requests). Never retried immediately - the pipeline cools down instead.</summary>
public sealed class RateLimitedException : Exception
{
    /// <summary>Value of the Retry-After header, if Yahoo sent one.</summary>
    public TimeSpan? RetryAfter { get; }

    public RateLimitedException(string message, TimeSpan? retryAfter) 
        : base(message) => RetryAfter = retryAfter;

    public static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests;

    public static RateLimitedException FromResponse(HttpResponseMessage response, string ticker)
    {
        TimeSpan? retryAfter = null;
        var header = response.Headers.RetryAfter;
        if (header?.Delta != null)
            retryAfter = header.Delta;
        else if (header?.Date != null)
            retryAfter = header.Date.Value - DateTimeOffset.UtcNow;

        if (retryAfter.HasValue && retryAfter.Value < TimeSpan.Zero) 
            retryAfter = null;
        
        return new RateLimitedException($"Yahoo! Finance rate-limited the request for {ticker} (HTTP 429).", retryAfter);
    }
}
