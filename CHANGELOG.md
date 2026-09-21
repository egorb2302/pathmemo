# Changelog

What changed in each release, newest first. Dates are ISO-8601.

The heading of a version is what the release workflow copies into the GitHub
Release notes (`build/release-notes.ps1`), so this file is the one place a
release is described. A build reports the tag it was cut from as
`pathmemo --version`.

## [Unreleased]

The Reclaim tab, in the window.

### Added

- **Reclaim in the window.** The tab that said *not in the window yet* now shows the report:
  the rules that matched the last scan, largest first, each with what deleting it gives
  back, its share, both axes (`safe redownload`, `caution irreversible`), a note and the
  number of places. A double-click or Enter lists the paths behind a rule; the word
  *Reclaim* above the list leads back, and the cursor returns to the rule it left. The risk
  ceiling is three segments — safe, caution, danger — and `t` cycles it; `y` copies the
  paths under the cursor.
- **It reports and does not delete.** Deleting is still `pathmemo reclaim --apply` and the
  terminal's Reclaim screen, where the guard checks every path first, and the tab says so.
  The headline counts only what pathmemo can delete itself; what needs another tool
  (`git gc`) or a person (a disk image) is listed, dimmed, and named beside the total
  rather than added to it. It ends in the id of the scan the numbers came from, and over a
  scan that was stopped early the tab says so: what was not read is not listed, so every
  number is a floor.
- The rules run on a worker, so the tab opens at once and says *Applying 31 rules…* until
  they are done: from the key to the list is under a second on a 1.2M-file snapshot,
  loading the snapshot included.

### Fixed

- **After a scan, the Tree tab said "run a scan first".** A scan drops the snapshot the tab
  was showing, and nothing loaded the new one until the tab was left and re-entered. The
  Tree and Reclaim tabs now reload when a scan finishes under them.
- **The scan card's Stop button sat on top of the path being read.** The card was one line
  shorter than its contents; its height is now added up from them.

### Known limitations

Audit and duplicates are still the commands and the terminal screens, and the window has no
delete dialog, which is why its Reclaim tab only reports. The rest is unchanged from 0.2.2.

402 tests; ten of the new ones drive the Reclaim tab through the shell and two hold the
scan card's lines clear of its button.

## [0.2.2] — 2026-09-22

The walk scanner's numbers, corrected — and, by the same change, an unelevated scan
several times faster.

### Fixed

- **The walk scanner under-reported used space.** For every ordinary file it reported the
  logical size as the allocated one: `GetCompressedFileSize`, which it asked once per file,
  only knows a different number for compressed and sparse files and answers with the plain
  length for everything else. Over the 1.2M files of this machine's C: that was 3 GB missing
  from *scanned* and booked to *unaccounted*, which fell from 17.9 GB (8.7%) to 14.8 GB
  (7.2%) — the rest is what the accuracy limits say it is. The MFT scanner, which reads the
  run lists, was right all along; the test that compares the two had never run (below).
