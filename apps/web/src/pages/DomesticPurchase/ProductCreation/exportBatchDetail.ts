import type { TFunction } from 'i18next'
import { ProductCreationType } from '../../../types/domesticProductCreation'
import type { BatchDetail, BatchProductItem } from '../../../types/domesticProductCreation'
import { generateBarcodeImages } from '../../../utils/barcode'

interface ExportBatchDetailOptions {
  batchNumber: string
  t: TFunction
}

interface ExportRow {
  type: string
  parentItemNumber: string
  itemNumber: string
  barcode: string
  productName: string
  privateLabelPrice: number | string
  setQuantity: number | string
  setPrice: number | string
}

/** 导出列顺序（也是批次明细抽屉列顺序的延伸）。 */
export const EXPORT_COLUMN_KEYS = [
  'itemNumber',
  'barcode',
  'productName',
  'type',
  'privateLabelPrice',
  'parentItemNumber',
  'setQuantity',
  'setPrice',
  'barcodeImage',
] as const

type ExportColumnKey = (typeof EXPORT_COLUMN_KEYS)[number]

export function getExportableBatchItems(items: BatchProductItem[]) {
  const normalItems = items
    .filter((item) => item.productType === ProductCreationType.NORMAL)
    .sort((a, b) => a.hbProductNo.localeCompare(b.hbProductNo))
  const setItems = items
    .filter((item) => item.productType === ProductCreationType.SET)
    .sort((a, b) => a.hbProductNo.localeCompare(b.hbProductNo))
  const subItemsByParent = items
    .filter((item) => item.productType === ProductCreationType.SET_SUB_ITEM)
    .reduce<Record<string, BatchProductItem[]>>((groups, item) => {
      const parentItemNumber = (item.parentItemNumber || '').trim()
      if (!parentItemNumber) return groups
      groups[parentItemNumber] = groups[parentItemNumber] || []
      groups[parentItemNumber].push(item)
      return groups
    }, {})
  const groupedSubItemIds = new Set<string>()

  const groupedSetItems = setItems.flatMap((setItem) => {
    const children = (subItemsByParent[setItem.hbProductNo.trim()] || []).sort((a, b) =>
      a.hbProductNo.localeCompare(b.hbProductNo),
    )
    children.forEach((child) => groupedSubItemIds.add(child.itemNumber || child.hbProductNo))
    return [setItem, ...children]
  })
  const unmatchedSubItems = items
    .filter((item) => item.productType === ProductCreationType.SET_SUB_ITEM)
    .filter((item) => !groupedSubItemIds.has(item.itemNumber || item.hbProductNo))
    .sort(
      (a, b) =>
        (a.parentItemNumber || '').localeCompare(b.parentItemNumber || '') || a.hbProductNo.localeCompare(b.hbProductNo),
    )

  return [...groupedSetItems, ...unmatchedSubItems, ...normalItems]
}

export function toExportRows(items: BatchProductItem[], t?: TFunction): ExportRow[] {
  const typeMap: Record<ProductCreationType, string> = {
    [ProductCreationType.NORMAL]: t?.('productCreation.normal', '普通') ?? '普通',
    [ProductCreationType.SET]: t?.('productCreation.set', '套装') ?? '套装',
    [ProductCreationType.SET_SUB_ITEM]: t?.('productCreation.setSubItem', '套装子项') ?? '套装子项',
  }
  return getExportableBatchItems(items).map((item) => ({
    type: typeMap[item.productType] || String(item.productType),
    parentItemNumber: item.productType === ProductCreationType.SET_SUB_ITEM ? item.parentItemNumber || '' : '',
    itemNumber: item.hbProductNo,
    barcode: item.barcode,
    productName: item.productName,
    privateLabelPrice: item.privateLabelPrice ?? '',
    setQuantity: item.setQuantity ?? '',
    setPrice: item.setPrice ?? '',
  }))
}

