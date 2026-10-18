# Wyckoff Scanner (.NET 8 console app + PostgreSQL)

Loads daily OHLCV data from Yahoo! Finance, stores it in PostgreSQL, scans every symbol for the Wyckoff
accumulation **and distribution** sequences once a day, and e-mails the symbols that are flagged.

```
WyckoffScanner.sln                 <- open this in JetBrains Rider
database/schema.sql                <- PostgreSQL schema (single script)
WyckoffScanner/
  Program.cs                       DI / host setup
  appsettings.json                 all configuration
  Configuration/Options.cs         strongly-typed settings
  Data/                            Npgsql + Dapper repositories
  YahooFinance/                    HTML-scrape source, JSON-chart source, 3-minute throttle, URL builder
  Loading/DataLoaderService.cs     1-year back-load + previous-trading-day load
  Analysis/                        WyckoffAnalyzer (+ AnalyzerOptions with every threshold)
  Notifications/EmailNotifier.cs   MailKit SMTP e-mail (HTML + plain text)
  Scheduling/                      TradingCalendar + DailyPipelineWorker (the 6:00 AM loop)
```

## Setup

1. **Database**
   ```
   createdb wyckoff
   psql -d wyckoff -f database/schema.sql
   # existing install? run database/patch_001_throttle.sql and database/patch_002_distribution.sql instead
   psql -d wyckoff -c "INSERT INTO symbols (ticker) VALUES ('AAPL'),('MSFT'),('NVDA');"
   ```
   Tickers must be upper case, spelled as Yahoo spells them (`BRK-B`).
2. **Configure** `WyckoffScanner/appsettings.json`: `Database:ConnectionString`, the `Email` section
   (SMTP host/port/credentials, `From`, `To`), and `Schedule:TimeZoneId`.
   Keep passwords out of the file, e.g.
   `export Email__Password=...` / `export Database__ConnectionString=...`
   (or `dotnet user-secrets` with `DOTNET_ENVIRONMENT=Development`).
3. **Run** from Rider (open `WyckoffScanner.sln`, run the `WyckoffScanner` project) or
   `dotnet run --project WyckoffScanner`. Leave it running (service / container / tmux).

## What it does each trading day

| Step | Behaviour |
|------|-----------|
| Schedule | Runs Mon-Fri (and skips dates in `Schedule:Holidays`) from 06:00 in `Schedule:TimeZoneId`. If started later in the day it catches up; each stage is recorded in `job_runs`, so a restart resumes instead of repeating. |
| Back-load | Any symbol with `backfill_completed_at IS NULL` gets 1 year of daily bars (one call). New symbols you add later are back-loaded automatically. |
| Daily load | Every symbol gets the previous trading day (one call each). Symbols already covered (`load_log`) cost no call. |
| Throttle | At least **3 minutes** between any two Yahoo calls (`Yahoo:DelayBetweenCallsMinutes`). **Runtime = 3 min x number of calls** - 100 symbols is about 5 hours, 300 symbols about 15 hours. Keep the list sized so it finishes before you need the e-mail. |
| Rate-limit protection | The last-call time is stored in `throttle_state`, so a restart does not reset the 3-minute clock. A random 0-`Yahoo:JitterSeconds` (default 30) is added to every gap. On **HTTP 429** the app does not retry: it pauses *all* Yahoo calls for `RateLimitCooldownMinutes` (default 60, or Retry-After if longer), doubling for each consecutive 429 up to `MaxCooldownMinutes` (default 720). The cooldown is also persisted, then the pipeline resumes where it stopped. |
| De-duplication | `UNIQUE (symbol_id, trade_date)` + `ON CONFLICT DO NOTHING`: an existing symbol/date is never inserted again. |
| Analyzer | Runs **concurrently with the loader**: as soon as a symbol is up to date it is queued (in-memory channel) and `Analyzer:MaxConcurrency` (default 4) workers read its last year from the DB, analyse it and save the result immediately. Symbols that need no Yahoo call are queued first. After a crash/429 the run resumes: symbols that already have today's result are skipped. |
| E-mail | One e-mail listing each flagged symbol and its flag. `Email:SendWhenEmpty` controls whether an empty day still sends a note. |

### URL notes
* The spec's first URL says `/quotes/`; Yahoo's real path is **`/quote/`** (used as the default, `Yahoo:QuoteBaseUrl`).
* Both back-load and daily load use the `/history/?period1=..&period2=..&interval=1d&filter=history&frequency=1d` URL.
  The plain quote page only shows the current session, not a clean previous-day OHLCV row, so the daily load requests
  a one-day window of the history page instead.
