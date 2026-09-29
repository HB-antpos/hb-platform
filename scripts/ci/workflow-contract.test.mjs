import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import {
  chmodSync,
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const workflowDirectoryPath = new URL('../../.github/workflows/', import.meta.url)
const workflowPath = new URL('../../.github/workflows/pr-ci.yml', import.meta.url)
const wpfWorkflowPath = new URL('../../.github/workflows/wpf-inno-smoke-build.yml', import.meta.url)
const rootPackagePath = new URL('../../package.json', import.meta.url)
const nodeRunnerPath = new URL('./run-node-component.sh', import.meta.url)
const dotnetRunnerPath = new URL('./run-dotnet-component.sh', import.meta.url)
const macosRunnerPath = new URL('./run-macos-component.sh', import.meta.url)
const androidRunnerPath = new URL('./run-android-component.sh', import.meta.url)
const windowsRunnerPath = new URL('./run-windows-component.ps1', import.meta.url)
const weeklySqlRunnerPath = new URL('./run-weekly-sql.sh', import.meta.url)
const schemaSqlRunnerPath = new URL('./run-schema-sql.sh', import.meta.url)
const testAllPath = new URL('../test-all.sh', import.meta.url)
const mobilePackagePath = new URL('../../apps/mobile/package.json', import.meta.url)
const wpfClientTestsProjectPath = new URL('../../apps/pos-wpf/tests/Hbpos.Client.Tests/Hbpos.Client.Tests.csproj', import.meta.url)
const xunitRunnerConfigPath = new URL('../../apps/pos-wpf/tests/Hbpos.Client.Tests/xunit.runner.json', import.meta.url)
const wpfSolutionPath = new URL('../../apps/pos-wpf/hbpos_win.slnx', import.meta.url)
const transactionHistoryTestsPath = new URL(
  '../../apps/pos-wpf/tests/Hbpos.Client.Tests/TransactionHistoryViewModelTests.cs',
  import.meta.url,
)
const posPricingPerformanceTestPaths = [
  new URL('../../apps/pos-ipad/src/features/sales/domain/pricing-cart.test.ts', import.meta.url),
  new URL('../../apps/pos-handheld/src/features/sales/domain/pricing-cart.test.ts', import.meta.url),
]
const fixedXcodePath = '/Applications/Xcode_26.5.app/Contents/Developer'
const fixedXcodeAvailable = existsSync(join(fixedXcodePath, 'usr/bin/xcodebuild'))

const expectedLanes = [
  'node:web',
  'node:mobile',
  'node:pos-ipad',
  'node:pos-handheld',
  'node:supplier-extension',
  'node:antpos-web',
  'dotnet:backend',
  'dotnet:pos-api',
  'dotnet:pos-contract',
  'windows:pos-wpf',
  'macos:pos-ipad-native',
  'macos:pos-handheld-native',
  'macos:supplier-safari',
  'android:pos-handheld-android',
]

function workflowJobBlock(source, jobName) {
  const match = source.match(new RegExp(`^  ${jobName}:\\n([\\s\\S]*?)(?=^  [a-zA-Z_][a-zA-Z0-9_]*:\\n|$(?![\\s\\S]))`, 'm'))
  assert.ok(match, `缺少 workflow job: ${jobName}`)
  return match[0]
}

function collectWorkflowUses() {
  return readdirSync(workflowDirectoryPath, { withFileTypes: true })
    .filter((entry) => entry.isFile() && /\.ya?ml$/i.test(entry.name))
    .sort((left, right) => left.name.localeCompare(right.name))
    .flatMap((entry) => {
      const source = readFileSync(new URL(entry.name, workflowDirectoryPath), 'utf8')
      return source.split(/\r?\n/).flatMap((line, index) => {
        const match = line.match(/^\s*(?:-\s*)?uses:\s*(['"]?)([^'"\s#]+)\1(?:\s+#.*)?\s*$/)
        return match
          ? [{ action: match[2], file: entry.name, line: index + 1, sourceLine: line }]
          : []
      })
    })
}

function writeExecutable(path, source) {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, source)
  chmodSync(path, 0o755)
}

function runMacosRunner({ xcodeVersion = '26.5', useValidDeveloperDir = true } = {}) {
  const temporaryRoot = mkdtempSync(join(tmpdir(), 'hb-macos-runner-'))
  const fakeBin = join(temporaryRoot, 'bin')
  const environmentDeveloperDir = join(temporaryRoot, 'EnvironmentXcode.app/Contents/Developer')
  const selectedDeveloperDir = join(temporaryRoot, 'SelectedXcode.app/Contents/Developer')
  const invalidDeveloperDir = join(temporaryRoot, 'InvalidXcode.app/Contents/Developer')
  const xcodeVersionScript = `#!/usr/bin/env bash\nprintf '%s\\n' 'Xcode ${xcodeVersion}'\nprintf '%s\\n' 'Build version TEST'\n`

  writeExecutable(join(environmentDeveloperDir, 'usr/bin/xcodebuild'), xcodeVersionScript)
  writeExecutable(join(selectedDeveloperDir, 'usr/bin/xcodebuild'), xcodeVersionScript)
  mkdirSync(invalidDeveloperDir, { recursive: true })
  writeExecutable(join(fakeBin, 'xcodebuild'), xcodeVersionScript)
  writeExecutable(join(fakeBin, 'xcode-select'), '#!/usr/bin/env bash\nprintf \'%s\\n\' "$FAKE_XCODE_SELECT_PATH"\n')
  writeExecutable(join(fakeBin, 'sudo'), '#!/usr/bin/env bash\necho "不应调用 sudo" >&2\nexit 97\n')
  for (const command of ['npm', 'node', 'git']) {
    writeExecutable(join(fakeBin, command), '#!/usr/bin/env bash\nexit 0\n')
  }

  let result
  try {
    result = spawnSync('bash', [fileURLToPath(macosRunnerPath), 'supplier-safari'], {
      encoding: 'utf8',
      env: {
        ...process.env,
        DEVELOPER_DIR: useValidDeveloperDir ? environmentDeveloperDir : invalidDeveloperDir,
        FAKE_XCODE_SELECT_PATH: selectedDeveloperDir,
        PATH: `${fakeBin}:${process.env.PATH ?? '/usr/bin:/bin'}`,
      },
      timeout: 10_000,
    })
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }

  return { environmentDeveloperDir, result, selectedDeveloperDir }
}

function runMacosProfile({ component, profile, includeProfile = true }) {
  const temporaryRoot = mkdtempSync(join(tmpdir(), 'hb-macos-profile-'))
  const fakeBin = join(temporaryRoot, 'bin')
  const selectedDeveloperDir = join(temporaryRoot, 'SelectedXcode.app/Contents/Developer')
  const commandLogPath = join(temporaryRoot, 'commands.log')
  const logCommand = '#!/usr/bin/env bash\nprintf \'%s %s\\n\' "$(basename "$0")" "$*" >> "$FAKE_COMMAND_LOG"\n'

  writeExecutable(join(selectedDeveloperDir, 'usr/bin/xcodebuild'), '#!/usr/bin/env bash\nexit 0\n')
  writeExecutable(join(fakeBin, 'npm'), `${logCommand}exit 0\n`)
  writeExecutable(join(fakeBin, 'node'), `${logCommand}exit 0\n`)
  writeExecutable(
    join(fakeBin, 'xcodebuild'),
    `${logCommand}if [[ "\${1:-}" == '-version' ]]; then\n  printf '%s\\n' 'Xcode 26.5' 'Build version TEST'\nfi\n`,
  )
  writeExecutable(join(fakeBin, 'xcode-select'), '#!/usr/bin/env bash\nprintf \'%s\\n\' "$FAKE_XCODE_SELECT_PATH"\n')

  const environment = {
    ...process.env,
    DEVELOPER_DIR: selectedDeveloperDir,
    FAKE_COMMAND_LOG: commandLogPath,
    FAKE_XCODE_SELECT_PATH: selectedDeveloperDir,
    PATH: `${fakeBin}:${process.env.PATH ?? '/usr/bin:/bin'}`,
  }
  if (includeProfile) {
    environment.CI_PROFILE = profile
  } else {
    delete environment.CI_PROFILE
  }

  let result
  let commands
  try {
    result = spawnSync('bash', [fileURLToPath(macosRunnerPath), component], {
      encoding: 'utf8',
      env: environment,
      timeout: 10_000,
    })
    commands = existsSync(commandLogPath) ? readFileSync(commandLogPath, 'utf8') : ''
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }

  return { commands, result }
}

function runTestAll({ kernelName, osName = '' }) {
  const temporaryRoot = mkdtempSync(join(tmpdir(), 'hb-test-all-'))
  const fakeBin = join(temporaryRoot, 'bin')
  const copiedTestAllPath = join(temporaryRoot, 'scripts/test-all.sh')

  mkdirSync(dirname(copiedTestAllPath), { recursive: true })
  copyFileSync(testAllPath, copiedTestAllPath)
  chmodSync(copiedTestAllPath, 0o755)
  for (const runner of [
    'run-node-component.sh',
    'run-dotnet-component.sh',
    'run-macos-component.sh',
    'run-android-component.sh',
  ]) {
    writeExecutable(join(temporaryRoot, 'scripts/ci', runner), '#!/usr/bin/env bash\nexit 0\n')
  }
  writeExecutable(join(fakeBin, 'pwsh'), '#!/usr/bin/env bash\nexit 0\n')
  writeExecutable(join(fakeBin, 'uname'), '#!/usr/bin/env bash\nprintf \'%s\\n\' "$FAKE_UNAME"\n')

  let result
  try {
    result = spawnSync('bash', [copiedTestAllPath], {
      encoding: 'utf8',
      env: {
        ...process.env,
        FAKE_UNAME: kernelName,
        OS: osName,
        PATH: `${fakeBin}:${process.env.PATH ?? '/usr/bin:/bin'}`,
      },
      timeout: 10_000,
    })
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true })
  }

  return result
}

test('PR workflow 每个 PR 都启动，并按 Brisbane 周日 02:23 周更全量', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const linuxNode = workflowJobBlock(source, 'linux_node')
  assert.match(source, /pull_request:/)
  assert.match(source, /cron:\s*['"]23 16 \* \* 6['"]/) // UTC Saturday 16:23
  assert.doesNotMatch(source, /^env:\s*\n\s+TZ:\s*Australia\/Brisbane\s*$/m)
  assert.match(linuxNode, /^\s{6}TZ:\s*Australia\/Brisbane\s*$/m)
  assert.doesNotMatch(source, /^\s{2}paths:/m)
  assert.doesNotMatch(source, /nightly/i)
})

test('只有 pull_request 能产出分支保护使用的 required 检查名', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const required = workflowJobBlock(source, 'required')
  const requiredNameOccurrences = readdirSync(workflowDirectoryPath, { withFileTypes: true })
    .filter((entry) => entry.isFile() && /\.ya?ml$/i.test(entry.name))
    .flatMap((entry) => {
      const workflowSource = readFileSync(new URL(entry.name, workflowDirectoryPath), 'utf8')
      return [...workflowSource.matchAll(/PR CI \/ required/g)].map(() => entry.name)
    })

  assert.deepEqual(requiredNameOccurrences, ['pr-ci.yml'])
  assert.doesNotMatch(required, /^    name:\s*PR CI \/ required\s*$/m)
  assert.match(
    required,
    /name:\s*>-\s*\n\s*\$\{\{\s*github\.event_name == 'pull_request'\s*\n\s*&& 'PR CI \/ required'\s*\n\s*\|\| 'Non-PR CI \/ matrix required'\s*\}\}/,
  )
})

test('Backend PR 的真实 Schema SQL 独立执行并纳入 required 门禁', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const schemaSql = workflowJobBlock(source, 'schema_sql')
  const required = workflowJobBlock(source, 'required')
  const runner = readFileSync(schemaSqlRunnerPath, 'utf8')

  assert.match(schemaSql, /needs:\s*plan/)
  assert.match(schemaSql, /contains\(fromJSON\(needs\.plan\.outputs\.components\), 'backend'\)/)
  assert.match(schemaSql, /run-schema-sql\.sh/)
  assert.match(schemaSql, /HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION:/)
  assert.match(schemaSql, /docker run/)
  assert.match(schemaSql, /docker rm --force/)
  assert.match(required, /- schema_sql/)
  assert.match(required, /"schema_sql":"\$\{\{ needs\.schema_sql\.result \}\}"/)
  assert.match(runner, /FullyQualifiedName~BlazorApp\.Api\.Tests\.SchemaMigrationSqlServerIntegrationTests/)
  assert.match(runner, /schema-migration-sql\.trx/)
  assert.match(runner, /'PR schema migration SQL tests'\s+\\\n\s+10/)
  assert.match(
    runner,
    /services\/backend\/BlazorApp\.MobileDeviceActivation\.Tests\/BlazorApp\.MobileDeviceActivation\.Tests\.csproj/,
  )
  assert.match(
    runner,
    /FullyQualifiedName~BlazorApp\.MobileDeviceActivation\.Tests\.MobileDeviceActivationSchemaSqlServerIntegrationTests/,
  )
  assert.match(runner, /mobile-device-activation-schema-sql\.trx/)
  assert.match(runner, /'PR Mobile device activation schema SQL test'\s+\\\n\s+1/)
  assert.doesNotMatch(runner, /docker ps --filter/)
})

