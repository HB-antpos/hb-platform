import type { ApiResponse } from '../types/api'
import type {
  ConflictDto,
  HookModelDto,
  HookModelInput,
  ModelListQuery,
  PlanogramConflictSeverity,
  PlanogramCreateInput,
  PlanogramDetailDto,
  PlanogramExportDto,
  PlanogramExportItemDto,
  PlanogramHangHoleType,
  PlanogramImportInput,
  PlanogramItemDto,
  PlanogramListItemDto,
  PlanogramListQuery,
  PlanogramPaged,
  PlanogramProngCount,
  PlanogramRowNumberingFrom,
  PlanogramSaveInput,
  PlanogramSideType,
  PlanogramSnapshotDto,
  PlanogramSnapshotProductDto,
  PlanogramSpecSource,
  PlanogramStatsDto,
  PlanogramStatus,
  PlanogramValidateInput,
  PlanogramValidateResult,
  PlanogramVersionSummaryDto,
  ProductDisplaySpecDto,
  ProductSpecListItemDto,
  ProductSpecListQuery,
  ShelfModelDto,
  ShelfModelInput,
} from '../types/planogram'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

/**
 * 商品陈列图接口（基础路径 api/react/v1/planogram；批 1：货架型号 / 挂钩型号 / 商品陈列参数；批 2：陈列方案）。
 * 响应统一包在 ApiResponse<T> 里：HTTP 非 2xx 由 request 抛 RequestError（payload 里带 errorCode 与可直接展示的中文 message）；
 * HTTP 200 但 success=false 由 unwrapApiData 抛 RequestError。这里只做信封解包与字段兜底：
 * 数字字段缺失 / 是字符串时转成数字，数组 / 字典缺失时给空值，避免页面 map / 取值报错；不改写任何业务数值。
 */

export const PLANOGRAM_API_BASE = '/api/react/v1/planogram'

/** 型号下拉 / 挂数表一次取全部型号用的单页上限（型号只有几个到几十个）。 */
export const PLANOGRAM_MODEL_CATALOG_PAGE_SIZE = 100

type Envelope<T> = ApiResponse<T> | T

function planogramUrl(path: string) {
  return `${PLANOGRAM_API_BASE}/${path}`
}

function num(value: unknown, fallback = 0): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value)
    if (Number.isFinite(parsed)) return parsed
  }
  return fallback
}

function nullableNum(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null
  const parsed = num(value, Number.NaN)
  return Number.isNaN(parsed) ? null : parsed
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function nullableText(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null
}

function pick<T extends number>(value: unknown, allowed: readonly T[], fallback: T): T {
  const parsed = num(value, Number.NaN)
  return (allowed as readonly number[]).includes(parsed) ? (parsed as T) : fallback
}

export function normalizeShelfModel(raw: Partial<ShelfModelDto> | null | undefined): ShelfModelDto {
  const r = (raw ?? {}) as Record<string, unknown>
  return {
    guid: text(r.guid),
    code: text(r.code),
    name: text(r.name),
    sideType: pick<PlanogramSideType>(r.sideType, [1, 2], 1),
    color: nullableText(r.color),
    source: nullableText(r.source),
    bayWidthMm: num(r.bayWidthMm),
    totalHeightMm: num(r.totalHeightMm),
    baseDepthMm: num(r.baseDepthMm),
    baseTopHeightMm: num(r.baseTopHeightMm),
    uprightWidthMm: num(r.uprightWidthMm),
    holePitchXMm: num(r.holePitchXMm),
    holePitchYMm: num(r.holePitchYMm),
    holeDiameterMm: num(r.holeDiameterMm),
    firstHoleLeftMm: num(r.firstHoleLeftMm),
    firstHoleTopMm: num(r.firstHoleTopMm),
    holeCols: num(r.holeCols),
    holeRows: num(r.holeRows),
    rowNumberingFrom: pick<PlanogramRowNumberingFrom>(r.rowNumberingFrom, [1, 2], 1),
    isActive: r.isActive !== false,
    referencedPlanCount: num(r.referencedPlanCount),
    updatedAt: nullableText(r.updatedAt),
    updatedBy: nullableText(r.updatedBy),
  }
}

export function normalizeHookModel(raw: Partial<HookModelDto> | null | undefined): HookModelDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const prongCount = pick<PlanogramProngCount>(r.prongCount, [1, 2], 2)
  return {
    guid: text(r.guid),
    code: text(r.code),
    name: text(r.name),
    color: nullableText(r.color),
    prongCount,
    hasPriceTagArm: r.hasPriceTagArm === true,
    armLengthMm: num(r.armLengthMm),
    dropMm: num(r.dropMm),
    wireDiameterMm: num(r.wireDiameterMm),
    tipReserveMm: num(r.tipReserveMm),
    tagWidthMm: num(r.tagWidthMm),
    tagHeightMm: num(r.tagHeightMm),
    // 缺省按附录 A 的默认值 10；只有字段缺失时兜底，后端明确给 0（无价签臂）就保持 0。
    tagBottomAboveArmMm: num(r.tagBottomAboveArmMm, 10),
    holeSpanX: num(r.holeSpanX, prongCount === 2 ? 2 : 1),
    holeSpanY: num(r.holeSpanY, 1),
    loadKg: nullableNum(r.loadKg),
    isActive: r.isActive !== false,
    referencedPlanCount: num(r.referencedPlanCount),
    updatedAt: nullableText(r.updatedAt),
    updatedBy: nullableText(r.updatedBy),
  }
}

