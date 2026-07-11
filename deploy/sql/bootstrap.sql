-- ============================================================
-- vouchfx Telemetry Backend — PostgreSQL 16 bootstrap
-- Target: PostgreSQL 16 on Azure Database for PostgreSQL Flexible Server
--
-- PURPOSE
--   Bootstraps all tables, indexes, functions, views, and
--   initial partitions required by the vouchfx telemetry
--   ingest service.
--
-- PARTITION STRATEGY — why event_timestamp, not received_at
--   PostgreSQL requires the partition key to appear in every
--   UNIQUE constraint (and therefore in the PRIMARY KEY) of a
--   partitioned table.  The natural, privacy-compliant dedup
--   key for a telemetry row is
--     (install_id, event_timestamp, schema_version).
--   Using event_timestamp (the client-reported run-end time)
--   as the partition key makes that triple a legal PRIMARY KEY
--   AND guarantees that a re-sent batch — which carries the
--   identical client timestamp — lands in the same partition
--   as the original, causing the PK conflict to fire and the
--   duplicate to be silently discarded (ON CONFLICT DO NOTHING).
--
-- DEDUP MODEL — two complementary layers
--   1. Batch level  — ingest_batch.idempotency_key (SHA-256
--      hex of the raw request body) prevents a duplicated
--      HTTP POST from being processed at all.  0 rows affected
--      on insert → batch already processed → service returns
--      HTTP 200 immediately.
--   2. Row level    — ON CONFLICT (install_id, event_timestamp,
--      schema_version) DO NOTHING on telemetry_event handles
--      the case where two semantically identical rows arrive in
--      different batches (e.g. after the outbox re-batches on
--      a client restart).
--
-- IDEMPOTENCY
--   This script is fully re-runnable on an existing database:
--   CREATE TABLE IF NOT EXISTS, CREATE INDEX IF NOT EXISTS,
--   CREATE OR REPLACE FUNCTION, CREATE OR REPLACE VIEW.
--   Running it a second time produces no error and leaves the
--   schema unchanged.
-- ============================================================


-- ============================================================
-- 1. TELEMETRY_EVENT  (daily RANGE-partitioned parent)
-- ============================================================
CREATE TABLE IF NOT EXISTS telemetry_event (
    -- identity / dedup
    install_id              uuid        NOT NULL,
    event_timestamp         timestamptz NOT NULL,   -- partition key; client run-end time
    schema_version          int         NOT NULL,
    -- server-side audit only; not part of the dedup key
    received_at             timestamptz NOT NULL DEFAULT now(),
    -- client versions
    tool_version            text        NOT NULL,
    engine_version          text        NOT NULL,
    dotnet_version          text        NOT NULL,
    -- run counts
    run_count               int         NOT NULL,
    scenario_count          int         NOT NULL,
    -- step verdict counts
    step_pass               int         NOT NULL,
    step_fail               int         NOT NULL,
    step_env_error          int         NOT NULL,
    step_inconclusive       int         NOT NULL,
    -- scenario verdict counts
    scenario_pass           int         NOT NULL,
    scenario_fail           int         NOT NULL,
    scenario_env_error      int         NOT NULL,
    scenario_inconclusive   int         NOT NULL,
    -- step-family and step-provider usage maps
    -- e.g. step_families = {"http":3,"db-assert":1}
    --      step_providers = {"http.rest":3,"db-assert.postgres":1}
    step_families           jsonb       NOT NULL,
    step_providers          jsonb       NOT NULL,
    -- timing in milliseconds
    startup_ms              bigint      NOT NULL,
    time_to_first_test_ms   bigint      NOT NULL,

    -- Natural dedup key.
    -- event_timestamp is included because Postgres requires the
    -- partition key to appear in every unique constraint on a
    -- partitioned table.
    PRIMARY KEY (install_id, event_timestamp, schema_version)
)
PARTITION BY RANGE (event_timestamp);

