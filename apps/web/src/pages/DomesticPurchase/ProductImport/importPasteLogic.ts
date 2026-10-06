// 商品导入页的粘贴 / 清列纯逻辑：定位起点行、把 Excel 矩阵写入可编辑列。

import type { ProductImportItem } from './types'
import { createEmptyProduct, generateImageUrl, updateCalculatedFields } from './utils'

/** 可粘贴 / 可编辑的列，顺序即 Excel 多列粘贴时列的写入顺序，必须与表格里从左到右的可编辑列一致。 */
export const IMPORT_EDITABLE_COLUMNS = ['quantity', 'productCode', 'barcode', 'productName', 'englishName', 'domesticPrice', 'oemPrice', 'midPackQuantity', 'casePackQuantity', 'volume'] as const
export type ImportEditableColumn = (typeof IMPORT_EDITABLE_COLUMNS)[number]

export const IMPORT_NUMERIC_COLUMNS: readonly ImportEditableColumn[] = ['quantity', 'domesticPrice', 'oemPrice', 'midPackQuantity', 'casePackQuantity', 'volume']

const INTEGER_COLUMNS: readonly ImportEditableColumn[] = ['quantity', 'midPackQuantity', 'casePackQuantity']
const DECIMAL_COLUMNS: readonly ImportEditableColumn[] = ['domesticPrice', 'oemPrice', 'volume']

/** 列 key 是否是可编辑列（状态列、图片列、操作列等不是）。 */
export function toEditableColumn(columnKey: string | null | undefined): ImportEditableColumn | null {
  return (IMPORT_EDITABLE_COLUMNS as readonly string[]).includes(columnKey ?? '') ? (columnKey as ImportEditableColumn) : null
}

/**
 * 用行 key（tr 的 data-row-key）在数组里找下标。
 * 行 id 形如 row_时间戳_随机串，旧代码对它 parseInt 得到 NaN，再拿 "NaN" 去匹配 id，
 * 导致「点单元格再粘贴」永远报「无法确定行位置」；这里直接按 id 全等查找。
 */
export function findRowIndexById(products: readonly ProductImportItem[], rowKey: string | null | undefined): number {
  if (!rowKey) return -1
  return products.findIndex((row) => row.id === rowKey)
}

function cleanPastedValue(column: ImportEditableColumn, raw: string): string | number | undefined {
  const text = raw.trim()
  // Excel 粘贴里的空单元格也要覆盖目标格，才能保持整列数据和原表行号一致。
  if (!text) return IMPORT_NUMERIC_COLUMNS.includes(column) ? undefined : ''
  if (INTEGER_COLUMNS.includes(column)) return parseInt(text, 10) || undefined
  if (DECIMAL_COLUMNS.includes(column)) return parseFloat(text.replace(/[¥￥€£$₩₹,，]/g, '')) || undefined
  return text
}

/**
 * 把矩阵写入表格：从 startRowIndex 行、startColumn 列开始，超出行数自动补空行，超出列数的单元格忽略。
 * 返回新数组，不修改入参。
 */
export function applyImportPaste(
  products: readonly ProductImportItem[],
  startRowIndex: number,
  startColumn: ImportEditableColumn,
  data: readonly (readonly string[])[],
): ProductImportItem[] {
  const startColumnIndex = IMPORT_EDITABLE_COLUMNS.indexOf(startColumn)
  const next = [...products]
  const requiredRows = startRowIndex + data.length
  while (next.length < requiredRows) next.push(createEmptyProduct())

  data.forEach((rowData, rowOffset) => {
    const rowIndex = startRowIndex + rowOffset
    if (rowIndex < 0 || rowIndex >= next.length) return
    const row = { ...next[rowIndex], newProduct: { ...next[rowIndex].newProduct } }
    const editable = row.newProduct as Record<string, unknown>
    rowData.forEach((cell, columnOffset) => {
      const columnIndex = startColumnIndex + columnOffset
      if (columnIndex < 0 || columnIndex >= IMPORT_EDITABLE_COLUMNS.length) return
      const column = IMPORT_EDITABLE_COLUMNS[columnIndex]
      editable[column] = cleanPastedValue(column, cell)
    })
    if (row.newProduct.productCode) {
      row.imageUrl = generateImageUrl(row.newProduct.productCode)
      row.imageLoadStatus = 'loading'
    }
    next[rowIndex] = updateCalculatedFields(row)
  })
  return next
}

/** 清空某一可编辑列的全部数据（数值列清成 undefined，文本列清成空串）。 */
export function clearImportColumn(products: readonly ProductImportItem[], column: ImportEditableColumn): ProductImportItem[] {
  const emptyValue = IMPORT_NUMERIC_COLUMNS.includes(column) ? undefined : ''
  return products.map((row) => updateCalculatedFields({ ...row, newProduct: { ...row.newProduct, [column]: emptyValue } }))
}