test('PR/weekly 使用 15/45 分钟端到端预算并为稳定 gate 预留时间', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const plan = workflowJobBlock(source, 'plan')
  const required = workflowJobBlock(source, 'required')
  const weeklyRequired = workflowJobBlock(source, 'weekly_required')
  assert.match(source, /timeout-minutes:\s*\$\{\{ matrix\.timeout \}\}/)
  assert.match(source, /budget_seconds:\s*\$\{\{ steps\.plan\.outputs\.budget_seconds \}\}/)
  assert.equal([...source.matchAll(/CI_RUN_ATTEMPT:\s*\$\{\{ github\.run_attempt \}\}/g)].length, 2)
  assert.equal([...source.matchAll(/CI_RUN_BUDGET_SECONDS:\s*\$\{\{ needs\.plan\.outputs\.budget_seconds \}\}/g)].length, 2)
  for (const gate of [required, weeklyRequired]) {
    assert.match(gate, /GITHUB_TOKEN:\s*\$\{\{ github\.token \}\}/)
    assert.match(gate, /CI_API_URL:\s*\$\{\{ github\.api_url \}\}/)
    assert.match(gate, /CI_REPOSITORY:\s*\$\{\{ github\.repository \}\}/)
    assert.match(gate, /CI_RUN_ID:\s*\$\{\{ github\.run_id \}\}/)
    assert.match(gate, /CI_RUN_ATTEMPT:\s*\$\{\{ github\.run_attempt \}\}/)
    assert.match(gate, /CI_RUN_BUDGET_SECONDS:\s*\$\{\{ needs\.plan\.outputs\.budget_seconds \}\}/)
  }
  assert.equal(plan.match(/^    timeout-minutes:/gm)?.length, 1)
  assert.match(plan, /^    timeout-minutes:[ \t]*4[ \t]*$/m)
  assert.match(source, /timeout-minutes:\s*40/g)
  assert.match(required, /&& 'PR CI \/ required'/)
  assert.match(source, /node scripts\/ci\/required-gate\.mjs/)
  assert.match(source, /name:\s*Weekly full \/ required/)
  assert.match(source, /weekly_required:[\s\S]*?needs:\s*\n\s*- plan\s*\n/)
})

