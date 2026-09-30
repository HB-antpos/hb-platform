import dayjs, { type Dayjs } from 'dayjs'

import type { LegacyEmployeeLogOperationCount, LegacyEmployeeLogQueryParams } from '../../../types/legacyEmployeeLog'

export const LEGACY_LOG_MAX_RANGE_DAYS = 31
export const LEGACY_LOG_DEFAULT_PAGE_SIZE = 50
/** 上传滞后超过该分钟数标橙：用来解释“刚才的操作为什么还查不到”。 */
export const LEGACY_LOG_LATE_UPLOAD_MINUTES = 60
export const LEGACY_LOG_STORE_STORAGE_KEY = 'hb.legacyEmployeeLogs.storeCodes'

/** 去空白、去重、保持选择顺序。 */
export function normalizeStoreCodes(codes: readonly (string | null | undefined)[] | null | undefined) {
  const seen = new Set<string>()
  const result: string[] = []
  ;(codes ?? []).forEach((code) => {
    const trimmed = code?.trim()
    if (trimmed && !seen.has(trimmed)) {
      seen.add(trimmed)
      result.push(trimmed)
    }
  })
  return result
}

/** 分店选择的比较键：与顺序无关，用来判断选择是否真的变了。 */
export function storeSelectionKey(codes: readonly string[] | null | undefined) {
  return [...normalizeStoreCodes(codes)].sort().join(',')
}

/**
 * 解析记住的分店（JSON 数组；兼容早期只存单个编码的纯文本），只保留当前账号仍可选的分店。
 * 都不可选时，只有一个可选分店就直接选它。
 */
export function resolveInitialStores(raw: string | null | undefined, visibleCodes: readonly string[]) {
  const visible = new Set(visibleCodes)
  let remembered: string[] = []
  if (raw) {
    try {
      const parsed: unknown = JSON.parse(raw)
      remembered = Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : [String(parsed)]
    } catch {
      remembered = [raw]
    }
  }
  const kept = normalizeStoreCodes(remembered).filter((code) => visible.has(code))
  if (kept.length > 0) return kept
  return visibleCodes.length === 1 ? [visibleCodes[0]] : []
}

export type LegacyOperationCategory = 'item' | 'price' | 'delete' | 'payment' | 'return' | 'auth' | 'window' | 'other'

/**
 * 旧收银写入的操作名称（2026-09-30 生产抽样）按业务归类，用于上色与快捷筛选。
 * 未收录的新名称归入 other，仍可从服务端返回的计数里选到。
 */
const OPERATION_CATEGORY_ENTRIES: [LegacyOperationCategory, string[]][] = [
  ['item', ['搜索商品', '搜索商品多结果', '添加商品', '添加无码商品', '增加商品数量', '减少商品数量', '修改商品数量']],
  ['price', ['修改商品价格', '修改商品折扣', '修改所有商品折扣']],
  ['delete', ['删除商品']],
  ['payment', ['结账', '支付完成', '挂单', '恢复挂单', '重打印']],
  ['return', ['退货授权', '添加退货商品', '无小票退货成功']],
  ['auth', ['开钱箱', '身份确认', '登录', '用户登录', '切换用户']],
  ['window', ['打开特殊商品窗口', '打开更多功能', '打开历史订单']],
]

const OPERATION_CATEGORY = new Map<string, LegacyOperationCategory>(
  OPERATION_CATEGORY_ENTRIES.flatMap(([category, names]) => names.map((name) => [name, category] as const)),
)

export const KNOWN_LEGACY_OPERATIONS = OPERATION_CATEGORY_ENTRIES.flatMap(([, names]) => names)

/** 高风险：直接影响收款金额或现金的操作。 */
export const HIGH_RISK_OPERATIONS = [
  '删除商品',
  '修改商品价格',
  '修改商品折扣',
  '修改所有商品折扣',
  '开钱箱',
  '无小票退货成功',
  '退货授权',
  '重打印',
]
const HIGH_RISK_SET = new Set(HIGH_RISK_OPERATIONS)

export const CATEGORY_TAG_COLOR: Record<LegacyOperationCategory, string> = {
  item: 'blue',
  price: 'orange',
  delete: 'red',
  payment: 'green',
  return: 'magenta',
  auth: 'purple',
  window: 'default',
  other: 'default',
}

