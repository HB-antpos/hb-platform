import dayjs, { type Dayjs } from 'dayjs'
import type { StatusPillTone } from '../../../components/listToolbar/StatusPill'
import {
  StoreOrderFlowStatus,
  type StoreOrderListColumnFilters,
  type StoreOrderListItem,
  type StoreOrderListQuery,
} from '../../../types/storeOrder'

/**
 * 分店订货列表的纯逻辑：状态页签口径、页签计数、本页合计、出库/更新时间的展示判定与筛选摘要。
 * 页面只负责渲染与请求，这里的规则都有单测覆盖。
 */

export type StoreOrderStatusTabKey = 'active' | 'submitted' | 'picking' | 'completed' | 'all'

export const STORE_ORDER_STATUS_TAB_KEYS: readonly StoreOrderStatusTabKey[] = [
  'active',
  'submitted',
  'picking',
  'completed',
  'all',
]

/** 默认页签「进行中」= 已提交 + 配货中，与改版前状态复选框的默认勾选一致。 */
export const DEFAULT_STORE_ORDER_STATUS_TAB: StoreOrderStatusTabKey = 'active'

/**
 * 需要单独计数的三个状态。「全部」也只含这三个状态：改版前状态复选框只提供这三项，
 * 购物车（未提交）订单不进入仓库列表。
 */
export const STORE_ORDER_COUNTED_STATUSES: readonly StoreOrderFlowStatus[] = [
  StoreOrderFlowStatus.Submitted,
  StoreOrderFlowStatus.Picking,
  StoreOrderFlowStatus.Completed,
]

const STATUS_TAB_STATUS_LIST: Record<StoreOrderStatusTabKey, readonly StoreOrderFlowStatus[]> = {
  active: [StoreOrderFlowStatus.Submitted, StoreOrderFlowStatus.Picking],
  submitted: [StoreOrderFlowStatus.Submitted],
  picking: [StoreOrderFlowStatus.Picking],
  completed: [StoreOrderFlowStatus.Completed],
  all: STORE_ORDER_COUNTED_STATUSES,
}

/** 页签对应的服务端 statusList；每次返回新数组，避免调用方误改常量。 */
export function getStoreOrderStatusTabStatusList(tab: StoreOrderStatusTabKey): StoreOrderFlowStatus[] {
  return [...STATUS_TAB_STATUS_LIST[tab]]
}

export interface StoreOrderStatusCounts {
  submitted: number
  picking: number
  completed: number
}

/** 由三个状态的计数推出各页签数字：进行中 = 已提交 + 配货中，全部 = 三者之和；没有计数时都不显示。 */
export function buildStoreOrderStatusTabCounts(
  counts: StoreOrderStatusCounts | null,
): Record<StoreOrderStatusTabKey, number | undefined> {
  if (!counts) {
    return { active: undefined, submitted: undefined, picking: undefined, completed: undefined, all: undefined }
  }
  return {
    active: counts.submitted + counts.picking,
    submitted: counts.submitted,
    picking: counts.picking,
    completed: counts.completed,
    all: counts.submitted + counts.picking + counts.completed,
  }
}

/** 按与 STORE_ORDER_COUNTED_STATUSES 相同顺序返回的 total 组装计数。 */
export function toStoreOrderStatusCounts(totals: readonly number[]): StoreOrderStatusCounts {
  const [submitted = 0, picking = 0, completed = 0] = totals
  return { submitted, picking, completed }
}

/**
 * 页签计数只随「状态以外」的筛选变化：翻页、排序、切换页签都不需要重新计数。
 * 用签名判断是否要重发计数请求。
 */
export function buildStoreOrderStatusCountSignature(query: StoreOrderListQuery) {
  return JSON.stringify({
    keyword: query.keyword ?? null,
    productKeyword: query.productKeyword ?? null,
    storeCode: query.storeCode ?? null,
    storeCodes: query.storeCodes ?? null,
    startDate: query.startDate ?? null,
    endDate: query.endDate ?? null,
    columnFilters: query.columnFilters ?? null,
  })
}

/**
 * 单个状态的计数请求：沿用列表的其他筛选，只取 1 条看 total。
 * 排序固定为订单日期，避免按金额等聚合列排序时把计数请求也拖进聚合管线。
 */
export function buildStoreOrderStatusCountQuery(
  query: StoreOrderListQuery,
  status: StoreOrderFlowStatus,
): StoreOrderListQuery {
  return {
    ...query,
    statusList: [status],
    pageNumber: 1,
    pageSize: 1,
    sortBy: 'orderDate',
    sortDescending: true,
  }
}

