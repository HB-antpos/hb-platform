import type { ContainerDetail, ContainerDetailQuery, ContainerDetailQueryResult, ContainerMain, UpdateContainerDetailRequest } from '../../../types/container'
import type { PushProductsToHqItem, PushProductsToHqResult } from '../../../types/posProduct'

export type ContainerDetailProductTypeFilter = 'normal' | 'set' | 'multi' | 'setChild'
export type ContainerDetailTagFilter = 'all' | 'new' | 'existing' | 'noOemPrice' | 'abnormalImport' | 'active' | 'inactive' | ContainerDetailProductTypeFilter

export type ContainerDetailTagStats = Record<ContainerDetailTagFilter, number> & {
  productCodeMatched: number
  supplierItemMatched: number
  unmatched: number
}
type ContainerDetailSelectableTagFilter = Exclude<ContainerDetailTagFilter, 'all'>
export type ContainerDetailNewProductFilter = 'new' | 'existing'
export type ContainerDetailMatchTypeFilter = 'productCode' | 'supplierItem' | 'unmatched'
export type ContainerDetailWarehouseStatusFilter = 'active' | 'inactive'
export type ContainerDetailSortOrder = 'ascend' | 'descend'
export const CONTAINER_DETAIL_ALL_CATEGORY_FILTER_KEY = '__ALL_CONTAINER_DETAIL_CATEGORIES__'
export const CONTAINER_DETAIL_UNCATEGORIZED_FILTER_KEY = '__UNCATEGORIZED_CONTAINER_DETAIL_CATEGORIES__'
export const DEFAULT_CONTAINER_DETAIL_FLOAT_RATE = 1.3
export const CONTAINER_DETAIL_ENGLISH_NAME_FIELD = '英文名称'
export const CONTAINER_DETAIL_INITIAL_PAGE_SIZE = 100
export const CONTAINER_DETAIL_FULL_LOAD_LIMIT = 200
export const CONTAINER_DETAIL_DEFAULT_PAGE_SIZE = 100
export const CONTAINER_DETAIL_PAGE_SIZE_OPTIONS = [50, 100, 200, 500, 1000] as const
export type ContainerDetailLoadMode = 'probe' | 'full' | 'paged'

export function resolveContainerDetailInitialPage(
  result: Pick<ContainerDetailQueryResult, 'items' | 'itemsTotal'>,
) {
  if (result.itemsTotal <= CONTAINER_DETAIL_FULL_LOAD_LIMIT) {
    return {
      mode: 'full' as const,
      items: result.items,
      itemsTotal: result.itemsTotal,
      requiresFullLoad: result.items.length < result.itemsTotal,
    }
  }

  return {
    mode: 'paged' as const,
    // 大货柜直接复用首屏 100 行，避免再请求同一个第 1 页。
    items: result.items,
    itemsTotal: result.itemsTotal,
    requiresFullLoad: false,
  }
}

export function resolveContainerDetailFullPage(
  result: Pick<ContainerDetailQueryResult, 'items' | 'itemsTotal' | 'hasMore'>,
  fullLoadLimit = CONTAINER_DETAIL_FULL_LOAD_LIMIT,
) {
  if (result.hasMore) {
    return {
      mode: 'paged' as const,
      items: result.items.slice(0, CONTAINER_DETAIL_DEFAULT_PAGE_SIZE),
      // 关闭 total 的二次请求只会返回 hasMore；至少保留一个可翻页的可信下界。
      itemsTotal: Math.max(result.itemsTotal, fullLoadLimit + 1),
    }
  }

  return {
    mode: 'full' as const,
    items: result.items,
    itemsTotal: Math.max(result.itemsTotal, result.items.length),
  }
}

export function canReuseContainerDetailInitialPage({
  filters,
  selectedTags,
  sortState,
  pageSize,
}: {
  filters: ContainerDetailColumnFilters
  selectedTags: ContainerDetailTagFilter[]
  sortState: ContainerDetailSortState
  pageSize: number
}) {
  if (
    pageSize !== CONTAINER_DETAIL_DEFAULT_PAGE_SIZE
    || sortState.field !== 'itemNumber'
    || sortState.order !== 'ascend'
  ) return false

  const query = buildContainerDetailQuery({
    containerGuid: '__probe__',
    filters,
    selectedTags,
    sortState,
    pageNumber: 1,
    pageSize,
  })
  const scopeKeys = Object.keys(query).filter((key) => ![
    'containerGuid',
    'pageNumber',
    'pageSize',
    'sortBy',
    'sortOrder',
  ].includes(key))
  return scopeKeys.length === 0
}

/**
 * 所有明细可编辑字段共用一套草稿元数据。这样普通、套装和多码行的保存都走相同字段级并发契约，
 * 不会因页面入口不同而漏掉本地恢复或服务器基线令牌。
 */
export const CONTAINER_DETAIL_DRAFT_FIELDS = [
  '调整浮率', '国内价格', '进口价格', '运输成本', '商品名称', '英文名称',
  'ProductCategoryGUID', '贴牌价格', '单件装箱数', '中包数', '单件体积',
  '装柜数量', '合计装柜体积', '合计装柜金额', '备注', 'IsActive',
] as const
export type ContainerDetailDraftField = typeof CONTAINER_DETAIL_DRAFT_FIELDS[number]
export type PendingContainerDetailPatch = Pick<UpdateContainerDetailRequest, 'hguid'> &
  Partial<Pick<UpdateContainerDetailRequest, ContainerDetailDraftField | 'ClearEnglishName'>>
export type PendingContainerDetailPatchMap = Record<string, PendingContainerDetailPatch>

/**
 * 接口中少数可编辑字段使用了历史别名或嵌套商品对象。冲突抽屉与单元格提示必须读取
 * 同一个规范值，避免服务器已有值被错误展示为 "--"。
 */
export function getContainerDetailConflictServerValue(row: ContainerDetail, field: string): unknown {
  if (field === '商品名称') return getContainerDetailProductName(row)
  if (field === '英文名称') return getContainerDetailEnglishName(row)
  if (field === 'IsActive') return row.IsActive ?? row.warehouseIsActive
  if (field === 'ProductCategoryGUID') return getContainerDetailCategoryGuid(row)
  return row[field as keyof ContainerDetail]
}

/** 批量预览令牌过期只能重新预览，调用方不得据此自动重放原写请求。 */
export function isContainerDetailActionPreviewExpired(error: unknown) {
  const candidate = error as {
    status?: number
    response?: { status?: number; data?: { code?: string } }
    code?: string
  } | null
  const status = candidate?.status ?? candidate?.response?.status
  const code = candidate?.code ?? candidate?.response?.data?.code
  return status === 409 || code === 'BATCH_PREVIEW_EXPIRED' || code === 'BATCH_PREVIEW_CHANGED'
}

export interface ContainerDetailSaveValidationError {
  hguid: string
  field: string
  code: string
  message: string
}

export interface PendingContainerDetailSavePlan {
  pendingPatches: PendingContainerDetailPatch[]
  detailUpdates: UpdateContainerDetailRequest[]
  localValidationErrors: ContainerDetailSaveValidationError[]
  importPriceCount: number
  retailPriceCount: number
  englishNameCount: number
  clearEnglishNameCount: number
}

export function normalizeContainerDetailEnglishNameForSave(englishName: string) {
  // 只把空白视为分词边界，保留连字符、撇号、缩写和词内既有大小写。
  return englishName.trim().replace(/\S+/gu, (word) => (
    word.replace(/\p{Script=Latin}/u, (letter) => letter.toUpperCase())
  ))
}

export function resolveContainerDetailPendingPriceOnBlur(
  rawValue: string,
  currentValue: number | undefined,
) {
  const normalizedValue = rawValue.trim().replace(/,/g, '')
  if (!normalizedValue) return undefined

  const parsedValue = Number(normalizedValue)
  if (!Number.isFinite(parsedValue) || parsedValue < 0) return undefined

  const value = Number(parsedValue.toFixed(2))
  return value === currentValue ? undefined : value
}

export function getSubmittedContainerDetailFields(update: UpdateContainerDetailRequest) {
  const fields = CONTAINER_DETAIL_DRAFT_FIELDS.filter((field) => field in update)
  if (update.ClearEnglishName === true && !fields.includes(CONTAINER_DETAIL_ENGLISH_NAME_FIELD)) {
    fields.push(CONTAINER_DETAIL_ENGLISH_NAME_FIELD)
  }
  return fields
}

/**
 * 直接操作可能与已存在草稿合并为同一次部分成功保存。只有该更新涉及的每个字段都已落库，
 * 才允许调用方关闭弹窗或把匹配结果回显到行；否则不能用客户端值伪造成功。
 */
export function filterSuccessfullySavedContainerDetailUpdates<T extends UpdateContainerDetailRequest>(
  updates: T[],
  successfulFieldKeys: readonly string[],
) {
  const successfulKeys = new Set(successfulFieldKeys)
  return updates.filter((update) => {
    const fields = getSubmittedContainerDetailFields(update)
    return fields.length > 0 && fields.every((field) => successfulKeys.has(`${update.hguid}:${field}`))
  })
}

function hasPendingContainerDetailFields(patch: PendingContainerDetailPatch) {
  return getSubmittedContainerDetailFields(patch).length > 0
}

export function mergePendingContainerDetailPatch(
  current: PendingContainerDetailPatchMap,
  patch: PendingContainerDetailPatch,
): PendingContainerDetailPatchMap {
  const key = patch.hguid
  const nextPatch: PendingContainerDetailPatch = {
    ...(current[key] ?? { hguid: patch.hguid }),
  }

  CONTAINER_DETAIL_DRAFT_FIELDS.forEach((field) => {
    if (field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD || !(field in patch)) return
    const value = patch[field]
    if (value == null) delete nextPatch[field]
    else nextPatch[field] = value as never
  })
  if ('英文名称' in patch) {
    nextPatch.英文名称 = patch.英文名称
    delete nextPatch.ClearEnglishName
  }
  if (patch.ClearEnglishName === true) {
    nextPatch.ClearEnglishName = true
    delete nextPatch.英文名称
  }

  const next = { ...current }
  if (hasPendingContainerDetailFields(nextPatch)) next[key] = nextPatch
  else delete next[key]
  return next
}

export function applyPendingContainerDetailPatches(
  rows: ContainerDetail[],
  pendingPatches: PendingContainerDetailPatchMap,
): ContainerDetail[] {
  return rows.map((row) => {
    const pendingPatch = pendingPatches[row.hguid]
    if (!pendingPatch) return row

    const visiblePatch: Partial<ContainerDetail> = {}
    CONTAINER_DETAIL_DRAFT_FIELDS.forEach((field) => {
      if (field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD || !(field in pendingPatch)) return
      ;(visiblePatch as Record<string, unknown>)[field] = pendingPatch[field]
    })
    if ('贴牌价格' in pendingPatch) {
      visiblePatch.贴牌价格 = pendingPatch.贴牌价格
      if (!row.是否新商品) {
        // 已有商品的零售价列读取仓库实时价，重载后也要继续显示本地草稿。
        visiblePatch.warehouseOEMPrice = pendingPatch.贴牌价格
        visiblePatch.WarehouseOEMPrice = pendingPatch.贴牌价格
      }
    }
    if (pendingPatch.ClearEnglishName === true) {
      visiblePatch.英文名称 = undefined
    } else if ('英文名称' in pendingPatch) {
      visiblePatch.英文名称 = pendingPatch.英文名称
    }

    return mergeContainerDetailPatch(row, visiblePatch)
  })
}

export function buildPendingContainerDetailSavePlan(
  pendingPatches: PendingContainerDetailPatch[],
): PendingContainerDetailSavePlan {
  const localValidationErrors: ContainerDetailSaveValidationError[] = []
  const detailUpdates = pendingPatches
    .map((patch) => {
      const update: UpdateContainerDetailRequest = { hguid: patch.hguid }
      CONTAINER_DETAIL_DRAFT_FIELDS.forEach((field) => {
        if (field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD || !(field in patch)) return
        update[field] = patch[field] as never
      })
      if (patch.ClearEnglishName === true) {
        update.ClearEnglishName = true
      } else if (patch.英文名称 !== undefined) {
        const englishName = normalizeContainerDetailEnglishNameForSave(patch.英文名称)
        if (englishName) {
          update.英文名称 = englishName
        } else {
          localValidationErrors.push({
            hguid: patch.hguid,
            field: CONTAINER_DETAIL_ENGLISH_NAME_FIELD,
            code: 'EMPTY_ENGLISH_NAME',
            message: '英文名称为空时不会保存，如需清空请使用“清除英文名称”',
          })
        }
      }
      return update
    })
    .filter((update) => getSubmittedContainerDetailFields(update).length > 0)

  return {
    pendingPatches,
    detailUpdates,
    localValidationErrors,
    importPriceCount: pendingPatches.filter((patch) => patch.进口价格 != null).length,
    retailPriceCount: pendingPatches.filter((patch) => patch.贴牌价格 != null).length,
    englishNameCount: pendingPatches.filter((patch) => patch.英文名称 !== undefined).length,
    clearEnglishNameCount: pendingPatches.filter((patch) => patch.ClearEnglishName === true).length,
  }
}

