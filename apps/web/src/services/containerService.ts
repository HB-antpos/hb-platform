import type { SupplyNoticeInput } from '../types/supplyNotice'
import type { ApiResponse } from '../types/api'
import type {
  AlignDomesticProductCodeRequest,
  AlignDomesticProductCodeResult,
  ComingSoonHomeContainer,
  ComingSoonHomeContainerSummary,
  ComingSoonHomeProduct,
  ContainerDetailBatchActionResult,
  ContainerDetailActionPreview,
  ContainerDetailActionPreviewRequest,
  ContainerDetailBatchUpdateResult,
  ContainerDetailBatchScope,
  ContainerDetailEditingPresence,
  ContainerDomesticSetCodeItem,
  ContainerDetail,
  ContainerDetailQuery,
  ContainerDetailQueryResult,
  CreateContainerRequest,
  DateFilterOption,
  ContainerListResponse,
  ContainerMain,
  ContainerQueryRequest,
  HqTranslationResult,
  SyncResult,
  UpdateContainerDomesticSetCodePriceItem,
  UpdateContainerDomesticSetCodePricesResult,
  UpdateContainerDetailRequest,
  UpdateContainerRequest,
} from '../types/container'
import request from '../utils/request'

const API_BASE = '/api/react/v1/containers'

type RawHqTranslationResult = HqTranslationResult & {
  totalCandidates?: number
  totalTranslated?: number
  totalSkipped?: number
  totalFailed?: number
  samples?: Record<string, string>
}

interface ContainerListApiResponse {
  success?: boolean
  message?: string
  data?: {
    items?: ContainerMain[]
    total?: number
    page?: number
    pageSize?: number
  }
}

interface ContainerArrayApiResponse<T> {
  success?: boolean
  message?: string
  data?: T[]
}

function ensureSuccess(success?: boolean, message?: string, fallback?: string) {
  if (success === false) {
    throw new Error(message || fallback || '请求失败')
  }
}

function normalizeHqTranslationResult(result: RawHqTranslationResult = {}): HqTranslationResult {
  return {
    TotalCandidates: result.TotalCandidates ?? result.totalCandidates,
    TotalTranslated: result.TotalTranslated ?? result.totalTranslated,
    TotalSkipped: result.TotalSkipped ?? result.totalSkipped,
    TotalFailed: result.TotalFailed ?? result.totalFailed,
    Samples: result.Samples ?? result.samples,
  }
}

function toTimestamp(value?: string) {
  if (!value) {
    return Number.MAX_SAFE_INTEGER
  }

  const timestamp = new Date(value).getTime()
  return Number.isNaN(timestamp) ? Number.MAX_SAFE_INTEGER : timestamp
}

function toComingSoonProduct(item: ContainerDetail): ComingSoonHomeProduct {
  return {
    id: item.id,
    hguid: item.hguid,
    productCode: item.商品编码 ?? item.商品信息?.商品编码,
    itemNumber: item.商品信息?.货号,
    barcode: item.商品信息?.条形码,
    productName: item.商品信息?.商品名称,
    englishName: item.商品信息?.英文名称,
    productImage: item.商品信息?.商品图片,
    quantity: item.装柜数量,
    retailPrice: item.商品信息?.零售价格,
    // 优先用「本柜新品」：新商品建档后 是否新商品 会变 false，即将上新仍需标为新品。
    isNewProduct: item.isContainerNewProduct ?? item.是否新商品 ?? item.warehouseIsActive === false,
    warehouseIsActive: item.warehouseIsActive,
  }
}

const itemNumberCollator = new Intl.Collator('en', {
  numeric: true,
  sensitivity: 'base',
})

function compareComingSoonProducts(left: ComingSoonHomeProduct, right: ComingSoonHomeProduct) {
  const leftItemNumber = (left.itemNumber ?? '').trim()
  const rightItemNumber = (right.itemNumber ?? '').trim()

  if (leftItemNumber && !rightItemNumber) return -1
  if (!leftItemNumber && rightItemNumber) return 1

  const itemNumberCompare = itemNumberCollator.compare(leftItemNumber, rightItemNumber)
  if (itemNumberCompare !== 0) return itemNumberCompare

  const productCodeCompare = itemNumberCollator.compare(left.productCode ?? '', right.productCode ?? '')
  if (productCodeCompare !== 0) return productCodeCompare

  return itemNumberCollator.compare(left.productName ?? left.englishName ?? '', right.productName ?? right.englishName ?? '')
}

