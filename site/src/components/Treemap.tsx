import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { hierarchy, treemap, treemapSquarify, type HierarchyRectangularNode } from 'd3-hierarchy'
import gsap from 'gsap'
import { DISK, SCAN, type DiskNode } from '../data/scan'
import { gb, reducedMotion } from '../lib/format'

type Rect = HierarchyRectangularNode<DiskNode>
type Box = { x: number; y: number; w: number; h: number }

const GAP = 2
const STACKED = '(max-width: 1023px)'
const SHADES = ['#161c27', '#1a2130', '#1e2636', '#222b3c', '#262f42']

function shadeOf(name: string, depth: number): string {
  let h = depth * 7
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) >>> 0
  return SHADES[h % SHADES.length]
}

function findByPath(root: DiskNode, path: string[]): DiskNode {
  let node = root
  for (const name of path.slice(1)) {
    const next = node.children?.find((c) => c.name === name)
    if (!next) break
    node = next
  }
  return node
}

function total(node: DiskNode): number {
  return node.children ? node.children.reduce((s, c) => s + total(c), 0) : node.size ?? 0
}

function foldSmall(node: DiskNode, floor: number): DiskNode {
  if (!node.children) return node
  const kept: DiskNode[] = []
  let rest = 0
  for (const c of node.children) {
    if (!c.children && !c.match && (c.size ?? 0) < floor) rest += c.size ?? 0
    else kept.push(foldSmall(c, floor))
  }
  if (rest > 0) {
    const i = kept.findIndex((k) => k.name === 'other' && !k.children)
    if (i >= 0) kept[i] = { ...kept[i], size: (kept[i].size ?? 0) + rest }
    else kept.push({ name: 'other', size: rest })
  }
  return { ...node, children: kept }
}

function layout(data: DiskNode, box: Box, narrow: boolean): Rect[] {
  if (box.w < 4 || box.h < 4) return []
  const root = hierarchy<DiskNode>(data)
    .sum((d) => (d.children ? 0 : d.size ?? 0))
    .sort((a, b) => (b.value ?? 0) - (a.value ?? 0))
  const laid = treemap<DiskNode>()
    .tile(treemapSquarify.ratio(narrow ? 1 : 1.25))
    .size([box.w, box.h])
    .paddingInner(GAP)
    .paddingOuter((n) => (n.depth === 0 ? 0 : GAP))
    .paddingTop((n) => (n.depth === 0 ? 0 : narrow ? (n.depth === 1 ? 16 : 0) : 22))
    .round(true)(root)
  const nodes = laid.descendants()
  for (const n of nodes) {
    n.x0 += box.x
    n.x1 += box.x
    n.y0 += box.y
    n.y1 += box.y
  }
  return nodes
}

type Plan = { main: Box; side: Box | null; cell: Box; mainNodes: Rect[]; sideNodes: Rect[] }

function plan(focus: string[], w: number, h: number, cellH: number, stacked: boolean): Plan {
  const narrow = w < 640
  const subtree = findByPath(DISK, focus)
  const data = narrow ? foldSmall(subtree, total(subtree) * 0.02) : subtree
  const atRoot = focus.length === 1

  if (stacked) {
    const ch = Math.min(cellH, h - 200)
    const main = { x: 0, y: 0, w, h: h - ch - GAP }
    const cell = { x: 0, y: h - ch, w, h: ch }
    return { main, side: null, cell, mainNodes: layout(data, main, narrow), sideNodes: [] }
  }

  const kids = [...(data.children ?? [])].sort((a, b) => total(b) - total(a))
  const top = kids.slice(0, 2)
  const rest = kids.slice(2)
  const vTop = top.reduce((s, c) => s + total(c), 0)
  const vRest = rest.reduce((s, c) => s + total(c), 0)
  const minH = Math.max(290, h * 0.36)

  if (!atRoot || vRest <= 0) {
    const ch = Math.min(Math.max(minH, 250), h - 260)
    const main = { x: 0, y: 0, w, h: h - ch - GAP }
    const cellW = Math.round(w * 0.62)
    const cell = { x: 0, y: h - ch, w: cellW, h: ch }
    const side = { x: cellW + GAP, y: h - ch, w: w - cellW - GAP, h: ch }
    return { main, side, cell, mainNodes: layout(data, main, false), sideNodes: [] }
  }

  const ratio = vTop / vRest
  let cellW = w * 0.56
  let ch = (w * h) / (w + ratio * (w - cellW))
  if (ch < minH) {
    cellW = w - ((w * h) / minH - w) / ratio
    cellW = Math.min(cellW, w - 240)
    ch = (w * h) / (w + ratio * (w - cellW))
  }
  cellW = Math.round(cellW)
  ch = Math.round(ch)
  const main = { x: 0, y: 0, w, h: h - ch - GAP }
  const cell = { x: 0, y: h - ch, w: cellW, h: ch }
  const side = { x: cellW + GAP, y: h - ch, w: w - cellW - GAP, h: ch }
  return {
    main,
    side,
    cell,
    mainNodes: layout({ name: data.name, children: top }, main, false),
    sideNodes: layout({ name: data.name, children: rest }, side, false),
  }
}

