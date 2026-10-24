import type {
  PasteReplaceStoreOrderLinesPayload,
  StoreOrderBatchLookupItem,
  StoreOrderPasteAction,
  StoreOrderProductItem,
} from '../../../types/storeOrder'
export type { StoreOrderPasteAction } from '../../../types/storeOrder'

export type StoreOrderPastePreviewStatus = 'new' | 'existing' | 'invalidQuantity' | 'unmatched'
export type StoreOrderPastePreviewFilter = 'all' | 'importable' | 'invalid' | 'unmatched' | 'existing'

export interface StoreOrderPasteColumnMapping {
  itemNumber: number
  quantity: number
  price: number
}

export type StoreOrderPasteQuantityMode = 'direct' | 'inner'

export interface BuildPasteSubmitItemsOptions {
  quantityMode?: StoreOrderPasteQuantityMode
}

export interface ParsedStoreOrderPasteItem {
  rowIndex: number
  itemNumber: string
  quantity: number
  quantityRaw?: string
  quantityValid: boolean
  price?: number
}

export interface ExistingStoreOrderPasteLine {
  productCode: string
  quantity?: number
  allocQuantity?: number
}

export interface StoreOrderPastePreviewItem extends ParsedStoreOrderPasteItem {
  product?: StoreOrderProductItem
  valid: boolean
  status: StoreOrderPastePreviewStatus
  action: StoreOrderPasteAction
  existingQuantity?: number
  existingAllocQuantity?: number
}

export type StoreOrderPasteSubmitItem = PasteReplaceStoreOrderLinesPayload['items'][number]

function normalizeLookupKey(value: string | undefined) {
  return value?.trim().toLowerCase() ?? ''
}

function parseQuantity(
  rawQuantity: string | undefined,
  quantityColumnEnabled: boolean,
  quantityMode: StoreOrderPasteQuantityMode,
) {
  if (!quantityColumnEnabled) {
    return { quantity: 1, quantityValid: true }
  }

  const normalized = rawQuantity?.trim() ?? ''
  if (!normalized) {
    // 数量列为空时，普通写入用于清零；inner 写入仍按 1 个中包换算。
    return { quantity: quantityMode === 'inner' ? 1 : 0, quantityValid: true }
  }

  const isNonNegativeInteger = /^\d+$/.test(normalized)
  const quantity = isNonNegativeInteger ? Number.parseInt(normalized, 10) : 0

  return {
    quantity,
    quantityRaw: rawQuantity,
    quantityValid: isNonNegativeInteger,
  }
}

export function parseStoreOrderPasteRows(
  pasteData: string,
  columnMapping: StoreOrderPasteColumnMapping,
  quantityMode: StoreOrderPasteQuantityMode = 'direct',
): ParsedStoreOrderPasteItem[] {
  return pasteData
    .split(/\r?\n/)
    .map((row, index) => ({ row, index }))
    .map(({ row, index }) => {
      if (!row.trim()) {
        return null
      }

      // 不能 trim 整行，否则 Excel 前置空列会被吞掉，导致用户配置的列序号错位。
      const cols = row.split('\t').map((col) => col.trim())
      const itemNumber = cols[columnMapping.itemNumber] || cols[0] || ''
      if (!itemNumber) {
        return null
      }

      const quantityResult = parseQuantity(
        columnMapping.quantity >= 0 ? cols[columnMapping.quantity] : undefined,
        columnMapping.quantity >= 0,
        quantityMode,
      )
      const rawPrice = columnMapping.price >= 0 ? cols[columnMapping.price] : undefined
      const parsedPrice = rawPrice === undefined ? Number.NaN : Number.parseFloat(rawPrice)
      const parsedItem: ParsedStoreOrderPasteItem = {
        rowIndex: index,
        itemNumber,
        quantity: quantityResult.quantity,
        quantityValid: quantityResult.quantityValid,
        price: Number.isFinite(parsedPrice) ? parsedPrice : undefined,
      }

      if (quantityResult.quantityRaw !== undefined) {
        parsedItem.quantityRaw = quantityResult.quantityRaw
      }

      return parsedItem
    })
    .filter((item): item is ParsedStoreOrderPasteItem => Boolean(item))
}

