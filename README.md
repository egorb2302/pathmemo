# pathmemo

**Disk space screener for Windows.** One `.exe`, no installer. Finds where the space went, and frees it safely.

**Spec version:** 3.0
**Target:** Windows 10 1809+ / Windows 11 (x64 and arm64, separate binaries)
**Language:** English throughout — CLI, TUI, logs, exports, and this document.

## Install

Download the zip for your architecture from [Releases](../../releases), unpack it anywhere, run `pathmemo.exe`. Nothing is installed; nothing is written outside `%LOCALAPPDATA%\pathmemo`. The executable is not code-signed, so Windows warns about an unknown publisher: **More info → Run anyway**, or check the published SHA-256 first.

```
pathmemo                 # double-click, or run with no arguments: the TUI
pathmemo scan            # scan every fixed volume
pathmemo top --min 1GB   # largest files
pathmemo audit           # where the invisible space went
pathmemo reclaim         # what is worth deleting, by rule, with risk and recovery
pathmemo diff            # what changed since the previous scan
pathmemo rm <path>       # delete, into quarantine by default (restore, purge, ops)
```

Administrator rights are optional but change what the tool can see: with them the scanner reads `$MFT` directly — roughly 40x faster, and it sees paths a directory walk is denied (§4.1).

## Status

| Phase | Contents | State |
|---|---|---|
| P0 | Skeleton, manifest, single-file publish, `doctor` | **done** — 16.1 MB exe |
| P1 | Walk scanner, `.pmsnap` format, `scan` / `tree` / `top` / `history` | **done** — 1.22M files in 41 s |
| P1+ | Double-click session: volume status, menu, line-by-line navigation | **done** — stand-in until P5 |
| P2 | Space Audit: 21 probes, `audit` / `--id` / `--copy` / `--json`, `[E]` restart elevated | **done** — 4.1 s unelevated (16 probes answer without rights) |
| P3 | MFT scanner, hard-link dedup in both scanners, ADS, volume reconciliation, `--scanner` | **done** — 1.27M files in 9.6 s, 1.4% unaccounted |
| P4 | SQLite (schema §11, WAL), history, `diff`, `--format json\|csv`, `--data-dir` | **done** — diff of two 1.6M-node snapshots in 105 ms |
| P5 | TUI: Overview + Tree, own renderer, `status` | **done** — 0.1–0.6 ms per frame, 56 ms to search 1.58M nodes |
| P6 | Deletion: PathGuard, HandleTreeDeleter, quarantine, journal, `rm` / `restore` / `purge` / `ops`, `audit --apply` | **done** — canary intact after 10k junction-swap races (19,478 swaps, 114 s) |
| P7 | Reclaim rules: 31 rules as data, two axes, `reclaim` / `--rule` / `--apply`, keep-list, TUI screen 4 and badges | **done** — 1.2M nodes matched and 1,800 matches guard-checked in 2.5 s |
| P8 | Duplicates | not started |
| P9 | USN incremental scan, scheduling | not started |
| P10 | Polish, NativeAOT | not started |

254 tests green.

**Known limitations.** Unelevated, the walk scanner runs: some paths are unreadable, hard-link dedup covers only files ≥ 1 MB (WinSxS overstated by ~1.5 GB), ADS are not counted. Elevation removes all three. No incremental USN scan yet (P9). `diff` needs two full snapshots and warns when they came from different scanners, because part of the difference is then the scanners, not the disk. `reclaim` counts a multiply-linked file as shared rather than reclaimable, because `.pmsnap` v1 carries no file identity — the number understates rather than overstates (§7.5). An open snapshot holds ~200 MB against a 150 MB budget — lazy section loading in P10 (§20).

---

## Contents

