import { existsSync, readFileSync } from 'node:fs'
import dayjs from 'dayjs'
import {
  CENTER_LOG_PATH,
  DEFAULT_CENTER_LOG_PAGE_SIZE,
  buildCenterLogQueryParams,
  buildDefaultCenterLogQueryParams,
  shouldHydrateCenterLogQueryFromLocation,
} from './query'
import * as queryModule from './query'

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const start = dayjs('2026-06-05T00:00:00.000Z')
const end = dayjs('2026-06-05T02:30:00.000Z')

const query = buildCenterLogQueryParams(
  {
    projectCodes: [' hbweb_rv ', '', 'HbwebExpo', 'hbweb_rv'],
    level: 'Error',
    sourceType: 'Web',
    category: ' frontend-request ',
    requestPath: ' /api/system/users ',
    traceId: ' trace-001 ',
    storeCode: ' S01 ',
    deviceCode: ' POS-01 ',
    appVersion: ' 1.2.3 ',
    instanceId: ' instance-01 ',
    keyword: ' timeout ',
    timeRange: [start, end],
  },
  3,
  50,
)

assertEqual(Array.isArray(query.projectCodes), true, 'project codes are returned as an array')
assertEqual(query.projectCodes?.join(','), 'hbweb_rv,HbwebExpo', 'project codes are trimmed and deduplicated')
assertEqual(query.projectCode, 'hbweb_rv', 'legacy project code keeps first selected project')
assertEqual(query.level, 'Error', 'level is preserved')
assertEqual(query.sourceType, 'Web', 'source type is preserved')
assertEqual(query.category, 'frontend-request', 'category is trimmed')
assertEqual(query.requestPath, '/api/system/users', 'request path is trimmed')
assertEqual(query.traceId, 'trace-001', 'trace id is trimmed')
assertEqual(query.storeCode, 'S01', 'store code is trimmed')
assertEqual(query.deviceCode, 'POS-01', 'device code is trimmed')
assertEqual(query.appVersion, '1.2.3', 'app version is trimmed')
assertEqual(query.instanceId, 'instance-01', 'instance id is trimmed')
assertEqual(query.keyword, 'timeout', 'keyword is trimmed')
assertEqual(query.startUtc, start.toISOString(), 'start time is serialized')
assertEqual(query.endUtc, end.toISOString(), 'end time is serialized')
assertEqual(query.pageNumber, 3, 'page number is preserved')
assertEqual(query.pageSize, 50, 'page size is preserved')
assertEqual(query.sortBy, 'TimestampUtc', 'sort field defaults to timestamp')
assertEqual(query.sortDirection, 'desc', 'sort direction defaults to descending')

const defaultQuery = buildDefaultCenterLogQueryParams()
assertEqual(
  Object.prototype.hasOwnProperty.call(defaultQuery, 'projectCodes'),
  false,
  'default query omits project codes to query all projects',
)
assertEqual(
  Object.prototype.hasOwnProperty.call(defaultQuery, 'projectCode'),
  false,
  'default query omits legacy project code to query all projects',
)
assertEqual(defaultQuery.pageNumber, 1, 'default query resets page number')
assertEqual(defaultQuery.pageSize, DEFAULT_CENTER_LOG_PAGE_SIZE, 'default query keeps default page size')
assertEqual(defaultQuery.level, undefined, 'default query clears level')
assertEqual(defaultQuery.sourceType, undefined, 'default query clears source type')
assertEqual(defaultQuery.category, undefined, 'default query clears category')
assertEqual(defaultQuery.requestPath, undefined, 'default query clears request path')
assertEqual(defaultQuery.traceId, undefined, 'default query clears trace id')
assertEqual(defaultQuery.keyword, undefined, 'default query clears keyword')

const emptyUrlValues = (
  queryModule as unknown as {
    buildCenterLogFormValuesFromSearchParams: (params: URLSearchParams) => { projectCodes?: string[] }
  }
).buildCenterLogFormValuesFromSearchParams(new URLSearchParams())
assertEqual(emptyUrlValues.projectCodes?.length, 0, 'plain URL defaults to all projects')

