import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { hierarchy, treemap, treemapSquarify } from 'd3-hierarchy'
import gsap from 'gsap'
import { Treemap } from './components/Treemap'
import { Reveal } from './components/Reveal'
import { CopyCommand } from './components/CopyCommand'
import { AUDIT, DIFF, RELEASE, SCAN } from './data/scan'
import { RULE_ROWS } from './data/rules'
import { gb, reducedMotion } from './lib/format'

function Logo() {
  return (
    <svg className="logo-mark" viewBox="0 0 64 64" aria-hidden="true">
      <rect width="64" height="64" rx="14" fill="#181e2a" />
      <g transform="rotate(-90 32 32)" fill="none" strokeWidth="9">
        <circle cx="32" cy="32" r="18" stroke="#3a465c" />
        <circle cx="32" cy="32" r="18" stroke="#56d6d6" pathLength="100" strokeDasharray="87 100" />
      </g>
    </svg>
  )
}

function GitHubIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className="ico">
      <path
        fill="currentColor"
        d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"
      />
    </svg>
  )
}

function DownloadIcon() {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" className="ico">
      <path d="M8 2v8M4.5 6.8 8 10.3l3.5-3.5M3 13.5h10" fill="none" stroke="currentColor" strokeWidth="1.6" />
    </svg>
  )
}

function Hero({ ready }: { ready: boolean }) {
  const [safe, setSafe] = useState(0)
  const [menu, setMenu] = useState(false)
  const onProgress = useCallback((v: number) => setSafe(v), [])
  return (
    <section className="hero" aria-labelledby="hero-title">
      <header className="bar">
        <a className="wordmark" href="#top" aria-label="pathmemo home">
          <Logo />
          <span>pathmemo</span>
        </a>
        <button
          type="button"
          className="burger"
          aria-expanded={menu}
          aria-controls="site-nav"
          onClick={() => setMenu((m) => !m)}
        >
          <i aria-hidden="true" />
          Menu
        </button>
        <nav id="site-nav" className={`nav${menu ? ' is-open' : ''}`} aria-label="Sections" onClick={() => setMenu(false)}>
          <a href="#reclaim">Reclaim</a>
          <a href="#audit">Audit</a>
          <a href="#history">History</a>
          <a href="#safety">Safety</a>
          <a href="#download">Download</a>
        </nav>
        <p className="bar-scan">
          scan {SCAN.id}, {SCAN.volume} {SCAN.total} GB in {SCAN.files} files, {SCAN.date}, {SCAN.scanner}
        </p>
        <a className="nav-gh" href={RELEASE.repo} target="_blank" rel="noreferrer">
          <GitHubIcon />
          <span>Source</span>
        </a>
      </header>

      <div className="hero-map">
        <Treemap
          ready={ready}
          onReclaimProgress={onProgress}
          cell={
            <div className="cell">
              <h1 id="hero-title" className="hero-title">
                Where the space went
              </h1>
              <p className="cell-sub">
                A disk space screener for Windows in one exe
                <span className="sub-more">
                  . It shows where the space went, finds what a folder scan cannot see, and frees caches the way their
                  own tools do
                </span>
              </p>
              <div className="cell-act">
                <p className="counter">
                  <span className="counter-num" aria-hidden="true">
                    {safe.toFixed(1)} GB
                  </span>
                  <span className="sr-only">{SCAN.safe} GB</span>
                  <span className="counter-label">
                    safe to free on this disk, {SCAN.matches} matches, the largest drawn
                    <span className="counter-key">
                      <i className="key key-safe" /> safe <i className="key key-caution" /> reported only
                    </span>
                  </span>
                </p>
                <div className="cell-btns">
                  <a className="btn" href={RELEASE.x64}>
                    <DownloadIcon />
                    Download for Windows
                  </a>
                  <a className="btn-alt" href={RELEASE.arm64}>
                    arm64
                  </a>
                </div>
              </div>
            </div>
          }
        />
      </div>
    </section>
  )
}

function SectionHead({ id, title, children }: { id: string; title: string; children: React.ReactNode }) {
  return (
    <div className="sec-head">
      <Reveal as="h2" id={`${id}-title`} className="sec-title">
        {title}
      </Reveal>
      <div className="sec-lede">{children}</div>
    </div>
  )
}

