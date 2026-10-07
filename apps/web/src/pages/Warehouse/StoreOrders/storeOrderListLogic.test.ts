import dayjs from 'dayjs'
import { StoreOrderFlowStatus, type StoreOrderListQuery } from '../../../types/storeOrder'
import {
  DEFAULT_STORE_ORDER_STATUS_TAB,
  STORE_ORDER_COUNTED_STATUSES,
  buildStoreOrderStatusCountQuery,
  buildStoreOrderStatusCountSignature,
  buildStoreOrderStatusTabCounts,
  canCopySelectedStoreOrders,
  describeStoreOrderUpdatedAt,
  formatStoreOrderDateRange,
  formatStoreOrderInteger,
  formatStoreOrderListDate,
  formatStoreOrderMoney,
  formatStoreOrderNumberRange,
  getActiveStoreOrderMoreFilterGroups,
  getStoreOrderOutboundState,
  getStoreOrderStatusPillTone,
  getStoreOrderStatusTabStatusList,
  removeStoreOrderMoreFilterGroup,
  summarizeStoreOrders,
  toStoreOrderStatusCounts,
} from './storeOrderListLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const S = StoreOrderFlowStatus

// 状态页签口径：默认「进行中」= 已提交 + 配货中；「全部」只含三种仓库状态，不含购物车。
assertEqual(DEFAULT_STORE_ORDER_STATUS_TAB, 'active', '默认页签应为进行中')
assertDeepEqual(getStoreOrderStatusTabStatusList('active'), [S.Submitted, S.Picking], '进行中应查询已提交和配货中')
assertDeepEqual(getStoreOrderStatusTabStatusList('submitted'), [S.Submitted], '已提交页签只查已提交')
assertDeepEqual(getStoreOrderStatusTabStatusList('picking'), [S.Picking], '配货中页签只查配货中')
assertDeepEqual(getStoreOrderStatusTabStatusList('completed'), [S.Completed], '已完成页签只查已完成')
assertDeepEqual(
  getStoreOrderStatusTabStatusList('all'),
  [S.Submitted, S.Picking, S.Completed],
  '全部页签应显式只查三种状态，不能传空列表把购物车也查出来',
)
const activeList = getStoreOrderStatusTabStatusList('active')
activeList.push(S.Completed)
assertDeepEqual(getStoreOrderStatusTabStatusList('active'), [S.Submitted, S.Picking], '调用方修改返回数组不能污染页签口径')

// 页签计数：由三个状态计数推出，失败或未加载时全部不显示。
assertDeepEqual(
  buildStoreOrderStatusTabCounts(null),
  { active: undefined, submitted: undefined, picking: undefined, completed: undefined, all: undefined },
  '没有计数时各页签都不显示数字',
)
assertDeepEqual(
  buildStoreOrderStatusTabCounts({ submitted: 31, picking: 15, completed: 1238 }),
  { active: 46, submitted: 31, picking: 15, completed: 1238, all: 1284 },
  '进行中 = 已提交 + 配货中，全部 = 三者之和',
)
assertDeepEqual(toStoreOrderStatusCounts([3, 4, 5]), { submitted: 3, picking: 4, completed: 5 }, '计数按固定状态顺序组装')
assertDeepEqual(STORE_ORDER_COUNTED_STATUSES, [S.Submitted, S.Picking, S.Completed], '计数请求状态顺序应与组装顺序一致')

// 计数请求：沿用其他筛选，只取 1 条，固定按订单日期排序。
const baseQuery: StoreOrderListQuery = {
  keyword: 'SO-1',
  storeCodes: ['PEN10'],
  startDate: '2026-10-01T00:00:00.000Z',
  endDate: '2026-10-06T23:59:59.999Z',
  statusList: [S.Submitted, S.Picking],
  columnFilters: { totalOrderAmountMin: 1000 },
  pageNumber: 3,
  pageSize: 50,
  sortBy: 'totalOrderAmount',
  sortDescending: false,
}
assertDeepEqual(
  buildStoreOrderStatusCountQuery(baseQuery, S.Completed),
  {
    ...baseQuery,
    statusList: [S.Completed],
    pageNumber: 1,
    pageSize: 1,
    sortBy: 'orderDate',
    sortDescending: true,
  },
  '计数请求应保留关键字/分店/日期/列筛选，只替换状态、分页与排序',
)

