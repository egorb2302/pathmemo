export function gb(value: number): string {
  if (value >= 100) return `${Math.round(value)} GB`
  if (value >= 1) return `${value.toFixed(value >= 10 ? 1 : 2).replace(/\.?0+$/, '')} GB`
  return `${Math.round(value * 1000)} MB`
}

export function reducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}
