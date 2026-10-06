// 「创建批次」工作台的纯逻辑：汇总、校验、草稿脏判断、请求体与行操作。
// 全部是无副作用函数，便于单测；界面只负责展示这些函数的结果。
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchInfo, CreateBatchRequest, CreateBatchResponse } from '../../../types/domesticProductCreation'
import {
  buildCreateBatchItems,
  buildPreviewItems,
  createDraftProduct,
  createDraftSetSubItem,
  getValidSetSubItems,
  isMeaningfulSetSubItem,
  normalizeCreateCount,
} from './batchCreateRules'
import type { DraftPreviewItem, DraftProductItem, DraftSetSubItem } from './batchCreateRules'

/** 后端单批上限：商品行 + 套装（创建套数 × (1 + 有效子项)）展开后的货号总数，与 CreateBatchAsync 的 maxCreatedItems 保持一致。 */
export const MAX_CREATED_ITEMS = 10_000

/**
 * 预计生成的货号数。口径必须与后端 CreateBatchAsync 的展开量计算一致：
 * 普通商品每行 1 个；套装每行 = 创建套数 × (1 个套装本身 + 有效子项数)。
 * 子项里名称与价格都为空的占位行不会提交，因此不计入。
 */
export function countExpectedItems(products: readonly DraftProductItem[]): number {
  return products.reduce((sum, product) => {
    if (product.productType !== ProductCreationType.SET) return sum + 1
    return sum + normalizeCreateCount(product.createCount) * (1 + getValidSetSubItems(product.subItems).length)
  }, 0)
}

export interface DraftSummary {
  normalCount: number
  setCount: number
  /** 还没有任何有效子项的套装行数（提交前必须补齐）。 */
  pendingSetCount: number
  expectedItems: number
  overLimit: boolean
}

export function summarizeDraft(products: readonly DraftProductItem[]): DraftSummary {
  let normalCount = 0
  let setCount = 0
  let pendingSetCount = 0
  for (const product of products) {
    if (product.productType === ProductCreationType.SET) {
      setCount += 1
      if (getValidSetSubItems(product.subItems).length === 0) pendingSetCount += 1
    } else {
      normalCount += 1
    }
  }
  const expectedItems = countExpectedItems(products)
  return { normalCount, setCount, pendingSetCount, expectedItems, overLimit: expectedItems > MAX_CREATED_ITEMS }
}

export type DraftIssue =
  | { kind: 'missing_supplier' }
  | { kind: 'set_without_sub_item'; rowKey: string; rowIndex: number }
  | { kind: 'over_limit'; expected: number }

/**
 * 提交前的真正错误，且仅此三类：
 * 1. 没选供应商；2. 套装没有任何有效子项（名称或价格有一个即可）；3. 展开后超过后端单批上限。
 * 商品名称、零售价允许留空，业务已确认，不能在这里加别的必填校验。
 */
export function validateDraft({
  supplierCode,
  products,
}: {
  supplierCode?: string | null
  products: readonly DraftProductItem[]
}): DraftIssue[] {
  const issues: DraftIssue[] = []
  if (!supplierCode?.trim()) issues.push({ kind: 'missing_supplier' })

  products.forEach((product, index) => {
    if (product.productType === ProductCreationType.SET && getValidSetSubItems(product.subItems).length === 0) {
      issues.push({ kind: 'set_without_sub_item', rowKey: product.key, rowIndex: index + 1 })
    }
  })

  const expected = countExpectedItems(products)
  if (expected > MAX_CREATED_ITEMS) issues.push({ kind: 'over_limit', expected })
  return issues
}

/** 一行是否已经填了东西（用于判断草稿是否值得在离开前确认）。 */
export function hasProductContent(product: DraftProductItem): boolean {
  return Boolean(
    product.productName?.trim()
    || product.privateLabelPrice != null
    || product.setPrice != null
    || product.subItems?.some(isMeaningfulSetSubItem),
  )
}

/**
 * 草稿是否有「离开会丢失」的内容：选了供应商/前缀、多于一行、出现了套装行或任一行有内容。
 * 工作台刚打开时只有 1 行空白普通商品，这种状态返回不需要确认。
 */
export function isBatchDraftDirty({
  supplierCode,
  prefixCode,
  products,
}: {
  supplierCode?: string | null
  prefixCode?: string | null
  products: readonly DraftProductItem[]
}): boolean {
  if (supplierCode?.trim() || prefixCode?.trim()) return true
  if (products.length > 1) return true
  return products.some((product) => product.productType === ProductCreationType.SET || hasProductContent(product))
}