const buildProjectChangeQuery = (
  queryModule as unknown as {
    buildCenterLogProjectChangeQuery?: (
      values: ReturnType<typeof buildCenterLogQueryParams>,
      projectCodes: string[],
    ) => ReturnType<typeof buildCenterLogQueryParams>
  }
).buildCenterLogProjectChangeQuery
assertEqual(typeof buildProjectChangeQuery, 'function', 'project selection should expose immediate query builder')
const changedProjectQuery = buildProjectChangeQuery?.(
  {
    projectCodes: ['HBBBackend'],
    projectCode: 'HBBBackend',
    level: 'Error',
    startUtc: '2026-07-14T00:00:00.000Z',
    pageNumber: 5,
    pageSize: 50,
  },
  ['HbwebExpo', 'hbpos_api'],
)
assertEqual(changedProjectQuery?.projectCodes?.join(','), 'HbwebExpo,hbpos_api', 'project change applies multiple values immediately')
assertEqual(changedProjectQuery?.projectCode, 'HbwebExpo', 'project change keeps legacy first-project fallback')
assertEqual(changedProjectQuery?.level, 'Error', 'project change preserves active non-project filters')
assertEqual(
  changedProjectQuery?.startUtc,
  '2026-07-14T00:00:00.000Z',
  'project change preserves active date filters instead of applying unrelated form edits',
)
assertEqual(changedProjectQuery?.pageNumber, 1, 'project change resets page number')
assertEqual(changedProjectQuery?.pageSize, 50, 'project change preserves the active page size')

const buildStatusOverview = (
  queryModule as unknown as {
    buildCenterLogStatusOverview?: (summary: {
      status?: { projects: Array<{ lastReceivedAtUtc: string | null }> }
      pipeline?: {
        droppedOldestCount: number
        enqueueFailureCount: number
        failedFlushBatchCount: number
        failedFlushLogCount: number
      }
    }) => {
      latestReceivedAtUtc?: string
      pipelineAnomalies?: {
        droppedOldestCount: number
        enqueueFailureCount: number
        failedFlushBatchCount: number
        failedFlushLogCount: number
        hasRecordedAnomaly: boolean
      }
    }
  }
).buildCenterLogStatusOverview
assertEqual(typeof buildStatusOverview, 'function', 'center logs should expose compact status overview logic')
const statusOverview = buildStatusOverview?.({
  status: {
    projects: [
      { lastReceivedAtUtc: '2026-07-14T01:00:00.000Z' },
      { lastReceivedAtUtc: '2026-07-14T03:00:00.000Z' },
      { lastReceivedAtUtc: null },
    ],
  },
  pipeline: {
    droppedOldestCount: 1,
    enqueueFailureCount: 2,
    failedFlushBatchCount: 4,
    failedFlushLogCount: 3,
  },
})
assertEqual(statusOverview?.latestReceivedAtUtc, '2026-07-14T03:00:00.000Z', 'status overview keeps latest received time')
assertEqual(statusOverview?.pipelineAnomalies?.droppedOldestCount, 1, 'status overview preserves dropped count')
assertEqual(statusOverview?.pipelineAnomalies?.enqueueFailureCount, 2, 'status overview preserves enqueue failure count')
assertEqual(statusOverview?.pipelineAnomalies?.failedFlushBatchCount, 4, 'status overview preserves failed batch count')
assertEqual(statusOverview?.pipelineAnomalies?.failedFlushLogCount, 3, 'status overview preserves failed log count')
assertEqual(statusOverview?.pipelineAnomalies?.hasRecordedAnomaly, true, 'status overview marks historical anomalies without summing unlike counters')

const projectDefinitions = (
  queryModule as unknown as {
    CENTER_LOG_PROJECT_DEFINITIONS?: Array<{ projectCode: string; labelKey: string }>
  }
).CENTER_LOG_PROJECT_DEFINITIONS
assertEqual(projectDefinitions?.length, 7, 'center logs should define all seven projects')
assertEqual(
  projectDefinitions?.map((item) => item.projectCode).join(','),
  'HBBBackend,hbweb_rv,HbwebExpo,hbpos_win,hbpos_ipad,hbpos_handheld,hbpos_api',
  'project definitions should keep the supported project order including iPad and handheld',
)