function Reclaim() {
  const max = Math.max(...RULE_ROWS.map((r) => r.size))
  return (
    <section className="sec" id="reclaim" aria-labelledby="reclaim-title">
      <SectionHead id="reclaim" title="The vendor's command, not a path to delete">
        <p>
          38 rules know where developer tools keep their caches. Every match carries what it costs to get back and,
          where the tool ships one, its own cleanup command. Caches that pathmemo should not touch are reported and
          left to their owner
        </p>
      </SectionHead>
      <div className="ledger" role="table" aria-label={`Reclaim matches on scan ${SCAN.id}`}>
        <div className="ledger-row ledger-th" role="row">
          <span role="columnheader">Rule</span>
          <span role="columnheader">Where</span>
          <span role="columnheader" className="r">
            Size
          </span>
          <span role="columnheader">Risk</span>
          <span role="columnheader">Recovery</span>
          <span role="columnheader">Command</span>
        </div>
        {[...RULE_ROWS].sort((a, b) => b.size - a.size).map((r, i) => (
          <Reveal as="div" key={r.rule} className={`ledger-row is-${r.risk}`} role="row" delay={i * 0.04} kind="row">
            <span role="cell" className="l-rule">
              {r.rule}
            </span>
            <span role="cell" className="l-where">
              {r.where}
              {r.note && <em>{r.note}</em>}
            </span>
            <span role="cell" className="l-size r">
              <span className="l-bar" style={{ ['--w' as string]: `${(r.size / max) * 100}%` }} />
              {gb(r.size)}
            </span>
            <span role="cell" className={`l-risk l-risk-${r.risk}`}>
              {r.risk}
            </span>
            <span role="cell" className="l-rec">
              {r.recovery}
            </span>
            <span role="cell" className="l-cmd">
              {r.command ? (
                <CopyCommand text={r.command} />
              ) : (
                <span className="l-none">{r.risk === 'safe' ? 'quarantined on --apply' : 'yours to decide'}</span>
              )}
            </span>
          </Reveal>
        ))}
      </div>
      <p className="foot-note">
        {RULE_ROWS.length} of the 21 rules that matched on scan {SCAN.id}, {SCAN.matches} safe matches in all. A match inside another match is counted once,
        and a file shared through hard links is not counted as space you get back
      </p>
    </section>
  )
}

function Audit() {
  const total = AUDIT.findings.reduce((s, f) => s + parseFloat(f.size), 0)
  const cells = useMemo(() => {
    type Cell = { name: string; v: number; children?: Cell[] }
    const data: Cell = { name: 'audit', v: 0, children: AUDIT.findings.map((f) => ({ name: f.name, v: parseFloat(f.size) })) }
    const root = hierarchy<Cell>(data)
      .sum((d) => (d.children ? 0 : d.v))
      .sort((a, b) => (b.value ?? 0) - (a.value ?? 0))
    return treemap<Cell>().tile(treemapSquarify.ratio(1.4)).size([160, 100]).paddingInner(1.4)(root).leaves()
  }, [])
  return (
    <section className="sec" id="audit" aria-labelledby="audit-title">
      <SectionHead id="audit" title="The space a folder scan cannot see">
        <p>
          Restore points, the page file, hibernation, Windows Update packages, WSL and Docker disks. Space Audit runs{' '}
          {AUDIT.probes} probes for what a walk of the folders physically misses, and names the setting or command that
          gives it back
        </p>
      </SectionHead>
      <div className="audit">
        <div className="audit-map" aria-hidden="true">
          {cells.map((c, i) => {
            const f = AUDIT.findings.find((x) => x.name === c.data.name)!
            return (
              <Reveal
                as="div"
                key={f.name}
                kind="dash"
                delay={i * 0.08}
                className={`audit-cell is-${f.risk}`}
                style={{ left: `${c.x0 / 1.6}%`, top: `${c.y0}%`, width: `${(c.x1 - c.x0) / 1.6}%`, height: `${c.y1 - c.y0}%` }}
              >
                <span className="b-name">{f.name}</span>
                <span className="b-size">{f.size}</span>
              </Reveal>
            )
          })}
        </div>
        <ol className="audit-list">
          {AUDIT.findings.map((f) => (
            <li key={f.name}>
              <div className="a-top">
                <span className="a-name">{f.name}</span>
                <span className="a-size">{f.size}</span>
                <span className={`l-risk l-risk-${f.risk}`}>{f.risk}</span>
              </div>
              <code className="a-cmd">
                {f.command}
                {f.admin && <span className="a-admin">admin</span>}
              </code>
            </li>
          ))}
        </ol>
      </div>
      <p className="foot-note">
        {gb(total)} found on the same machine, {AUDIT.date}, without administrator rights. {AUDIT.answered} of the {AUDIT.probes} probes
        answer unelevated, the rest need administrator rights
      </p>
    </section>
  )
}