/** 覆盖表只保留「键是字符串、值是数字」的项，其余（脏数据）丢掉。 */
function normalizeOverrides(raw: unknown): Record<string, number> {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {}
  const result: Record<string, number> = {}
  for (const [key, value] of Object.entries(raw as Record<string, unknown>)) {
    const parsed = nullableNum(value)
    if (parsed !== null) result[key] = parsed
  }
  return result
}

export function normalizeProductSpec(raw: Partial<ProductDisplaySpecDto> | null | undefined): ProductDisplaySpecDto | null {
  if (!raw || typeof raw !== 'object') return null
  const r = raw as Record<string, unknown>
  return {
    productCode: text(r.productCode),
    widthMm: num(r.widthMm),
    heightMm: num(r.heightMm),
    depthMm: num(r.depthMm),
    hangHoleOffsetMm: num(r.hangHoleOffsetMm, 10),
    hangHoleType: pick<PlanogramHangHoleType>(r.hangHoleType, [0, 1, 2], 2),
    unitWeightG: nullableNum(r.unitWeightG),
    defaultHookGuid: nullableText(r.defaultHookGuid),
    hookQtyOverrides: normalizeOverrides(r.hookQtyOverrides),
    source: pick<PlanogramSpecSource>(r.source, [1, 2, 3], 1),
    notes: nullableText(r.notes),
  }
}

export function normalizeProductSpecItem(raw: Partial<ProductSpecListItemDto> | null | undefined): ProductSpecListItemDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const spec = normalizeProductSpec(r.spec as Partial<ProductDisplaySpecDto> | null | undefined)
  return {
    productCode: text(r.productCode),
    itemNumber: nullableText(r.itemNumber),
    barcode: nullableText(r.barcode),
    productName: text(r.productName),
    englishName: nullableText(r.englishName),
    productImage: nullableText(r.productImage),
    retailPrice: num(r.retailPrice),
    categoryName: nullableText(r.categoryName),
    // hasSpec 缺失时以 spec 是否存在为准，避免已设参数的行被显示成「补充参数」。
    hasSpec: typeof r.hasSpec === 'boolean' ? r.hasSpec : spec !== null,
    referencedPlanCount: num(r.referencedPlanCount),
    spec,
    updatedAt: nullableText(r.updatedAt),
    updatedBy: nullableText(r.updatedBy),
  }
}

function normalizePaged<T, R>(
  payload: Envelope<{ items?: R[]; total?: number; page?: number; pageSize?: number }>,
  map: (item: R) => T,
  fallbackPageSize: number,
): PlanogramPaged<T> {
  const result = unwrapPagedResult(payload as ApiResponse<{ items: R[]; total: number; page: number; pageSize: number }>)
  return {
    items: (Array.isArray(result.items) ? result.items : []).map(map),
    total: result.total,
    page: result.page,
    pageSize: result.pageSize || fallbackPageSize,
  }
}