test('POS CI 只消费根 workspace lock，并由各 job 单次安装后调用 workspace 脚本', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const linuxNode = workflowJobBlock(source, 'linux_node')
  const nodeRunner = readFileSync(nodeRunnerPath, 'utf8')
  const rootPackage = JSON.parse(readFileSync(rootPackagePath, 'utf8'))
  const dotnetRunner = readFileSync(dotnetRunnerPath, 'utf8')
  const macosRunner = readFileSync(macosRunnerPath, 'utf8')
  const androidRunner = readFileSync(androidRunnerPath, 'utf8')

  assert.doesNotMatch(source, /apps\/pos-(?:ipad|handheld)\/package-lock\.json/)
  assert.match(
    linuxNode,
    /fetch-depth:\s*\$\{\{\s*\(matrix\.component == 'pos-ipad' \|\| matrix\.component == 'pos-handheld'\) && '0' \|\| '1'\s*\}\}/,
    '双端 POS Node lane 必须保留完整基线历史，才能直接验证迁移归属与语义哈希',
  )
  for (const jobName of ['linux_node', 'linux_dotnet', 'macos', 'android']) {
    assert.match(workflowJobBlock(source, jobName), /cache-dependency-path:[\s\S]*?package-lock\.json/)
  }

  for (const runner of [nodeRunner, dotnetRunner, macosRunner, androidRunner]) {
    assert.doesNotMatch(runner, /npm --prefix apps\/pos-(?:ipad|handheld) ci/)
    assert.match(runner, /npm ci --no-audit --no-fund/)
  }
  assert.equal(nodeRunner.match(/npm run test:pos-shared-ci/g)?.length, 1)
  const sharedCiScript = rootPackage.scripts?.['test:pos-shared-ci']
  assert.equal(typeof sharedCiScript, 'string')
  for (const command of [
    'check:pos-workspace-resolution',
    'check:pos-shared-ownership',
    'check:pos-package-boundaries',
    'test:react-native-scheduler',
    'test:expo-audio-android-routing',
    'test:pos-packages',
    'lint:pos-packages',
    'typecheck:pos',
  ]) {
    assert.match(sharedCiScript, new RegExp(`npm run ${command.replaceAll(':', '\\:')}`))
  }
  assert.doesNotMatch(sharedCiScript, /test:hbpos-client/)
  assert.match(nodeRunner, /npm run typecheck --workspace=@hb\/pos-ipad/)
  assert.match(nodeRunner, /npm run test:ci --workspace=@hb\/pos-handheld/)
  assert.match(dotnetRunner, /npm run test:hbpos-client/)
  assert.match(macosRunner, /npm run prebuild:ios --workspace=@hb\/pos-(?:ipad|handheld) -- --clean/)
  assert.match(androidRunner, /npm run prebuild:android --workspace=@hb\/pos-handheld -- --clean/)
})

