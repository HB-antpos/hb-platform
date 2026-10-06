// 国内供应商页的纯逻辑：排序键映射、状态筛选、翻页兜底、保存载荷整理、同步结果归一化。
// 全部是无副作用的纯函数（不引入 antd / i18n），便于用 esbuild 单文件直接跑单测。
import type {
  ChinaSupplierItem,
  SaveChinaSupplierPayload,
  SyncChinaSupplierResult,
} from '../../../types/chinaSupplier'

export type SupplierFormValues = SaveChinaSupplierPayload

// ---------------------------------------------------------------------------
// 排序
// ---------------------------------------------------------------------------

/** 列表里允许点表头排序的列（列的 `key`）。其余列没有排序箭头，因为后端没有对应排序键。 */
export type SupplierSortColumn = 'supplierName' | 'shopNumber' | 'contactPerson' | 'status' | 'createdAt'

/**
 * 列 → 后端 `ChinaSupplierService.GetChinaSuppliersAsync` 认识的排序键（小写比较）。
 * 旧页面曾发 `createdAt / updatedAt`，后端落入默认分支，等于排序无效，所以这里只放后端真实支持的键。
 */
export const SUPPLIER_SORT_FIELD_BY_COLUMN: Readonly<Record<SupplierSortColumn, string>> = {
  supplierName: 'suppliername',
  shopNumber: 'shopnumber',
  contactPerson: 'contactperson',
  status: 'status',
  createdAt: 'createdate',
}

export type SupplierSortDirection = 'asc' | 'desc'
export type SupplierSortOrder = 'ascend' | 'descend' | null

export interface SupplierSortState {
  sortField: string
  sortDirection: SupplierSortDirection
}

/** 默认排序：创建时间倒序（与后端不传排序键时的默认一致）。 */
export const DEFAULT_SUPPLIER_SORT: Readonly<SupplierSortState> = {
  sortField: SUPPLIER_SORT_FIELD_BY_COLUMN.createdAt,
  sortDirection: 'desc',
}

/**
 * 把 antd 表格回传的 `columnKey + order` 翻译成后端排序参数。
 * 取消排序（order 为空）或未知列都回到默认排序，避免向后端发送它不认识的键。
 */
export function resolveSupplierSort(columnKey: unknown, order: SupplierSortOrder | undefined): SupplierSortState {
  if (!order || typeof columnKey !== 'string') {
    return { ...DEFAULT_SUPPLIER_SORT }
  }
  const sortField = SUPPLIER_SORT_FIELD_BY_COLUMN[columnKey as SupplierSortColumn]
  if (!sortField) {
    return { ...DEFAULT_SUPPLIER_SORT }
  }
  return { sortField, sortDirection: order === 'ascend' ? 'asc' : 'desc' }
}

/** 某一列当前应显示的排序箭头：只有排序键与该列对应时才高亮。 */
export function sortOrderForColumn(
  column: SupplierSortColumn,
  state: SupplierSortState,
): SupplierSortOrder {
  if (state.sortField !== SUPPLIER_SORT_FIELD_BY_COLUMN[column]) {
    return null
  }
  return state.sortDirection === 'asc' ? 'ascend' : 'descend'
}

// ---------------------------------------------------------------------------
// 筛选
// ---------------------------------------------------------------------------

export type SupplierStatusFilter = 'all' | 'enabled' | 'disabled'

/** 状态分段 → 接口参数：全部 = 不传，启用 = 1，禁用 = 0（注意 0 是有效值，不能用 `||` 判空）。 */
export function statusFilterToParam(filter: SupplierStatusFilter): number | undefined {
  return filter === 'all' ? undefined : filter === 'enabled' ? 1 : 0
}

export function statusParamToFilter(status: number | undefined): SupplierStatusFilter {
  return status === undefined ? 'all' : status === 1 ? 'enabled' : 'disabled'
}

/** 搜索框文本 → 接口参数：去掉首尾空白，空串不传。 */
export function normalizeKeyword(text: string | undefined | null): string | undefined {
  const trimmed = text?.trim()
  return trimmed ? trimmed : undefined
}

// ---------------------------------------------------------------------------
// 分页
// ---------------------------------------------------------------------------

/**
 * 删除 / 停用后当前页可能变空（例如删掉最后一页唯一的一条）。
 * 返回应该回退到的最后一页；不需要回退时返回 null。
 */
export function resolveOverflowPage(args: {
  page: number
  pageSize: number
  total: number
  itemCount: number
}): number | null {
  const { page, pageSize, total, itemCount } = args
  if (itemCount > 0 || total <= 0 || page <= 1 || pageSize <= 0) {
    return null
  }
  const lastPage = Math.max(1, Math.ceil(total / pageSize))
  return lastPage < page ? lastPage : null
}

// ---------------------------------------------------------------------------
// 展示
// ---------------------------------------------------------------------------

/**
 * 创建时间只显示日期。后端返回的是墙钟文本（`yyyy-MM-dd HH:mm:ss`），
 * 这里只做文本截取、不经 `Date` 转换，避免被浏览器时区改写；完整时间放在 tooltip 里。
 */
export function formatCreatedDate(value?: string | null): { text: string; full: string } | null {
  const full = value?.trim()
  if (!full) {
    return null
  }
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(full)
  return { text: match ? match[1] : full, full: full.replace('T', ' ') }
}

