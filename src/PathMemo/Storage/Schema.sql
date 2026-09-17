-- pathmemo database schema, version 1 (README section 11).
--
-- SQLite holds only what has to be queryable and long-lived: scan metadata,
-- aggregates that outlive their snapshot, audit findings and the operations
-- journal. The file tree itself lives in .pmsnap (README section 5.1) - a
-- million rows per scan would make a disk-space tool the biggest thing on the
-- disk.
--
-- Applied in one transaction by Database.Migrate when PRAGMA user_version is 0.
-- Tables for phases that are not implemented yet are created now on purpose:
-- one schema version is easier to reason about than six, and empty tables cost
-- a few hundred bytes.

CREATE TABLE scans (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at          TEXT    NOT NULL,           -- ISO-8601 UTC
    finished_at         TEXT,
    status              TEXT    NOT NULL,           -- running|completed|cancelled|failed
    scanner             TEXT    NOT NULL,           -- mft|walk|incremental
    flags               INTEGER NOT NULL DEFAULT 0, -- ScanFlags
    roots               TEXT    NOT NULL,           -- JSON array of root paths
    total_files         INTEGER NOT NULL DEFAULT 0,
    total_dirs          INTEGER NOT NULL DEFAULT 0,
    allocated_bytes     INTEGER NOT NULL DEFAULT 0, -- unique allocated
    logical_bytes       INTEGER NOT NULL DEFAULT 0,
    duration_ms         INTEGER,
    error_count         INTEGER NOT NULL DEFAULT 0,
    tool_version        TEXT    NOT NULL,
    snapshot_path       TEXT,                       -- NULL once retention removed it
    snapshot_bytes      INTEGER,
    note                TEXT
);

CREATE INDEX idx_scans_started ON scans(started_at DESC);

CREATE TABLE scan_volumes (
    scan_id           INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    letter            TEXT    NOT NULL,
    label             TEXT,
    filesystem        TEXT,
    volume_serial     INTEGER NOT NULL,
    volume_guid       TEXT,
    cluster_bytes     INTEGER NOT NULL,
    total_bytes       INTEGER NOT NULL,
    free_bytes        INTEGER NOT NULL,
    scanned_bytes     INTEGER NOT NULL,
    metadata_bytes    INTEGER,
    unaccounted_bytes INTEGER,
    usn_journal_id    INTEGER,
    next_usn          INTEGER,
    PRIMARY KEY (scan_id, letter)
);

-- Aggregates for multi-year charts. These outlive the snapshot they came from,
-- which is the whole reason they are rows and not recomputed on demand.
CREATE TABLE scan_category_totals (
    scan_id         INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    category        TEXT    NOT NULL,   -- media|archive|cache|source|document|app|system|other
    allocated_bytes INTEGER NOT NULL,
    file_count      INTEGER NOT NULL,
    PRIMARY KEY (scan_id, category)
);

CREATE TABLE scan_extension_totals (
    scan_id         INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    extension       TEXT    NOT NULL,
    allocated_bytes INTEGER NOT NULL,
    file_count      INTEGER NOT NULL,
    PRIMARY KEY (scan_id, extension)
);

CREATE TABLE audit_findings (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_id           INTEGER REFERENCES scans(id) ON DELETE CASCADE,
    probed_at         TEXT    NOT NULL,
    finding_id        TEXT    NOT NULL,   -- "vss.shadow-storage"
    volume            TEXT,
    used_bytes        INTEGER,
    reclaimable_bytes INTEGER,
    risk              TEXT    NOT NULL,
    recoverability    TEXT    NOT NULL,
    status            TEXT    NOT NULL,   -- measured|unknown|needsElevation|...
    detail_json       TEXT
);

CREATE INDEX idx_audit_scan ON audit_findings(scan_id, reclaimable_bytes DESC);

-- Deletion journal (P6). Created here so there is one schema version, not six.
CREATE TABLE delete_ops (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at         TEXT    NOT NULL,
    finished_at        TEXT,
    mode               TEXT    NOT NULL,   -- recycle|quarantine|permanent
    source             TEXT    NOT NULL,   -- tree|reclaim|dupes|audit|cli
    scan_id            INTEGER REFERENCES scans(id) ON DELETE SET NULL,
    item_count         INTEGER NOT NULL,
    predicted_bytes    INTEGER NOT NULL,
    actual_freed_bytes INTEGER,
    status             TEXT    NOT NULL,   -- completed|partial|failed|restored|purged
    quarantine_path    TEXT,
    purge_after        TEXT,
    reason             TEXT
);

CREATE INDEX idx_delete_ops_date ON delete_ops(started_at DESC);

CREATE TABLE delete_items (
    op_id         INTEGER NOT NULL REFERENCES delete_ops(id) ON DELETE CASCADE,
    seq           INTEGER NOT NULL,
    original_path TEXT    NOT NULL,
    stored_name   TEXT,
    size_bytes    INTEGER NOT NULL,
    content_hash  TEXT,
    result        TEXT    NOT NULL,       -- ok|skipped|failed
    hresult       INTEGER,
    message       TEXT,
    PRIMARY KEY (op_id, seq)
);

-- Dry runs are kept apart from real operations on purpose: a journal that mixes
-- "would have deleted" with "did delete" cannot be trusted when it matters.
CREATE TABLE dryrun_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ran_at      TEXT    NOT NULL,
    source      TEXT    NOT NULL,
    item_count  INTEGER NOT NULL,
    total_bytes INTEGER NOT NULL,
    detail_json TEXT
);

-- Hash cache (P8), keyed by file id rather than path so a rename does not
-- invalidate it.
CREATE TABLE file_hashes (
    volume_serial INTEGER NOT NULL,
    file_id_low   INTEGER NOT NULL,
    file_id_high  INTEGER NOT NULL,
    size_bytes    INTEGER NOT NULL,
    mtime_unix    INTEGER NOT NULL,
    hash_algo     TEXT    NOT NULL,
    full_hash     TEXT    NOT NULL,
    computed_at   TEXT    NOT NULL,
    last_used_at  TEXT    NOT NULL,
    PRIMARY KEY (volume_serial, file_id_low, file_id_high, size_bytes, mtime_unix, hash_algo)
);

CREATE INDEX idx_file_hashes_hash ON file_hashes(hash_algo, full_hash);
CREATE INDEX idx_file_hashes_lru  ON file_hashes(last_used_at);

CREATE TABLE dupe_runs (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    ran_at       TEXT    NOT NULL,
    scan_id      INTEGER REFERENCES scans(id) ON DELETE CASCADE,
    min_size     INTEGER NOT NULL,
    hash_algo    TEXT    NOT NULL,
    group_count  INTEGER NOT NULL,
    wasted_bytes INTEGER NOT NULL
);

CREATE TABLE dupe_groups (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id       INTEGER NOT NULL REFERENCES dupe_runs(id) ON DELETE CASCADE,
    size_bytes   INTEGER NOT NULL,
    file_count   INTEGER NOT NULL,
    wasted_bytes INTEGER NOT NULL,
    kind         TEXT    NOT NULL,       -- duplicate|hardlink_set
    full_hash    TEXT    NOT NULL
);

CREATE INDEX idx_dupe_groups ON dupe_groups(run_id, wasted_bytes DESC);

CREATE TABLE dupe_files (
    group_id       INTEGER NOT NULL REFERENCES dupe_groups(id) ON DELETE CASCADE,
    seq            INTEGER NOT NULL,
    path           TEXT    NOT NULL,
    mtime_unix     INTEGER,
    link_count     INTEGER NOT NULL DEFAULT 1,
    suggested_keep INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (group_id, seq)
);