test('托管 runner 隔离 WPF 分片，并避免原生多架构与 Android 内存争抢', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const windows = workflowJobBlock(source, 'windows')
  const macos = workflowJobBlock(source, 'macos')
  const android = workflowJobBlock(source, 'android')
  const androidRunner = readFileSync(androidRunnerPath, 'utf8')
  const macosRunner = readFileSync(macosRunnerPath, 'utf8')
  const windowsRunner = readFileSync(windowsRunnerPath, 'utf8')
  const wpfProject = readFileSync(wpfClientTestsProjectPath, 'utf8')
  const xunitRunnerConfig = JSON.parse(readFileSync(xunitRunnerConfigPath, 'utf8'))

  // conservative：已启动的用例真正结束后才启动新用例，避免阻塞写法占满 4 个调度线程后续延排队、超时误报。
  assert.deepEqual(xunitRunnerConfig, {
    parallelizeAssembly: true,
    parallelizeTestCollections: true,
    maxParallelThreads: 4,
    parallelAlgorithm: 'conservative',
  })
  assert.match(wpfProject, /<None Update="xunit\.runner\.json" CopyToOutputDirectory="PreserveNewest" \/>/)
  assert.match(windows, /\$\{\{ matrix\.shard \|\| 'noop' \}\}/)
  assert.match(windows, /-Shard '\$\{\{ matrix\.shard \|\| 'noop' \}\}'/)
  assert.match(windowsRunner, /client-a-b-d-h/)
  assert.match(windowsRunner, /client-c-card/)
  assert.match(windowsRunner, /client-c-other/)
  assert.match(windowsRunner, /client-l-linkly/)
  assert.match(windowsRunner, /client-l-other/)
  assert.match(windowsRunner, /client-s-shared/)
  assert.match(windowsRunner, /client-s-other/)
  assert.match(windowsRunner, /client-t-z/)
  assert.match(windowsRunner, /\$clientTestNamespace = 'Hbpos\.Client\.Tests\.'/)
  assert.match(windowsRunner, /FullyQualifiedName~\$clientTestNamespace/)
  assert.match(windowsRunner, /WPF client tests \(\$Shard\)/)
  assert.match(windowsRunner, /if \(\$Shard -eq 'all' -or \$Shard -eq 'ui'\)/)
  // ui 分片兼跑 RemoteStatus 测试，并用全量清单确认 client 测试恰好各属一个分片，防止漏片静默跳过。
  assert.match(windowsRunner, /Hbpos\.RemoteStatus\.Tests\/Hbpos\.RemoteStatus\.Tests\.csproj/)
  assert.match(windowsRunner, /--list-tests/)
  assert.match(windowsRunner, /^\s+Assert-ClientShardCoverage\s*$/m)
  // Windows lane 只构建 slnx，RemoteStatus 必须在其中，否则上面的测试没有产物可跑。
  const wpfSolution = readFileSync(wpfSolutionPath, 'utf8')
  assert.match(wpfSolution, /src\/Hbpos\.RemoteStatus\/Hbpos\.RemoteStatus\.csproj/)
  assert.match(wpfSolution, /tests\/Hbpos\.RemoteStatus\.Tests\/Hbpos\.RemoteStatus\.Tests\.csproj/)

  // PR 执行 prebuild 与原生互操作测试；完整 app 冷构建留给 weekly，并继续强制 arm64。
  assert.match(macos, /CI_PROFILE:\s*\$\{\{ needs\.plan\.outputs\.profile \}\}/)
  assert.match(macosRunner, /profile="\$\{CI_PROFILE:-weekly\}"/)
  assert.match(macosRunner, /if \[\[ "\$profile" == "weekly" \]\]; then[\s\S]*?xcodebuild/)
  assert.match(macosRunner, /PR profile 已完成 .*prebuild.*原生互操作测试/)
  assert.match(macos, /xcodebuild\(\) \{[\s\S]*?command xcodebuild "\$@" ARCHS=arm64 ONLY_ACTIVE_ARCH=YES COMPILER_INDEX_STORE_ENABLE=NO/)
  assert.match(macos, /export -f xcodebuild/)
  assert.doesNotMatch(macosRunner, /\.xcworkspace -list/)
  assert.match(macosRunner, /-quiet[\s\\]*\n[\s]*-showBuildTimingSummary/)

  // Android 保留既有四项 task，CI 构建只编译代表性的 arm64 ABI。
  assert.match(android, /bash\(\) \{[\s\S]*?command bash "\$@" --parallel --build-cache '-Dorg\.gradle\.jvmargs=-Xmx6g -XX:MaxMetaspaceSize=1g'/)
  assert.match(android, /"\$\{1:-\}" == 'apps\/pos-handheld\/android\/gradlew'/)
  assert.match(androidRunner, /-PreactNativeArchitectures=arm64-v8a/)
  assert.match(
    androidRunner,
    /bash apps\/pos-handheld\/android\/gradlew \\\n\s+-p apps\/pos-handheld\/android/,
  )
  assert.doesNotMatch(androidRunner, /bash \.\/gradlew/)
  assert.match(
    androidRunner,
    /\(\s*\n\s*cd apps\/pos-handheld\/android\s*\n\s*node --print "require\.resolve\('@sentry\/react-native\/package\.json'\)"[^\n]*\n\s*\)/,
  )
  for (const task of [
    ':hb-app-installer:testDebugUnitTest',
    ':hb-attendance-security:testDebugUnitTest',
    ':app:assembleDebug',
    ':app:lintDebug',
  ]) {
    assert.match(androidRunner, new RegExp(task.replaceAll(':', '\\:')))
  }
})

test('WPF 挂单夹具只捕获一次当前日期，避免跨午夜产生不一致', () => {
  const source = readFileSync(transactionHistoryTestsPath, 'utf8')

  assert.equal([...source.matchAll(/DateTime\.Today/g)].length, 1)
  assert.match(source, /private static readonly DateTimeOffset HeldFixtureTime = new\(DateTime\.Today\.AddHours\(9\)\);/)
  assert.match(source, /viewModel\.DateFrom = HeldFixtureDate;/)
  assert.match(source, /viewModel\.DateTo = HeldFixtureDate;/)
})

test('所有 workflow yml/yaml 的第三方 uses（含条件步骤）全部固定 40 位提交 SHA', () => {
  const uses = collectWorkflowUses()
  assert.ok(uses.length >= 20)
  assert.ok(uses.some(({ sourceLine }) => !/^\s*-\s*uses:/.test(sourceLine)), '必须覆盖条件步骤中的 uses')

  for (const { action, file, line } of uses) {
    if (action.startsWith('./') || action.startsWith('docker://')) {
      continue
    }
    assert.match(action, /^[^@]+@[0-9a-f]{40}$/, `${file}:${line} 未固定为 40 位提交 SHA`)
  }
})

test('WPF workflow 与 PR CI 使用相同的 checkout/setup-dotnet/upload-artifact SHA', () => {
  const source = readFileSync(wpfWorkflowPath, 'utf8')
  const expectedActions = [
    ['actions/checkout', '11d5960a326750d5838078e36cf38b85af677262', 1],
    ['actions/setup-dotnet', '67a3573c9a986a3f9c594539f4ab511d57bb3ce9', 1],
    ['actions/upload-artifact', 'ea165f8d65b6e75b540449e92b4886f43607fa02', 2],
  ]

  for (const [action, sha, count] of expectedActions) {
    const references = [...source.matchAll(new RegExp(`^\\s*uses:\\s*${action.replace('/', '\\/')}@([^\\s]+)$`, 'gm'))]
      .map((match) => match[1])
    assert.equal(references.length, count, `${action} 引用数量不符`)
    assert.deepEqual([...new Set(references)], [sha], `${action} 必须复用 PR CI 的固定 SHA`)
  }
})

test('macOS runner 按固定路径、DEVELOPER_DIR、xcode-select 顺序选择并只导出 Xcode 26.5 目录', () => {
  const source = readFileSync(macosRunnerPath, 'utf8')
  const fixedPathIndex = source.indexOf(fixedXcodePath)
  const developerDirIndex = source.indexOf('${DEVELOPER_DIR:-}', fixedPathIndex + 1)
  const xcodeSelectIndex = source.indexOf('xcode-select -p', developerDirIndex + 1)

  assert.ok(fixedPathIndex >= 0, '缺少固定 Xcode 26.5 路径')
  assert.ok(developerDirIndex > fixedPathIndex, '固定路径之后必须检查现有 DEVELOPER_DIR')
  assert.ok(xcodeSelectIndex > developerDirIndex, 'DEVELOPER_DIR 之后必须回退到 xcode-select -p')
  assert.deepEqual(
    [...source.matchAll(/^\s*export\s+([A-Za-z_][A-Za-z0-9_]*)=/gm)].map((match) => match[1]),
    ['DEVELOPER_DIR'],
  )
  assert.match(source, /xcodebuild -version/)
  assert.match(source, /26\.5/)
  assert.doesNotMatch(source, /\bsudo\b/)
  assert.doesNotMatch(source, /xcode-select\s+(?:-s|--switch)\b/)
})

test('macOS runner 在固定路径不可用时优先有效 DEVELOPER_DIR，再回退 xcode-select -p', {
  skip: fixedXcodeAvailable ? '本机固定 Xcode 26.5 路径存在，无法隔离验证后备顺序' : false,
}, () => {
  const fromEnvironment = runMacosRunner({ useValidDeveloperDir: true })
  assert.equal(fromEnvironment.result.error, undefined)
  assert.equal(fromEnvironment.result.status, 0, fromEnvironment.result.stderr)
  assert.match(fromEnvironment.result.stdout, new RegExp(`使用 Xcode 开发目录: ${fromEnvironment.environmentDeveloperDir}`))

  const fromXcodeSelect = runMacosRunner({ useValidDeveloperDir: false })
  assert.equal(fromXcodeSelect.result.error, undefined)
  assert.equal(fromXcodeSelect.result.status, 0, fromXcodeSelect.result.stderr)
  assert.match(fromXcodeSelect.result.stdout, new RegExp(`使用 Xcode 开发目录: ${fromXcodeSelect.selectedDeveloperDir}`))
})

test('macOS runner 拒绝非 Xcode 26.5', {
  skip: fixedXcodeAvailable ? '本机固定 Xcode 26.5 路径存在，无法注入错误版本' : false,
}, () => {
  const { result } = runMacosRunner({ xcodeVersion: '26.4' })
  assert.equal(result.error, undefined)
  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /要求 Xcode 26\.5/)
})