export async function getContainerList(query: ContainerQueryRequest): Promise<ContainerListResponse> {
  const response = await request<ContainerListApiResponse>(`${API_BASE}/list`, {
    method: 'POST',
    data: {
      DateType: query.dateType || '预计到岸日期',
      StartDate: query.startDate,
      EndDate: query.endDate,
      Page: query.page || 1,
      PageSize: query.pageSize || 1000,
      ItemNumberFilter: query.itemNumberFilter,
      ProductKeyword: query.productKeyword,
      ContainerNumberFilter: query.containerNumberFilter,
      LoadingDateStart: query.loadingDateStart,
      LoadingDateEnd: query.loadingDateEnd,
      EstimatedArrivalDateStart: query.estimatedArrivalDateStart,
      EstimatedArrivalDateEnd: query.estimatedArrivalDateEnd,
      ActualArrivalDateStart: query.actualArrivalDateStart,
      ActualArrivalDateEnd: query.actualArrivalDateEnd,
      TotalPiecesMin: query.totalPiecesMin,
      TotalPiecesMax: query.totalPiecesMax,
      TotalAmountMin: query.totalAmountMin,
      TotalAmountMax: query.totalAmountMax,
      TotalVolumeMin: query.totalVolumeMin,
      TotalVolumeMax: query.totalVolumeMax,
      Statuses: query.statuses,
      SortBy: query.sortBy || '预计到岸日期',
      SortDirection: query.sortDirection || 'desc',
    },
  })

  ensureSuccess(response.success, response.message, '获取货柜列表失败')

  const items = response.data?.items ?? []
  const total = response.data?.total ?? items.length
  const page = response.data?.page ?? query.page ?? 1
  const pageSize = response.data?.pageSize ?? query.pageSize ?? 1000

  return {
    containers: items,
    totalCount: total,
    page,
    pageSize,
    totalPages: pageSize > 0 ? Math.ceil(total / pageSize) : 1,
  }
}

export async function getContainerProducts(containerGuid: string): Promise<ContainerDetail[]> {
  const response = await request<ApiResponse<ContainerDetail[]> | ContainerDetail[]>(`${API_BASE}/${containerGuid}/products`, {
    method: 'GET',
  })

  if (Array.isArray(response)) {
    return response
  }

  ensureSuccess(response.success ?? response.isSuccess, response.message, '获取货柜商品列表失败')

  return response.data ?? []
}

export async function queryContainerProducts(
  containerGuid: string,
  query: Omit<ContainerDetailQuery, 'containerGuid'> & Partial<Pick<ContainerDetailQuery, 'containerGuid'>>,
  signal?: AbortSignal,
): Promise<ContainerDetailQueryResult> {
  const { containerGuid: _ignoredContainerGuid, ...queryBody } = query
  const response = await request<ApiResponse<ContainerDetailQueryResult> | { success?: boolean; isSuccess?: boolean; message?: string; data?: ContainerDetailQueryResult }>(
    `${API_BASE}/${encodeURIComponent(containerGuid)}/products/query`,
    {
      method: 'POST',
      data: {
        // 后端契约要求 body 内也携带货柜 GUID，避免路由参数和查询体脱节。
        containerGuid,
        ...queryBody,
      },
      signal,
    },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '查询货柜商品明细失败')

  if (!response.data) {
    throw new Error('查询货柜商品明细失败')
  }

  return response.data
}

export async function getContainerDomesticSetCodes(productCode: string, signal?: AbortSignal): Promise<ContainerDomesticSetCodeItem[]> {
  const response = await request<ApiResponse<ContainerDomesticSetCodeItem[]> | { success?: boolean; isSuccess?: boolean; message?: string; data?: ContainerDomesticSetCodeItem[] }>(
    `${API_BASE}/products/${encodeURIComponent(productCode)}/domestic-set-codes`,
    {
      method: 'GET',
      signal,
    },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '获取套装多码数据失败')
  return response.data ?? []
}

