import type {
  PackCartonItem,
  PackCartonSummary,
  PackDestination,
  PackDestinationRequest,
  PackIncomingItem,
  PackLocateRow,
  PackOwner,
  PackPalletDetail,
  PackPalletSummary,
} from '../../types/packing'
import {
  PACK_CARTON_STATUS,
  PACK_DESTINATION_TYPE,
  PACK_ERROR_CODE,
  PACK_PALLET_STATUS,
} from '../../types/packing'

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

// ---- 所属地点 ----

/** 页面右上角选中的地点记在本机；账号只有一个地点时不用选。 */
export const PACK_OWNER_STORAGE_KEY = 'hb.packing.ownerCode'

/** 初始地点：本机记住的（必须仍在账号可操作范围内），否则取第一个。 */
export function resolveInitialOwnerCode(owners: PackOwner[], saved: string | null | undefined): string | null {
  if (owners.length === 0) {
    return null
  }
  const savedCode = (saved ?? '').trim().toLowerCase()
  const matched = owners.find((owner) => owner.code.toLowerCase() === savedCode)
  return (matched ?? owners[0]).code
}

// ---- 去向 ----

/** 去向表单值：箱和托盘共用。 */
export interface PackDestinationValue {
  destinationType: number
  /** 其他分店的编码；总仓不用填。 */
  destinationCode?: string | null
  /** 怎么送：为空 = 自己送 / 对方来取。 */
  carrierGuid?: string | null
  /** 仅界面用：选了「交运输公司」但还没选具体哪一家时为 true（不会提交给接口）。 */
  viaCarrier?: boolean
}

export const UNASSIGNED_DESTINATION: PackDestinationValue = { destinationType: PACK_DESTINATION_TYPE.Unassigned }

export function destinationToValue(destination?: PackDestination | null): PackDestinationValue {
  if (!destination) {
    return UNASSIGNED_DESTINATION
  }
  return {
    destinationType: destination.destinationType,
    destinationCode: destination.code ?? null,
    carrierGuid: destination.carrierGuid ?? null,
    viaCarrier: Boolean(destination.carrierGuid),
  }
}

/** 要发出的去向（其他分店 / 总仓）才有「怎么送」。 */
export function isShippableDestinationType(type: number): boolean {
  return type === PACK_DESTINATION_TYPE.Store || type === PACK_DESTINATION_TYPE.Warehouse
}

/** 表单值 → 接口请求：本地存放 / 未指定不带地点和运输公司；总仓编码由后端固定，不传。 */
export function destinationToRequest(value: PackDestinationValue): PackDestinationRequest {
  if (value.destinationType === PACK_DESTINATION_TYPE.Store) {
    return {
      destinationType: value.destinationType,
      destinationCode: value.destinationCode ?? null,
      carrierGuid: value.carrierGuid ?? null,
    }
  }
  if (value.destinationType === PACK_DESTINATION_TYPE.Warehouse) {
    return { destinationType: value.destinationType, destinationCode: null, carrierGuid: value.carrierGuid ?? null }
  }
  return {
    destinationType: value.destinationType === PACK_DESTINATION_TYPE.Local ? PACK_DESTINATION_TYPE.Local : PACK_DESTINATION_TYPE.Unassigned,
    destinationCode: null,
    carrierGuid: null,
  }
}

export type PackDestinationProblem = 'storeRequired' | 'warehouseFromWarehouse' | 'carrierRequired'

/** 提交前校验（与后端规则一致）：其他分店必须选分店；总仓不能发给总仓自己；选了交运输公司就要选一家。 */
export function validateDestination(value: PackDestinationValue, ownerIsWarehouse = false): PackDestinationProblem | null {
  if (value.destinationType === PACK_DESTINATION_TYPE.Store && !value.destinationCode) {
    return 'storeRequired'
  }
  if (value.destinationType === PACK_DESTINATION_TYPE.Warehouse && ownerIsWarehouse) {
    return 'warehouseFromWarehouse'
  }
  if (isShippableDestinationType(value.destinationType) && value.viaCarrier && !value.carrierGuid) {
    return 'carrierRequired'
  }
  return null
}

