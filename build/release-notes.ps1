#Requires -Version 5.1
<#
  Builds the GitHub Release notes for one version (README section 19.3).

  The description of a release lives in CHANGELOG.md and nowhere else. Notes written
  inline in the workflow drift from the changelog the moment either is edited, and the
  drift is only visible after the tag is pushed - when it is already on the download
  page. So the workflow calls this, and this reads the changelog.

  A missing section is a hard failure by design: a release whose notes are empty is
  worse than a workflow that goes red, and the built zips are uploaded as workflow
  artifacts by then either way, so nothing has to be rebuilt to recover.

  Deliberately ASCII in its own strings. The arrows and dashes belong to the changelog,
  which is read as UTF-8 explicitly; keeping this file plain means it behaves the same
  under Windows PowerShell 5.1 and pwsh, whatever the console code page is.
#>
[CmdletBinding()]
param(
    # The release being described, without the leading "v".
    [Parameter(Mandatory = $true)]
    [string] $Version,

    # SHA256SUMS.txt, as the release workflow produces it. Omitted for a dry run.
    [string] $Checksums = '',

    # "https://github.com/<owner>/<repo>". Given it, the relative links of the changelog
    # are rewritten to absolute ones at this version's tag: a release page is served from
    # /releases/, so "README.md" there is a 404, and pinning to the tag means the document
    # a reader lands on is the one this build was cut from.
    [string] $RepoUrl = '',

    # Where to write the notes. Empty writes them to stdout instead, which is how to
    # look at them before pushing a tag.
    [string] $OutFile = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$changelog = Join-Path $repo 'CHANGELOG.md'

if (-not (Test-Path -LiteralPath $changelog)) { throw "no changelog at $changelog" }

# -Encoding UTF8 rather than the default: the changelog has em-dashes and a multiplication
# sign in it, and the default is the ANSI code page on PowerShell 5.1.
$lines = Get-Content -LiteralPath $changelog -Encoding UTF8

# The heading of the section, matched on the version alone so the date can be anything:
# "## [0.2.0] - 2026-09-19" and "## [0.2.0]" both count.
$escaped = [regex]::Escape($Version)
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^##\s*\[$escaped\]") { $start = $i; break }
}

if ($start -lt 0) {
    throw "CHANGELOG.md has no section for $Version - add '## [$Version] - <date>' before tagging"
}

# Everything up to the next release heading. "##" exactly: the "###" subheadings inside a
# section are part of it.
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { $end = $i; break }
}

$section = ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
if (-not $section) { throw "the $Version section of CHANGELOG.md is empty" }

if ($RepoUrl) {
    # Only links that are relative and point at a document in the repository: anything
    # already absolute is left exactly as written.
    $section = [regex]::Replace(
        $section,
        '\]\((?!\w+:)([^)#]+\.md)(#[^)]*)?\)',
        { "](" + $RepoUrl.TrimEnd('/') + "/blob/v$Version/" + $args[0].Groups[1].Value + $args[0].Groups[2].Value + ")" })
}

# Single-quoted, so the backticks and the dollar are all literal: this is the markdown
# `$MFT`, and escaping it inside the here-string below would be three backticks against
# two and easy to get wrong in a way only the published page shows.
$mft = '`$MFT`'

# What someone who just landed on the download page needs, in the order they need it:
# what it runs on, which file to take, how to start it, and why Windows is about to
# warn them. The changelog then says what the thing actually does.
$notes = @"
**Windows 10 1809 or later, x64 or arm64.** One file, self-contained - no installer, and
no runtime to install alongside it.

1. Download ``pathmemo-$Version-win-x64.zip``, or ``-win-arm64`` on an ARM machine.
2. Unpack it anywhere.
3. Double-click ``pathmemo.exe`` for the window, or run it from a terminal:
   ``pathmemo scan``, ``pathmemo top --min 1GB``, ``pathmemo audit``.

Windows will warn that the publisher is unknown, because the executable is not
code-signed. Choose **More info -> Run anyway**, or check your download against the
SHA-256 below first.

Administrator rights are optional, and change what the scanner can see: with them it
reads $mft directly, which is roughly 40x faster and reaches paths a directory walk is
denied.

Nothing is deleted without an explicit confirmation, and what is deleted goes to a
quarantine that ``pathmemo restore`` reverses.

---

$section
"@

if ($Checksums) {
    if (-not (Test-Path -LiteralPath $Checksums)) { throw "no checksum file at $Checksums" }

    $sums = (Get-Content -LiteralPath $Checksums -Raw).Trim()
    $fence = '```'
    $notes = $notes + @"


---

### SHA-256

$fence
$sums
$fence
"@
}

if ($OutFile) {
    # UTF8Encoding with $false: no byte-order mark. Set-Content -Encoding UTF8 writes one
    # on PowerShell 5.1, and it would show up as a stray character at the top of the
    # release page.
    [IO.File]::WriteAllText($OutFile, $notes, (New-Object Text.UTF8Encoding $false))
    Write-Host "release notes for $Version -> $OutFile ($($notes.Length) characters)"
}
else {
    $notes
}