export async function updateContainerDomesticSetCodePrices(
  productCode: string,
  items: UpdateContainerDomesticSetCodePriceItem[],
): Promise<UpdateContainerDomesticSetCodePricesResult> {
  const response = await request<ApiResponse<UpdateContainerDomesticSetCodePricesResult> | { success?: boolean; isSuccess?: boolean; message?: string; data?: UpdateContainerDomesticSetCodePricesResult }>(
    `${API_BASE}/products/${encodeURIComponent(productCode)}/domestic-set-codes/prices`,
    {
      method: 'PATCH',
      data: {
        // 只回写国内套装表价格字段，不同步仓库/POS 多码表。
        items,
      },
    },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '保存套装多码价格失败')
  return response.data ?? { updatedCount: 0 }
}

async function postContainerDetailAction<TBody extends object>(
  containerGuid: string,
  action: string,
  body: TBody,
  fallbackMessage: string,
): Promise<ContainerDetailBatchActionResult> {
  const response = await request<ApiResponse<ContainerDetailBatchActionResult> | { success?: boolean; isSuccess?: boolean; message?: string; data?: ContainerDetailBatchActionResult }>(
    `${API_BASE}/${encodeURIComponent(containerGuid)}/actions/${action}`,
    {
      method: 'POST',
      data: body,
    },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, fallbackMessage)

  return response.data ?? { totalUpdated: 0 }
}

/**
 * 所有批量写入先使用同一服务端预览，执行令牌把用户、筛选范围、参数和目标字段基线绑定在一起。
 */
export async function previewContainerDetailAction(
  containerGuid: string,
  requestBody: ContainerDetailActionPreviewRequest,
): Promise<ContainerDetailActionPreview> {
  const response = await request<ApiResponse<ContainerDetailActionPreview> | {
    success?: boolean
    isSuccess?: boolean
    message?: string
    data?: ContainerDetailActionPreview
  }>(`${API_BASE}/${encodeURIComponent(containerGuid)}/actions/preview`, {
    method: 'POST',
    data: requestBody,
  })
  ensureSuccess(response.success ?? response.isSuccess, response.message, '批量操作预览失败')
  const data = response.data
  if (
    !data
    || typeof data.previewToken !== 'string'
    || !data.previewToken
    || !Number.isInteger(data.affectedCount)
    || data.affectedCount < 0
    || !Array.isArray(data.fieldSummary)
    || data.fieldSummary.some((field) => typeof field !== 'string')
    || typeof data.expiresAt !== 'string'
    || Number.isNaN(Date.parse(data.expiresAt))
  ) {
    throw new Error('批量操作预览返回数据不完整')
  }
  return data
}

export async function getContainerDetailEditingPresence(
  containerGuid: string,
): Promise<ContainerDetailEditingPresence> {
  const response = await request<ApiResponse<ContainerDetailEditingPresence> | {
    success?: boolean
    isSuccess?: boolean
    message?: string
    data?: ContainerDetailEditingPresence
  }>(`${API_BASE}/${encodeURIComponent(containerGuid)}/editing-presence`, { method: 'GET' })
  ensureSuccess(response.success ?? response.isSuccess, response.message, '获取货柜编辑状态失败')
  const data = response.data
  if (!data || !Array.isArray(data.viewers) || !Array.isArray(data.editors)) {
    throw new Error('获取货柜编辑状态返回数据不完整')
  }
  const normalize = (users: Array<ContainerDetailEditingPresence['viewers'][number] & { displayName?: unknown }>) => users.flatMap((user) => (
    user
    && typeof user.userGuid === 'string'
    && (typeof user.userName === 'string' || typeof user.displayName === 'string')
    && typeof user.lastActiveAt === 'string'
    && !Number.isNaN(Date.parse(user.lastActiveAt))
      ? [{ userGuid: user.userGuid, userName: user.userName ?? user.displayName as string, lastActiveAt: user.lastActiveAt }]
      : []
  ))
  return { viewers: normalize(data.viewers), editors: normalize(data.editors) }
}

