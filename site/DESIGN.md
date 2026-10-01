---
name: pathmemo
description: A disk space screener for Windows, shown as a real treemap of one developer's C: where cyan marks what is safe to free
colors:
  ground: "#0e121a"
  deep: "#0a0d13"
  slate-1: "#121722"
  slate-2: "#1a2130"
  slate-3: "#232c3c"
  line: "#222b3b"
  line-strong: "#2b3547"
  slate-mid: "#3a465c"
  text: "#e8edf3"
  text-soft: "#b9c3d0"
  text-muted: "#8d99ab"
  safe-cyan: "#56d6d6"
  safe-cyan-hi: "#7fe4e4"
typography:
  display:
    fontFamily: "Archivo Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "clamp(48px, min(10.2cqw, 31cqh), 190px)"
    fontWeight: 800
    lineHeight: 0.82
    letterSpacing: "-0.012em"
    fontStretch: "62%"
  headline:
    fontFamily: "Archivo Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "clamp(44px, 6vw, 104px)"
    fontWeight: 800
    lineHeight: 0.88
    letterSpacing: "-0.01em"
    fontStretch: "66%"
  title:
    fontFamily: "Archivo Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "22px"
    fontWeight: 700
    lineHeight: 1.15
    fontStretch: "85%"
  figure:
    fontFamily: "Archivo Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "24px to 28px"
    fontWeight: 750
    lineHeight: 1
    fontStretch: "70%"
  body:
    fontFamily: "Archivo Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "17px"
    fontWeight: 400
    lineHeight: 1.55
  label:
    fontFamily: "Martian Mono Variable, Consolas, ui-monospace, monospace"
    fontSize: "10px"
    fontWeight: 400
    letterSpacing: "0.06em"
rounded:
  none: "0"
  hairline: "2px"
spacing:
  gutter: "clamp(16px, 2.6vw, 44px)"
  gap: "2px"
  wide: "1680px"
components:
  button-primary:
    backgroundColor: "{colors.safe-cyan}"
    textColor: "{colors.ground}"
    rounded: "{rounded.hairline}"
    padding: "0 20px"
    height: "46px"
  button-primary-hover:
    backgroundColor: "{colors.safe-cyan-hi}"
  button-primary-large:
    backgroundColor: "{colors.safe-cyan}"
    textColor: "{colors.ground}"
    rounded: "{rounded.hairline}"
    padding: "0 26px"
    height: "58px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.hairline}"
    height: "58px"
  command-chip:
    backgroundColor: "{colors.slate-1}"
    textColor: "{colors.text}"
    rounded: "{rounded.hairline}"
    padding: "8px 10px 8px 12px"
  tooltip:
    backgroundColor: "{colors.deep}"
    textColor: "{colors.text-soft}"
    padding: "14px 16px"
  text-cell:
    backgroundColor: "{colors.ground}"
    textColor: "{colors.text}"
---

# Design System: pathmemo

## Overview

**Creative North Star: "The Disk Itself"**

The page is the disk. A squarified treemap of one developer's real C: drive (scan 12, 191 GB) fills the first viewport edge to edge, and the product's reclaim pass is shown as light: cyan blocks are what is safe to free, everything else stays slate. The page below the map keeps the same grammar: flat slate blocks on a night ground, separated by 2px gutters, with data set in monospace.

The system is cold, dense and measured, matching the product voice (numbers with their conditions, no hype). There is one hue. Colour carries a single meaning, so the page can be read as a legend of one fact. Text cells are carved out of the map grid as ground-coloured blocks, so the headline and the data share one surface instead of text floating over an image.

**Key Characteristics:**
- Treemap world: rectangles, gutters, areas proportional to gigabytes
- One meaning colour: cyan equals safe to free
- Condensed uppercase display type against calm text and monospace data
- Flat tonal layering, almost no shadow
- Square corners, 2px gutters as the only separator in dense areas

## Colors

A night-slate palette with a single cyan voice. Slate steps do the structural work, cyan does the semantic work.

