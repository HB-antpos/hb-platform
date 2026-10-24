import type { LocalSupplierDto } from '../../../types/localSupplier'
import { SUPPLIER_IMAGE_TEMPLATE_PRESETS } from '../ProductManagement/productImageTemplate'

/** 与后端字段长度一致（HBLocalSupplier.ImageBaseUrl）。 */
export const SUPPLIER_IMAGE_TEMPLATE_MAX_LENGTH = 512
/** 预览最多试几个货号。 */
export const SUPPLIER_IMAGE_PREVIEW_LIMIT = 3

export type SupplierImageTemplateState =
  | { kind: 'configured'; host: string | null }
  | { kind: 'cos' }
  | { kind: 'preset'; name: string }
  | { kind: 'missing' }

/**
 * 列表「商品图片」列：单独配置了模板显示域名；没配但供应商代码命中内置预设（DATS / MALMAR / YATSAL）
 * 显示「xx 预设」——商品管理会自动套用；两者都没有才是「未配置」。
 */
export function describeSupplierImageTemplate(
  supplier: Pick<LocalSupplierDto, 'localSupplierCode' | 'imageBaseUrl'>,
): SupplierImageTemplateState {
  const template = supplier.imageBaseUrl?.trim()
  if (template) {
    const host = readTemplateHost(template)
    // 公司自己的 COS 图床域名很长，列表里直接显示「COS 图床」。
    if (host?.endsWith('.myqcloud.com')) {
      return { kind: 'cos' }
    }
    return { kind: 'configured', host }
  }
  const code = supplier.localSupplierCode?.trim().toLowerCase()
  if (code && SUPPLIER_IMAGE_TEMPLATE_PRESETS[code]) {
    return { kind: 'preset', name: code.toUpperCase() }
  }
  return { kind: 'missing' }
}

function readTemplateHost(template: string): string | null {
  try {
    // 占位符不是合法 URL 字符，先替换掉再解析域名。
    const host = new URL(template.replace(/\{[^}]*\}/g, 'x')).hostname
    return host.replace(/^www\./i, '') || null
  } catch {
    return null
  }
}

export type SupplierImageTemplateIssue = 'missingPlaceholder' | 'tooLong'

/** 图片模板：可以不填；填了就必须含 {itemNumber}，否则商品管理拼不出图片地址。 */
export function validateSupplierImageTemplateInput(value: string | undefined | null): SupplierImageTemplateIssue | null {
  const template = value?.trim() ?? ''
  if (!template) {
    return null
  }
  if (template.length > SUPPLIER_IMAGE_TEMPLATE_MAX_LENGTH) {
    return 'tooLong'
  }
  if (!template.includes('{itemNumber}')) {
    return 'missingPlaceholder'
  }
  return null
}

/** 预览货号：按空白或逗号分隔，去重，最多取 3 个。 */
export function parsePreviewItemNumbers(text: string): string[] {
  const seen = new Set<string>()
  for (const part of text.split(/[\s,，]+/)) {
    const value = part.trim()
    if (value && !seen.has(value)) {
      seen.add(value)
    }
    if (seen.size >= SUPPLIER_IMAGE_PREVIEW_LIMIT) {
      break
    }
  }
  return [...seen]
}

/** 订单日期只显示到天（后端是日期列，序列化后可能带 T00:00:00）。 */
export function formatPurchaseDate(value: string | null | undefined): string | null {
  if (!value) {
    return null
  }
  const match = value.match(/^(\d{4}-\d{2}-\d{2})/)
  return match ? match[1] : null
}

export const LOCAL_SUPPLIER_INVOICES_PATH = '/pos-admin/local-supplier-invoices'

/** 跳到分店进货单并预选该供应商（进货单页会同时清掉默认 90 天日期）。 */
export function buildSupplierInvoicesLink(supplierCode: string): string {
  return `${LOCAL_SUPPLIER_INVOICES_PATH}?supplierCode=${encodeURIComponent(supplierCode)}`
}

/** 近期进货单：按订单日期倒序取前几张，只按供应商过滤（分店范围由后端按账号收口）。 */
export function buildRecentInvoicesRequest(supplierCode: string, limit = 5) {
  return {
    startRow: 0,
    endRow: limit,
    pageSize: limit,
    sortModel: [{ colId: 'orderDate', sort: 'desc' }],
    filterModel: {
      supplierCode: { filterType: 'text', type: 'equals', filter: supplierCode },
    },
  }
}