export async function heartbeatContainerDetailEditingPresence(
  containerGuid: string,
  payload: { clientSessionId: string; state: 'viewing' | 'editing' },
) {
  const response = await request<{ success?: boolean; isSuccess?: boolean; message?: string }>(
    `${API_BASE}/${encodeURIComponent(containerGuid)}/editing-presence/heartbeat`,
    { method: 'POST', data: payload },
  )
  ensureSuccess(response.success ?? response.isSuccess, response.message, '更新货柜编辑状态失败')
}

/** 离开只做尽力而为的租约释放；网络失败绝不能阻断页面关闭或编辑。 */
export async function leaveContainerDetailEditingPresence(
  containerGuid: string,
  payload: { clientSessionId: string },
) {
  const response = await request<{ success?: boolean; isSuccess?: boolean; message?: string }>(
    `${API_BASE}/${encodeURIComponent(containerGuid)}/editing-presence/leave`,
    { method: 'POST', data: payload },
  )
  ensureSuccess(response.success ?? response.isSuccess, response.message, '离开货柜编辑状态失败')
}

export async function applyContainerFloatRateByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  floatRate: number,
  previewToken: string,
): Promise<ContainerDetailBatchActionResult> {
  return postContainerDetailAction(containerGuid, 'apply-float-rate', {
    ...scope,
    // 批量写接口使用统一 scope；未勾选时 scope.query 代表当前筛选结果全量。
    floatRate,
    previewToken,
  }, '批量调浮率失败')
}

export async function applyContainerPricesByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  prices: { importPrice?: number | null; oemPrice?: number | null },
  previewToken: string,
): Promise<ContainerDetailBatchActionResult> {
  return postContainerDetailAction(containerGuid, 'apply-prices', {
    ...scope,
    importPrice: prices.importPrice,
    oemPrice: prices.oemPrice,
    previewToken,
  }, '批量改价失败')
}

export async function recalculateContainerCostsByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  previewToken: string,
): Promise<ContainerDetailBatchActionResult> {
  return postContainerDetailAction(containerGuid, 'recalculate-costs', { ...scope, previewToken }, '重算成本失败')
}

export async function backfillContainerLastPricesByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  previewToken: string,
): Promise<ContainerDetailBatchActionResult> {
  return postContainerDetailAction(containerGuid, 'backfill-last-prices', { ...scope, previewToken }, '回填上次价格失败')
}

/** 货柜范围上下架与明细并发/批量预览使用同一条受保护动作链。 */
export async function setContainerDetailStatusByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  isActive: boolean,
  previewToken: string,
  supplyNotice?: SupplyNoticeInput,
): Promise<ContainerDetailBatchActionResult> {
  // 下架时随请求登记供货说明；上架不传，也不进入预览指纹。
  return postContainerDetailAction(
    containerGuid,
    'set-status',
    { ...scope, isActive, previewToken, ...(supplyNotice ? { supplyNotice } : {}) },
    '批量上下架失败',
  )
}

export async function assignContainerDetailCategoryByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  categoryGuid: string,
  previewToken: string,
): Promise<ContainerDetailBatchActionResult> {
  return postContainerDetailAction(containerGuid, 'assign-category', { ...scope, categoryGuid, previewToken }, '批量分类失败')
}

export async function getContainerDetail(containerGuid: string): Promise<ContainerMain> {
  const response = await request<ApiResponse<ContainerMain> | { success?: boolean; isSuccess?: boolean; message?: string; data?: ContainerMain }>(
    `${API_BASE}/${encodeURIComponent(containerGuid)}`,
    { method: 'GET' },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '获取货柜详情失败')

  const data = 'data' in response ? response.data : undefined
  if (!data) {
    throw new Error('获取货柜详情失败')
  }
  return data
}

export async function getDateFilterOptions(): Promise<DateFilterOption[]> {
  const response = await request<ApiResponse<DateFilterOption[]> | { success?: boolean; isSuccess?: boolean; message?: string; data?: DateFilterOption[] }>(
    `${API_BASE}/date-filter-options`,
    { method: 'GET' },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '获取日期筛选项失败')

  return response.data?.length
    ? response.data
    : [
        { value: '预计到岸日期', label: '预计到岸日期' },
        { value: '实际到货日期', label: '实际到货日期' },
      ]
}