/**
 * 「预览货号」的示意行。真实货号/条码由后端在提交时分配，这里只是 前缀 + 4 位序号 的推演；
 * 没选前缀时后端按供应商规则分配，推演结果没有意义，直接留空交给界面显示说明文字。
 */
export function buildPreviewRows(products: readonly DraftProductItem[], prefixCode?: string | null): DraftPreviewItem[] {
  const prefix = prefixCode?.trim() ?? ''
  return buildPreviewItems([...products], prefix).map((row) => (prefix ? row : { ...row, itemNumber: '' }))
}

/** 提交请求体。prefixName 与 prefixCode 同值，沿用旧实现与后端契约。 */
export function buildSubmitRequest({
  supplierCode,
  prefixCode,
  products,
}: {
  supplierCode: string
  prefixCode?: string | null
  products: readonly DraftProductItem[]
}): CreateBatchRequest {
  const prefix = prefixCode?.trim() || undefined
  return {
    supplierCode,
    prefixCode: prefix,
    prefixName: prefix,
    items: buildCreateBatchItems([...products]),
  }
}

/** 提交成功后拼出批次摘要，供列表页直接打开明细抽屉（真实创建人/时间以明细接口为准）。 */
export function buildCreatedBatchInfo({
  response,
  supplierCode,
  supplierName,
  prefixCode,
  now = new Date(),
}: {
  response: CreateBatchResponse
  supplierCode: string
  supplierName?: string
  prefixCode?: string | null
  now?: Date
}): BatchInfo {
  return {
    batchNumber: response.batchNumber,
    supplierCode,
    supplierName: supplierName || supplierCode,
    prefixCode: prefixCode?.trim() || undefined,
    normalCount: response.normalProductCount,
    setCount: response.setProductCount,
    totalCount: response.totalCreated,
    createdAt: now.toISOString(),
  }
}

export type BatchRenameMode = 'replace' | 'prefix' | 'suffix'

/** 批量命名：替换 / 加前缀 / 加后缀，作用于所有行的商品名称（套装行本身的名称也算，不含子项）。 */
export function applyBatchRename(
  products: readonly DraftProductItem[],
  mode: BatchRenameMode,
  value: string,
): DraftProductItem[] {
  return products.map((product) => {
    const current = product.productName ?? ''
    const next = mode === 'replace' ? value : mode === 'prefix' ? `${value}${current}` : `${current}${value}`
    return { ...product, productName: next }
  })
}

// ---- 行与子项的不可变更新（套装的 setQuantity 始终与子项总数同步，沿用旧实现） ----

export function updateProductField<K extends keyof DraftProductItem>(
  products: readonly DraftProductItem[],
  key: string,
  field: K,
  value: DraftProductItem[K],
): DraftProductItem[] {
  return products.map((product) => (product.key === key ? { ...product, [field]: value } : product))
}

export function removeProduct(products: readonly DraftProductItem[], key: string): DraftProductItem[] {
  // 至少保留一行：与旧实现一致，最后一行不可删。
  if (products.length <= 1) return [...products]
  return products.filter((product) => product.key !== key)
}

export function addSubItem(
  products: readonly DraftProductItem[],
  setKey: string,
  subItem: DraftSetSubItem = createDraftSetSubItem(),
): DraftProductItem[] {
  return products.map((product) => {
    if (product.key !== setKey) return product
    const subItems = [...(product.subItems || []), subItem]
    return { ...product, subItems, setQuantity: subItems.length }
  })
}

export function removeSubItem(products: readonly DraftProductItem[], setKey: string, subKey: string): DraftProductItem[] {
  return products.map((product) => {
    if (product.key !== setKey) return product
    const subItems = (product.subItems || []).filter((subItem) => subItem.key !== subKey)
    return { ...product, subItems, setQuantity: subItems.length }
  })
}

export function updateSubItemField<K extends keyof DraftSetSubItem>(
  products: readonly DraftProductItem[],
  setKey: string,
  subKey: string,
  field: K,
  value: DraftSetSubItem[K],
): DraftProductItem[] {
  return products.map((product) => (
    product.key === setKey
      ? {
        ...product,
        subItems: (product.subItems || []).map((subItem) => (subItem.key === subKey ? { ...subItem, [field]: value } : subItem)),
      }
      : product
  ))
}

/** 工作台初始状态：1 行空白普通商品。 */
export function createInitialProducts(): DraftProductItem[] {
  return [createDraftProduct(ProductCreationType.NORMAL, 0)]
}
