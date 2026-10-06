import type {
  BatchCheckProductsItemDto,
  BatchCheckProductsJobDto,
  LocalSupplierInvoiceListDto,
} from '../../../types/localSupplierInvoice'

/** 单次批量检测最多进货单数，与后端 LocalSupplierInvoiceBatchCheckProductsLimits.MaxInvoices 一致。 */
export const BATCH_CHECK_MAX_INVOICES = 50

/** 轮询间隔与列表刷新的最小间隔：刷新要重查列表和计数，频率比轮询低。 */
export const BATCH_CHECK_POLL_INTERVAL_MS = 2000
export const BATCH_CHECK_LIST_REFRESH_MIN_INTERVAL_MS = 3000

/** 离开列表再回来（或刷新页面）时接着显示进度。只存 jobId，任务过期后自动清掉。 */
export const BATCH_CHECK_JOB_STORAGE_KEY = 'hbweb_rv.localSupplierInvoices.batchCheckJobId.v1'

/** 行内检测列的批量覆盖态；null 表示按列表数据正常显示（已检测 / 待检测 / 新品）。 */
export type BatchRowDisplay = 'queued' | 'running' | 'failed' | 'skipped' | null

export function isBatchJobActive(job: BatchCheckProductsJobDto | null | undefined): boolean {
  return job?.status === 'Running'
}

/**
 * 勾选上限：已选的单保留，新勾的只补到上限为止；取消勾选总是生效。
 * 不能简单截取前 N 个——那会把用户刚点的那一行丢掉，看起来像「点了没反应还报错」。
 */
export function applyBatchSelectionLimit(
  previousKeys: string[],
  nextKeys: string[],
  max = BATCH_CHECK_MAX_INVOICES,
) {
  const next = new Set(nextKeys)
  const previous = new Set(previousKeys)
  const kept = previousKeys.filter((key) => next.has(key))
  const added = nextKeys.filter((key) => !previous.has(key))
  const room = Math.max(0, max - kept.length)
  return {
    keys: [...kept, ...added.slice(0, room)],
    truncated: added.length > room,
  }
}

export function buildBatchItemMap(job: BatchCheckProductsJobDto | null | undefined) {
  return new Map<string, BatchCheckProductsItemDto>((job?.items ?? []).map((item) => [item.invoiceGuid, item]))
}

/**
 * 成功的单回到列表数据显示，让「已检测 · 新品 N」与后端统一口径；
 * 失败 / 跳过保留到用户关闭进度条，排队 / 检测中只在任务运行时出现。
 */
export function getRowBatchDisplay(
  item: BatchCheckProductsItemDto | undefined,
  jobActive: boolean,
): BatchRowDisplay {
  if (!item) return null
  switch (item.status) {
    case 'Queued':
      return jobActive ? 'queued' : null
    case 'Running':
      return jobActive ? 'running' : null
    case 'Failed':
      return 'failed'
    case 'Skipped':
      return 'skipped'
    default:
      return null
  }
}

export interface BatchListRefreshState {
  /** 上次刷新列表时任务已处理的单数。 */
  refreshedProcessed: number
  lastRefreshAt: number
}

/**
 * 有新的单处理完才刷新列表；运行中按最小间隔节流，任务结束时不再等间隔，保证最后一张的结果立即出现。
 */
export function shouldRefreshListForBatch(
  job: BatchCheckProductsJobDto,
  state: BatchListRefreshState,
  now: number,
  minIntervalMs = BATCH_CHECK_LIST_REFRESH_MIN_INTERVAL_MS,
): boolean {
  if (job.processed <= state.refreshedProcessed) return false
  if (!isBatchJobActive(job)) return true
  return now - state.lastRefreshAt >= minIntervalMs
}

/** 已选单的概况：「待检测」按列表口径（isProductChecked === false）。 */
export function summarizeBatchSelection(
  keys: string[],
  recordsByGuid: Record<string, LocalSupplierInvoiceListDto | undefined>,
) {
  let pending = 0
  for (const key of keys) {
    if (recordsByGuid[key]?.isProductChecked === false) pending += 1
  }
  return { total: keys.length, pending }
}

export function pickPendingSelection(
  keys: string[],
  recordsByGuid: Record<string, LocalSupplierInvoiceListDto | undefined>,
) {
  return keys.filter((key) => recordsByGuid[key]?.isProductChecked === false)
}

/** 合并跨页勾选的行数据：本页给出的行优先，其它页的沿用之前记下的。 */
export function mergeSelectedRecords(
  keys: string[],
  rows: (LocalSupplierInvoiceListDto | undefined)[],
  previous: Record<string, LocalSupplierInvoiceListDto | undefined>,
) {
  const rowsByGuid = new Map(
    rows.filter((row): row is LocalSupplierInvoiceListDto => Boolean(row)).map((row) => [row.invoiceGUID, row]),
  )
  const next: Record<string, LocalSupplierInvoiceListDto | undefined> = {}
  for (const key of keys) {
    next[key] = rowsByGuid.get(key) ?? previous[key]
  }
  return next
}

export function readStoredBatchJobId(storage: Pick<Storage, 'getItem'> | undefined): string | null {
  try {
    const value = storage?.getItem(BATCH_CHECK_JOB_STORAGE_KEY)
    return value && /^[0-9a-f]{32}$/i.test(value) ? value : null
  } catch {
    return null
  }
}

export function writeStoredBatchJobId(
  storage: Pick<Storage, 'setItem' | 'removeItem'> | undefined,
  jobId: string | null,
) {
  try {
    if (jobId) storage?.setItem(BATCH_CHECK_JOB_STORAGE_KEY, jobId)
    else storage?.removeItem(BATCH_CHECK_JOB_STORAGE_KEY)
  } catch {
    // sessionStorage 不可用时只是回到页面后看不到进度，任务本身照常在后台执行。
  }
}