export async function createContainer(data: CreateContainerRequest): Promise<string> {
  const response = await request<{ success?: boolean; message?: string; data?: { containerGuid?: string } }>(API_BASE, {
    method: 'POST',
    data,
  })

  ensureSuccess(response.success, response.message, '创建货柜失败')
  return response.data?.containerGuid ?? ''
}

export async function updateContainer(containerGuid: string, data: UpdateContainerRequest): Promise<boolean> {
  const response = await request<{ success?: boolean; message?: string }>(`${API_BASE}/${encodeURIComponent(containerGuid)}`, {
    method: 'PUT',
    data,
  })

  ensureSuccess(response.success, response.message, '更新货柜失败')
  return true
}

export async function batchUpdateDetails(
  containerGuid: string,
  updates: UpdateContainerDetailRequest[],
): Promise<ContainerDetailBatchUpdateResult> {
  const normalizedContainerGuid = containerGuid.trim()
  if (!normalizedContainerGuid) {
    throw new Error('货柜 GUID 不能为空')
  }
  const serializedUpdates = updates.map((item) => ({
    HGUID: item.hguid,
    调整浮率: item.调整浮率,
    国内价格: item.国内价格,
    进口价格: item.进口价格,
    运输成本: item.运输成本,
    商品名称: item.商品名称,
    英文名称: item.英文名称,
    ClearEnglishName: item.ClearEnglishName,
    ProductCategoryGUID: item.ProductCategoryGUID,
    贴牌价格: item.贴牌价格,
    单件装箱数: item.单件装箱数,
    中包数: item.中包数,
    单件体积: item.单件体积,
    装柜数量: item.装柜数量,
    合计装柜体积: item.合计装柜体积,
    合计装柜金额: item.合计装柜金额,
    备注: item.备注,
    IsActive: item.IsActive,
    SkipRelatedProductSync: item.SkipRelatedProductSync,
    ExpectedServerFieldTokens: item.expectedServerFieldTokens,
    OverrideAcknowledgements: item.overrideAcknowledgements,
  }))
  const submittedFieldsByHguid = new Map<string, Set<string>>()
  serializedUpdates.forEach((update) => {
    if (submittedFieldsByHguid.has(update.HGUID)) {
      throw new Error('批量更新货柜明细包含重复 HGUID，未保存草稿已保留')
    }
    const fields = new Set(Object.entries(update).flatMap(([field, value]) => {
      if (
        field === 'HGUID'
        || field === 'SkipRelatedProductSync'
        || field === 'ExpectedServerFieldTokens'
        || field === 'OverrideAcknowledgements'
        || value === undefined
      ) return []
      if (field === 'ClearEnglishName') return value === true ? ['英文名称'] : []
      return [field]
    }))
    if (!fields.size) {
      throw new Error('批量更新货柜明细没有可保存字段，未保存草稿已保留')
    }
    submittedFieldsByHguid.set(update.HGUID, fields)
  })
  const response = await request<{
    success?: boolean
    message?: string
    data?: {
      totalUpdated?: number
      totalRequested?: number
      autoRepairedStoreGroupCount?: number
      autoRepairedRelationCount?: number
      validationErrors?: Array<{
        hguid?: string
        field?: string
        code?: string
        message?: string
      }>
      conflicts?: Array<{
        hguid?: string
        field?: string
        serverValue?: unknown
        submittedValue?: unknown
      currentServerFieldToken?: string
      code?: string
      message?: string
      }>
    }
  }>(`${API_BASE}/${encodeURIComponent(normalizedContainerGuid)}/batch-update-details`, {
    method: 'POST',
    data: serializedUpdates,
  })

  if (response.success !== true) {
    throw new Error(response.message || '批量更新货柜明细失败')
  }

  const data = response.data
  const totalUpdated = data?.totalUpdated
  const totalRequested = data?.totalRequested
  if (!data
    || typeof totalUpdated !== 'number'
    || typeof totalRequested !== 'number'
    || !Number.isInteger(totalUpdated)
    || !Number.isInteger(totalRequested)
    || totalUpdated < 0
    || totalRequested !== updates.length
    || totalUpdated > totalRequested
    || !Array.isArray(data.validationErrors)
  ) {
    // 2xx 但结果不完整时不能按成功处理，否则会错误清除本地草稿。
    throw new Error('批量更新货柜明细返回数据不完整，未保存草稿已保留')
  }
  const updateHguids = new Set(serializedUpdates.map((item) => item.HGUID))
  const validationErrorKeys = new Set<string>()
  const validationErrors = data.validationErrors.map((error) => {
    if (
      !error
      || typeof error.hguid !== 'string'
      || !updateHguids.has(error.hguid)
      || typeof error.field !== 'string'
      || !error.field.trim()
      || typeof error.code !== 'string'
      || !error.code.trim()
      || typeof error.message !== 'string'
      || !error.message.trim()
      || (error.field !== '*' && !submittedFieldsByHguid.get(error.hguid)?.has(error.field))
    ) {
      throw new Error('批量更新货柜明细返回校验错误不完整，未保存草稿已保留')
    }
    const errorKey = `${error.hguid}:${error.field}`
    if (validationErrorKeys.has(errorKey)) {
      throw new Error('批量更新货柜明细返回校验错误冲突，未保存草稿已保留')
    }
    validationErrorKeys.add(errorKey)
    return {
      hguid: error.hguid,
      field: error.field,
      code: error.code,
      message: error.message,
    }
  })
  const conflictKeys = new Set<string>()
  // 灰度期间允许尚未升级的后端省略 conflicts；省略只能表示“没有可报告冲突”，不能把未知失败清成成功。
  const conflicts = (data.conflicts ?? []).map((conflict) => {
    if (
      !conflict
      || typeof conflict.hguid !== 'string'
      || !updateHguids.has(conflict.hguid)
      || typeof conflict.field !== 'string'
      || !conflict.field.trim()
      || !submittedFieldsByHguid.get(conflict.hguid)?.has(conflict.field)
      || typeof conflict.currentServerFieldToken !== 'string'
      || !conflict.currentServerFieldToken.trim()
      || conflict.code !== 'CONCURRENT_FIELD_UPDATE'
    ) {
      throw new Error('批量更新货柜明细返回并发冲突不完整，未保存草稿已保留')
    }
    const conflictKey = `${conflict.hguid}:${conflict.field}`
    if (conflictKeys.has(conflictKey) || validationErrorKeys.has(conflictKey)) {
      throw new Error('批量更新货柜明细返回字段结果冲突，未保存草稿已保留')
    }
    conflictKeys.add(conflictKey)
    return {
      hguid: conflict.hguid,
      field: conflict.field,
      serverValue: conflict.serverValue,
      submittedValue: conflict.submittedValue,
      currentServerFieldToken: conflict.currentServerFieldToken,
      code: 'CONCURRENT_FIELD_UPDATE' as const,
      message: typeof conflict.message === 'string' ? conflict.message : undefined,
    }
  })
  const assertOptionalCount = (value: unknown) => (
    value === undefined || (Number.isInteger(value) && (value as number) >= 0)
  )
  if (!assertOptionalCount(data.autoRepairedStoreGroupCount) || !assertOptionalCount(data.autoRepairedRelationCount)) {
    throw new Error('批量更新货柜明细返回修复统计不完整，未保存草稿已保留')
  }

  const failedFieldsByHguid = new Map<string, Set<string>>()
  const fullFailureHguids = new Set<string>()
  validationErrors.forEach((error) => {
    if (error.field === '*') {
      fullFailureHguids.add(error.hguid)
      return
    }
    const fields = failedFieldsByHguid.get(error.hguid) ?? new Set<string>()
    fields.add(error.field)
    failedFieldsByHguid.set(error.hguid, fields)
  })
  conflicts.forEach((conflict) => {
    const fields = failedFieldsByHguid.get(conflict.hguid) ?? new Set<string>()
    fields.add(conflict.field)
    failedFieldsByHguid.set(conflict.hguid, fields)
  })
  const successfulHguidCount = Array.from(submittedFieldsByHguid.entries()).filter(([hguid, fields]) => (
    !fullFailureHguids.has(hguid)
    && Array.from(fields).some((field) => !failedFieldsByHguid.get(hguid)?.has(field))
  )).length
  if (successfulHguidCount !== totalUpdated) {
    throw new Error('批量更新货柜明细返回成功计数不一致，未保存草稿已保留')
  }

  return {
    totalUpdated,
    totalRequested,
    autoRepairedStoreGroupCount: data.autoRepairedStoreGroupCount,
    autoRepairedRelationCount: data.autoRepairedRelationCount,
    validationErrors,
    conflicts,
  }
}