test('macOS runner 按 profile 执行原生 PR 快检与 weekly 完整构建', () => {
  const components = [
    ['pos-ipad-native', 'apps/pos-ipad/ios/HBPOS.xcworkspace', 'HBPOS'],
    ['pos-handheld-native', 'apps/pos-handheld/ios/HBPOSMobile.xcworkspace', 'HBPOSMobile'],
  ]

  for (const [component, workspace, scheme] of components) {
    const pr = runMacosProfile({ component, profile: 'pr' })
    assert.equal(pr.result.status, 0, pr.result.stderr)
    assert.match(pr.commands, /npm ci --no-audit --no-fund/)
    assert.match(pr.commands, /npm run prebuild:ios --workspace=@hb\/pos-(?:ipad|handheld) -- --clean/)
    assert.match(pr.commands, /node scripts\/ci\/test-inventory\.mjs --app pos-(?:ipad|handheld) --run native/)
    assert.doesNotMatch(pr.commands, /xcodebuild -workspace/)

    const weekly = runMacosProfile({ component, profile: 'weekly' })
    assert.equal(weekly.result.status, 0, weekly.result.stderr)
    assert.match(weekly.commands, new RegExp(`xcodebuild -workspace ${workspace} -scheme ${scheme}`))
    assert.match(weekly.commands, /CODE_SIGNING_ALLOWED=NO build/)

    const defaultProfile = runMacosProfile({ component, includeProfile: false })
    assert.equal(defaultProfile.result.status, 0, defaultProfile.result.stderr)
    assert.match(defaultProfile.commands, /xcodebuild -workspace/)
  }

  const invalid = runMacosProfile({ component: 'pos-ipad-native', profile: 'invalid' })
  assert.notEqual(invalid.result.status, 0)
  assert.equal(invalid.commands, '')
  assert.match(invalid.result.stderr, /未知 CI profile/)
})

