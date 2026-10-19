using Microsoft.Extensions.Options;
using WyckoffScanner.Configuration;
using WyckoffScanner.Data;

namespace WyckoffScanner.YahooFinance;

/// <summary>
/// Enforces the gap between calls to Yahoo! Finance:
///  * at least <c>DelayBetweenCallsMinutes</c> (3) between calls, plus 0..<c>JitterSeconds</c> random extra so the
///    cadence is not machine-perfect;
///  * the last-call time is persisted in PostgreSQL, so restarting the app does not reset the clock;
///  * after an HTTP 429 the throttle blocks all calls until a cooldown (also persisted) has passed. The cooldown
///    is Retry-After if Yahoo sent it, else RateLimitCooldownMinutes, doubling for each consecutive 429 up to
///    MaxCooldownMinutes.
/// Used from a single pipeline thread; the first call after a long pause goes out immediately.
/// </summary>
public sealed class RequestThrottle
{
    private readonly TimeSpan _interval;
    private readonly double _jitterSeconds;
    private readonly YahooOptions _options;
    private readonly ThrottleRepository _repo;
    private readonly ILogger<RequestThrottle> _log;

    private bool _loaded;
    private DateTime _lastCallUtc = DateTime.MinValue;
    private DateTime _blockedUntilUtc = DateTime.MinValue;
    private int _consecutiveRateLimits;

    public RequestThrottle(IOptions<YahooOptions> options, ThrottleRepository repo, ILogger<RequestThrottle> log)
    {
        _options = options.Value;
        _interval = TimeSpan.FromMinutes(_options.DelayBetweenCallsMinutes);
        _jitterSeconds = Math.Max(0, _options.JitterSeconds);
        _repo = repo;
        _log = log;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            var state = await _repo.GetAsync(ct);
            if (state.LastCallUtc.HasValue) 
                _lastCallUtc = state.LastCallUtc.Value;
            if (state.BlockedUntilUtc.HasValue) 
                _blockedUntilUtc = state.BlockedUntilUtc.Value;
            
            _loaded = true;
        }

        var jitter = TimeSpan.FromSeconds(Random.Shared.NextDouble() * _jitterSeconds);
        var spacedAt = (_lastCallUtc == DateTime.MinValue ? DateTime.MinValue : _lastCallUtc + _interval + jitter);
        var readyAt = (spacedAt > _blockedUntilUtc ? spacedAt : _blockedUntilUtc);

        var wait = readyAt - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            if (_blockedUntilUtc > DateTime.UtcNow)
                _log.LogWarning("Rate-limit cooldown active; waiting {Minutes:F1} min before the next Yahoo! Finance call.", wait.TotalMinutes);
            else
                _log.LogInformation("Waiting {Seconds:F0}s before next Yahoo! Finance call...", wait.TotalSeconds);

            await Task.Delay(wait, ct);
        }

        _lastCallUtc = DateTime.UtcNow;
        await _repo.SetLastCallAsync(_lastCallUtc, ct);
    }

    /// <summary>Call after a successful response: resets the escalation counter.</summary>
    public void ReportSuccess() => _consecutiveRateLimits = 0;

    /// <summary>Call when Yahoo answered 429: blocks every further call until the cooldown ends (survives restarts).</summary>
    public async Task ReportRateLimitedAsync(TimeSpan? retryAfter, CancellationToken ct)
    {
        _consecutiveRateLimits++;

        double baseMinutes = _options.RateLimitCooldownMinutes * Math.Pow(2, Math.Min(_consecutiveRateLimits - 1, 10));
        var cooldown = TimeSpan.FromMinutes(Math.Min(baseMinutes, _options.MaxCooldownMinutes));
        if (retryAfter.HasValue && retryAfter.Value > cooldown) 
            cooldown = retryAfter.Value;

        _blockedUntilUtc = DateTime.UtcNow + cooldown;
        await _repo.SetBlockedUntilAsync(_blockedUntilUtc, ct);

        _log.LogWarning("Yahoo! Finance returned HTTP 429 ({Count} in a row). Pausing all requests for {Minutes:F0} minutes (until {Until:u}).",
            _consecutiveRateLimits, cooldown.TotalMinutes, _blockedUntilUtc);
    }
}