const zhCenterLogsProjects = JSON.parse(readFileSync('src/i18n/locales/zh.json', 'utf8')).system.centerLogs.projects
const enCenterLogsProjects = JSON.parse(readFileSync('src/i18n/locales/en.json', 'utf8')).system.centerLogs.projects
assertEqual(zhCenterLogsProjects.hbpos_ipad, 'iPad客户端', 'Chinese iPad project label is defined')
assertEqual(enCenterLogsProjects.hbpos_ipad, 'iPad Client', 'English iPad project label is defined')
assertEqual(zhCenterLogsProjects.hbpos_handheld, '手持收银客户端', 'Chinese handheld project label is defined')
assertEqual(enCenterLogsProjects.hbpos_handheld, 'Handheld POS Client', 'English handheld project label is defined')

const resolveConfigurationState = (
  queryModule as unknown as {
    resolveCenterLogConfigurationState?: (status: {
      configurationState?: string
      enabled: boolean
      credentialConfigured: boolean | null
    }) => string
  }
).resolveCenterLogConfigurationState
assertEqual(typeof resolveConfigurationState, 'function', 'center logs should expose project configuration state mapping')
assertEqual(
  resolveConfigurationState?.({ configurationState: 'Ready', enabled: true, credentialConfigured: true }),
  'Ready',
  'ready project state is preserved',
)
assertEqual(
  resolveConfigurationState?.({ configurationState: '', enabled: false, credentialConfigured: true }),
  'Disabled',
  'disabled project state is derived when backend state is absent',
)
assertEqual(
  resolveConfigurationState?.({ configurationState: '', enabled: true, credentialConfigured: false }),
  'MissingCredential',
  'missing credential project state is derived when backend state is absent',
)

const resolveCredentialState = (
  queryModule as unknown as {
    resolveCenterLogCredentialState?: (status: {
      enabled: boolean
      mode?: string
      credentialConfigured?: boolean | null
    }) => string
  }
).resolveCenterLogCredentialState
assertEqual(typeof resolveCredentialState, 'function', 'center logs should expose credential display state mapping')
assertEqual(
  resolveCredentialState?.({ enabled: false, mode: 'External', credentialConfigured: undefined }),
  'Inactive',
  'disabled project should remain inactive when an older response omits credential state',
)
assertEqual(
  resolveCredentialState?.({ enabled: true, mode: 'Internal', credentialConfigured: undefined }),
  'NotRequired',
  'omitted internal credential state should be reported as not required',
)
assertEqual(
  resolveCredentialState?.({ enabled: true, mode: 'Internal', credentialConfigured: null }),
  'NotRequired',
  'null internal credential state should be reported as not required',
)
assertEqual(
  resolveCredentialState?.({ enabled: true, mode: 'External', credentialConfigured: undefined }),
  'Unknown',
  'omitted external credential state should remain unknown during staggered rollout',
)
assertEqual(
  resolveCredentialState?.({ enabled: true, mode: 'External', credentialConfigured: true }),
  'Configured',
  'configured external credential should be preserved',
)
assertEqual(
  resolveCredentialState?.({ enabled: true, mode: 'External', credentialConfigured: false }),
  'MissingCredential',
  'enabled external project without a credential should remain actionable',
)
assertEqual(
  resolveCredentialState?.({ enabled: false, mode: 'External', credentialConfigured: true }),
  'Inactive',
  'disabled state should take precedence over an existing credential',
)

assertEqual(
  typeof (queryModule as Record<string, unknown>).buildCenterLogFormValuesFromSearchParams,
  'function',
  'center logs should expose URL query hydration for audit detail links',
)