function modelListParams(query: ModelListQuery) {
  return {
    keyword: query.keyword?.trim() || undefined,
    isActive: query.isActive,
    page: query.page,
    pageSize: query.pageSize,
  }
}

// ---------------- 货架型号 ----------------

export async function listShelfModels(query: ModelListQuery, signal?: AbortSignal): Promise<PlanogramPaged<ShelfModelDto>> {
  const payload = await request<Envelope<PlanogramPaged<ShelfModelDto>>>(planogramUrl('shelf-models'), {
    method: 'GET',
    signal,
    params: modelListParams(query),
  })
  return normalizePaged(payload, normalizeShelfModel, query.pageSize)
}

export async function createShelfModel(input: ShelfModelInput): Promise<ShelfModelDto> {
  const payload = await request<Envelope<ShelfModelDto>>(planogramUrl('shelf-models'), { method: 'POST', data: input })
  return normalizeShelfModel(unwrapApiData(payload))
}

export async function updateShelfModel(guid: string, input: ShelfModelInput): Promise<ShelfModelDto> {
  const payload = await request<Envelope<ShelfModelDto>>(planogramUrl(`shelf-models/${encodeURIComponent(guid)}`), {
    method: 'PUT',
    data: input,
  })
  return normalizeShelfModel(unwrapApiData(payload))
}

/** 被方案引用时后端返回 PLANOGRAM_MODEL_IN_USE，调用方改为提示「改为停用」。 */
export async function deleteShelfModel(guid: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`shelf-models/${encodeURIComponent(guid)}`), { method: 'DELETE' })
  unwrapApiData(payload)
}

// ---------------- 挂钩型号 ----------------

export async function listHookModels(query: ModelListQuery, signal?: AbortSignal): Promise<PlanogramPaged<HookModelDto>> {
  const payload = await request<Envelope<PlanogramPaged<HookModelDto>>>(planogramUrl('hook-models'), {
    method: 'GET',
    signal,
    params: modelListParams(query),
  })
  return normalizePaged(payload, normalizeHookModel, query.pageSize)
}

export async function createHookModel(input: HookModelInput): Promise<HookModelDto> {
  const payload = await request<Envelope<HookModelDto>>(planogramUrl('hook-models'), { method: 'POST', data: input })
  return normalizeHookModel(unwrapApiData(payload))
}

export async function updateHookModel(guid: string, input: HookModelInput): Promise<HookModelDto> {
  const payload = await request<Envelope<HookModelDto>>(planogramUrl(`hook-models/${encodeURIComponent(guid)}`), {
    method: 'PUT',
    data: input,
  })
  return normalizeHookModel(unwrapApiData(payload))
}

export async function deleteHookModel(guid: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`hook-models/${encodeURIComponent(guid)}`), { method: 'DELETE' })
  unwrapApiData(payload)
}

/** 取全部挂钩型号（含停用）：商品参数页的默认挂钩名称与每钩挂数表用，型号数量很少，一页取完。 */
export async function listAllHookModels(signal?: AbortSignal): Promise<HookModelDto[]> {
  const result = await listHookModels({ page: 1, pageSize: PLANOGRAM_MODEL_CATALOG_PAGE_SIZE }, signal)
  return result.items
}

// ---------------- 商品陈列参数 ----------------

export async function listProductSpecs(
  query: ProductSpecListQuery,
  signal?: AbortSignal,
): Promise<PlanogramPaged<ProductSpecListItemDto>> {
  const payload = await request<Envelope<PlanogramPaged<ProductSpecListItemDto>>>(planogramUrl('product-specs'), {
    method: 'GET',
    signal,
    params: {
      keyword: query.keyword?.trim() || undefined,
      status: query.status,
      page: query.page,
      pageSize: query.pageSize,
    },
  })
  return normalizePaged(payload, normalizeProductSpecItem, query.pageSize)
}