export async function exportProductCreationBatchToExcel(
  detail: BatchDetail,
  { batchNumber, t }: ExportBatchDetailOptions,
) {
  const { default: ExcelJS } = await import('exceljs')
  const workbook = new ExcelJS.Workbook()
  const worksheet = workbook.addWorksheet(t('productCreation.batchDetail', '批次明细'))
  // 列顺序以 EXPORT_COLUMN_KEYS 为唯一来源：前 5 列与批次明细抽屉的列（货号/条码/名称/类型/零售价）顺序一致，
  // 其余补充列（父套装货号、套装数量/价格、条码图片）排在后面，界面所见与导出文件不再错位。
  const columnDefinitions: Record<ExportColumnKey, { header: string; width: number }> = {
    itemNumber: { header: t('productImport.hbProductNoCol', '货号'), width: 20 },
    barcode: { header: t('domesticProducts.barcode', '条码'), width: 18 },
    productName: { header: t('domesticProducts.productName', '商品名称'), width: 30 },
    type: { header: t('productCreation.type', '类型'), width: 12 },
    privateLabelPrice: { header: t('productCreation.privateLabelPrice', '零售价'), width: 12 },
    parentItemNumber: { header: t('productCreation.parentSetItemNumber', '父套装货号'), width: 20 },
    setQuantity: { header: t('productCreation.setQuantity', '套装数量'), width: 10 },
    setPrice: { header: t('productCreation.setPrice', '套装价格'), width: 12 },
    barcodeImage: { header: t('productCreation.barcodeImage', '条码图片'), width: 25 },
  }
  worksheet.columns = EXPORT_COLUMN_KEYS.map((key) => ({ header: columnDefinitions[key].header, key, width: columnDefinitions[key].width }))
  const barcodeImageColumnIndex = EXPORT_COLUMN_KEYS.indexOf('barcodeImage')

  const headerRow = worksheet.getRow(1)
  headerRow.height = 25
  headerRow.eachCell((cell) => {
    cell.font = { bold: true, color: { argb: 'FFFFFFFF' } }
    cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FF4472C4' } }
    cell.alignment = { horizontal: 'center', vertical: 'middle' }
  })

  const rows = toExportRows(detail.items, t)
  const barcodes = rows.map((item) => item.barcode).filter(Boolean)
  const barcodeMap = await generateBarcodeImages(barcodes, { width: 1, height: 40, displayValue: true })

  rows.forEach((item, index) => {
    const currentRow = worksheet.getRow(index + 2)
    // 条码图片列只放图片，单元格留空；其余按 EXPORT_COLUMN_KEYS 顺序取值。
    currentRow.values = EXPORT_COLUMN_KEYS.map((key) => (key === 'barcodeImage' ? '' : item[key]))
    currentRow.height = 50
    if (index % 2 === 0) {
      currentRow.eachCell((cell) => {
        cell.fill = { type: 'pattern', pattern: 'solid', fgColor: { argb: 'FFF9F9F9' } }
      })
    }
    if (item.barcode) {
      const barcodeData = barcodeMap.get(item.barcode)
      if (barcodeData) {
        const base64Image = barcodeData.split(',')[1]
        const imageId = workbook.addImage({ base64: base64Image, extension: 'png' })
        worksheet.addImage(imageId, {
          tl: { col: barcodeImageColumnIndex, row: index + 1 },
          br: { col: barcodeImageColumnIndex + 1, row: index + 2 },
          editAs: 'oneCell',
        } as any)
      }
    }
  })

  worksheet.eachRow((row, rowNumber) => {
    if (rowNumber > 1) {
      row.eachCell({ includeEmpty: false }, (cell) => {
        cell.alignment = { vertical: 'middle' }
      })
    }
  })

  const buffer = await workbook.xlsx.writeBuffer()
  const blob = new Blob([buffer], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  const today = new Date()
  const dateStr = `${today.getFullYear()}${String(today.getMonth() + 1).padStart(2, '0')}${String(today.getDate()).padStart(2, '0')}`
  link.href = url
  link.download = t('productCreation.batchDetailFile', '批次明细_{{batchNumber}}_{{date}}.xlsx', {
    batchNumber,
    date: dateStr,
  })
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  URL.revokeObjectURL(url)
}