* `period2` is sent as 00:00 UTC of the day *after* the previous trading day, because Yahoo treats it as exclusive;
  otherwise the previous trading day's bar itself would be cut off.

### Yahoo caveats (please read)
* Scraping the HTML page is inherently fragile and Yahoo may rate-limit, block, or change the markup
  (the parser looks for table rows with 7 cells: Date, Open, High, Low, Close, Adj Close, Volume).
  If you get repeated "No `<table>` found"/HTTP 429 errors, set `"Yahoo": { "Source": "ChartApi" }` to use Yahoo's JSON
  chart endpoint, which is more stable (same throttling applies). Also review Yahoo's terms of use for your use case.
* Failed symbols are logged in `load_log` (status `failed`), retried on the next run, and never block the analysis.

## Wyckoff detection (how the flags are decided)

All logic is in `Analysis/` (`PatternEngine` = the detectors, `WyckoffAnalyzer` = runs both directions and reports);
**every threshold is in `AnalyzerOptions` and overridable under `"Analyzer"` in appsettings.json.** Every swing point is
tried as a candidate climax, the later phases are searched strictly in sequence, and the candidate that progressed
furthest (then most recent) is reported.

| # | Accumulation (bullish) | Distribution (reverse polarity) |
|---|------------------------|---------------------------------|
| 1 | **Selling exhaustion** - decline >= 15% (<= 60 bars), smoothed daily range peaked >= 1.5x normal on >= 1.3x median volume, then faded to <= 70% of peak into the low | **Buying climax** - run-up >= 15% (`MinAdvancePct`), same range/volume/fade rules into the high |
| 2 | **Automatic rally** - within 20 bars: >= 6% bounce, recovers >= 20% of the decline, on 0.5-1.5x median volume, quieter than the climax | **Automatic reaction** - within 20 bars: >= 6% drop (`MinReactionPct`), same volume rules |
| 3 | **Accumulation** - >= 10-bar boxes, <= 12% high-to-low, volume <= 0.9x median; a close >8% below the climax low invalidates it | **Distribution** - same boxes below the climax high; a close >8% above the climax high invalidates it |
| 4 | **Spring** - in a tight range (<= 15%, >= 2 touches each side) a bar undercuts support by 0.3%-6% and closes back above within 3 bars | **Upthrust (UTAD)** - a bar exceeds resistance by 0.3%-6% and closes back below within 3 bars |
| 5 | **Sign of strength** - up-bar closes >0.5% above resistance, >= 1.3x 20-day volume, at least average range | **Sign of weakness** - down-bar closes >0.5% below support, same volume/range rules |

Distribution is implemented as the mirror image of accumulation: the same detectors run on a price-inverted copy of the
series (price' = 1/price; volume unchanged), with the percentage thresholds converted so they keep their natural
meaning (`AnalyzerOptions.ForDistribution`). Set `Analyzer:DetectDistribution` to `false` to turn it off.

| Flag | Pattern | Rule |
|------|---------|------|
| **Likely progressing** | Accumulation | 1-3, latest box ended within 20 bars |
| **Spring detected** | Accumulation | 1-4, spring within 30 bars, support not broken |
| **SoS detected** | Accumulation | 1-5, SoS within 10 bars and price still holding above resistance |
| **Distribution likely progressing** | Distribution | 1-3, latest box ended within 20 bars |
| **Upthrust detected** | Distribution | 1-4, upthrust within 30 bars, resistance not decisively cleared |
| **SoW detected** | Distribution | 1-5, SoW within 10 bars and price still holding below support |

A symbol gets one flag. If both patterns are present, the one whose latest event is more recent is reported. In
`analysis_results` the `pattern` column says which; for distribution rows the stage columns mean buying climax /
automatic reaction / distribution box / upthrust / sign of weakness. The e-mail has a Pattern column (green =
accumulation, red = distribution). These are rule-of-thumb heuristics for screening, not trading signals. Not investment advice.

## Holidays
`Schedule:Holidays` ships with NYSE dates for late 2026-2027 - verify them and extend each year. An empty list means pure Mon-Fri.

## Useful SQL
```sql
SELECT * FROM v_flagged_symbols WHERE analysis_date = current_date ORDER BY flag_message, ticker;
SELECT s.ticker, l.target_date, l.status, l.message FROM load_log l JOIN symbols s USING (symbol_id) WHERE l.status = 'failed';
UPDATE symbols SET is_active = FALSE WHERE ticker = 'XYZ';   -- stop loading/analysing a symbol
```