export async function getProductSpec(productCode: string, signal?: AbortSignal): Promise<ProductSpecListItemDto> {
  const payload = await request<Envelope<ProductSpecListItemDto>>(
    planogramUrl(`product-specs/${encodeURIComponent(productCode)}`),
    { method: 'GET', signal },
  )
  return normalizeProductSpecItem(unwrapApiData(payload))
}

/** 保存参数；hookQtyOverrides 里的 hookGuid 必须存在、值 ≥ 1，调用方保存前先剔除无效项。 */
export async function saveProductSpec(productCode: string, spec: ProductDisplaySpecDto): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`product-specs/${encodeURIComponent(productCode)}`), {
    method: 'PUT',
    data: spec,
  })
  unwrapApiData(payload)
}

/** 清除参数（Manage 权限）；批 2 起被方案引用时后端拒绝。 */
export async function deleteProductSpec(productCode: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`product-specs/${encodeURIComponent(productCode)}`), {
    method: 'DELETE',
  })
  unwrapApiData(payload)
}

// ---------------- 陈列方案（批 2，附录 B.2） ----------------

function normalizeStatus(value: unknown): PlanogramStatus {
  return pick<PlanogramStatus>(value, [0, 1, 2], 0)
}

export function normalizePlanListItem(raw: Partial<PlanogramListItemDto> | null | undefined): PlanogramListItemDto {
  const r = (raw ?? {}) as Record<string, unknown>
  return {
    guid: text(r.guid),
    name: text(r.name),
    notes: nullableText(r.notes),
    shelfModelGuid: text(r.shelfModelGuid),
    shelfModelName: text(r.shelfModelName),
    bayCount: num(r.bayCount, 1),
    // 间距缺省按系统默认 10（附录 B.2）；后端明确给 0（上下不留间距）就保持 0。
    gapXMm: num(r.gapXMm, 10),
    gapYMm: num(r.gapYMm, 10),
    status: normalizeStatus(r.status),
    version: num(r.version, 1),
    publishedVersion: nullableNum(r.publishedVersion),
    publishedAt: nullableText(r.publishedAt),
    itemCount: num(r.itemCount),
    skuCount: num(r.skuCount),
    unitCount: num(r.unitCount),
    fillPercent: num(r.fillPercent),
    conflictCount: num(r.conflictCount),
    // 附录 C 补充字段：缺省按 false（后端始终会给；老版本后端没有这个字段时当作「没有未发布改动」）
    hasUnpublishedChanges: r.hasUnpublishedChanges === true,
    updatedAt: nullableText(r.updatedAt),
    updatedBy: nullableText(r.updatedBy),
  }
}

export function normalizePlanItem(raw: Partial<PlanogramItemDto> | null | undefined): PlanogramItemDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const perHookQty = nullableNum(r.perHookQty)
  return {
    guid: text(r.guid),
    bayIndex: num(r.bayIndex, 1),
    face: 0,
    itemType: 1,
    productCode: text(r.productCode),
    hookModelGuid: text(r.hookModelGuid),
    holeCol: num(r.holeCol),
    holeRow: num(r.holeRow),
    // 0 / 负数没有意义（后端按「没填」处理），统一成 null，设计器里就不会出现「每钩 0 件」的手填值
    perHookQty: perHookQty !== null && perHookQty >= 1 ? perHookQty : null,
    sortOrder: num(r.sortOrder),
    specChanged: r.specChanged === true,
  }
}

export function normalizeConflict(raw: Partial<ConflictDto> | null | undefined): ConflictDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const severity: PlanogramConflictSeverity = r.severity === 'warn' ? 'warn' : 'error'
  return {
    itemGuid: text(r.itemGuid),
    otherItemGuid: nullableText(r.otherItemGuid),
    code: text(r.code),
    severity,
    message: text(r.message),
  }
}

export function normalizePlanStats(raw: Partial<PlanogramStatsDto> | null | undefined): PlanogramStatsDto {
  const r = (raw ?? {}) as Record<string, unknown>
  return {
    itemCount: num(r.itemCount),
    skuCount: num(r.skuCount),
    unitCount: num(r.unitCount),
    retailValue: num(r.retailValue),
    fillPercent: num(r.fillPercent),
    conflictCount: num(r.conflictCount),
  }
}