function CountUp({ value, className }: { value: string; className: string }) {
  const ref = useRef<HTMLSpanElement>(null)
  useEffect(() => {
    const el = ref.current
    const m = value.match(/^([+-]?)(\d+(?:\.\d+)?)(.*)$/)
    if (!el || !m || reducedMotion()) return
    const [, sign, num, unit] = m
    const end = parseFloat(num)
    const digits = num.includes('.') ? num.split('.')[1].length : 0
    const io = new IntersectionObserver((entries) => {
      if (!entries.some((e) => e.isIntersecting)) return
      io.disconnect()
      const o = { v: 0 }
      gsap.to(o, {
        v: end,
        duration: 1.4,
        ease: 'power3.out',
        onUpdate: () => void (el.textContent = `${sign}${o.v.toFixed(digits)}${unit}`),
      })
    })
    el.textContent = `${sign}${(0).toFixed(digits)}${unit}`
    io.observe(el)
    return () => {
      io.disconnect()
      el.textContent = value
    }
  }, [value])
  return (
    <span ref={ref} className={className}>
      {value}
    </span>
  )
}

function History() {
  const parse = (d: string) => {
    const n = parseFloat(d.replace(/[+-]/, ''))
    return d.includes('GB') ? n : n / 1000
  }
  const max = Math.max(...DIFF.rows.map((r) => parse(r.delta)))
  return (
    <section className="sec" id="history" aria-labelledby="history-title">
      <SectionHead id="history" title="What changed since the last scan">
        <p>
          Every scan is kept as a compact snapshot, so pathmemo can answer the question a one-off scanner cannot: what
          grew, what is new, what went. The diff of two 1.6 million node snapshots takes 105 ms
        </p>
      </SectionHead>
      <div className="diff">
        <div className="diff-head">
          <span>
            scan {DIFF.from} to scan {DIFF.to}
          </span>
          <span>{DIFF.span}</span>
          <CountUp className="diff-total" value={DIFF.change} />
          <span>{DIFF.free}</span>
        </div>
        {DIFF.rows.map((r, i) => (
          <Reveal as="div" key={r.path} className={`diff-row ${r.up ? 'is-up' : 'is-down'}`} delay={i * 0.05} kind="row">
            <span className="d-path">{r.path}</span>
            <span className="d-bar">
              <i style={{ ['--w' as string]: `${Math.max(2, (Math.sqrt(parse(r.delta)) / Math.sqrt(max)) * 100)}%` }} />
            </span>
            <span className="d-delta">{r.delta}</span>
            <span className="d-note">{r.note}</span>
          </Reveal>
        ))}
      </div>
    </section>
  )
}

const FACES = [
  {
    name: 'The window',
    how: 'Double-click pathmemo.exe',
    text: 'Volumes, the treemap, the folder tree and the reclaim report, drawn by its own Win32 code rather than a framework',
  },
  {
    name: 'The terminal',
    how: 'pathmemo --tui',
    text: 'Five screens in any terminal from 80 by 24, over SSH too. A frame draws in under a millisecond',
  },
  {
    name: 'The script',
    how: 'pathmemo reclaim --json',
    text: 'Every action from the command line, with stable JSON and exit codes that mean something',
  },
]

const COMMANDS = [
  ['pathmemo scan', 'scan every fixed volume'],
  ['pathmemo top --min 1GB', 'largest files'],
  ['pathmemo audit', 'where the invisible space went'],
  ['pathmemo reclaim', 'what is worth deleting, by rule'],
  ['pathmemo diff', 'what changed since the previous scan'],
  ['pathmemo rm <path>', 'delete into quarantine'],
]

function Faces() {
  return (
    <section className="sec" id="faces" aria-labelledby="faces-title">
      <SectionHead id="faces" title="One exe, three ways in">
        <p>
          The same file opens a window when double-clicked and answers commands when typed. Administrator rights are
          optional: with them the scanner reads the MFT directly, 1.27 million files in 9.6 seconds
        </p>
      </SectionHead>
      <div className="faces">
        {FACES.map((f, i) => (
          <Reveal as="article" key={f.name} className="face" delay={i * 0.08}>
            <h3>{f.name}</h3>
            <code>{f.how}</code>
            <p>{f.text}</p>
          </Reveal>
        ))}
      </div>
      <Reveal as="figure" className="shot">
        <img
          src="/shots/window-overview.webp"
          width={1400}
          height={860}
          loading="lazy"
          decoding="async"
          alt="The pathmemo 0.2.3 window on its Overview tab: C: 91.4% used with 14.8 GB unaccounted by scan 12, D: 63.3% used, the last scan and pathmemo's own data under its 500 MB limit"
        />
        <figcaption>The window, pathmemo 0.2.3 on the same machine, 1 Oct 2026</figcaption>
      </Reveal>
      <div className="term" role="region" aria-label="Commands">
        {COMMANDS.map(([cmd, what]) => (
          <div className="term-row" key={cmd}>
            <code>{cmd}</code>
            <span>{what}</span>
          </div>
        ))}
      </div>
    </section>
  )
}