-- Default (catch-all) partition.
-- Accepts rows whose event_timestamp does not fall in any
-- explicit day partition — typically from clients with broken
-- clocks or extremely skewed timestamps.  The sweep_default()
-- function prunes old rows from this partition on the daily
-- purge run.
CREATE TABLE IF NOT EXISTS telemetry_event_default
    PARTITION OF telemetry_event DEFAULT;


-- ============================================================
-- 2. INGEST_BATCH  (batch-level idempotency; NOT partitioned)
-- ============================================================
CREATE TABLE IF NOT EXISTS ingest_batch (
    -- SHA-256 hex digest of the raw HTTP request body (64 chars).
    -- The service inserts this before processing the batch; a
    -- conflict means the batch is a duplicate and can be skipped.
    idempotency_key char(64)    NOT NULL,
    -- First line's installId; stored so that a forget() request
    -- can scrub batch records tied to an install even when the
    -- event rows have already been aged out.
    install_id      uuid        NULL,
    line_count      int         NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (idempotency_key)
);

-- Used by the daily purge job to delete batches older than the
-- dedup retention window (default 35 days — beyond the 30-day
-- client outbox cap plus back-off headroom).
CREATE INDEX IF NOT EXISTS idx_ingest_batch_received_at
    ON ingest_batch (received_at);

-- Used by the forget drain to scrub batch records by install.
CREATE INDEX IF NOT EXISTS idx_ingest_batch_install_id
    ON ingest_batch (install_id)
    WHERE install_id IS NOT NULL;


-- ============================================================
-- 3. FORGET_QUEUE
-- ============================================================
CREATE TABLE IF NOT EXISTS forget_queue (
    install_id   uuid        NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz NULL,
    PRIMARY KEY (install_id)
);

-- Partial index: only un-processed requests are queried by
-- the background forget worker.
CREATE INDEX IF NOT EXISTS idx_forget_queue_unprocessed
    ON forget_queue (install_id)
    WHERE processed_at IS NULL;


-- ============================================================
-- 4. FUNCTIONS
-- ============================================================

-- ------------------------------------------------------------
-- 4a. ensure_partition(d date)
--
--     Creates the single-day child partition for day d if it
--     does not already exist.  The child table name is derived
--     exclusively from the server-computed date parameter using
--     to_char(), so no untrusted input ever reaches the dynamic
--     DDL — DDL injection is structurally impossible.
--
--     Example: ensure_partition('2024-01-15') creates
--       telemetry_event_20240115 PARTITION OF telemetry_event
--         FOR VALUES FROM ('2024-01-15') TO ('2024-01-16')
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION ensure_partition(d date)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    child_name text := 'telemetry_event_' || to_char(d, 'YYYYMMDD');
BEGIN
    -- %I safely double-quotes the identifier; %L produces a
    -- properly escaped SQL string literal.  Both arguments are
    -- derived from a typed date parameter — no user input.
    EXECUTE format(
        'CREATE TABLE IF NOT EXISTS %I'
        ' PARTITION OF telemetry_event'
        ' FOR VALUES FROM (%L) TO (%L)',
        child_name,
        d::text,
        (d + 1)::text
    );
END;
$$;


-- ------------------------------------------------------------
-- 4b. ensure_partitions(from_date date, to_date date)
--
--     Calls ensure_partition for every day in the half-open
--     range [from_date, to_date).  Idempotent: IF NOT EXISTS
--     inside ensure_partition makes repeated calls safe.
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION ensure_partitions(from_date date, to_date date)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    d date := from_date;
BEGIN
    WHILE d < to_date LOOP
        PERFORM ensure_partition(d);
        d := d + 1;
    END LOOP;
END;
$$;