function hasValidationError(
  validationErrors: ContainerDetailSaveValidationError[],
  hguid: string,
  field: string,
) {
  return validationErrors.some((error) => (
    error.hguid === hguid
    && (error.field === field || error.field === '*')
  ))
}

function removeSavedPendingField<K extends ContainerDetailDraftField | 'ClearEnglishName'>(
  currentPatch: PendingContainerDetailPatch,
  submittedPatch: UpdateContainerDetailRequest,
  field: K,
) {
  if (!(field in submittedPatch)) return
  if (field === '英文名称') {
    if (
      currentPatch.英文名称 !== undefined
      && normalizeContainerDetailEnglishNameForSave(currentPatch.英文名称) === submittedPatch.英文名称
    ) {
      delete currentPatch.英文名称
    }
    return
  }
  if (currentPatch[field] === submittedPatch[field]) delete currentPatch[field]
}

export function buildContainerDetailSuccessfulEnglishNameUpdates(
  currentPatches: PendingContainerDetailPatchMap,
  submittedUpdates: UpdateContainerDetailRequest[],
  validationErrors: ContainerDetailSaveValidationError[],
): Array<Pick<UpdateContainerDetailRequest, 'hguid' | '英文名称'>> {
  return submittedUpdates.flatMap((submittedUpdate) => {
    const submittedEnglishName = submittedUpdate.英文名称
    if (
      submittedEnglishName === undefined
      || hasValidationError(
        validationErrors,
        submittedUpdate.hguid,
        CONTAINER_DETAIL_ENGLISH_NAME_FIELD,
      )
    ) {
      return []
    }

    const currentPatch = currentPatches[submittedUpdate.hguid]
    if (currentPatch?.ClearEnglishName === true) return []
    if (
      currentPatch?.英文名称 !== undefined
      && normalizeContainerDetailEnglishNameForSave(currentPatch.英文名称) !== submittedEnglishName
    ) {
      return []
    }

    return [{ hguid: submittedUpdate.hguid, 英文名称: submittedEnglishName }]
  })
}

export function clearSavedPendingContainerDetailFields(
  current: PendingContainerDetailPatchMap,
  submittedUpdates: UpdateContainerDetailRequest[],
  validationErrors: ContainerDetailSaveValidationError[],
): PendingContainerDetailPatchMap {
  const next = { ...current }
  submittedUpdates.forEach((submittedPatch) => {
    const currentPatch = next[submittedPatch.hguid]
    if (!currentPatch) return
    const remainingPatch = { ...currentPatch }
    getSubmittedContainerDetailFields(submittedPatch).forEach((field) => {
      if (hasValidationError(validationErrors, submittedPatch.hguid, field)) return
      if (field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD) {
        removeSavedPendingField(remainingPatch, submittedPatch, '英文名称')
        removeSavedPendingField(remainingPatch, submittedPatch, 'ClearEnglishName')
        return
      }
      removeSavedPendingField(remainingPatch, submittedPatch, field)
    })
    if (hasPendingContainerDetailFields(remainingPatch)) next[submittedPatch.hguid] = remainingPatch
    else delete next[submittedPatch.hguid]
  })
  return next
}

export function countSuccessfullySavedContainerDetailRows(
  submittedUpdates: UpdateContainerDetailRequest[],
  validationErrors: ContainerDetailSaveValidationError[],
) {
  return new Set(
    submittedUpdates
      .filter((update) => getSubmittedContainerDetailFields(update).some((field) => (
        !hasValidationError(validationErrors, update.hguid, field)
      )))
      .map((update) => update.hguid),
  ).size
}

export function shouldInvalidateContainerDetailLoadAfterSave({
  saveContainerGuid,
  currentContainerGuid,
  detailRequestIdAtSaveStart,
  currentDetailRequestId,
  isSameAbortController,
}: {
  saveContainerGuid: string
  currentContainerGuid: string
  detailRequestIdAtSaveStart: number
  currentDetailRequestId: number
  isSameAbortController: boolean
}) {
  return saveContainerGuid === currentContainerGuid
    && detailRequestIdAtSaveStart === currentDetailRequestId
    && isSameAbortController
}

export async function settleScopedContainerDetailSave<T>(
  request: Promise<T>,
  snapshot: {
    saveContainerGuid: string
    detailRequestIdAtSaveStart: number
    abortControllerTokenAtSaveStart: unknown
  },
  getCurrentContext: () => {
    containerGuid: string
    detailRequestId: number
    abortControllerToken: unknown
  },
) {
  const result = await request
  const currentContext = getCurrentContext()
  const isCurrentContainer = snapshot.saveContainerGuid === currentContext.containerGuid
  const isSameDetailLoad = (
    snapshot.detailRequestIdAtSaveStart === currentContext.detailRequestId
    && snapshot.abortControllerTokenAtSaveStart === currentContext.abortControllerToken
  )
  return {
    result,
    isCurrentContainer,
    shouldInvalidateDetailLoad: shouldInvalidateContainerDetailLoadAfterSave({
      saveContainerGuid: snapshot.saveContainerGuid,
      currentContainerGuid: currentContext.containerGuid,
      detailRequestIdAtSaveStart: snapshot.detailRequestIdAtSaveStart,
      currentDetailRequestId: currentContext.detailRequestId,
      isSameAbortController: (
        snapshot.abortControllerTokenAtSaveStart === currentContext.abortControllerToken
      ),
    }),
    shouldReloadCurrentDetail: isCurrentContainer && !isSameDetailLoad,
  }
}

export interface ContainerDetailTableScrollYOptions {
  viewportHeight: number
  toolbarHeight: number
  tableChromeHeight: number
  isSmallLandscape: boolean
  isSmallPortrait: boolean
  maxScrollY: number
}

export function calculateContainerDetailTableScrollY({
  viewportHeight,
  toolbarHeight,
  tableChromeHeight,
  isSmallLandscape,
  isSmallPortrait,
  maxScrollY,
}: ContainerDetailTableScrollYOptions) {
  const safeViewportHeight = Number.isFinite(viewportHeight) ? viewportHeight : maxScrollY
  const stableContentTop = isSmallLandscape ? 88 : isSmallPortrait ? 72 : 150
  const safeToolbarHeight = Math.max(0, Number.isFinite(toolbarHeight) ? toolbarHeight : 0)
  const safeTableChromeHeight = Math.max(0, Number.isFinite(tableChromeHeight) ? tableChromeHeight : 0)
  const bottomInset = isSmallLandscape ? 12 : isSmallPortrait ? 88 : 24
  const contentGap = isSmallLandscape ? 8 : isSmallPortrait ? 10 : 12
  const hardMinScrollY = isSmallLandscape ? 96 : isSmallPortrait ? 112 : 220

  // 表格高度只按稳定工作区位置计算，避免滚动时实时测量 top 导致虚拟表格反复重排。
  const availableHeight = safeViewportHeight - stableContentTop - bottomInset - safeToolbarHeight - contentGap - safeTableChromeHeight
  return Math.max(hardMinScrollY, Math.min(maxScrollY, availableHeight))
}

export interface ContainerDetailLoadMoreScrollMetrics {
  scrollTop: number
  clientHeight: number
  scrollHeight: number
}

export function shouldLoadNextContainerDetailChunk({
  scrollTop,
  clientHeight,
  scrollHeight,
}: ContainerDetailLoadMoreScrollMetrics) {
  const safeScrollTop = Math.max(0, Number.isFinite(scrollTop) ? scrollTop : 0)
  const safeClientHeight = Math.max(0, Number.isFinite(clientHeight) ? clientHeight : 0)
  const safeScrollHeight = Math.max(0, Number.isFinite(scrollHeight) ? scrollHeight : 0)
  const preloadDistance = Math.max(600, safeClientHeight)
  const remainingDistance = safeScrollHeight - safeScrollTop - safeClientHeight

  return remainingDistance <= preloadDistance
}

export interface ContainerDetailAppendRequest {
  key: string
  controller: AbortController
}

export function startContainerDetailAppendRequest(
  activeRequest: ContainerDetailAppendRequest | null,
  requestKey: string,
  controller = new AbortController(),
) {
  if (activeRequest) {
    return {
      request: activeRequest,
      started: false,
    }
  }

  return {
    request: {
      key: requestKey,
      controller,
    },
    started: true,
  }
}

export function cancelContainerDetailAppendRequest(
  request: ContainerDetailAppendRequest | null,
) {
  request?.controller.abort()
}

export function finishContainerDetailAppendRequest(
  activeRequest: ContainerDetailAppendRequest | null,
  finishedRequest: ContainerDetailAppendRequest | null,
) {
  return activeRequest === finishedRequest ? null : activeRequest
}

export type ContainerDetailReadAheadOutcome<T> =
  | { status: 'success'; result: T }
  | { status: 'failure'; error: unknown }

export interface ContainerDetailReadAheadRequest<T> {
  key: string
  pageNumber: number
  controller: AbortController
  promise: Promise<ContainerDetailReadAheadOutcome<T>>
}

export function startContainerDetailReadAheadRequest<T>(
  activeRequest: ContainerDetailReadAheadRequest<T> | null,
  requestKey: string,
  pageNumber: number,
  load: (signal: AbortSignal) => Promise<T>,
) {
  if (activeRequest?.key === requestKey) {
    return {
      request: activeRequest,
      started: false,
    }
  }

  activeRequest?.controller.abort()
  const controller = new AbortController()
  const promise = load(controller.signal).then(
    (result): ContainerDetailReadAheadOutcome<T> => ({ status: 'success', result }),
    (error: unknown): ContainerDetailReadAheadOutcome<T> => ({ status: 'failure', error }),
  )

  return {
    request: {
      key: requestKey,
      pageNumber,
      controller,
      promise,
    },
    started: true,
  }
}

export function cancelContainerDetailReadAheadRequest<T>(
  request: ContainerDetailReadAheadRequest<T> | null,
) {
  request?.controller.abort()
}

export function finishContainerDetailReadAheadRequest<T>(
  activeRequest: ContainerDetailReadAheadRequest<T> | null,
  finishedRequest: ContainerDetailReadAheadRequest<T> | null,
) {
  return activeRequest === finishedRequest ? null : activeRequest
}

export type ContainerDetailSortField =
  | 'itemNumber'
  | 'barcode'
  | 'productName'
  | 'englishName'
  | 'productType'
  | 'newProduct'
  | 'matchType'
  | 'containerPieces'
  | 'middlePackQuantity'
  | 'containerQuantity'
  | 'packingQuantity'
  | 'unitVolume'
  | 'domesticPrice'
  | 'floatRate'
  | 'transportCost'
  | 'unitTransportCost'
  | 'warehouseImportPrice'
  | 'lastOEMPrice'
  | 'importPrice'
  | 'oemPrice'
  | 'warehouseStatus'
  | 'remark'

export type ContainerDetailTableColumnKey =
  | 'index'
  | 'image'
  | 'categoryName'
  | 'readonlyOemPrice'
  // 「成本核算 / 上架定价」视图专用的合成列：商品（图+货号+标签+名称）与问题标签，「全部列」不显示。
  | 'product'
  | 'issues'
  | ContainerDetailSortField

export interface ContainerDetailCategoryFilterLookup {
  byGuid: Map<string, string>
  byName: Map<string, string[]>
  descendantGuidsByGuid: Map<string, Set<string>>
}

export type ContainerDetailEditableCellDirection = 'up' | 'down' | 'left' | 'right'
export type ContainerDetailExportColumnKey =
  | 'index'
  | 'itemNumber'
  | 'barcode'
  | 'barcodeImage'
  | 'productImage'
  | 'productName'
  | 'englishName'
  | 'categoryName'
  | 'containerPieces'
  | 'packingQuantity'
  | 'containerQuantity'
  | 'unitVolume'
  | 'totalVolume'
  | 'middlePackQuantity'
  | 'domesticPrice'
  | 'transportCost'
  | 'unitTransportCost'
  | 'floatRate'
  | 'importPrice'
  | 'lastImportPrice'
  | 'lastOEMPrice'
  | 'oemPrice'
  | 'productType'
  | 'newProduct'
  | 'matchType'
  | 'warehouseStatus'
  | 'remark'

export type ContainerDetailExportValueType = 'text' | 'number' | 'integer' | 'money' | 'volume'

export interface ContainerDetailExportColumnDefinition {
  key: ContainerDetailExportColumnKey
  labelKey: string
  fallbackLabel: string
  width: number
  valueType: ContainerDetailExportValueType
}

export type ContainerDetailExportRow = Record<ContainerDetailExportColumnKey, string | number>

export interface ContainerDetailExportRowOptions {
  getProductTypeLabel?: (value: string) => string
  getMatchTypeLabel?: (value: ContainerDetailMatchTypeFilter) => string
  newProductLabel?: string
  existingProductLabel?: string
  activeLabel?: string
  inactiveLabel?: string
  missingNumericValue?: '' | 0
}

export interface UpdateFieldSelectionState {
  isAllSelected: boolean
  isPartiallySelected: boolean
}

export function getUpdateFieldSelectionState<T extends string>(
  selectedFields: readonly T[],
  allFields: readonly T[],
): UpdateFieldSelectionState {
  const fieldSet = new Set(allFields)
  const selectedCount = selectedFields.filter((field) => fieldSet.has(field)).length

  return {
    isAllSelected: allFields.length > 0 && selectedCount === allFields.length,
    isPartiallySelected: selectedCount > 0 && selectedCount < allFields.length,
  }
}