// 计数签名：翻页、排序、切页签不变；其他筛选变化才变。
const signature = buildStoreOrderStatusCountSignature(baseQuery)
assertEqual(
  buildStoreOrderStatusCountSignature({ ...baseQuery, pageNumber: 1, pageSize: 20, sortBy: 'orderNo', statusList: [S.Completed] }),
  signature,
  '翻页、排序和切换状态不应触发重新计数',
)
assertEqual(
  buildStoreOrderStatusCountSignature({ ...baseQuery, storeCodes: ['LIV11'] }) === signature,
  false,
  '分店变化应触发重新计数',
)
assertEqual(
  buildStoreOrderStatusCountSignature({ ...baseQuery, columnFilters: undefined }) === signature,
  false,
  '列筛选变化应触发重新计数',
)

// 状态胶囊颜色只表达状态。
assertEqual(getStoreOrderStatusPillTone(S.Submitted), 'blue', '已提交为蓝色')
assertEqual(getStoreOrderStatusPillTone(S.Picking), 'orange', '配货中为橙色')
assertEqual(getStoreOrderStatusPillTone(S.Completed), 'green', '已完成为绿色')
assertEqual(getStoreOrderStatusPillTone(S.ShoppingCart), 'gray', '购物车为灰色')

assertEqual(canCopySelectedStoreOrders(0), false, '未勾选不能复制')
assertEqual(canCopySelectedStoreOrders(1), true, '恰好勾选 1 单才能复制')
assertEqual(canCopySelectedStoreOrders(2), false, '勾选多单时不能复制（原先只复制第一张会误导）')

// 本页合计。
assertDeepEqual(
  summarizeStoreOrders([
    { totalQuantity: 860, totalOrderVolume: 4.12, totalOrderAmount: 9846.2, importTotalAmount: 0 },
    { totalQuantity: 1482, totalOrderVolume: undefined, totalOrderAmount: 17630.1, importTotalAmount: 15902.44 },
  ]).count,
  2,
  '合计应统计行数',
)
const totals = summarizeStoreOrders([
  { totalQuantity: 860, totalOrderVolume: 4.12, totalOrderAmount: 9846.2, importTotalAmount: 0 },
  { totalQuantity: 1482, totalOrderVolume: undefined, totalOrderAmount: 17630.1, importTotalAmount: 15902.44 },
])
assertEqual(totals.totalQuantity, 2342, '件数合计')
assertEqual(totals.hasVolume, true, '有一行带体积即显示体积合计')
assertEqual(totals.totalVolume.toFixed(2), '4.12', '缺失体积按 0 计入')
assertEqual(totals.totalOrderAmount.toFixed(2), '27476.30', '订货金额合计')
assertEqual(totals.totalShipAmount.toFixed(2), '15902.44', '发货金额合计')
assertEqual(
  summarizeStoreOrders([{ totalQuantity: 1, totalOrderVolume: undefined, totalOrderAmount: 1, importTotalAmount: 0 }]).hasVolume,
  false,
  '全部行都没有体积时不显示体积合计',
)
assertDeepEqual(
  summarizeStoreOrders([]),
  { count: 0, totalQuantity: 0, hasVolume: false, totalVolume: 0, totalOrderAmount: 0, totalShipAmount: 0 },
  '空页合计为 0',
)

// 数字格式。
assertEqual(formatStoreOrderMoney(17630.1), '17,630.10', '金额千分位两位小数')
assertEqual(formatStoreOrderMoney(undefined), '--', '金额缺失显示 --')
assertEqual(formatStoreOrderInteger(1482), '1,482', '件数千分位')
assertEqual(formatStoreOrderInteger(null), '--', '件数缺失显示 --')