export type LegacyQuickFilterKey = 'highRisk' | 'price' | 'return' | 'auth' | 'payment'

export const QUICK_FILTER_OPERATIONS: Record<LegacyQuickFilterKey, string[]> = {
  highRisk: HIGH_RISK_OPERATIONS,
  price: operationsOf('price'),
  return: operationsOf('return'),
  auth: operationsOf('auth'),
  payment: operationsOf('payment'),
}

function operationsOf(category: LegacyOperationCategory) {
  return OPERATION_CATEGORY_ENTRIES.find(([key]) => key === category)?.[1] ?? []
}

export function getOperationCategory(operation?: string | null): LegacyOperationCategory {
  return (operation && OPERATION_CATEGORY.get(operation.trim())) || 'other'
}

export function isHighRiskOperation(operation?: string | null) {
  return Boolean(operation && HIGH_RISK_SET.has(operation.trim()))
}

/** 快捷筛选是否与当前所选操作类型完全一致（顺序无关），用于高亮对应的筛选芯片。 */
export function isQuickFilterActive(key: LegacyQuickFilterKey, selected: readonly string[] | undefined) {
  const expected = QUICK_FILTER_OPERATIONS[key]
  if (!selected || selected.length !== expected.length) return false
  const set = new Set(selected)
  return expected.every((operation) => set.has(operation))
}

/** 操作类型下拉：已知名称在前（保持业务顺序），再补上服务端计数里出现的未知名称。 */
export function buildOperationOptions(counts: readonly LegacyEmployeeLogOperationCount[]) {
  const countMap = new Map<string, number>()
  counts.forEach((row) => {
    if (row.operation) countMap.set(row.operation, row.count)
  })
  const unknown = [...countMap.keys()]
    .filter((operation) => !OPERATION_CATEGORY.has(operation))
    .sort((a, b) => a.localeCompare(b, 'zh-CN'))
  return [...KNOWN_LEGACY_OPERATIONS, ...unknown].map((operation) => ({
    operation,
    count: countMap.get(operation) ?? 0,
    category: getOperationCategory(operation),
  }))
}

export function sumOperationCounts(counts: readonly LegacyEmployeeLogOperationCount[], operations: readonly string[]) {
  const set = new Set(operations)
  return counts.reduce((sum, row) => (row.operation && set.has(row.operation) ? sum + row.count : sum), 0)
}

export type DetailTone = 'danger' | 'money' | undefined

export interface ParsedDetailField {
  key: string
  value: string
  tone?: DetailTone
}

export interface ParsedLegacyDetail {
  /** 商品名称（「商品」「从购物车删除商品」等键的值），单独突出显示。 */
  productName?: string
  /** 没有「键:值」结构的片段，原样显示。 */
  texts: string[]
  fields: ParsedDetailField[]
  orderGuid?: string
}

const GUID_PATTERN = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i
const PRODUCT_KEYS = new Set(['商品', '从购物车删除商品'])
const DANGER_KEYS = new Set(['新价格', '新折扣', '将所有商品折扣率设置为'])
const KEY_ALIASES: Record<string, string> = { 将所有商品折扣率设置为: '折扣率' }

/**
 * 解析旧收银写入的详情文本。格式是以全角逗号分隔的「键:值」片段，例如
 * 「从购物车删除商品: XMAS ＄2 CARDS，编码:xmascard2，数量:5，单价:2.00，总金额:10.00」。
 * 只按全角逗号切分（商品名里可能有半角逗号），键取第一个冒号之前的文字；解析不了的片段原样保留。
 */
