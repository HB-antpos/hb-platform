import type {
  PackCartonItem,
  PackDestination,
  PackDestinationRequest,
  PackLocateRow,
} from '../../../types/warehousePacking'
import { PACK_DESTINATION_TYPE } from '../../../types/warehousePacking'

// 扫码打包页面的纯逻辑：不依赖 React / DOM，便于单测。

/** 后端时间是 UTC；数据库读出的值可能不带时区后缀，这里统一按 UTC 解析。 */
export function parsePackUtc(value: string | null | undefined): Date | null {
  if (!value) {
    return null
  }
  const hasZone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(value)
  const date = new Date(hasZone ? value : `${value}Z`)
  return Number.isNaN(date.getTime()) ? null : date
}

/** 列表 / 抽屉里的时间：`11-13 14:32`（浏览器本地时区）；空值显示占位。 */
export function formatPackTime(value: string | null | undefined, placeholder = '—'): string {
  const date = parsePackUtc(value)
  if (!date) {
    return placeholder
  }
  const pad = (part: number) => String(part).padStart(2, '0')
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

// ---- 去向 ----

/** 去向表单值：箱和托盘共用。 */
export interface PackDestinationValue {
  destinationType: number
  storeCode?: string | null
  carrierGuid?: string | null
}

export const UNASSIGNED_DESTINATION: PackDestinationValue = { destinationType: PACK_DESTINATION_TYPE.Unassigned }

export function destinationToValue(destination?: PackDestination | null): PackDestinationValue {
  if (!destination) {
    return UNASSIGNED_DESTINATION
  }
  return {
    destinationType: destination.destinationType,
    storeCode: destination.storeCode ?? null,
    carrierGuid: destination.carrierGuid ?? null,
  }
}

/** 表单值 → 接口请求：未指定时不带分店 / 运输公司；直送分店不带运输公司。 */
export function destinationToRequest(value: PackDestinationValue): PackDestinationRequest {
  if (value.destinationType === PACK_DESTINATION_TYPE.Store) {
    return { destinationType: value.destinationType, destinationStoreCode: value.storeCode ?? null, carrierGuid: null }
  }
  if (value.destinationType === PACK_DESTINATION_TYPE.Carrier) {
    return {
      destinationType: value.destinationType,
      destinationStoreCode: value.storeCode ?? null,
      carrierGuid: value.carrierGuid ?? null,
    }
  }
  return { destinationType: PACK_DESTINATION_TYPE.Unassigned, destinationStoreCode: null, carrierGuid: null }
}

export type PackDestinationProblem = 'storeRequired' | 'carrierRequired'

/** 提交前校验（与后端规则一致）：分店必须选分店；运输公司必须选一家，最终收货分店可先不填。 */
export function validateDestination(value: PackDestinationValue): PackDestinationProblem | null {
  if (value.destinationType === PACK_DESTINATION_TYPE.Store && !value.storeCode) {
    return 'storeRequired'
  }
  if (value.destinationType === PACK_DESTINATION_TYPE.Carrier && !value.carrierGuid) {
    return 'carrierRequired'
  }
  return null
}

export function isSameDestination(left: PackDestinationValue, right: PackDestinationValue): boolean {
  const a = destinationToRequest(left)
  const b = destinationToRequest(right)
  return (
    a.destinationType === b.destinationType &&
    (a.destinationStoreCode ?? '') === (b.destinationStoreCode ?? '') &&
    (a.carrierGuid ?? '') === (b.carrierGuid ?? '')
  )
}

/** 去向的展示文字：分店名；经运输公司为「公司 → 分店」；未指定返回 null 由界面显示占位。 */
export function describeDestination(destination?: PackDestination | null): string | null {
  if (!destination || destination.destinationType === PACK_DESTINATION_TYPE.Unassigned) {
    return null
  }
  const store = destination.storeName || destination.storeCode || ''
  if (destination.destinationType === PACK_DESTINATION_TYPE.Carrier) {
    const carrier = destination.carrierName || ''
    return store ? `${carrier} → ${store}` : carrier || null
  }
  return store || null
}

// ---- 打包台 ----

/** 计数方式：每扫 +1 / 按中包 / 按箱规。 */
export type PackCountMode = 'one' | 'middle' | 'packing'

export interface PackScanStep {
  /** 本次扫码应增加的件数。 */
  quantity: number
  /** 选了按中包 / 箱规但商品没有维护该数量，退回每扫 +1。 */
  fellBack: boolean
}

/** 按计数方式算一次扫码增加多少件；没有中包数 / 箱规（或不大于 1）时按 1 件。 */
export function resolveScanStep(
  mode: PackCountMode,
  product?: Pick<PackCartonItem, 'middlePackageQuantity' | 'packingQuantity'> | null,
): PackScanStep {
  if (mode === 'one') {
    return { quantity: 1, fellBack: false }
  }
  const size = mode === 'middle' ? product?.middlePackageQuantity : product?.packingQuantity
  return typeof size === 'number' && Number.isFinite(size) && size > 1
    ? { quantity: Math.floor(size), fellBack: false }
    : { quantity: 1, fellBack: true }
}

/** 扫到的码是否就是箱里已有的某个商品（条码 / 货号 / 商品编码，忽略大小写）；用于提前知道中包数 / 箱规。 */
export function findCartonItemByCode(items: PackCartonItem[], code: string): PackCartonItem | undefined {
  const target = code.trim().toLowerCase()
  if (!target) {
    return undefined
  }
  return items.find((item) =>
    [item.barcode, item.itemNumber, item.productCode].some((candidate) => (candidate ?? '').trim().toLowerCase() === target),
  )
}

export interface PackOrderProgress {
  /** 商品在关联订货单上。 */
  onOrder: boolean
  /** 还差多少件（配货数 − 其他箱已装 − 本箱）；装多了为负数。 */
  remaining: number
  /** 装多了。 */
  over: boolean
}

/** 关联订货单后每行的对照：订货单外的商品 onOrder=false。 */
export function resolveOrderProgress(
  item: Pick<PackCartonItem, 'orderQuantity' | 'packedInOtherCartons' | 'quantity'>,
): PackOrderProgress {
  if (item.orderQuantity === null || item.orderQuantity === undefined) {
    return { onOrder: false, remaining: 0, over: false }
  }
  const remaining = item.orderQuantity - (item.packedInOtherCartons ?? 0) - item.quantity
  return { onOrder: true, remaining, over: remaining < 0 }
}

/** 箱号（BX…）或托盘号（PL…）的样子：先交给后端 resolve，再决定按商品码处理。 */
export function looksLikePackNo(code: string): boolean {
  return /^(?:BX|PL)\d{6,}$/i.test(code.trim())
}

export type PackStationHotkey = 'newCarton' | 'sealCarton' | 'sealPallet'

/** 打包台快捷键：F2 新箱、F9 封箱并打印、F10 封托并打印；带修饰键时不处理。 */
export function resolveStationHotkey(event: {
  key: string
  ctrlKey?: boolean
  metaKey?: boolean
  altKey?: boolean
  shiftKey?: boolean
}): PackStationHotkey | null {
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) {
    return null
  }
  switch (event.key) {
    case 'F2':
      return 'newCarton'
    case 'F9':
      return 'sealCarton'
    case 'F10':
      return 'sealPallet'
    default:
      return null
  }
}

