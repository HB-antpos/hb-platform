import type { Key } from 'react'
import type {
  BatchExecuteActionsResult,
  BatchExecuteExpectedAction,
  BatchExecuteNewProductProductTypeSelection,
  LocalSupplierInvoiceItemDto,
} from '../../../../types/localSupplierInvoice'
import { DetailAction } from '../../../../types/localSupplierInvoice'
import { RequestError } from '../../../../utils/request'

export interface BatchExecuteActionCounts {
  selectedCount: number
  createProductCount: number
  createProductWithAdditionalBarcodesCount: number
}

export interface BatchExecuteConfirmLabels {
  title: string
  content: string
  createProductNotice: string
  okText: string
  cancelText: string
}

export interface BatchExecuteConfirmText {
  title: string
  content: string
  okText: string
  cancelText: string
}

export interface BatchExecuteSnapshot {
  selectedCount: number
  detailGuids: string[]
  expectedActions: BatchExecuteExpectedAction[]
  confirmedCreateProductCount: number
  newProductProductTypeSelections: BatchExecuteNewProductProductTypeSelection[]
  confirmedAt?: string
}

export interface NewProductWithAdditionalBarcodesRow {
  detailGuid: string
  itemNumber?: string
  barcode?: string
  productName?: string
  additionalBarcodeCount: number
}

export interface BatchExecuteErrorFeedback {
  message: string
  details: string[]
  failure?: BatchExecuteActionsResult
}

function getCurrentDetailAction(
  detail: Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType'>,
  rowActions: Record<string, number>,
) {
  return rowActions[detail.detailGUID] ?? detail.activityType ?? DetailAction.None
}

function renderTemplate(template: string, values: Record<string, number | string>) {
  return Object.entries(values).reduce(
    (result, [key, value]) => result.replace(new RegExp(`{{${key}}}`, 'g'), String(value)),
    template,
  )
}

function normalizeBatchExecuteFailure(error: unknown): BatchExecuteActionsResult | undefined {
  if (!(error instanceof RequestError)) return undefined

  const payload = error.payload as { data?: unknown; details?: unknown } | undefined
  const candidate = (payload?.details ?? payload?.data) as Partial<BatchExecuteActionsResult> | undefined
  if (!candidate || typeof candidate !== 'object') return undefined

  return {
    createdProducts: Number(candidate.createdProducts ?? 0),
    updatedPurchasePrices: Number(candidate.updatedPurchasePrices ?? 0),
    updatedStorePrices: Number(candidate.updatedStorePrices ?? 0),
    updatedItemNumbers: Number(candidate.updatedItemNumbers ?? 0),
    addedMultiCodes: Number(candidate.addedMultiCodes ?? 0),
    skipped: Number(candidate.skipped ?? 0),
    failed: Number(candidate.failed ?? 0),
    errors: Array.isArray(candidate.errors) ? candidate.errors.map(String) : [],
  }
}

export function countSelectedBatchExecuteActions(
  selectedRowKeys: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType'> & Partial<Pick<LocalSupplierInvoiceItemDto, 'additionalBarcodes'>>>,
  rowActions: Record<string, number>,
): BatchExecuteActionCounts {
  const selectedKeys = new Set(selectedRowKeys.map(String))
  const selectedDetails = details.filter((item) => selectedKeys.has(item.detailGUID))
  const createDetails = selectedDetails.filter((item) => {
    // 页面内存中的 rowActions 代表用户刚刚修改但可能尚未重新加载的操作类型。
    const currentAction = getCurrentDetailAction(item, rowActions)
    return currentAction === DetailAction.CreateProduct
  })

  return {
    selectedCount: selectedKeys.size,
    createProductCount: createDetails.length,
    createProductWithAdditionalBarcodesCount: createDetails.filter((item) => (item.additionalBarcodes?.length ?? 0) > 0).length,
  }
}