-- ------------------------------------------------------------
-- 4c. drop_old_partitions(retention_days int)
--
--     Drops every direct child partition of telemetry_event
--     whose upper bound satisfies:
--
--       upper_bound <= current_date - retention_days
--
--     IDENTIFICATION STRATEGY
--       Uses pg_get_expr(c.relpartbound, c.oid) to obtain
--       the human-readable bound expression string, then
--       extracts the upper-bound timestamp with regexp_match.
--       This is more robust than parsing the partition name
--       suffix because it does not depend on our YYYYMMDD
--       naming convention being applied uniformly — any
--       partition, however created, is correctly evaluated.
--
--       pg_get_expr returns text of the form:
--         FOR VALUES FROM ('2024-01-01 00:00:00+00')
--                      TO ('2024-01-02 00:00:00+00')
--       The regex extracts the substring inside the final
--       pair of parentheses and single quotes.
--
--     DEFAULT PARTITION SAFETY
--       Reads pg_partitioned_table.partdefid to obtain the
--       OID of the DEFAULT partition and unconditionally
--       excludes it from the loop.  The DEFAULT partition's
--       bound expression does NOT contain a TO clause, so the
--       regex returns NULL and the loop would already skip it;
--       the OID exclusion is a belt-and-suspenders guarantee.
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION drop_old_partitions(retention_days int)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    cutoff_date  date := current_date - retention_days;
    parent_oid   oid;
    default_oid  oid;
    rec          record;
    upper_match  text[];
    upper_date   date;