export function getNextUpdateFieldSelection<T extends string>(
  checked: boolean,
  allFields: readonly T[],
): T[] {
  return checked ? [...allFields] : []
}

// 默认导出列固定为业务核对模板，避免用户误导出旧字段顺序。
export const DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS: ContainerDetailExportColumnKey[] = [
  'index',
  'itemNumber',
  'productName',
  'englishName',
  'containerPieces',
  'containerQuantity',
  'unitVolume',
  'totalVolume',
  'middlePackQuantity',
  'domesticPrice',
  'oemPrice',
]

export const DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMN_KEYS: ContainerDetailExportColumnKey[] = [
  'index',
  'productImage',
  'itemNumber',
  'barcodeImage',
  'englishName',
  'oemPrice',
]

// “全部导出”对应当前明细表的业务列顺序；条码保留文本，商品图片单独嵌入工作簿。
export const ALL_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS: ContainerDetailExportColumnKey[] = [
  'index',
  'productImage',
  'itemNumber',
  'barcode',
  'englishName',
  'oemPrice',
  'productName',
  'categoryName',
  'containerPieces',
  'packingQuantity',
  'containerQuantity',
  'unitVolume',
  'domesticPrice',
  'transportCost',
  'unitTransportCost',
  'floatRate',
  'middlePackQuantity',
  'importPrice',
  'lastImportPrice',
  'lastOEMPrice',
  'productType',
  'newProduct',
  'matchType',
  'warehouseStatus',
  'remark',
]

export const CONTAINER_DETAIL_EXPORT_COLUMNS: ContainerDetailExportColumnDefinition[] = [
  { key: 'index', labelKey: 'containers.export.indexColumn', fallbackLabel: '序号', width: 8, valueType: 'integer' },
  { key: 'itemNumber', labelKey: 'containers.fields.itemNumber', fallbackLabel: '货号', width: 18, valueType: 'text' },
  { key: 'barcode', labelKey: 'containers.fields.barcode', fallbackLabel: '条码', width: 20, valueType: 'text' },
  { key: 'barcodeImage', labelKey: 'containers.export.barcodeImageColumn', fallbackLabel: '条码图片', width: 24, valueType: 'text' },
  { key: 'productImage', labelKey: 'containers.export.productImageColumn', fallbackLabel: '商品图片', width: 18, valueType: 'text' },
  { key: 'productName', labelKey: 'containers.export.chineseNameColumn', fallbackLabel: '中文名称', width: 36, valueType: 'text' },
  { key: 'englishName', labelKey: 'containers.fields.englishName', fallbackLabel: '英文名称', width: 36, valueType: 'text' },
  { key: 'containerPieces', labelKey: 'containers.export.piecesColumn', fallbackLabel: '件数', width: 12, valueType: 'integer' },
  { key: 'containerQuantity', labelKey: 'containers.export.totalQuantityColumn', fallbackLabel: '总装柜数', width: 12, valueType: 'integer' },
  { key: 'unitVolume', labelKey: 'containers.export.unitVolumeColumn', fallbackLabel: '单件体积', width: 12, valueType: 'volume' },
  { key: 'totalVolume', labelKey: 'containers.export.totalVolumeColumn', fallbackLabel: '总体积', width: 12, valueType: 'volume' },
  { key: 'middlePackQuantity', labelKey: 'containers.fields.middlePackQuantity', fallbackLabel: '中包数', width: 12, valueType: 'integer' },
  { key: 'domesticPrice', labelKey: 'containers.fields.domesticPrice', fallbackLabel: '国内价格', width: 12, valueType: 'money' },
  { key: 'lastImportPrice', labelKey: 'containers.fields.warehouseImportPrice', fallbackLabel: '实时进货价', width: 14, valueType: 'money' },
  { key: 'lastOEMPrice', labelKey: 'containers.fields.lastOEMPrice', fallbackLabel: '实时零售价', width: 14, valueType: 'money' },
  { key: 'oemPrice', labelKey: 'containers.fields.oemPrice', fallbackLabel: '零售价', width: 12, valueType: 'money' },
  { key: 'categoryName', labelKey: 'containers.fields.category', fallbackLabel: '分类', width: 24, valueType: 'text' },
  { key: 'packingQuantity', labelKey: 'containers.fields.packingQuantity', fallbackLabel: '单件装箱数', width: 14, valueType: 'integer' },
  { key: 'transportCost', labelKey: 'containers.fields.transportCost', fallbackLabel: '运输成本', width: 14, valueType: 'money' },
  { key: 'unitTransportCost', labelKey: 'containers.fields.unitTransportCost', fallbackLabel: '单件运输成本', width: 16, valueType: 'money' },
  { key: 'floatRate', labelKey: 'containers.fields.floatRate', fallbackLabel: '调整浮率', width: 12, valueType: 'number' },
  { key: 'importPrice', labelKey: 'containers.fields.importPrice', fallbackLabel: '进口价格', width: 12, valueType: 'money' },
  { key: 'productType', labelKey: 'containers.fields.productType', fallbackLabel: '类型', width: 14, valueType: 'text' },
  { key: 'newProduct', labelKey: 'containers.fields.newProduct', fallbackLabel: '新商品', width: 12, valueType: 'text' },
  { key: 'matchType', labelKey: 'containers.fields.matchType', fallbackLabel: '匹配方式', width: 16, valueType: 'text' },
  { key: 'warehouseStatus', labelKey: 'containers.fields.warehouseStatus', fallbackLabel: '仓库状态', width: 12, valueType: 'text' },
  { key: 'remark', labelKey: 'containers.fields.remark', fallbackLabel: '备注', width: 24, valueType: 'text' },
]

const containerDetailSortFields = new Set<string>([
  'itemNumber',
  'barcode',
  'productName',
  'englishName',
  'productType',
  'newProduct',
  'matchType',
  'containerPieces',
  'middlePackQuantity',
  'containerQuantity',
  'packingQuantity',
  'unitVolume',
  'domesticPrice',
  'floatRate',
  'transportCost',
  'unitTransportCost',
  'warehouseImportPrice',
  'lastOEMPrice',
  'importPrice',
  'oemPrice',
  'warehouseStatus',
  'remark',
])

const chineseTextPattern = /[\u4e00-\u9fff]/

export function containsChineseText(value?: string) {
  return Boolean(value && chineseTextPattern.test(value))
}

export function isValidContainerDetailEnglishTranslation(value?: string) {
  return Boolean(value?.trim()) && !containsChineseText(value)
}

export function getPendingContainerDetailEnglishNameError(
  patch?: PendingContainerDetailPatch,
): 'EMPTY_ENGLISH_NAME' | 'CONTAINS_CHINESE' | undefined {
  if (patch?.ClearEnglishName === true || patch?.英文名称 === undefined) return undefined
  if (!patch.英文名称.trim()) return 'EMPTY_ENGLISH_NAME'
  if (containsChineseText(patch.英文名称)) return 'CONTAINS_CHINESE'
  return undefined
}

export function isContainerDetailSortField(value: unknown): value is ContainerDetailSortField {
  return typeof value === 'string' && containerDetailSortFields.has(value)
}

export function mergeContainerDetailColumnOrder(
  savedOrder: readonly unknown[] | null | undefined,
  availableOrder: readonly ContainerDetailTableColumnKey[],
): ContainerDetailTableColumnKey[] {
  const availableSet = new Set(availableOrder)
  const seen = new Set<ContainerDetailTableColumnKey>()
  const merged: ContainerDetailTableColumnKey[] = []

  for (const value of savedOrder ?? []) {
    if (typeof value !== 'string' || !availableSet.has(value as ContainerDetailTableColumnKey)) {
      continue
    }
    const key = value as ContainerDetailTableColumnKey
    if (seen.has(key)) {
      continue
    }
    seen.add(key)
    merged.push(key)
  }

  for (const key of availableOrder) {
    if (!seen.has(key)) {
      merged.push(key)
    }
  }

  return merged
}

export function moveContainerDetailColumnOrder(
  currentOrder: readonly ContainerDetailTableColumnKey[],
  activeKey: unknown,
  overKey: unknown,
): ContainerDetailTableColumnKey[] {
  if (typeof activeKey !== 'string' || typeof overKey !== 'string' || activeKey === overKey) {
    return [...currentOrder]
  }

  const fromIndex = currentOrder.indexOf(activeKey as ContainerDetailTableColumnKey)
  const toIndex = currentOrder.indexOf(overKey as ContainerDetailTableColumnKey)
  if (fromIndex < 0 || toIndex < 0) {
    return [...currentOrder]
  }

  const nextOrder = [...currentOrder]
  const [moved] = nextOrder.splice(fromIndex, 1)
  nextOrder.splice(toIndex, 0, moved)
  return nextOrder
}

export function isContainerDetailColumnOrderCustomized(
  currentOrder: readonly ContainerDetailTableColumnKey[],
  defaultOrder: readonly ContainerDetailTableColumnKey[],
) {
  if (!currentOrder.length) {
    return false
  }
  if (currentOrder.length !== defaultOrder.length) {
    return true
  }
  return currentOrder.some((key, index) => key !== defaultOrder[index])
}

export function getContainerDetailEditableColumnKeysInOrder<ColumnKey extends string>(
  currentColumnOrder: readonly string[],
  editableColumnKeys: readonly ColumnKey[],
): ColumnKey[] {
  const editableColumnKeySet = new Set<string>(editableColumnKeys)
  return currentColumnOrder.filter((key): key is ColumnKey => editableColumnKeySet.has(key))
}

export function getNextContainerDetailEditableCell(
  currentRowKey: string,
  currentColumnKey: string,
  rowKeys: readonly string[],
  columnKeys: readonly string[],
  direction: ContainerDetailEditableCellDirection,
) {
  const rowIndex = rowKeys.indexOf(currentRowKey)
  const columnIndex = columnKeys.indexOf(currentColumnKey)
  if (rowIndex < 0 || columnIndex < 0) {
    return null
  }

  const nextRowIndex = direction === 'up'
    ? rowIndex - 1
    : direction === 'down'
      ? rowIndex + 1
      : rowIndex
  const nextColumnIndex = direction === 'left'
    ? columnIndex - 1
    : direction === 'right'
      ? columnIndex + 1
      : columnIndex
  const nextRowKey = rowKeys[nextRowIndex]
  const nextColumnKey = columnKeys[nextColumnIndex]
  if (!nextRowKey || !nextColumnKey) {
    return null
  }

  return {
    rowKey: nextRowKey,
    columnKey: nextColumnKey,
  }
}

export interface ContainerDetailNumberRangeFilter {
  min?: number
  max?: number
}

export interface ContainerDetailColumnFilters {
  itemNumber?: string
  barcode?: string
  productName?: string
  englishName?: string
  productTypes?: ContainerDetailProductTypeFilter[]
  newProductStates?: ContainerDetailNewProductFilter[]
  matchTypes?: ContainerDetailMatchTypeFilter[]
  containerPieces?: ContainerDetailNumberRangeFilter
  middlePackQuantity?: ContainerDetailNumberRangeFilter
  containerQuantity?: ContainerDetailNumberRangeFilter
  packingQuantity?: ContainerDetailNumberRangeFilter
  unitVolume?: ContainerDetailNumberRangeFilter
  domesticPrice?: ContainerDetailNumberRangeFilter
  floatRate?: ContainerDetailNumberRangeFilter
  transportCost?: ContainerDetailNumberRangeFilter
  unitTransportCost?: ContainerDetailNumberRangeFilter
  warehouseImportPrice?: ContainerDetailNumberRangeFilter
  lastOEMPrice?: ContainerDetailNumberRangeFilter
  importPrice?: ContainerDetailNumberRangeFilter
  oemPrice?: ContainerDetailNumberRangeFilter
  warehouseStatus?: ContainerDetailWarehouseStatusFilter[]
  remark?: string
}

export interface ContainerDetailSortState {
  field: ContainerDetailSortField
  order: ContainerDetailSortOrder
}

/**
 * 本柜新品：未建档，或由本货柜「创建新商品/提交货柜」建档。
 * 是否新商品 在建档后变为 false（建档、零售价来源等操作仍以它为准），
 * 新商品列、新商品/已有商品筛选与排序改用本口径，建档后仍能区分新品。
 */
export function isContainerDetailContainerNewProduct(row: ContainerDetail) {
  return row.isContainerNewProduct ?? Boolean(row.是否新商品)
}

/** 本柜新品且已建档：用于在新商品标签上补充「已建档」说明。 */
export function isContainerDetailCreatedContainerNewProduct(row: ContainerDetail) {
  return isContainerDetailContainerNewProduct(row) && !row.是否新商品
}

export function getContainerDetailProductName(row: ContainerDetail) {
  return row.商品名称 ?? row.商品信息?.商品名称
}

export function getContainerDetailImageUrl(row: ContainerDetail) {
  // 货柜明细和商品信息可能来自不同接口层，图片字段需要按明细优先、商品信息兜底读取。
  return row.商品图片?.trim() || row.商品信息?.商品图片?.trim() || undefined
}

export function getContainerDetailCreateProductRowLabel(row: ContainerDetail) {
  return getContainerDetailItemNumber(row) ?? getContainerDetailProductCode(row) ?? row.hguid
}

