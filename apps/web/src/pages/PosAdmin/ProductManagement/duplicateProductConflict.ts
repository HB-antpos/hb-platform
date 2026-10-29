/**
 * 新建商品被后端查重拦截时的 409 响应解析（ReactProductsController.CreateWithPrices / ReactProductController.Create）。
 *
 * - BARCODE_EXISTS：条码（主条码 / 分店多码 / 一品多码）已被任何供应商的存活商品占用，
 *   响应里带 existingProducts，提示用户选用已有商品，不提供「仍然创建」。
 * - ITEM_NUMBER_EXISTS：同供应商下货号已存在（原有逻辑，没有 existingProducts）。
 */
export const BARCODE_EXISTS_ERROR_CODE = 'BARCODE_EXISTS'
export const ITEM_NUMBER_EXISTS_ERROR_CODE = 'ITEM_NUMBER_EXISTS'

export interface ExistingProductHit {
  productCode: string
  itemNumber?: string
  productName?: string
  localSupplierCode?: string
  /** 占用方式：MainBarcode / StoreMultiCode / ProductSetCode（后端 ProductDuplicateGuard.Source*），仅作透传。 */
  source?: string
}

export interface ProductCreateConflict {
  errorCode: typeof BARCODE_EXISTS_ERROR_CODE | typeof ITEM_NUMBER_EXISTS_ERROR_CODE
  /** 后端给出的中文说明（含「条码 X 已属于商品 …」），缺失时由页面使用本地文案。 */
  message?: string
  existingProducts: ExistingProductHit[]
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readText(source: Record<string, unknown>, aliases: string[]): string | undefined {
  for (const alias of aliases) {
    const value = source[alias]
    if (typeof value === 'string' && value.trim()) return value.trim()
  }
  return undefined
}

/**
 * 从请求异常里读出新建商品的查重冲突；不是这两类冲突（网络错误、5xx、其他业务错误）返回 null，由调用方走通用提示。
 * 请求层把 4xx 响应体放在 error.payload；这里按结构读取，不依赖具体的异常类。
 */
export function readProductCreateConflict(error: unknown): ProductCreateConflict | null {
  const payload = isRecord(error) ? error.payload : undefined
  if (!isRecord(payload)) return null

  const errorCode = readText(payload, ['errorCode', 'ErrorCode'])
  if (errorCode !== BARCODE_EXISTS_ERROR_CODE && errorCode !== ITEM_NUMBER_EXISTS_ERROR_CODE) return null

  const rawProducts = payload.existingProducts ?? payload.ExistingProducts
  const existingProducts: ExistingProductHit[] = []
  if (Array.isArray(rawProducts)) {
    for (const item of rawProducts) {
      if (!isRecord(item)) continue
      const productCode = readText(item, ['productCode', 'ProductCode'])
      if (!productCode) continue
      existingProducts.push({
        productCode,
        itemNumber: readText(item, ['itemNumber', 'ItemNumber']),
        productName: readText(item, ['productName', 'ProductName']),
        localSupplierCode: readText(item, ['localSupplierCode', 'LocalSupplierCode']),
        source: readText(item, ['source', 'Source']),
      })
    }
  }

  return {
    errorCode,
    message: readText(payload, ['message', 'Message']),
    existingProducts,
  }
}

/** 弹窗里列出的一行：货号 · 名称 · 供应商；缺失的部分用 -- 占位，保证一眼能认出是哪件商品。 */
export function formatExistingProductLine(product: ExistingProductHit, labels: { supplier: string }) {
  const part = (value?: string) => (value && value.trim() ? value.trim() : '--')
  return `${part(product.itemNumber)} · ${part(product.productName)} · ${labels.supplier} ${part(product.localSupplierCode)}`
}