### Primary
- **Safe Cyan** (#56d6d6): space that is safe to free. Used for filled safe blocks, the live GB counter, risk labels reading safe, the primary Download buttons, the prompt glyph in terminal rows, focus rings, selection and the custom scrollbar thumb. Hover shifts to **Safe Cyan Light** (#7fe4e4).

### Neutral
- **Night Ground** (#0e121a): page background, text cells in the hero, the 2px gutters between blocks, text on cyan buttons.
- **Deep Slate** (#0a0d13): the quietest layer. Side cell, tooltip, terminal block, breadcrumb strip, scrollbar track.
- **Slate 1 / 2 / 3** (#121722, #1a2130, #232c3c): stepped surfaces for cards, folder group headers and hover states. Treemap blocks use five close shades from #161c27 to #262f42, chosen by name hash so neighbours differ without implying meaning.
- **Rule Line** (#222b3b) and **Rule Line Strong** (#2b3547): 1px hairlines for ledger rows, tooltip and chip borders.
- **Slate Mid** (#3a465c): breadcrumb separators and shrinking-bar fills.
- **Text** (#e8edf3), **Text Soft** (#b9c3d0), **Text Muted** (#8d99ab): primary, secondary and caption text.

### Named Rules
**The One Meaning Rule.** Cyan means safe to free and nothing else, apart from the primary action (Download) and the scrollbar. No second hue is introduced for any state, category or decoration.

**The Hatch Rule.** Caution or reported-only is cyan hatching (135 degree stripes, 2px line every 9px at 62% alpha) on a slate fill. Hatched never reads as solid, so caution can not be confused with safe.

**The Slate Is Everything Else Rule.** Whatever carries no verdict stays in the slate steps, with no tinting by category.

## Typography

**Display Font:** Archivo Variable (with Segoe UI, system-ui)
**Body Font:** Archivo Variable
**Label/Mono Font:** Martian Mono Variable (with Consolas, ui-monospace)

**Character:** Archivo squeezed on the width axis to 62 to 70 percent at weight 750 to 800 gives tall, dense uppercase that behaves like the treemap's own blocks. Plain-width Archivo carries reading text. Martian Mono is reserved for what the tool prints.

### Hierarchy
- **Display** (800, width 62%, clamp(48px, min(10.2cqw, 31cqh), 190px), line-height 0.82, uppercase): hero headline, sized by its container cell. The download title uses the same voice at clamp(64px, 11vw, 220px).
- **Headline** (800, width 66%, clamp(44px, 6vw, 104px), 0.88, uppercase): section titles, max 13ch.
- **Title** (700, width 85%, 22px, 1.15): safety item headings and the wordmark (21px). Feature card titles go to uppercase 66% width at clamp(30px, 2.8vw, 44px).
- **Figure** (750, width 70%, tabular numerals, 24 to 28px; the block size scales with its cell as clamp(11px, 22cqmin, 72px)): sizes on blocks, ledger and audit rows, diff deltas. The counter uses 800 at 62% width, clamp(36px, 3.2vw, 54px), in cyan.
- **Body** (400, 17px, 1.55): text; ledes capped at 46ch, notes at 60 to 66ch. Secondary copy 14 to 16px in Text Soft.
- **Label** (Martian Mono, 10 to 12px, letter-spacing 0.06 to 0.1em, uppercase for table heads and captions): paths, rule ids, commands, captions, scan stamp, block names at 10.5px.
- **Nav** (Archivo 550, 14px, 0.01em).

### Named Rules
**The Mono Is Data Rule.** Martian Mono appears only where the tool itself would print: paths, sizes in captions, commands, rule ids, scan conditions. Never for prose or headings.

**The Narrow Is Loud Rule.** The condensed width axis is for display, figures and sizes only. Reading text stays at normal width.

## Layout

The hero is a single `100svh` treemap (min 640px, 560px on small screens) under a thin top bar holding wordmark, nav, scan caption and GitHub link. Text cells sit on the treemap grid as ground-coloured rectangles.

From 1024px up, plan() carves the map into three areas with equal gigabytes per pixel: a top rectangle holding the two largest top-level folders, a bottom-right rectangle holding the smaller top-level folders, and a bottom-left text cell with wordmark context, headline, subline, the live safe-GB counter and the Download buttons. Below 1024px the text cell stacks under a full-width map, and below 640px small leaves fold into an "other" block.

Below the hero, sections sit in a centred column (max 1680px) with fluid side gutters (16 to 44px) and very generous vertical air (110 to 210px before each section). Section heads use a 12-column grid: title across 8, lede across 4, collapsing to one column at 960px. Dense content (ledger, diff, audit list) uses 1px hairline rows and monospace columns; card groups (faces, safety) use bento grids with 2px gaps, stepped slate fills and one spanning item. Breakpoints: 1200, 1023 (map stacks), 960, 760 (burger nav, single column), 640 (map folds).

## Elevation & Depth

Flat tonal layering. Depth is conveyed by slate steps (ground, deep, slate 1 to 3) and 2px ground gutters, never by shadow on cards. Two functional shadows exist: the tooltip (`0 18px 40px -18px rgba(0,0,0,0.8)`) so it lifts off the map, and the screenshot frame (`0 40px 80px -40px rgba(0,0,0,0.9)`). The primary button gains a faint cyan glow on hover (`0 10px 24px -12px rgba(86,214,214,0.55)`).

### Named Rules
**The Gutter Is The Border Rule.** Blocks are separated by a 2px gap of ground colour, not by outlines or shadows.

## Shapes

Square. Corners are 0 on blocks, cells, cards and the tooltip; buttons and the command chip carry a 2px hairline radius, nothing larger. Silhouettes are rectangles whose area means gigabytes. Borders are 1px hairlines; audit cells use a 1.5px dashed cyan outline to mark space a walk cannot see. Reveals use clip-path wipes rather than fades.

## Components

### Buttons
- **Shape:** rectangular, 2px radius, 46px tall (58px large).
- **Primary:** Safe Cyan fill, Night Ground text, weight 650, 15px (17px large), padding 0 20px (0 26px). Two on the page: hero and download section.
- **Hover / Focus:** lifts 2px, shifts to Safe Cyan Light with the soft cyan glow; returns on press. Focus ring is 2px cyan with 2px offset, shared by all interactive elements.
- **Ghost (download section only):** transparent, inset 1.5px Rule Line Strong border, turns Text colour on hover. Used for the arm64 build.
- **Text link (hero secondary):** monospace 11px uppercase, Text Soft, turns cyan on hover.

### Treemap blocks
- Slate leaf blocks with mono name (10.5px) and a narrow-width size scaled to the block. Safe blocks are overlaid with a cyan fill that wipes in by clip-path, text flipping to ground. Caution blocks fill with the hatch, their text set on small ground chips for legibility.
- Folder groups carry a 22px mono header (10px uppercase, name left, size right).
- Hover raises brightness 1.32 and shows the tooltip; click zooms into the block, breadcrumbs lead back.

### Tooltip
- Deep Slate panel, 1px strong border, mono 11px. Order: path, large narrow size, rule / risk / recovery row, plain-language note, vendor command in a slate chip with cyan text.

### Command chip
- Slate 1 fill, 1px strong border, 2px radius, mono 11.5px; the right-hand state word (copy / copied) turns cyan on hover and when done, border turns cyan on hover. Cursor is copy.

### Navigation
- Bar on ground with a 2px ground bottom border. Archivo 14px in Text Soft; hover brightens and a 1px cyan underline scales in from the left. Under 760px a bordered burger opens a deep-slate panel by clip-path wipe (reveal curve in, hide curve out).

### Ledger, diff and audit rows
- Hairline-ruled rows on ground, mono for rule ids and risk, large narrow figure for size. Bars: 3px solid cyan for safe, 6px hatch for caution; diff bars grow from a centre line, up in Text, down in Slate Mid. Risk text reads cyan for safe, Text with a hatch swatch for caution.

### Cards (faces, safety)
- Slate 1 / 2 / #1e2636 stepped fills, 2px gaps, content pinned to the bottom, hover to Slate 3. Titles in condensed uppercase; code in cyan mono.

### Terminal block
- Deep Slate with 1px rule border; rows are mono 12px with a cyan `>` prompt before each command and muted description.

### Preloader
- Inline in `index.html` (`#pl`), drawn before the bundle: ground #0e121a, the app icon's ring (slate track, cyan arc, `pathLength` 100) filling with load progress, and a mono caption `SCANNING C: N%`.
- `src/main.tsx` drives it: weighted progress (fonts, first render), at least 1.2 s on screen, 0.4 s on a repeat visit in the same session (`sessionStorage` key `pm-seen`), 15 s hard timeout plus an inline fallback timer. Hand-off is `html.is-ready` and the `app:ready` event; the preloader leaves with a clip-path wipe upward on the hide curve.

## Motion

One curve pair: reveal `cubic-bezier(0.19, 1, 0.22, 1)` and hide `cubic-bezier(0.77, 0, 0.175, 1)`, plus `ease-out` / `quint` for hover micro-states (150ms micro, 300ms UI). Entrances are clip-path wipes (rows left to right, headings bottom to top) and short lifts. On ready the map draws slate, safe blocks fill cyan largest first and the counter rolls to 37.6 GB. Zoom is a transform on the map layer. Lenis smooth scroll runs only when motion is allowed. With reduced motion every final state shows at once and Lenis is off.

Divergence: the JS tweens (GSAP) use the named `expo.out` and `power3` eases, which approximate but do not literally use the two CSS curves; only CSS transitions use the exact pair.

## Do's and Don'ts

### Do:
- **Do** use cyan (#56d6d6) only for safe-to-free, the primary Download action and the scrollbar.
- **Do** show caution as cyan hatching on slate, never as solid cyan or a second hue.
- **Do** separate blocks with 2px ground gutters and keep corners at 0 (2px on buttons and chips).
- **Do** set display and figures in Archivo at 62 to 70% width, weight 750 to 800; set paths, sizes in captions and commands in Martian Mono.
- **Do** carry every figure with its scan and condition, and use tabular numerals for sizes.
- **Do** end visible text blocks without a final period, and write without em or en dashes.
- **Do** keep a custom cyan scrollbar and honor reduced motion by showing final states.

### Don't:
- **Don't** add a second accent hue for status, categories or decoration.
- **Don't** put Martian Mono on prose or headings, or condensed width on body text.
- **Don't** add cards with outlines or drop shadows; depth comes from slate steps and gutters.
- **Don't** replace the treemap hero with a headline beside a terminal screenshot above a feature grid.
- **Don't** invent numbers: users, stars, downloads or benchmarks are absent from the product.
