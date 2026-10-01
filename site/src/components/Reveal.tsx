import { createElement, useLayoutEffect, useRef, type CSSProperties, type ReactNode } from 'react'
import gsap from 'gsap'
import { reducedMotion } from '../lib/format'

type Kind = 'line' | 'row' | 'dash' | 'lift'

type Props = {
  as: keyof HTMLElementTagNameMap
  kind?: Kind
  delay?: number
  className?: string
  id?: string
  role?: string
  style?: CSSProperties
  children: ReactNode
}

const FROM: Record<Kind, gsap.TweenVars> = {
  line: { clipPath: 'inset(0 0 100% 0)', y: 24 },
  row: { clipPath: 'inset(0 100% 0 0)' },
  dash: { opacity: 0, scale: 0.94 },
  lift: { opacity: 0, y: 28 },
}

const TO: Record<Kind, gsap.TweenVars> = {
  line: { clipPath: 'inset(0 0 0% 0)', y: 0, duration: 1.1 },
  row: { clipPath: 'inset(0 0% 0 0)', duration: 0.8 },
  dash: { opacity: 1, scale: 1, duration: 0.9 },
  lift: { opacity: 1, y: 0, duration: 0.9 },
}

export function Reveal({ as, kind, delay = 0, children, ...rest }: Props) {
  const ref = useRef<HTMLElement>(null)
  const k: Kind = kind ?? (as === 'h2' ? 'line' : 'lift')

  useLayoutEffect(() => {
    const el = ref.current
    if (!el || reducedMotion()) return
    const r = el.getBoundingClientRect()
    if (r.top < window.innerHeight * 0.92) return
    gsap.set(el, FROM[k])
    const io = new IntersectionObserver(
      (entries) => {
        if (!entries.some((e) => e.isIntersecting)) return
        io.disconnect()
        gsap.to(el, { ...TO[k], delay, ease: 'expo.out', clearProps: 'clipPath,transform,opacity' })
      },
      { rootMargin: '0px 0px -12% 0px' },
    )
    io.observe(el)
    return () => {
      io.disconnect()
      gsap.set(el, { clearProps: 'clipPath,transform,opacity' })
    }
  }, [k, delay])

  return createElement(as, { ref, ...rest, 'data-reveal': k }, children)
}
