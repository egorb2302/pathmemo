import { useEffect, useRef, useState } from 'react'

export function CopyCommand({ text }: { text: string }) {
  const [state, setState] = useState<'idle' | 'done' | 'manual'>('idle')
  const codeRef = useRef<HTMLElement>(null)
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => () => window.clearTimeout(timer.current), [])

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setState('done')
    } catch {
      const el = codeRef.current
      if (el) {
        const range = document.createRange()
        range.selectNodeContents(el)
        const sel = window.getSelection()
        sel?.removeAllRanges()
        sel?.addRange(range)
      }
      setState('manual')
    }
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setState('idle'), 2400)
  }

  return (
    <button type="button" className={`cmd${state === 'done' ? ' is-done' : ''}`} onClick={copy} aria-label={`Copy ${text}`}>
      <code ref={codeRef}>{text}</code>
      <span className="cmd-state" aria-live="polite">
        {state === 'done' ? 'copied' : state === 'manual' ? 'press Ctrl+C' : 'copy'}
      </span>
    </button>
  )
}