/** 状态胶囊颜色：已提交蓝、配货中橙、已完成绿、购物车灰。 */
export function getStoreOrderStatusPillTone(status: StoreOrderFlowStatus): StatusPillTone {
  switch (status) {
    case StoreOrderFlowStatus.Submitted:
      return 'blue'
    case StoreOrderFlowStatus.Picking:
      return 'orange'
    case StoreOrderFlowStatus.Completed:
      return 'green'
    default:
      return 'gray'
  }
}

/** 「复制为新订单」只能以一张订单为源：原按钮显示勾选数却只复制第一张，这里要求恰好勾选 1 单。 */
export function canCopySelectedStoreOrders(selectedCount: number) {
  return selectedCount === 1
}

export interface StoreOrderListTotals {
  count: number
  totalQuantity: number
  /** 当前行里只要有一行带体积就为 true；全都没有体积时合计显示为空而不是 0。 */
  hasVolume: boolean
  totalVolume: number
  totalOrderAmount: number
  totalShipAmount: number
}

function toFiniteNumber(value: unknown) {
  const numeric = Number(value)
  return Number.isFinite(numeric) ? numeric : 0
}

/** 当前页（或勾选行）的件数、体积、订货金额与发货金额合计；只基于已加载的行，不代表筛选结果全集。 */
export function summarizeStoreOrders(
  items: readonly Pick<StoreOrderListItem, 'totalQuantity' | 'totalOrderVolume' | 'totalOrderAmount' | 'importTotalAmount'>[],
): StoreOrderListTotals {
  return items.reduce<StoreOrderListTotals>(
    (totals, item) => {
      const hasVolume = typeof item.totalOrderVolume === 'number' && Number.isFinite(item.totalOrderVolume)
      return {
        count: totals.count + 1,
        totalQuantity: totals.totalQuantity + toFiniteNumber(item.totalQuantity),
        hasVolume: totals.hasVolume || hasVolume,
        totalVolume: totals.totalVolume + (hasVolume ? Number(item.totalOrderVolume) : 0),
        totalOrderAmount: totals.totalOrderAmount + toFiniteNumber(item.totalOrderAmount),
        totalShipAmount: totals.totalShipAmount + toFiniteNumber(item.importTotalAmount),
      }
    },
    { count: 0, totalQuantity: 0, hasVolume: false, totalVolume: 0, totalOrderAmount: 0, totalShipAmount: 0 },
  )
}

