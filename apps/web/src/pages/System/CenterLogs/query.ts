import dayjs, { type Dayjs } from 'dayjs'
import type { ApplicationLogQueryParams, ApplicationLogSummary } from '../../../types/centerLog'

export interface CenterLogQueryFormValues {
  projectCodes?: string[]
  level?: string
  sourceType?: string
  category?: string
  requestPath?: string
  traceId?: string
  storeCode?: string
  deviceCode?: string
  appVersion?: string
  instanceId?: string
  keyword?: string
  timeRange?: [Dayjs, Dayjs]
}

export const DEFAULT_CENTER_LOG_PAGE_SIZE = 20
export const CENTER_LOG_PATH = '/system/center-logs'

export const CENTER_LOG_PROJECT_DEFINITIONS = [
  { projectCode: 'HBBBackend', labelKey: 'system.centerLogs.projects.HBBBackend' },
  { projectCode: 'hbweb_rv', labelKey: 'system.centerLogs.projects.hbweb_rv' },
  { projectCode: 'HbwebExpo', labelKey: 'system.centerLogs.projects.HbwebExpo' },
  { projectCode: 'hbpos_win', labelKey: 'system.centerLogs.projects.hbpos_win' },
  { projectCode: 'hbpos_ipad', labelKey: 'system.centerLogs.projects.hbpos_ipad' },
  { projectCode: 'hbpos_handheld', labelKey: 'system.centerLogs.projects.hbpos_handheld' },
  { projectCode: 'hbpos_api', labelKey: 'system.centerLogs.projects.hbpos_api' },
] as const
export const CENTER_LOG_LEVEL_OPTIONS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical']
export const CENTER_LOG_SOURCE_TYPE_OPTIONS = ['Backend', 'Web', 'Mobile', 'POS']

export type CenterLogConfigurationState = 'Ready' | 'Disabled' | 'MissingCredential'
export type CenterLogCredentialState = 'Configured' | 'Inactive' | 'MissingCredential' | 'NotRequired' | 'Unknown'

function normalizeProjectCodes(projectCodes?: string[]) {
  const normalized = projectCodes
    ?.map((item) => item.trim())
    .filter((item) => item.length > 0)

  return normalized?.length ? Array.from(new Set(normalized)) : undefined
}

export function buildCenterLogQueryParams(
  values: CenterLogQueryFormValues,
  pageNumber = 1,
  pageSize = DEFAULT_CENTER_LOG_PAGE_SIZE,
): ApplicationLogQueryParams {
  const projectCodes = normalizeProjectCodes(values.projectCodes)

  return {
    // 空项目表示查询全部，必须省略两个项目参数；有选择时保留单项目参数兼容非同步发布。
    ...(projectCodes ? { projectCode: projectCodes[0], projectCodes } : {}),
    level: values.level || undefined,
    sourceType: values.sourceType || undefined,
    category: values.category?.trim() || undefined,
    requestPath: values.requestPath?.trim() || undefined,
    traceId: values.traceId?.trim() || undefined,
    storeCode: values.storeCode?.trim() || undefined,
    deviceCode: values.deviceCode?.trim() || undefined,
    appVersion: values.appVersion?.trim() || undefined,
    instanceId: values.instanceId?.trim() || undefined,
    keyword: values.keyword?.trim() || undefined,
    startUtc: values.timeRange?.[0]?.toISOString(),
    endUtc: values.timeRange?.[1]?.toISOString(),
    pageNumber,
    pageSize,
    sortBy: 'TimestampUtc',
    sortDirection: 'desc',
  }
}

export function buildDefaultCenterLogQueryParams(pageSize = DEFAULT_CENTER_LOG_PAGE_SIZE) {
  return buildCenterLogQueryParams(
    { projectCodes: [] },
    1,
    pageSize,
  )
}

export function buildCenterLogProjectChangeQuery(
  activeQuery: ApplicationLogQueryParams,
  projectCodes: string[],
) {
  const normalizedProjectCodes = normalizeProjectCodes(projectCodes)
  const nextQuery: ApplicationLogQueryParams = {
    ...activeQuery,
    pageNumber: 1,
    pageSize: activeQuery.pageSize ?? DEFAULT_CENTER_LOG_PAGE_SIZE,
  }

  // 仅替换已应用查询中的项目范围，避免顺带提交表单里尚未点击“查询”的其他改动。
  delete nextQuery.projectCode
  delete nextQuery.projectCodes
  if (normalizedProjectCodes) {
    nextQuery.projectCode = normalizedProjectCodes[0]
    nextQuery.projectCodes = normalizedProjectCodes
  }

  return nextQuery
}