- **An unelevated scan is 3–4× faster.** The per-file syscall above is gone for ordinary
  files, which now round up to the cluster the way the audit already measured directories.
  A walk of the same C: — 1.21M files, 361k directories — takes 13.5 s where it took 41 s
  (the README's own figure) to 61 s (a cold run the same day) before. See [README](README.md)
  §4.4 and §22.5.

### Tests

- `Mft_scanner_agrees_with_the_walk_when_elevated` has run and passed, for the first time.
  Since the suite began declaring itself unelevated it had returned on its first line even
  as an administrator, because it asked the declaration rather than the token; it now asks
  Windows directly, and on a CI runner — an administrator — it runs too.
- Two new tests pin the walk's allocated sizes against real files, unelevated, on every run:
  ordinary files round to the cluster, a compressed file is asked.

### Known limitations of this release

Unchanged from 0.2.0: the window shows the volumes and the tree, and audit, reclaim and
duplicates are the commands and the terminal screens for now; the window is unverified on
arm64, above 100% scaling and across monitors of different scale; unelevated, some paths
are unreadable, hard-link deduplication covers only files of 1 MB and over, and alternate
data streams are not counted — elevation removes all three.

390 tests, passing both as an ordinary user and as an administrator.

## [0.2.1] — 2026-09-19

A fix for a crash that made the window unusable on exactly the machine it was most likely
to be opened on: one where nothing has been scanned yet.

### Fixed

- **The window closed instead of showing the tree.** With no scan on record, clicking
  **Tree** — or a volume card, which is the other way in — ended the process on the spot,
  with no message and no error. Two methods called each other until the stack ran out, two
  frames short of the line that says to run a scan. Every first run was affected, because
  having no snapshot is what a new install *is*. The window now says there is nothing to
  browse yet and stays open.
- **A snapshot that cannot be opened is now reported rather than thrown.** One that has
  been deleted by retention, or is held open by another process, between being listed and
  being read used to escape as an unhandled exception: a stack trace on the command line,
  and the same instant close in the window.

### Known limitations of this release

Unchanged from 0.2.0, and worth repeating on the page you are reading:

- The window shows the volumes and the tree. Audit, reclaim and duplicates are the commands
  and the terminal screens for now, and each tab in the window says so.
- The window has only been run on x64 at 96 dpi on a single monitor. The arm64 binary is
  built and tested by the same suite, but its window is unverified, as is scaling above
  100% and dragging between monitors of different scale.
- Unelevated, the walk scanner runs: some paths are unreadable, hard-link deduplication
  covers only files of 1 MB and over, and alternate data streams are not counted. Elevation
  removes all three.

388 tests, passing both as an ordinary user and as an administrator. Six of them are new and
cover the window's navigation, which had no tests at all before this release — see
[README](README.md) §24.6 for why that was, and what it cost.

## [0.2.0] — 2026-09-19

The first public release: one self-contained `pathmemo.exe`, no installer and no
runtime to install alongside it.

### The two faces

- **A window.** Double-click the executable and it opens a real window — volume
  cards with their used space, a browsable tree of the largest directories with
  a clickable breadcrumb, a treemap panel, and a scan that runs from the toolbar
  without freezing anything. The window is drawn directly on Win32 and GDI, so it
  costs 0.27 MB of the download rather than the 100 MB a UI framework would have.
- **A command line.** The same file, typed into a terminal, prints to that
  terminal and answers with exit codes: `scan`, `tree`, `top`, `history`, `diff`,
  `audit`, `reclaim`, `dupes`, `rm`, `restore`, `purge`, `ops`, `errors`,
  `export`, `config`, `status`, `doctor`. Every one of them takes `--format json`
  or `csv` where a table would be the answer.
- **Terminal screens.** `pathmemo --tui` gives the same browsing in any terminal,
  for a machine with no desktop — overview, tree, reclaim and duplicates.

### Finding the space

- Two scanners. A directory walk that needs no rights, and a scanner that reads
  `$MFT` directly when run as administrator — roughly 40× faster (1.27M files in
  9.6 s against 41 s), and it sees paths a walk is denied.
- An incremental rescan from the USN change journal, and scheduling to keep
  snapshots current. Both need administrator rights; without them every scan is a
  full one and `doctor` says so.
- Snapshots are stored under `%LOCALAPPDATA%\pathmemo` with a history, so `diff`
  can say what changed since last time — two 1.6M-node snapshots compared in
  105 ms.
- **Space Audit**: 21 probes for the space no file browser shows — shadow copies,
  the component store, hibernation, page and crash dump files, Windows Update
  leftovers, per-app caches.

### Freeing it

- **Reclaim**: 31 rules over what a scan found, each with its own risk and
  recovery note, so the recommendation says what is lost as well as what is
  gained.
- **Duplicates**: five stages, from a size grouping that cut 1.23M files to 8,508
  candidates in 0.44 s, to a content hash with a cache that makes the second run
  cheap.
- **Deletion, deliberately narrow.** Nothing is ever deleted without an explicit
  confirmation, deletions go to a quarantine that `restore` reverses, and every
  operation is journalled (`ops`). A path guard refuses system and boot paths
  before anything is touched — it held a canary intact through 19,478 junction
  swaps aimed at making the deleter follow a link out of its own tree.

### Known limitations of this release

- The window shows the volumes and the tree. Audit, reclaim and duplicates are
  the commands and the terminal screens for now, and each tab in the window says
  so rather than pretending otherwise.
- The window has only been run on x64 at 96 dpi and a single monitor. The arm64
  binary is built and tested by the same suite, but its window is unverified, as
  is scaling above 100% and dragging between monitors of different scale.
- Unelevated, the walk scanner runs: some paths are unreadable, hard-link
  deduplication covers only files of 1 MB and over, and alternate data streams
  are not counted. Elevation removes all three.

382 tests, passing both as an ordinary user and as an administrator, and the full list
of what is measured rather than claimed is in
[README](README.md) §21.
