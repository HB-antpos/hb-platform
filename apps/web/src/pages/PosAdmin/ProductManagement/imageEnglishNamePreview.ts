import type { BatchUpdatePosProductDto } from '../../../types/posProduct'
import type {
  ProductDescriptionSource,
  ProductImageDescriptionItem,
  ProductImageDescriptionSummary,
} from '../../../services/productDescriptionService'

const chineseTextPattern = /[㐀-鿿]/

/** 预览表里的一行：保存前让用户逐条确认看图生成的英文名。 */
export interface ImageEnglishNamePreviewRow {
  key: string
  productCode: string
  productImage?: string
  currentName: string
  newName: string
  source: Exclude<ProductDescriptionSource, 'failed'>
}

interface PreviewSourceRow {
  key: string
  productCode: string
  productName: string
  productImage?: string
}

/** 选中商品转成看图请求：没有商品编码的行无法保存，直接跳过；商品名称作为看图参考。 */
export function buildPosProductImageDescriptionItems(rows: PreviewSourceRow[]): ProductImageDescriptionItem[] {
  return rows
    .filter((row) => Boolean(row.productCode?.trim()))
    .map((row) => ({
      key: row.key,
      imageUrl: row.productImage?.trim() || undefined,
      chineseName: row.productName?.trim() || undefined,
    }))
    .filter((item) => Boolean(item.imageUrl || item.chineseName))
}

/** 只保留有效、且与当前名称不同的结果；失败、空结果、仍含中文的都不进预览。 */
export function buildImageEnglishNamePreviewRows(
  rows: PreviewSourceRow[],
  summary: ProductImageDescriptionSummary,
): ImageEnglishNamePreviewRow[] {
  return rows.reduce<ImageEnglishNamePreviewRow[]>((previewRows, row) => {
    const result = summary.results[row.key]
    const newName = result?.description?.trim()
    const currentName = row.productName?.trim() ?? ''
    if (!result || result.source === 'failed' || !newName || chineseTextPattern.test(newName) || newName === currentName) {
      return previewRows
    }
    previewRows.push({
      key: row.key,
      productCode: row.productCode,
      productImage: row.productImage,
      currentName,
      newName,
      source: result.source,
    })
    return previewRows
  }, [])
}

/** 与原「批量翻译」口径一致：同时覆盖商品名称和英文名称。 */
export function buildImageEnglishNameUpdates(rows: ImageEnglishNamePreviewRow[]): BatchUpdatePosProductDto[] {
  return rows.map((row) => ({
    productCode: row.productCode,
    productName: row.newName,
    englishName: row.newName,
  }))
}