export function isSameDestination(left: PackDestinationValue, right: PackDestinationValue): boolean {
  const a = destinationToRequest(left)
  const b = destinationToRequest(right)
  return (
    a.destinationType === b.destinationType &&
    (a.destinationCode ?? '') === (b.destinationCode ?? '') &&
    (a.carrierGuid ?? '') === (b.carrierGuid ?? '')
  )
}

export type PackDestinationKind = 'unassigned' | 'local' | 'store' | 'warehouse'

export function destinationKind(destination?: PackDestination | null): PackDestinationKind {
  switch (destination?.destinationType) {
    case PACK_DESTINATION_TYPE.Local:
      return 'local'
    case PACK_DESTINATION_TYPE.Store:
      return 'store'
    case PACK_DESTINATION_TYPE.Warehouse:
      return 'warehouse'
    default:
      return 'unassigned'
  }
}

/** 去向是不是要发出的（其他分店 / 总仓，且有收货地点）。 */
export function isShippableDestination(destination?: PackDestination | null): boolean {
  return isShippableDestinationType(destination?.destinationType ?? -1) && Boolean(destination?.code)
}

/** 去向的收货地点名称（其他分店 / 总仓）；本地存放、未指定返回 null。 */
export function destinationPlaceName(destination?: PackDestination | null): string | null {
  if (!isShippableDestinationType(destination?.destinationType ?? -1)) {
    return null
  }
  return destination?.name || destination?.code || null
}

// ---- 状态展示（按当前查看的地点区分「本地存放」和「对方已签收」）----

export type PackStateKind = 'packing' | 'building' | 'stored' | 'transit' | 'received' | 'voided'

function sameCode(left: string | null | undefined, right: string | null | undefined) {
  return (left ?? '').trim().toLowerCase() === (right ?? '').trim().toLowerCase()
}

/** 箱的状态：已封箱且还在本地点手里是「本地存放」；已被对方签收（所属地点换了）是「对方已签收」。 */
export function cartonStateKind(carton: Pick<PackCartonSummary, 'status' | 'ownerCode'>, viewerOwnerCode: string): PackStateKind {
  switch (carton.status) {
    case PACK_CARTON_STATUS.Packing:
      return 'packing'
    case PACK_CARTON_STATUS.InTransit:
      return 'transit'
    case PACK_CARTON_STATUS.Voided:
      return 'voided'
    default:
      return sameCode(carton.ownerCode, viewerOwnerCode) ? 'stored' : 'received'
  }
}

export function palletStateKind(pallet: Pick<PackPalletSummary, 'status' | 'ownerCode'>, viewerOwnerCode: string): PackStateKind {
  switch (pallet.status) {
    case PACK_PALLET_STATUS.Building:
      return 'building'
    case PACK_PALLET_STATUS.InTransit:
      return 'transit'
    case PACK_PALLET_STATUS.Voided:
      return 'voided'
    default:
      return sameCode(pallet.ownerCode, viewerOwnerCode) ? 'stored' : 'received'
  }
}

/** 发出后发出方不能再改：在路上、或已被对方签收（所属地点不是自己）。 */
export function isReadOnlyForOwner(item: { status: number; ownerCode: string }, viewerOwnerCode: string): boolean {
  if (item.status === PACK_CARTON_STATUS.InTransit || item.status === PACK_CARTON_STATUS.Voided) {
    return true
  }
  return !sameCode(item.ownerCode, viewerOwnerCode)
}

/** 散箱能不能发出：已封箱、不在托盘上、去向是其他分店 / 总仓。 */
export function canDispatchCarton(carton: PackCartonSummary, viewerOwnerCode: string): boolean {
  return (
    carton.status === PACK_CARTON_STATUS.Sealed &&
    sameCode(carton.ownerCode, viewerOwnerCode) &&
    !carton.palletGuid &&
    isShippableDestination(carton.destination)
  )
}