const linkedValues = (
  queryModule as unknown as {
    buildCenterLogFormValuesFromSearchParams: (params: URLSearchParams) => {
      projectCodes?: string[]
      deviceCode?: string
      traceId?: string
      timeRange?: [dayjs.Dayjs, dayjs.Dayjs]
    }
  }
).buildCenterLogFormValuesFromSearchParams(
  new URLSearchParams({
    projectCode: 'hbpos_win',
    deviceCode: 'POS-01',
    traceId: 'trace-01',
    fromUtc: '2026-07-10T00:57:03.000Z',
    toUtc: '2026-07-10T01:07:03.000Z',
  }),
)

assertEqual(linkedValues.projectCodes?.join(','), 'hbpos_win', 'URL project should hydrate project selection')
assertEqual(linkedValues.deviceCode, 'POS-01', 'URL device should hydrate device filter')
assertEqual(linkedValues.traceId, 'trace-01', 'URL trace should hydrate trace filter')
assertEqual(linkedValues.timeRange?.[0].toISOString(), '2026-07-10T00:57:03.000Z', 'URL start should hydrate range')
assertEqual(linkedValues.timeRange?.[1].toISOString(), '2026-07-10T01:07:03.000Z', 'URL end should hydrate range')

assertEqual(
  shouldHydrateCenterLogQueryFromLocation(true, CENTER_LOG_PATH, '?traceId=new', 'next-key', 'old-key'),
  true,
  'active keep-alive page should hydrate changed audit link query',
)
assertEqual(
  shouldHydrateCenterLogQueryFromLocation(false, CENTER_LOG_PATH, '?traceId=new', 'next-key', 'old-key'),
  false,
  'hidden keep-alive page should ignore global location changes',
)
assertEqual(
  shouldHydrateCenterLogQueryFromLocation(true, '/pos-admin/operation-logs', '?traceId=new', 'next-key', 'old-key'),
  false,
  'other routes should not hydrate hidden center log form',
)
assertEqual(
  shouldHydrateCenterLogQueryFromLocation(true, CENTER_LOG_PATH, '?traceId=same', 'same-key', 'same-key'),
  false,
  'same navigation should not reload center logs',
)
assertEqual(
  shouldHydrateCenterLogQueryFromLocation(true, CENTER_LOG_PATH, '?traceId=same', 'second-key', 'first-key'),
  true,
  'same audit link should rehydrate on a new navigation',
)
assertEqual(
  shouldHydrateCenterLogQueryFromLocation(true, CENTER_LOG_PATH, '', 'tab-key', 'audit-link-key'),
  true,
  'plain keep-alive re-entry should hydrate all-project defaults for a new navigation',
)

const createLatestRequestRunner = (
  queryModule as unknown as {
    createLatestCenterLogRequestRunner?: () => {
      run: <T>(
        operation: () => Promise<T>,
        handlers: {
          onStart?: () => void
          onSuccess: (value: T) => void
          onError?: (error: unknown) => void
          onSettled?: () => void
        },
      ) => Promise<void>
    }
  }
).createLatestCenterLogRequestRunner
assertEqual(typeof createLatestRequestRunner, 'function', 'center logs should expose a latest-request runner')

function createDeferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((nextResolve) => {
    resolve = nextResolve
  })
  return { promise, resolve }
}

const requestRunner = createLatestRequestRunner?.()
const firstDeferred = createDeferred<string>()
const secondDeferred = createDeferred<string>()
const appliedResults: string[] = []
let loading = false
const firstRun = requestRunner?.run(
  () => firstDeferred.promise,
  {
    onStart: () => { loading = true },
    onSuccess: (value) => { appliedResults.push(value) },
    onSettled: () => { loading = false },
  },
)
const secondRun = requestRunner?.run(
  () => secondDeferred.promise,
  {
    onStart: () => { loading = true },
    onSuccess: (value) => { appliedResults.push(value) },
    onSettled: () => { loading = false },
  },
)
firstDeferred.resolve('stale')
await firstRun
assertEqual(appliedResults.length, 0, 'stale response should not update center-log state')
assertEqual(loading, true, 'stale response should not finish loading while latest request is pending')
secondDeferred.resolve('latest')
await secondRun
assertEqual(appliedResults.join(','), 'latest', 'latest response should update center-log state')
assertEqual(loading, false, 'latest response should finish loading')