/** 店面照片预览只接受 http(s) 链接，其它协议（javascript: / file: 等）一律不渲染。 */
export function normalizePhotoUrl(value?: string | null): string | undefined {
  const text = value?.trim()
  if (!text || !/^https?:\/\/\S+$/i.test(text)) {
    return undefined
  }
  try {
    const url = new URL(text)
    return url.protocol === 'http:' || url.protocol === 'https:' ? text : undefined
  } catch {
    return undefined
  }
}

// ---------------------------------------------------------------------------
// 表单与保存
// ---------------------------------------------------------------------------

function emptyToUndefined(value?: string | null): string | undefined {
  const trimmed = value?.trim()
  return trimmed ? trimmed : undefined
}

/**
 * 表单值 → 保存载荷。
 * 关键点：后端邮箱字段带 `[EmailAddress]`，只有 null 才跳过校验，空串会被判成「邮箱格式不正确」(400)，
 * 而 antd 清空输入框得到的是 ''，所以所有可选文本字段为空时一律不发送（后端 Update 会把缺省字段写成 null，即清空）。
 */
export function buildSavePayload(values: SupplierFormValues): SaveChinaSupplierPayload {
  return {
    supplierCode: values.supplierCode.trim(),
    supplierName: values.supplierName.trim(),
    shopNumber: emptyToUndefined(values.shopNumber),
    contactPerson: emptyToUndefined(values.contactPerson),
    phone: emptyToUndefined(values.phone),
    email: emptyToUndefined(values.email),
    storefrontPhoto: emptyToUndefined(values.storefrontPhoto),
    status: values.status === 0 ? 0 : 1,
    remarks: emptyToUndefined(values.remarks),
  }
}

/** 列表行 → 编辑表单初值。 */
export function toFormValues(item: ChinaSupplierItem): SupplierFormValues {
  return {
    supplierCode: item.supplierCode,
    supplierName: item.supplierName,
    shopNumber: item.shopNumber,
    contactPerson: item.contactPerson,
    phone: item.phone,
    email: item.email,
    storefrontPhoto: item.storefrontPhoto,
    status: item.status === 0 ? 0 : 1,
    remarks: item.remarks,
  }
}

// ---------------------------------------------------------------------------
// 同步到销售库的结果
// ---------------------------------------------------------------------------

export type SyncOutcome = 'success' | 'partial' | 'failed'

export interface SyncResultView {
  totalProcessed: number
  insertedCount: number
  updatedCount: number
  failCount: number
  /** 接口返回的失败明细原文（后端格式：`{编码}({名称}): {原因}`）。 */
  errors: string[]
  /**
   * 选中但在系统里没找到的条数（例如已被别人删除）。
   * 后端的「共处理」取的是勾选数，这部分既不算成功也不算失败，不单列会让四个数字对不上账。
   */
  unmatchedCount: number
  outcome: SyncOutcome
}

function toCount(value: unknown): number {
  const numeric = Number(value)
  return Number.isFinite(numeric) && numeric > 0 ? Math.floor(numeric) : 0
}

/**
 * 归一化同步接口的返回值，并判定结果性质。
 * 只要存在失败或未找到的条目就不是 success——界面据此决定不能用成功色提示。
 */
export function normalizeSyncResult(raw: Partial<SyncChinaSupplierResult> | null | undefined): SyncResultView {
  const errors = Array.isArray(raw?.errors)
    ? raw.errors.filter((line): line is string => typeof line === 'string' && line.trim().length > 0).map((line) => line.trim())
    : []
  const insertedCount = toCount(raw?.insertedCount)
  const updatedCount = toCount(raw?.updatedCount)
  // 失败数以接口为准；接口漏报数字但给了明细时，明细条数兜底。
  const failCount = Math.max(toCount(raw?.failCount), errors.length)
  const totalProcessed = Math.max(toCount(raw?.totalProcessed), insertedCount + updatedCount + failCount)
  const unmatchedCount = Math.max(0, totalProcessed - insertedCount - updatedCount - failCount)
  const hasProblem = failCount > 0 || unmatchedCount > 0
  const outcome: SyncOutcome = !hasProblem ? 'success' : insertedCount + updatedCount > 0 ? 'partial' : 'failed'
  return { totalProcessed, insertedCount, updatedCount, failCount, errors, unmatchedCount, outcome }
}

/**
 * 把一行失败明细拆成「编码 名称」+「原因」，方便界面把标识加粗。
 * 解析不出来（格式不是 `编码(名称): 原因`）时整行当作原因原样显示，不丢信息。
 */
export function parseSyncFailure(line: string): { label: string; reason: string } {
  const text = line.trim()
  // 名称里可能含括号，用惰性匹配取第一处「): 」作为分隔。
  const match = /^([^\s()（）]+)\((.*?)\):\s*([\s\S]*)$/.exec(text)
  if (!match) {
    return { label: '', reason: text }
  }
  const [, code, name, reason] = match
  return { label: name.trim() ? `${code} ${name.trim()}` : code, reason: reason.trim() }
}

/** 「复制失败明细」的剪贴板文本：直接用后端原文，一行一条，便于粘贴给开发排查。 */
export function buildFailureClipboardText(result: Pick<SyncResultView, 'errors'>): string {
  return result.errors.join('\n')
}