/**
 * 把明细分成「新建商品」与其余操作两组，保持传入顺序。
 * 两条流程分开提交：「新建商品」单独确认（可同时更新 HQ），「执行操作」只执行改进货价、改货号、加多码等其余操作。
 */
export function splitCreateProductDetailGuids(
  detailGuids: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType'>>,
  rowActions: Record<string, number>,
): { createGuids: string[]; otherGuids: string[] } {
  const detailMap = new Map(details.map((item) => [item.detailGUID, item]))
  const createGuids: string[] = []
  const otherGuids: string[] = []
  for (const key of detailGuids) {
    const guid = String(key)
    const detail = detailMap.get(guid)
    const action = detail ? getCurrentDetailAction(detail, rowActions) : rowActions[guid]
    if (action === DetailAction.CreateProduct) {
      createGuids.push(guid)
    } else {
      otherGuids.push(guid)
    }
  }
  return { createGuids, otherGuids }
}

export interface PurchasePriceDirectionSplit {
  /** 操作为「更新进货价」且本单进货价高于上次进货价的行。 */
  upGuids: string[]
  /** 操作为「更新进货价」且本单进货价低于上次进货价的行。 */
  downGuids: string[]
  /** 其余行：改货号、加多码，以及进货价未变或没有上次进货价可比的「更新进货价」。 */
  otherGuids: string[]
}

/**
 * 「执行操作」里按进货价涨跌分组，让涨价、降价可以分开执行。
 * 只有「更新进货价」会写进货价（改货号、加多码不动价格），所以只给这一类按方向分组；
 * 涨跌口径与明细工作台的「涨价 / 降价」快捷筛选一致：上次进货价 > 0 才可比较。
 */
export function splitPurchasePriceDirectionGuids(
  detailGuids: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType' | 'purchasePrice' | 'lastPurchasePrice'>>,
  rowActions: Record<string, number>,
): PurchasePriceDirectionSplit {
  const detailMap = new Map(details.map((item) => [item.detailGUID, item]))
  const split: PurchasePriceDirectionSplit = { upGuids: [], downGuids: [], otherGuids: [] }
  for (const key of detailGuids) {
    const guid = String(key)
    const detail = detailMap.get(guid)
    const action = detail ? getCurrentDetailAction(detail, rowActions) : rowActions[guid]
    const last = detail?.lastPurchasePrice
    const current = detail?.purchasePrice
    const comparable = action === DetailAction.UpdatePurchasePrice
      && typeof last === 'number' && last > 0
      && typeof current === 'number'
    if (comparable && current > last) split.upGuids.push(guid)
    else if (comparable && current < last) split.downGuids.push(guid)
    else split.otherGuids.push(guid)
  }
  return split
}

/** 按用户勾选合并要执行的行，保持原始顺序；未勾选的涨价 / 降价行留作待执行。 */
export function pickPurchasePriceDirectionGuids(
  detailGuids: Key[],
  split: PurchasePriceDirectionSplit,
  choice: { includeUp: boolean; includeDown: boolean },
): string[] {
  const excluded = new Set<string>([
    ...(choice.includeUp ? [] : split.upGuids),
    ...(choice.includeDown ? [] : split.downGuids),
  ])
  return detailGuids.map(String).filter((guid) => !excluded.has(guid))
}

/** 进货价较上次涨跌绝对值超过该比例（严格大于）时，「更新进货价」必须二次确认；与后端 LocalSupplierInvoicesPurchasePriceChangeGuard 同口径。 */
export const PRICE_CHANGE_CONFIRM_RATIO = 0.4

/** 后端因未带二次确认标志而拒绝时返回的错误码。 */
export const PRICE_CHANGE_CONFIRM_REQUIRED_CODE = 'PRICE_CHANGE_CONFIRM_REQUIRED'

