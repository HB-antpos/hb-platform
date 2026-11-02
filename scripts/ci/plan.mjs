import { appendFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const scriptDirectory = dirname(fileURLToPath(import.meta.url))
const repositoryRoot = resolve(scriptDirectory, '..', '..')

export const ALL_COMPONENTS = Object.freeze([
  'backend',
  'web',
  'mobile',
  'pos-ipad',
  'pos-handheld',
  'pos-wpf',
  'pos-contract',
  'supplier-extension',
  'supplier-safari',
  'antpos-web',
])

export const PROFILE_BUDGETS = Object.freeze({
  // PR 仍由 required 严格执行 15 分钟端到端门禁；plan 的 4 分钟仅用于容纳 checkout 抖动。
  pr: Object.freeze({ budgetSeconds: 15 * 60, matrixTimeoutMinutes: 12 }),
  // weekly 还要经过最终聚合；40 分钟矩阵为两个聚合 job 留出余量。
  weekly: Object.freeze({ budgetSeconds: 45 * 60, matrixTimeoutMinutes: 40 }),
})

const DOC_PATTERNS = [
  /^docs\//,
  /(?:^|\/)README(?:\.[^/]*)?$/i,
  /(?:^|\/)CHANGELOG(?:\.[^/]*)?$/i,
  /(?:^|\/)LICENSE(?:\.[^/]*)?$/i,
  /\.(?:md|mdx|txt)$/i,
  /^\.github\/(?:ISSUE_TEMPLATE|PULL_REQUEST_TEMPLATE)\//,
]

const CI_PATTERNS = [
  /^\.github\/workflows\//,
  /^scripts\/ci\//,
  /^scripts\/test-all\.sh$/,
  /^\.node-version$/,
  /^global\.json$/,
  /^services\/backend\/global\.json$/,
  /^AGENTS\.md$/,
]

const COMPONENT_PATTERNS = [
  ['backend', /^services\/backend\//],
  ['pos-wpf', /^services\/backend\/BlazorApp\.Shared\//],
  ['web', /^apps\/web\//],
  ['mobile', /^apps\/mobile\//],
  ['pos-ipad', /^apps\/pos-ipad\//],
  ['pos-handheld', /^apps\/pos-handheld\//],
  ['pos-wpf', /^apps\/pos-wpf\//],
  ['supplier-extension', /^apps\/supplier-order-extension\//],
  ['supplier-safari', /^apps\/supplier-order-safari-extension\//],
  ['antpos-web', /^apps\/antpos-web\//],
]

const POS_SHARED_PATTERNS = [
  /^packages\/pos-[^/]+\//,
  /^scripts\/pos-shared\//,
  /^patches\//,
  /^package(?:-lock)?\.json$/,
  /^eslint\.config\.mjs$/,
  /^tsconfig\.pos-packages\.json$/,
]

function addDependencies(selected) {
  if (selected.has('pos-wpf')) {
    selected.add('pos-contract')
  }
  if (selected.has('pos-ipad') || selected.has('pos-handheld')) {
    selected.add('pos-contract')
  }
  if (selected.has('supplier-extension')) {
    selected.add('supplier-safari')
  }
  return selected
}

export function selectComponents(files, { full = false } = {}) {
  if (full) {
    return new Set(ALL_COMPONENTS)
  }

  const selected = new Set()
  let unknownNonDocumentPath = false

  for (const rawFile of files) {
    const file = rawFile.replaceAll('\\', '/').replace(/^\.\//, '')
    if (!file) {
      continue
    }
    if (CI_PATTERNS.some((pattern) => pattern.test(file))) {
      return new Set(ALL_COMPONENTS)
    }

    let matched = false
    if (POS_SHARED_PATTERNS.some((pattern) => pattern.test(file))) {
      selected.add('pos-ipad')
      selected.add('pos-handheld')
      matched = true
    }
    for (const [component, pattern] of COMPONENT_PATTERNS) {
      if (pattern.test(file)) {
        selected.add(component)
        matched = true
      }
    }

    if (file === 'test-fixtures/shared-held-orders/example.json' ||
        file.startsWith('test-fixtures/shared-held-orders/')) {
      selected.add('pos-ipad')
      selected.add('pos-wpf')
      matched = true
    }

    if (file === 'apps/web/src/components/SupplierOrderingExtensionEntry/supplierOrderingExtensionLogic.ts') {
      selected.add('supplier-extension')
      matched = true
    }
    if (file === 'services/backend/BlazorApp.Shared/DTOs/BrowserExtensionDtos.cs') {
      selected.add('supplier-extension')
      matched = true
    }

    if (!matched && !DOC_PATTERNS.some((pattern) => pattern.test(file))) {
      unknownNonDocumentPath = true
    }
  }

  return unknownNonDocumentPath ? new Set(ALL_COMPONENTS) : addDependencies(selected)
}

function matrix(entries, timeout, runner) {
  const scheduledEntries = entries.length > 0
    ? entries.map((entry) => ({ ...entry, runner }))
    : [{ component: 'noop', runner: 'ubuntu-24.04' }]
  return {
    include: scheduledEntries.map((entry) => ({
      ...entry,
      timeout: entry.timeout ?? timeout,
    })),
  }
}

export function buildMatrices(selected, { timeout = 15 } = {}) {
  const linuxNode = []
  const linuxDotnet = []
  const windows = []
  const macos = []
  const android = []

  for (const component of ALL_COMPONENTS) {
    if (!selected.has(component)) {
      continue
    }
    if (['web', 'mobile', 'pos-ipad', 'pos-handheld', 'supplier-extension', 'antpos-web'].includes(component)) {
      linuxNode.push({ component })
    }
    if (component === 'backend') {
      linuxDotnet.push({ component: 'backend' })
    }
    if (component === 'pos-wpf') {
      linuxDotnet.push({ component: 'pos-api' })
      // 分片按类名前缀切，目标是各片测试墙钟接近：T-Z 用例极少并入 A-H；
      // S 里 SettingsFirstScreenReceiptDevice/Viewport 两组视图运行时测试同属串行集合、合计约 200 秒，
      // 单独成片，避免 client-s-other 逼近 12 分钟超时。
      for (const shard of [
        'client-a-b-d-h-t-z',
        'client-c-card',
        'client-c-other',
        'client-i-k-m-n',
        'client-l-linkly',
        'client-l-other',
        'client-o-r',
        'client-s-shared',
        'client-s-settings-device-viewport',
        'client-s-other',
        'ui',
      ]) {
        windows.push({ component: 'pos-wpf', shard })
      }
    }
    if (component === 'pos-contract') {
      linuxDotnet.push({ component: 'pos-contract' })
    }
    if (component === 'pos-ipad') {
      macos.push({ component: 'pos-ipad-native' })
    }
    if (component === 'mobile') {
      android.push({ component: 'mobile-android' })
    }
    if (component === 'pos-handheld') {
      macos.push({
        component: 'pos-handheld-native',
        // PR 端到端仍由 required gate 限制为 15 分钟；给较大的手持端原生图留 2 分钟收尾。
        timeout: timeout === PROFILE_BUDGETS.pr.matrixTimeoutMinutes ? 14 : timeout,
      })
      android.push({
        component: 'pos-handheld-android',
        // 冷构建主体可在 12 分钟内完成；额外 2 分钟只留给 Gradle cache 的 post-job 收尾。
        timeout: timeout === PROFILE_BUDGETS.pr.matrixTimeoutMinutes ? 14 : timeout,
      })
    }
    if (component === 'supplier-safari') {
      macos.push({ component: 'supplier-safari' })
    }
  }

  return {
    linuxNode: matrix(linuxNode, timeout, 'ubuntu-24.04'),
    linuxDotnet: matrix(linuxDotnet, timeout, 'ubuntu-24.04'),
    windows: matrix(windows, timeout, 'windows-2025'),
    macos: matrix(macos, timeout, 'macos-26'),
    android: matrix(android, timeout, 'ubuntu-24.04'),
  }
}

function parseArgs(argv) {
  const options = { full: false, profile: 'pr' }
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index]
    if (arg === '--full') {
      options.full = true
    } else if (arg === '--base' || arg === '--head' || arg === '--component' || arg === '--profile') {
      const value = argv[index + 1]
      if (!value) {
        throw new Error(`${arg} 缺少参数`)
      }
      options[arg.slice(2)] = value
      index += 1
    } else {
      throw new Error(`未知参数: ${arg}`)
    }
  }
  if (!['pr', 'weekly'].includes(options.profile)) {
    throw new Error(`未知 profile: ${options.profile}`)
  }
  return options
}

export function changedFiles(base, head, cwd = repositoryRoot) {
  if (!base || !head) {
    throw new Error('PR 增量规划必须同时提供 --base 与 --head')
  }
  // rename 必须展开成删除与新增，确保源组件和目标组件都进入 CI。
  const output = execFileSync(
    'git',
    ['diff', '--no-renames', '--name-only', '-z', `${base}...${head}`],
    { cwd },
  )
  return output.toString('utf8').split('\0').filter(Boolean)
}

function emit(name, value) {
  const serialized = typeof value === 'string' ? value : JSON.stringify(value)
  process.stdout.write(`${name}=${serialized}\n`)
  if (process.env.GITHUB_OUTPUT) {
    appendFileSync(process.env.GITHUB_OUTPUT, `${name}=${serialized}\n`, 'utf8')
  }
}

function main() {
  const options = parseArgs(process.argv.slice(2))
  let selected
  if (options.component) {
    if (options.component === 'all') {
      selected = new Set(ALL_COMPONENTS)
    } else if (ALL_COMPONENTS.includes(options.component)) {
      selected = addDependencies(new Set([options.component]))
    } else {
      throw new Error(`未知组件: ${options.component}`)
    }
  } else {
    const files = options.full ? [] : changedFiles(options.base, options.head)
    selected = selectComponents(files, { full: options.full })
  }

  const budget = PROFILE_BUDGETS[options.profile]
  const matrices = buildMatrices(selected, { timeout: budget.matrixTimeoutMinutes })
  emit('profile', options.profile)
  emit('budget_seconds', budget.budgetSeconds)
  emit('components', [...selected])
  // 质量报告复用已选组件的结果，避免启动第二套 build/test 或等待未执行的组件。
  emit('quality_lanes', ['backend', 'web', 'pos-ipad', 'pos-handheld'].filter((component) => selected.has(component)))
  emit('linux_node_matrix', matrices.linuxNode)
  emit('linux_dotnet_matrix', matrices.linuxDotnet)
  emit('windows_matrix', matrices.windows)
  emit('macos_matrix', matrices.macos)
  emit('android_matrix', matrices.android)
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main()
}