1. [Purpose and principles](#1-purpose-and-principles)
2. [Non-goals](#2-non-goals)
3. [The disk space model](#3-the-disk-space-model)
4. [Scanning](#4-scanning)
5. [Snapshot format](#5-snapshot-format)
6. [Space Audit — the invisible space](#6-space-audit--the-invisible-space)
7. [Reclaim — cleanup recommendations](#7-reclaim--cleanup-recommendations)
8. [Duplicates](#8-duplicates)
9. [Deletion](#9-deletion)
10. [History and diff](#10-history-and-diff)
11. [Database schema](#11-database-schema)
12. [Configuration](#12-configuration)
13. [CLI](#13-cli)
14. [TUI](#14-tui)
15. [OS integration](#15-os-integration)
16. [Threat model](#16-threat-model)
17. [Code architecture](#17-code-architecture)
18. [Technology stack](#18-technology-stack)
19. [Packaging and build](#19-packaging-and-build)
20. [Performance budgets](#20-performance-budgets)
21. [Acceptance criteria](#21-acceptance-criteria)
22. [Testing](#22-testing)
23. [Glossary](#23-glossary)

---

## 1. Purpose and principles

pathmemo answers two questions: **where did the space go** — including what a filesystem walk cannot see — and **what can be freed safely**, with an honest estimate of how many bytes come back.

### 1.1. Principles

| № | Principle | Consequence |
|---|---|---|
| P1 | **Honest numbers.** The scan total must reconcile with the volume's used space, and any gap must be explained. | Allocated size, hard links, sparse files and clusters all count; the "unaccounted" delta is shown with its causes. |
| P2 | **Scan everything, protect selectively.** | Excluded from accounting ≠ protected from deletion. Two independent lists; the default exclusion list is nearly empty. |
| P3 | **Freed space is measured, not assumed.** | Free space is read before and after every operation, and the real delta shown. |
| P4 | **The right way to clean beats deleting files.** | For caches and system stores, print the vendor's own command (`DISM`, `git gc`, `docker prune`) rather than hand a path to `rm`. |
| P5 | **Nothing is deleted without an explicit user action.** | No auto-clean, no one-click "optimisation". |
| P6 | **Deletion is auditable and reversible where possible.** | Quarantine, journal, dry-run. The Recycle Bin only where it actually helps. |
| P7 | **Do not harm the disk being cleaned.** | The tool's own data is bounded and never exceeds 500 MB. |
| P8 | **Scriptable on equal terms with interactive.** | Every action is reachable from the CLI, with stable JSON and meaningful exit codes. |
| P9 | **Works in any terminal.** | No bindings a terminal intercepts. Minimum 80×24. |
| P10 | **Untrusted data is data.** | Names, config and paths are input, never commands: sanitised on render, nothing executed from config, found files not executed by default. |

### 1.2. How it differs from WizTree / TreeSize / ncdu

- **History and diff** — "what grew by 18 GB since last week", automatically, on a schedule.
- **Space Audit** — VSS, WinSxS, WSL/Docker vhdx, Windows Update cache, hiberfil, Recycle Bin: what a filesystem walk physically cannot see.
- **Knows developer ecosystems** — npm, pnpm, nuget, gradle, maven, pip, cargo, go, Unity, Unreal, Docker, WSL, each with its correct cleanup command.
- **One exe** equally good in a script and in a terminal.

---

## 2. Non-goals

Explicitly out of scope: a GUI or web app as the primary interface *(a local treemap viewer is an optional Phase 3 extra)*; deletion without confirmation, "system speed-up", registry cleaning, anything of the optimiser genre; managing security settings (Defender exclusions, UAC, policy) — the tool *suggests* a command, the user runs it; a server, multi-user mode or cloud sync; perceptual hashing and similar-file search; network and removable drives as a primary scenario (supported degraded, not optimised); Linux and macOS — abstractions for them are deliberately **not** built in advance (§17.1).

## 2.1. Launch modes

| Condition | Behaviour |
|---|---|
| Double-clicked from Explorer (the console was created for us) | TUI, window stays open |
| `--interactive` / `-i` | TUI forced, from any terminal |
| No arguments, from a terminal | help |
| Arguments present | that command, non-interactive |
| stdout redirected, no arguments | `status` — a text summary |
| stdout redirected | always non-interactive, UTF-8 output |
| Terminal cannot do ANSI | line-based menu instead of the TUI |

Explorer launches are told apart from shell launches through `GetConsoleProcessList`: exactly one process attached to the console means the window was created for us and dies with us. Without that check, double-clicking gives a window that flashes for a fraction of a second.

**What double-clicking gets (P5).** A window created by Explorer has nobody to inherit sensible settings from, so we set them: the title (`pathmemo <version>` rather than the full exe path), the size (up to 110×32, and only when the window is ours), **quick-edit off**, and a glyph set matched to the console font. The exe carries an icon (§19.1). A fatal error in our own window waits for Enter instead of taking the message down with it.

Quick-edit is the one item that is not cosmetic: with it on, a click inside the window starts a selection and **the next console write blocks until the selection is cleared**. The screen freezes and the user has no idea why. The mode is cleared while the screens are up and restored on exit.

*From P5* an interactive session is the real screens (§14): `TuiHost` enables VT processing, switches to the alternate buffer and draws frames. If VT cannot be enabled or the stream is redirected, the older line-based session runs instead — in a host without VT a frame would appear as literal escape sequences, which is worse than no TUI.

Line-based fallback menu: `[A]` audit, `[B]` browse, `[S]` scan C:, `[V]` scan all volumes, `[L]` largest files, `[C]` compare the last two scans, `[H]` history, `[D]` diagnostics, `[E]` restart as administrator (only when unelevated), `[Q]` quit. `[E]` relaunches the same exe through `ShellExecute` with the `runas` verb: the manifest stays `asInvoker`, so rights are requested only when asked for. Elevation has no dedicated TUI key — the scan itself offers it (§14.7).

---

## 3. The disk space model

Without this section the numbers do not add up and the tool is useless.

### 3.1. Four different "sizes"

| Term | What it is | Source |
|---|---|---|
| **Logical size** | The data stream's length (what `FileInfo.Length` shows). | MFT `DataSize` / `FILE_STANDARD_INFO.EndOfFile` |
| **Allocated size** | Bytes actually occupied: cluster rounding, NTFS compression, sparse holes. | MFT `AllocatedSize` / `GetCompressedFileSize` |
| **Unique allocated** | Allocated with hard-link dedup: each physical file counted once. | Dedup by `(VolumeSerial, FileReferenceNumber)` |
| **Reclaimable** | What deleting **this particular set** of paths frees. | Unique allocated, but only for files whose links are **all** inside the set |

Every size in the dashboard, tree and reports is **unique allocated** by default — the only quantity that adds up to the volume's used space. File details show logical, allocated and the link count. The delete dialog shows **reclaimable**, not the sum of sizes, and explains a difference ("12 GB selected, 3.1 GB reclaimable: 8.9 GB is shared via hard links with files outside the selection"). Mode switch: `m` in the TUI, `--size logical|allocated|unique` in the CLI.

### 3.2. Hard links

`C:\Windows\WinSxS` consists almost entirely of hard links to files in `System32`. Without dedup, `C:\Windows` shows 2–3x its real size.

Every file gets the key `(VolumeSerialNumber, FileReferenceNumber)`. The first occurrence in traversal order **owns** the bytes, the rest are aliases (`NodeFlags.HardlinkAlias`), and unique allocated aggregates over owners only. The link count is stored per node, which answers "will deleting this free anything" instantly; aliases are marked in the UI and their size greyed: `1.2 GB (link)`.

**Degraded mode (walk scanner):** `FileReferenceNumber` needs an open handle, so it is read only for files **over 1 MB** — a few percent of the count, and hard-linked duplicates below that size contribute negligible error. `SnapshotFlags.PartialHardlinkResolution` tells the UI to warn.

*Implemented (P3):* the handle is opened with `FILE_READ_ATTRIBUTES` + `FILE_FLAG_OPEN_REPARSE_POINT`; `FILE_STANDARD_INFO.NumberOfLinks` is read first and `FILE_ID_INFO` only when it is `> 1`. The ids live in a side list per directory rather than in `RawEntry` (16 bytes × 1.5M entries). C: has 16k files ≥ 1 MB, costing about 3 s out of 44. The owner is the first occurrence in **BFS order**, so a file present as `System32\foo.dll` and `WinSxS\amd64_…\foo.dll` is owned by the shorter path. The MFT scanner follows the same rule.

### 3.3. Sparse and NTFS-compressed

Sparse files have `AllocatedSize` < `LogicalSize` — typical for `ext4.vhdx` (WSL), databases and images. **This matters**: an 80 GB logical WSL disk may occupy 12 GB, or all 80. NTFS compression is the same effect for a different reason, handled identically through `AllocatedSize`. When `allocated / logical < 0.9` the UI adds a `sparse` or `compressed` badge.

### 3.4. Alternate data streams

An MFT scan sums every `$DATA` attribute of a file automatically. The walk scanner does not see ADS — a documented limitation, under 0.1% error on a typical system.

### 3.5. Volume reconciliation

After every scan:

```
Volume C:  total 476.1 GB   free 21.3 GB   used 454.8 GB
  Scanned files                          381.2 GB
  Inaccessible paths (147)                 ~?  GB
  Shadow copies (VSS)                      38.4 GB
  Recycle Bin                               4.1 GB
  NTFS metadata ($MFT, $LogFile, ...)       2.9 GB
  Reserved for system                       1.1 GB
  ─────────────────────────────────────────────────
  Unaccounted                              27.1 GB   [?] explain
```

`[?] explain` lists the possible causes with commands to check each, which turns "the numbers don't add up" from a bug into a feature. Sources: `GetDiskFreeSpaceExW`, `FSCTL_GET_NTFS_VOLUME_DATA`, `SHQueryRecycleBinW`, WMI `Win32_ShadowStorage`.

---
## 4. Scanning

### 4.1. Two paths, one result

| | **MFT scanner** (primary) | **Walk scanner** (fallback) |
|---|---|---|
| Mechanism | `\\.\C:` + `FSCTL_GET_NTFS_VOLUME_DATA` + direct `$MFT` parsing (§4.3.1) | `FileSystemEnumerator<T>` |
| Needs admin | **yes** | no |
| Filesystem | NTFS only | any |
| 1M files | **3–8 s** | 45 s – 5 min |
| Allocated size | yes, free | `GetCompressedFileSize` (+1 syscall) |
| FileReferenceNumber / LinkCount | yes, free | files > 1 MB only |
| ADS | yes | no |
| Sees ACL-denied paths | **yes** | no |

The MFT path is the primary one: an order of magnitude better numbers, two orders of magnitude more speed, and it sees what an ordinary walk is denied. Core of the product, not a later optimisation.

### 4.2. Choosing a path

```
for each target volume:
    NTFS and elevated        -> MFT scanner
    NTFS and not elevated    -> offer to relaunch elevated;
                                if declined, Walk scanner with the Degraded flag
    otherwise                -> Walk scanner
```

Relaunch is `ShellExecuteEx` with `lpVerb = "runas"` and the same command line plus `--elevated-relaunch`; from the CLI only when there is a TTY, and in script mode a warning is printed and the degraded path used.

```
Running without administrator rights.
  · Fast MFT scan unavailable (falling back to directory walk: ~40x slower)
  · Files in other user profiles and protected folders will be missed
  · Hard link detection limited to files over 1 MB

  [R] Restart as administrator    [C] Continue anyway    [Q] Quit
```

### 4.3. MFT scanner

Open the volume, read `FSCTL_GET_NTFS_VOLUME_DATA` for cluster size, `$MFT` size and record count, then parse `$MFT` records directly: `$STANDARD_INFORMATION` and `$DATA` (resident and non-resident, `AllocatedSize`, `RealSize`, run totals for sparse). One sequential pass yields names, parents, sizes, attributes, times and link counts. `parentId → id` forms a graph rooted at record 5; one bottom-up pass produces full paths without string concatenation. Deleted and extension records are dropped. Any parse failure — a non-standard volume, a locked BitLocker volume, corruption — falls back to the walk scanner, with the reason in `scan_errors`.

#### 4.3.1. Implementation notes (P3)

- **`FSCTL_ENUM_USN_DATA` is not used at all.** It gives one name per file, while hard-link dedup needs every `$FILE_NAME`. Full `$MFT` parsing gives everything in one pass — `MftParser` (a pure function over bytes, covered by synthetic records) and `MftScanner`.
- **Locating `$MFT`:** record 0 is read at `MftStartLcn`, and its `$DATA` runs give the extents. Under heavy fragmentation the continuation runs live in extension records pointed at by record 0's `$ATTRIBUTE_LIST`, which are read too. `FSCTL_GET_RETRIEVAL_POINTERS` is not needed.
- **Reading:** `\\.\C:` is opened with `GENERIC_READ` (the only thing requiring elevation); reads are positional via `ReadFile` with `OVERLAPPED.Offset`, in 8 MB chunks, double-buffered so the next chunk arrives while the current one is parsed. One or two GB of sequential I/O, not a single file opened.
- **Fixup:** the update sequence array is applied to every record; a mismatch skips it and counts into `scan_errors`. Never-written slots are normal.
- **Extension records** attach to their base record by `BaseRecord`, with a sequence-number check so a stale reference to a reused slot is dropped.
- **Sizes:** logical is `RealSize` from the first `$DATA` part; **allocated is the sum of the real data runs of every part, holes excluded.** That is the only rule equally correct for ordinary, sparse and compressed streams and for `$BadClus:$Bad` — a stream the size of the whole volume with no sparse flag, which produced "−220 GB unaccounted" on the first run. All `$DATA` streams are summed, so ADS are counted. Resident data costs 0 clusters: it lives inside the `$MFT` record, which the `ntfs.metadata` probe already counts.
- **Attributes:** in raw `$STANDARD_INFORMATION`, bit `0x40000` means "has extended attributes" (every file from a WIM image, and `C:\Windows` itself), while Win32 uses the same value for `RECALL_ON_OPEN`. Unmasked, half of Windows showed up as `[cloud]`. Rule: recall bits count only on a reparse point; `OFFLINE` always counts.
- **Tree:** links are indexed by parent with a counting sort, then BFS from record 5. The first occurrence owns, later ones are `HardlinkAlias`; a directory met twice (corruption) is not walked again. Records whose parent is deleted or reused are orphans: counted and reported, not attached.
- **What lands in the tree:** everything in `$MFT`, including `$MFT`, `$LogFile`, `$Bitmap` and `$Extend\$UsnJrnl` — real volume bytes, shown the same way by WizTree. Reconciliation therefore does not add metadata twice in MFT mode.
- **Mixed volume sets:** NTFS with rights goes through the MFT, the rest (exFAT, FAT32, an MFT refusal) through one work-stealing walk; trees are spliced by `TreeAssembly.Concat` with index offsets.
- **Memory:** arrays sized by record count (~30 bytes each) plus a link list (20 bytes) plus the final `NodeStore`; C:'s 1.8M records fit the 250 MB budget.

### 4.4. Walk scanner

A custom `FileSystemEnumerator<RawEntry>` rather than `Directory.EnumerateFileSystemEntries`, which allocates a full path per entry and needs a separate stat for the size:

```csharp
protected override RawEntry TransformEntry(ref FileSystemEntry entry) => new()
{
    Name       = entry.FileName,          // ReadOnlySpan<char>, copied into a shared blob
    Logical    = entry.Length,            // from FILE_FULL_DIR_INFO, no syscall
    Attributes = entry.Attributes,
    MTime      = entry.LastWriteTimeUtc,
    IsDir      = entry.IsDirectory,
};

protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
    => entry.IsDirectory
    && (entry.Attributes & FileAttributes.ReparsePoint) == 0;   // count the point, never enter it
```

`EnumerationOptions`: `RecurseSubdirectories = false` (recursion is ours), `IgnoreInaccessible = false` (errors belong in the report, not in silence), `AttributesToSkip = 0` (**nothing** hidden — not Hidden, not System), `BufferSize = 64 * 1024`, `MatchType.Win32`.

**Parallelism is work-stealing, not partitioning by level.** A `ConcurrentQueue<DirTask>`, N workers, each taking a directory and pushing its subdirectories back. Partitioning "by second-level folder" was rejected: `C:\Windows\WinSxS` is ~40% of all files on the disk, and one worker would grind through it alone.

N follows the medium, via `IOCTL_STORAGE_QUERY_PROPERTY` → `StorageDeviceSeekPenaltyProperty`: HDD → 2, SSD → `min(8, CPU)`, unknown or network → 4. `MaxDegreeOfParallelism = CPU count` makes an HDD scan **2–3x slower** than single-threaded, so the config default of `0` means auto-detect, not "one per core".

### 4.5. Incremental rescan through the USN journal

The snapshot stores `(VolumeSerial, UsnJournalId, NextUsn)`. On a later scan, `FSCTL_QUERY_USN_JOURNAL` confirms the journal was not reset; `FSCTL_READ_USN_JOURNAL` from the saved USN lists the changed records; those are re-read and aggregates recomputed up the tree. Otherwise a full scan runs. Result: **0.2–2 s** instead of 5, which is what makes history, diff and hourly scheduled scans practical. If the journal is off, the tool suggests `fsutil usn createjournal m=32000000 a=8000000 C:` for the user to run.

### 4.6. What is skipped and what is counted

| Entity | Recurse | Size counted | Why |
|---|---|---|---|
| Reparse point (junction, symlink, mount point) | **no** | the point itself = 0 bytes, flagged `Reparse` | Prevents infinite recursion and double counting; the target is shown in details. |
| Cloud placeholder (`RECALL_ON_*`, `OFFLINE`) | — | **allocated (usually ~0)**, flagged `CloudOnly` | A cloud-only file occupies nothing, and saying so matters: "50 GB in cloud, 0 GB local". |
| Hidden / System files | yes | **yes** | Skipping them hides `hiberfil.sys`, `pagefile.sys` and `AppData` — the most important things. |
| `$Recycle.Bin` | yes | yes, as its own category | This is reclaimable space. |
| `System Volume Information` | no access even as admin | covered by the VSS probe | |
| pathmemo's own data | yes | yes, flagged `SelfData` | Honesty: the tool shows itself too. |
| Mount point of another volume | **no** | 0 | Otherwise the disk is counted twice; that volume is scanned separately. |

### 4.7. Progress

Refreshed every 250 ms from a renderer thread reading `volatile` counters, so the hot path takes no locks.

```
Scanning C:\  ·  MFT mode
  1,284,391 records   ·   412.8 GB   ·   198k rec/s   ·   6.4 s elapsed
  $MFT: 78%  ████████████████████░░░░░
```

Walk mode shows the current directory and files/s, and **no ETA**: the file count is unknown in advance, and a fake percentage is worse than none. The MFT percentage is honest — the record count is known up front.

### 4.8. Cancellation

`Ctrl+C` or `Esc` sets a `CancellationToken`. The partial result **is saved**, with status `cancelled` and the `Partial` flag: fit for browsing, not for diff or reclaim advice. A second `Ctrl+C` within 3 s exits immediately without saving, in case a save hangs on a network drive. The handler only sets the token; the main thread writes, with a 10 s timeout.

### 4.9. Errors

Every failure becomes a `ScanError { Path, Kind, Win32Code, Message }`, grouped by kind:

```
AccessDenied          104 paths    (~? GB)
SharingViolation       12 paths
PathTooLong             3 paths
NameInvalid             1 path     (created by WSL: contains ':')
IoError                 0 paths
```

In MFT mode the size behind `AccessDenied` **is known** — metadata is read past the ACL — so the exact figure is shown.

---

## 5. Snapshot format

### 5.1. Why not SQLite row by row

A million files as SQLite rows is 150–250 MB per scan, and with history that is tens of gigabytes: **the tool for freeing space would eat the disk** (violating P7). Inserting a million rows takes seconds, and reading them back for the tree takes seconds again. The answer: **a binary snapshot per scan, with SQLite for metadata and journals only.**

```
%LOCALAPPDATA%\pathmemo\
├── pathmemo.db              # SQLite (WAL): history, deletion journal, rules, hash cache
├── config.json
├── snapshots\0000000042.pmsnap    # ~10–25 MB per 1M files
├── quarantine\0000000043\         # §9.4
├── logs\
└── exports\
```

### 5.2. `.pmsnap` v1 structure

```
HEADER (64 bytes, uncompressed)
    magic          u8[8]    "PMSNAP\x01\x00"
    formatVersion  u16      1
    toolVersion    u32      packed semver
    createdAtUtc   i64      unix seconds
    nodeCount      u32
    volumeCount    u16
    flags          u32      Partial | Degraded | PartialHardlinkResolution | Elevated | Incremental
    sectionCount   u16

SECTION TABLE (sectionCount x 16 bytes)
    kind u32, compression u32 (0=raw, 1=deflate), rawLen u64, storedLen u64, offset u64

SECTIONS (each compressed independently)
    NAMES     : length-prefixed UTF-8 name segments, deduplicated (not full paths)
    NAME_IDX  : u32[] offsets into NAMES
    NODES     : struct-of-arrays, below
    VOLUMES   : letter, label, fs, serial, clusterSize, total, free, mftSize,
                metadataSize, usnJournalId, nextUsn
    ERRORS    : ScanError[]
    AGGREGATES: precomputed tops, optional cache
```

### 5.3. NODES: struct-of-arrays

Nodes are ordered so that **each directory's children occupy a contiguous range** (emission order is BFS), which gives O(1) navigation and linear reads while drawing.

Sort-by-size is deliberately **not** baked in: subtree sums are known only after a bottom-up aggregation pass, so ordering the stored arrays would need a second permuting pass that rewrites every child pointer — a fragile trade for something that costs milliseconds at render time. `TreeQuery.ChildrenBySize` sorts on display.

| Array | Type | Bytes | Purpose |
|---|---|---|---|
| `parent` | `i32[]` | 4 | parent index, `-1` at a volume root |
| `nameIdx` | `i32[]` | 4 | index into `NAME_IDX` |
| `firstChild` | `i32[]` | 4 | first child index, `-1` if none |
| `childCount` | `i32[]` | 4 | direct children |
| `allocated` | `i64[]` | 8 | own size for a file, unique subtree sum for a directory |
| `logical` | `i64[]` | 8 | the same in logical bytes |
| `fileCount` | `i32[]` | 4 | files in the subtree |
| `mtime` | `u32[]` | 4 | seconds since 2000-01-01 UTC (good to 2136) |
| `attributes` | `u32[]` | 4 | Win32 `FileAttributes` |
| `flags` | `u8[]` | 1 | `IsDir, Reparse, HardlinkAlias, CloudOnly, Sparse, SelfData, Encrypted, HasAds` |
| `linkCount` | `u8[]` | 1 | hard links, `255` means "255 or more" |
| | | **46** | |

1M nodes = **46 MB** of arrays plus ~14 MB of name blob (segments are deduplicated: `Microsoft`, `bin`, `node_modules` recur tens of thousands of times) ≈ **60 MB in memory**, 12–20 MB on disk after deflate. Loading is a `MemoryMappedFile` with sections decompressed on demand; the tree needs only `parent/nameIdx/firstChild/childCount/allocated/flags`.

**Deliberately absent:** `createdAt` (almost never decides whether to delete something); `accessedAt` — **dead data on Windows**, since `NtfsDisableLastAccessUpdate` has been on by default since Vista, and "last opened 3 years ago" for a file opened yesterday misleads; full paths, rebuilt by walking `parent` in microseconds.

### 5.4. Retention

```
Keep:  the last 20 snapshots
   +   one per calendar month for the last 12 months
Hard cap: snapshots\ <= 400 MB; over that, drop the oldest monthlies first,
          then the oldest of the last 20 (never the 3 newest)
```

Scan metadata (a row in `scans`) lives forever — ~200 bytes, and it is what draws the used-space graph years back. The snapshot may already be gone: the row is then marked `snapshot_available = 0` and the scan is viewable as aggregates only.

*Implemented in P4:* a snapshot dropped by retention clears `snapshot_path`, `history` prints `deleted` instead of a size, and `diff` refuses to open it with an explanation. The numbers remain.

---
## 6. Space Audit — the invisible space

A filesystem walk physically cannot see half of what fills a disk. This is a **separate subsystem of probes**, and for the "the disk is filling up hard" scenario it is worth more than the scan.

### 6.1. Probes

Each probe returns an `AuditFinding`:

```csharp
record AuditFinding(
    string   Id,                 // "vss.shadow-storage"
    string   Title,              // "Volume Shadow Copies"
    string   Volume,
    long     UsedBytes,
    long     ReclaimableBytes,   // what actually comes back
    Risk     Risk,               // Safe | Caution | Danger
    Recoverability Recoverability,
    string   Explanation,        // 1-3 sentences
    Remedy[] Remedies);

record Remedy(
    RemedyKind Kind,             // RunCommand | DeletePaths | OpenSettings | Manual
    string     Display,          // "dism /Online /Cleanup-Image /StartComponentCleanup"
    bool       NeedsElevation,
    bool       NeedsReboot,
    string?    Caveat);          // "Removes ability to uninstall installed updates"
```

| ID | What it measures | Typical | Cleanup |
|---|---|---|---|
| `vss.shadow-storage` | Shadow copies / restore points, via CIM (`powershell.exe -NoProfile` from System32, JSON out so byte counts are integers in any locale); **elevated only** | 5–60 GB | `vssadmin delete shadows /for=C: /oldest`, or shrink the quota |
| `winsxs.component-store` | The genuinely removable part of the component store: `DISM /English /Online /Cleanup-Image /AnalyzeComponentStore`, parsed by `label : value` position rather than by words; **elevated only** | 2–12 GB | `DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase` ⚠️ |
| `windows.old` | Previous Windows installation | 10–30 GB | `cleanmgr` handler `Previous Installations` |
| `wu.softwaredistribution` | Windows Update cache | 1–20 GB | stop `wuauserv` + `bits` → clear → start |
| `wu.delivery-optimization` | P2P update delivery cache | 1–10 GB | `Delete-DeliveryOptimizationCache` |
| `windows.installer-orphans` | Orphaned MSI/MSP in `%WINDIR%\Installer`, cross-checked with the registry | 2–15 GB | ⚠️ report only; auto-deletion breaks uninstall |
| `hiberfil` | Hibernation file: size from the root listing (no file opened) plus registry `HiberFileType` | 0.4 × RAM | `powercfg /h /type reduced` or `/h off` |
| `pagefile` | Page file, plus `Win32_PageFileSetting` | 1–32 GB | System Properties → Virtual Memory |
| `swapfile` | UWP swapfile | 256 MB | with the pagefile |
| `recyclebin` | Recycle Bin per volume, via `SHQueryRecycleBinW` (`SHQUERYRBINFO` is 24 bytes with natural alignment; with `Pack=1` the Shell answers `E_INVALIDARG`) | 0–50 GB | `SHEmptyRecycleBin` (in-app, P6) |
| `wsl.vhdx` | WSL2 disks from registry `Lxss`, logical vs allocated | 10–100 GB | `wsl --manage <d> --set-sparse true`, or `compact vdisk` |
| `docker.vhdx` | Docker Desktop disks | 10–80 GB | `docker system prune -a --volumes`, then compact |
| `hyperv.vhdx` | VHD/VHDX outside Docker and WSL | varies | `Optimize-VHD`, report |
| `dumps` | `MEMORY.DMP`, `CrashDumps`, `LiveKernelReports`, `Minidump` | 1–20 GB | delete (safe) |
| `onedrive.local` | Locally materialised cloud files | 10–200 GB | `attrib +U -P` per folder |
| `fastboot.reserved` | Reserved storage (Win10 1903+) | 4–7 GB | `DISM /Online /Set-ReservedStorageState /State:Disabled` |
| `logs.cbs-panther` | `CBS.log`, `Panther`, `%WINDIR%\Logs` | 0.5–5 GB | delete (safe) |
| `browser.caches` | Chrome / Edge / Firefox / Brave | 1–15 GB | delete with the browser closed |
| `store.temp` | `%WINDIR%\Temp`, `%TEMP%`, `%LOCALAPPDATA%\Temp` | 0.5–20 GB | delete, skipping locked files |
| `defender.history` | `Windows Defender\Scans\History` | 0.1–3 GB | delete (safe) |
| `ntfs.metadata` | `$MFT` plus reserved clusters, through `FSCTL_GET_NTFS_VOLUME_DATA` on a handle to the **root directory** (`C:\` + `FILE_FLAG_BACKUP_SEMANTICS`), not `\\.\C:`: a volume opened without `GENERIC_READ` goes straight to the device, bypassing NTFS, and the FSCTL returns `ERROR_INVALID_FUNCTION` — while `GENERIC_READ` on a volume needs rights and on a directory does not | 1–5 GB | **not reclaimable**, explanation only |

### 6.2. Output

```
$ pathmemo audit

Volume C:   476.1 GB total   ·   21.3 GB free   ·   95.5% used

  RECLAIMABLE                                             38.4 GB
  ──────────────────────────────────────────────────────────────
  Volume Shadow Copies                        38.4 GB   safe
    12 restore points, oldest 2026-02-11. Windows keeps these for
    System Restore and Previous Versions.
    → vssadmin delete shadows /for=C: /oldest        [admin]

  WSL2 virtual disks                          31.2 GB   caution
    Ubuntu-22.04: 44.1 GB on disk, 12.9 GB used inside. WSL disks
    never shrink automatically.
    → wsl --manage Ubuntu-22.04 --set-sparse true
    ⚠ Shut down WSL first: wsl --shutdown

  Component store (WinSxS)                     6.8 GB   caution
  Hibernation file                             6.4 GB   caution
  Recycle Bin                                  4.1 GB   safe
  Windows Update cache                         2.9 GB   safe
  Crash dumps                                  1.2 GB   safe

  NOT RECLAIMABLE                                         3.9 GB
  ──────────────────────────────────────────────────────────────
  NTFS metadata ($MFT 2.4 GB, $LogFile 0.1 GB, other 1.4 GB)

  Run `pathmemo audit --id vss.shadow-storage` for details.
```

### 6.3. Rules for probes

- A probe **changes nothing**. Read-only, always.
- External tools are invoked for **reading** only (`DISM /Analyze...`, `vssadmin list`, `powercfg /a`) — `Process.Start` with `ArgumentList`, no shell, 30 s timeout, by absolute path from `%WINDIR%\System32` (PATH-hijacking defence).
- `DISM` and `vssadmin` output is locale-dependent, so: invariant culture in the process environment, parsing by numbers and structure rather than English words, and `Unknown` rather than zero when parsing fails.
- A `Remedy` is by default **displayed and copied to the clipboard**, nothing more. Execution needs an explicit `pathmemo audit --apply <id>` or a TUI key, always confirmed, always logged.

### 6.4. Implementation notes (P2)

- **Probe isolation.** `AuditRunner` runs probes in parallel (≤ 8 threads), each capped at 45 s; an exception or timeout becomes a finding with `status=error`, so one probe cannot take the report down. A probe that cannot measure returns `NeedsElevation` / `NoSnapshot` / `Unknown` / `NotApplicable` — never "0 bytes".
- **Two numbers, both nullable.** WSL and Docker know `UsedBytes` and not `ReclaimableBytes` (free space inside a vhdx is visible only from inside, and booting a distro to ask is not a read-only act), so they land in a `WORTH A LOOK` section rather than the `RECLAIMABLE` total. `pagefile` is special: reclaimable equals its size if another fixed volume has > 20 GB free, and the probe suggests moving it there; otherwise 0.
- **Directory measurement** (`DirectoryMeasure`): size on disk is the logical size rounded up to the cluster, and `GetCompressedFileSize` is called only for files flagged `Compressed` or `SparseFile`. The attributes are already in the enumeration buffer, so this is free: measuring a 175k-file `%TEMP%` took 20 s with a call per file and ~3 s without. Reparse points are neither counted nor entered; access errors are counted, not thrown — "a lower bound plus a note" beats an empty result.
- **External tools** (`ExternalTool`): `%WINDIR%\System32\*` by absolute path only, no window, no `__COMPAT_LAYER`, timeout with `Kill(entireProcessTree)`, output decoded in the console OEM code page. Two invocations in the whole audit, both elevated-only: DISM (`/English` pins the format) and `powershell.exe` for CIM.
- **`--apply` arrived with P6**, once there was an operations journal (§9.7) to put it in. It is confirmed, it refuses rather than degrades when it lacks the rights a remedy declares, and it records what the free space actually did. `--copy` / `--copy-index <n>` (§15.1) and `c<n>` interactively remain the way to take a command elsewhere.
- **Unelevated**, VSS, DISM, `Minidump`, `Defender\Scans\History` and part of `Windows\Logs` cannot answer; 16 of 21 probes still do, in 4.1 s.
- *P4:* a full run writes to `audit_findings` (§11) tied to the scan the probe used — 17 rows out of 21 on a real machine, since `not applicable` is not recorded. A single probe (`--id`) writes nothing: a spot check, not a point of history.

---

## 7. Reclaim — cleanup recommendations

### 7.1. Two independent axes instead of one "risk"

A single safe/caution/danger axis mixes incompatible things: deleting `node_modules` ("I lose 10 minutes") and deleting `.git\objects` ("I lose the work") end up in one bucket.

**Axis 1 — Risk: what breaks.** `Safe` — nothing; the application recreates it. `Caution` — functionality or a way back is lost (update uninstall, hibernate, history); reversible, but it takes work. `Danger` — may break the system or an application; requires typing a confirmation word.

**Axis 2 — Recoverability: what getting it back costs.** `Instant` — recreated automatically on next use (browser, thumbnail, shader caches). `Redownload` — fetched from the network (`node_modules`, `.nuget\packages`, Docker images). `Rebuild` — rebuilt locally, costs CPU time (`obj/`, Unity `Library/`, `.venv`). `Irreversible` — personal files, the only copy of an installer, `.git` objects.

The UI shows both: `safe · redownload` means go ahead; `caution · irreversible` means think.

### 7.2. Default rules

Patterns are **globs**, not regex (§12.2). Every rule is data, not code.

| Rule | Pattern | Risk | Recov. | Right way |
|---|---|---|---|---|
| `dev.node_modules` | `**\node_modules` | Safe | Redownload | delete; `npm ci` restores |
| `dev.npm_cache` | `%LOCALAPPDATA%\npm-cache`, `~\.npm\_cacache` | Safe | Redownload | `npm cache clean --force` |
| `dev.pnpm_store` | `%LOCALAPPDATA%\pnpm\store` | Safe | Redownload | `pnpm store prune` |
| `dev.yarn_cache` | `%LOCALAPPDATA%\Yarn\Cache` | Safe | Redownload | `yarn cache clean` |
| `dev.nuget` | `~\.nuget\packages`, `%LOCALAPPDATA%\NuGet\v3-cache` | Safe | Redownload | `dotnet nuget locals all --clear` |
| `dev.dotnet_artifacts` | `**\bin\{Debug,Release}`, `**\obj` | Safe | Rebuild | delete |
| `dev.gradle` | `~\.gradle\caches` | Safe | Redownload | `gradle --stop`, then delete |
| `dev.maven` | `~\.m2\repository` | Safe | Redownload | delete |
| `dev.pip_cache` | `%LOCALAPPDATA%\pip\Cache` | Safe | Redownload | `pip cache purge` |
| `dev.pycache` | `**\__pycache__`, `**\*.pyc` | Safe | Rebuild | delete |
| `dev.venv` | `**\.venv`, `**\venv` | Safe | Rebuild | delete; `pip install -r` restores |
| `dev.cargo` | `~\.cargo\registry`, `**\target\{debug,release}` | Safe | Rebuild | `cargo clean` |
| `dev.go_modcache` | `~\go\pkg\mod` | Safe | Redownload | `go clean -modcache` |
| `dev.conda_pkgs` | `**\{anaconda3,miniconda3}\pkgs` | Safe | Redownload | `conda clean --all` |
| `dev.unity_library` | `**\Library\ArtifactDB` with a sibling `Assets\` | Safe | Rebuild | delete (slow reimport) |
| `dev.unreal_ddc` | `**\DerivedDataCache`, `**\Intermediate`, `**\Saved\Autosaves` | Safe | Rebuild | delete |
| `dev.git_gc` | `**\.git` where `objects` > 500 MB | **Caution** | **Irreversible** | **`git gc --prune=now`** — never delete `objects` directly |
| `dev.docker` | Docker vhdx | Caution | Redownload | `docker system prune -a --volumes` |
| `dev.vs_artifacts` | `**\.vs`, `**\CachedExtensionVSIXs` | Safe | Instant | delete |
| `app.browser_cache` | Chromium `**\User Data\*\Cache*`, `**\GPUCache`; Firefox `**\cache2` | Safe | Instant | delete |
| `app.electron_cache` | `%APPDATA%\{Slack,discord,Teams,...}\Cache`, `**\ShaderCache` | Safe | Instant | delete |
| `app.shader_cache` | `%LOCALAPPDATA%\{NVIDIA,AMD,D3DSCache}`, Steam `shadercache` | Safe | Instant | delete |
| `app.steam_downloading` | `**\steamapps\{downloading,temp}` | Safe | Redownload | delete |
| `app.adobe_media_cache` | `%APPDATA%\Adobe\Common\Media Cache*` | Safe | Rebuild | delete |
| `app.apple_backups` | `%APPDATA%\Apple Computer\MobileSync\Backup` | **Caution** | **Irreversible** | manual |
| `sys.temp` | `%TEMP%`, `%WINDIR%\Temp`, `%LOCALAPPDATA%\Temp` | Safe | Instant | delete |
| `sys.thumbnails` | `**\Explorer\thumbcache_*.db` | Safe | Instant | delete |
| `sys.old_logs` | `*.log`, `*.etl` older than 30 days outside `%ProgramData%` | Safe | Irreversible | delete |
| `sys.dumps` | `**\CrashDumps`, `MEMORY.DMP`, `**\Minidump` | Safe | Irreversible | delete |
| `user.old_installers` | `%USERPROFILE%\Downloads\*.{msi,exe,iso}` older than 90 days | Caution | Redownload* | delete |
| `user.large_media` | `*.{iso,vhd,vhdx,img,bak,vmdk}` over 1 GB | Caution | Irreversible | manual |

\* `Redownload` with the caveat "unless it's a license-bound installer".

**Deliberately not rules:** `hiberfil.sys`, `pagefile.sys`, `swapfile.sys` — in use and undeletable; they are an `AuditFinding` with a `powercfg` command. `WinSxS` — deleting it by hand breaks the system irreversibly; `DISM` only. `C:\Windows\Installer` — breaks uninstall and updates; report only. `System Volume Information` — VSS API only. `.git\objects` — destroys the repository; `git gc` only.

### 7.3. Estimating the gain

Every match is scored by **reclaimable** (§3.1), not by the sum of sizes:

```
$ pathmemo reclaim --scan 43

  RULE                       MATCHES      SIZE   RECLAIM   RISK     RECOVERY      NOTE
  ──────────────────────────────────────────────────────────────────────────────────────────────
  dev.node_modules                47   18.2 GB   18.2 GB   safe     redownload    npm ci
  dev.unity_library                3   12.4 GB   12.4 GB   safe     rebuild
  app.browser_cache                6    4.1 GB    4.1 GB   safe     instant
  dev.dotnet_artifacts           212    3.8 GB    3.8 GB   safe     rebuild
  sys.temp                         4    1.9 GB    1.9 GB   safe     instant       contents only
  dev.pnpm_store                   1    7.6 GB    1.2 GB   safe     redownload    6.4 GB shared via hard links
  dev.git_gc                       8    2.2 GB    2.2 GB   caution  irreversible  use git gc --prune=now
  user.old_installers             23    6.7 GB    6.7 GB   caution  redownload
  ──────────────────────────────────────────────────────────────────────────────────────────────
  safe only                      273   43.5 GB   37.1 GB
  including caution              304   52.4 GB   46.0 GB

  pathmemo reclaim --scan 43 --dry-run    preview what would be deleted
  pathmemo reclaim --scan 43 --apply      quarantine it (37.1 GB)
  pathmemo reclaim --rule <id>            the paths behind one row
```

```
--scan <id>   which stored scan to read        --dry-run, -n  the plan and a token
--risk <l>    safe (default) | caution | danger --apply        delete through `rm`
--rule <id>   the paths behind one rule        --mode <mode>  quarantine | recycle | permanent
--list        the rule set, no scan needed     --yes, -y      skip the y/n question
--min <size>  ignore matches below this        --force        required for --risk danger
--limit <n>   paths shown with --rule          --json         machine-readable (§13.6)
--keep <path> add to protect.keep              --disable / --enable <rule>
```

`SIZE` is what the rule found; `RECLAIM` is what deleting it gives back. The note column carries the one fact the two numbers cannot: a command that does the job better, bytes held by hard links, or how many paths the guard has already refused. Every match is opened and put to the guard while the report is built, so a row never promises space that `rm` would then decline to free (§9.3).

### 7.4. Custom rules and exclusions

Everything lives in `config.json`, the **single source of truth**; the TUI and CLI edit that file.

```json
{
  "rules": {
    "disabled": ["dev.venv", "user.old_installers"],
    "custom": [
      { "id": "my.render_output", "patterns": ["D:\\renders\\**\\frames"],
        "risk": "safe", "recoverability": "rebuild", "minSizeBytes": 104857600 }
    ]
  },
  "keep": ["C:\\Users\\me\\projects\\important\\node_modules", "D:\\archive\\**"]
}
```

`keep` holds paths and globs that **never** appear in recommendations and cannot be deleted through pathmemo. `K` in the TUI adds one.

A custom rule accepts every condition a built-in one uses — there is no private back door in the rule language: `id`, `patterns`, `risk`, `recoverability`, `what`, `command`, `kind` (`directory` | `file` | `any`), `minSizeBytes`, `olderThan`, `requiresChild`, `requiresSibling`, `notUnder`, `contentsOnly`. A custom rule whose `id` is a built-in one **replaces** it, which is how one threshold is changed without retyping the table. An entry that will not parse is skipped with a warning naming it, and the rest of the file still applies.

The live paths into the same file: `pathmemo reclaim --keep <path>`, `--disable <rule>`, `--enable <rule>`, and in the TUI `K` — on a rule it disables the rule, on a path it adds that path to `protect.keep`. Edits go through `JsonNode`, so keys pathmemo knows nothing about survive, and the file is written through a temporary and moved into place: a half-written `config.json` is a tool that will not start.

### 7.5. Implementation notes (P7)

- **The two axes are the audit's, not a second copy.** `Risk` and `Recoverability` already existed in `Audit/` from P2 (§6.1); the rules use those enums. Two identical enums in two namespaces is how a `safe` that means different things in two screens gets born.
- **A third axis was needed after all, and it is not risk.** What the table calls the "right way" splits into three: pathmemo can delete it, another tool must (`git gc`, `docker system prune`), or a person must decide (iPhone backups, disk images). That is `ReclaimAction`, and `Command` and `Manual` rules are **never** acted on, whatever the flags say — `--apply --risk danger --force` still leaves `.git` alone. Risk says how much it hurts if you are wrong; the action says who is allowed to do it at all.
- **Matching is a prefilter, not thirty globs per node.** The last segment of each pattern goes into one of three buckets: an exact name (`node_modules`, `obj`), an extension (`*.pyc`, `*.log`), or a wildcard (`Cache*`, `thumbcache_*.db`), and even the last is screened by its literal prefix and suffix before a glob runs. A node's full path is built only for a candidate — 1.2M nodes against 31 rules in a few hundred milliseconds, against several seconds for the obvious implementation (§12.2).
- **A pattern that ends in an extension is about a file.** `**\*.pyc` under a rule that says `kind: any` does not claim a directory somebody named `weird.pyc`. A rule that really means such a directory says `kind: directory`.
- **Brace alternation is not in the glob syntax.** The `{Debug,Release}` of §7.2 is written as separate patterns. One more metacharacter buys one line of table and costs every reader of every pattern.
- **Three built-in rules carry a condition §7.2 only implies.** `dev.unity_library` needs both an `ArtifactDB` inside and an `Assets` beside it, or every directory called `Library` on the disk is a Unity project. `dev.venv` needs a `pyvenv.cfg`, or a folder of holiday videos called `venv` is a virtual environment. `sys.old_logs` has a 1 MB floor that §7.2 does not state: without one it matches tens of thousands of two-kilobyte files, and a report nobody can read is the same as no report.
- **`sys.temp` deletes contents, not the directory.** Windows and half the installed software assume `%TEMP%` exists, and the guard refuses the directory itself anyway (§9.3). `contentsOnly` is a rule flag, and the children are read from the live filesystem rather than from a snapshot that may be days old.
- **Hard links are counted as shared, always.** A file with more than one link keeps its data until the last name goes, so deleting this one may free nothing. Proving the other links are inside the same set needs file identity, which `.pmsnap` v1 does not carry (§5.3). Every multiply-linked file therefore counts as shared, which understates the gain — an underestimate disappoints, an overestimate is a promise of space that never arrives. It is also right far more often than it looks: a pnpm store hard-linked into live projects genuinely frees nothing.
- **The guard is consulted while the report is built, not afterwards.** One handle per match, a second or so for the few hundred a real disk produces. A recommendation the guard would refuse is worse than no recommendation: it is a number in the total that never comes. A path that has gone since the scan drops out; one the guard refuses stays, with the reason beside it.
- **`--apply` is `rm` with a list.** The confirmation, the journal, the mode, the re-check at execution time and the free-space measurement are all the ones from §9, so there is no second deletion path with its own mistakes in it. The journal entry records the rule ids in its reason.
- **`Risk = Danger` now means something outside reclaim.** Any deletion — typed at a prompt, marked in the tree — whose path matches a `danger` rule needs `--force` **and** the typed confirmation, which `--yes` cannot answer (§13.1). This closes the P6 note that `--force` was only a synonym for `--yes`.
- **The TUI runs the rules on a background thread.** A frame is budgeted at 16 ms and the rule pass is a few hundred; until it finishes, the badges are simply absent, which is what a node no rule claims would show anyway. That index skips the guard check — it is a per-handle cost behind a screen the user may never open — and the delete dialog does the guarded plan before anything happens.
- **Two bugs the live runs found, both in P6's verification (§9.3, step 5).** Snapshot timestamps are seconds since 2000-01-01 (§5.3) and the comparison read them as Unix seconds, putting every expectation thirty years in the past: *every* file the scan knew about was refused as "modified since the scan". It went unnoticed because the P6 test stored a Unix timestamp too, so the two mistakes cancelled, and because a file created after the scan has no expectation to disagree with. Second, a directory was judged by its modification time, which moves whenever anything inside is written — that is every cache the rules exist to find. A directory is now judged by its total, which the plan has just measured live, with a tenth of slack.

---

## 8. Duplicates

### 8.1. Algorithm

```
Stage 0  filter
         · size >= minSize (1 MB by default), size != 0
         · not CloudOnly, not Reparse, not Encrypted, not in `keep`
         · group by size; unique sizes dropped

Stage 1  hard-link collapse
         · files sharing (VolumeSerial, FileReferenceNumber) are ONE file:
           grouped as a `HardlinkSet`, saving from deletion = 0

Stage 2  partial hash - XxHash128 of the first and last 64 KB
         (or the whole file when <= 128 KB); singletons dropped

Stage 3  full hash - XxHash128 streamed, 1 MB buffer, ArrayPool

Stage 4  byte-for-byte verification of the finalists
         · pairwise inside the group, 1 MB blocks
         · a MATHEMATICAL guarantee instead of a probabilistic one, for the same IO
         · few groups survive stage 3, so it is cheap
```

### 8.2. Why XxHash128 and not BLAKE3

The bottleneck is **IO, not hashing**: 100 MB/s on an HDD, 500–3000 MB/s on NVMe, against 10+ GB/s for XxHash3 and 2–5 GB/s for BLAKE3. `System.IO.Hashing.XxHash128` is in the box, managed, with zero native dependencies, while BLAKE3.NET means another native DLL in the self-extract, +200 ms of startup, trimming and AOT trouble and a separate arm64 build. Cryptographic strength is not needed: there is no adversary here, and stage 4 makes the guarantee absolute. MD5 was dropped — slower, weaker, and it creates the false impression of being a "crypto hash". `--hash sha256` stays for comparing with another tool, but not as the default.

### 8.3. Hash cache

The key is **not the path** — renaming would throw the cache away:

```sql
PRIMARY KEY (volume_serial, file_id_low, file_id_high, size_bytes, mtime_unix)
```

Eviction is LRU by `last_used_at`, hard-capped at 200k rows (~30 MB). Changing `hash_algo` invalidates everything.

### 8.4. Operation safety

- **At least one file always survives a group.** The UI refuses to unmark the last one; the CLI exits `EX_UNSAFE`.
- **The "original" is never chosen automatically without showing it.** Priority: not in `Temp`/`Downloads`/caches; shallower path; a `keep` match always wins; older `mtime` last. "Oldest file" alone was rejected — the oldest is usually the one in `Downloads\tmp`.
- **Everything is re-verified before deletion.** Size, mtime and the full hash of both survivor and victim are recomputed; any mismatch cancels the whole operation.
- Duplicates are searched **across volumes** (photos backed up to D: with the originals on C:).
- Files are opened with `FileShare.ReadWrite | FileShare.Delete` (otherwise half of `AppData` is unreadable) and `FILE_FLAG_SEQUENTIAL_SCAN`.
- **Cloud placeholders are never hashed.** Reading one triggers a download, so "find duplicates" could pull 200 GB of traffic and **fill** the disk. Attributes are checked before opening, and `FILE_FLAG_OPEN_NO_RECALL` is the second barrier.

### 8.5. Antivirus warning

A full read pass makes Defender scan everything read. Before starting:

```
Hashing will read 84.2 GB from disk. Real-time antivirus scanning may
slow this down 2-5x and use significant CPU.

You can exclude pathmemo from Defender yourself (run as admin):
  Add-MpPreference -ExclusionProcess pathmemo.exe
pathmemo will not change your security settings.

  [Enter] Continue    [S] Skip files over 1 GB    [Esc] Cancel
```

---
## 9. Deletion

The most dangerous module. Designed so that nothing is deleted until three independent checks agree.

### 9.1. The Recycle Bin does not free space

`$Recycle.Bin` lives **on the same volume**: moving 40 GB into it frees **0 bytes**. On top of that, the bin has a quota (~5% of the volume by default) and the Shell **silently deletes forever** anything larger — so the "safe" path is more dangerous than the direct one; moving 200k small files into it takes minutes and writes an `$I` record for each; and there is no bin on network drives, often none on removable ones, and different behaviour on ReFS.

### 9.2. Three modes, chosen by context

| Mode | When | Frees space now | Undo |
|---|---|---|---|
| `Recycle` | ≤ 100 files, ≤ 500 MB total, the volume has a bin, `Recoverability != Instant` | **no** | Explorer → Restore |
| `Quarantine` | default for everything else | no (until `purge`) | `pathmemo restore <op-id>` |
| `Permanent` | explicit `--permanent` / `Shift+D`, or `Recoverability == Instant` | **yes** | none |

The UI always says what will happen, without euphemism:

```
Delete 47 items · 18.2 GB

  Mode:   quarantine  (moved aside, disk space freed after purge)
  Frees now:      0 bytes
  Frees on purge: 18.2 GB
  Undo:   pathmemo restore op-118    (until purged)

  [Tab] change mode: quarantine / permanent
  [Enter] proceed   [L] list items   [Esc] cancel
```

`Permanent` over 1 GB, or at Risk ≥ `Caution`, requires typing `delete 47` to confirm.

### 9.3. Path canonicalisation and protection

**String comparison of paths was rejected as unsafe.** A list of literal strings can be walked around at least twelve ways:

```
c:\windows                      case
C:\Windows\                     trailing slash
C:\WINDOW~1                     8.3 short name
\\?\C:\Windows                  Win32 prefix
\\.\C:\Windows                  device prefix
\\localhost\c$\Windows          UNC to self
\\127.0.0.1\c$\Windows          the same
C:\Users\..\Windows             traversal
C:\Documents and Settings\...   junction -> C:\Users
C:\Users\All Users\...          junction -> C:\ProgramData
D:\mnt\sys\...                  mount point onto the system volume
```

and a hard-coded `C:\` is wrong to begin with — the system volume need not be C:.

```
1. Open a handle:
     CreateFileW(path, 0 /* query only */,
                 FILE_SHARE_READ|WRITE|DELETE, NULL, OPEN_EXISTING,
                 FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL)
   Cannot open -> refuse. We never delete what we could not open.

2. canonical = GetFinalPathNameByHandleW(h, VOLUME_NAME_GUID)
   -> \\?\Volume{GUID}\Windows\System32, which eliminates every line above.

3. Check canonical against ProtectedSet (canonicalised at startup):
     · KnownFolders via SHGetKnownFolderPath, not hard-coded paths:
       Windows, System, SystemX86, ProgramFiles(x86), ProgramFilesCommon,
       ProgramData, UserProfiles, Profile, Public, Fonts, Startup,
       StartMenu, RoamingAppData
     · any volume root, any path at depth <= 1 from one, any mount point
     · System Volume Information, $Recycle.Bin (except the "empty" operation)
     · `keep` paths from the config
     · pathmemo's own data directory (except `purge`)
   A match, or canonical being a PREFIX of a protected path -> refuse.
   Being inside a protected path -> refuse, EXCEPT for explicitly allowed
   subpaths (C:\Windows\Temp, SoftwareDistribution\Download, Logs,
   CrashDumps): a whitelist inside the blacklist, defined by rules.

4. Check that canonical's volume is the volume the operation asked for.

5. Re-read size and mtime through FILE_BASIC_INFO / FILE_STANDARD_INFO on
   THIS handle. Disagreement with the snapshot -> refuse, rescan needed.

6. Delete through that same handle (§9.5), never by path.
```

*P7:* step 5 applies to a **file** as written. A directory is judged by its total instead, which the plan has just measured live, with a tenth of slack: a directory's modification time moves whenever anything inside it is written, and that is every cache the reclaim rules exist to find (§7.5).

### 9.4. Quarantine

```
%LOCALAPPDATA%\pathmemo\quarantine\op-000118\
├── manifest.json        # op id, time, mode, orig path -> stored name, sizes, hashes
└── data\000001\         # original tree under a numeric name (path-length headroom)
```

- The move is `MoveFileWithProgressW` with `MOVEFILE_WRITE_THROUGH` and **without** `MOVEFILE_COPY_ALLOWED`: within a volume that is a rename — instant and atomic. If the quarantine is on another volume the operation is **refused** (copying 18 GB instead of renaming is not acceptable) and `Permanent`, or a same-volume quarantine, offered instead.
- A quarantine is created **on every volume involved**: `D:\pathmemo-quarantine\` (hidden, system) for files from D:.
- `pathmemo restore op-118` puts everything back from the manifest, checking that the target paths are free.
- `pathmemo purge` deletes for real. Automatic purge is by age (`quarantineRetentionDays`, 7) at startup, journalled, **and announced on first run** so it is never a surprise.
- The dashboard always shows `Quarantine: 18.2 GB in 2 operations — purge to reclaim`.

### 9.5. Recursive deletion without following the link

The classic vulnerability: between the check and the delete, a subdirectory is swapped for a junction to `C:\Windows\System32`, and the recursive deleter walks into it.

```
DeleteTree(parentHandle):
    for each entry in NtQueryDirectoryFile(parentHandle):
        childHandle = NtCreateFile(name,
                          RootDirectory = parentHandle,        // relative open!
                          FILE_OPEN_REPARSE_POINT)             // never follow
        if childHandle is a reparse point:
            delete the point itself, do NOT enter it
        else if directory:
            DeleteTree(childHandle)     // recursion by handle
            delete the directory by handle
        else:
            SetFileInformationByHandle(childHandle, FileDispositionInfoEx,
                                       FILE_DISPOSITION_DELETE | POSIX_SEMANTICS)
```

`RootDirectory = parentHandle` resolves the name **relative to an already-open directory**, so swapping a path mid-operation physically cannot redirect us elsewhere. `FILE_DISPOSITION_POSIX_SEMANTICS` (Win10 1709+) deletes a file another process holds open — the name disappears at once, the data when the last handle closes — which matters for caches inside running applications.

### 9.6. Recycle Bin through `IFileOperation`

`SHFileOperation` is deprecated, swallows errors and reports success after doing nothing. `IFileOperation` only:

```csharp
op.SetOperationFlags(FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
                   | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING);
op.SetOwnerWindow(IntPtr.Zero);
op.Advise(new ProgressSink());   // REQUIRED: otherwise we never learn what failed
```

COM needs STA, so this runs on a dedicated thread. Rather than letting `FOF_WANTNUKEWARNING` raise a dialog, we check the size against the bin's quota ourselves (`SHQueryRecycleBinW` plus the registry) and switch to `Quarantine` before silent destruction can happen. `PostDeleteItem` gives a per-item `HRESULT`, and every item is journalled.

### 9.7. Operations journal

```
op-000118  2026-09-17T14:22:08Z  quarantine  47 items  18.2 GB  reclaimed 0 B
  status: completed   ·   restorable until 2026-09-24
  47 succeeded, 0 failed
```

Each item is a row in `delete_items` (§11). Dry runs go to a **separate table**, `dryrun_log`: mixing real and rehearsed deletions in one journal makes the audit untrustworthy.

### 9.8. Measuring what was actually freed

`GetDiskFreeSpaceExW` is read before the operation and after it (with a 500 ms delay for deferred filesystem work):

```
Done in 1.4 s
  47 items moved to quarantine
  Predicted:  18.2 GB     Actual free space change:  0 bytes  (as expected)

  pathmemo purge op-118    to reclaim 18.2 GB
```

and after the purge, `Predicted: 18.2 GB → Actual: 18.4 GB, C: free 21.3 → 39.7 GB`. A divergence over 10% is logged and shown: the only way to notice that the size model is lying.

### 9.9. Implementation notes (P6)

- **Two classes of protected path, not one.** §9.3's list mixes the operating system's own directories with the structural ones, and treating them alike breaks the tool either way: seal `%LOCALAPPDATA%` and a 44 GB WSL disk becomes undeletable; unseal `C:\Users` and so does nothing. So `Windows`, `System32`, `Program Files`, `ProgramData`, `Fonts`, `Startup` and the Start menu are **sealed** — nothing inside them goes without an `allowInsideProtected` rule — while the profile, `AppData\Roaming`, `AppData\Local`, `C:\Users` and `Public` are **anchors**: the directory itself is refused, its contents are ordinary. Volume roots and everything directly under them are refused separately, so `D:\games` is safe from a typo while `D:\games\old` is not.
- **A whitelist opens contents, never its own anchor.** `%WINDIR%\Temp\**` makes what is in `Temp` deletable and leaves `Temp` itself protected; Windows expects that directory to exist. The rule lives in the protected set rather than in the glob syntax, so `**` keeps one meaning everywhere (§12.2).
- **A link is judged by where it points.** `C:\Users\All Users` opened with `FILE_FLAG_OPEN_REPARSE_POINT` canonicalises to itself, which no rule would catch; so a reparse point is opened a second time *following* the link and the target is checked too. That is what refuses the compatibility junctions in §9.3's table rather than a list of their names.
- **Rename by handle, at the NT layer.** Quarantine moves with `NtSetInformationFile` / `FileRenameInformation` and the destination directory's handle in `RootDirectory` — not `MoveFileWithProgressW`, which takes paths and would resolve the source again after the guard has checked it. The Win32 wrapper `SetFileInformationByHandle` cannot be used here: it rejects a non-null `RootDirectory` with `ERROR_INVALID_PARAMETER`, measured on every buffer shape. A rename cannot cross volumes, so a quarantine on the wrong disk fails with `ERROR_NOT_SAME_DEVICE` instead of quietly becoming an 18 GB copy.
- **The listing is a hint; the handle is the fact.** The recursive deleter reads each child's attributes from the handle it just opened rather than from the directory enumeration, because the swap this module exists to survive happens in exactly that window. With it, a directory that became a junction between the two calls is deleted as a link. The race test runs the attack for real: 10,000 rounds, 19,478 junctions actually swapped in, canary intact, 114 s (§22.3).
- **Modes are three code paths, not three implementations of one interface.** They do not share a contract: quarantine needs the operation id and a per-volume store handle before the first item, the Recycle Bin takes the whole batch at once on an STA thread, permanent works item by item. One interface would have been three different contracts wearing one name (§17.2).
- **Plan, then check again.** The plan is computed with query-only handles and is what the dialog, `--dry-run` and the confirmation token all describe. Execution reopens every item with delete access and re-verifies that the canonical name still matches and the size has not moved — the plan may be minutes old by the time a human answers. The size is compared against the *stream* length, not the cluster-rounded on-disk figure the plan carries; conflating the two rejected every file that was not cluster-aligned, which is most of them.
- **No journal, no deletion.** Everywhere else a busy database is a warning and the work proceeds (§11.1). Here it is a refusal with exit code 8: "every operation has a journal entry" is the promise the rest of this rests on. The row is written before the first item and closed after the last, so a process killed halfway leaves `running` rather than silence.
- **`audit --apply` at last.** A remedy that deletes paths goes through this same engine and removes the *contents* of the directories a finding names, never the directories. A remedy that runs a command needs the rights it declares or refuses, needs a typed `apply <id>` when its risk is above `safe`, and is journalled with the free-space change. The Recycle Bin finding uses `SHEmptyRecycleBin` rather than shelling out to PowerShell to do the same thing one layer further away.
- **The Recycle Bin is the one place we work from paths.** `IFileOperation` parses a path and offers no handle-taking entry point, so that mode carries a weaker guarantee than the other two — one more reason quarantine is the default. Success there is any `HRESULT` with the severity bit clear, not `S_OK`: the copy engine answers with its own codes (`COPYENGINE_S_DONT_PROCESS_CHILDREN`, 0x00270008) and reading those as failures reports working deletions as failed.

---

## 10. History and diff

### 10.1. Scheduled scans

**Without this the history is dead** — nobody scans by hand twice a week.

```
$ pathmemo schedule --weekly --time 03:00
Registered scheduled task "pathmemo weekly scan".
  Runs: every Monday at 03:00, only when the computer is idle and on AC power.
  Command: pathmemo scan --all-volumes --quiet
  Remove with: pathmemo schedule --off
```

Implemented over `ITaskService` (COM) or `schtasks.exe` with `ArgumentList`: `RunOnlyIfIdle`, `StartWhenAvailable`, `DisallowStartIfOnBatteries`, priority `BELOW_NORMAL`. Registering in the current user's context (no password) means the scan runs unelevated and degraded; when installed elevated, `RunLevel = Highest` is offered for MFT scans.

### 10.2. Diff

```
$ pathmemo diff 41 43

C:   +18.4 GB     2026-09-10 03:00 → 2026-09-17 03:00

  GREW                                                    +24.1 GB
    C:\Users\me\AppData\Local\Docker                       +9.8 GB   12.1 → 21.9
    C:\Users\me\projects\bigapp\node_modules               +4.2 GB   0.1 → 4.3
    C:\Program Files\Epic Games\Fortnite                   +3.9 GB
    ... 42 more

  SHRANK                                                   -5.7 GB
    C:\Users\me\Downloads                                  -4.1 GB
    ... 8 more

  APPEARED                                                 +6.2 GB   (118 paths)
  DISAPPEARED                                              -6.2 GB   (204 paths)

  Largest single new file:
    C:\Users\me\Downloads\Win11_24H2.iso                    5.8 GB
```

Both snapshots are loaded and walked in step over sorted child names (a merge join per directory), with depth limited to the first level that explains the change — `node_modules\.bin\x` is not shown when all of `node_modules` grew. Both snapshots must be available and neither may be `Partial`.

#### 10.2.1. Implementation notes (P4)

- **The threshold is a fraction of the change, not of the parent's size.** 1% of a 500 GB volume root is 5 GB, which would silence the diff about nearly everything. What is implemented is `max(64 MB, 1% of this level's change)`: on a disk that grew by 18 GB the top-level threshold is 180 MB, and on one that moved by 200 MB it is still 64 MB. The threshold narrows on the way down by itself.
- **Rows do not overlap, and their sum plus the remainder equals the volume's change.** When children explain only part of a directory's change, the rest is one `residual` row for the directory itself (`C:\mix  +6.0 GB  other entries here`) — an honest way to say "a thousand small files here". When no child clears the threshold the directory is shown whole and the descent stops, which is how `C:\Windows\SoftwareDistribution` becomes one line instead of 400.
- **`UNEXPLAINED` closes the arithmetic.** A remainder below its level's threshold does not become a row, so `GREW + SHRANK` need not match the volume's change. The difference is printed as its own line rather than left as a mystery: on a real pair of scans it was 92 MB out of 917 MB. A reader who adds two columns, gets a third number and stops trusting the tool is right to.
- **Appeared and disappeared are shown two ways.** Large new (or vanished) entries appear in `GREW`/`SHRANK` tagged `new`/`gone` — otherwise a new 10 GB directory would have no path in the report — while the summary over everything stays a separate block. That count is gathered only where the report descended, and the line underneath says so: a number without its limits is exactly why disk tools are not believed.
- **Volumes are matched by letter.** A volume present in only one snapshot, or a serial mismatch (same letter, different filesystem), is printed as a `!` row.
- **Incomparable scanners are called out** — different scanner, elevation, hard-link policy or ADS accounting give four separate warnings ahead of the numbers. On real snapshots an unelevated walk against an MFT scan yields `+1.71 GB C:\$MFT (new)` and `-1.6 GB C:\Windows\WinSxS`, which is visibility, not disk change.
- **Refusals:** no snapshot, an unreadable snapshot, a `Partial` snapshot — each with its own message and `EX_NO_DATA`. Argument order does not matter: snapshots are sorted by time so growth never reads as shrinkage.
- **Without arguments** it compares the last two snapshots; with one, that one against the latest. `--limit`, `--min`, `--size`, `--json`.
- **Cost:** 405 ms to load two 1.6M-node snapshots, 105 ms for the merge join on a real walk/MFT pair, 5 ms where one directory changed. `PATHMEMO_DIAG=1` prints the breakdown to stderr.

---
## 11. Database schema

SQLite holds **only** what has to be queryable and long-lived. The file tree lives in snapshots (§5).

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store   = MEMORY;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size   = -16384;     -- 16 MB, not 64: the database is small

-- Versioned by PRAGMA user_version, no DbUp.
-- integrity_check runs ONLY after an unclean exit (a .clean marker file is
-- removed at startup and written on a clean shutdown).

CREATE TABLE scans (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at          TEXT    NOT NULL,          -- ISO-8601 UTC
    finished_at         TEXT,
    status              TEXT    NOT NULL,          -- running|completed|cancelled|failed
    scanner             TEXT    NOT NULL,          -- mft|walk|incremental
    flags               INTEGER NOT NULL DEFAULT 0,
    roots               TEXT    NOT NULL,          -- JSON: ["C:\\","D:\\"]
    total_files         INTEGER NOT NULL DEFAULT 0,
    total_dirs          INTEGER NOT NULL DEFAULT 0,
    allocated_bytes     INTEGER NOT NULL DEFAULT 0,  -- unique allocated
    logical_bytes       INTEGER NOT NULL DEFAULT 0,
    duration_ms         INTEGER,
    error_count         INTEGER NOT NULL DEFAULT 0,
    tool_version        TEXT    NOT NULL,
    snapshot_path       TEXT,                       -- NULL once retention drops it
    snapshot_bytes      INTEGER,
    note                TEXT
);
CREATE INDEX idx_scans_started ON scans(started_at DESC);

CREATE TABLE scan_volumes (
    scan_id         INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    letter          TEXT    NOT NULL,
    label           TEXT,
    filesystem      TEXT,
    volume_serial   INTEGER NOT NULL,
    volume_guid     TEXT,
    cluster_bytes   INTEGER NOT NULL,
    total_bytes     INTEGER NOT NULL,
    free_bytes      INTEGER NOT NULL,
    scanned_bytes   INTEGER NOT NULL,
    metadata_bytes  INTEGER,
    unaccounted_bytes INTEGER,
    usn_journal_id  INTEGER,
    next_usn        INTEGER,
    PRIMARY KEY (scan_id, letter)
);

-- Aggregates for multi-year graphs; they outlive the snapshot.
-- Same shape for both: (scan_id, key, allocated_bytes, file_count).
CREATE TABLE scan_category_totals (
    scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
    category TEXT NOT NULL,   -- media|archive|cache|source|document|app|system|other
    allocated_bytes INTEGER NOT NULL,
    file_count INTEGER NOT NULL,
    PRIMARY KEY (scan_id, category)
);
CREATE TABLE scan_extension_totals ( /* ... extension TEXT ... */ );

CREATE TABLE audit_findings (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    scan_id       INTEGER REFERENCES scans(id) ON DELETE CASCADE,
    probed_at     TEXT    NOT NULL,
    finding_id    TEXT    NOT NULL,   -- "vss.shadow-storage"
    volume        TEXT,
    used_bytes    INTEGER,
    reclaimable_bytes INTEGER,
    risk          TEXT    NOT NULL,
    recoverability TEXT   NOT NULL,
    status        TEXT    NOT NULL,   -- ok|unknown|probe_failed
    detail_json   TEXT
);
CREATE INDEX idx_audit_scan ON audit_findings(scan_id, reclaimable_bytes DESC);

CREATE TABLE delete_ops (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at        TEXT    NOT NULL,
    finished_at       TEXT,
    mode              TEXT    NOT NULL,   -- recycle|quarantine|permanent
    source            TEXT    NOT NULL,   -- tree|reclaim|dupes|audit|cli
    scan_id           INTEGER REFERENCES scans(id) ON DELETE SET NULL,
    item_count        INTEGER NOT NULL,
    predicted_bytes   INTEGER NOT NULL,
    actual_freed_bytes INTEGER,
    status            TEXT    NOT NULL,   -- completed|partial|failed|restored|purged
    quarantine_path   TEXT,
    purge_after       TEXT,
    reason            TEXT
);
CREATE INDEX idx_delete_ops_date ON delete_ops(started_at DESC);

CREATE TABLE delete_items (
    op_id         INTEGER NOT NULL REFERENCES delete_ops(id) ON DELETE CASCADE,
    seq           INTEGER NOT NULL,
    original_path TEXT    NOT NULL,
    stored_name   TEXT,                   -- name inside the quarantine
    size_bytes    INTEGER NOT NULL,
    content_hash  TEXT,                   -- checked on restore
    result        TEXT    NOT NULL,       -- ok|skipped|failed
    hresult       INTEGER,
    message       TEXT,
    PRIMARY KEY (op_id, seq)
);

-- Dry runs, SEPARATE from real operations.
CREATE TABLE dryrun_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT, ran_at TEXT NOT NULL,
    source TEXT NOT NULL, item_count INTEGER NOT NULL,
    total_bytes INTEGER NOT NULL, detail_json TEXT
);

-- Hash cache keyed by file id, never by path.
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

-- Duplicate groups from the last run: a cache, not history.
--   dupe_runs   (id, ran_at, scan_id, min_size, hash_algo, group_count, wasted_bytes)
--   dupe_groups (id, run_id, size_bytes, file_count, wasted_bytes,
--                kind = duplicate|hardlink_set, full_hash)
--   dupe_files  (group_id, seq, path, mtime_unix, link_count, suggested_keep)
```

Snapshot writing batches 20k rows for aggregates and runs `PRAGMA wal_checkpoint(PASSIVE)` after each scan operation: one transaction around a million inserts would inflate the WAL to the size of the data.

### 11.1. Implementation notes (P4)

- **The schema is created whole, as one version.** `user_version` = 1, `Storage/Schema.sql` as an embedded resource, applied in one transaction. Tables for later phases are created now and sit empty at a few hundred bytes: one schema version is simpler than six, and P6 will not open with a migration.
- **`scans.id` is the snapshot's file number.** The id is allocated before the scan starts, by one `AUTOINCREMENT` insert, and the snapshot written under it — otherwise `history` and `tree --scan 42` would be talking about different scans. An insert rather than "max plus one" because of concurrency: two processes started together get different numbers. Deleting the database breaks nothing: snapshots on disk are imported back under their own numbers (4 real snapshots in 1.6 s). Only when the database is entirely unavailable does the number come from the snapshot directory — the one case where numbering is not atomic.
- **The file is the fact; the row is an index.** Before history is read the two are reconciled: a snapshot without a row is imported, and a row without a snapshot (retention, manual deletion, a truncated or foreign file — the header is checked) loses `snapshot_path` but keeps its numbers. That makes `snapshot_available = 0` an observable state rather than a promise.
- **The database is a convenience, not a precondition.** A locked or broken database is not worth a lost scan: the snapshot is written anyway, one line goes to stderr, and the database is left alone for the rest of the run. `EX_LOCKED` comes only from commands that require it. In WAL mode a reader is never blocked, so a second process can browse history while the first writes.
- **`integrity_check` only after a dirty exit**, via the `.clean` marker; on a real database it takes milliseconds and never runs in a normal cycle.
- **`--no-save` writes not one row.** Otherwise a scripted JSON export would quietly fill up the history.
- **Aggregates.** Eight categories, plus the 250 largest extensions and a `(rest)` row — a full disk has tens of thousands of distinct suffixes. The category is inherited from the directory (`C:\Windows` → system, `node_modules` → cache) and only then taken from the extension: `C:\Windows` is full of `.wav` files that are not the user's media. NTFS metafiles at a volume root are system too, but only at the root — a user file is entitled to be called `$draft`. Computed once per scan and sent to the database, the `By category` block and the JSON alike. On a real C: (walk, 1.22M files, 197 GB): system 62.5 GB, other 49.8, cache 34.5 (613k files), app 34.2, archive 12, source 2.6, media 1.7, document 0.2.
- **Still NULL:** `metadata_bytes`, `usn_journal_id`, `next_usn` arrive with P9; `dupe_*` and `file_hashes` with P8. *P6:* `delete_ops`, `delete_items` and `dryrun_log` are written, and a deletion refuses to run at all when the database cannot be opened (§9.9).

---

## 12. Configuration

`%LOCALAPPDATA%\pathmemo\config.json`. Keys are camelCase, sizes accept a human form (`"1GB"`, `1048576`), durations look like `"30d"`.

```json
{
  "scan": {
    "preferMftScanner": true, "promptForElevation": true, "parallelism": "auto",
    "excludeFromScan": [], "doNotRecurse": ["\\\\*"], "includeCloudOnlyFiles": true,
    "progressIntervalMs": 250, "useUsnIncremental": true
  },
  "protect": {
    "keep": [],
    "allowInsideProtected": [
      "%WINDIR%\\Temp\\**", "%WINDIR%\\SoftwareDistribution\\Download\\**",
      "%WINDIR%\\Logs\\**", "%WINDIR%\\Prefetch\\**"
    ]
  },
  "delete": {
    "defaultMode": "quarantine", "recycleMaxItems": 100, "recycleMaxBytes": "500MB",
    "quarantineRetentionDays": 7, "autoPurgeExpired": true,
    "requireTypedConfirmationOverBytes": "1GB", "verifyBeforeDelete": true
  },
  "duplicates": {
    "minSize": "1MB", "hashAlgorithm": "xxh128", "bufferSize": "1MB",
    "partialHashBytes": "64KB", "byteForByteVerify": true, "crossVolume": true,
    "skipCloudOnly": true, "hashCacheMaxEntries": 200000
  },
  "rules": { "disabled": [], "custom": [] },
  "storage": {
    "dataDirectory": "%LOCALAPPDATA%\\pathmemo", "keepRecentSnapshots": 20,
    "keepMonthlySnapshots": 12, "snapshotsMaxBytes": "400MB"
  },
  "logging": { "level": "Information", "retentionDays": 14, "logFilePaths": false },
  "ui": { "sizeMode": "unique", "minWidth": 80, "minHeight": 24, "mouse": true,
          "confirmOpenExecutables": true },
  "export": { "redactPaths": false }
}
```

### 12.1. Elevated mode ignores user config for paths

The config lives in `%LOCALAPPDATA%`, writable by an ordinary user, while an MFT scan runs as administrator. A `logging.filePath` or `database.path` taken from that config would be **arbitrary file write as administrator** — a local privilege escalation. So whenever `IsProcessElevated()`:

- `storage.dataDirectory` and the log path **do not come from the config**, only from the calling user's fixed `%LOCALAPPDATA%` (via `WTSQueryUserToken` / `SHGetKnownFolderPath` with the session token);
- the config file's DACL is checked: if anything wider than `Administrators` / `SYSTEM` / the owner can write it, a warning is printed and `rules.custom`, `protect.*` and everything else affecting deletion is **ignored**;
- **there is no "run a command after cleanup" key**, and there never will be.

*Implemented in P4:* the same rule covers `--data-dir`. When elevated it prints a warning and is ignored: a command-line argument from an administrator is the same arbitrary-write primitive as a config key.

*P6:* `config.json` is read, for the `protect` and `delete` sections. While elevated its DACL is checked first, and if anything outside `Administrators` / `SYSTEM` / the owner can write the file it is ignored with one warning — an unprivileged user must not be able to stage `"allowInsideProtected": ["**"]` for an elevated process to obey. A file that will not parse yields the defaults and a warning rather than a failure to start; an unusable single value warns about that key alone and keeps its default.

### 12.2. Globs, not regex

One syntax, not two. Matching is `FileSystemName.MatchesSimpleExpression` (built in, zero allocations) extended with `**` for "any number of segments". Variables (`%WINDIR%`, `%TEMP%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%USERPROFILE%`, `%ProgramData%`, `%ProgramFiles%`, `~`) expand through `SHGetKnownFolderPath`, never a hard-coded `C:\`. Matching runs against **canonicalised** paths.

Regex is opt-in as `{ "regex": "..." }`, **mandatorily** with `RegexOptions.NonBacktracking | CultureInvariant` and `matchTimeout = 50 ms`: without `NonBacktracking` one bad pattern would hang a million-path scan on catastrophic backtracking. Where possible a rule matches on the name and path suffix rather than the whole path — a hash-set prefilter on the last segment rejects 99% of nodes in O(1).

---

## 13. CLI

Readable output by default, `--json` for scripts.

```
pathmemo                                   TUI when attached to a TTY, else `status`

pathmemo scan [<path>...] [options]        scan; no path means every fixed volume
pathmemo status                            last scan plus free space
pathmemo tree [<path>] [--scan <id>]       tree, largest first, non-interactive
pathmemo top [options]                     largest files and folders, with filters
pathmemo audit [--id <finding>] [--apply <finding>]
pathmemo reclaim [options]                 cleanup recommendations (options: §7.3, §7.4)
pathmemo dupes [options]                   duplicate search
pathmemo rm <path>... [options]            deletion (quarantine by default)
pathmemo restore <op-id>                   restore from quarantine
pathmemo purge [<op-id>|--expired|--bin]   free what a quarantine (or the bin) holds
pathmemo ops [<op-id>] [--limit N]         deletion journal
pathmemo history [--limit N] [--since <date>]
pathmemo diff <id-a> <id-b>
pathmemo errors <scan-id>
pathmemo export <scan-id> --format json|csv --output <file> [--redact]
pathmemo schedule [--weekly|--daily] [--time HH:MM] | --off | --status
pathmemo config [--path | --edit | --reset]
pathmemo doctor                            rights, USN, filesystem, database, version
```

### 13.1. Common options

```
--json                  stable machine-readable stdout, logs to stderr
--quiet                 errors only
--no-color              no ANSI (NO_COLOR and a non-TTY are honoured too)
--size logical|allocated|unique      default: unique
--yes                   skip confirmations (NOT for permanent delete or Risk=Danger,
                        which always need --force plus a typed confirmation)
--data-dir <path>       alternative data directory (ignored when elevated)
--verbose / -v
```

*P4:* `--data-dir` is stripped from the arguments before the command is parsed, so it works with any of them, and when elevated it warns and is ignored (§12.1). `--json` exists on `scan`, `diff`, `history` and `audit`; `--size` on `tree`, `top` and `diff`.

*P6:* `--yes` works on `rm`, `purge` and `audit --apply`, and never answers the typed confirmation that a large permanent deletion demands - that is what `--confirm-token` is for. `--json` covers `rm`, as a plan before the fact or a result after it.

*P7:* `--force` stops being a synonym for `--yes`. Anything a rule calls `Risk = Danger` — however the path arrived, typed at a prompt or marked in the tree — needs `--force` **and** the typed confirmation, and `--yes` answers neither. `--json` covers `reclaim` too.

*P5:* `status` prints the Overview screen's data as text — volumes with free space and unaccounted bytes, the last scan, the size of the store — and is what a bare `pathmemo` prints with stdout redirected (§14.5). `--no-color` is not parsed as an option yet, but `NO_COLOR` in the environment is honoured: no palette, selection still inverted.

### 13.2. `scan`

```
pathmemo scan [<path>...]
  --all-volumes             every local fixed volume
  --scanner mft|walk|auto   default: auto
  --no-elevate              no relaunch prompt, go degraded immediately
  --full                    ignore USN, scan from scratch
  --exclude <glob>          repeatable; this scan only
  --parallelism <n|auto>    --note <text>    --no-audit
  --format console|json|csv --output <file>
```

*P3:* `--scanner`, `--no-elevate`, `--parallelism`, `--note`, `--top`, `--no-save`, `--quiet`, plus `--pause`, which the tool passes to itself when relaunching elevated so the new console window does not close before the user has seen the result.

*P4:* `--format` and `--output`; `--json` is a synonym for `--format json`. The JSON carries `schemaVersion`, scan metadata, volumes with reconciliation, categories, tops and a `limitations` list — the machine-readable version of the accuracy warning the console prints. CSV is a table of the largest entries per `--top`. In a non-console format no progress is printed and the write confirmation goes to stderr: stdout carries data only (§13.6).

### 13.3. `top`

The main semi-CLI scenario: find the fat things, with filters.

```
pathmemo top
  --scan <id>       default: the latest      --files | --dirs   default: --files
  --limit <n>       default: 40              --under <path>
  --min <size>      e.g. 1GB                 --ext iso,vhdx,zip
  --older-than 180d / --newer-than <dur>     --category <c>
  --sort size|mtime|count                    --paths-only   one path per line, for a pipe
```

```
$ pathmemo top --min 1GB --ext iso,vhdx --older-than 90d

     SIZE  MODIFIED    PATH
  ───────────────────────────────────────────────────────────────────────
  44.1 GB  2026-01-04  C:\Users\me\AppData\Local\Packages\…\ext4.vhdx
   5.8 GB  2026-03-11  C:\Users\me\Downloads\Win11_24H2.iso
   4.2 GB  2025-11-28  D:\vm\win10-test.vhdx
  ───────────────────────────────────────────────────────────────────────
  3 files · 54.1 GB
```

### 13.4. `rm`

```
pathmemo rm <path>...
  --mode quarantine|recycle|permanent     default: from the config
  --dry-run          --reason <text>      --from-stdin     --scan <id>
  --force                                 required for Risk >= Caution
  --confirm-token <token>                 non-interactive stand-in for the typed
                                          confirmation; printed by --dry-run
```

`--confirm-token` answers "how do you automate a dangerous operation without making `--yes` a skeleton key": the dry run prints a token derived from the exact list of paths and sizes, and if anything changed the token is invalid.

*P6:* all of the above except `--force`, which was accepted as a synonym for `--yes` until the risk axis it belongs to arrived in P7 (§13.1). The token is ten base32 characters over the canonical paths, sizes and modification times, sorted - so the same list in another order is the same token, and one extra byte in one file is not. `--permanent` is a spelling of `--mode permanent`, and `pathmemo delete` of `pathmemo rm`.

### 13.5. Exit codes

| Code | Constant | Meaning |
|---|---|---|
| 0 | `EX_OK` | success |
| 1 | `EX_FAILURE` | general failure |
| 2 | `EX_USAGE` | bad arguments |
| 3 | `EX_PARTIAL` | partially done (some paths unreachable) |
| 4 | `EX_CANCELLED` | cancelled by the user |
| 5 | `EX_NEEDS_ELEVATION` | administrator rights required |
| 6 | `EX_UNSAFE` | refused by a guard (protected path, last copy, verify failed) |
| 7 | `EX_NO_DATA` | no snapshot or scan for the query |
| 8 | `EX_LOCKED` | database held by another process; only from commands that require it (§11.1) |

### 13.6. JSON stability

`--json` emits an object with `"schemaVersion": 1`. Fields are only ever added within a major version. Anything that is not data goes to stderr. Numbers are bytes as integers, not strings; times are ISO-8601 UTC with `Z`.

*P6:* `rm --json` emits the plan (with its token and every refusal and its reason) when nothing is to be done or `--dry-run` is given, and the per-item result otherwise.

*P4:* the contract is pinned by tests on `scan --format json` and on CSV quoting. The writer is a hand-written `Utf8JsonWriter` with no serializer: reflection is what makes a trimmed build fail at runtime instead of at build time (§18).

---
## 14. TUI

### 14.1. Five screens, not eleven

| # | Screen | Why |
|---|---|---|
| 1 | **Overview** | Volumes, free/used, unaccounted, quarantine, last scan, links to the rest |
| 2 | **Tree** | **The main screen.** Top-down navigation, like `ncdu`. 80% of the product's value. |
| 3 | **Audit** | Findings from §6 with their remedies |
| 4 | **Reclaim** | Recommendations from §7, grouped by rule, bulk selection |
| 5 | **Duplicates** | Groups, choosing the original |

`1`…`5` switch. Modals: `Details`, `Confirm delete`, `Search`, `Help`, `Sort`.

*Implemented in P5:* screens 1 and 2, the `Details`, `Search`, `Help` and `Sort` modals, plus a shared `Confirm`. `3` opens the line-based audit view from P2; `4` and `5` say honestly which phase they arrive in. *P7:* `4` is the reclaim screen; only `5` still names its phase.

*P6:* the delete modal is its own view rather than a `Confirm`, because it has state: `Tab` cycles the mode and every line - what it frees now, what it frees on purge, what undo costs - is recomputed from a fresh plan, not patched. `L` lists the items and the guard's refusals. It hands off to the ordinary console to run, where the progress line, the confirmation and the free-space report belong (§14.6).

**Settings** is not a screen (a JSON editor in a terminal is days of work for nothing; `c` opens the config in an external editor and `F5` reloads), nor is **Errors** (a counter on Overview plus `pathmemo errors`), nor **History/Diff** (CLI, with a sparkline on Overview).

### 14.2. The main screen — Tree

```
 pathmemo   C:\Users\me\AppData\Local                      unique  ·  scan 43  ·  03:00
 ─────────────────────────────────────────────────────────────────────────────────────
  Total 84.2 GB                                             ../  C:\Users\me
 ─────────────────────────────────────────────────────────────────────────────────────
   44.1 GB  ████████████████████░░░░░░  52.4%  Packages/                    12,481
   21.9 GB  ██████████░░░░░░░░░░░░░░░░  26.0%  Docker/                       8,102
    6.2 GB  ███░░░░░░░░░░░░░░░░░░░░░░░   7.4%  Temp/                        41,209
    4.1 GB  ██░░░░░░░░░░░░░░░░░░░░░░░░   4.9%  Google/                      19,884
    1.9 GB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   2.3%  npm-cache/          redownload 9,441
    1.2 GB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   1.4%  CrashDumps/              safe     14
▸ 940.2 MB  ░░░░░░░░░░░░░░░░░░░░░░░░░░   1.1%  NVIDIA/                  safe  2,014
   512.0 MB ░░░░░░░░░░░░░░░░░░░░░░░░░░   0.6%  thumbcache_1024.db  link  sparse
 ─────────────────────────────────────────────────────────────────────────────────────
 j/k move  l/Enter in  h out  y copy  e explorer  x mark  d delete  / search  ? help
```

The bar and percentage are relative to the current directory. Badges on the right: the reclaim rule (`safe`, `redownload`), `link` (hard-link alias), `sparse`, `cloud`, `reparse`. Directories and files share one list sorted by size. Virtualisation is mandatory — only visible rows are rendered, and a directory with 200k children must not stutter.

*Implemented in P5:* `link`, `sparse`, `cloud`, `reparse`, `self` and `partial`; *P7* adds the reclaim badge, which reads `safe redownload` rather than one word, because the two axes are the point (§7.1) and it comes first on the row - it answers "can this go?", the others answer "what is this?". The dirs/files/all filter is on `t`, not `Tab`: `Tab` moves focus in a terminal and the host takes it. The badge column is sized from the visible rows, so where there are no links or sparse files every column goes to names.

### 14.3. Key map

In the spirit of `ncdu` and `lazygit`: **single characters**, working everywhere.

```
NAVIGATION                       ACTIONS
  j / ↓        down                y      copy path
  k / ↑        up                  Y      copy full details
  l / → / ⏎    enter dir           e      reveal in Explorer
  h / ←        parent dir          o      open file (confirm required)
  g / G        top / bottom        x / ␣  mark
  Ctrl+D/U     page down/up        a      mark all in view
  ~            volume root         X      clear marks
  1 … 5        switch screen       d      delete marked (or current)
                                   Shift+D  delete permanently
VIEW                               K      add to keep-list
  s            sort menu           i / ⏎  details (on file)
  m            size mode           r      recompute (dupes/audit)
  t            filter dirs/files
  /            search              MISC
  n / N        next / prev match   ?      help
  F5           rescan              c      open config in editor
                                   q/Esc  back
                                   Q      quit
  Ctrl+C       cancel / quit  (standard behaviour, NOT hijacked)
```

Bindings that were rejected: `Ctrl+C` for "copy path" — it is SIGINT, so a user with a hung network scan could not get out, and Windows Terminal intercepts it when there is a selection (`y` copies instead, like vim's yank); `Ctrl+Shift+C` — **intercepted by the terminal** in Windows Terminal, VS Code and ConEmu (`Y`); `Ctrl+1..6` for sorting — a digit with Ctrl has no VT sequence and cmd.exe does not deliver it (`s` opens a menu); `Ctrl+/` — delivered as `0x1F` only sometimes (`?`); `Ctrl+D` for delete — it is EOF and "page down" in most TUIs, a dangerous binding for a destructive act (`d` plus a dialog); `F10` to quit — conhost takes it as a menu, and tmux takes F-keys (`Q`).

### 14.4. Rendering untrusted names

File names are **input from an untrusted source**. Before drawing:

1. **Bidi overrides are stripped** (`U+202A..U+202E`, `U+2066..U+2069`, `U+200E/200F`). Otherwise `annexe[U+202E]txt.exe` displays as `annexe.txt` — classic spoofing, shown to a user with an "open" key right there.
2. **Control characters** become `·`. WSL and Samba create such names and they wreck ANSI layout.
3. **Width is East Asian Width plus emoji**, not `string.Length`: CJK and emoji take two columns, and without this the table falls apart on the first Chinese file name.
4. **Truncation in the middle**: `C:\Users\me\…\node_modules\.bin` beats `C:\Users\me\projects\bigap…`.
5. **Zero-width characters** are removed.

*Implemented in P5:* `Sanitizer` (1, 2, 5) and `TextWidth` (3) are pure functions with unit tests, including that `annexe\u202Etxt.exe`. Width comes from a binary-searched table of East Asian Width W/F ranges plus emoji blocks; combining marks and `U+FE0F` are zero. ZWJ sequences are measured per part: whether the terminal ligates them cannot be known from here — Windows Terminal does, conhost does not — and choosing conhost keeps alignment where it breaks more visibly. The sanitised name goes to the screen; the real one to the clipboard and Explorer.

### 14.5. Terminal size and resizing

Minimum **80×24** (80 columns is the default of half the windows out there). Under 100 columns the bar and file counter disappear, leaving size, percentage and name. The `SIGWINCH` equivalent (polling the width every 200 ms) triggers a redraw. With output redirected the TUI does not start at all, and a bare `pathmemo` behaves as `pathmemo status`.

### 14.6. Logs and the TUI

Logs go **to a file only**, never to stdout or stderr while the TUI is up. With `--json`, data goes to stdout, diagnostics to stderr, and the TUI is off.

### 14.7. Implementation notes (P5)

**The renderer is a frame buffer with row-level diffing.** `Screen` keeps two copies of the frame and sends only the rows that changed. Redrawing everything on each keystroke means flicker in conhost and unusability over SSH. Diffing by row rather than by cell: per-cell rendering needs an attribute buffer and run coalescing, for a gain nobody sees across 24 rows.

**"Erase first, then write" is not cosmetic.** The first version wrote the row and appended `ESC[K`; on a live terminal every row filled to the right edge lost its last character. Writing into the final column leaves the cursor there in the pending-wrap state, and an erase-to-end-of-line then wipes the cell just written. Found in a screenshot of a real window (`937,09` instead of `937,094`), closed by a test on the exact byte sequence.

`Line` truncates by **columns**, not characters, so no caller can skew a table. The selected row is drawn with reverse video: reverse is not colour, so it survives `NO_COLOR`, which disables only the palette.

**Input is polled `Console.ReadKey(intercept: true)`, not `ReadConsoleInputW`.** The BCL already folds Windows Terminal's VT sequences and conhost's virtual-key records into one `ConsoleKeyInfo`; rewriting that is a week of edge cases. Polling `KeyAvailable` in short slices is needed because the loop must also notice a resize. `TreatControlCAsInput` is left alone — Ctrl+C stays a standard cancel through `CancelKeyPress`.

**The terminal is restored three ways:** a `finally` in the host, `ProcessExit` (a second Ctrl+C, a crash) and an explicit `Restore`. The alternate buffer preserves the user's scrollback: the tree never enters their history, and exiting returns the shell exactly as it was.

**Anything that prints leaves the alternate buffer.** An `F5` rescan, the audit screen, opening the config — the host hands the terminal back, does the work in an ordinary console with its progress and questions (including the elevation offer), waits for Enter and returns. That is how §14.6 is honoured without threading a logger through every call. Afterwards the snapshot and summary are reloaded: a new scan may have appeared while we were away.

**The Tree screen keeps no stack of levels.** Its state is the directory, the selection and the scroll offset; going up rebuilds the parent's list and puts the cursor on the node we came out of. A per-level cache would have to be invalidated on every change of sort, filter and size mode — exactly where a tree starts showing yesterday's order. Re-sorting 26k children costs single-digit milliseconds.

**Search covers the whole snapshot, not the current directory** — otherwise it is useless, because what is being looked for is five levels down. Names are decoded into a `stackalloc` buffer rather than materialised as strings: 1.58M short strings would cost more in collections than the search itself (§17.3). Up to 2000 matches sorted by size; `n`/`N` jump between them, clearing the filter if it hides a hit.

**Marks (`x`, `a`, `X`) are stored as node indices and cleared on any snapshot change:** an index pointing into a different tree is the worst kind of bug for a deletion list. *P6:* `d` and `Shift+D` open the delete dialog for the marks, or for the row under the cursor when there are none. The paths come out of a snapshot that may be days old, and the guard opens and re-checks every one of them - so a stale tree costs a refusal, never the wrong file. *P7:* `K` writes the path straight into `protect.keep` in `config.json`, because that file is the single source of truth and the guard reads it on the next run (§7.4); on the reclaim screen the same key disables the rule when the cursor is on a rule.

**`o` refuses to launch executables** (§15.3) before any dialog, not "with a warning". Everything else gets a confirmation showing the sanitised name, the real extension and a mark-of-the-web note. `y` confirms, **not** Enter: a dialog that appears under a finger already travelling towards Enter is not a confirmation. The delete dialog inherits the spirit of it - `Enter` proceeds there only after `Tab` and `L` have had the chance to change what proceeding means, and a large permanent deletion still has to be typed out in the console. `e` opens Explorer through `SHParseDisplayName` + `SHOpenFolderAndSelectItems` (§15.2).

**Overview and `pathmemo status` collect the same data** (`StatusReport`): free space comes from the volume and is always current, everything else comes from the last scan and is dated, so every stored number carries its scan id. A locked database costs the history block and nothing else. The sparkline scales between its minimum and maximum rather than from zero: a disk that went from 401 to 409 GB would otherwise be a flat line, which is precisely what such a row must not do.

**Glyphs follow the console font.** Frames, bars and the sparkline are box-drawing and block characters, which only a TrueType font has, and an old console profile hands a double-click the raster Terminal font. `GetCurrentConsoleFontEx` checks `TMPF_TRUETYPE` once at startup and falls back to ASCII: `#` and `.` in bars, `+`/`-`/`|` in frames, `>` for the cursor. Not a crippled mode — it is how disk utilities looked for twenty years. `PATHMEMO_ASCII=1` forces it, and tests it.

**Deviations from the letter of §14:** the filter is on `t` rather than `Tab`; the audit screen is the line-based one from P2; Enter does not confirm in dialogs. *P7:* the reclaim screen has two levels - rules, then the paths behind one rule - rather than a tree, because that is the shape of the decision: a person agrees to "all the node_modules" or picks three of them, never to something in between. `t` there is the risk ceiling rather than the row filter, and marks survive moving between the levels, so marking inside two rules and pressing `d` once is one operation and one journal entry (§9.7).

**Verified live** (conhost, 118×30): screen switching, descending, help, search ("402 matches across 1.58M nodes"), Details, quitting with `Q`; Ctrl+C inside the TUI returns the shell with its scrollback untouched; a window shrunk to 60×17 shows `pathmemo needs 80x24; this window is 60x17` and redraws in full when enlarged. Launched through the shell (the same as a double-click): title `pathmemo 0.1.0`, a 110×32 window, the icon in the title bar and on the taskbar; clicking and dragging inside the window breaks nothing; `PATHMEMO_ASCII=1` gives a fully ASCII frame.

---

## 15. OS integration

### 15.1. Clipboard

Three paths, in order: **P/Invoke** `OpenClipboard` / `SetClipboardData(CF_UNICODETEXT)` on a dedicated STA thread (~40 lines, no dependencies); **OSC 52**, which works in Windows Terminal, WezTerm, kitty and, crucially, **over SSH**, enabled when `WT_SESSION`/`TERM_PROGRAM` are known or `--clipboard osc52` is given; and finally a refusal that prints the path to stdout.

`System.Windows.Forms.Clipboard` was rejected: `<UseWindowsForms>`, +10 MB of binary, and a running message pump inside a console app. `clip.exe` was rejected as the primary path: piping encodings is finicky (UTF-16LE with a BOM) and it costs a process.

### 15.2. Reveal in Explorer

**Not through a command line.** `Process.Start("explorer.exe", $"/select,\"{path}\"")` is exploitable: a quote in the path breaks the parsing, such names do exist (created through `\\?\` by WSL, Cygwin and Samba), and `ArgumentList` does not help because `explorer /select` demands the concatenated form.

```
SHParseDisplayName(path, null, out pidl, 0, out _)
SHOpenFolderAndSelectItems(parentPidl, 1, &childPidl, 0)
```

### 15.3. Opening a file, under confirmation

`Process.Start(UseShellExecute = true)` on an arbitrary found file is **running untrusted code with one keystroke**. The tool scans the whole disk, `Downloads` included, where `invoice.pdf.exe` and `update.hta` live.

- The key is `o`, deliberately not next to the destructive ones.
- An extension on the executable list (`exe com scr bat cmd ps1 psm1 vbs vbe js jse wsf wsh hta msi msp msc reg lnk url jar appx cpl pif inf`) is **refused**: `"Refusing to launch executable files. Press e to open the containing folder instead."`
- Everything else gets a confirmation showing the **sanitised** name, the real extension and whether the file carries a mark of the web.
- `ui.confirmOpenExecutables: false` does not lift the ban on executables; it only drops the dialog for documents.

### 15.4. External tools

Read-only (§6.3): an absolute path under `%WINDIR%\System32` against PATH hijacking; `ArgumentList`, never a joined string; `UseShellExecute = false`, `CreateNoWindow = true`; a 30 s timeout with a process-tree kill; `__COMPAT_LAYER` not inherited.

### 15.5. Long paths

`<longPathAware>true</longPathAware>` in the manifest, legacy path handling off, and a `\\?\` prefix on every Win32 call past 250 characters. The MFT scanner has **no** path limit at all: it builds no strings while walking.

---

## 16. Threat model

Every row is a real scenario, not a theoretical one.

| # | Threat | Countermeasure |
|---|---|---|
| T1 | Deleting system files — a string-based protected list is bypassable twelve ways (case, 8.3, `\\?\`, UNC `c$`, junction, mount point, `..`), and `C:\` is hard-coded to begin with | Canonicalisation through `GetFinalPathNameByHandle(VOLUME_NAME_GUID)` plus `SHGetKnownFolderPath` (§9.3) |
| T2 | Recursive deletion escaping the tree — a subdirectory swapped for a junction between check and delete (TOCTOU) | Handle-relative traversal with `RootDirectory` and `FILE_OPEN_REPARSE_POINT` (§9.5) |
| T3 | Deleting something other than what was shown — hours passed since the scan | Size and mtime re-read on the same handle; a mismatch refuses (§9.3) |
| T4 | Running malware — "open file" on `invoice.pdf.exe` from `Downloads` | Executable extensions refused, confirmation for the rest, MotW warning (§15.3) |
| T5 | File-name spoofing with an RTL override `U+202E` | Bidi, control and zero-width characters stripped on render (§14.4) |
| T6 | Command-line injection — a quote in a path reaching `explorer.exe /select,"..."` | `SHOpenFolderAndSelectItems`; no command line is built (§15.2) |
| T7 | Local privilege escalation — a user-writable config read by an elevated process turns `logFilePath` into arbitrary write as admin | Elevated mode takes no paths from the config; DACL check; no "run a command" keys (§12.1) |
| T8 | PATH hijacking — `vssadmin` or `dism` replaced on `PATH` | Absolute paths under `%WINDIR%\System32`, `UseShellExecute=false` (§15.4) |
| T9 | ReDoS — a config regex against a million deep paths hangs the scan | Globs by default; regex only with `NonBacktracking` and a 50 ms timeout (§12.2) |
| T10 | Hydrating cloud files — hashing a OneDrive placeholder **downloads** it, pulling 200 GB and filling the disk | `RECALL_ON_*`/`OFFLINE` checked before opening, plus `FILE_FLAG_OPEN_NO_RECALL` (§8.4) |
| T11 | Losing the only copy — every file in a duplicate group unmarked | "At least one survives" enforced in the core, not the UI (§8.4) |
| T12 | Silent permanent deletion — a file over the Recycle Bin quota destroyed silently by the Shell | Our own quota check switches to Quarantine (§9.6) |
| T13 | Leaking private data — an export is a full map of the disk: project names, people's names | `--redact` (hashed names, structure and sizes preserved); `logFilePaths: false` by default |
| T14 | Corrupting our own database on power loss | WAL, `synchronous=NORMAL`, `integrity_check` **only after an unclean exit** |
| T15 | The tool filling the disk | Binary snapshots of 10–25 MB and a hard 400 MB cap (§5.4) |
| T16 | Recursive growth — pathmemo scans its own data, grows, scans again | Flagged `SelfData`, excluded from reclaim, undeletable except by `purge`. *P7:* the rule engine skips a `SelfData` node before it looks at its name, so no pattern can reach the store |
| T17 | The process killed mid-save — `Console.CancelKeyPress` is time-limited | The handler only sets a token; the main thread writes with a timeout; a second `Ctrl+C` is a hard exit (§4.8) |
| T18 | Symlink loop — `AppData\Local\Application Data` pointing at itself | Reparse points are never entered, plus a depth limit (256 levels; a real `node_modules` chain runs to about 40) |
| T19 | Deleting in-use files breaks an application | `NumberOfLinks` and sharing checked; `FILE_DISPOSITION_POSIX_SEMANTICS`; a "close <app> first" warning on known rules |
| T20 | Concurrent pathmemo processes writing to one database | WAL plus `busy_timeout`, and a refusal to write with a warning while still saving the snapshot. *P4:* no mutex needed — WAL serialises writers, and snapshots use different numbers |

---
## 17. Code architecture

### 17.1. Two projects, not six

```
pathmemo/
├── pathmemo.sln  ·  Directory.Build.props  ·  global.json (pins the SDK to .NET 9)
├── src/PathMemo/                      ← all the code, separated by folders
│   ├── Program.cs  ·  app.manifest (longPathAware, asInvoker)  ·  Resources/app.ico
│   │
│   ├── Cli/
│   │   ├── ArgParse.cs            value parsers: sizes, durations, dates, modes
│   │   ├── Commands/              Scan, Tree, Top, Audit (+ AuditApply), History, Diff,
│   │   │                          Doctor, Status (+ StatusReport, shared with Overview),
│   │   │                          Rm, Quarantine (restore / purge / ops), Reclaim
│   │   ├── Interactive/           Launcher, Browser, AuditView, ElevationPrompt
│   │   │                          (line-based fallback for terminals without VT)
│   │   └── Output/                SizeFormat, PathDisplay, ScanExport (json/csv),
│   │                              DeleteReport (plan, outcome, journal),
│   │                              ReclaimTable (rules, one rule's paths, json)
│   │
│   ├── Tui/
│   │   ├── TuiHost.cs             input loop and render, suspended during a scan
│   │   ├── TuiSession.cs          snapshot, size mode, marks, ITuiView
│   │   ├── Terminal/              Screen (frame buffer + row diff), Line, KeyReader,
│   │   │                          VirtualTerminal, TextWidth, Sanitizer, Draw
│   │   ├── Screens/               OverviewScreen, TreeScreen, ReclaimScreen
│   │   └── Dialogs/               Details, Confirm, Delete, SortMenu, Help
│   │                              (search is TreeScreen state, not its own file)
│   │
│   ├── Scanning/
│   │   ├── IScanner.cs            ← one of the few justified interfaces
│   │   ├── Mft/                   MftScanner (BFS, hard-link owners), MftParser
│   │   │                          (fixup, attributes, data runs), MftVolume (\\.\C:,
│   │   │                          $MFT extents from record 0, positional reads)
│   │   ├── WalkScanner.cs  ·  FastEnumerator.cs  ·  UsnIncrementalScanner.cs
│   │   ├── DirectoryWorkQueue.cs  work-stealing
│   │   └── MediaTypeDetector.cs   HDD/SSD → parallelism
│   │
│   ├── Snapshots/                 SnapshotBuilder, SnapshotFile (.pmsnap sections and
│   │                              header), SnapshotStore (numbering, retention),
│   │                              NodeStore (SoA), NameBlob, TreeAssembly
│   │
│   ├── Analysis/                  TreeQuery, SnapshotDiff (merge join), FileCategory,
│   │                              ScanAggregates, Reconciler; ReclaimModels (two axes
│   │                              plus the action), RuleEngine (prefilter, then match),
│   │                              ReclaimPlanner (nesting, keep, the guard),
│   │                              ReclaimIndex (node -> rule, for the screens)
│   │
│   ├── Audit/                     IAuditProbe ← justified: ~20 implementations;
│   │                              AuditRunner; Probes/ (Vss, WinSxS, Wsl, Docker,
│   │                              Hibernation, RecycleBin, Dumps, …)
│   │
│   ├── Duplicates/                DuplicateFinder, HashPipeline, ByteComparer, HashCache
│   │
│   ├── Deletion/
│   │   ├── PathGuard.cs           CRITICAL: open first, judge the handle
│   │   ├── ProtectedSet.cs        CRITICAL: sealed vs anchor folders, keep, allow rules
│   │   ├── Canonical.cs           volume-GUID names and segment-aware comparison
│   │   ├── HandleTreeDeleter.cs   CRITICAL: handle-relative recursion, POSIX unlink
│   │   ├── Quarantine.cs          store layout, manifest, rename by handle, restore
│   │   ├── RecycleBin.cs          IFileOperation on an STA thread, quota check
│   │   └── DeleteEngine.cs        plan, re-check, execute, free-space delta
│   │                              (no IDeleteBackend: the three modes do not share
│   │                              a contract - see §9.9)
│   │
│   ├── Platform/                  Native/ (P/Invoke by DLL), Clipboard, ShellReveal,
│   │                              FileLaunch, Elevation, KnownFolders, VolumeInfo,
│   │                              TaskScheduler
│   │
│   ├── Storage/                   Database (PRAGMA, user_version, integrity_check),
│   │                              Schema.sql (embedded, all of §11), ScanCatalog
│   │                              (file-vs-row reconciliation, id allocation),
│   │                              ScanRecords, ScanRepository, AuditRepository,
│   │                              DeleteRepository (ops, items, dry runs)
│   │
│   └── Config/                    AppPaths, AppConfig (+ DACL check when elevated),
│                                  PathGlob (one syntax, ** and %VARS%), DefaultRules
│                                  (the table of §7.2 as data), ConfigFile (the edits
│                                  K and --keep make, through JsonNode)
└── tests/PathMemo.Tests/
```

**Why not six projects.** For a tool written by one person that will never be a library, `Cli / Core / Data / Platform / Reporting` is friction without benefit: slower builds, `InternalsVisibleTo` ceremony, DI for the sake of DI, and no way to just call a function in the next folder. Folders and code review hold the boundaries. Split when there is a second consumer.

### 17.2. Interfaces only where there are 2+ implementations

```csharp
// JUSTIFIED: real polymorphism
interface IScanner        { ... }   // MftScanner, WalkScanner, UsnIncrementalScanner
interface IAuditProbe     { ... }   // ~20 probes
interface IDeleteBackend  { ... }   // recycle, quarantine, permanent, no-op (tests)

// REJECTED: one implementation, so the interface is pure overhead
// IClipboardService, IShellService, IReportWriter, IScanRepository
// -> static classes / concrete types, covered by integration tests.
```

A scanner knows nothing about persistence, so the two are separate and explicit: `IScanner.ScanAsync(request, progress, ct)` returns a `ScanResult`, and `ScanStore.Save(result)` writes the `.pmsnap` and the database rows, returning the id.

### 17.3. Allocation rules on the hot path

RSS under 250 MB at a million files is reachable **only** if:

- **No class per file.** No `List<FileEntry>` of reference types, no full-path `string` per entry. Struct-of-arrays only (§5.3), with names in one shared `byte[]` blob and an offset per node.
- IO buffers come from `ArrayPool<byte>.Shared`, always returned in a `finally`.
- Directory enumeration stays on `ReadOnlySpan<char>` without materialising.
- Progress counters are `Interlocked`/`volatile` fields, not an event per file.
- Arrays over 85 KB go to the LOH, so they are allocated **once** for the expected node count (estimated from `FSCTL_GET_NTFS_VOLUME_DATA`) rather than grown by doubling. When the estimate is short, chunked arrays of 1M elements — never `Array.Resize`.
- `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`, `TieredPGO=true`: for a short-lived CLI, workstation GC gives the lowest RSS.

---

## 18. Technology stack

| Component | Choice | Why |
|---|---|---|
| Runtime | **.NET 9**, pinned by `global.json` | `FileSystemEnumerator`, `System.IO.Hashing`, `NonBacktracking` regex, the best single-file story. The pin stops a machine with a newer SDK compiling the code under a different language version |
| CLI parser | ~~System.CommandLine~~ → **own parsing** | **Not needed.** Ten commands with flat options are a 40-line `switch` plus `ArgParse`; the package would generate help that is written by hand here and more accurate, and would remain the main NativeAOT blocker (§19.4) |
| Static output | ~~Spectre.Console~~ → **own** | **Not needed.** Our tables are three or four row formats with fixed columns, static output uses no colour at all, and progress is one rewritten line |
| TUI | **own renderer** (§18.1, §14.7) | `ReadConsoleInputW` was not needed either: the BCL parses both VT sequences and virtual-key records |
| SQLite | **Microsoft.Data.Sqlite**, hand-written mapping | Dapper is reflection, hostile to trimming and AOT. **The application's only PackageReference**; the trimmed single file grew from 19.5 to 22.5 MB |
| Migrations | **`PRAGMA user_version` + embedded .sql** | DbUp is overkill (25 lines of own code) and breaks trimming |
| Hashing | **XxHash128** plus BCL `SHA256` | zero native dependencies (§8.2) |
| Snapshot compression | **`DeflateStream`** | in the box; Zstd would be 30% smaller and a native DLL |
| Logging | **own `FileLogger`** (~80 lines) | Serilog pulls four packages and reflection for logs we send nowhere |
| Clipboard | **P/Invoke + OSC 52** | no WinForms (§15.1) |
| Recycle Bin | **`IFileOperation`**, hand-written COM interop | without `Microsoft.WindowsAPICodePack` |
| Scheduler | **`ITaskService` COM** or `schtasks` | |
| Tests | **xUnit** plus integration tests on a real filesystem | §22 |
| Publishing | **self-contained, single-file, trimmed** | §19 |

Considered and dropped: Terminal.Gui, Dapper, DbUp, Blake3.NET, System.Windows.Forms, FluentAssertions.

### 18.1. Why not Terminal.Gui

v1 is legacy and v2 spent a long time in prerelease with a moving API. `TableView` is limited, and large lists need manual virtualisation anyway. Its heavy reflection **costs trimming, and with it the difference between 20 MB and 70**; NativeAOT becomes unreachable. Its own focus and event model fights a single-screen application that needs full control over how the tree is drawn.

This project needs **one** complex screen (a virtualised tree), four simple ones and some modals — 600–900 lines of own renderer with full control, no dependency, and correct CJK and emoji widths, which Terminal.Gui does not have either. `Tui/Terminal/` is designed so the render backend can be swapped if that judgement turns out wrong.

---

## 19. Packaging and build

### 19.1. Artefacts

| File | Size | Note |
|---|---|---|
| `pathmemo-win-x64.exe` | **16–30 MB** | measured: 16.1 MB on the P0 skeleton (77.3 MB untrimmed), 19.5 MB at P3, 22.5 MB at P4 (Sqlite with native `e_sqlite3` added 3 MB), **23.0 MB at P5** — the whole TUI fit in 0.3 MB because it has no dependencies |
| `pathmemo-win-arm64.exe` | 16–30 MB | separate binary |
| `pathmemo-win-x64.zip` | **10.6 MB** | exe + README + LICENSE, built by `build\publish.ps1 -Zip` |

**The icon** (`Resources\app.ico`) is drawn by `build\make-icon.ps1` rather than committed as an unreadable binary: the shape is twelve lines of code, and regenerating it is cheaper than explaining what is inside a blob. Sizes 16–64 are stored as classic DIBs and 128/256 as PNG: the Windows shell understands PNG inside an ICO but GDI+ does **not**, while everything reads the small sizes.

"One exe, x64 and arm64" is a contradiction — that is **two** files. The honest phrasing: one file per architecture, no installer, no dependencies.

### 19.2. Publish command

```bash
dotnet publish src/PathMemo/PathMemo.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false \
  -p:PublishReadyToRun=true -p:DebugType=embedded \
  -o dist/win-x64
```

**`EnableCompressionInSingleFile=false` is deliberate.** Compression saves 40% of the size and adds 200–400 ms to **every** launch, and pathmemo is invoked from a console dozens of times a day. 28 MB without compression beats 17 MB with a delay.

`e_sqlite3.dll` is the only native dependency, which makes the host self-extract into `%TEMP%` once per version. *Measured in P4:* 1.72 MB extracted in **15 ms, once** — `pathmemo --version` runs in 77 ms on the first launch after clearing the cache and 62 ms afterwards.

### 19.3. Publishing: Releases, not Packages

**GitHub Packages is the wrong mechanism.** It hosts package registries — npm, NuGet, Maven, containers — with no place for a plain `.exe`, and its NuGet feed demands a personal access token even for public packages. Asking someone to create a token before they can download a disk cleanup tool is exactly what should not happen.

**GitHub Releases** is the mechanism for standalone binaries: no account and no token to download, a download counter, and the release URL is what winget, Scoop and Chocolatey point at later.

`.github/workflows/release.yml`, on a `v*` tag:

1. `dotnet test -c Release` — a release that fails its own tests should not exist;
2. publishes `win-x64` and `win-arm64` (the arm64 cross-build from an x64 host is verified: 24.6 MB, 30 s);
3. stamps the version from the tag (`v0.2.0` → `pathmemo --version` = `0.2.0`), so the title bar and the release cannot disagree;
4. writes `SHA256SUMS.txt` beside them — a self-contained exe from an unknown author should be verifiable;
5. creates the release with `gh release create` (preinstalled on the runner), keeping third-party actions out of the supply chain.

```bash
git tag v0.2.0
git push origin v0.2.0
```

`workflow_dispatch` builds the same archives without creating a release, for testing the pipeline.

**There is no code signature.** An unsigned exe downloaded from the internet carries a mark of the web, and SmartScreen shows "Windows protected your PC" with *More info → Run anyway*. The release notes say so plainly. An OV/EV certificate costs money and a reputation built from downloads; until then, the checksum is the answer.

### 19.4. NativeAOT — a Phase 3 goal

With Dapper, DbUp, Terminal.Gui and Blake3 out of the picture, the AOT road is open: `PublishAot=true` should give **~12 MB and a 15 ms start** instead of 120. *At P5* the `System.CommandLine` blocker is gone — it was never added — and the TUI is dependency-free and ports as is. What remains is `Microsoft.Data.Sqlite` (AOT-compatible via `SQLitePCLRaw`, needs verifying). *At P6* the COM interop landed without closing that road: `IFileOperation` is declared with `GeneratedComInterface` and the sink with `GeneratedComClass`, so the marshalling is source-generated rather than reflected.

---
## 20. Performance budgets

Measured on: Ryzen 7, 32 GB, NVMe, Windows 11, 1.2M files / 420 GB on C:, Defender on.

| Operation | Budget | Measured |
|---|---|---|
| Cold start to first frame | < 250 ms | **62 ms** (`--version`), 77 ms when `e_sqlite3` is extracted, once per version |
| MFT scan of C:, 1.2M records | < 10 s | **9.1–9.6 s** — 1.79M `$MFT` records, 1.27M files / 399k dirs / 209 GB, 175–184k rec/s. The 5 s target has headroom in parallel chunk parsing |
| Walk scan of C:, 1.2M files | < 150 s | **40–51 s** with hard-link dedup for files ≥ 1 MB (16k handles ≈ 3 s); 41 s without it at P3. The spread is filesystem cache state. Adding a 932 GB HDD costs fifteen minutes more, so every figure here is C: only |
| Incremental USN scan | < 2 s | typically 0.3 s (P9) |
| Writing a snapshot | < 1.5 s | **26 MB for 1.58M nodes** |
| Opening an existing snapshot | < 300 ms | **~250 ms**, full decompression; lazy sections are not in yet |
| Tree navigation, one frame | < 16 ms | **0.08 ms** in an ordinary directory, **0.6 ms** scrolling a 26k-entry one where every row changes; 4–7 ms for the first frame after a load |
| Entering a directory with 200k children | < 50 ms | **10–15 ms** on the widest real directory (`WinSxS\Manifests`, 26,205 entries); a synthetic 200k one stays in budget. Children are contiguous, so only the sort is paid for |
| Searching names across a snapshot | — | **56–69 ms** over 1.58M nodes, 402 matches for `node_modules` |
| Overview / `status` data | — | **48–70 ms**: volumes, last scan, a used-bytes series over 40 scans |
| Space Audit, all probes | < 8 s | **4.1 s** unelevated (21 probes, 175k files in temp); elevated adds DISM, 3–6 s |
| Diff of two snapshots | < 500 ms | **105 ms** for the merge join on 1.58M and 1.67M node snapshots, 5 ms where one directory changed, plus 405 ms to load both |
| Importing snapshots into a fresh database | — | **1.6 s** for 4 snapshots (106 MB, 6.5M nodes) |
| Reclaim rules over a snapshot | < 3 s | **2.5 s** end to end on 1.22M nodes: the process, loading the snapshot, matching 31 rules, and opening a handle per match to ask the guard about all 1,800 of them. The TUI runs the matching alone, on a background thread, so no frame waits for it (§7.5) |
| Category and extension aggregates | < 500 ms | **330 ms** over 1.58M nodes. The first version took 600 ms and +80 MB of peak: a linear scan of 200 extensions per file and a string per name. Now a hash lookup over a span and stack buffers (§17.3) |
| RSS during an MFT scan | < 250 MB | an elevated peak measurement is still outstanding |
| RSS during a walk scan | < 400 MB | **373 MB** peak working set (380 MB at P3, 478 MB with the first aggregates, 535 MB before file ids moved out of `RawEntry`). The live snapshot is 94 MB of that; the rest is transient GC heap |
| RSS in the TUI with a snapshot open | < 150 MB | **not met: 182–201 MB** with a 1.58M-node snapshot open. The tree is 94 MB, and decompression doubles it: `SnapshotFile.Read` materialises each section into a whole `byte[]` before copying. Fixed by lazy section loading in P10. The early 103 MB estimate used a snapshot half the size and ignored the transient |
| Data directory size | **< 500 MB always** | hard limit |
| Duplicate search over 200 GB | IO-bound | about the cost of reading 200 GB |

---

## 21. Acceptance criteria

**Correct numbers**
- [x] Scan + audit findings + metadata + unaccounted = the volume's used bytes, unaccounted < 2% — **MFT: 209 GB of 212, unaccounted 1.4%**; unelevated walk: 7.5%.
- [ ] `C:\Windows` and `C:\` match WizTree within 1% — not compared yet; MFT gives 42.3 GB (WinSxS 6.94) against the walk's 43.4 GB (WinSxS 8.47).
- [ ] A sparse `ext4.vhdx` shows allocated, not logical, with a `sparse` badge.
- [ ] A cloud-only folder shows ~0 with a `cloud` badge, its logical size in details.
- [ ] Switching `unique`/`allocated`/`logical` changes the numbers predictably.

**Scanning**
- [x] An MFT scan of 1M records finishes in < 10 s — 1.79M in 9.6 s.
- [x] Unelevated, a relaunch is offered and declining scans degraded with a warning — `[R]/[C]/[Q]` only with a TTY; scripts get stderr.
- [x] exFAT, FAT32 and network drives fall back to the walk scanner, per volume, trees spliced.
- [x] Unreadable paths land in `scan_errors`, grouped; in MFT mode their size is known — MFT sees 53k more files and 38k more directories than an unelevated walk.
- [ ] A rescan uses USN and finishes in < 2 s.
- [ ] `Ctrl+C` saves a partial result as `cancelled`; a second exits immediately.
- [ ] A junction causes no recursion; the point shows with `reparse` and size 0.
- [ ] The data directory is visible with a `self` badge.

**Space Audit**
- [ ] VSS, WinSxS, hiberfil, Recycle Bin, WSL and Docker vhdx, Windows Update cache, dumps and Windows.old are all found and sized.
- [ ] Every finding shows a remedy marked `[admin]` / `[reboot]`, copyable.
- [ ] No probe changes the system; a failed `DISM` parse yields `unknown`, never `0 bytes`.

**Deletion**
- [x] No protected path is deleted, including `c:\windows`, `C:\WINDOW~1`, `\\?\C:\Windows`, `\\localhost\c$\Windows`, `C:\Users\All Users\...`, `C:\Users\..\Windows` — twelve spellings, each opened and refused on the real machine.
- [x] Swapping a subdirectory for a junction mid-delete does not escape the tree — 10,000 rounds, 19,478 junctions actually swapped in, canary intact (§22.3).
- [x] A file changed between scan and deletion aborts the operation — checked against the snapshot when the plan is built, and against the handle again when it runs.
- [x] Quarantine is a rename, not a copy, and onto another volume it is refused clearly — `ERROR_NOT_SAME_DEVICE` becomes that sentence. The 18 GB timing is not measured yet; a rename does not depend on the size.
- [x] `restore` returns the tree to its original paths and refuses when something else has taken the name; `purge` frees the space, measured against the volume rather than assumed.
- [x] A file over the bin quota never enters `recycle` mode and is never destroyed silently — the quota is read before the mode is chosen, and the mode falls back to quarantine with the reason on screen.
- [x] `--dry-run` moves nothing and writes to `dryrun_log`, not `delete_ops`.
- [x] Permanent deletion over the threshold needs a typed confirmation `--yes` cannot bypass; a script uses the token from a dry run of that exact list.
- [x] Every operation has a journal entry with per-item results — and no journal means no deletion.

**Reclaim**
- [x] Every rule of §7.2 is data with two axes and a remedy, and a custom rule can express everything a built-in one does — asserted over the whole built-in set.
- [x] No built-in rule claims `hiberfil.sys`, `pagefile.sys`, `WinSxS`, `C:\Windows\Installer`, `System Volume Information` or `.git\objects` — the "deliberately not rules" list, asserted as a test.
- [x] A rule that needs another tool is reported and never deleted, whatever the flags say — `git gc` and `docker system prune` survive `--apply --risk danger --force`.
- [x] A match inside another match is counted once; the outer one wins, across rules as well as within one.
- [x] The keep list removes a path from the recommendations entirely, and `K` in the TUI writes it into `config.json` without disturbing the rest of the file.
- [x] A recommendation the guard would refuse never reaches the total — every match is opened and judged while the report is built.
- [x] `--apply` goes through `rm`: the guard, the confirmation, the journal and the free-space measurement are the ones from §9 — verified end to end, 7.23 MB predicted and 7.23 MB freed.
- [x] Risk `danger` needs `--force` plus a typed confirmation, wherever the path came from; exit code 6 without it.
- [ ] The estimate is within 10% of what a real cleanup frees on a machine with a pnpm store and a Unity project — the hard-link case is deliberately conservative (§7.5) and has not been measured against a real one.

**Duplicates** (all P8)
- [ ] A hard-link set is shown apart from duplicates, with a saving of 0.
- [ ] Cloud-only files are never hashed (verified by the absence of network traffic).
- [ ] The last file in a group cannot be unmarked, in the UI or the CLI.
- [ ] Byte-for-byte verification runs before deletion; a swapped file aborts it.
- [ ] Duplicates are found across C: and D:.

**CLI**
- [x] Redirected stdout disables the TUI and prints `status`.
- [x] `--json` emits valid JSON and nothing else, warnings on stderr — tests on the §13.6 contract.
- [x] Exit codes follow §13.5 — `EX_UNSAFE` is what a guard refusal and a stale token return.
- [x] `top --paths-only | rm --from-stdin --dry-run` works as a pipe.
- [ ] `doctor` reports elevation, filesystems, USN state, integrity, version, free space — everything except USN state (P9).

**TUI**
- [x] Works in Windows Terminal, conhost, ConEmu and the VS Code terminal — verified in conhost from Explorer and `cmd.exe`; a host without VT falls back to the line menu, a raster font to ASCII glyphs.
- [x] A double-click gives an icon, the title `pathmemo <version>`, 110×32 and quick-edit off; clicking inside does not freeze rendering.
- [x] `Ctrl+C` is a standard cancel — the TUI exits, the scrollback is intact, the next command runs.
- [x] Every action is a single key; the only Ctrl combinations are `Ctrl+D`/`Ctrl+U`, which have PageDown/PageUp aliases.
- [x] 80×24 is fully usable; anything smaller gets a message, not broken output; resizing redraws correctly.
- [x] `U+202E` does not reverse the text, and CJK or emoji names do not break alignment — asserted by a test over every frame row.
- [x] A 200k-child directory opens in < 50 ms and scrolls smoothly.
- [x] Logs never reach stdout while the TUI is up.
- [x] `o` refuses `.exe`, `.lnk` and `.hta` — before the dialog, tested on `invoice.pdf.exe`.

**Hygiene**
- [x] Two instances: the second reads and refuses to write — verified against a database held by `BEGIN EXCLUSIVE`; in WAL mode reading is never blocked, and a write refuses with one warning while **still saving the snapshot**. Losing a 10-second scan over a busy history row would be worse.
- [x] An unclean exit triggers `integrity_check` on the next start, a clean one does not — unit-tested both ways.
- [x] A corrupted `.pmsnap` does not break the application: `history` prints `deleted`, `diff` refuses with an explanation.
- [ ] The data directory never exceeds 500 MB (retention is implemented; the 100-scan run is not done).
- [x] A corrupted `config.json` gives a warning and defaults, not a crash — and one bad value warns about that key alone.
- [x] An elevated run ignores config paths — `--data-dir` always, and the whole file when its permissions let anyone else write it (§12.1).

---

## 22. Testing

### 22.1. What is unit-tested

Pure logic, no filesystem: `RuleEngine` (glob matching, variable expansion, `keep` precedence); `PathGuard.IsProtected` over **pre-canonicalised** strings, all twelve bypasses; `NodeStore` / `SnapshotFormat` round trips, boundaries and corrupted data; `DiffEngine` and `HardlinkResolver` over synthetic data; `DISM` and `vssadmin` parsers over fixed samples including non-English locales; size formatting, path truncation, CJK width, bidi sanitisation.

*P4:* `SnapshotDiff` — 13 tests over synthetic trees (`TestTree` builds a `NodeStore` from a list of paths): the explaining level, splitting across children, residual rows, the threshold, appeared/disappeared, a name changing type, hard-link aliases, and rows plus remainder equalling the volume's change. `ScanAggregates` — category inherited from the directory, the extension cut-off. `ScanExport` — the JSON contract and CSV quoting. `Storage` runs against a real SQLite file in `%TEMP%` rather than in-memory: WAL, the `.clean` marker and file-versus-row reconciliation exist only on disk, and they are the subject. The suite runs serially because the data directory is one static path per process.

*P6:* the protected set over canonical strings — the sealed-versus-anchor rule, the whitelist inside the blacklist and its anchor, the keep list, `$Recycle.Bin` and `System Volume Information`, the store and the one operation allowed into it. `PathGlob` per segment, `**`, name-only patterns and variable expansion. The confirmation token: same list in another order gives the same token, one byte or one mode different gives another. `AppConfig` — a good file, a broken one, and a file with one nonsense value. The filesystem half is in §22.2.

*P7:* the rules over synthetic trees — 26 tests: a rule matching anywhere, a match inside another match (within a rule and across two), the sibling and child conditions that separate a Unity project from any other `Library` and a virtual environment from a folder of videos, the age and size floors, a veto pattern, a hard-linked file counted as shared, an alias adding a name and no bytes, the keep list, a reparse point never matched, the store never recommended, an extension pattern not claiming a directory, a wildcard inside a segment, which rule wins when two claim a node, the risk ceiling, `RiskOf`, disabled and replaced rules, a custom rule out of `config.json` and a broken one skipped, and two properties of the whole built-in set: every `Command` rule names its command, and none of them claims anything on the "deliberately not rules" list. The reclaim screen — 9 tests that press keys and read the frame, including that the first frame is drawn before the background pass has finished. The filesystem half is in §22.2.

*P5:* the terminal layer — 15 tests: CJK, emoji and combining-mark widths, `Fit` landing on exactly N columns, sanitisation, frame row diffing (an identical frame writes nothing, a changed row writes only itself), the erase-before-write order, colour suppression, sparkline scaling. The Tree screen — 14 tests that press keys and read the frame: row order, descending and returning to the same row, the size mode on a hard-link alias, the filter, search jumping into the match's directory, marks, refusing `.exe`, details, alignment under `U+202E` and CJK, a 200k-entry directory drawing exactly one page, `g`/`G`, narrowing to 80 columns. Frames are asserted as uncoloured text: assertions about escape sequences would test the colour scheme, not the behaviour.

### 22.2. What is integration-tested against a real filesystem

**An `IFileSystem` abstraction is deliberately not introduced.** All the value is in Win32 edge cases — reparse points, hard links, sparse files, long paths, ACLs, sharing violations, POSIX delete — which a fake filesystem **cannot** reproduce. A fake would produce green tests and a red production build.

The harness builds a real tree in `%TEMP%`:

```
CreateJunction, CreateSymbolicLink, CreateHardLink
FSCTL_SET_SPARSE + FSCTL_SET_ZERO_DATA        -> a sparse file
a 400-character path through \\?\
a file opened with FileShare.None              -> sharing violation
a directory with a DENY ACE                    -> access denied
names with U+202E, CJK, emoji and control characters
a recursive junction loop, a zero-byte file
two links to one file in different directories
```

It checks traversal, sizes, dedup, errors, and above all **that deletion never leaves the tree**.

*P6:* the twelve spellings of a protected path, each opened against the real machine, plus the property they rest on — the aliases of one directory canonicalise to one name. A tree deleted around a junction, with the junction's target untouched afterwards. A read-only file. A file another process holds open, unlinked while that handle still reads its data. Quarantine, restore, a restore refused because something took the name back, and purge. A dry run that moves nothing and lands in the right table. The scan-verification refusal, and its opposite. A file whose size is not a whole number of clusters — a regression that once rejected most files. One real item into the Recycle Bin through `IFileOperation`, because the apartment, the sink and the copy engine's own success codes cannot be faked.

*P7:* the three things a synthetic tree cannot show. That the report never promises what the guard refuses: one rule pointed at a name the real system directory also has, with one match it may offer, one the guard refuses and one that has gone since the scan. That a contents-only rule hands over the children and not the directory. That `config.json` survives being edited by `K` and `--keep`: an unknown section is still there afterwards, a second `K` on the same path is not an error, and a rule disabled and enabled again leaves the file as it was.

### 22.3. Race test for T2

Thread A deletes a tree recursively while thread B repeatedly swaps a subdirectory for a junction pointing at a guarded canary. After 10k iterations the canary must be intact. Without this test `HandleTreeDeleter` cannot be considered done.

*P6:* implemented and run. Junctions are created through `FSCTL_SET_REPARSE_POINT` rather than `mklink`, or the race would be a contest between the deleter and the process loader. The full run: **10,000 rounds, 19,478 junctions actually swapped in, canary intact, 114 s**. The suite runs 600 rounds under a 20-second budget; `PATHMEMO_RACE_ITERATIONS` asks for the long version. The test also asserts that at least one swap landed — a round where the attacker never won the race would pass while testing nothing.

### 22.4. Comparison against a reference

A script compares a WizTree CSV export over the 50 largest directories, tolerance 1%. Run by hand before a release on two or three real machines — synthetic data is useless here.

### 22.5. What is not tested automatically

Actually invoking `vssadmin delete shadows`, `DISM /StartComponentCleanup` or `powercfg /h off` — by hand, on a VM with a snapshot. Such tests have no place in CI.

---

## 23. Glossary

| Term | Meaning |
|---|---|
| **MFT** | Master File Table — the metadata of every file on an NTFS volume, in one file. Reading it directly lists the disk two orders of magnitude faster than walking it. |
| **USN Journal** | The NTFS change journal: what changed since last time, without a walk. |
| **Allocated size** | What is really occupied on the volume (clusters, compression, sparse holes). The only quantity that adds up to "used". |
| **Logical size** | The length of the data stream — what Explorer shows in a file's properties. |
| **Unique allocated** | Allocated, with hard links deduplicated. |
| **Reclaimable** | How many bytes deleting a given set actually frees. |
| **Hard link** | Several names for one physical file. `WinSxS` is almost entirely made of them. |
| **Reparse point** | Junction, symlink, mount point, cloud placeholder. Needs care when walking and when deleting. |
| **Sparse file** | A file with holes: logical > allocated. Typical for `.vhdx`. |
| **Cloud placeholder** | A OneDrive or Dropbox file whose data is in the cloud. Reading it downloads it. |
| **VSS / shadow copy** | A shadow copy of the volume; restore points. Invisible to a filesystem walk. |
| **WinSxS** | The Windows component store. Cleaned only through DISM. |
| **Snapshot (`.pmsnap`)** | A binary image of one scan's file tree. |
| **Quarantine** | Staging for deleted items on the same volume. Frees space only after a purge. |
| **Purge** | Physically deleting the quarantine — the moment space is actually freed. |
| **Risk** | What breaks if this is deleted: Safe / Caution / Danger. |
| **Recoverability** | What getting it back costs: Instant / Redownload / Rebuild / Irreversible. |
| **Finding** | The result of one Space Audit probe. |
| **Remedy** | A way to act on a finding: a command, deleting paths, a system setting. |
| **Degraded scan** | A scan without administrator rights, or on non-NTFS: slower and incomplete. |
| **Unaccounted** | The gap between the volume's used space and everything pathmemo could explain. |

---

## Licence

MIT. See [LICENSE](LICENSE).