/** 涨跌幅（本次 / 上次 - 1）；上次进货价为空或 ≤ 0（新商品）、本次价缺失时无可比价，返回 null。 */
export function getPurchasePriceChangeRatio(
  lastPurchasePrice: number | null | undefined,
  purchasePrice: number | null | undefined,
): number | null {
  if (typeof lastPurchasePrice !== 'number' || !(lastPurchasePrice > 0)) return null
  if (typeof purchasePrice !== 'number' || !Number.isFinite(purchasePrice)) return null
  return purchasePrice / lastPurchasePrice - 1
}

export interface LargePriceChangeRow {
  detailGuid: string
  itemNumber?: string
  productName?: string
  lastPurchasePrice: number
  purchasePrice: number
  /** 涨跌幅，0.5 表示 +50%，负数为降价。 */
  ratio: number
}

/**
 * 找出本次将执行「更新进货价」且涨跌幅超限的行（含降价）。
 * 新进货价 ≤ 0 的行后端会跳过不写入，不需要确认；改货号、加多码不动价格，也不看。
 */
export function findLargePriceChangeRows(
  detailGuids: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType' | 'purchasePrice' | 'lastPurchasePrice' | 'itemNumber' | 'productName'>>,
  rowActions: Record<string, number>,
): LargePriceChangeRow[] {
  const detailMap = new Map(details.map((item) => [item.detailGUID, item]))
  const rows: LargePriceChangeRow[] = []
  for (const key of detailGuids) {
    const detail = detailMap.get(String(key))
    if (!detail || getCurrentDetailAction(detail, rowActions) !== DetailAction.UpdatePurchasePrice) continue
    if (!(typeof detail.purchasePrice === 'number' && detail.purchasePrice > 0)) continue
    const ratio = getPurchasePriceChangeRatio(detail.lastPurchasePrice, detail.purchasePrice)
    if (ratio === null || Math.abs(ratio) <= PRICE_CHANGE_CONFIRM_RATIO) continue
    rows.push({
      detailGuid: detail.detailGUID,
      itemNumber: detail.itemNumber,
      productName: detail.productName,
      lastPurchasePrice: detail.lastPurchasePrice as number,
      purchasePrice: detail.purchasePrice,
      ratio,
    })
  }
  return rows
}

/** 后端是否因「涨跌幅超限未二次确认」拒绝了本次批量执行（前端明细过期时的兜底）。 */
export function isPriceChangeConfirmRequiredError(error: unknown): boolean {
  if (!(error instanceof RequestError)) return false
  return (error.payload as { code?: unknown } | undefined)?.code === PRICE_CHANGE_CONFIRM_REQUIRED_CODE
}

export function getNewProductWithAdditionalBarcodesRows(
  selectedRowKeys: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType' | 'additionalBarcodes' | 'itemNumber' | 'barcode' | 'productName'>>,
  rowActions: Record<string, number>,
): NewProductWithAdditionalBarcodesRow[] {
  const selectedKeys = new Set(selectedRowKeys.map(String))
  return details
    .filter((item) => selectedKeys.has(item.detailGUID))
    .filter((item) => getCurrentDetailAction(item, rowActions) === DetailAction.CreateProduct)
    .filter((item) => (item.additionalBarcodes?.length ?? 0) > 0)
    .map((item) => ({
      detailGuid: item.detailGUID,
      itemNumber: item.itemNumber,
      barcode: item.barcode,
      productName: item.productName,
      additionalBarcodeCount: item.additionalBarcodes?.length ?? 0,
    }))
}

export function buildBatchExecuteSnapshot({
  selectedRowKeys,
  details,
  rowActions,
  newProductProductTypeSelections,
  confirmedAt,
}: {
  selectedRowKeys: Key[]
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType'>>
  rowActions: Record<string, number>
  newProductProductTypeSelections?: BatchExecuteNewProductProductTypeSelection[]
  confirmedAt?: string
}): BatchExecuteSnapshot {
  const detailMap = new Map(details.map((item) => [item.detailGUID, item]))
  const detailGuids = selectedRowKeys.map(String)
  const expectedActions: BatchExecuteExpectedAction[] = detailGuids.flatMap((detailGuid) => {
    const detail = detailMap.get(detailGuid)
    if (!detail) {
      return []
    }

    const action = getCurrentDetailAction(detail, rowActions)
    return [{
      detailGuid,
      action,
      // 这里保留明细原始 activityType，方便后端按确认当刻做契约校验。
      activityType: detail.activityType ?? action,
    }]
  })

  return {
    selectedCount: detailGuids.length,
    detailGuids: [...detailGuids],
    expectedActions,
    confirmedCreateProductCount: expectedActions.filter((item) => item.action === DetailAction.CreateProduct).length,
    newProductProductTypeSelections: newProductProductTypeSelections?.map((item) => ({ ...item })) ?? [],
    confirmedAt,
  }
}