export function parseLegacyDetail(detail: string | null | undefined, operation?: string | null): ParsedLegacyDetail {
  const result: ParsedLegacyDetail = { texts: [], fields: [] }
  if (!detail) return result
  const category = getOperationCategory(operation)

  detail.split('，').forEach((rawSegment) => {
    const segment = rawSegment.trim()
    if (!segment) return
    const match = /^([^:：]{1,20})[:：]\s*(.*)$/s.exec(segment)
    if (!match) {
      result.texts.push(segment)
      return
    }
    const rawKey = match[1].trim()
    let value = match[2].trim()
    const guid = GUID_PATTERN.exec(value)?.[0]
    if (guid && /订单/.test(rawKey)) {
      // 「订单:<GUID>支付成功」：订单号单独提取，其后的文字作为说明保留。
      result.orderGuid = guid
      const rest = value.replace(guid, '').trim()
      if (rest) result.texts.push(rest)
      return
    }
    if (PRODUCT_KEYS.has(rawKey) && !result.productName) {
      result.productName = value
      return
    }
    value = value.replace(/\s+/g, ' ')
    let tone: DetailTone
    if (DANGER_KEYS.has(rawKey)) tone = 'danger'
    else if (category === 'delete' && rawKey === '总金额') tone = 'danger'
    else if (category === 'return' && rawKey === '新数量') tone = 'danger'
    else if (category === 'payment' && rawKey === '金额') tone = 'money'
    result.fields.push({ key: KEY_ALIASES[rawKey] ?? rawKey, value, tone })
  })
  return result
}

/** 上传滞后（分钟）；时间解析失败或上传早于操作（收银机时钟偏差）时返回 null。 */
export function getUploadLagMinutes(operationTime: string, lastUploadTime: string) {
  const operated = dayjs(operationTime)
  const uploaded = dayjs(lastUploadTime)
  if (!operated.isValid() || !uploaded.isValid()) return null
  const minutes = uploaded.diff(operated, 'minute')
  return minutes < 0 ? null : minutes
}

export function splitLagMinutes(minutes: number) {
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  return { days, hours, minutes: minutes % 60 }
}

/** 时间范围校验：结束必须晚于开始，且不超过 31 天。 */
export function validateTimeRange(range: [Dayjs, Dayjs] | null | undefined): 'required' | 'reversed' | 'tooLong' | null {
  if (!range || !range[0] || !range[1]) return 'required'
  const [from, toExclusive] = toQueryRange(range)
  if (!toExclusive.isAfter(from)) return 'reversed'
  if (toExclusive.diff(from, 'millisecond') > LEGACY_LOG_MAX_RANGE_DAYS * 24 * 3600 * 1000) return 'tooLong'
  return null
}

/**
 * 选择器精确到分钟：开始取该分钟起点，结束取所选分钟的下一分钟（半开区间），
 * 这样选「00:00 ~ 23:59」正好覆盖一整天，与后端 [From, To) 比较一致。
 */
export function toQueryRange(range: [Dayjs, Dayjs]): [Dayjs, Dayjs] {
  return [range[0].startOf('minute'), range[1].startOf('minute').add(1, 'minute')]
}

/** 墙钟时间按字面格式化，不带时区，后端直接与库内 OperationTime 比较。 */
export function formatWallClock(value: Dayjs) {
  return value.format('YYYY-MM-DDTHH:mm:ss')
}

export function getDayRange(day: Dayjs): [Dayjs, Dayjs] {
  return [day.startOf('day'), day.endOf('day').startOf('minute')]
}

export interface LegacyLogFormValues {
  timeRange?: [Dayjs, Dayjs]
  storeCodes?: string[]
  deviceCode?: string
  employeeIds?: string[]
  operations?: string[]
  keyword?: string
}

export function buildLegacyLogQuery(
  values: LegacyLogFormValues,
  page: { pageNumber: number; pageSize: number; sortOrder: 'asc' | 'desc' },
): LegacyEmployeeLogQueryParams | null {
  const storeCodes = normalizeStoreCodes(values.storeCodes)
  if (storeCodes.length === 0 || !values.timeRange || validateTimeRange(values.timeRange)) return null
  const [from, to] = toQueryRange(values.timeRange)
  const keyword = values.keyword?.trim()
  return {
    storeCodes,
    from: formatWallClock(from),
    to: formatWallClock(to),
    deviceCode: values.deviceCode?.trim() || undefined,
    employeeIds: values.employeeIds?.length ? values.employeeIds : undefined,
    operations: values.operations?.length ? values.operations : undefined,
    keyword: keyword || undefined,
    pageNumber: page.pageNumber,
    pageSize: page.pageSize,
    sortOrder: page.sortOrder,
  }
}

/** 只有最新一次请求的结果可以落到界面上，避免慢请求晚到覆盖新条件的结果。 */
export function createLatestRequestGuard() {
  let latest = 0
  return {
    begin: () => ++latest,
    isLatest: (id: number) => id === latest,
  }
}