function normalizeAlignDomesticProductCodeResult(
  result?: Partial<AlignDomesticProductCodeResult>,
): AlignDomesticProductCodeResult {
  return {
    oldProductCode: result?.oldProductCode ?? result?.OldProductCode ?? '',
    OldProductCode: result?.OldProductCode,
    newProductCode: result?.newProductCode ?? result?.NewProductCode ?? '',
    NewProductCode: result?.NewProductCode,
    updatedDomesticProducts: result?.updatedDomesticProducts ?? result?.UpdatedDomesticProducts ?? 0,
    UpdatedDomesticProducts: result?.UpdatedDomesticProducts,
    updatedContainerDetails: result?.updatedContainerDetails ?? result?.UpdatedContainerDetails ?? 0,
    UpdatedContainerDetails: result?.UpdatedContainerDetails,
    updatedDomesticSetProducts: result?.updatedDomesticSetProducts ?? result?.UpdatedDomesticSetProducts ?? 0,
    UpdatedDomesticSetProducts: result?.UpdatedDomesticSetProducts,
    updatedProductGrades: result?.updatedProductGrades ?? result?.UpdatedProductGrades ?? 0,
    UpdatedProductGrades: result?.UpdatedProductGrades,
    updatedDomesticProductCreationLogs: result?.updatedDomesticProductCreationLogs ?? result?.UpdatedDomesticProductCreationLogs ?? 0,
    UpdatedDomesticProductCreationLogs: result?.UpdatedDomesticProductCreationLogs,
  }
}

