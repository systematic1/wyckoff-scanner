using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using WyckoffScanner.Lib.Analysis;
using WyckoffScanner.Lib.Configuration;
using WyckoffScanner.Lib.Data;
using WyckoffScanner.Lib.Loading;
using WyckoffScanner.Lib.Models;
using WyckoffScanner.Lib.Notifications;
using WyckoffScanner.Lib.YahooFinance;

namespace WyckoffScanner.Lib.Scheduling;

/// <summary>
/// Long-running host service. On every trading day (Mon-Fri, minus holidays) from the configured start time (6:00 AM):
///   loader (analyzer runs concurrently on each symbol as soon as it is loaded) -> e-mail
/// then sleeps until the next trading day's start time. Each stage is recorded in job_runs, so if the app is
/// stopped or crashes mid-day it resumes where it left off (it also catches up if started after 6:00 AM).
/// </summary>
public sealed class DailyPipelineWorker : BackgroundService
{
    private readonly TradingCalendar _calendar;
    private readonly JobRunRepository _jobs;
    private readonly DataLoaderService _loader;
    private readonly AnalysisService _analysis;
    private readonly EmailNotifier _email;
    private readonly RequestThrottle _throttle;
    private readonly ScheduleOptions _schedule;
    private readonly ILogger<DailyPipelineWorker> _log;

    public DailyPipelineWorker(
        TradingCalendar calendar, JobRunRepository jobs, DataLoaderService loader, AnalysisService analysis,
        EmailNotifier email, RequestThrottle throttle, IOptions<ScheduleOptions> schedule, ILogger<DailyPipelineWorker> log)
    {
        _calendar = calendar;
        _jobs = jobs;
        _loader = loader;
        _analysis = analysis;
        _email = email;
        _throttle = throttle;
        _schedule = schedule.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Wyckoff scanner started. Start time {Start} ({Zone}).", _calendar.StartTime, _calendar.TimeZone.Id);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nowLocal = _calendar.NowLocal();
                var today = DateOnly.FromDateTime(nowLocal);

                if (_calendar.IsTradingDay(today) && TimeOnly.FromDateTime(nowLocal) >= _calendar.StartTime)
                {
                    var job = await _jobs.GetOrCreateAsync(today, ct);
                    if (job.EmailSentAt == null)
                        await RunPipelineAsync(today, job, ct);
                }

                await SleepUntilNextRunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (RateLimitedException rl)
            {
                // Persist the cooldown; the loop re-enters the pipeline (resumable) and the throttle holds every
                // Yahoo call until the cooldown has passed - even across an app restart.
                _log.LogWarning("{Message}", rl.Message);
                await _throttle.ReportRateLimitedAsync(rl.RetryAfter, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipeline failed; will resume in {Minutes} minute(s).", _schedule.FailureRetryMinutes);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_schedule.FailureRetryMinutes), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _log.LogInformation("Wyckoff scanner stopped.");
    }

    private async Task RunPipelineAsync(DateOnly today, JobRun job, CancellationToken ct)
    {
        _log.LogInformation("PIPELINE:  === Pipeline for {Date:yyyy-MM-dd} ===", today);

        bool needLoader = job.LoaderCompletedAt == null;
        bool needAnalysis = job.AnalysisCompletedAt == null;

        if (needLoader && needAnalysis)
        {
            await LoadAndAnalyzeConcurrentlyAsync(today, ct);
        }
        else if (needLoader)
        {
            await _loader.RunAsync(today, ct);
            await _jobs.MarkStageCompleteAsync(today, JobStage.Loader, ct);
        }
        else if (needAnalysis)
        {
            // Loader finished earlier (e.g. before a restart): analyse whatever has no result yet.
            await _analysis.RunAsync(today, ct);
            await _jobs.MarkStageCompleteAsync(today, JobStage.Analysis, ct);
        }

        await _email.SendDailyReportAsync(today, ct);
        await _jobs.MarkStageCompleteAsync(today, JobStage.Email, ct);

        _log.LogInformation("PIPELINE:  === Pipeline for {Date:yyyy-MM-dd} complete; going dormant. ===", today);
    }

    /// <summary>
    /// The loader pushes each symbol onto a channel the moment it is up to date; analysis workers consume the channel
    /// in parallel, so analysis overlaps with the (slow, throttled) loading of the remaining symbols. If the loader
    /// fails (e.g. HTTP 429) the already-queued symbols are still analysed before the error is rethrown, and the
    /// next attempt resumes: finished symbols are skipped by both the loader and the analyzer.
    /// </summary>
    private async Task LoadAndAnalyzeConcurrentlyAsync(DateOnly today, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<SymbolUpdate>();
        var analysisTask = _analysis.ProcessAsync(today, channel.Reader, ct);

        Exception? loaderError = null;
        try
        {
            await _loader.RunAsync(today, ct, (update, _) =>
            {
                channel.Writer.TryWrite(update);
                return Task.CompletedTask;
            });
            await _jobs.MarkStageCompleteAsync(today, JobStage.Loader, ct);
        }
        catch (Exception ex)
        {
            loaderError = ex;
        }
        finally
        {
            channel.Writer.TryComplete();   // lets the analysis workers drain what is queued and finish
        }

        try
        {
            await analysisTask;
        }
        catch when (loaderError != null)
        {
            // The loader's failure is the primary problem (rate limit, cancellation...); analysis errors were already logged.
        }

        if (loaderError != null)
            ExceptionDispatchInfo.Capture(loaderError).Throw();

        await _jobs.MarkStageCompleteAsync(today, JobStage.Analysis, ct);
    }

    private async Task SleepUntilNextRunAsync(CancellationToken ct)
    {
        var target = _calendar.NextRunTimeLocal(_calendar.NowLocal());
        _log.LogInformation("Dormant until {Next:yyyy-MM-dd HH:mm} ({Zone}).", target, _calendar.TimeZone.Id);

        while (!ct.IsCancellationRequested)
        {
            var remaining = target - _calendar.NowLocal();
            if (remaining <= TimeSpan.Zero) 
                return;
            
            // Wake periodically so clock changes / DST shifts can't strand us.
            await Task.Delay(remaining < TimeSpan.FromMinutes(30) ? remaining : TimeSpan.FromMinutes(30), ct);
        }
    }
}