function asArray<R>(value: unknown): R[] {
  return Array.isArray(value) ? (value as R[]) : []
}

/**
 * 详情归一化。契约是 { header: {...}, rowVersion, items, shelfModel, hookModels, productSpecs, conflicts, stats }；
 * 为稳妥也兼容「header 字段直接平铺在顶层」的写法（附录 B.2 文字是「header(同 ListItem 字段) + rowVersion + …」，两种读法都说得通）。
 */
export function normalizePlanDetail(raw: Partial<PlanogramDetailDto> | null | undefined): PlanogramDetailDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const headerSource = r.header && typeof r.header === 'object' ? (r.header as Partial<PlanogramListItemDto>) : (r as Partial<PlanogramListItemDto>)
  return {
    header: normalizePlanListItem(headerSource),
    rowVersion: text(r.rowVersion),
    items: asArray<Partial<PlanogramItemDto>>(r.items).map(normalizePlanItem),
    shelfModel: normalizeShelfModel(r.shelfModel as Partial<ShelfModelDto> | undefined),
    hookModels: asArray<Partial<HookModelDto>>(r.hookModels).map(normalizeHookModel),
    productSpecs: asArray<Partial<ProductSpecListItemDto>>(r.productSpecs).map(normalizeProductSpecItem),
    conflicts: asArray<Partial<ConflictDto>>(r.conflicts).map(normalizeConflict),
    stats: normalizePlanStats(r.stats as Partial<PlanogramStatsDto> | undefined),
  }
}

export async function listPlans(query: PlanogramListQuery, signal?: AbortSignal): Promise<PlanogramPaged<PlanogramListItemDto>> {
  const payload = await request<Envelope<PlanogramPaged<PlanogramListItemDto>>>(planogramUrl('planograms'), {
    method: 'GET',
    signal,
    params: {
      shelfModelGuid: query.shelfModelGuid || undefined,
      status: query.status,
      keyword: query.keyword?.trim() || undefined,
      page: query.page,
      pageSize: query.pageSize,
    },
  })
  return normalizePaged(payload, normalizePlanListItem, query.pageSize)
}

/** 新建：status=0、version=1，返回完整详情（可直接进设计器）。 */
export async function createPlan(input: PlanogramCreateInput): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl('planograms'), { method: 'POST', data: input })
  return normalizePlanDetail(unwrapApiData(payload))
}

export async function getPlan(guid: string, signal?: AbortSignal): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl(`planograms/${encodeURIComponent(guid)}`), {
    method: 'GET',
    signal,
  })
  return normalizePlanDetail(unwrapApiData(payload))
}

/**
 * 整体保存头 + 明细（items 整体替换）：rowVersion 不一致 → PLANOGRAM_STALE，已归档 → PLANOGRAM_ARCHIVED；
 * 已发布的方案保存后 version+1、status 回到 0；返回保存后的详情（含服务端复算的 conflicts）。
 */
export async function savePlan(guid: string, input: PlanogramSaveInput): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl(`planograms/${encodeURIComponent(guid)}`), {
    method: 'PUT',
    data: input,
  })
  return normalizePlanDetail(unwrapApiData(payload))
}

/** 只校验不保存：请求体同 PUT（不含 rowVersion），返回冲突与统计。 */
export async function validatePlan(guid: string, input: PlanogramValidateInput, signal?: AbortSignal): Promise<PlanogramValidateResult> {
  const payload = await request<Envelope<PlanogramValidateResult>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/validate`), {
    method: 'POST',
    data: input,
    signal,
  })
  const data = unwrapApiData(payload) as Partial<PlanogramValidateResult> | null | undefined
  return {
    conflicts: asArray<Partial<ConflictDto>>(data?.conflicts).map(normalizeConflict),
    stats: normalizePlanStats(data?.stats),
  }
}

/** 另存为：新方案（草稿、version=1、明细全复制、guid 全新），返回新方案详情。 */
export async function duplicatePlan(guid: string, name: string): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/duplicate`), {
    method: 'POST',
    data: { name },
  })
  return normalizePlanDetail(unwrapApiData(payload))
}