const centerLogsPageSource = readFileSync('src/pages/System/CenterLogs/index.tsx', 'utf8')
assertEqual(
  centerLogsPageSource.includes('const { active } = useKeepAliveContext()'),
  true,
  'center logs page should guard URL hydration with keep-alive active state',
)
assertEqual(
  centerLogsPageSource.includes('shouldHydrateCenterLogQueryFromLocation('),
  true,
  'center logs page should rehydrate changed audit-link query',
)
assertEqual(
  centerLogsPageSource.includes('mode="multiple"'),
  true,
  'project selector should use fixed multiple mode',
)
assertEqual(
  centerLogsPageSource.includes('buildCenterLogProjectChangeQuery('),
  true,
  'project selector should apply project changes immediately',
)
assertEqual(
  centerLogsPageSource.includes('setActiveQuery((currentQuery) => buildCenterLogProjectChangeQuery('),
  true,
  'project selector should update projects from active query without applying other form edits',
)
assertEqual(
  centerLogsPageSource.includes('summaryStatus?.status?.projects'),
  true,
  'center logs page should safely read project status during staggered backend rollout',
)
assertEqual(
  centerLogsPageSource.includes('summaryStatus.status.'),
  false,
  'center logs page should not require status from an older backend response',
)
assertEqual(
  centerLogsPageSource.includes('{summaryStatus?.status ? ('),
  true,
  'missing status should render unknown instead of reporting backend capture as disabled',
)
assertEqual(
  centerLogsPageSource.includes('createLatestCenterLogRequestRunner()'),
  true,
  'center logs page should create a latest-request runner',
)
assertEqual(
  centerLogsPageSource.includes('requestRunnerRef.current.run('),
  true,
  'center logs page should execute requests through latest-response coordination',
)
assertEqual(centerLogsPageSource.includes('Collapse,'), true, 'status details should use existing Ant Design Collapse')
assertEqual(centerLogsPageSource.includes('defaultActiveKey'), false, 'diagnostic details should be collapsed by default')
assertEqual(
  centerLogsPageSource.includes('buildCenterLogStatusOverview(summaryStatus)'),
  true,
  'status area should render a compact always-visible overview',
)
assertEqual(
  centerLogsPageSource.includes('<Tag color={color}>{state}</Tag>'),
  false,
  'project status should not duplicate raw and localized state labels',
)
assertEqual(
  centerLogsPageSource.includes("t('system.centerLogs.status.registered')"),
  true,
  'project entry should render registered rather than configured',
)
assertEqual(
  centerLogsPageSource.includes("t('system.centerLogs.status.instanceScopeNote')") &&
    centerLogsPageSource.includes("t('system.centerLogs.status.databaseScopeNote')") &&
    centerLogsPageSource.includes("t('system.centerLogs.status.pipelineScopeNote')"),
  true,
  'status area should render the three scope notes separately',
)

