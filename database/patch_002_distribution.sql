-- Patch 002: distribution (reverse polarity) support. Run once on an existing database:
--   psql -d wyckoff -f patch_002_distribution.sql
-- (Already included in schema.sql for new installs; safe to run twice.)
BEGIN;

ALTER TABLE analysis_results ADD COLUMN IF NOT EXISTS pattern VARCHAR(12) NOT NULL DEFAULT 'None';

-- Existing rows with a flag were all accumulation results.
UPDATE analysis_results SET pattern = 'Accumulation' WHERE pattern = 'None' AND selling_exhaustion;

ALTER TABLE analysis_results DROP CONSTRAINT IF EXISTS ck_analysis_flag;
ALTER TABLE analysis_results ADD CONSTRAINT ck_analysis_flag CHECK (flag IN
    ('None','LikelyProgressing','SpringDetected','SosDetected',
     'LikelyDistribution','UpthrustDetected','SowDetected'));

ALTER TABLE analysis_results DROP CONSTRAINT IF EXISTS ck_analysis_pattern;
ALTER TABLE analysis_results ADD CONSTRAINT ck_analysis_pattern CHECK (pattern IN ('None','Accumulation','Distribution'));

DROP VIEW IF EXISTS v_flagged_symbols;
CREATE VIEW v_flagged_symbols AS
SELECT a.analysis_date, s.ticker, a.pattern, a.flag_message, a.support_price, a.resistance_price,
       a.last_close, a.details
FROM   analysis_results a
JOIN   symbols s ON s.symbol_id = a.symbol_id
WHERE  a.flag <> 'None';

COMMIT;