export function findContainerDetailRowsMissingCreateProductRetailPrice(rows: ContainerDetail[]) {
  return rows
    .filter((row) => row.是否新商品)
    .map((row) => {
      const retailPrice = resolveContainerDetailOemPrice(row)
      return {
        hguid: row.hguid,
        label: getContainerDetailCreateProductRowLabel(row),
        retailPrice,
      }
    })
    // 创建仓库新商品会把该价格写入商品主表、仓库商品和分店零售价，必须为有效正数。
    .filter((row) => !(typeof row.retailPrice === 'number' && Number.isFinite(row.retailPrice) && row.retailPrice > 0))
}

export function getContainerDetailEnglishName(row: ContainerDetail) {
  return row.英文名称 ?? row.商品信息?.英文名称
}

export function getContainerDetailTranslationSource(row: ContainerDetail) {
  const englishName = getContainerDetailEnglishName(row)
  if (containsChineseText(englishName)) return englishName
  return getContainerDetailProductName(row)
}

export function getContainerDetailItemNumber(row: ContainerDetail) {
  return row.商品信息?.货号?.trim() || undefined
}

export function getContainerDetailBarcode(row: ContainerDetail) {
  return row.商品信息?.条形码?.trim() || undefined
}

export function getContainerDetailProductCode(row: ContainerDetail) {
  return row.商品编码?.trim() || row.商品信息?.商品编码?.trim() || undefined
}

function firstTrimmedValue(...values: Array<string | undefined>) {
  return values.map((value) => value?.trim()).find((value): value is string => Boolean(value))
}

export function getContainerDetailLocalProductCode(row: ContainerDetail) {
  return firstTrimmedValue(row.localProductCode, row.LocalProductCode)
}

export function getContainerDetailDomesticProductCode(row: ContainerDetail) {
  return firstTrimmedValue(row.domesticProductCode, row.DomesticProductCode, getContainerDetailProductCode(row))
}

export function hasContainerDetailProductCodeConflict(row: ContainerDetail) {
  const explicit = row.hasProductCodeConflict ?? row.HasProductCodeConflict
  if (explicit != null) return Boolean(explicit)

  const localProductCode = normalizeMatchKey(getContainerDetailLocalProductCode(row))
  const domesticProductCode = normalizeMatchKey(getContainerDetailDomesticProductCode(row))
  return Boolean(localProductCode && domesticProductCode && localProductCode !== domesticProductCode)
}

export function getContainerDetailCategoryName(row: ContainerDetail) {
  return firstTrimmedValue(
    row.categoryName,
    row.CategoryName,
    row.productCategoryName,
    row.ProductCategoryName,
    row.商品信息?.categoryName,
    row.商品信息?.CategoryName,
    row.商品信息?.productCategoryName,
    row.商品信息?.ProductCategoryName,
  )
}

export function getContainerDetailCategoryPath(row: ContainerDetail) {
  return firstTrimmedValue(
    row.categoryPath,
    row.CategoryPath,
    row.categoryFullPath,
    row.CategoryFullPath,
    row.商品信息?.categoryPath,
    row.商品信息?.CategoryPath,
    row.商品信息?.categoryFullPath,
    row.商品信息?.CategoryFullPath,
  )
}

export function getContainerDetailCategoryGuid(row: ContainerDetail) {
  return firstTrimmedValue(
    row.warehouseCategoryGUID,
    row.WarehouseCategoryGUID,
    row.productCategoryGUID,
    row.ProductCategoryGUID,
    row.商品信息?.warehouseCategoryGUID,
    row.商品信息?.WarehouseCategoryGUID,
    row.商品信息?.productCategoryGUID,
    row.商品信息?.ProductCategoryGUID,
  )
}

export function getContainerDetailCategoryTooltipRecord(row: ContainerDetail) {
  return {
    categoryName: getContainerDetailCategoryName(row),
    categoryPath: getContainerDetailCategoryPath(row),
    warehouseCategoryGUID: getContainerDetailCategoryGuid(row),
  }
}

function isContainerDetailCategoryNameInSelectedSubtree(
  categoryName: string | undefined,
  selectedCategoryGuid: string,
  lookup?: ContainerDetailCategoryFilterLookup,
) {
  if (!categoryName || !lookup) {
    return false
  }

  const selectedPath = lookup.byGuid.get(selectedCategoryGuid)
  if (!selectedPath) {
    return false
  }

  const pathsByName = lookup.byName.get(categoryName)
  return pathsByName?.some((path) => path === selectedPath || path.startsWith(`${selectedPath} > `)) ?? false
}

export function matchesContainerDetailCategoryFilter(
  row: ContainerDetail,
  categoryFilterValue: string,
  lookup?: ContainerDetailCategoryFilterLookup,
) {
  if (!categoryFilterValue || categoryFilterValue === CONTAINER_DETAIL_ALL_CATEGORY_FILTER_KEY) {
    return true
  }

  const categoryGuid = getContainerDetailCategoryGuid(row)
  const categoryName = getContainerDetailCategoryName(row)

  if (categoryFilterValue === CONTAINER_DETAIL_UNCATEGORIZED_FILTER_KEY) {
    return !categoryGuid && !categoryName
  }

  const allowedCategoryGuids = lookup?.descendantGuidsByGuid.get(categoryFilterValue)
  if (categoryGuid) {
    return allowedCategoryGuids ? allowedCategoryGuids.has(categoryGuid) : categoryGuid === categoryFilterValue
  }

  // 有些接口暂时只给分类名称，没有 GUID；用分类树路径兜底判断是否落在当前父分类子树内。
  return isContainerDetailCategoryNameInSelectedSubtree(categoryName, categoryFilterValue, lookup)
}

export function applyContainerDetailCategoryFilter(
  rows: ContainerDetail[],
  categoryFilterValue: string,
  lookup?: ContainerDetailCategoryFilterLookup,
) {
  return rows.filter((row) => matchesContainerDetailCategoryFilter(row, categoryFilterValue, lookup))
}

export function getContainerDetailBatchCategoryProductCodes(rows: ContainerDetail[]) {
  const productCodes: string[] = []
  const seen = new Set<string>()
  let skippedMissingCodeCount = 0

  for (const row of rows) {
    const productCode = getContainerDetailProductCode(row)
    if (!productCode) {
      skippedMissingCodeCount += 1
      continue
    }
    if (seen.has(productCode)) {
      continue
    }
    seen.add(productCode)
    productCodes.push(productCode)
  }

  return { productCodes, skippedMissingCodeCount }
}

export function getContainerDetailMatchType(row: ContainerDetail): ContainerDetailMatchTypeFilter {
  const raw = row.matchType ?? row.MatchType
  const normalized = raw?.trim().toLowerCase()
  if (normalized === 'productcode' || normalized === 'product_code' || normalized === '商品编码') {
    return 'productCode'
  }
  if (
    normalized === 'supplieritem' ||
    normalized === 'supplier_item' ||
    normalized === 'item_number' ||
    normalized === 'itemnumber' ||
    normalized === '供应商编码+货号' ||
    normalized === '货号匹配'
  ) return 'supplierItem'
  if (normalized === 'unmatched' || normalized === '未匹配') return 'unmatched'
  return 'unmatched'
}

export function getContainerDetailProductType(row: ContainerDetail) {
  // 套装子商品只记录在货柜明细快照中，必须优先识别；其他类型仍以国内商品表为准。
  if (row.商品类型 === '套装子商品') return '套装子商品'
  return row.商品信息?.商品类型 || row.商品类型 || '普通商品'
}

export function getContainerDetailProductTypeFilterKey(row: ContainerDetail): ContainerDetailProductTypeFilter {
  const type = getContainerDetailProductType(row)
  if (type === '套装商品') return 'set'
  if (type === '多码商品') return 'multi'
  if (type === '套装子商品') return 'setChild'
  return 'normal'
}

export function getContainerDetailWarehouseStatusFilterKey(row: ContainerDetail): ContainerDetailWarehouseStatusFilter {
  return row.warehouseIsActive === true ? 'active' : 'inactive'
}

export function resolveContainerDetailOemPrice(row: ContainerDetail): number | undefined {
  // 纯货柜明细业务价；新商品创建和缺价判断仍以它为准。
  return row.贴牌价格
}

export function getContainerDetailReadonlyOemPrice(row: ContainerDetail): number | undefined {
  // 只读快览价由后端按新商品/已有商品分流；缺字段时不回退明细业务价。
  return row.readonlyOemPrice ?? row.ReadonlyOemPrice
}

export function getContainerDetailOemPriceSource(row: ContainerDetail): 'detail' | 'none' {
  return row.贴牌价格 == null ? 'none' : 'detail'
}

export function getContainerDetailRealtimeImportPrice(row: ContainerDetail): number | undefined {
  // 实时进货价只读仓库商品表字段；缺失时不回退货柜历史快照。
  return row.warehouseImportPrice ?? row.WarehouseImportPrice
}

export function getContainerDetailLastImportPrice(row: ContainerDetail): number | undefined {
  return getContainerDetailRealtimeImportPrice(row)
}

export function getContainerDetailImportPriceTrend(row: ContainerDetail): 'up' | 'down' | undefined {
  const realtimeImportPrice = getContainerDetailRealtimeImportPrice(row)
  const currentImportPrice = row.进口价格
  if (
    typeof realtimeImportPrice !== 'number' ||
    typeof currentImportPrice !== 'number' ||
    !Number.isFinite(realtimeImportPrice) ||
    !Number.isFinite(currentImportPrice) ||
    realtimeImportPrice === currentImportPrice
  ) {
    return undefined
  }

  // 趋势以本次进口价格相对实时仓库进货价判断，用于表格箭头和颜色。
  return currentImportPrice > realtimeImportPrice ? 'up' : 'down'
}

export function getContainerDetailRealtimeRetailPrice(row: ContainerDetail): number | undefined {
  // 实时零售价取 WarehouseProduct.OEMPrice；缺失时不回退货柜明细零售价。
  return row.warehouseOEMPrice ?? row.WarehouseOEMPrice
}

export function getContainerDetailVisibleOemPrice(row: ContainerDetail): number | undefined {
  // 表格零售价：新商品沿用明细价，已有商品绑定仓库实时零售价。
  return row.是否新商品 ? resolveContainerDetailOemPrice(row) : getContainerDetailRealtimeRetailPrice(row)
}

export function getContainerDetailLastOemPrice(row: ContainerDetail): number | undefined {
  return getContainerDetailRealtimeRetailPrice(row)
}

export function calculateContainerDetailUnitTransportCost(row: ContainerDetail): number | undefined {
  if (row.运输成本 == null || row.单件装箱数 == null) return undefined
  return roundToDigits(row.运输成本 * row.单件装箱数, 2)
}

export function getContainerDetailExportColumns(
  selectedKeys: readonly ContainerDetailExportColumnKey[] = DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMN_KEYS,
) {
  const columnMap = new Map(CONTAINER_DETAIL_EXPORT_COLUMNS.map((column) => [column.key, column]))
  const seen = new Set<ContainerDetailExportColumnKey>()
  const columns: ContainerDetailExportColumnDefinition[] = []

  for (const key of selectedKeys) {
    if (seen.has(key)) continue
    const column = columnMap.get(key)
    if (!column) continue
    seen.add(key)
    columns.push(column)
  }

  return columns
}

function getContainerDetailUnitVolume(row: ContainerDetail, missingNumericValue: '' | 0 = 0) {
  return row.单件体积 ?? row.商品信息?.单件体积 ?? missingNumericValue
}

function getContainerDetailTotalVolume(row: ContainerDetail, missingNumericValue: '' | 0 = 0) {
  const unitVolume = row.单件体积 ?? row.商品信息?.单件体积
  // 优先使用后端已落库的合计体积；缺失时按件数和单件体积生成导出兜底值。
  return row.合计装柜体积 ?? (
    row.装柜件数 != null && unitVolume != null
      ? row.装柜件数 * unitVolume
      : missingNumericValue
  )
}

export function buildContainerDetailExportRow(
  row: ContainerDetail,
  index = 0,
  options: ContainerDetailExportRowOptions = {},
): ContainerDetailExportRow {
  const missingNumericValue = options.missingNumericValue ?? 0
  const matchType = getContainerDetailMatchType(row)
  const productType = getContainerDetailProductType(row)
  return {
    index: index + 1,
    itemNumber: getContainerDetailItemNumber(row) ?? '',
    barcode: getContainerDetailBarcode(row) ?? '',
    barcodeImage: getContainerDetailBarcode(row) ?? '',
    productImage: getContainerDetailImageUrl(row) ?? '',
    productName: getContainerDetailProductName(row) ?? '',
    englishName: getContainerDetailEnglishName(row) ?? '',
    categoryName: getContainerDetailCategoryPath(row) ?? getContainerDetailCategoryName(row) ?? '',
    containerPieces: row.装柜件数 ?? missingNumericValue,
    packingQuantity: row.单件装箱数 ?? missingNumericValue,
    containerQuantity: row.装柜数量 ?? missingNumericValue,
    unitVolume: getContainerDetailUnitVolume(row, missingNumericValue),
    totalVolume: getContainerDetailTotalVolume(row, missingNumericValue),
    middlePackQuantity: row.中包数 ?? missingNumericValue,
    domesticPrice: row.国内价格 ?? missingNumericValue,
    transportCost: row.运输成本 ?? missingNumericValue,
    unitTransportCost: calculateContainerDetailUnitTransportCost(row) ?? missingNumericValue,
    floatRate: row.调整浮率 ?? missingNumericValue,
    importPrice: row.进口价格 ?? missingNumericValue,
    lastImportPrice: getContainerDetailRealtimeImportPrice(row) ?? missingNumericValue,
    lastOEMPrice: getContainerDetailRealtimeRetailPrice(row) ?? missingNumericValue,
    oemPrice: getContainerDetailVisibleOemPrice(row) ?? missingNumericValue,
    productType: options.getProductTypeLabel?.(productType) ?? productType,
    newProduct: isContainerDetailContainerNewProduct(row)
      ? (options.newProductLabel ?? '新商品')
      : (options.existingProductLabel ?? '已有商品'),
    matchType: options.getMatchTypeLabel?.(matchType) ?? matchType,
    warehouseStatus: row.warehouseIsActive === true
      ? (options.activeLabel ?? '上架')
      : (options.inactiveLabel ?? '下架'),
    remark: row.备注 ?? '',
  }
}

