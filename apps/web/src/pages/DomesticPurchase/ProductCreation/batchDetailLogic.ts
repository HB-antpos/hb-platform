// 批次明细抽屉的纯逻辑：页签筛选与行序、价格改动、复制文本。
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchProductItem, UpdatePriceItem } from '../../../types/domesticProductCreation'

export type BatchDetailTab = 'all' | 'normal' | 'set'

/**
 * 已编辑的价格草稿：键是明细行主键 itemNumber。
 * 值为 number 表示用户输入了新价格；null 表示输入框被清空（只存在于输入过程中，失焦即还原，见 settleEditedPrice）。
 */
export type EditedPrices = Record<string, number | null>

export interface DetailTabCounts {
  all: number
  normal: number
  set: number
}

export function getDetailTabCounts(items: readonly BatchProductItem[]): DetailTabCounts {
  return {
    all: items.length,
    normal: items.filter((item) => item.productType === ProductCreationType.NORMAL).length,
    // 「套装」页签只数套装本身（子项显示在各自套装下面，不重复计数）。
    set: items.filter((item) => item.productType === ProductCreationType.SET).length,
  }
}

const byHbProductNo = (left: BatchProductItem, right: BatchProductItem) => left.hbProductNo.localeCompare(right.hbProductNo)

/**
 * 明细表的行：按货号排序，套装子项紧跟在各自的套装后面（靠 parentItemNumber 对应套装的货号）。
 * 找不到父级的子项排在最后，避免被悄悄丢弃。「普通」页签只含普通商品，「套装」页签含套装及其子项。
 */
export function buildDetailRows(items: readonly BatchProductItem[], tab: BatchDetailTab): BatchProductItem[] {
  const subItems = items.filter((item) => item.productType === ProductCreationType.SET_SUB_ITEM)
  const topLevel = items
    .filter((item) => item.productType !== ProductCreationType.SET_SUB_ITEM)
    .filter((item) => {
      if (tab === 'normal') return item.productType === ProductCreationType.NORMAL
      if (tab === 'set') return item.productType === ProductCreationType.SET
      return true
    })
    .sort(byHbProductNo)

  const childrenByParent = new Map<string, BatchProductItem[]>()
  for (const subItem of subItems) {
    const parent = (subItem.parentItemNumber || '').trim()
    childrenByParent.set(parent, [...(childrenByParent.get(parent) || []), subItem])
  }

  const rows: BatchProductItem[] = []
  const placedChildren = new Set<BatchProductItem>()
  for (const item of topLevel) {
    rows.push(item)
    if (item.productType === ProductCreationType.SET) {
      const children = [...(childrenByParent.get(item.hbProductNo.trim()) || [])].sort(byHbProductNo)
      children.forEach((child) => placedChildren.add(child))
      rows.push(...children)
    }
  }

  if (tab !== 'normal') {
    rows.push(...subItems.filter((subItem) => !placedChildren.has(subItem)).sort(byHbProductNo))
  }
  return rows
}

const roundPrice = (value: number) => Math.round((value + Number.EPSILON) * 100) / 100

/** 价格相等判断统一取两位小数，避免 6.99 与 6.990000001 被当成改动。 */
export function isSamePrice(left?: number | null, right?: number | null): boolean {
  if (left == null || right == null) return left == null && right == null
  return roundPrice(left) === roundPrice(right)
}

/** 输入过程中写入草稿（允许 null = 暂时清空）。 */
export function setEditedPrice(edited: EditedPrices, itemNumber: string, value: number | null): EditedPrices {
  return { ...edited, [itemNumber]: value }
}

/**
 * 输入框失焦时结算：清空或与原价相同的草稿直接移除，输入框回到原价。
 * 「清空 = 不改这一行」，绝不会把空值当成 0 提交（旧实现 value || 0 会把价格写成 0）。
 */
export function settleEditedPrice(
  edited: EditedPrices,
  items: readonly BatchProductItem[],
  itemNumber: string,
): EditedPrices {
  if (!(itemNumber in edited)) return edited
  const value = edited[itemNumber]
  const original = items.find((item) => item.itemNumber === itemNumber)?.privateLabelPrice
  if (value == null || isSamePrice(value, original)) {
    const next = { ...edited }
    delete next[itemNumber]
    return next
  }
  return edited
}

/** 只提交真正改过的价格：值为数字且与服务端原价不同；清空的行与未动的行都不在其中。 */
export function computeChangedPrices(items: readonly BatchProductItem[], edited: EditedPrices): UpdatePriceItem[] {
  const changed: UpdatePriceItem[] = []
  for (const item of items) {
    if (!(item.itemNumber in edited)) continue
    const value = edited[item.itemNumber]
    if (value == null || Number.isNaN(value)) continue
    if (isSamePrice(value, item.privateLabelPrice)) continue
    changed.push({ itemNumber: item.itemNumber, privateLabelPrice: roundPrice(value) })
  }
  return changed
}

/** 表格里该行应显示的价格：有草稿显示草稿，否则显示服务端价格。 */
export function getDisplayedPrice(item: BatchProductItem, edited: EditedPrices): number | null {
  if (item.itemNumber in edited) return edited[item.itemNumber]
  return item.privateLabelPrice ?? null
}

/** 该行价格是否处于「已改动待保存」状态（用于浅黄底）。 */
export function isPriceChanged(item: BatchProductItem, edited: EditedPrices): boolean {
  if (!(item.itemNumber in edited)) return false
  const value = edited[item.itemNumber]
  return value != null && !isSamePrice(value, item.privateLabelPrice)
}

/**
 * 「复制货号 + 条码」：制表符分列，粘贴到 Excel 会自动分成两列；第一行是表头。
 * 旧实现用空格拼接，货号/条码本身带空格或粘贴时无法分列。
 */
export function formatCopyText(
  rows: readonly BatchProductItem[],
  headers: { itemNumber: string; barcode: string },
): string {
  const lines = rows
    .filter((row) => row.hbProductNo || row.barcode)
    .map((row) => `${row.hbProductNo}\t${row.barcode}`)
  if (lines.length === 0) return ''
  return [`${headers.itemNumber}\t${headers.barcode}`, ...lines].join('\n')
}