test('test-all 在 Linux、macOS、Windows 都逐 lane 打印 executed/skipped 与原因', () => {
  const scenarios = [
    {
      name: 'Linux',
      kernelName: 'Linux',
      statuses: {
        'android:pos-handheld-android': 'executed',
        'windows:pos-wpf': 'skipped',
        'macos:pos-ipad-native': 'skipped',
        'macos:pos-handheld-native': 'skipped',
        'macos:supplier-safari': 'skipped',
      },
    },
    {
      name: 'macOS',
      kernelName: 'Darwin',
      statuses: {
        'android:pos-handheld-android': 'skipped',
        'windows:pos-wpf': 'skipped',
        'macos:pos-ipad-native': 'executed',
        'macos:pos-handheld-native': 'executed',
        'macos:supplier-safari': 'executed',
      },
    },
    {
      name: 'Windows',
      kernelName: 'Windows_NT',
      osName: 'Windows_NT',
      statuses: {
        'android:pos-handheld-android': 'skipped',
        'windows:pos-wpf': 'executed',
        'macos:pos-ipad-native': 'skipped',
        'macos:pos-handheld-native': 'skipped',
        'macos:supplier-safari': 'skipped',
      },
    },
  ]

  for (const scenario of scenarios) {
    const result = runTestAll(scenario)
    assert.equal(result.error, undefined, `${scenario.name}: ${result.error?.message ?? ''}`)
    assert.equal(result.status, 0, `${scenario.name}: ${result.stderr}`)
    const reports = result.stdout
      .split(/\r?\n/)
      .map((line) => line.match(/^(executed|skipped) lane=(\S+) reason=(.+)$/))
      .filter(Boolean)
      .map((match) => ({ status: match[1], lane: match[2], reason: match[3] }))
    const byLane = new Map(reports.map((report) => [report.lane, report]))

    assert.deepEqual([...byLane.keys()].sort(), [...expectedLanes].sort(), `${scenario.name}: lane 清单不完整`)
    assert.equal(reports.length, expectedLanes.length, `${scenario.name}: 每个 lane 必须且只能报告一次`)
    for (const lane of expectedLanes.slice(0, 9)) {
      assert.equal(byLane.get(lane)?.status, 'executed', `${scenario.name}: ${lane}`)
    }
    for (const [lane, expectedStatus] of Object.entries(scenario.statuses)) {
      assert.equal(byLane.get(lane)?.status, expectedStatus, `${scenario.name}: ${lane}`)
    }
    for (const report of reports) {
      assert.ok(report.reason.trim().length > 0, `${scenario.name}: ${report.lane} 缺少原因`)
    }
  }
})