const zhCenterLogs = JSON.parse(readFileSync('src/i18n/locales/zh.json', 'utf8')).system.centerLogs
const enCenterLogs = JSON.parse(readFileSync('src/i18n/locales/en.json', 'utf8')).system.centerLogs
assertEqual(zhCenterLogs.filters.projectPlaceholder, '全部项目（可多选）', 'Chinese project placeholder explains empty selection means all projects')
assertEqual(enCenterLogs.filters.projectPlaceholder, 'All projects (multiple selection)', 'English project placeholder explains empty selection means all projects')
assertEqual(zhCenterLogs.projects.HBBBackend, 'Web/移动端后端', 'Chinese backend project label is defined')
assertEqual(enCenterLogs.projects.HBBBackend, 'Web/Mobile Backend', 'English backend project label is defined')
assertEqual(zhCenterLogs.projects.hbweb_rv, 'Web前端', 'Chinese web project label is defined')
assertEqual(enCenterLogs.projects.hbweb_rv, 'Web Frontend', 'English web project label is defined')
assertEqual(zhCenterLogs.projects.HbwebExpo, '移动端', 'Chinese mobile project label is defined')
assertEqual(enCenterLogs.projects.HbwebExpo, 'Mobile App', 'English mobile project label is defined')
assertEqual(zhCenterLogs.projects.hbpos_win, 'WPF客户端', 'Chinese WPF client label is defined')
assertEqual(enCenterLogs.projects.hbpos_win, 'WPF Client', 'English WPF client label is defined')
assertEqual(zhCenterLogs.projects.hbpos_api, 'WPF收银后端', 'Chinese POS backend label is defined')
assertEqual(enCenterLogs.projects.hbpos_api, 'WPF POS Backend', 'English POS backend label is defined')
assertEqual(zhCenterLogs.status.modes.Internal, '内部采集', 'Chinese internal mode label is defined')
assertEqual(zhCenterLogs.status.modes.External, '外部接入', 'Chinese external mode label is defined')
assertEqual(enCenterLogs.status.modes.Internal, 'Internal', 'English internal mode label is defined')
assertEqual(enCenterLogs.status.modes.External, 'External', 'English external mode label is defined')
assertEqual(zhCenterLogs.status.states.Ready, '配置齐全', 'Chinese ready copy describes configuration')
assertEqual(zhCenterLogs.status.webBuildConfigured, '已配置', 'Chinese web build copy describes configuration')
assertEqual(zhCenterLogs.status.configurationState, '当前实例接收状态', 'Chinese configuration column names its scope')
assertEqual(enCenterLogs.status.configurationState, 'Current Instance Ingest Status', 'English configuration column names its scope')
assertEqual(zhCenterLogs.status.explicitConfiguration, '中心端项目条目', 'Chinese project entry column names its scope')
assertEqual(enCenterLogs.status.explicitConfiguration, 'Central Project Entry', 'English project entry column names its scope')
assertEqual(zhCenterLogs.status.credential, '当前实例凭据', 'Chinese credential column names its scope')
assertEqual(enCenterLogs.status.credential, 'Current Instance Credential', 'English credential column names its scope')
assertEqual(zhCenterLogs.status.lastReceivedAt, '日志库最近入库', 'Chinese last received column names the database scope')
assertEqual(enCenterLogs.status.lastReceivedAt, 'Latest Stored in Log DB', 'English last received column names the database scope')
assertEqual(zhCenterLogs.status.credentialStates.Inactive, '停用中 / 不适用', 'Chinese disabled credential copy is not actionable')
assertEqual(enCenterLogs.status.credentialStates.Inactive, 'Inactive / Not applicable', 'English disabled credential copy is not actionable')
assertEqual(zhCenterLogs.status.credentialStates.Unknown, '状态未知', 'Chinese unknown credential copy is explicit')
assertEqual(enCenterLogs.status.credentialStates.Unknown, 'Unknown', 'English unknown credential copy is explicit')
assertEqual(zhCenterLogs.status.registered, '已登记', 'Chinese project entry copy does not imply client configuration')
assertEqual(enCenterLogs.status.registered, 'Registered', 'English project entry copy does not imply client configuration')
assertEqual(
  zhCenterLogs.status.pipelineSummary,
  '当前实例 HBBBackend 内部采集队列异常摘要',
  'Chinese pipeline summary names the covered queue',
)
assertEqual(
  enCenterLogs.status.pipelineSummary,
  'Current Instance HBBBackend Internal Capture Queue Anomaly Summary',
  'English pipeline summary names the covered queue',
)
assertEqual(zhCenterLogs.status.pipeline, '当前实例 HBBBackend 内部采集队列计数', 'Chinese pipeline title names the covered queue')
assertEqual(enCenterLogs.status.pipeline, 'Current Instance HBBBackend Internal Capture Queue Counters', 'English pipeline title names the covered queue')
assertEqual(zhCenterLogs.status.notReceived, '当前保留数据中无记录', 'Chinese empty project copy names the retention scope')
assertEqual(enCenterLogs.status.notReceived, 'No records in currently retained data', 'English empty project copy names the retention scope')
assertEqual(
  zhCenterLogs.status.instanceScopeNote.includes('当前实例') &&
    zhCenterLogs.status.instanceScopeNote.includes('已登记不代表客户端已接入'),
  true,
  'Chinese instance note describes only current-instance configuration',
)
assertEqual(
  zhCenterLogs.status.databaseScopeNote.includes('日志库最近入库') &&
    zhCenterLogs.status.databaseScopeNote.includes('其他实例或历史配置'),
  true,
  'Chinese database note describes shared storage evidence',
)
assertEqual(
  zhCenterLogs.status.pipelineScopeNote.includes('HBBBackend 内部采集队列') &&
    zhCenterLogs.status.pipelineScopeNote.includes('不覆盖外部客户端上传或 /ingest') &&
    zhCenterLogs.status.pipelineScopeNote.includes('重启后归零'),
  true,
  'Chinese pipeline note names exclusions and reset behavior',
)
assertEqual(
  enCenterLogs.status.instanceScopeNote.includes('current backend instance') &&
    enCenterLogs.status.instanceScopeNote.includes('does not mean the client is connected'),
  true,
  'English instance note describes only current-instance configuration',
)
assertEqual(
  enCenterLogs.status.databaseScopeNote.includes('log database') &&
    enCenterLogs.status.databaseScopeNote.includes('other instances or earlier configurations'),
  true,
  'English database note describes shared storage evidence',
)
assertEqual(
  enCenterLogs.status.pipelineScopeNote.includes('HBBBackend internal capture queue') &&
    enCenterLogs.status.pipelineScopeNote.includes('external client uploads or /ingest') &&
    enCenterLogs.status.pipelineScopeNote.includes('resets after process restart'),
  true,
  'English pipeline note names exclusions and reset behavior',
)
assertEqual(
  zhCenterLogs.empty.description,
  '当前筛选下没有日志不代表采集链路故障，请结合配置状态、最近接收时间和 Pipeline 计数判断。',
  'Chinese empty copy should describe all logs rather than error logs only',
)
assertEqual(
  enCenterLogs.empty.description,
  'No logs for the current filters does not mean the capture pipeline is broken. Check configuration, last received time, and Pipeline counters together.',
  'English empty copy should describe all logs rather than error logs only',
)
assertEqual(
  JSON.stringify(zhCenterLogs.status).includes('已连接') || JSON.stringify(zhCenterLogs.status).includes('在线'),
  false,
  'status copy must not imply connection or online state',
)