export function buildContainerDetailExportRows(
  rows: ContainerDetail[],
  options: ContainerDetailExportRowOptions = {},
): ContainerDetailExportRow[] {
  return rows.map((row, index) => buildContainerDetailExportRow(row, index, options))
}

export function withContainerDetailEnglishName(row: ContainerDetail, englishName?: string): ContainerDetail {
  return {
    ...row,
    英文名称: englishName,
    商品信息: row.商品信息 ? { ...row.商品信息, 英文名称: englishName } : row.商品信息,
  }
}

export function mergeContainerDetailPatch(row: ContainerDetail, patch: Partial<ContainerDetail>): ContainerDetail {
  const next = { ...row, ...patch }
  const productInfoPatch: Partial<NonNullable<ContainerDetail['商品信息']>> = {}

  if ('英文名称' in patch) {
    productInfoPatch.英文名称 = patch.英文名称
  }
  if ('商品名称' in patch) {
    productInfoPatch.商品名称 = patch.商品名称
  }
  if ('单件装箱数' in patch) {
    productInfoPatch.单件装箱数 = patch.单件装箱数
  }
  if ('单件体积' in patch) {
    productInfoPatch.单件体积 = patch.单件体积
  }

  if (Object.keys(productInfoPatch).length > 0 && next.商品信息) {
    return { ...next, 商品信息: { ...next.商品信息, ...productInfoPatch } }
  }

  return next
}

export function buildContainerDetailSaveFailureKeys(rowKey: string, patch: object) {
  const fields = Array.from(new Set(
    Object.keys(patch)
      .filter((key) => key !== 'hguid')
      .map((field) => field === 'ClearEnglishName' ? CONTAINER_DETAIL_ENGLISH_NAME_FIELD : field),
  )).sort()
  if (!fields.length) {
    return [`${rowKey}:__row__`]
  }
  return fields.map((field) => `${rowKey}:${field}`)
}

export function reconcilePendingContainerDetailSaveFailureKeys(
  failedKeys: ReadonlySet<string>,
  pendingPatches: PendingContainerDetailPatchMap,
) {
  const currentKeys = new Set(
    Object.values(pendingPatches).flatMap((patch) => (
      buildContainerDetailSaveFailureKeys(patch.hguid, patch)
    )),
  )
  return Array.from(failedKeys).filter((key) => currentKeys.has(key)).sort()
}

export function matchesContainerDetailTagFilter(row: ContainerDetail, filter: ContainerDetailTagFilter) {
  if (filter === 'new') return isContainerDetailContainerNewProduct(row)
  if (filter === 'existing') return !isContainerDetailContainerNewProduct(row)
  if (isContainerDetailProductTypeTag(filter)) {
    return getContainerDetailProductTypeFilterKey(row) === filter
  }
  if (filter === 'noOemPrice') {
    const oemPrice = resolveContainerDetailOemPrice(row)
    return Boolean(row.是否新商品) && (!oemPrice || oemPrice <= 0)
  }
  if (filter === 'abnormalImport') return !row.进口价格 || row.进口价格 <= 0
  if (filter === 'active') return row.warehouseIsActive === true
  if (filter === 'inactive') return row.warehouseIsActive !== true
  return true
}

const containerDetailTagFilterGroups: ContainerDetailSelectableTagFilter[][] = [
  ['new', 'existing'],
  ['normal', 'set', 'multi', 'setChild'],
  ['noOemPrice', 'abnormalImport'],
  ['active', 'inactive'],
]

const containerDetailProductTypeTags: ContainerDetailProductTypeFilter[] = ['normal', 'set', 'multi', 'setChild']

function isContainerDetailProductTypeTag(tag: ContainerDetailTagFilter): tag is ContainerDetailProductTypeFilter {
  return containerDetailProductTypeTags.includes(tag as ContainerDetailProductTypeFilter)
}

export function matchesContainerDetailSelectedTags(row: ContainerDetail, selectedTags: ContainerDetailTagFilter[]) {
  const selected = selectedTags.filter((tag): tag is ContainerDetailSelectableTagFilter => tag !== 'all')
  if (!selected.length) return true

  return containerDetailTagFilterGroups.every((group) => {
    const selectedInGroup = group.filter((tag) => selected.includes(tag))
    if (!selectedInGroup.length) return true
    // 同一类标签取并集，不同类标签再取交集，避免“新商品 + 已有商品”互相抵消。
    return selectedInGroup.some((tag) => matchesContainerDetailTagFilter(row, tag))
  })
}

export interface ContainerDetailLocalTagFilterState {
  loadedQueryKey?: string | null
  baseQueryKey: string
  loadedRowsLength: number
  itemsTotal: number
  hasMore: boolean
  loading: boolean
  loadingMore: boolean
}

export function canUseContainerDetailLocalTagFilters({
  loadedQueryKey,
  baseQueryKey,
  loadedRowsLength,
  itemsTotal,
  hasMore,
  loading,
  loadingMore,
}: ContainerDetailLocalTagFilterState) {
  return (
    loadedQueryKey === baseQueryKey &&
    !hasMore &&
    !loading &&
    !loadingMore &&
    loadedRowsLength >= itemsTotal
  )
}

export function buildContainerDetailTagStats(rows: ContainerDetail[]): ContainerDetailTagStats {
  const stats: ContainerDetailTagStats = {
    all: rows.length,
    new: 0,
    existing: 0,
    noOemPrice: 0,
    abnormalImport: 0,
    active: 0,
    inactive: 0,
    normal: 0,
    set: 0,
    multi: 0,
    setChild: 0,
    productCodeMatched: 0,
    supplierItemMatched: 0,
    unmatched: 0,
  }

  rows.forEach((row) => {
    // 统计栏和标签过滤共用同一判断，避免数量与点击后的列表不一致。
    if (matchesContainerDetailTagFilter(row, 'new')) stats.new += 1
    if (matchesContainerDetailTagFilter(row, 'existing')) stats.existing += 1
    if (matchesContainerDetailTagFilter(row, 'noOemPrice')) stats.noOemPrice += 1
    if (matchesContainerDetailTagFilter(row, 'abnormalImport')) stats.abnormalImport += 1
    if (matchesContainerDetailTagFilter(row, 'active')) stats.active += 1
    if (matchesContainerDetailTagFilter(row, 'inactive')) stats.inactive += 1
    const productType = getContainerDetailProductTypeFilterKey(row)
    stats[productType] += 1
    const matchType = getContainerDetailMatchType(row)
    if (matchType === 'productCode') stats.productCodeMatched += 1
    else if (matchType === 'supplierItem') stats.supplierItemMatched += 1
    else stats.unmatched += 1
  })

  return stats
}

function normalizeText(value?: string) {
  return (value ?? '').trim().toLowerCase()
}

function matchesTextFilter(value: string | undefined, filter: string | undefined) {
  const normalizedFilter = normalizeText(filter)
  if (!normalizedFilter) return true
  return normalizeText(value).includes(normalizedFilter)
}

export function applyContainerDetailLoadedTextFilters(
  rows: ContainerDetail[],
  itemNumberFilter: string,
  filters: ContainerDetailColumnFilters,
) {
  return rows.filter((row) => (
    // 前端文字筛选只作用于当前已加载行，避免输入关键字时触发货柜明细远程重载。
    matchesTextFilter(getContainerDetailItemNumber(row), itemNumberFilter) &&
    matchesTextFilter(getContainerDetailItemNumber(row), filters.itemNumber) &&
    matchesTextFilter(getContainerDetailBarcode(row), filters.barcode) &&
    matchesTextFilter(getContainerDetailProductName(row), filters.productName) &&
    matchesTextFilter(getContainerDetailEnglishName(row), filters.englishName) &&
    matchesTextFilter(row.备注, filters.remark)
  ))
}

export function omitContainerDetailTextFilters(filters: ContainerDetailColumnFilters): ContainerDetailColumnFilters {
  const {
    itemNumber: _itemNumber,
    barcode: _barcode,
    productName: _productName,
    englishName: _englishName,
    remark: _remark,
    ...remoteFilters
  } = filters
  // 文本列头筛选已在前端处理，这里只保留仍需后端查询的数字和枚举筛选。
  return remoteFilters
}

function isEmptyNumberRange(filter: ContainerDetailNumberRangeFilter | undefined) {
  return filter?.min == null && filter?.max == null
}

function matchesNumberRange(value: number | undefined, filter: ContainerDetailNumberRangeFilter | undefined) {
  if (isEmptyNumberRange(filter)) return true
  if (value == null) return false
  if (filter?.min != null && value < filter.min) return false
  if (filter?.max != null && value > filter.max) return false
  return true
}

function matchesOneOf<T extends string>(value: T, selected: T[] | undefined) {
  return !selected?.length || selected.includes(value)
}

function getColumnSortValue(row: ContainerDetail, field: ContainerDetailSortField): string | number | undefined {
  switch (field) {
    case 'itemNumber':
      return getContainerDetailItemNumber(row)
    case 'barcode':
      return getContainerDetailBarcode(row)
    case 'productName':
      return getContainerDetailProductName(row)
    case 'englishName':
      return getContainerDetailEnglishName(row)
    case 'productType': {
      const productType = getContainerDetailProductTypeFilterKey(row)
      if (productType === 'set') return 1
      if (productType === 'multi') return 2
      if (productType === 'setChild') return 3
      return 0
    }
    case 'newProduct':
      return isContainerDetailContainerNewProduct(row) ? 1 : 0
    case 'matchType':
      return getContainerDetailMatchType(row)
    case 'containerPieces':
      return row.装柜件数
    case 'middlePackQuantity':
      return row.中包数
    case 'containerQuantity':
      return row.装柜数量
    case 'packingQuantity':
      return row.单件装箱数
    case 'unitVolume':
      return row.单件体积
    case 'domesticPrice':
      return row.国内价格
    case 'floatRate':
      return row.调整浮率
    case 'transportCost':
      return row.运输成本
    case 'unitTransportCost':
      return calculateContainerDetailUnitTransportCost(row)
    case 'warehouseImportPrice':
      return getContainerDetailRealtimeImportPrice(row)
    case 'lastOEMPrice':
      return getContainerDetailRealtimeRetailPrice(row)
    case 'importPrice':
      return row.进口价格
    case 'oemPrice':
      return getContainerDetailVisibleOemPrice(row)
    case 'warehouseStatus':
      return row.warehouseIsActive === true ? 1 : 0
    case 'remark':
      return row.备注
    default:
      return undefined
  }
}

function compareColumnValues(a: string | number | undefined, b: string | number | undefined) {
  const aEmpty = a == null || (typeof a === 'string' && !a.trim())
  const bEmpty = b == null || (typeof b === 'string' && !b.trim())
  if (aEmpty && bEmpty) return 0
  if (aEmpty) return 1
  if (bEmpty) return -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  return String(a).localeCompare(String(b), 'zh-CN', { numeric: true, sensitivity: 'base' })
}

