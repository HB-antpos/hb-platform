// 前缀的共用规则：前缀管理页和「货号条码创建」里的前缀管理弹窗共用同一份，避免两处各写一套校验。
import type { ProductPrefixCodeItem, SavePrefixCodePayload } from '../../../types/productPrefixCode'

/** 前缀代码长度上限，与后端 CreateProductPrefixCodeDto / UpdateProductPrefixCodeDto 的 StringLength(10) 一致。 */
export const PREFIX_CODE_MAX_LENGTH = 10
/** 前缀说明长度上限，与后端 StringLength(200) 一致。 */
export const PREFIX_DESCRIPTION_MAX_LENGTH = 200
/** 排序号上限，后端是 int，Range(0, int.MaxValue)。 */
export const PREFIX_SORT_ORDER_MAX = 2147483647

// 与后端 RegularExpression(@"^[A-Za-z0-9]+$") 保持一致：只允许字母和数字。
const PREFIX_CODE_PATTERN = /^[A-Za-z0-9]+$/

export type PrefixCodeRuleError = 'required' | 'pattern' | 'tooLong'

/**
 * 保存前统一整理前缀：去掉首尾空白并转大写。
 * 旧页面只有弹窗转大写、页面不转，会让大小写不同的同名前缀并存；统一在这里收口。
 */
export function normalizePrefixCode(value: unknown): string {
  return typeof value === 'string' ? value.trim().toUpperCase() : ''
}

/** 校验前缀（先整理再校验，所以首尾空白和小写都是可接受的输入）。通过时返回 null。 */
export function validatePrefixCode(value: unknown): PrefixCodeRuleError | null {
  const normalized = normalizePrefixCode(value)
  if (!normalized) {
    return 'required'
  }
  if (!PREFIX_CODE_PATTERN.test(normalized)) {
    return 'pattern'
  }
  if (normalized.length > PREFIX_CODE_MAX_LENGTH) {
    return 'tooLong'
  }
  return null
}

/** 排序号只接受不小于 0 的整数；空值或非法值一律视为「未填」，由后端按空值处理，而不是悄悄补成 0。 */
export function normalizeSortOrder(value: unknown): number | undefined {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return undefined
  }
  const integer = Math.trunc(value)
  if (integer < 0) {
    return undefined
  }
  return Math.min(integer, PREFIX_SORT_ORDER_MAX)
}

/** 表单里的原始取值（InputNumber 清空后是 null）。 */
export interface PrefixCodeFormValues {
  supplierCode?: string
  prefixName?: string
  prefixDescription?: string
  sortOrder?: number | null
  isActive?: boolean
}

/**
 * 把表单取值整理成请求体。
 * - 前缀统一转大写；
 * - 说明去首尾空白，清空后传 undefined（后端 DTO 为 null，即清除说明）；
 * - sortOrder 随表单提交，不再被服务层补成 0。
 * 编辑时不带 supplierCode（后端 Update 不改供应商）。
 */
export function buildPrefixPayload(values: PrefixCodeFormValues, supplierCode?: string): SavePrefixCodePayload {
  return {
    ...(supplierCode ? { supplierCode } : {}),
    prefixName: normalizePrefixCode(values.prefixName),
    prefixDescription: values.prefixDescription?.trim() || undefined,
    isActive: values.isActive ?? true,
    sortOrder: normalizeSortOrder(values.sortOrder),
  }
}

/**
 * 列表里直接切换启用状态用的请求体：后端 React 接口没有单独的状态接口，只能走 PUT 全量更新。
 * 必须原样带回前缀、说明和排序，只改 isActive；排序为空时保持为空，不能顺手写成 0。
 */
export function buildStatusTogglePayload(
  record: Pick<ProductPrefixCodeItem, 'prefixName' | 'prefixDescription' | 'sortOrder'>,
  nextActive: boolean,
): SavePrefixCodePayload {
  return {
    prefixName: record.prefixName,
    prefixDescription: record.prefixDescription,
    isActive: nextActive,
    sortOrder: record.sortOrder,
  }
}

/** 后端业务错误码（RequestError.payload.errorCode），用于把「前缀重复」落到字段上而不是只弹全局提示。 */
export function getPrefixApiErrorCode(error: unknown): string | undefined {
  if (typeof error !== 'object' || error === null || !('payload' in error)) {
    return undefined
  }
  const payload = (error as { payload?: unknown }).payload
  if (typeof payload !== 'object' || payload === null || !('errorCode' in payload)) {
    return undefined
  }
  const code = (payload as { errorCode?: unknown }).errorCode
  return typeof code === 'string' ? code : undefined
}

/** antd Form.validateFields 校验失败抛出的是带 errorFields 的对象，这类错误不应再弹全局提示。 */
export function isFormValidationError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'errorFields' in error
}