const productionEnvExample = readFileSync('.env.production.example', 'utf8')
assertEqual(
  productionEnvExample.includes('.env.production.local'),
  true,
  'production example should direct local secrets to the ignored production-local file',
)
assertEqual(
  productionEnvExample.includes('复制为 .env.production '),
  false,
  'production example should not direct secrets to the shared production env file',
)
assertEqual(
  productionEnvExample.includes('公开低权限写入 token'),
  true,
  'production example should explain the public low-privilege ingest token architecture',
)
assertEqual(
  productionEnvExample.includes('不得复用其他 secret'),
  true,
  'production example should forbid reusing other secrets',
)

const packageJson = JSON.parse(readFileSync('package.json', 'utf8'))
assertEqual(packageJson.scripts.dev.includes('--config vite.config.ts'), true, 'dev script should select TypeScript Vite config')
assertEqual(packageJson.scripts.build.includes('--config vite.config.ts'), true, 'build script should select TypeScript Vite config')
assertEqual(packageJson.scripts.preview.includes('--config vite.config.ts'), true, 'preview script should select TypeScript Vite config')

const nodeTsConfig = JSON.parse(readFileSync('tsconfig.node.json', 'utf8'))
assertEqual(nodeTsConfig.compilerOptions.noEmit, true, 'node TypeScript config should never regenerate Vite config artifacts')
assertEqual(existsSync('vite.config.js'), false, 'tracked generated Vite JavaScript should be removed')
assertEqual(existsSync('vite.config.d.ts'), false, 'tracked generated Vite declaration should be removed')

console.log('centerLogs.query.test: ok')