/** 归档（Manage 权限）。 */
export async function archivePlan(guid: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/archive`), { method: 'POST' })
  unwrapApiData(payload)
}

/** 恢复为草稿（Manage 权限）。 */
export async function restorePlan(guid: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/restore`), { method: 'POST' })
  unwrapApiData(payload)
}

/** 删除（Manage 权限）：仅草稿且从未发布可删，否则 PLANOGRAM_NOT_DELETABLE。 */
export async function deletePlan(guid: string): Promise<void> {
  const payload = await request<Envelope<unknown>>(planogramUrl(`planograms/${encodeURIComponent(guid)}`), { method: 'DELETE' })
  unwrapApiData(payload)
}

// ---------------- 发布 / 版本 / JSON 导入导出（批 3，附录 C） ----------------

/**
 * 发布（Manage 权限）：请求体 { rowVersion }。失败的错误码：
 * PLANOGRAM_HAS_CONFLICTS（details 是冲突列表）/ PLANOGRAM_EMPTY / PLANOGRAM_ALREADY_PUBLISHED / PLANOGRAM_STALE / PLANOGRAM_ARCHIVED。
 * 成功返回发布后的详情（status=1、publishedVersion = 发布的版本号、新的 rowVersion）。
 */
export async function publishPlan(guid: string, rowVersion: string): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/publish`), {
    method: 'POST',
    data: { rowVersion },
  })
  return normalizePlanDetail(unwrapApiData(payload))
}

export function normalizeVersionSummary(raw: Partial<PlanogramVersionSummaryDto> | null | undefined): PlanogramVersionSummaryDto {
  const r = (raw ?? {}) as Record<string, unknown>
  return {
    version: num(r.version),
    publishedAt: nullableText(r.publishedAt),
    publishedBy: nullableText(r.publishedBy),
    itemCount: num(r.itemCount),
    skuCount: num(r.skuCount),
    unitCount: num(r.unitCount),
  }
}

/** 历史版本列表（View 权限），按版本倒序（这里再排一次，不依赖后端顺序）。 */
export async function listPlanVersions(guid: string, signal?: AbortSignal): Promise<PlanogramVersionSummaryDto[]> {
  const payload = await request<Envelope<PlanogramVersionSummaryDto[]>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/versions`), {
    method: 'GET',
    signal,
  })
  return asArray<Partial<PlanogramVersionSummaryDto>>(unwrapApiData(payload))
    .map(normalizeVersionSummary)
    .sort((a, b) => b.version - a.version)
}

function normalizeSnapshotProduct(raw: Partial<PlanogramSnapshotProductDto> | null | undefined): PlanogramSnapshotProductDto | null {
  const r = (raw ?? {}) as Record<string, unknown>
  const spec = normalizeProductSpec(r.spec as Partial<ProductDisplaySpecDto> | null | undefined)
  // 快照里的商品必有陈列参数（发布时才会写进去）；缺参数的脏数据丢掉，对应明细在版面里按「缺参数」处理
  if (!spec) return null
  const productCode = text(r.productCode) || spec.productCode
  return {
    productCode,
    itemNumber: text(r.itemNumber),
    barcode: text(r.barcode),
    productName: text(r.productName),
    englishName: nullableText(r.englishName),
    productImage: nullableText(r.productImage),
    retailPrice: num(r.retailPrice),
    spec: { ...spec, productCode },
  }
}