export async function alignDomesticProductCode(
  payload: AlignDomesticProductCodeRequest,
): Promise<AlignDomesticProductCodeResult> {
  const response = await request<ApiResponse<AlignDomesticProductCodeResult> | { success?: boolean; isSuccess?: boolean; message?: string; data?: AlignDomesticProductCodeResult }>(
    `${API_BASE}/details/align-domestic-product-code`,
    {
      method: 'POST',
      data: {
        DetailHguid: payload.detailHguid,
        ExpectedDomesticProductCode: payload.expectedDomesticProductCode,
        TargetProductCode: payload.targetProductCode,
        SupplierCode: payload.supplierCode,
      },
    },
  )

  ensureSuccess(response.success ?? response.isSuccess, response.message, '对齐国内商品编码失败')
  return normalizeAlignDomesticProductCodeResult(response.data)
}

export async function batchDeleteDetails(hguids: string[]): Promise<{ totalDeleted: number; totalRequested: number }> {
  const response = await request<{
    success?: boolean
    message?: string
    data?: { totalDeleted?: number; totalRequested?: number }
  }>(`${API_BASE}/batch-delete-details`, {
    method: 'POST',
    data: { hguids },
  })

  ensureSuccess(response.success, response.message, '批量删除货柜明细失败')
  return {
    totalDeleted: response.data?.totalDeleted ?? hguids.length,
    totalRequested: response.data?.totalRequested ?? hguids.length,
  }
}

export async function deleteContainerDetailsByScope(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  previewToken: string,
): Promise<{ totalDeleted: number; totalRequested: number }> {
  const response = await request<ApiResponse<{ totalDeleted: number; totalRequested: number }> | {
    success?: boolean
    isSuccess?: boolean
    message?: string
    data?: { totalDeleted?: number; totalRequested?: number }
  }>(`${API_BASE}/${encodeURIComponent(containerGuid)}/actions/delete-details`, {
    method: 'POST',
    data: { ...scope, previewToken },
  })
  ensureSuccess(response.success ?? response.isSuccess, response.message, '删除货柜明细失败')
  const data = response.data
  const totalDeleted = data?.totalDeleted
  const totalRequested = data?.totalRequested
  if (typeof totalDeleted !== 'number' || !Number.isInteger(totalDeleted)
    || typeof totalRequested !== 'number' || !Number.isInteger(totalRequested)) {
    throw new Error('删除货柜明细返回数据不完整')
  }
  return { totalDeleted, totalRequested }
}