/** 金额：千分位 + 两位小数；缺失显示 --。不加货币符号，与订货明细页的金额显示保持一致。 */
export function formatStoreOrderMoney(value?: number | null) {
  if (value === undefined || value === null || !Number.isFinite(value)) {
    return '--'
  }
  return value.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

/** 件数：千分位整数；缺失显示 --。 */
export function formatStoreOrderInteger(value?: number | null) {
  if (value === undefined || value === null || !Number.isFinite(value)) {
    return '--'
  }
  return Math.round(value).toLocaleString('en-US')
}

/** 列表里的日期：当年只显示月-日，跨年补上年份，避免旧订单被误读成今年。 */
export function formatStoreOrderListDate(value: string | undefined, now: Dayjs = dayjs()) {
  if (!value) {
    return null
  }
  const date = dayjs(value)
  if (!date.isValid()) {
    return value
  }
  return date.year() === now.year() ? date.format('MM-DD') : date.format('YYYY-MM-DD')
}

export type StoreOrderOutboundState =
  | { kind: 'unset' }
  | { kind: 'today' }
  | { kind: 'date'; label: string }

/** 出库日期第二行：没填为「出库未定」，今天为「今天出库」（强调），其余显示日期。 */
export function getStoreOrderOutboundState(value: string | undefined, now: Dayjs = dayjs()): StoreOrderOutboundState {
  if (!value) {
    return { kind: 'unset' }
  }
  const date = dayjs(value)
  if (!date.isValid()) {
    return { kind: 'date', label: value }
  }
  if (date.isSame(now, 'day')) {
    return { kind: 'today' }
  }
  return { kind: 'date', label: formatStoreOrderListDate(value, now) ?? value }
}

export type StoreOrderUpdatedAt =
  | { kind: 'today'; time: string }
  | { kind: 'yesterday'; time: string }
  | { kind: 'date'; label: string }

/**
 * 「更新人 · 今天 14:20 更新」里的时间。用绝对时刻而不是「N 分钟前」：
 * 后端各写入点的 UpdatedAt 混用本地时间与 UTC，相对时间会把这种偏差放大成「刚改完却显示 11 小时前」。
 * 解析方式与改版前「更新时间」列（new Date(value)）一致，只换展示形式。
 */
export function describeStoreOrderUpdatedAt(
  value: string | undefined,
  now: Dayjs = dayjs(),
): StoreOrderUpdatedAt | null {
  if (!value) {
    return null
  }
  const date = dayjs(value)
  if (!date.isValid()) {
    return null
  }
  if (date.isSame(now, 'day')) {
    return { kind: 'today', time: date.format('HH:mm') }
  }
  if (date.isSame(now.subtract(1, 'day'), 'day')) {
    return { kind: 'yesterday', time: date.format('HH:mm') }
  }
  return {
    kind: 'date',
    label: date.year() === now.year() ? date.format('MM-DD HH:mm') : date.format('YYYY-MM-DD'),
  }
}

/** 数值区间摘要：只填下限为「≥ x」，只填上限为「≤ y」，两端都有为「x ~ y」。 */
export function formatStoreOrderNumberRange(min?: number, max?: number) {
  const hasMin = typeof min === 'number' && Number.isFinite(min)
  const hasMax = typeof max === 'number' && Number.isFinite(max)
  if (hasMin && hasMax) {
    return `${min} ~ ${max}`
  }
  if (hasMin) {
    return `≥ ${min}`
  }
  if (hasMax) {
    return `≤ ${max}`
  }
  return null
}

/** 日期区间摘要（YYYY-MM-DD），用于已生效筛选条。 */
export function formatStoreOrderDateRange(start?: string | null, end?: string | null) {
  const startText = start && dayjs(start).isValid() ? dayjs(start).format('YYYY-MM-DD') : ''
  const endText = end && dayjs(end).isValid() ? dayjs(end).format('YYYY-MM-DD') : ''
  if (!startText && !endText) {
    return null
  }
  if (startText && endText) {
    return startText === endText ? startText : `${startText} ~ ${endText}`
  }
  return startText ? `≥ ${startText}` : `≤ ${endText}`
}

/** 收进「更多筛选」的条件分组；每组对应已生效筛选条里的一个可移除标签。 */
export type StoreOrderMoreFilterGroup =
  | 'outboundDate'
  | 'totalQuantity'
  | 'totalOrderAmount'
  | 'totalOrderVolume'
  | 'importTotalAmount'
  | 'remarks'
  | 'updatedBy'
  | 'createdAt'
  | 'updatedAt'
  | 'orderNo'

export const STORE_ORDER_MORE_FILTER_GROUP_KEYS: Record<StoreOrderMoreFilterGroup, readonly (keyof StoreOrderListColumnFilters)[]> = {
  outboundDate: ['outboundDateStart', 'outboundDateEnd'],
  totalQuantity: ['totalQuantityMin', 'totalQuantityMax'],
  totalOrderAmount: ['totalOrderAmountMin', 'totalOrderAmountMax'],
  totalOrderVolume: ['totalOrderVolumeMin', 'totalOrderVolumeMax'],
  importTotalAmount: ['importTotalAmountMin', 'importTotalAmountMax'],
  remarks: ['remarks'],
  updatedBy: ['updatedBy'],
  createdAt: ['createdAtStart', 'createdAtEnd'],
  updatedAt: ['updatedAtStart', 'updatedAtEnd'],
  // 订单号已由顶部搜索覆盖，不再单独提供输入框；保留分组只为兼容已有条件的展示与移除。
  orderNo: ['orderNo'],
}

function isFilterValueSet(value: unknown) {
  if (typeof value === 'number') {
    return Number.isFinite(value)
  }
  if (typeof value === 'string') {
    return value.trim().length > 0
  }
  return false
}

/** 当前生效的更多筛选分组（按固定顺序），用于按钮角标与已生效筛选条。 */
export function getActiveStoreOrderMoreFilterGroups(filters: StoreOrderListColumnFilters): StoreOrderMoreFilterGroup[] {
  return (Object.keys(STORE_ORDER_MORE_FILTER_GROUP_KEYS) as StoreOrderMoreFilterGroup[]).filter((group) =>
    STORE_ORDER_MORE_FILTER_GROUP_KEYS[group].some((key) => isFilterValueSet(filters[key])),
  )
}

/** 移除某一组条件后的列筛选（不修改入参）。 */
export function removeStoreOrderMoreFilterGroup(
  filters: StoreOrderListColumnFilters,
  group: StoreOrderMoreFilterGroup,
): StoreOrderListColumnFilters {
  const next = { ...filters }
  STORE_ORDER_MORE_FILTER_GROUP_KEYS[group].forEach((key) => {
    delete next[key]
  })
  return next
}
