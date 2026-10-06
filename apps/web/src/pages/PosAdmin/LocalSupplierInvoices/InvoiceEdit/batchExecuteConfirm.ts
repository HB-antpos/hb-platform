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