export function applyContainerDetailColumnState(
  rows: ContainerDetail[],
  filters: ContainerDetailColumnFilters,
  sortState?: ContainerDetailSortState,
) {
  const filtered = rows.filter((row) => (
    matchesTextFilter(getContainerDetailItemNumber(row), filters.itemNumber) &&
    matchesTextFilter(getContainerDetailBarcode(row), filters.barcode) &&
    matchesTextFilter(getContainerDetailProductName(row), filters.productName) &&
    matchesTextFilter(getContainerDetailEnglishName(row), filters.englishName) &&
    matchesTextFilter(row.备注, filters.remark) &&
    matchesOneOf(getContainerDetailProductTypeFilterKey(row), filters.productTypes) &&
    matchesOneOf(isContainerDetailContainerNewProduct(row) ? 'new' : 'existing', filters.newProductStates) &&
    matchesOneOf(getContainerDetailMatchType(row), filters.matchTypes) &&
    matchesOneOf(getContainerDetailWarehouseStatusFilterKey(row), filters.warehouseStatus) &&
    matchesNumberRange(row.装柜件数, filters.containerPieces) &&
    matchesNumberRange(row.中包数, filters.middlePackQuantity) &&
    matchesNumberRange(row.装柜数量, filters.containerQuantity) &&
    matchesNumberRange(row.单件装箱数, filters.packingQuantity) &&
    matchesNumberRange(row.单件体积, filters.unitVolume) &&
    matchesNumberRange(row.国内价格, filters.domesticPrice) &&
    matchesNumberRange(row.调整浮率, filters.floatRate) &&
    matchesNumberRange(row.运输成本, filters.transportCost) &&
    matchesNumberRange(calculateContainerDetailUnitTransportCost(row), filters.unitTransportCost) &&
    matchesNumberRange(getContainerDetailRealtimeImportPrice(row), filters.warehouseImportPrice) &&
    matchesNumberRange(getContainerDetailRealtimeRetailPrice(row), filters.lastOEMPrice) &&
    matchesNumberRange(row.进口价格, filters.importPrice) &&
    matchesNumberRange(getContainerDetailVisibleOemPrice(row), filters.oemPrice)
  ))

  if (!sortState) return filtered

  return filtered
    .map((row, index) => ({ row, index }))
    .sort((left, right) => {
      const result = compareColumnValues(
        getColumnSortValue(left.row, sortState.field),
        getColumnSortValue(right.row, sortState.field),
      )
      if (result === 0) return left.index - right.index
      return sortState.order === 'ascend' ? result : -result
    })
    .map((item) => item.row)
}

export function prepareContainerDetailWholeExportRows(
  rows: ContainerDetail[],
  pendingPatches: PendingContainerDetailPatchMap,
  sortState?: ContainerDetailSortState,
) {
  // 整柜导出只继承当前排序；页面筛选和勾选均不应缩小导出范围。
  return applyContainerDetailColumnState(
    applyPendingContainerDetailPatches(rows, pendingPatches),
    {},
    sortState,
  )
}

export function applyContainerDetailLocalExportValues(
  rows: ContainerDetail[],
  localRows: ContainerDetail[],
) {
  const localRowsByGuid = new Map(
    localRows
      .filter((row) => Boolean(row.hguid))
      .map((row) => [row.hguid, row] as const),
  )

  return rows.map((row) => {
    const localRow = row.hguid ? localRowsByGuid.get(row.hguid) : undefined
    if (!localRow) return row

    // 仅覆盖页面可编辑值及其本地联动结果，其他字段继续使用刚分页拉取的服务端快照。
    return mergeContainerDetailPatch(row, {
      商品名称: localRow.商品名称,
      单件装箱数: localRow.单件装箱数,
      单件体积: localRow.单件体积,
      中包数: localRow.中包数,
      调整浮率: localRow.调整浮率,
      装柜数量: localRow.装柜数量,
      合计装柜体积: localRow.合计装柜体积,
      合计装柜金额: localRow.合计装柜金额,
      运输成本: localRow.运输成本,
      进口价格: localRow.进口价格,
      备注: localRow.备注,
    })
  })
}

export interface BuildContainerDetailQueryOptions {
  containerGuid: string
  filters: ContainerDetailColumnFilters
  selectedTags?: ContainerDetailTagFilter[]
  sortState?: ContainerDetailSortState
  pageNumber: number
  pageSize: number
  includeItems?: boolean
  includeTotal?: boolean
  includeStats?: boolean
}

function assignQueryValue<K extends keyof ContainerDetailQuery>(
  target: ContainerDetailQuery,
  key: K,
  value: ContainerDetailQuery[K],
) {
  target[key] = value
}

function assignTrimmedText<K extends keyof ContainerDetailQuery>(
  target: ContainerDetailQuery,
  key: K,
  value?: string,
) {
  const normalized = value?.trim()
  if (normalized) {
    assignQueryValue(target, key, normalized as ContainerDetailQuery[K])
  }
}

function assignNonEmptyArray<K extends keyof ContainerDetailQuery, V>(
  target: ContainerDetailQuery,
  key: K,
  value?: V[],
) {
  if (value?.length) {
    assignQueryValue(target, key, [...value] as unknown as ContainerDetailQuery[K])
  }
}

function assignNumberRange(
  target: ContainerDetailQuery,
  minKey: keyof ContainerDetailQuery,
  maxKey: keyof ContainerDetailQuery,
  range?: ContainerDetailNumberRangeFilter,
) {
  // 0 是有效筛选值，不能用 truthy 判断丢掉。
  if (range?.min != null) {
    assignQueryValue(target, minKey, range.min as ContainerDetailQuery[typeof minKey])
  }
  if (range?.max != null) {
    assignQueryValue(target, maxKey, range.max as ContainerDetailQuery[typeof maxKey])
  }
}

export function buildContainerDetailQuery({
  containerGuid,
  filters,
  selectedTags,
  sortState,
  pageNumber,
  pageSize,
  includeItems,
  includeTotal,
  includeStats,
}: BuildContainerDetailQueryOptions): ContainerDetailQuery {
  const query: ContainerDetailQuery = {
    containerGuid,
    pageNumber,
    pageSize,
  }

  // items/total/tagStats 可独立关闭；分页首屏先返回行，统计再由轻量请求补齐。
  if (includeItems != null) {
    query.includeItems = includeItems
  }
  if (includeTotal != null) {
    query.includeTotal = includeTotal
  }
  if (includeStats != null) {
    query.includeStats = includeStats
  }

  assignTrimmedText(query, 'itemNumber', filters.itemNumber)
  assignTrimmedText(query, 'barcode', filters.barcode)
  assignTrimmedText(query, 'productName', filters.productName)
  assignTrimmedText(query, 'englishName', filters.englishName)
  assignTrimmedText(query, 'remark', filters.remark)
  assignNonEmptyArray(query, 'productTypes', filters.productTypes)
  assignNonEmptyArray(query, 'newProductStates', filters.newProductStates)
  assignNonEmptyArray(query, 'matchTypes', filters.matchTypes)
  assignNonEmptyArray(query, 'warehouseStatus', filters.warehouseStatus)
  assignNonEmptyArray(
    query,
    'selectedTags',
    selectedTags?.filter((tag) => tag !== 'all'),
  )

  assignNumberRange(query, 'containerPiecesMin', 'containerPiecesMax', filters.containerPieces)
  assignNumberRange(query, 'middlePackQuantityMin', 'middlePackQuantityMax', filters.middlePackQuantity)
  assignNumberRange(query, 'containerQuantityMin', 'containerQuantityMax', filters.containerQuantity)
  assignNumberRange(query, 'packingQuantityMin', 'packingQuantityMax', filters.packingQuantity)
  assignNumberRange(query, 'unitVolumeMin', 'unitVolumeMax', filters.unitVolume)
  assignNumberRange(query, 'domesticPriceMin', 'domesticPriceMax', filters.domesticPrice)
  assignNumberRange(query, 'floatRateMin', 'floatRateMax', filters.floatRate)
  assignNumberRange(query, 'transportCostMin', 'transportCostMax', filters.transportCost)
  assignNumberRange(query, 'unitTransportCostMin', 'unitTransportCostMax', filters.unitTransportCost)
  assignNumberRange(query, 'warehouseImportPriceMin', 'warehouseImportPriceMax', filters.warehouseImportPrice)
  assignNumberRange(query, 'lastOEMPriceMin', 'lastOEMPriceMax', filters.lastOEMPrice)
  assignNumberRange(query, 'importPriceMin', 'importPriceMax', filters.importPrice)
  assignNumberRange(query, 'oemPriceMin', 'oemPriceMax', filters.oemPrice)

  if (sortState) {
    query.sortBy = sortState.field
    query.sortOrder = sortState.order
  }

  return query
}

export function mergeContainerDetailLoadedItems(
  loadedItems: ContainerDetail[],
  nextItems: ContainerDetail[],
): ContainerDetail[] {
  const merged = [...loadedItems]
  const indexByGuid = new Map<string, number>()

  merged.forEach((item, index) => {
    if (item.hguid) {
      indexByGuid.set(item.hguid, index)
    }
  })

  nextItems.forEach((item) => {
    const existingIndex = item.hguid ? indexByGuid.get(item.hguid) : undefined
    if (existingIndex == null) {
      if (item.hguid) {
        indexByGuid.set(item.hguid, merged.length)
      }
      merged.push(item)
      return
    }

    // 重复明细保留原位置，但以后端最新页数据覆盖，避免编辑后刷新显示旧值。
    merged[existingIndex] = item
  })

  return merged
}

export interface ContainerDetailRemoteQueryResetState<Key = string> {
  selectedRowKeys: Key[]
  loadedItems: ContainerDetail[]
  pageNumber: number
}

export function getContainerDetailRemoteQueryResetState<Key = string>(
  _state?: Partial<ContainerDetailRemoteQueryResetState<Key>>,
): ContainerDetailRemoteQueryResetState<Key> {
  // 远程查询条件变化后，旧选择和旧分页块都不再代表当前结果集。
  return {
    selectedRowKeys: [],
    loadedItems: [],
    pageNumber: 1,
  }
}

export function applyContainerDetailWarehouseStatusByProductCodes(
  rows: ContainerDetail[],
  productCodes: string[],
  isActive: boolean,
) {
  const productCodeSet = new Set(productCodes.map((value) => value.trim()).filter(Boolean))

  return rows.map((row) => {
    const productCode = getContainerDetailProductCode(row)
    return productCode && productCodeSet.has(productCode)
      ? { ...row, warehouseIsActive: isActive }
      : row
  })
}

export function rollbackContainerDetailWarehouseStatuses(
  rows: ContainerDetail[],
  previousStatuses: Array<{ key: string; warehouseIsActive?: boolean }>,
  getRowKey: (row: ContainerDetail) => string,
) {
  const previousStatusMap = new Map(previousStatuses.map((item) => [item.key, item.warehouseIsActive]))

  return rows.map((row) => {
    const key = getRowKey(row)
    return previousStatusMap.has(key)
      ? { ...row, warehouseIsActive: previousStatusMap.get(key) }
      : row
  })
}

export interface ContainerDetailWarehouseActionResultLike {
  success?: boolean
  isSuccess?: boolean
  failedCount?: number
  FailedCount?: number
  errors?: string[]
  Errors?: string[]
  message?: string
  Message?: string
}

export function getContainerDetailWarehouseActionFailureMessage(
  result: ContainerDetailWarehouseActionResultLike,
  fallback: string,
) {
  const failedCount = Number(result.failedCount ?? result.FailedCount ?? 0)
  const errors = result.errors ?? result.Errors ?? []
  if (result.success === false || result.isSuccess === false || failedCount > 0) {
    return result.message ?? result.Message ?? errors.join('；') ?? fallback
  }
  return undefined
}

export function buildContainerDetailTranslationUpdates(
  rows: ContainerDetail[],
  translations: Record<string, string>,
): UpdateContainerDetailRequest[] {
  const updates: UpdateContainerDetailRequest[] = []

  rows.forEach((row) => {
    const name = getContainerDetailTranslationSource(row)
    const englishName = name ? translations[name] : undefined

    if (row.hguid && isValidContainerDetailEnglishTranslation(englishName)) {
      updates.push({ hguid: row.hguid, 英文名称: englishName!.trim() })
    }
  })

  return updates
}

export function countContainerDetailInvalidTranslationResults(
  rows: ContainerDetail[],
  translations: Record<string, string>,
) {
  return rows.filter((row) => {
    const name = getContainerDetailTranslationSource(row)
    const englishName = name ? translations[name] : undefined
    return Boolean(englishName) && !isValidContainerDetailEnglishTranslation(englishName)
  }).length
}

export function buildContainerDetailEnglishNameUpdates(
  rows: ContainerDetail[],
  englishName: string,
): UpdateContainerDetailRequest[] {
  const normalizedEnglishName = englishName.trim()
  if (!isValidContainerDetailEnglishTranslation(normalizedEnglishName)) return []

  return rows
    .filter((row) => Boolean(row.hguid))
    .map((row) => ({ hguid: row.hguid, 英文名称: normalizedEnglishName }))
}

export function buildContainerDetailClearEnglishNameUpdates(
  rows: ContainerDetail[],
): UpdateContainerDetailRequest[] {
  return rows
    .filter((row) => Boolean(row.hguid))
    .map((row) => ({ hguid: row.hguid, ClearEnglishName: true }))
}

export function applyContainerDetailEnglishNameUpdates(
  rows: ContainerDetail[],
  updates: Pick<UpdateContainerDetailRequest, 'hguid' | '英文名称'>[],
): ContainerDetail[] {
  const updateMap = new Map(updates.map((item) => [item.hguid, item.英文名称]))

  return rows.map((row) => (
    updateMap.has(row.hguid)
      ? withContainerDetailEnglishName(row, updateMap.get(row.hguid))
      : row
  ))
}

function roundToDigits(value: number, digits: number) {
  const base = 10 ** digits
  return Math.round((value + Number.EPSILON) * base) / base
}

export type ContainerFreightInputMode = 'perCbm' | 'standard68'

export const STANDARD_CONTAINER_VOLUME_CBM = 68

function isValidContainerFreightAmount(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
}

export function normalizeContainerFreightInput(
  value: number | null | undefined,
  mode: ContainerFreightInputMode,
): number | undefined {
  if (!isValidContainerFreightAmount(value)) {
    return undefined
  }

  const normalizedValue = roundToDigits(value, mode === 'perCbm' ? 4 : 2)
  return Number.isFinite(normalizedValue) ? normalizedValue : undefined
}