test('Weekly SQL 使用独立容器且不会接入 LiveE2e', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const runner = readFileSync(weeklySqlRunnerPath, 'utf8')
  assert.match(source, /mcr\.microsoft\.com\/mssql\/server@sha256:[0-9a-f]{64}/)
  assert.match(source, /run-weekly-sql\.sh/)
  assert.match(source, /DEVICE_ACTIVATION_SQLSERVER_TEST_CONNECTION:/)
  assert.match(source, /HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION:/)
  assert.match(runner, /DEVICE_ACTIVATION_SQLSERVER_TEST_CONNECTION/)
  assert.match(runner, /HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION/)
  assert.match(runner, /FullyQualifiedName~BlazorApp\.Api\.Tests\.SchemaMigrationSqlServerIntegrationTests/)
  assert.match(runner, /weekly-schema-migration-sql\.trx/)
  assert.match(
    runner,
    /weekly-schema-migration-sql\.trx"\s+\\\n\s+'Weekly schema migration SQL tests'\s+\\\n\s+10/,
  )
  assert.match(runner, /apps\/pos-wpf\/tests\/Hbpos\.Api\.Tests\/Hbpos\.Api\.Tests\.csproj/)
  assert.match(runner, /weekly-device-activation-sql\.trx/)
  assert.doesNotMatch(source, /Category=LiveE2e/)
})