export function createPastePreviewItems(
  parsedItems: ParsedStoreOrderPasteItem[],
  lookupResult: StoreOrderBatchLookupItem[],
  existingLines: ExistingStoreOrderPasteLine[],
): StoreOrderPastePreviewItem[] {
  const productMap = new Map<string, StoreOrderProductItem>()
  const existingMap = new Map<string, ExistingStoreOrderPasteLine>()

  lookupResult.forEach((entry) => {
    if (entry.lookupCode && entry.product) {
      productMap.set(normalizeLookupKey(entry.lookupCode), entry.product)
    }
  })

  existingLines.forEach((line) => {
    if (line.productCode) {
      existingMap.set(normalizeLookupKey(line.productCode), line)
    }
  })

  return parsedItems.map((item) => {
    const product = productMap.get(normalizeLookupKey(item.itemNumber))
    const existingLine = product?.productCode ? existingMap.get(normalizeLookupKey(product.productCode)) : undefined
    const isNewZeroQuantity = Boolean(product) && !existingLine && item.quantity === 0
    const status: StoreOrderPastePreviewStatus = !item.quantityValid || isNewZeroQuantity
      ? 'invalidQuantity'
      : product
        ? existingLine
          ? 'existing'
          : 'new'
        : 'unmatched'
    const canImportQuantity = item.quantityValid && (item.quantity > 0 || Boolean(existingLine))

    return {
      ...item,
      product,
      valid: Boolean(product) && canImportQuantity,
      status,
      // 默认覆盖，用户可在预览中批量或逐条改成追加/跳过。
      // 已是退货行（发货数为负）的商品默认跳过：Excel 粘贴一期不支持退货，覆盖会把退货数量冲成正数。
      action: Number(existingLine?.allocQuantity ?? 0) < 0 ? 'skip' : 'replace',
      existingQuantity: existingLine?.quantity,
      existingAllocQuantity: existingLine?.allocQuantity,
    }
  })
}

export function filterPastePreviewItems(
  items: StoreOrderPastePreviewItem[],
  filter: StoreOrderPastePreviewFilter,
) {
  if (filter === 'all') return items
  if (filter === 'importable') return items.filter((item) => item.valid && item.action !== 'skip')
  if (filter === 'invalid') return items.filter((item) => item.status === 'invalidQuantity')
  if (filter === 'unmatched') return items.filter((item) => item.status === 'unmatched')
  return items.filter((item) => item.status === 'existing')
}

export function setExistingPastePreviewAction(
  items: StoreOrderPastePreviewItem[],
  action: StoreOrderPasteAction,
) {
  return items.map((item) => (item.status === 'existing' && item.valid ? { ...item, action } : item))
}

function resolveInnerQuantityMultiplier(item: StoreOrderPastePreviewItem) {
  const minOrderQuantity = item.product?.minOrderQuantity
  // inner 数量按商品中包数/最小订货量换算；异常配置回退为 1，避免提交 0 或 NaN。
  return typeof minOrderQuantity === 'number' && Number.isFinite(minOrderQuantity) && minOrderQuantity > 1
    ? minOrderQuantity
    : 1
}

export function getPasteSubmitQuantity(
  item: StoreOrderPastePreviewItem,
  quantityMode: StoreOrderPasteQuantityMode = 'direct',
) {
  return quantityMode === 'inner'
    ? item.quantity * resolveInnerQuantityMultiplier(item)
    : item.quantity
}

export function buildPasteSubmitItems(
  items: StoreOrderPastePreviewItem[],
  options: BuildPasteSubmitItemsOptions = {},
): StoreOrderPasteSubmitItem[] {
  const quantityMode = options.quantityMode ?? 'direct'

  return items
    .filter((item) => item.valid && item.action !== 'skip' && item.product?.productCode)
    .map((item) => ({
      productCode: item.product!.productCode,
      quantity: getPasteSubmitQuantity(item, quantityMode),
      importPrice: item.price,
      action: item.action,
    }))
}

export function formatPastePreviewQuantity(
  item: StoreOrderPastePreviewItem,
  quantityMode: StoreOrderPasteQuantityMode = 'direct',
) {
  if (item.quantityValid) {
    return getPasteSubmitQuantity(item, quantityMode)
  }

  const rawQuantity = item.quantityRaw?.trim()
  return rawQuantity ? rawQuantity : '--'
}