type Props = {
  ready: boolean
  onReclaimProgress: (gigabytes: number) => void
  cell: ReactNode
}

export function Treemap({ ready, onReclaimProgress, cell }: Props) {
  const boxRef = useRef<HTMLDivElement>(null)
  const layerRef = useRef<HTMLDivElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const cellRef = useRef<HTMLDivElement>(null)
  const played = useRef(false)
  const busy = useRef(false)
  const keyboard = useRef(false)
  const pinned = useRef<string | null>(null)
  const [size, setSize] = useState({ w: 0, h: 0 })
  const [cellH, setCellH] = useState(300)
  const [focus, setFocus] = useState<string[]>(['C:'])
  const [hover, setHover] = useState<Rect | null>(null)
  const [active, setActive] = useState(0)
  const [filled, setFilled] = useState(false)
  const [stacked, setStacked] = useState(() => window.matchMedia(STACKED).matches)

  useEffect(() => {
    const mq = window.matchMedia(STACKED)
    const on = () => setStacked(mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])

  useLayoutEffect(() => {
    const box = boxRef.current
    if (!box) return
    const measure = () => {
      setSize({ w: box.clientWidth, h: box.clientHeight })
    }
    measure()
    const ro = new ResizeObserver(measure)
    ro.observe(box)
    return () => ro.disconnect()
  }, [])

  useLayoutEffect(() => {
    if (!size.w || !stacked) return
    const inner = cellRef.current?.firstElementChild as HTMLElement | null
    if (!inner) return
    const h = Math.ceil(inner.scrollHeight)
    if (Math.abs(h - cellH) > 1) setCellH(h)
  })

  const p = useMemo(() => (size.w && size.h ? plan(focus, size.w, size.h, cellH, stacked) : null), [focus, size, cellH, stacked])

  const { groups, leaves } = useMemo(() => {
    if (!p) return { groups: [] as Rect[], leaves: [] as Rect[] }
    const all = [...p.mainNodes, ...p.sideNodes]
    return {
      groups: all.filter((n) => n.depth > 0 && n.children),
      leaves: all.filter((n) => !n.children),
    }
  }, [p])

  const prefix = focus.slice(0, -1).join('\\')
  const fullPath = useCallback(
    (n: Rect) => {
      const own = n
        .ancestors()
        .reverse()
        .map((a) => a.data.name)
        .join('\\')
      return prefix ? `${prefix}\\${own}` : own
    },
    [prefix],
  )

  const fillOrder = useMemo(
    () => leaves.filter((l) => l.data.match).sort((a, b) => (b.value ?? 0) - (a.value ?? 0)),
    [leaves],
  )

  useEffect(() => {
    if (!ready || played.current || !leaves.length || !layerRef.current) return
    played.current = true
    const layer = layerRef.current
    const blocks = layer.querySelectorAll<HTMLElement>('[data-block]')
    const fills = fillOrder.map((l) => layer.querySelector<HTMLElement>(`[data-fill="${CSS.escape(fullPath(l))}"]`))
    if (reducedMotion()) {
      setFilled(true)
      onReclaimProgress(SCAN.safe)
      return
    }
    const counter = { v: 0 }
    const tl = gsap.timeline({ onComplete: () => setFilled(true) })
    tl.fromTo(
      blocks,
      { clipPath: 'inset(0 0 100% 0)' },
      { clipPath: 'inset(0 0 0% 0)', duration: 0.9, ease: 'expo.out', stagger: 0.018, clearProps: 'clipPath' },
    )
    fills.forEach((el, i) => {
      if (!el) return
      tl.fromTo(
        el,
        { clipPath: 'inset(0 100% 0 0)' },
        { clipPath: 'inset(0 0% 0 0)', duration: 0.75, ease: 'expo.out' },
        i === 0 ? '-=0.2' : '<0.14',
      )
    })
    tl.to(
      counter,
      {
        v: SCAN.safe,
        duration: 0.14 * fills.length + 0.6,
        ease: 'power3.out',
        onUpdate: () => onReclaimProgress(counter.v),
      },
      fills.length ? `<-${0.14 * (fills.length - 1)}` : '>',
    )
    return () => {
      tl.progress(1)
    }
  }, [ready, leaves, fillOrder, fullPath, onReclaimProgress])

  const go = useCallback(
    (target: string[], from?: Rect) => {
      const layer = layerRef.current
      if (!layer || !p || busy.current || target.join('\\') === focus.join('\\')) return
      setHover(null)
      pinned.current = null
      gsap.killTweensOf(layer)
      if (reducedMotion()) {
        setFocus(target)
        return
      }
      busy.current = true
      const done = () => {
        setFocus(target)
        gsap.set(layer, { x: 0, y: 0, scaleX: 1, scaleY: 1, scale: 1 })
        gsap.fromTo(
          layer,
          { opacity: 0 },
          { opacity: 1, duration: 0.35, ease: 'power2.out', onComplete: () => void (busy.current = false) },
        )
      }
      if (from) {
        const t = p.main
        const sx = t.w / Math.max(1, from.x1 - from.x0)
        const sy = t.h / Math.max(1, from.y1 - from.y0)
        gsap.to(layer, {
          transformOrigin: '0 0',
          x: t.x - from.x0 * sx,
          y: t.y - from.y0 * sy,
          scaleX: sx,
          scaleY: sy,
          opacity: 0.2,
          duration: 0.65,
          ease: 'power3.inOut',
          onComplete: done,
        })
      } else {
        gsap.to(layer, {
          transformOrigin: '50% 50%',
          scale: 0.88,
          opacity: 0,
          duration: 0.4,
          ease: 'power3.in',
          onComplete: done,
        })
      }
    },
    [focus, p],
  )

  const back = useCallback(() => {
    if (focus.length > 1) go(focus.slice(0, -1))
  }, [focus, go])

  const groupPath = (g: Rect) => [
    ...focus,
    ...g
      .ancestors()
      .reverse()
      .slice(1)
      .map((n) => n.data.name),
  ]

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') back()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [back])

  const placeTip = (x: number, y: number) => {
    const tip = tipRef.current
    const box = boxRef.current
    if (!tip || !box) return
    const w = tip.offsetWidth
    const h = tip.offsetHeight
    const left = x + 18 + w > box.clientWidth ? x - 18 - w : x + 18
    const top = Math.min(Math.max(8, y + 18), box.clientHeight - h - 8)
    tip.style.transform = `translate3d(${Math.max(8, left)}px, ${top}px, 0)`
  }

  const tipAtBlock = (l: Rect) => {
    requestAnimationFrame(() => placeTip(Math.min(l.x0 + (l.x1 - l.x0) / 2, size.w - 40), l.y0 + Math.min(40, (l.y1 - l.y0) / 2)))
  }

  const onMove = (e: React.PointerEvent) => {
    if (e.pointerType !== 'mouse' || pinned.current) return
    const r = boxRef.current?.getBoundingClientRect()
    if (r) placeTip(e.clientX - r.left, e.clientY - r.top)
  }

  const onLeafClick = (l: Rect, e: React.MouseEvent) => {
    const key = fullPath(l)
    const touch = (e.nativeEvent as PointerEvent).pointerType === 'touch' || window.matchMedia('(pointer: coarse)').matches
    if (touch && pinned.current !== key) {
      pinned.current = key
      setHover(l)
      tipAtBlock(l)
      return
    }
    const g = l.parent && l.parent.depth > 0 ? l.parent : null
    if (g) go(groupPath(g), g)
  }

  const onKeyNav = (e: React.KeyboardEvent) => {
    if (!leaves.length) return
    const next = e.key === 'ArrowRight' || e.key === 'ArrowDown' ? 1 : e.key === 'ArrowLeft' || e.key === 'ArrowUp' ? -1 : 0
    if (!next) return
    e.preventDefault()
    keyboard.current = true
    setActive((a) => (a + next + leaves.length) % leaves.length)
  }

  useEffect(() => {
    if (!keyboard.current) return
    const el = layerRef.current?.querySelectorAll<HTMLElement>('[data-block]')[active]
    el?.focus()
  }, [active])

  useEffect(() => {
    setActive(0)
    keyboard.current = false
  }, [focus])

  const hovered = hover?.data
  const match = hovered?.match
  const zoomed = focus.length > 1
  const crumbs = zoomed && (
    <nav className="map-crumbs" aria-label="Folder path">
      <button type="button" className="crumb-back" onClick={back} aria-label="Back out one level">
        <svg viewBox="0 0 16 16" aria-hidden="true">
          <path d="M10 3 5 8l5 5" fill="none" stroke="currentColor" strokeWidth="1.6" />
        </svg>
      </button>
      {focus.map((name, i) => (
        <button
          key={name + i}
          type="button"
          className="crumb"
          disabled={i === focus.length - 1}
          onClick={() => go(focus.slice(0, i + 1))}
        >
          {name}
        </button>
      ))}
    </nav>
  )

  return (
    <div
      className="map"
      ref={boxRef}
      onPointerMove={onMove}
      onPointerLeave={(e) => {
        if (e.pointerType === 'mouse' && !pinned.current) setHover(null)
      }}
    >
      <div className="map-layer" ref={layerRef}>
        {groups.map((g) => (
          <button
            key={`g:${fullPath(g)}`}
            type="button"
            className="map-group"
            tabIndex={-1}
            style={{ left: g.x0, top: g.y0, width: g.x1 - g.x0, height: g.y1 - g.y0 }}
            onClick={() => go(groupPath(g), g)}
            aria-label={`Open ${g.data.name}, ${gb(g.value ?? 0)}`}
          >
            {g.x1 - g.x0 > 96 && !(size.w < 640 && g.depth > 1) && (
              <>
                <span className="map-group-name">{g.data.name}</span>
                <span className="map-group-size">{gb(g.value ?? 0)}</span>
              </>
            )}
          </button>
        ))}
        {leaves.map((l, i) => {
          const w = l.x1 - l.x0
          const h = l.y1 - l.y0
          const m = l.data.match
          const showName = w > 70 && h > 40
          const showSize = w > 58 && h > 28
          const key = fullPath(l)
          const opens = !!(l.parent && l.parent.depth > 0)
          const label = (
            <>
              {showName ? <span className="b-name">{l.data.name}</span> : <span />}
              {showSize && <span className="b-size">{gb(l.value ?? 0)}</span>}
            </>
          )
          return (
            <button
              key={key}
              type="button"
              data-block
              tabIndex={i === active ? 0 : -1}
              className={`b${m ? (m.risk === 'safe' ? ' is-safe' : ' is-caution') : ''}${filled ? ' is-filled' : ''}${opens ? '' : ' is-flat'}`}
              style={{ left: l.x0, top: l.y0, width: w, height: h, background: shadeOf(l.data.name, l.depth) }}
              onPointerEnter={(e) => {
                if (e.pointerType === 'mouse' && !pinned.current) setHover(l)
              }}
              onFocus={(e) => {
                setActive(i)
                if (keyboard.current || e.currentTarget.matches(':focus-visible')) {
                  setHover(l)
                  tipAtBlock(l)
                }
              }}
              onBlur={() => {
                if (!pinned.current) setHover(null)
              }}
              onPointerDown={() => void (keyboard.current = false)}
              onKeyDown={onKeyNav}
              onClick={(e) => onLeafClick(l, e)}
              aria-label={`${key}, ${gb(l.value ?? 0)}${m ? `, ${m.risk === 'safe' ? 'safe to free' : 'caution, reported only'}, rule ${m.rule}` : ''}`}
            >
              {label}
              {m && (
                <span className="b-fill" data-fill={key} aria-hidden="true">
                  {label}
                </span>
              )}
            </button>
          )
        })}
      </div>

      {p && (
        <div
          ref={cellRef}
          className="map-cell"
          style={{ left: p.cell.x, top: p.cell.y, width: p.cell.w, height: stacked ? 'auto' : p.cell.h }}
        >
          {cell}
        </div>
      )}

      {p && zoomed && p.side && (
        <div className="map-side" style={{ left: p.side.x, top: p.side.y, width: p.side.w, height: p.side.h }}>
          {crumbs}
        </div>
      )}
      {p && zoomed && !p.side && <div className="map-crumbs-float">{crumbs}</div>}

      <div ref={tipRef} className={`tip${hovered ? ' is-on' : ''}`} aria-hidden="true">
        {hover && (
          <>
            <div className="tip-path">{fullPath(hover)}</div>
            <div className="tip-size">{gb(hover.value ?? 0)}</div>
            {match ? (
              <>
                <div className="tip-rule">
                  <span className={`tip-risk tip-risk-${match.risk}`}>{match.reportOnly ? 'reported only' : match.risk}</span>
                  <span>{match.recovery}</span>
                  <span className="tip-id">{match.rule}</span>
                </div>
                <div className="tip-what">{match.what}</div>
                {match.command && <code className="tip-cmd">{match.command}</code>}
              </>
            ) : (
              <div className="tip-what tip-none">No rule claims it</div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