test('Mobile CI 同时执行 iOS 与 Android bundle 构建', () => {
  const source = readFileSync(workflowPath, 'utf8')
  const android = workflowJobBlock(source, 'android')
  const required = workflowJobBlock(source, 'required')
  const runner = readFileSync(nodeRunnerPath, 'utf8')
  const androidRunner = readFileSync(androidRunnerPath, 'utf8')
  const mobilePackage = JSON.parse(readFileSync(mobilePackagePath, 'utf8'))
  assert.match(runner, /npm --prefix apps\/mobile run build:ci/)
  assert.match(mobilePackage.scripts['build:ci'], /expo export --platform ios/)
  assert.match(mobilePackage.scripts['build:ci'], /expo export --platform android/)

  assert.match(android, /apps\/mobile\/package-lock\.json/)
  assert.match(android, /apps\/mobile\/android\/gradlew/)
  assert.match(required, /- android/)
  assert.match(required, /"android":"\$\{\{ needs\.android\.result \}\}"/)
  assert.match(androidRunner, /mobile-android\)/)
  assert.match(androidRunner, /npm --prefix apps\/mobile ci --no-audit --no-fund/)
  assert.match(
    androidRunner,
    /npx expo prebuild --platform android --no-install/,
  )
  const mobileAndroidCase = androidRunner.slice(
    androidRunner.indexOf('  mobile-android)'),
    androidRunner.indexOf('  pos-handheld-android)'),
  )
  assert.doesNotMatch(mobileAndroidCase, /prebuild[^\n]*--clean/)
  for (const task of [
    ':hb-app-installer:testDebugUnitTest',
    ':app:compileDebugKotlin',
    ':app:processDebugResources',
  ]) {
    assert.match(androidRunner, new RegExp(task.replaceAll(':', '\\:')))
  }
})

test('Web PR CI 以 manifest 构建并执行确定性 bundle 硬门禁', () => {
  const runner = readFileSync(nodeRunnerPath, 'utf8')
  const webPackage = JSON.parse(readFileSync(new URL('../../apps/web/package.json', import.meta.url), 'utf8'))
  const webCase = runner.slice(runner.indexOf('  web)'), runner.indexOf('  mobile)'))

  assert.match(webCase, /npm --prefix apps\/web run build:ci -- --manifest/)
  assert.match(webCase, /npm --prefix apps\/web run verify:bundle/)
  assert.ok(
    webCase.indexOf('run build:ci -- --manifest') < webCase.indexOf('run verify:bundle'),
    'bundle 硬门禁必须使用当前 CI 构建产物',
  )
  assert.equal(
    webPackage.scripts['verify:bundle'],
    'node ../../scripts/performance/verify-web-bundle.mjs --dist dist --budget ../../web-bundle-budget.json',
  )
})

test('环境敏感的 POS 微基准只在 weekly profile 执行', () => {
  const workflowSource = readFileSync(workflowPath, 'utf8')
  const testAllSource = readFileSync(testAllPath, 'utf8')
  assert.match(
    workflowSource,
    /HBPOS_RUN_PERF_TESTS:\s*\$\{\{ needs\.plan\.outputs\.profile == 'weekly' && '1' \|\| '0' \}\}/,
  )
  assert.match(testAllSource, /export HBPOS_RUN_PERF_TESTS="\$\{HBPOS_RUN_PERF_TESTS:-1\}"/)

  for (const path of posPricingPerformanceTestPaths) {
    const source = readFileSync(path, 'utf8')
    assert.match(source, /const performanceTest = process\.env\.HBPOS_RUN_PERF_TESTS === "1" \? test : test\.skip;/)
    assert.match(source, /performanceTest\("300 行不可合并百分比折扣的按钮预测保持近线性"/)
  }
})