const SAFETY = [
  ['Nothing happens on its own', 'No auto-clean and no one-click optimisation. Files leave their folder only on a command you typed or a key you pressed'],
  ['Quarantine first', 'rm moves into quarantine by default and restore brings it back. Quarantine older than 7 days is purged at startup, and the first run says so'],
  ['Freed space is measured', 'Free space is read before and after each operation and the real difference is shown, not the estimate'],
  ['A guard on every path', 'Deletion never follows a junction or a link out of the folder. The guard kept its canary intact through 10,000 junction-swap races'],
]

function Safety() {
  return (
    <section className="sec" id="safety" aria-labelledby="safety-title">
      <SectionHead id="safety" title="Nothing is deleted until you say so">
        <p>No account and no telemetry. The tool keeps its own data in one folder and never lets it pass 500 MB</p>
      </SectionHead>
      <div className="safety">
        {SAFETY.map(([h, t], i) => (
          <Reveal as="div" key={h} className="safety-item" delay={i * 0.06}>
            <h3>{h}</h3>
            <p>{t}</p>
          </Reveal>
        ))}
      </div>
    </section>
  )
}

function Get() {
  return (
    <section className="get" id="download" aria-labelledby="get-title">
      <Reveal as="h2" id="get-title" className="get-title">
        Get pathmemo {RELEASE.version}
      </Reveal>
      <div className="get-grid">
        <div className="get-btns">
          <a className="btn btn-lg" href={RELEASE.x64}>
            <DownloadIcon />
            Windows x64 zip
          </a>
          <a className="btn btn-lg btn-ghost" href={RELEASE.arm64}>
            <DownloadIcon />
            Windows arm64 zip
          </a>
        </div>
        <div className="get-notes">
          <p>
            Unpack anywhere and run pathmemo.exe. Nothing is installed, and nothing is written outside
            %LOCALAPPDATA%\pathmemo
          </p>
          <p>
            The exe is not code-signed yet, so Windows warns about an unknown publisher: More info, then Run anyway. Or
            check it first
          </p>
          <div className="sha">
            <span>SHA-256, x64</span>
            <code>{RELEASE.x64Sha}</code>
            <a href={RELEASE.sums}>all sums</a>
          </div>
        </div>
      </div>
    </section>
  )
}

function Footer() {
  return (
    <footer className="foot">
      <a className="wordmark" href="#top">
        <Logo />
        <span>pathmemo</span>
      </a>
      <nav aria-label="Project">
        <a href={RELEASE.repo} target="_blank" rel="noreferrer">
          <GitHubIcon /> GitHub
        </a>
        <a href={`${RELEASE.repo}/blob/master/CHANGELOG.md`} target="_blank" rel="noreferrer">
          Changelog
        </a>
        <a href={`${RELEASE.repo}/releases`} target="_blank" rel="noreferrer">
          Releases
        </a>
        <a href={`${RELEASE.repo}/blob/master/LICENSE`} target="_blank" rel="noreferrer">
          MIT license
        </a>
      </nav>
      <p>
        v{RELEASE.version}, {RELEASE.date}. Open source, by egorb2302
      </p>
    </footer>
  )
}

export default function App() {
  const [ready, setReady] = useState(document.documentElement.classList.contains('is-ready'))
  const mainRef = useRef<HTMLElement>(null)

  useEffect(() => {
    const on = () => setReady(true)
    window.addEventListener('app:ready', on)
    return () => window.removeEventListener('app:ready', on)
  }, [])

  useEffect(() => {
    if (!ready || reducedMotion()) return
    const title = document.querySelector('.hero-title')
    if (!title) return
    gsap.fromTo(
      title,
      { clipPath: 'inset(0 100% 0 0)' },
      { clipPath: 'inset(0 0% 0 0)', duration: 1.1, ease: 'expo.out', clearProps: 'clipPath' },
    )
    gsap.fromTo('.cell-sub, .cell-act', { opacity: 0, y: 12 }, { opacity: 1, y: 0, duration: 0.8, delay: 0.25, stagger: 0.08, ease: 'expo.out' })
  }, [ready])

  return (
    <main id="top" ref={mainRef}>
      <Hero ready={ready} />
      <Reclaim />
      <Audit />
      <History />
      <Faces />
      <Safety />
      <Get />
      <Footer />
    </main>
  )
}
