# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

React + Vite + TypeScript, built to static files and served from Cloudflare Workers static assets (free plan). Code lives in `site/` inside the pathmemo repo, so the version and figures on the page move with the code. Chosen by the owner, 2026-10-01.

## Users

Windows users whose disk is running out and who want to know why before they delete anything. The core of them are developers: their space goes to package caches, build output, toolchains, Docker and WSL disks, and generic cleaners either miss those or delete them blindly. Comfortable with a terminal, suspicious of "optimiser" software, they want numbers they can check.

## Product Purpose

pathmemo is a disk space screener for Windows: one `.exe`, no installer. It answers two questions: where did the space go, including what a filesystem walk cannot see, and what can be freed safely, with an honest estimate of how many bytes come back. Double-click it for a window, type it for a command line; the same file does both.

The site exists to make a developer who lands from GitHub, winget or a link understand in seconds what is different, trust it enough to run an unsigned exe, and download it.

## Positioning

What WizTree, TreeSize and ncdu do not:

- History and diff: "what grew by 18 GB since last week", from saved snapshots.
- Space Audit: restore points (VSS), WinSxS, WSL and Docker virtual disks, Windows Update cache, hibernation file, Recycle Bin. Space a walk physically cannot see.
- Knows developer ecosystems: npm, pnpm, bun, nuget, gradle, maven, pip, cargo, rustup, Solana tools, go, Unity, Unreal, Next.js, Playwright, Docker, WSL. Each with its own correct cleanup command rather than a path handed to delete.
- Honest numbers: the scan total reconciles with the volume's used space and the gap is explained. Freed space is measured before and after, not assumed.
- One exe that is equally good in a script and in a terminal.

## Operating Context

- Windows 10 1809+ and Windows 11, x64 and arm64 builds.
- Installed by unpacking a zip from GitHub Releases, or `winget install egorb2302.pathmemo` once the winget-pkgs PR is merged (submitted 2026-10-01, not merged yet).
- Not code-signed: SmartScreen warns about an unknown publisher (More info, Run anyway), and SHA-256 sums are published with every release.
- Three faces: an own Win32 window with a treemap (double-click), terminal screens (`--tui`), and a scriptable CLI with JSON output.
- Administrator rights optional: elevated, the scanner reads the MFT directly (about 40x faster) and sees paths a walk is denied.

## Capabilities and Constraints

- Commands: `scan`, `top`, `audit`, `reclaim`, `diff`, `dupes`, `rm` (quarantine by default), `restore`, `purge`, `ops`, `doctor`.
- Reclaim: 38 rules as data, two independent axes (risk and recovery), the vendor's own cleanup command where one exists.
- Deletion: quarantine, operations journal, dry run, path guard against junction tricks. Nothing deleted without an explicit user action, no auto-clean.
- The window reports and does not delete yet: freeing space is still `reclaim --apply` and the terminal screens.
- Not a web app, not a "speed-up" tool, no registry cleaning, no cloud, no Linux or macOS.
- Open source, MIT, free. Repo: github.com/egorb2302/pathmemo. Current version 0.2.4.

## Brand Commitments

- Name is lowercase: pathmemo.
- English throughout, as in the product.
- Existing icon `src/PathMemo/Resources/app.ico`: a dark rounded square with a ring showing a nearly full disk (87%), two foreground colours, used space in cyan. Drawn by `build/make-icon.ps1`.
- Voice of the README: precise, plain, measured claims with the number and the condition it was measured under. No hype.

## Evidence on Hand

Real measurements from the README (machine, conditions in the README):

- MFT scan: 1.27M files in 9.6 s, 1.4% unaccounted. Walk scan: 1.22M files in 41 s.
- Space Audit: 21 probes in 4.1 s unelevated.
- diff of two 1.6M-node snapshots in 105 ms.
- TUI frame 0.1 to 0.6 ms, search across 1.58M nodes in 56 ms.
- Reclaim: 1.2M nodes matched in 2.5 s.
- Duplicates stage 0: 1.23M files to 8,508 candidates in 0.44 s.
- Path guard: canary intact after 10k junction-swap races.
- 408 tests.
- Real scan of the owner's C: (scan 12, started 2026-09-21 23:17 UTC, 22 Sep local; 191 GB in 1,210,960 files, walk, unelevated), all 36 rules of 0.2.3: 37.6 GB safe to free, 49 GB including caution, 1,777 matches across 21 rules (`pathmemo reclaim --scan 12 --risk caution`, run 2026-10-01). The five rules new in 0.2.3 alone account for 6.7 GB safe (Solana tools 3.14 GB, bun cache 2.81 GB, Playwright browsers 0.71 GB) plus 3.77 GB of Rust toolchains reported only. Re-run with the 38 rules of 0.2.4 (2026-10-02): same totals and the same 21 matching rules; the Cargo registry (375 MB) and VS Code's VSIX cache (862 MB) now show under rules of their own.
- Space Audit on the same machine, 2026-10-01, unelevated: 41.7 GB across page file, Windows Update cache, temp, hibernation and browser caches.
- Every figure on the site comes from these runs or the README; nothing is mock. Names on the map are anonymised: the Windows user folder is shown as `you`, and non-developer apps are merged into "other apps".
- The real window, terminal screens and CLI output can be captured from the shipped exe.

Absent and never to be invented: users, download counts, stars, testimonials, press, benchmarks against other tools, a signed binary, a merged winget package before it is merged.

## Product Principles

1. Honest numbers beat impressive ones: every figure on the site carries the condition it was measured under.
2. The safe way to clean beats deleting files: the product points at the vendor's command.
3. Nothing happens without the user: no auto-clean, no one-click optimisation, and the site never implies otherwise.
4. Developer-native: the terminal is a first-class face, not an afterthought.
5. Small and local: one exe, no installer, no account, no telemetry.