/** 重量输入：空为不填；必须是 0～9999.99 的数字，否则返回 undefined 表示无效。 */
export function parseWeightInput(value: number | string | null | undefined): number | null | undefined {
  if (value === null || value === undefined || value === '') {
    return null
  }
  const weight = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(weight) || weight < 0 || weight > 9999.99) {
    return undefined
  }
  return Math.round(weight * 100) / 100
}

// ---- 商品位置 ----

export interface PackLocateGroup {
  /** 货位编码；没有暂存货位的箱归到空串一组。 */
  locationCode: string
  quantity: number
  rows: PackLocateRow[]
}

/** 位置查询结果按货位分组并给出小计：有货位的按编码排序在前，未指定货位的排最后；组内按最近变动倒序。 */
export function groupLocateRows(rows: PackLocateRow[]): PackLocateGroup[] {
  const groups = new Map<string, PackLocateGroup>()
  for (const row of rows) {
    const locationCode = (row.locationCode ?? '').trim()
    const group = groups.get(locationCode) ?? { locationCode, quantity: 0, rows: [] }
    group.quantity += row.quantity
    group.rows.push(row)
    groups.set(locationCode, group)
  }
  const time = (row: PackLocateRow) => parsePackUtc(row.updatedAtUtc)?.getTime() ?? 0
  return [...groups.values()]
    .map((group) => ({ ...group, rows: [...group.rows].sort((a, b) => time(b) - time(a)) }))
    .sort((a, b) => {
      if (!a.locationCode !== !b.locationCode) {
        return a.locationCode ? -1 : 1
      }
      return a.locationCode.localeCompare(b.locationCode)
    })
}
