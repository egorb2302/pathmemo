export type RuleRow = {
  rule: string
  where: string
  size: number
  risk: 'safe' | 'caution'
  recovery: 'instant' | 'redownload' | 'rebuild' | 'irreversible'
  command?: string
  note?: string
}

export const RULE_ROWS: RuleRow[] = [
  { rule: 'dev.npm_cache', where: '~\\AppData\\Local\\npm-cache', size: 7.66, risk: 'safe', recovery: 'redownload', command: 'npm cache clean --force', note: '274 MB shared via hard links, not counted' },
  { rule: 'app.shader_cache', where: 'NVIDIA\\DXCache and 26 more', size: 7.85, risk: 'safe', recovery: 'instant' },
  { rule: 'sys.temp', where: '~\\AppData\\Local\\Temp, C:\\Windows\\Temp', size: 7.52, risk: 'safe', recovery: 'instant' },
  { rule: 'dev.solana_tools', where: '~\\.cache\\solana', size: 3.14, risk: 'safe', recovery: 'redownload' },
  { rule: 'app.browser_cache', where: '163 browser and app caches', size: 3.11, risk: 'safe', recovery: 'instant' },
  { rule: 'dev.bun_cache', where: '~\\.bun\\install\\cache', size: 2.81, risk: 'safe', recovery: 'redownload', command: 'bun pm cache rm' },
  { rule: 'dev.nuget', where: '~\\.nuget\\packages, NuGet\\v3-cache', size: 1.23, risk: 'safe', recovery: 'redownload', command: 'dotnet nuget locals all --clear' },
  { rule: 'dev.node_modules', where: '31 node_modules folders', size: 1.13, risk: 'safe', recovery: 'redownload', note: 'npm ci brings a project back' },
  { rule: 'dev.playwright_browsers', where: '~\\AppData\\Local\\ms-playwright', size: 0.708, risk: 'safe', recovery: 'redownload', command: 'npx playwright uninstall --all' },
  { rule: 'dev.rustup_toolchains', where: '~\\.rustup\\toolchains', size: 3.77, risk: 'caution', recovery: 'redownload', command: 'rustup toolchain uninstall <old>', note: 'reported, never deleted by pathmemo' },
  { rule: 'user.large_media', where: 'a 7.63 GB virtual disk image', size: 7.63, risk: 'caution', recovery: 'irreversible', note: 'decide by hand' },
]
