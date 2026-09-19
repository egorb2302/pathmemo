# Changelog

What changed in each release, newest first. Dates are ISO-8601.

The heading of a version is what the release workflow copies into the GitHub
Release notes (`build/release-notes.ps1`), so this file is the one place a
release is described. A build reports the tag it was cut from as
`pathmemo --version`.

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