/** 托盘能不能发出：已封托、托盘上有箱、去向是其他分店 / 总仓。 */
export function canDispatchPallet(pallet: PackPalletSummary, viewerOwnerCode: string): boolean {
  return (
    pallet.status === PACK_PALLET_STATUS.Sealed &&
    sameCode(pallet.ownerCode, viewerOwnerCode) &&
    pallet.cartonCount > 0 &&
    isShippableDestination(pallet.destination)
  )
}

/** 托盘抽屉的四步进度：组托 → 封托 → 发出 → 签收；有时间的步骤点亮。 */
export interface PackPalletStep {
  key: 'build' | 'seal' | 'dispatch' | 'receive'
  done: boolean
  time: string | null
  by: string | null
}

export function buildPalletSteps(pallet: PackPalletSummary): PackPalletStep[] {
  return [
    { key: 'build', done: true, time: pallet.createdAtUtc, by: pallet.createdByName },
    { key: 'seal', done: Boolean(pallet.sealedAtUtc), time: pallet.sealedAtUtc ?? null, by: pallet.sealedByName ?? null },
    {
      key: 'dispatch',
      done: Boolean(pallet.dispatchedAtUtc),
      time: pallet.dispatchedAtUtc ?? null,
      by: pallet.dispatchedByName ?? null,
    },
    { key: 'receive', done: Boolean(pallet.receivedAtUtc), time: pallet.receivedAtUtc ?? null, by: pallet.receivedByName ?? null },
  ]
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

/** 关联订货单（只有总仓才有）后每行的对照：订货单外的商品 onOrder=false。 */
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

// ---- 到货签收 ----

/** 托盘上还在路上（没签收）的箱。 */
export function pendingPalletCartons(pallet: Pick<PackPalletDetail, 'cartons'>): PackCartonSummary[] {
  return pallet.cartons.filter((carton) => carton.status === PACK_CARTON_STATUS.InTransit)
}

/** 实到箱数输入：必须是非负整数，且等于系统记录的在路上箱数才算数对了（初始 0，数对才可点确认）。 */
export function isCartonCountConfirmed(input: number | string | null | undefined, expected: number): boolean {
  if (input === null || input === undefined || input === '') {
    return false
  }
  const value = typeof input === 'number' ? input : Number(input)
  return Number.isInteger(value) && value >= 0 && expected > 0 && value === expected
}

export type PackReceiveBlock = 'notInTransit' | 'notForYou' | null

/** 扫码查找后不能签收的原因（后端 blockReason）。 */
export function resolveReceiveBlock(reason: string | null | undefined): PackReceiveBlock {
  if (reason === PACK_ERROR_CODE.NotInTransit) {
    return 'notInTransit'
  }
  if (reason === PACK_ERROR_CODE.NotForYou) {
    return 'notForYou'
  }
  return null
}

/** 逐箱核对后「只签已核对的」要传的箱 GUID：只取仍在路上的。 */
export function selectCheckedCartons(pallet: Pick<PackPalletDetail, 'cartons'>, checked: ReadonlySet<string>): string[] {
  return pendingPalletCartons(pallet)
    .map((carton) => carton.cartonGuid)
    .filter((guid) => checked.has(guid))
}

/** 扫到一只箱时在核对面板里勾选它：已签收的箱、不在托盘上的箱不处理。返回是否勾上了。 */
export function checkScannedCarton(
  pallet: Pick<PackPalletDetail, 'cartons'>,
  cartonNo: string,
  checked: Set<string>,
): boolean {
  const target = cartonNo.trim().toUpperCase()
  const carton = pendingPalletCartons(pallet).find((item) => item.cartonNo.toUpperCase() === target)
  if (!carton) {
    return false
  }
  checked.add(carton.cartonGuid)
  return true
}

/** 待签收列表里的状态：托盘部分签收时显示「部分签收 3/5」。 */
export type PackIncomingState = { kind: 'transit' } | { kind: 'partial'; received: number; total: number } | { kind: 'received' }

export function incomingState(item: {
  kind: string
  cartonCount: number
  receivedCartonCount: number
  receivedAtUtc?: string | null
}): PackIncomingState {
  if (item.receivedAtUtc) {
    return { kind: 'received' }
  }
  if (item.kind === 'pallet' && item.receivedCartonCount > 0) {
    return { kind: 'partial', received: item.receivedCartonCount, total: item.cartonCount }
  }
  return { kind: 'transit' }
}

// ---- 按订单签收（二期）----

/** 核对面板里一张订单的「按订单签收」入口：同一订单只给一个，cartonCount 是面板里这张订单还在路上的箱数。 */
export interface PackOrderReceiveEntry {
  orderGuid: string
  orderNo: string
  cartonCount: number
}

/**
 * 从面板里的箱（托盘上的箱 / 扫到的散箱）收集「按订单签收」入口：只看还在路上、且带订单号的箱，
 * 同一订单合并成一个入口，按首次出现的顺序排列。
 */
export function collectOrderReceiveEntries(
  cartons: Pick<PackCartonSummary, 'status' | 'orderGuid' | 'orderNo'>[],
): PackOrderReceiveEntry[] {
  const entries = new Map<string, PackOrderReceiveEntry>()
  for (const carton of cartons) {
    const orderGuid = carton.orderGuid?.trim()
    const orderNo = carton.orderNo?.trim()
    if (carton.status !== PACK_CARTON_STATUS.InTransit || !orderGuid || !orderNo) {
      continue
    }
    const key = orderGuid.toLowerCase()
    const existing = entries.get(key)
    if (existing) {
      existing.cartonCount += 1
    } else {
      entries.set(key, { orderGuid, orderNo, cartonCount: 1 })
    }
  }
  return [...entries.values()]
}

/** 待签收清单里一行关联的订货单：label 是显示用的订单号（后端没给订单号时退回 GUID 前 8 位）。 */
export interface PackIncomingOrderRef {
  orderGuid: string
  label: string
}

/**
 * 待签收 / 最近签收清单一行关联的订货单：散箱行取 orderGuid / orderNo，托盘行取 orders[]（后端已去重、按订单号排序）；
 * 没有订单 GUID 的丢弃，同一行里按 GUID（忽略大小写）再去重一次。旧后端没有这些字段时返回空。
 */
export function incomingItemOrders(
  item: Pick<PackIncomingItem, 'kind' | 'orderGuid' | 'orderNo' | 'orders'>,
): PackIncomingOrderRef[] {
  const candidates =
    item.kind === 'pallet' ? (item.orders ?? []) : [{ orderGuid: item.orderGuid, orderNo: item.orderNo }]
  const refs = new Map<string, PackIncomingOrderRef>()
  for (const candidate of candidates) {
    const orderGuid = candidate.orderGuid?.trim()
    if (!orderGuid || refs.has(orderGuid.toLowerCase())) {
      continue
    }
    refs.set(orderGuid.toLowerCase(), { orderGuid, label: candidate.orderNo?.trim() || orderGuid.slice(0, 8) })
  }
  return [...refs.values()]
}

/** 一行在清单里的订单：entries 在这一行给「按订单签收」入口，covered 已经在前面的行给过入口，这里只显示订单号。 */
export interface PackIncomingRowOrders {
  entries: PackIncomingOrderRef[]
  covered: PackIncomingOrderRef[]
}

/**
 * 给待签收清单（按显示顺序）的每一行分配「按订单签收」入口：同一订单（按 GUID，忽略大小写）只在第一次出现的行给入口，
 * 后面的行只显示订单号，不重复占位。按订单签收签的是这个订单发给本店的全部在途箱，与从哪一行点进去无关。
 * 返回 key → 该行的订单；没有任何订单的行不在结果里。
 */
export function assignIncomingOrderEntries(
  rows: readonly { key: string; orders: PackIncomingOrderRef[] }[],
): Map<string, PackIncomingRowOrders> {
  const owned = new Set<string>()
  const result = new Map<string, PackIncomingRowOrders>()
  for (const row of rows) {
    if (row.orders.length === 0) {
      continue
    }
    const entries: PackIncomingOrderRef[] = []
    const covered: PackIncomingOrderRef[] = []
    for (const order of row.orders) {
      const key = order.orderGuid.toLowerCase()
      if (owned.has(key)) {
        covered.push(order)
      } else {
        owned.add(key)
        entries.push(order)
      }
    }
    result.set(row.key, { entries, covered })
  }
  return result
}

/** 订单发给本店的箱里还在路上（没签收）的。 */
export function pendingOrderCartons<T extends { status: number }>(order: { cartons: T[] }): T[] {
  return order.cartons.filter((carton) => carton.status === PACK_CARTON_STATUS.InTransit)
}

/** 本次发出数和配货数对不上（发出≠配货）：订单所有行都没有配货数时无从比较，不算。 */
export function isOrderShipMismatch(product: { allocPieces?: number | null; shippedPieces: number }): boolean {
  if (product.allocPieces === null || product.allocPieces === undefined) {
    return false
  }
  return Number(product.allocPieces) !== Number(product.shippedPieces)
}

export type PackOrderReceiveFailure = 'countMismatch' | 'notInTransit' | 'notFound' | 'other'

/** 按订单签收失败的类型（后端错误码）：数量对不上、没有在途箱、订单不在本店范围内。 */
export function classifyOrderReceiveFailure(errorCode: string | null | undefined): PackOrderReceiveFailure {
  switch (errorCode) {
    case PACK_ERROR_CODE.CountMismatch:
      return 'countMismatch'
    case PACK_ERROR_CODE.NotInTransit:
      return 'notInTransit'
    case PACK_ERROR_CODE.OrderNotFound:
      return 'notFound'
    default:
      return 'other'
  }
}

/** 409 COUNT_MISMATCH 带回的「实际在途箱数」；没带（或不是非负整数）时用当前读到的在途数兜底。 */
export function resolveActualInTransitCount(data: { actualInTransitCartonCount?: number | null } | null | undefined, fallback: number): number {
  const actual = data?.actualInTransitCartonCount
  return typeof actual === 'number' && Number.isInteger(actual) && actual >= 0 ? actual : fallback
}

// ---- 商品位置 ----

export const LOCATE_GROUP_ORDER = ['stored', 'transit', 'elsewhere', 'other'] as const

export interface PackLocateSection {
  group: (typeof LOCATE_GROUP_ORDER)[number]
  quantity: number
  rows: PackLocateRow[]
}

/**
 * 位置查询结果按后端的 group 分组：本地存放（含位置）、在路上、对方已签收、其他分店（仅店长查全部分店）。
 * 组内：有位置的按位置排序在前，没位置的在后；再按最近变动倒序。
 */
export function groupLocateRows(rows: PackLocateRow[]): PackLocateSection[] {
  const time = (row: PackLocateRow) => parsePackUtc(row.updatedAtUtc)?.getTime() ?? 0
  const location = (row: PackLocateRow) => (row.locationCode ?? '').trim()
  return LOCATE_GROUP_ORDER.flatMap((group) => {
    const own = rows.filter((row) => (LOCATE_GROUP_ORDER as readonly string[]).includes(row.group) ? row.group === group : group === 'other')
    if (own.length === 0) {
      return []
    }
    const sorted = [...own].sort((a, b) => {
      if (group === 'other' && a.ownerName !== b.ownerName) {
        return a.ownerName.localeCompare(b.ownerName)
      }
      const la = location(a)
      const lb = location(b)
      if (!la !== !lb) {
        return la ? -1 : 1
      }
      return la.localeCompare(lb) || time(b) - time(a)
    })
    return [{ group, quantity: sorted.reduce((sum, row) => sum + row.quantity, 0), rows: sorted }]
  })
}

// ---- 存放位置 ----

export const PACK_LOCATION_NAME_MAX = 50

/** 位置名称：去掉首尾空白，空或超长无效（返回 null）。 */
export function normalizeLocationName(value: string | null | undefined): string | null {
  const name = (value ?? '').trim()
  return name && name.length <= PACK_LOCATION_NAME_MAX ? name : null
}