BEGIN
    -- Resolve the parent table OID within the current schema.
    SELECT c.oid INTO STRICT parent_oid
    FROM   pg_class     c
    JOIN   pg_namespace n ON n.oid = c.relnamespace
    WHERE  c.relname   = 'telemetry_event'
      AND  n.nspname   = current_schema();

    -- Obtain the DEFAULT partition OID; NULL when none exists.
    -- partdefid is 0 (not NULL) when there is no default partition.
    SELECT NULLIF(p.partdefid, 0) INTO default_oid
    FROM   pg_partitioned_table p
    WHERE  p.partrelid = parent_oid;

    -- Walk every direct child partition, skipping the DEFAULT.
    FOR rec IN
        SELECT i.inhrelid                          AS child_oid,
               c.relname                           AS child_name,
               pg_get_expr(c.relpartbound, c.oid)  AS bound_expr
        FROM   pg_inherits i
        JOIN   pg_class    c ON c.oid = i.inhrelid
        WHERE  i.inhparent = parent_oid
          AND  (default_oid IS NULL OR i.inhrelid <> default_oid)
    LOOP
        -- Extract the upper bound timestamp from the bound expression.
        -- POSIX ERE pattern:  TO \('([^']+)'\)
        --   \(   — literal open-paren
        --   '    — opening single quote
        --   ([^']+) — capture: one or more non-quote chars (the timestamp)
        --   '    — closing single quote
        --   \)   — literal close-paren
        upper_match := regexp_match(
            rec.bound_expr,
            $re$TO \('([^']+)'\)$re$
        );

        IF upper_match IS NULL OR upper_match[1] IS NULL THEN
            CONTINUE;   -- no TO clause (e.g. unexpected format); skip
        END IF;

        BEGIN
            -- Cast timestamptz string to date; server timezone is UTC
            -- on Azure PostgreSQL so this is unambiguous.
            upper_date := upper_match[1]::timestamptz::date;
        EXCEPTION WHEN OTHERS THEN
            CONTINUE;   -- malformed timestamp; skip rather than abort
        END;

        IF upper_date <= cutoff_date THEN
            -- %I quotes the child table name; child_name comes from
            -- pg_class.relname (system catalog), not user input.
            EXECUTE format('DROP TABLE IF EXISTS %I', rec.child_name);
            RAISE NOTICE 'Dropped old partition: %', rec.child_name;
        END IF;
    END LOOP;
END;
$$;


-- ------------------------------------------------------------
-- 4d. sweep_default(retention_days int)
--
--     Deletes rows from the DEFAULT catch-all partition whose
--     event_timestamp is older than the retention window.
--     Targets clients with broken clocks or extreme timestamps
--     that miss every explicit day partition.
--
--     Returns the number of rows deleted (useful for logging).
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION sweep_default(retention_days int)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE
    deleted bigint;
BEGIN
    DELETE FROM telemetry_event_default
    WHERE  event_timestamp < now() - make_interval(days => retention_days);
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
END;
$$;


-- ============================================================
-- 5. DASHBOARD VIEWS
-- ============================================================

-- ------------------------------------------------------------
-- 5a. v_step_family_daily
--     Daily usage count per step family.
--     Unnests the step_families JSONB map so each family
--     (e.g. "http", "db-assert", "mq-publish") is a row.
-- ------------------------------------------------------------
CREATE OR REPLACE VIEW v_step_family_daily AS
SELECT
    date_trunc('day', event_timestamp)  AS day,
    kv.key                              AS family,
    SUM(kv.value::int)                  AS n
FROM  telemetry_event,
      LATERAL jsonb_each_text(step_families) AS kv
GROUP BY 1, 2;


-- ------------------------------------------------------------
-- 5b. v_step_provider_daily
--     Daily usage count per step provider.
--     Unnests the step_providers JSONB map so each provider
--     (e.g. "http.rest", "db-assert.postgres") is a row.
-- ------------------------------------------------------------
CREATE OR REPLACE VIEW v_step_provider_daily AS
SELECT
    date_trunc('day', event_timestamp)  AS day,
    kv.key                              AS provider,
    SUM(kv.value::int)                  AS n
FROM  telemetry_event,
      LATERAL jsonb_each_text(step_providers) AS kv
GROUP BY 1, 2;


-- ------------------------------------------------------------
-- 5c. v_run_daily
--     Per-day rollup: run/scenario counts, verdict sums,
--     startup and time-to-first-test percentiles (p50/p95),
--     and distinct install count.
-- ------------------------------------------------------------
CREATE OR REPLACE VIEW v_run_daily AS
SELECT
    date_trunc('day', event_timestamp)                               AS day,
    COUNT(*)                                                          AS events,
    SUM(run_count)                                                    AS runs,
    SUM(scenario_count)                                               AS scenarios,
    -- step verdicts
    SUM(step_pass)                                                    AS step_pass,
    SUM(step_fail)                                                    AS step_fail,
    SUM(step_env_error)                                               AS step_env_error,
    SUM(step_inconclusive)                                            AS step_inconclusive,
    -- scenario verdicts
    SUM(scenario_pass)                                                AS scenario_pass,
    SUM(scenario_fail)                                                AS scenario_fail,
    SUM(scenario_env_error)                                           AS scenario_env_error,
    SUM(scenario_inconclusive)                                        AS scenario_inconclusive,
    -- startup timing percentiles (milliseconds)
    percentile_cont(0.5)  WITHIN GROUP (ORDER BY startup_ms)         AS startup_ms_p50,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY startup_ms)         AS startup_ms_p95,
    -- time-to-first-test timing percentiles (milliseconds)
    percentile_cont(0.5)  WITHIN GROUP (ORDER BY time_to_first_test_ms)
                                                                      AS time_to_first_test_ms_p50,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY time_to_first_test_ms)
                                                                      AS time_to_first_test_ms_p95,
    -- distinct installs active that day
    COUNT(DISTINCT install_id)                                        AS distinct_installs
FROM  telemetry_event
GROUP BY 1;


-- ============================================================
-- 6. BOOTSTRAP PARTITIONS
--
--    Creates partitions covering:
--      - the trailing 90 days  (handles any backlog in the
--        client 30-day outbox + generous clock-skew margin)
--      - today and the next 6 days  (ensures the current day
--        and near-future days are pre-created before any
--        INSERT arrives)
--
--    This call is fully idempotent because ensure_partition
--    uses CREATE TABLE IF NOT EXISTS internally.  Running
--    bootstrap.sql again when partitions already exist is
--    harmless.
-- ============================================================
SELECT ensure_partitions(current_date - 90, current_date + 7);