export function resolveCenterLogConfigurationState(status: {
  configurationState?: string
  enabled: boolean
  credentialConfigured?: boolean | null
}): CenterLogConfigurationState {
  if (status.configurationState === 'Ready' ||
    status.configurationState === 'Disabled' ||
    status.configurationState === 'MissingCredential') {
    return status.configurationState
  }

  if (!status.enabled) {
    return 'Disabled'
  }

  return status.credentialConfigured === false ? 'MissingCredential' : 'Ready'
}

export function resolveCenterLogCredentialState(status: {
  enabled: boolean
  mode?: string
  credentialConfigured?: boolean | null
}): CenterLogCredentialState {
  // 项目停用时密钥不参与当前接收流程，避免把它误报为待处理故障。
  if (!status.enabled) {
    return 'Inactive'
  }

  // 只有内部采集明确无需外部凭据；旧响应缺字段时，外部项目必须保留为未知。
  if (status.credentialConfigured == null) {
    return status.mode === 'Internal' ? 'NotRequired' : 'Unknown'
  }

  return status.credentialConfigured ? 'Configured' : 'MissingCredential'
}

export interface LatestCenterLogRequestHandlers<T> {
  onStart?: () => void
  onSuccess: (value: T) => void
  onError?: (error: unknown) => void
  onSettled?: () => void
}

export function createLatestCenterLogRequestRunner() {
  let latestRequestId = 0

  return {
    async run<T>(
      operation: () => Promise<T>,
      handlers: LatestCenterLogRequestHandlers<T>,
    ) {
      latestRequestId += 1
      const requestId = latestRequestId
      handlers.onStart?.()

      try {
        const value = await operation()
        if (requestId !== latestRequestId) {
          return
        }

        handlers.onSuccess(value)
      } catch (error) {
        if (requestId !== latestRequestId) {
          return
        }

        handlers.onError?.(error)
      } finally {
        // 旧请求完成时不能关闭新请求的 loading，只由最新请求收尾。
        if (requestId === latestRequestId) {
          handlers.onSettled?.()
        }
      }
    },
  }
}

export function buildCenterLogStatusOverview(
  summary?: Pick<ApplicationLogSummary, 'status' | 'pipeline'> | null,
) {
  const latestReceivedAtUtc = summary?.status?.projects.reduce<string | undefined>(
    (latest, project) => {
      const current = project.lastReceivedAtUtc ?? undefined
      return current && (!latest || current > latest) ? current : latest
    },
    undefined,
  )
  const pipelineAnomalies = summary?.pipeline
    ? {
        droppedOldestCount: summary.pipeline.droppedOldestCount,
        enqueueFailureCount: summary.pipeline.enqueueFailureCount,
        failedFlushBatchCount: summary.pipeline.failedFlushBatchCount,
        failedFlushLogCount: summary.pipeline.failedFlushLogCount,
        // 四个计数口径不同，只判断是否曾记录异常，不能相加成一个总数。
        hasRecordedAnomaly: [
          summary.pipeline.droppedOldestCount,
          summary.pipeline.enqueueFailureCount,
          summary.pipeline.failedFlushBatchCount,
          summary.pipeline.failedFlushLogCount,
        ].some((count) => count > 0),
      }
    : undefined

  return { latestReceivedAtUtc, pipelineAnomalies }
}

export function buildCenterLogFormValuesFromSearchParams(
  searchParams: URLSearchParams,
): CenterLogQueryFormValues {
  const projectCodes = normalizeProjectCodes([
    ...searchParams.getAll('projectCodes'),
    searchParams.get('projectCode') ?? '',
  ]) ?? []
  const fromUtc = searchParams.get('fromUtc')
  const toUtc = searchParams.get('toUtc')
  const from = fromUtc ? dayjs(fromUtc) : null
  const to = toUtc ? dayjs(toUtc) : null
  const read = (key: string) => searchParams.get(key)?.trim() || undefined

  return {
    projectCodes,
    level: read('level'),
    sourceType: read('sourceType'),
    category: read('category'),
    requestPath: read('requestPath'),
    traceId: read('traceId'),
    storeCode: read('storeCode'),
    deviceCode: read('deviceCode'),
    appVersion: read('appVersion'),
    instanceId: read('instanceId'),
    keyword: read('keyword'),
    timeRange: from?.isValid() && to?.isValid() ? [from, to] : undefined,
  }
}

export function shouldHydrateCenterLogQueryFromLocation(
  active: boolean,
  pathname: string,
  _nextSearch: string,
  locationKey: string,
  hydratedLocationKey: string,
) {
  return active &&
    pathname === CENTER_LOG_PATH &&
    locationKey !== hydratedLocationKey
}
