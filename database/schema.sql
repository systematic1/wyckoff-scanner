-- =====================================================================
-- Wyckoff Scanner - PostgreSQL schema (PostgreSQL 13+)
--
-- Usage:
--   createdb wyckoff
--   psql -d wyckoff -f schema.sql
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- symbols: the watch list. Insert tickers here; the app does the rest.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS symbols (
    symbol_id             INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ticker                VARCHAR(20)  NOT NULL,
    is_active             BOOLEAN      NOT NULL DEFAULT TRUE,
    backfill_completed_at TIMESTAMPTZ  NULL,      -- set once the 1-year back-load succeeded
    created_at            TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_symbols_ticker UNIQUE (ticker),
    CONSTRAINT ck_symbols_ticker_upper CHECK (ticker = upper(ticker))
);

-- ---------------------------------------------------------------------
-- daily_quotes: OHLCV history for every symbol (back-load + daily loads)
-- The unique constraint guarantees we never store a (symbol, date) twice.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS daily_quotes (
    quote_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    symbol_id        INTEGER       NOT NULL REFERENCES symbols (symbol_id) ON DELETE CASCADE,
    trade_date       DATE          NOT NULL,
    open_price       NUMERIC(18,6) NOT NULL,
    high_price       NUMERIC(18,6) NOT NULL,
    low_price        NUMERIC(18,6) NOT NULL,
    close_price      NUMERIC(18,6) NOT NULL,
    adj_close_price  NUMERIC(18,6) NULL,
    volume           BIGINT        NOT NULL,
    created_at       TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT uq_daily_quotes_symbol_date UNIQUE (symbol_id, trade_date),
    CONSTRAINT ck_daily_quotes_prices CHECK (high_price >= low_price AND volume >= 0)
);

CREATE INDEX IF NOT EXISTS ix_daily_quotes_trade_date ON daily_quotes (trade_date);

-- ---------------------------------------------------------------------
-- load_log: one row per (symbol, target trading day) so a restarted app
-- does not re-call Yahoo for symbols already handled (incl. holidays
-- where Yahoo legitimately returns no row).
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS load_log (
    load_log_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    symbol_id      INTEGER      NOT NULL REFERENCES symbols (symbol_id) ON DELETE CASCADE,
    target_date    DATE         NOT NULL,          -- the "previous trading day" that was requested
    mode           VARCHAR(10)  NOT NULL,          -- 'backfill' | 'daily'
    status         VARCHAR(10)  NOT NULL,          -- 'success' | 'no_data' | 'failed'
    rows_inserted  INTEGER      NOT NULL DEFAULT 0,
    message        TEXT         NULL,
    loaded_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT uq_load_log_symbol_target UNIQUE (symbol_id, target_date),
    CONSTRAINT ck_load_log_mode   CHECK (mode   IN ('backfill', 'daily')),
    CONSTRAINT ck_load_log_status CHECK (status IN ('success', 'no_data', 'failed'))
);

-- ---------------------------------------------------------------------
-- job_runs: one row per trading day; makes the pipeline resumable
-- (loader -> analyzer -> email) if the app is restarted mid-day.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS job_runs (
    run_date              DATE        PRIMARY KEY,
    started_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    loader_completed_at   TIMESTAMPTZ NULL,
    analysis_completed_at TIMESTAMPTZ NULL,
    email_sent_at         TIMESTAMPTZ NULL
);

-- ---------------------------------------------------------------------
-- throttle_state: single row. Persists the last Yahoo call time and any
-- rate-limit (HTTP 429) cooldown so restarting the app cannot bypass them.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS throttle_state (
    id                SMALLINT    PRIMARY KEY,
    last_call_utc     TIMESTAMPTZ NULL,
    blocked_until_utc TIMESTAMPTZ NULL,
    CONSTRAINT ck_throttle_single_row CHECK (id = 1)
);
INSERT INTO throttle_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- analysis_results: one row per (symbol, analysis day)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS analysis_results (
    analysis_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    symbol_id          INTEGER       NOT NULL REFERENCES symbols (symbol_id) ON DELETE CASCADE,
    analysis_date      DATE          NOT NULL,
    pattern            VARCHAR(12)   NOT NULL DEFAULT 'None',   -- None | Accumulation | Distribution
    flag               VARCHAR(20)   NOT NULL,   -- None | LikelyProgressing | SpringDetected | SosDetected
                                                 --      | LikelyDistribution | UpthrustDetected | SowDetected
    flag_message       VARCHAR(40)   NULL,
    -- For pattern = 'Distribution' the stage columns hold the mirror-image events:
    --   selling_exhaustion = buying climax, automatic_rally = automatic reaction,
    --   accumulation = distribution box(es), spring = upthrust, sign_of_strength = sign of weakness.
    selling_exhaustion BOOLEAN       NOT NULL DEFAULT FALSE,
    automatic_rally    BOOLEAN       NOT NULL DEFAULT FALSE,
    accumulation       BOOLEAN       NOT NULL DEFAULT FALSE,
    accumulation_count INTEGER       NOT NULL DEFAULT 0,
    spring             BOOLEAN       NOT NULL DEFAULT FALSE,
    sign_of_strength   BOOLEAN       NOT NULL DEFAULT FALSE,
    support_price      NUMERIC(18,6) NULL,
    resistance_price   NUMERIC(18,6) NULL,
    last_close         NUMERIC(18,6) NULL,
    bars_analyzed      INTEGER       NOT NULL DEFAULT 0,
    details            TEXT          NULL,
    created_at         TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT uq_analysis_symbol_date UNIQUE (symbol_id, analysis_date),
    CONSTRAINT ck_analysis_flag CHECK (flag IN ('None','LikelyProgressing','SpringDetected','SosDetected',
                                                      'LikelyDistribution','UpthrustDetected','SowDetected')),
    CONSTRAINT ck_analysis_pattern CHECK (pattern IN ('None','Accumulation','Distribution'))
);

CREATE INDEX IF NOT EXISTS ix_analysis_date_flag ON analysis_results (analysis_date, flag);

-- Convenience view: flagged symbols per analysis day
CREATE OR REPLACE VIEW v_flagged_symbols AS
SELECT a.analysis_date, s.ticker, a.pattern, a.flag_message, a.support_price, a.resistance_price,
       a.last_close, a.details
FROM   analysis_results a
JOIN   symbols s ON s.symbol_id = a.symbol_id
WHERE  a.flag <> 'None';

COMMIT;

-- ---------------------------------------------------------------------
-- Add your watch list (uncomment / edit). Tickers must be upper case,
-- exactly as Yahoo! Finance spells them (e.g. BRK-B, not BRK.B).
-- ---------------------------------------------------------------------
-- INSERT INTO symbols (ticker) VALUES
--     ('AAPL'), ('MSFT'), ('NVDA'), ('AMD'), ('INTC')
-- ON CONFLICT (ticker) DO NOTHING;
