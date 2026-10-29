import type { Key } from 'react'
import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import { isActionColumnFilterKey, matchesActionColumnFilterKey } from './progressBuckets'
import {
  getBarcodeStatusFilter,
  getActionTypeFilter,
  getProductStatusFilter,
  type BarcodeStatusFilter,
  type ActionTypeFilter,
  type ProductStatusFilter,
} from './statusFilters'

export type TextFilterField = 'itemNumber' | 'barcode' | 'productName'
export type NumberFilterField =
  | 'quantity'
  | 'lastPurchasePrice'
  | 'purchasePrice'
  | 'retailPrice'
  | 'pricingFloatRate'
  | 'newAutoRetailPrice'
  | 'discountRate'
  | 'amount'
export type TextFilterMode = 'contains' | 'equals' | 'startsWith' | 'endsWith' | 'empty' | 'notEmpty'
export type NumberFilterMode = 'equals' | 'gt' | 'gte' | 'lt' | 'lte' | 'between' | 'empty' | 'notEmpty'

export interface TextColumnFilterModel {
  mode: TextFilterMode
  value?: string
}

export interface NumberColumnFilterModel {
  mode: NumberFilterMode
  value?: number
  min?: number
  max?: number
}

function isEmptyValue(value: unknown) {
  return value === undefined || value === null || value === ''
}

function parseJsonObject(value: Key | boolean) {
  if (typeof value !== 'string') return null

  try {
    const parsed = JSON.parse(value)
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : null
  } catch {
    return null
  }
}

function parseNumber(value: unknown) {
  if (value === undefined || value === null || value === '') return undefined
  const numeric = Number(value)
  return Number.isFinite(numeric) ? numeric : undefined
}

function getNumberColumnValue(record: LocalSupplierInvoiceItemDto, field: NumberFilterField) {
  const value = record[field]
  if (value === undefined || value === null) return undefined

  // 折扣率在 DTO 中按小数保存，表格显示和编辑使用百分比，列头过滤也按用户看到的百分比匹配。
  return field === 'discountRate' ? value * 100 : value
}

export function serializeTextColumnFilter(model: TextColumnFilterModel) {
  return JSON.stringify(model)
}

export function parseTextColumnFilter(value: Key | boolean): TextColumnFilterModel {
  const parsed = parseJsonObject(value)
  if (!parsed) {
    return { mode: 'contains', value: String(value ?? '') }
  }

  const mode = typeof parsed.mode === 'string' ? parsed.mode : 'contains'
  const normalizedMode: TextFilterMode = (
    ['contains', 'equals', 'startsWith', 'endsWith', 'empty', 'notEmpty'].includes(mode)
      ? mode
      : 'contains'
  ) as TextFilterMode

  return {
    mode: normalizedMode,
    value: typeof parsed.value === 'string' ? parsed.value : '',
  }
}

export function serializeNumberColumnFilter(model: NumberColumnFilterModel) {
  return JSON.stringify(model)
}

export function parseNumberColumnFilter(value: Key | boolean): NumberColumnFilterModel {
  const parsed = parseJsonObject(value)
  if (!parsed) {
    return { mode: 'equals', value: parseNumber(value) }
  }

  const mode = typeof parsed.mode === 'string' ? parsed.mode : 'equals'
  const normalizedMode: NumberFilterMode = (
    ['equals', 'gt', 'gte', 'lt', 'lte', 'between', 'empty', 'notEmpty'].includes(mode)
      ? mode
      : 'equals'
  ) as NumberFilterMode

  return {
    mode: normalizedMode,
    value: parseNumber(parsed.value),
    min: parseNumber(parsed.min),
    max: parseNumber(parsed.max),
  }
}

export function compareNullableNumbers(left?: number | null, right?: number | null) {
  const leftEmpty = isEmptyValue(left)
  const rightEmpty = isEmptyValue(right)

  if (leftEmpty && rightEmpty) return 0
  if (leftEmpty) return 1
  if (rightEmpty) return -1

  return Number(left) - Number(right)
}

export function compareNullableText(left?: string | null, right?: string | null) {
  const leftEmpty = isEmptyValue(left)
  const rightEmpty = isEmptyValue(right)

  if (leftEmpty && rightEmpty) return 0
  if (leftEmpty) return 1
  if (rightEmpty) return -1

  return String(left).localeCompare(String(right), undefined, {
    sensitivity: 'base',
    numeric: true,
  })
}

export function matchesTextColumnFilter(
  record: LocalSupplierInvoiceItemDto,
  field: TextFilterField,
  value: Key | boolean,
) {
  const filter = parseTextColumnFilter(value)
  const actual = String(record[field] ?? '')
  const normalizedActual = actual.trim().toLowerCase()

  if (filter.mode === 'empty') return !normalizedActual
  if (filter.mode === 'notEmpty') return Boolean(normalizedActual)

  const keyword = String(filter.value ?? '').trim().toLowerCase()
  if (!keyword) return true

  if (filter.mode === 'equals') return normalizedActual === keyword
  if (filter.mode === 'startsWith') return normalizedActual.startsWith(keyword)
  if (filter.mode === 'endsWith') return normalizedActual.endsWith(keyword)
  return normalizedActual.includes(keyword)
}

export function matchesNumberColumnFilter(
  record: LocalSupplierInvoiceItemDto,
  field: NumberFilterField,
  value: Key | boolean,
) {
  const filter = parseNumberColumnFilter(value)
  const actual = getNumberColumnValue(record, field)

  if (filter.mode === 'empty') return actual === undefined || actual === null
  if (filter.mode === 'notEmpty') return actual !== undefined && actual !== null
  if (actual === undefined || actual === null) return false

  if (filter.mode === 'between') {
    const hasMin = filter.min !== undefined
    const hasMax = filter.max !== undefined
    if (!hasMin && !hasMax) return true
    return (!hasMin || actual >= filter.min!) && (!hasMax || actual <= filter.max!)
  }

  if (filter.value === undefined) return true

  if (filter.mode === 'gt') return actual > filter.value
  if (filter.mode === 'gte') return actual >= filter.value
  if (filter.mode === 'lt') return actual < filter.value
  if (filter.mode === 'lte') return actual <= filter.value
  return actual === filter.value
}

export function filterBooleanColumn(actual: boolean | undefined | null, value: Key | boolean) {
  if (actual === undefined || actual === null) return false
  return actual === String(value).toLowerCase().includes('true')
}

export function filterProductStatusColumn(record: LocalSupplierInvoiceItemDto, value: Key | boolean) {
  return getProductStatusFilter(record) === (String(value) as ProductStatusFilter)
}

export function filterBarcodeStatusColumn(record: LocalSupplierInvoiceItemDto, value: Key | boolean) {
  return getBarcodeStatusFilter(record) === (String(value) as BarcodeStatusFilter)
}

export function matchesActionTypeColumnFilter(
  record: LocalSupplierInvoiceItemDto,
  value: Key | boolean,
  rowActions: Record<string, number> = {},
) {
  // 操作列表头过滤的取值是 bucket:xxx / action:N（按处理进度匹配）；纯数字沿用旧口径，按操作类型直接匹配。
  const text = String(value)
  if (isActionColumnFilterKey(text)) return matchesActionColumnFilterKey(record, text, rowActions)
  return getActionTypeFilter(record, rowActions) === (Number(value) as ActionTypeFilter)
}