// 日期与出库状态。
const now = dayjs('2026-10-06T10:30:00')
assertEqual(formatStoreOrderListDate('2026-10-05T00:00:00', now), '10-05', '当年日期只显示月-日')
assertEqual(formatStoreOrderListDate('2025-12-31T00:00:00', now), '2025-12-31', '跨年日期带年份')
assertEqual(formatStoreOrderListDate(undefined, now), null, '缺失日期返回 null')
assertDeepEqual(getStoreOrderOutboundState(undefined, now), { kind: 'unset' }, '没有出库日期为出库未定')
assertDeepEqual(getStoreOrderOutboundState('2026-10-06T00:00:00', now), { kind: 'today' }, '今天出库要强调')
assertDeepEqual(getStoreOrderOutboundState('2026-10-08T00:00:00', now), { kind: 'date', label: '10-08' }, '其他日期显示月-日')

// 更新时间：今天 / 昨天 / 更早，均为绝对时刻。
assertDeepEqual(describeStoreOrderUpdatedAt('2026-10-06T10:18:00', now), { kind: 'today', time: '10:18' }, '当天显示今天 + 时刻')
assertDeepEqual(describeStoreOrderUpdatedAt('2026-10-06T00:05:00', now), { kind: 'today', time: '00:05' }, '当天凌晨仍算今天')
assertDeepEqual(describeStoreOrderUpdatedAt('2026-10-05T17:40:00', now), { kind: 'yesterday', time: '17:40' }, '昨天显示时刻')
assertDeepEqual(describeStoreOrderUpdatedAt('2026-10-04T16:20:00', now), { kind: 'date', label: '10-04 16:20' }, '更早显示月-日 时刻')
assertDeepEqual(describeStoreOrderUpdatedAt('2025-10-04T16:20:00', now), { kind: 'date', label: '2025-10-04' }, '跨年只显示日期')
assertEqual(describeStoreOrderUpdatedAt(undefined, now), null, '缺失更新时间返回 null')
assertEqual(describeStoreOrderUpdatedAt('not-a-date', now), null, '无效时间返回 null')

// 筛选摘要。
assertEqual(formatStoreOrderNumberRange(1000, undefined), '≥ 1000', '只填下限')
assertEqual(formatStoreOrderNumberRange(undefined, 5), '≤ 5', '只填上限')
assertEqual(formatStoreOrderNumberRange(10, 20), '10 ~ 20', '上下限都有')
assertEqual(formatStoreOrderNumberRange(0, undefined), '≥ 0', '0 是有效下限')
assertEqual(formatStoreOrderNumberRange(undefined, undefined), null, '都没填返回 null')
assertEqual(
  formatStoreOrderDateRange(dayjs('2026-10-03').startOf('day').toISOString(), dayjs('2026-10-06').endOf('day').toISOString()),
  '2026-10-03 ~ 2026-10-06',
  '日期区间摘要',
)
assertEqual(formatStoreOrderDateRange(undefined, undefined), null, '没有日期区间返回 null')

const filters = {
  outboundDateStart: '2026-10-01T00:00:00.000Z',
  totalOrderAmountMin: 1000,
  remarks: '  ',
  updatedBy: '王敏',
  totalQuantityMax: 0,
}
assertDeepEqual(
  getActiveStoreOrderMoreFilterGroups(filters),
  ['outboundDate', 'totalQuantity', 'totalOrderAmount', 'updatedBy'],
  '生效分组按固定顺序列出，空白文本不算生效、0 算生效',
)
assertDeepEqual(
  removeStoreOrderMoreFilterGroup(filters, 'totalOrderAmount'),
  { outboundDateStart: '2026-10-01T00:00:00.000Z', remarks: '  ', updatedBy: '王敏', totalQuantityMax: 0 },
  '移除分组只删除该组字段',
)
assertEqual(filters.totalOrderAmountMin, 1000, '移除分组不能修改入参')

console.log('storeOrderListLogic.test: ok')
