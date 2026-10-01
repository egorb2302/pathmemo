import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import gsap from 'gsap'
import Lenis from 'lenis'
import '@fontsource-variable/archivo/wdth.css'
import '@fontsource-variable/martian-mono/standard.css'
import './styles.css'
import App from './App'

function seen(): boolean {
  try {
    return sessionStorage.getItem('pm-seen') === '1'
  } catch {
    return false
  }
}

const MIN_MS = seen() ? 400 : 1200
const HARD_MS = 15000
const started = performance.now()
const pct = document.getElementById('pl-pct')
const ring = document.querySelector<SVGCircleElement>('#pl .pl-fill')
let shown = 0
let target = 10

function paint() {
  shown += (target - shown) * 0.18
  const v = Math.min(100, Math.round(shown))
  if (pct) pct.textContent = String(v)
  if (ring) ring.style.strokeDasharray = `${v} 100`
}

const ticker = window.setInterval(paint, 40)

let finished = false

window.addEventListener('app:ready', () => lenis?.start())

function finish() {
  if (finished) return
  finished = true
  target = 100
  const wait = Math.max(0, MIN_MS - (performance.now() - started))
  window.setTimeout(() => {
    shown = 100
    paint()
    window.clearInterval(ticker)
    try {
      sessionStorage.setItem('pm-seen', '1')
    } catch {
      /* storage blocked */
    }
    if (!document.documentElement.classList.contains('is-ready')) {
      document.documentElement.classList.add('is-ready')
      window.dispatchEvent(new Event('app:ready'))
    }
    lenis?.start()
  }, wait)
}

const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches
const lenis = calm ? null : new Lenis({ autoRaf: false, lerp: 0.12 })
if (lenis) {
  lenis.stop()
  gsap.ticker.add((t) => lenis.raf(t * 1000))
  gsap.ticker.lagSmoothing(0)
}

document.addEventListener('click', (e) => {
  if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return
  const a = (e.target as HTMLElement).closest('a[href^="#"]')
  if (!a) return
  const id = a.getAttribute('href')!
  const el = id === '#top' ? document.body : document.querySelector(id)
  if (!el) return
  e.preventDefault()
  if (lenis) lenis.scrollTo(el as HTMLElement, { offset: 0, duration: 1.2 })
  else (el as HTMLElement).scrollIntoView()
  const target = (el as HTMLElement).querySelector<HTMLElement>('h1, h2') ?? (el as HTMLElement)
  if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1')
  target.focus({ preventScroll: true })
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)

target = 45
let done = false
const once = () => {
  if (done) return
  done = true
  finish()
}
Promise.all([
  document.fonts.load('800 100px "Archivo Variable"'),
  document.fonts.load('400 12px "Martian Mono Variable"'),
])
  .catch(() => undefined)
  .then(() => document.fonts.ready)
  .then(() => {
  target = 85
  requestAnimationFrame(() => requestAnimationFrame(once))
})
window.setTimeout(once, HARD_MS)