export function isValidContainerFreightVolume(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0
}

export function calculateContainerFreight(
  inputValue: number | null | undefined,
  totalVolume: number | null | undefined,
  mode: ContainerFreightInputMode,
): number | undefined {
  if (!isValidContainerFreightAmount(inputValue) || !isValidContainerFreightVolume(totalVolume)) {
    return undefined
  }

  const freight = mode === 'perCbm'
    ? inputValue * totalVolume
    : (inputValue * totalVolume) / STANDARD_CONTAINER_VOLUME_CBM
  if (!Number.isFinite(freight)) {
    return undefined
  }

  const roundedFreight = roundToDigits(freight, 2)
  return Number.isFinite(roundedFreight) ? roundedFreight : undefined
}

export function resolveContainerFreightPreview(
  savedFreight: number | null | undefined,
  inputValue: number | null | undefined,
  totalVolume: number | null | undefined,
  mode: ContainerFreightInputMode,
  inputDirty: boolean,
): number | undefined {
  if (!inputDirty) {
    return isValidContainerFreightAmount(savedFreight) ? savedFreight : undefined
  }

  return calculateContainerFreight(inputValue, totalVolume, mode)
}

export function deriveContainerFreightInput(
  freight: number | null | undefined,
  totalVolume: number | null | undefined,
  mode: ContainerFreightInputMode,
): number | undefined {
  if (!isValidContainerFreightAmount(freight) || !isValidContainerFreightVolume(totalVolume)) {
    return undefined
  }

  const inputValue = mode === 'perCbm'
    ? freight / totalVolume
    : (freight * STANDARD_CONTAINER_VOLUME_CBM) / totalVolume
  return Number.isFinite(inputValue) ? inputValue : undefined
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

export function normalizeContainerDetailPushToHqPayload(raw: unknown, fallbackMessage?: string): PushProductsToHqResult | null {
  if (!isPlainRecord(raw)) return null

  const errors = Array.isArray(raw.errors)
    ? raw.errors.map(String)
    : []
  const successCount = Number(raw.successCount ?? raw.productsAdded ?? 0) +
    Number(raw.successCount === undefined ? raw.productsUpdated ?? 0 : 0)
  const failedCount = Number(raw.failedCount ?? raw.errorCount ?? errors.length)
  const affectedRowCount =
    Number(raw.affectedRowCount ?? 0) ||
    Number(raw.productsAdded ?? 0) +
      Number(raw.productsUpdated ?? 0) +
      Number(raw.warehouseInventoriesCreated ?? 0) +
      Number(raw.warehouseInventoriesUpdated ?? 0) +
      Number(raw.storeRetailPricesCreated ?? 0) +
      Number(raw.storeRetailPricesUpdated ?? 0) +
      Number(raw.productSetCodesCreated ?? raw.productSetCodesAdded ?? 0) +
      Number(raw.productSetCodesUpdated ?? 0) +
      Number(raw.storeMultiCodesCreated ?? 0) +
      Number(raw.storeMultiCodesUpdated ?? 0)

  return {
    ...(raw as Partial<PushProductsToHqResult>),
    successCount,
    failedCount,
    totalCount: Number(raw.totalCount ?? successCount + failedCount),
    affectedRowCount,
    errors,
    message: typeof raw.message === 'string' ? raw.message : fallbackMessage,
  }
}

export function extractPushToHqErrorResult(error: unknown): PushProductsToHqResult | null {
  if (!isPlainRecord(error) || !('payload' in error)) return null
  const payload = error.payload
  if (!isPlainRecord(payload)) return null
  const fallbackMessage = typeof payload.message === 'string'
    ? payload.message
    : error instanceof Error
      ? error.message
      : undefined
  return (
    normalizeContainerDetailPushToHqPayload(payload.data, fallbackMessage) ??
    normalizeContainerDetailPushToHqPayload(payload.details, fallbackMessage) ??
    normalizeContainerDetailPushToHqPayload(payload, fallbackMessage)
  )
}

export function calculateContainerDetailTransportCost(row: ContainerDetail, container?: Pick<ContainerMain, '运费' | '总体积'> | null) {
  const freight = container?.运费
  const totalVolume = container?.总体积
  const containerQuantity = row.装柜数量
  const unitVolume = row.单件体积 ?? row.商品信息?.单件体积
  const detailVolume = row.合计装柜体积 ?? (
    row.装柜件数 != null && unitVolume != null
      ? row.装柜件数 * unitVolume
      : undefined
  )

  if (
    freight == null ||
    freight < 0 ||
    !totalVolume ||
    totalVolume <= 0 ||
    containerQuantity == null ||
    containerQuantity <= 0 ||
    detailVolume == null ||
    detailVolume < 0
  ) {
    return row.运输成本
  }

  return roundToDigits((freight * detailVolume) / containerQuantity / totalVolume, 2)
}

export function calculateContainerDetailImportPrice(
  row: ContainerDetail,
  container: Pick<ContainerMain, '汇率'> | null | undefined,
  floatRate: number,
  transportCost: number | undefined,
) {
  const exchangeRate = container?.汇率

  if (!exchangeRate || exchangeRate <= 0 || row.国内价格 == null) {
    return row.进口价格
  }

  return roundToDigits(((row.国内价格 / exchangeRate + (transportCost ?? 0)) * floatRate * 10) / 11, 2)
}

export type ContainerDetailCostMissingField = 'exchangeRate' | 'freight' | 'totalVolume'

export function getContainerDetailCostMissingFields(container?: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null): ContainerDetailCostMissingField[] {
  const fields: ContainerDetailCostMissingField[] = []
  if (!container?.汇率 || container.汇率 <= 0) {
    fields.push('exchangeRate')
  }
  if (container?.运费 == null) {
    fields.push('freight')
  }
  if (!container?.总体积 || container.总体积 <= 0) {
    fields.push('totalVolume')
  }
  return fields
}

export function calculateContainerSetCodePurchasePrice(
  mainPurchasePrice: number | null | undefined,
  itemRetailPrice: number | null | undefined,
  totalRetailPrice: number | null | undefined,
) {
  if (
    mainPurchasePrice == null ||
    mainPurchasePrice <= 0 ||
    itemRetailPrice == null ||
    itemRetailPrice < 0 ||
    totalRetailPrice == null ||
    totalRetailPrice <= 0
  ) {
    return undefined
  }

  // 套装子项进货价按子项售价占比，从货柜明细当前主商品进口价中分摊。
  return roundToDigits((mainPurchasePrice * itemRetailPrice) / totalRetailPrice, 2)
}

export function buildContainerDetailFloatRateUpdates(
  rows: ContainerDetail[],
  container: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null | undefined,
  floatRate?: number,
): UpdateContainerDetailRequest[] {
  return rows
    .filter((row) => row.hguid)
    .map((row): UpdateContainerDetailRequest | null => {
      const nextFloatRate = floatRate ?? row.调整浮率 ?? DEFAULT_CONTAINER_DETAIL_FLOAT_RATE
      const transportCost = calculateContainerDetailTransportCost(row, container)
      const importPrice = calculateContainerDetailImportPrice(row, container, nextFloatRate, transportCost)
      const hasChange =
        row.调整浮率 !== nextFloatRate ||
        row.运输成本 !== transportCost ||
        row.进口价格 !== importPrice

      if (!hasChange) {
        return null
      }

      return {
        hguid: row.hguid,
        调整浮率: nextFloatRate,
        运输成本: transportCost,
        进口价格: importPrice,
        // 浮率导致的系统重算只更新货柜明细，避免覆盖仓库表里人工维护的进货价。
        SkipRelatedProductSync: true,
      }
    })
    .filter((update): update is UpdateContainerDetailRequest => update !== null)
}

interface ContainerDetailDetectedPrice {
  ProductCode?: string
  productCode?: string
  ItemNumber?: string
  itemNumber?: string
  SupplierCode?: string
  supplierCode?: string
  Barcode?: string
  barcode?: string
  Exists?: boolean
  exists?: boolean
  MatchType?: string
  matchType?: string
  LocalProductCode?: string
  localProductCode?: string
  DomesticProductCode?: string
  domesticProductCode?: string
  HasProductCodeConflict?: boolean
  hasProductCodeConflict?: boolean
  ConflictReason?: string
  conflictReason?: string
  ProductName?: string
  productName?: string
  name?: string
  EnglishName?: string
  englishName?: string
  nameEn?: string
  DomesticPrice?: number
  domesticPrice?: number
  WarehouseDomesticPrice?: number
  warehouseDomesticPrice?: number
  OEMPrice?: number
  oemPrice?: number
  WarehouseOEMPrice?: number
  warehouseOEMPrice?: number
  DomesticOEMPrice?: number
  domesticOEMPrice?: number
  labelPrice?: number
  WarehouseVolume?: number
  warehouseVolume?: number
  PackingQuantity?: number
  packingQuantity?: number
  packingQty?: number
  UnitVolume?: number
  unitVolume?: number
  Volume?: number
  volume?: number
}

export interface ContainerDetailDetectionItem {
  ProductCode?: string
  ItemNumber?: string
  SupplierCode?: string
}

function isMissingPrice(value?: number) {
  return value == null || value <= 0
}

function normalizeMatchKey(value?: string) {
  return value?.trim().toUpperCase()
}

function getDetectedLocalProductCode(item: ContainerDetailDetectedPrice) {
  return item.LocalProductCode ?? item.localProductCode ?? item.ProductCode ?? item.productCode
}

function getDetectedDomesticProductCode(item: ContainerDetailDetectedPrice) {
  return item.DomesticProductCode ?? item.domesticProductCode
}

function getDetectedConflictReason(item: ContainerDetailDetectedPrice) {
  return item.ConflictReason ?? item.conflictReason
}

function hasDetectedProductCodeConflict(item: ContainerDetailDetectedPrice) {
  const explicit = item.HasProductCodeConflict ?? item.hasProductCodeConflict
  if (explicit != null) return Boolean(explicit)

  const localProductCode = normalizeMatchKey(getDetectedLocalProductCode(item))
  const domesticProductCode = normalizeMatchKey(getDetectedDomesticProductCode(item))
  return Boolean(localProductCode && domesticProductCode && localProductCode !== domesticProductCode)
}

function getContainerDetailDetectionProductCode(row: ContainerDetail) {
  const productCode = getContainerDetailProductCode(row)
  return productCode
}

function buildSupplierItemMatchKey(supplierCode?: string, itemNumber?: string) {
  const normalizedSupplierCode = normalizeMatchKey(supplierCode)
  const normalizedItemNumber = normalizeMatchKey(itemNumber)
  return normalizedSupplierCode && normalizedItemNumber
    ? `${normalizedSupplierCode}:${normalizedItemNumber}`
    : undefined
}

function getContainerDetailSupplierCode(row: ContainerDetail) {
  // 供应商+货号只是候选键；优先用行上的真实供应商，历史 HB 数据缺失时才回退 200。
  return firstTrimmedValue(row.localSupplierCode, row.商品信息?.localSupplierCode) ?? '200'
}

export function buildContainerDetailDetectionItems(rows: ContainerDetail[]): ContainerDetailDetectionItem[] {
  return rows
    .map((row) => ({
      // 检测同时携带商品编码和供应商+货号，由匹配结果决定最终展示方式。
      ProductCode: getContainerDetailDetectionProductCode(row),
      ItemNumber: getContainerDetailItemNumber(row),
      SupplierCode: getContainerDetailSupplierCode(row),
    }))
    .filter((item) => item.ProductCode || item.ItemNumber)
}

function getDetectedDomesticPrice(item: ContainerDetailDetectedPrice) {
  return item.DomesticPrice ?? item.domesticPrice ?? item.WarehouseDomesticPrice ?? item.warehouseDomesticPrice
}

function getDetectedOemPrice(item: ContainerDetailDetectedPrice) {
  return item.WarehouseOEMPrice ?? item.warehouseOEMPrice ?? item.DomesticOEMPrice ?? item.domesticOEMPrice ?? item.labelPrice ?? item.oemPrice ?? item.OEMPrice
}

function getDetectedProductName(item: ContainerDetailDetectedPrice) {
  return item.productName ?? item.ProductName ?? item.name
}

function getDetectedEnglishName(item: ContainerDetailDetectedPrice) {
  return item.englishName ?? item.EnglishName ?? item.nameEn
}

function getDetectedPackingQuantity(item: ContainerDetailDetectedPrice) {
  return item.PackingQuantity ?? item.packingQuantity ?? item.packingQty
}

function getDetectedUnitVolume(item: ContainerDetailDetectedPrice) {
  return item.WarehouseVolume ?? item.warehouseVolume ?? item.volume ?? item.Volume ?? item.unitVolume ?? item.UnitVolume
}

export function calculateContainerDetailTotalAmount(row: ContainerDetail) {
  if (row.装柜数量 == null || row.国内价格 == null) return row.合计装柜金额
  // 合计装柜金额 = 装柜数量 × 国内价格，与 HQ 货柜详情和导入数据口径一致（货柜头「国内价格合计」由它汇总）；
  // 调整浮率只作用于进口价格，不能乘进这里，否则改箱规/体积后会把国内价格合计放大。
  return roundToDigits(row.装柜数量 * row.国内价格, 2)
}

export function calculateContainerDetailTotalVolume(row: ContainerDetail) {
  const unitVolume = row.单件体积 ?? row.商品信息?.单件体积
  if (row.装柜件数 == null || unitVolume == null) return row.合计装柜体积
  return roundToDigits(row.装柜件数 * unitVolume, 3)
}

function buildDetectedPriceMaps(items: ContainerDetailDetectedPrice[]) {
  const productCodeMap = new Map<string, ContainerDetailDetectedPrice>()
  const supplierItemMap = new Map<string, ContainerDetailDetectedPrice>()

  items.forEach((item) => {
    if ((item.Exists ?? item.exists) === false) return
    const productCode = normalizeMatchKey(item.productCode ?? item.ProductCode)
    const hasConflict = hasDetectedProductCodeConflict(item)
    // 后端旧版本可能不回传 SupplierCode；缺失时只兼容历史默认 200，不扩大成跨供应商匹配。
    const supplierItemKey = buildSupplierItemMatchKey(item.supplierCode ?? item.SupplierCode ?? '200', item.itemNumber ?? item.ItemNumber)
    if (productCode && !hasConflict) productCodeMap.set(productCode, item)
    if (supplierItemKey) supplierItemMap.set(supplierItemKey, item)
  })

  return { productCodeMap, supplierItemMap }
}

interface ContainerDetailDetectedMatch {
  item: ContainerDetailDetectedPrice
  matchType: ContainerDetailMatchTypeFilter
}

function resolveContainerDetailDetectedMatch(
  row: ContainerDetail,
  detectedMaps: ReturnType<typeof buildDetectedPriceMaps>,
): ContainerDetailDetectedMatch | undefined {
  const itemNumber = normalizeMatchKey(getContainerDetailItemNumber(row))
  const supplierItemKey = buildSupplierItemMatchKey(getContainerDetailSupplierCode(row), itemNumber)
  const detectionProductCode = normalizeMatchKey(getContainerDetailDetectionProductCode(row))

  // 商品编码能匹配时优先展示商品编码匹配；只有没有商品编码命中时才落到供应商+货号。
  const productCodeMatch = detectionProductCode ? detectedMaps.productCodeMap.get(detectionProductCode) : undefined
  if (productCodeMatch) {
    return {
      item: productCodeMatch,
      matchType: 'productCode',
    }
  }

  // 商品编码未命中时，200 + 货号命中才展示为供应商货号匹配。
  const supplierItemMatch = supplierItemKey ? detectedMaps.supplierItemMap.get(supplierItemKey) : undefined
  if (supplierItemMatch) {
    return {
      item: supplierItemMatch,
      matchType: 'supplierItem',
    }
  }

  return undefined
}

export function buildContainerDetailMatchStatusUpdates(
  rows: ContainerDetail[],
  detectedItems: ContainerDetailDetectedPrice[],
): UpdateContainerDetailRequest[] {
  const detectedMaps = buildDetectedPriceMaps(detectedItems)

  return rows
    .map((row): UpdateContainerDetailRequest | null => {
      if (!row.hguid) return null
      const match = resolveContainerDetailDetectedMatch(row, detectedMaps)
      if (!match) return null
      const localProductCode = getDetectedLocalProductCode(match.item)
      const domesticProductCode = getDetectedDomesticProductCode(match.item) ?? getContainerDetailProductCode(row)
      const hasProductCodeConflict = Boolean(
        localProductCode
        && domesticProductCode
        && normalizeMatchKey(localProductCode) !== normalizeMatchKey(domesticProductCode),
      ) || hasDetectedProductCodeConflict(match.item)
      const isCandidate = match.matchType === 'supplierItem' || hasProductCodeConflict

      return {
        hguid: row.hguid,
        matchType: isCandidate ? 'supplierItem' : match.matchType,
        ...(isCandidate
          ? {
              hasProductCodeConflict,
              localProductCode,
              domesticProductCode,
              conflictReason: getDetectedConflictReason(match.item),
            }
          : { 是否新商品: false }),
      }
    })
    .filter((update): update is UpdateContainerDetailRequest => update !== null)
}

export function buildContainerDetailMatchedPriceUpdates(
  rows: ContainerDetail[],
  detectedItems: ContainerDetailDetectedPrice[],
  container?: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null,
): UpdateContainerDetailRequest[] {
  return buildContainerDetailMatchedDomesticDataUpdates(rows, detectedItems, container)
}

/**
 * 匹配国内数据只回写货柜明细本身：保存前给每条更新补上 SkipRelatedProductSync。
 * 字段草稿只保留业务字段，这个标记在进出草稿时会丢失，丢失后后端会把国内数据同步改写
 * 已有商品的仓库价、主档进货价/名称和分店进货价。
 */
/**
 * 自动保存列失焦时判断值是否与聚焦时的基线相同；相同就不发请求，避免无意义写库与变更历史。
 * 任一侧是数字时按数值比较（0.05 与 "0.050" 相同），文本去首尾空白后比较，null/undefined/空串视为同一个空值。
 */
export function isContainerDetailAutoSaveValueUnchanged(baseline: unknown, next: unknown): boolean {
  const normalize = (value: unknown): string | number => {
    if (value == null) return ''
    if (typeof value === 'number') return Number.isFinite(value) ? value : ''
    return String(value).trim()
  }
  const before = normalize(baseline)
  const after = normalize(next)
  if (before === '' || after === '') return before === after
  if (typeof before === 'number' || typeof after === 'number') {
    const beforeNumber = Number(before)
    const afterNumber = Number(after)
    return Number.isFinite(beforeNumber) && Number.isFinite(afterNumber) && Math.abs(beforeNumber - afterNumber) < 1e-9
  }
  return before === after
}

// 批量预览的字段摘要里混有后端内部字段名（ProductCategoryGUID、IsActive 等）和旧称「贴牌价格」，显示前统一映射为页面列名。
const CONTAINER_DETAIL_PREVIEW_FIELD_LABELS: Record<string, { key: string, fallback: string }> = {
  ProductCategoryGUID: { key: 'containers.fields.category', fallback: '分类' },
  IsActive: { key: 'containers.fields.warehouseStatus', fallback: '仓库状态' },
  LastImportPrice: { key: 'containers.fields.lastImportPriceSnapshot', fallback: '上次进口价' },
  LastOEMPrice: { key: 'containers.fields.lastOemPriceSnapshot', fallback: '上次零售价' },
  贴牌价格: { key: 'containers.fields.oemPrice', fallback: '零售价' },
  进口价格: { key: 'containers.fields.importPrice', fallback: '进口价格' },
  调整浮率: { key: 'containers.fields.floatRate', fallback: '调整浮率' },
  运输成本: { key: 'containers.fields.transportCost', fallback: '运输成本' },
  删除明细: { key: 'containers.actions.deleteDetails', fallback: '删除明细' },
}

export function formatContainerDetailPreviewFields(
  fields: readonly string[] | undefined,
  translate: (key: string, fallback: string) => string,
): string {
  const labels = (fields ?? []).map((field) => {
    const label = CONTAINER_DETAIL_PREVIEW_FIELD_LABELS[field]
    return label ? translate(label.key, label.fallback) : field
  })
  return Array.from(new Set(labels)).join('、') || '--'
}

export function markContainerDetailUpdatesSkipRelatedProductSync<T extends { SkipRelatedProductSync?: boolean }>(
  updates: T[],
): T[] {
  return updates.map((update) => ({ ...update, SkipRelatedProductSync: true }))
}

export function buildContainerDetailMatchedDomesticDataUpdates(
  rows: ContainerDetail[],
  detectedItems: ContainerDetailDetectedPrice[],
  container?: Pick<ContainerMain, '汇率' | '运费' | '总体积'> | null,
): UpdateContainerDetailRequest[] {
  const detectedMaps = buildDetectedPriceMaps(detectedItems)

  return rows
    .map((row): UpdateContainerDetailRequest | null => {
      if (!row.hguid) return null

      const detectedMatch = resolveContainerDetailDetectedMatch(row, detectedMaps)
      if (!detectedMatch) return null
      // 货号命中只说明“可能是同一个商品”，不能用来批量写价格/名称；先人工对齐商品编码。
      if (detectedMatch.matchType !== 'productCode' || hasDetectedProductCodeConflict(detectedMatch.item)) return null

      const update: UpdateContainerDetailRequest = { hguid: row.hguid }
      const match = detectedMatch.item
      update.matchType = detectedMatch.matchType
      update.是否新商品 = false
      const domesticPrice = getDetectedDomesticPrice(match)
      const oemPrice = getDetectedOemPrice(match)
      const productName = getDetectedProductName(match)
      const englishName = getDetectedEnglishName(match)
      const packingQuantity = getDetectedPackingQuantity(match)
      const unitVolume = getDetectedUnitVolume(match)

      if (isMissingPrice(row.国内价格) && domesticPrice != null && domesticPrice > 0) {
        update.国内价格 = domesticPrice
      }
      if (isMissingPrice(row.贴牌价格) && oemPrice != null && oemPrice > 0) {
        update.贴牌价格 = oemPrice
      }
      if (productName && productName !== getContainerDetailProductName(row)) {
        update.商品名称 = productName
      }
      if (englishName && englishName !== getContainerDetailEnglishName(row)) {
        update.英文名称 = englishName
      }
      if (packingQuantity != null && packingQuantity > 0 && packingQuantity !== row.单件装箱数) {
        update.单件装箱数 = packingQuantity
        if (row.装柜件数 != null) {
          update.装柜数量 = roundToDigits(row.装柜件数 * packingQuantity, 2)
        }
      }
      if (unitVolume != null && unitVolume >= 0 && unitVolume !== row.单件体积) {
        update.单件体积 = unitVolume
      }

      const nextRow = mergeContainerDetailPatch(row, update as Partial<ContainerDetail>)
      const totalVolume = calculateContainerDetailTotalVolume(nextRow)
      if (totalVolume !== row.合计装柜体积) update.合计装柜体积 = totalVolume

      const amountRow = mergeContainerDetailPatch(row, update as Partial<ContainerDetail>)
      const totalAmount = calculateContainerDetailTotalAmount(amountRow)
      if (totalAmount !== row.合计装柜金额) update.合计装柜金额 = totalAmount

      const pricedRow = mergeContainerDetailPatch(row, update as Partial<ContainerDetail>)
      const transportCost = calculateContainerDetailTransportCost(pricedRow, container)
      const importPrice = calculateContainerDetailImportPrice(
        { ...pricedRow, 运输成本: transportCost },
        container,
        pricedRow.调整浮率 ?? DEFAULT_CONTAINER_DETAIL_FLOAT_RATE,
        transportCost,
      )
      if (transportCost !== row.运输成本) update.运输成本 = transportCost
      if (importPrice !== row.进口价格) update.进口价格 = importPrice

      return Object.keys(update).length > 1 ? update : null
    })
    .filter((update): update is UpdateContainerDetailRequest => update !== null)
}

export interface ContainerDetailHqPushSelection {
  productCodes: string[]
  items: PushProductsToHqItem[]
  skippedNewProductCount: number
  missingProductCodeCount: number
}

export function buildContainerDetailHqPushSelection(rows: ContainerDetail[]): ContainerDetailHqPushSelection {
  const productCodes: string[] = []
  const items: PushProductsToHqItem[] = []
  let skippedNewProductCount = 0
  let missingProductCodeCount = 0
  const candidateKeys = new Set<string>()

  rows.forEach((row) => {
    const isNewProduct = Boolean(row.是否新商品)
    const productCode = row.商品编码?.trim() || row.商品信息?.商品编码?.trim()
    const localSupplierCode = row.localSupplierCode?.trim() || row.商品信息?.localSupplierCode?.trim()
    const itemNumber = row.商品信息?.货号?.trim()
    const productName = getContainerDetailProductName(row)?.trim()
    const englishName = getContainerDetailEnglishName(row)?.trim()
    const barcode = row.商品信息?.条形码?.trim()
    const imageUrl = row.商品图片?.trim() || row.商品信息?.商品图片?.trim()
    const oemPrice = getContainerDetailVisibleOemPrice(row)
    if (!productCode && !(localSupplierCode && itemNumber)) {
      missingProductCodeCount += 1
      return
    }

    const candidateKey = productCode
      ? `code:${productCode.toUpperCase()}`
      : `supplier-item:${localSupplierCode!.toUpperCase()}:${itemNumber!.toUpperCase()}`
    if (candidateKeys.has(candidateKey)) {
      return
    }
    candidateKeys.add(candidateKey)

    if (productCode && !productCodes.includes(productCode)) {
      productCodes.push(productCode)
    }

    // 前端的新商品标记可能滞后；这里只提交候选信息，最终是否能写 HQ 由后端实时查询本地 Product 兜底。
    items.push({
      productCode: productCode || undefined,
      localSupplierCode,
      itemNumber,
      productName,
      englishName,
      barcode,
      imageUrl,
      domesticPrice: row.国内价格 == null ? undefined : Number(row.国内价格),
      importPrice: row.进口价格 == null ? undefined : Number(row.进口价格),
      oemPrice: oemPrice == null ? undefined : Number(oemPrice),
      isNewProduct,
    })
  })

  return {
    productCodes,
    items,
    skippedNewProductCount,
    missingProductCodeCount,
  }
}