export function constrainSelectedRowKeysToVisibleDetails(
  selectedRowKeys: Key[],
  visibleDetails: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID'>>,
): Key[] {
  const visibleKeys = new Set(visibleDetails.map((item) => item.detailGUID))
  const nextSelectedRowKeys = selectedRowKeys.filter((key) => visibleKeys.has(String(key)))

  if (nextSelectedRowKeys.length === selectedRowKeys.length) {
    return selectedRowKeys
  }

  return nextSelectedRowKeys
}

export function buildBatchExecuteConfirmText({
  selectedCount,
  createProductCount,
  labels,
}: Pick<BatchExecuteActionCounts, 'selectedCount' | 'createProductCount'> & { labels: BatchExecuteConfirmLabels }): BatchExecuteConfirmText {
  const lines = [
    renderTemplate(labels.content, { count: selectedCount }),
  ]

  if (createProductCount > 0) {
    lines.push(renderTemplate(labels.createProductNotice, { count: createProductCount }))
  }

  return {
    title: labels.title,
    content: lines.join('\n'),
    okText: labels.okText,
    cancelText: labels.cancelText,
  }
}

export function getBatchExecuteErrorFeedback(error: unknown, fallbackMessage: string): BatchExecuteErrorFeedback {
  const failure = normalizeBatchExecuteFailure(error)

  return {
    message: error instanceof Error ? error.message : fallbackMessage,
    details: failure?.errors ?? [],
    failure,
  }
}

/** 选中行里操作为「更新进货价」的行数；为 0 时确认框不显示分店选择。 */
export function countPurchasePriceUpdateRows(
  detailGuids: Key[],
  details: Array<Pick<LocalSupplierInvoiceItemDto, 'detailGUID' | 'activityType'>>,
  rowActions: Record<string, number>,
): number {
  const detailMap = new Map(details.map((item) => [item.detailGUID, item]))
  return detailGuids.filter((key) => {
    const guid = String(key)
    const detail = detailMap.get(guid)
    const action = detail ? getCurrentDetailAction(detail, rowActions) : rowActions[guid]
    return action === DetailAction.UpdatePurchasePrice
  }).length
}

export interface StoreScopeOption {
  value: string
  label: string
}

/** 可额外勾选的分店：去掉本单分店（始终执行、不可取消）与重复项，保持原顺序。 */
export function buildExtraStoreOptions(
  options: StoreScopeOption[],
  currentStoreCode?: string | null,
): StoreScopeOption[] {
  const current = currentStoreCode?.trim().toUpperCase()
  const seen = new Set<string>()
  return options.filter((option) => {
    const code = option.value.trim().toUpperCase()
    if (!code || code === current || seen.has(code)) return false
    seen.add(code)
    return true
  })
}

/** 「全选」复选框状态：全部勾选为 checked，部分勾选为 indeterminate；没有可选分店时不可用。 */
export function getStoreSelectAllState(selectedCodes: string[], allCodes: string[]) {
  const selected = new Set(selectedCodes)
  const selectedCount = allCodes.filter((code) => selected.has(code)).length
  return {
    checked: allCodes.length > 0 && selectedCount === allCodes.length,
    indeterminate: selectedCount > 0 && selectedCount < allCodes.length,
    disabled: allCodes.length === 0,
  }
}
