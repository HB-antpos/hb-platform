import {
  buildLinklyDeviceSelectOptions,
  canExportLinklySettlementRange,
  createLatestAbortableRequestGuard,
  formatAmountMinor,
  formatLocalCalendarDate,
  getDefaultLinklySettlementDateRange,
  getAmountParseStatusColor,
  getInclusiveCalendarDayCount,
  getProviderSubmissionColor,
  getLinklySettlementRouteIdFromPathname,
  getSettlementStatusColor,
  getValidLinklySettlementRouteId,
} from './logic'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`)
}

assertEqual(
  formatLocalCalendarDate(new Date(2026, 7, 3, 23, 59, 59)),
  '2026-08-03',
  '默认业务日必须使用本地日历日期',
)
const beforeMidnightRange = getDefaultLinklySettlementDateRange(new Date(2026, 7, 3, 23, 59, 59))
const afterMidnightRange = getDefaultLinklySettlementDateRange(new Date(2026, 7, 4, 0, 0, 1))
assertEqual(beforeMidnightRange.join(','), '2026-08-03,2026-08-03', '长期挂载页面的初始日期')
assertEqual(afterMidnightRange.join(','), '2026-08-04,2026-08-04', '跨午夜重置必须重新读取本地当天')
assertEqual(formatAmountMinor(12345, 'Parsed'), '$123.45', '已解析分币应格式化为 AUD')
assertEqual(formatAmountMinor(-50, 'Parsed'), '-$0.50', '负金额应保留符号')
assertEqual(formatAmountMinor(0, 'Missing'), '--', '未解析金额不能显示为零')
assertEqual(formatAmountMinor(null, 'Parsed'), '--', '缺失金额不能显示为零')
assertEqual(getSettlementStatusColor('Succeeded'), 'success', '成功状态颜色')
assertEqual(getSettlementStatusColor('Unknown'), 'warning', '未知状态颜色')
assertEqual(getProviderSubmissionColor('NotSubmitted'), 'error', '未提交状态颜色')
assertEqual(getProviderSubmissionColor(null), 'default', '空提交状态不得伪装为 Unknown')
assertEqual(getAmountParseStatusColor('Invalid'), 'error', '无效金额状态颜色')

assertEqual(getInclusiveCalendarDayCount('2026-08-01', '2026-08-31'), 31, '导出范围应含首尾两天')
assertEqual(canExportLinklySettlementRange('2026-08-01', '2026-08-31'), true, '31 天允许导出')
assertEqual(canExportLinklySettlementRange('2026-08-01', '2026-09-01'), false, '32 天禁止导出')
assertEqual(canExportLinklySettlementRange('2026-08-31', '2026-08-01'), false, '反向日期禁止导出')
assertEqual(
  getValidLinklySettlementRouteId('9007199254740993'),
  '9007199254740993',
  '详情路由 BIGINT ID 必须保持原始十进制字符串',
)
assertEqual(getValidLinklySettlementRouteId('00041'), '00041', '详情路由不得通过数值转换规范化 ID')
assertEqual(getValidLinklySettlementRouteId('0'), null, '详情路由必须拒绝非正 ID')
assertEqual(getValidLinklySettlementRouteId('41x'), null, '详情路由必须拒绝非十进制 ID')
assertEqual(
  getLinklySettlementRouteIdFromPathname('/pos-admin/linkly-settlements/9007199254740993'),
  '9007199254740993',
  '后台手动解析路由元素时仍须从当前 pathname 取得详情 ID',
)
assertEqual(
  getLinklySettlementRouteIdFromPathname('/pos-admin/linkly-settlements/not-a-number'),
  null,
  '详情 pathname 必须拒绝非法 ID',
)

const guard = createLatestAbortableRequestGuard()
const first = guard.begin()
assertEqual(first.signal.aborted, false, '首个请求应处于活动状态')
const second = guard.begin()
assertEqual(first.signal.aborted, true, '新请求必须取消旧请求')
assertEqual(guard.isLatest(first.requestId), false, '旧响应不得成为最新请求')
assertEqual(guard.isLatest(second.requestId), true, '第二个请求应为最新请求')
guard.abort()
assertEqual(second.signal.aborted, true, '页面卸载必须取消活动请求')
assertEqual(guard.isLatest(second.requestId), false, '取消后迟到响应不得提交')

const detailGuard = createLatestAbortableRequestGuard()
let committedDetailId = ''
const firstDetail = detailGuard.begin()
const secondDetail = detailGuard.begin()
if (detailGuard.isLatest(firstDetail.requestId)) committedDetailId = '9007199254740992'
assertEqual(committedDetailId, '', '详情旧响应不得覆盖新路由记录')
if (detailGuard.isLatest(secondDetail.requestId)) committedDetailId = '9007199254740993'
assertEqual(committedDetailId, '9007199254740993', '详情最新响应应保留 BIGINT ID')

const detailSource = readFileSync(resolve('src/pages/PosAdmin/LinklySettlementDetail/index.tsx'), 'utf8')
assertEqual(
  detailSource.includes("dataIndex: 'cashOutCount'")
    && detailSource.includes("t('linklySettlements.detail.cashOutCount')"),
  true,
  '卡种明细必须同时显示 Cash Out 金额与笔数',
)
assertEqual(
  detailSource.includes('requestGuardRef.current.isLatest(currentRequest.requestId)')
    && detailSource.includes('currentRequest.signal'),
  true,
  '详情成功、错误和 finally 必须使用 latest guard 与 AbortSignal',
)

const listSource = readFileSync(resolve('src/pages/PosAdmin/LinklySettlements/index.tsx'), 'utf8')
assertEqual(
  listSource.includes('rowKey={(record) => record.id}')
    && !listSource.includes('Number(record.id)'),
  true,
  '列表 rowKey 必须直接使用字符串 ID',
)

const deviceCandidates = [
  { storeCode: '1002', deviceCode: 'POS_1002_2111' },
  { storeCode: '1013', deviceCode: 'POS_1013_0239' },
  { storeCode: '1013', deviceCode: 'POS_1013_0222' },
  { storeCode: '1013', deviceCode: 'POS_1013_0222' },
]
assertEqual(
  buildLinklyDeviceSelectOptions(deviceCandidates, '1013').map((item) => item.value).join(','),
  'POS_1013_0222,POS_1013_0239',
  '选了门店时只列该门店设备并去重排序',
)
assertEqual(
  buildLinklyDeviceSelectOptions(deviceCandidates).length,
  3,
  '未选门店时列出全部设备（去重）',
)
assertEqual(
  buildLinklyDeviceSelectOptions(deviceCandidates)[0].label,
  'POS_1002_2111 · 1002',
  '未选门店时选项文案带门店',
)

console.log('LinklySettlements logic.test: ok')