/** 发布快照归一化：数组 / 数字兜底，字段名照附录 C 的 PlanogramSnapshotDto。 */
export function normalizeSnapshot(raw: Partial<PlanogramSnapshotDto> | null | undefined): PlanogramSnapshotDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const plan = (r.plan && typeof r.plan === 'object' ? r.plan : {}) as Record<string, unknown>
  return {
    version: num(r.version),
    publishedAt: nullableText(r.publishedAt),
    publishedBy: nullableText(r.publishedBy),
    plan: {
      guid: text(plan.guid),
      name: text(plan.name),
      notes: nullableText(plan.notes),
      bayCount: num(plan.bayCount, 1),
      gapXMm: num(plan.gapXMm, 10),
      gapYMm: num(plan.gapYMm, 10),
    },
    shelfModel: normalizeShelfModel(r.shelfModel as Partial<ShelfModelDto> | undefined),
    hookModels: asArray<Partial<HookModelDto>>(r.hookModels).map(normalizeHookModel),
    products: asArray<Partial<PlanogramSnapshotProductDto>>(r.products)
      .map(normalizeSnapshotProduct)
      .filter((product): product is PlanogramSnapshotProductDto => product !== null),
    items: asArray<Partial<PlanogramItemDto>>(r.items).map(normalizePlanItem),
    stats: normalizePlanStats(r.stats as Partial<PlanogramStatsDto> | undefined),
  }
}

/** 某个历史版本的快照（View 权限）；快照被改坏时后端返回 PLANOGRAM_SNAPSHOT_INVALID。 */
export async function getPlanVersion(guid: string, version: number, signal?: AbortSignal): Promise<PlanogramSnapshotDto> {
  const payload = await request<Envelope<PlanogramSnapshotDto>>(
    planogramUrl(`planograms/${encodeURIComponent(guid)}/versions/${encodeURIComponent(String(version))}`),
    { method: 'GET', signal },
  )
  return normalizeSnapshot(unwrapApiData(payload))
}

function normalizeExportItem(raw: Partial<PlanogramExportItemDto> | null | undefined): PlanogramExportItemDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const perHookQty = nullableNum(r.perHookQty)
  return {
    bayIndex: num(r.bayIndex, 1),
    productCode: text(r.productCode),
    hookModelCode: text(r.hookModelCode),
    holeCol: num(r.holeCol),
    holeRow: num(r.holeRow),
    // 省略 = 按规则；写进文件时 null 也不输出，保持文件干净
    ...(perHookQty !== null && perHookQty >= 1 ? { perHookQty } : {}),
  }
}

/** 导出文件内容归一化（也用于导入前解析用户选的文件：缺字段时给出可被后端校验拒绝的值，而不是在这里猜）。 */
export function normalizeExportFile(raw: Partial<PlanogramExportDto> | null | undefined): PlanogramExportDto {
  const r = (raw ?? {}) as Record<string, unknown>
  const plan = (r.plan && typeof r.plan === 'object' ? r.plan : {}) as Record<string, unknown>
  return {
    formatVersion: num(r.formatVersion),
    exportedAt: nullableText(r.exportedAt),
    plan: {
      name: text(plan.name),
      notes: nullableText(plan.notes),
      bayCount: num(plan.bayCount, 1),
      gapXMm: num(plan.gapXMm, 10),
      gapYMm: num(plan.gapYMm, 10),
    },
    shelfModelCode: text(r.shelfModelCode),
    items: asArray<Partial<PlanogramExportItemDto>>(r.items).map(normalizeExportItem),
  }
}

/** 导出方案 JSON（Edit 权限）：用货架 / 挂钩型号编码和商品货号，不带 guid，便于跨环境搬运。 */
export async function exportPlan(guid: string): Promise<PlanogramExportDto> {
  const payload = await request<Envelope<PlanogramExportDto>>(planogramUrl(`planograms/${encodeURIComponent(guid)}/export`), { method: 'GET' })
  return normalizeExportFile(unwrapApiData(payload))
}

/**
 * 导入方案 JSON（Edit 权限）：创建新草稿方案（version=1），返回详情。
 * 失败：PLANOGRAM_IMPORT_UNRESOLVED（details = [{ kind, code }]，缺失的货架 / 挂钩 / 商品）、PLANOGRAM_VALIDATION（formatVersion 不认识、字段不合法）。
 */
export async function importPlan(input: PlanogramImportInput): Promise<PlanogramDetailDto> {
  const payload = await request<Envelope<PlanogramDetailDto>>(planogramUrl('planograms/import'), { method: 'POST', data: input })
  return normalizePlanDetail(unwrapApiData(payload))
}
