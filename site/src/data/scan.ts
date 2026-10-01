export type Risk = 'safe' | 'caution'

export type Match = {
  rule: string
  what: string
  risk: Risk
  recovery: 'instant' | 'redownload' | 'rebuild' | 'irreversible'
  command?: string
  reportOnly?: boolean
}

export type DiskNode = {
  name: string
  size?: number
  match?: Match
  children?: DiskNode[]
}

export const SCAN = {
  id: 12,
  volume: 'C:',
  date: '22 Sep 2026',
  scanner: 'walk, unelevated',
  files: '1,210,960',
  total: 191,
  safe: 37.6,
  caution: 49.0,
  matches: '1,777',
}

const npmCache: Match = {
  rule: 'dev.npm_cache',
  what: "npm's download cache",
  risk: 'safe',
  recovery: 'redownload',
  command: 'npm cache clean --force',
}

export const DISK: DiskNode = {
  name: 'C:',
  children: [
    {
      name: 'Users\\you',
      children: [
        {
          name: 'AppData\\Local',
          children: [
            { name: 'Packages', size: 11.9 },
            { name: 'npm-cache', size: 7.93, match: npmCache },
            {
              name: 'NVIDIA\\DXCache',
              size: 7.8,
              match: { rule: 'app.shader_cache', what: 'compiled GPU shaders', risk: 'safe', recovery: 'instant' },
            },
            {
              name: 'Temp',
              size: 7.58,
              match: { rule: 'sys.temp', what: 'temporary files', risk: 'safe', recovery: 'instant' },
            },
            {
              name: 'ms-playwright',
              size: 0.708,
              match: {
                rule: 'dev.playwright_browsers',
                what: 'browsers downloaded for Playwright',
                risk: 'safe',
                recovery: 'redownload',
                command: 'npx playwright uninstall --all',
              },
            },
            { name: 'other apps', size: 33.08 },
          ],
        },
        {
          name: 'AppData\\Roaming',
          children: [
            {
              name: 'Claude\\vm_bundles\\rootfs.vhdx',
              size: 7.63,
              match: {
                rule: 'user.large_media',
                what: 'disk images and backups over 1 GB',
                risk: 'caution',
                recovery: 'irreversible',
                reportOnly: true,
              },
            },
            { name: 'Claude', size: 1.7 },
            { name: 'Code', size: 2.02 },
            { name: 'other apps', size: 5.95 },
          ],
        },
        { name: 'AppData\\LocalLow', size: 2.3 },
        { name: '.foundry', size: 10.9 },
        {
          name: '.rustup\\toolchains',
          size: 3.77,
          match: {
            rule: 'dev.rustup_toolchains',
            what: 'installed Rust toolchains',
            risk: 'caution',
            recovery: 'redownload',
            command: 'rustup toolchain uninstall <old>',
            reportOnly: true,
          },
        },
        { name: '.claude', size: 3.15 },
        {
          name: '.cache\\solana',
          size: 3.14,
          match: { rule: 'dev.solana_tools', what: 'Solana platform tools, a copy per version', risk: 'safe', recovery: 'redownload' },
        },
        {
          name: '.bun\\install\\cache',
          size: 2.81,
          match: {
            rule: 'dev.bun_cache',
            what: "bun's global package cache",
            risk: 'safe',
            recovery: 'redownload',
            command: 'bun pm cache rm',
          },
        },
        { name: 'your files', size: 1.97 },
        { name: '.vscode', size: 1.67 },
        {
          name: '.nuget\\packages',
          size: 0.986,
          match: {
            rule: 'dev.nuget',
            what: 'NuGet packages and the HTTP cache',
            risk: 'safe',
            recovery: 'redownload',
            command: 'dotnet nuget locals all --clear',
          },
        },
        { name: '.cargo', size: 0.479 },
        { name: 'other', size: 1.48 },
      ],
    },
    {
      name: 'Windows',
      children: [
        { name: 'Installer', size: 12.9 },
        { name: 'SoftwareDistribution', size: 9.81 },
        { name: 'WinSxS', size: 8.68 },
        { name: 'System32', size: 6.36 },
        { name: 'assembly', size: 3.14 },
        { name: 'other', size: 2.91 },
      ],
    },
    { name: 'Program Files', size: 7.96 },
    { name: 'Program Files (x86)', size: 7.26 },
    { name: 'hiberfil.sys', size: 6.38 },
    { name: 'ProgramData', size: 5.69 },
    { name: 'other', size: 0.91 },
  ],
}

export type Grew = { path: string; delta: string; note: string; up: boolean }

export const DIFF = {
  from: 8,
  to: 12,
  span: '18 Sep to 22 Sep 2026',
  change: '-10.3 GB',
  free: '10.6 GB to 17.4 GB free',
  rows: [
    { path: 'C:\\pagefile.sys', delta: '-12 GB', note: 'gone', up: false },
    { path: 'C:\\Windows\\WinSxS', delta: '-2.22 GB', note: 'other entries here', up: false },
    { path: '~\\AppData\\Local\\ms-playwright', delta: '+708 MB', note: 'new', up: true },
    { path: '~\\.bun\\install\\cache', delta: '+429 MB', note: '2.39 GB to 2.81 GB', up: true },
    { path: '~\\.vscode\\extensions\\ms-dotnettools.csharp-2.160.4', delta: '+329 MB', note: 'new', up: true },
    { path: '~\\AppData\\Local\\NuGet\\v3-cache', delta: '+270 MB', note: 'new', up: true },
    { path: 'C:\\swapfile.sys', delta: '+240 MB', note: '16 MB to 256 MB', up: true },
    { path: '~\\.vscode\\extensions\\anthropic.claude-code-2.1.274', delta: '-234 MB', note: 'gone', up: false },
  ] as Grew[],
}

export type Finding = { name: string; size: string; risk: Risk; command: string; admin?: boolean }

export const AUDIT = {
  date: '1 Oct 2026',
  probes: 21,
  answered: 16,
  time: '4.1 s',
  findings: [
    {
      name: 'Page file',
      size: '15 GB',
      risk: 'caution',
      command: 'System Properties > Advanced > Performance > Virtual memory',
    },
    {
      name: 'Windows Update cache',
      size: '9.72 GB',
      risk: 'safe',
      command: 'net stop wuauserv && rd /s /q C:\\Windows\\SoftwareDistribution\\Download',
      admin: true,
    },
    { name: 'Temp directories', size: '9.2 GB', risk: 'safe', command: "Remove-Item -Recurse -Force ($env:TEMP + '\\*')" },
    { name: 'Hibernation file', size: '6.38 GB', risk: 'caution', command: 'powercfg /hibernate /type reduced', admin: true },
    { name: 'Browser caches', size: '1.38 GB', risk: 'safe', command: 'Clear browsing data > Cached images and files' },
  ] as Finding[],
}

export const RELEASE = {
  version: '0.2.3',
  date: '1 Oct 2026',
  repo: 'https://github.com/egorb2302/pathmemo',
  x64: 'https://github.com/egorb2302/pathmemo/releases/download/v0.2.3/pathmemo-0.2.3-win-x64.zip',
  arm64: 'https://github.com/egorb2302/pathmemo/releases/download/v0.2.3/pathmemo-0.2.3-win-arm64.zip',
  sums: 'https://github.com/egorb2302/pathmemo/releases/download/v0.2.3/SHA256SUMS.txt',
  x64Sha: 'bbb65a7192ea8afd2aee765cb39918ad8892aef6c1c9d9b002e6372ef2b5eabc',
}