export async function syncContainersFromHq(startDate?: string): Promise<SyncResult> {
  const response = await request<{ success?: boolean; message?: string; data?: SyncResult }>(`${API_BASE}/sync-from-hq`, {
    method: 'POST',
    data: { startDate },
  })

  ensureSuccess(response.success, response.message, '从HQ同步货柜失败')
  return response.data ?? { isSuccess: response.success, message: response.message }
}

export async function translateHqProductNamesByContainerNumber(containerNumber: string): Promise<HqTranslationResult> {
  const response = await request<{ success?: boolean; message?: string; data?: RawHqTranslationResult } & RawHqTranslationResult>(
    '/api/react/v1/hq-products/translate-names/by-container-number',
    {
      method: 'POST',
      data: {
        ContainerNumbers: [containerNumber],
        OverwriteExisting: false,
      },
    },
  )

  ensureSuccess(response.success, response.message, '翻译HQ数据失败')
  return normalizeHqTranslationResult(response.data ?? response)
}

export async function pushContainersToHbSales(containerGuids: string[]): Promise<SyncResult> {
  const response = await request<{ success?: boolean; message?: string; data?: SyncResult }>(`${API_BASE}/push-to-hbsales`, {
    method: 'POST',
    data: { containerGuids },
  })

  ensureSuccess(response.success, response.message, '发送到HBSales失败')
  return response.data ?? { isSuccess: response.success, message: response.message }
}

interface CheckConflictItem {
  hbProductNo?: string
  productCode?: string
}

interface CheckConflictsResponse {
  success: boolean
  data: Array<{ productCode: string; existingQuantity?: number; existingPieces?: number }>
  message?: string
}

export interface AssignContainerItem {
  hbProductNo?: string
  productCode?: string
  quantity: number
  packingQuantity?: number
  unitVolume?: number
  domesticPrice?: number
  oemPrice?: number
  notes?: string
}

interface AssignProductsResponse {
  success: boolean
  data: { created: number; updated: number; failed: Array<{ productCode: string; error: string }> }
  message?: string
}

export async function checkContainerConflicts(containerId: string, items: CheckConflictItem[]): Promise<CheckConflictsResponse> {
  const response = await request<CheckConflictsResponse>(`${API_BASE}/check-conflicts`, {
    method: 'POST',
    data: { ContainerId: containerId, Items: items },
  })
  return response
}

export async function assignProductsToContainer(containerId: string, items: AssignContainerItem[], resolution: 'override' | 'increase', notes?: string): Promise<AssignProductsResponse> {
  const response = await request<AssignProductsResponse>(`${API_BASE}/assign-products`, {
    method: 'POST',
    data: { ContainerId: containerId, Resolution: resolution, Notes: notes, Items: items },
  })
  return response
}

export async function getComingSoonContainerSummaries(): Promise<ComingSoonHomeContainerSummary[]> {
  const response = await request<ContainerArrayApiResponse<ContainerMain> | ContainerMain[]>(
    `${API_BASE}/coming-soon/summaries`,
    { method: 'GET' },
  )
  const containers = Array.isArray(response) ? response : response.data ?? []

  return containers.sort((left, right) => {
    const leftDate = left.实际到货日期 || left.预计到岸日期
    const rightDate = right.实际到货日期 || right.预计到岸日期
    return toTimestamp(leftDate) - toTimestamp(rightDate)
  })
}

export async function getComingSoonContainerProducts(containerGuid: string): Promise<ComingSoonHomeProduct[]> {
  const response = await request<ContainerArrayApiResponse<ContainerDetail> | ContainerDetail[]>(
    `${API_BASE}/coming-soon/${containerGuid}/products`,
    { method: 'GET' },
  )
  const products = Array.isArray(response) ? response : response.data ?? []
  return products.map(toComingSoonProduct).sort(compareComingSoonProducts)
}

export async function getComingSoonContainers(): Promise<ComingSoonHomeContainer[]> {
  const containers = await getComingSoonContainerSummaries()
  const productsList = await Promise.all(
    containers.map(async (container) => ({
      ...container,
      商品列表: await getComingSoonContainerProducts(container.hguid),
    } satisfies ComingSoonHomeContainer)),
  )

  return productsList
}
