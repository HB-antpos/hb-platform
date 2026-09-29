import assert from 'node:assert/strict'
import type { ReportPeriod } from '../ReportWorkbench/logic'
import { fetchRevenueReportSnapshot } from './revenueReportService'

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []
let pending = false
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return new Response(JSON.stringify({
    success: true,
    data: {
      branches: [{ rank: 1, branchCode: 'S1', branchName: 'Store 1', revenue: 10, revenueLY: 5, orderCount: 2, orderCountLY: 1, aov: 5, aovLY: 5 }],
      hourly: [{ hour: '09:00', branchCode: 'S1', revenue: 10, revenueLY: 5, percentage: 100, isPeak: true }],
      weekly: [{ key: 'w2026-36', level: 'week', hierarchy: '2026-W36', revenue: 10, revenueLY: 5, orders: 2, ordersLY: 1, aov: 5, aovLY: 5, children: [] }],
      comparePeriodPending: true,
      hourlyCurrentPending: false,
      hourlyComparePending: true,
      weeklyComparePending: false,
      lastDay: { date: '2026-09-03T00:00:00', compareDate: '2025-09-04T00:00:00', branches: [], hourly: [{ hour: '09:00', branchCode: 'S1', revenue: 3, revenueLY: 2, orderCount: 1, orderCountLY: 1, percentage: 100, isPeak: true }] },
    },
    statisticStatus: pending ? 'Pending' : 'Fresh',
    ...(pending ? {} : { statisticsLastSuccessfulAtUtc: '2026-09-03T06:12:00Z' }),
    cacheVersion: 'revenue-v1',
  }), { status: 200, headers: { 'content-type': 'application/json' } })
}) as typeof fetch

try {
  const period: ReportPeriod = {
    startDate: '2026-09-01', endDate: '2026-09-03', compareStartDate: '2025-09-02',
    compareEndDate: '2025-09-04', compareMode: 'ByWeek',
  }
  const controller = new AbortController()
  const snapshot = await fetchRevenueReportSnapshot(period, ['S1', 'S2'], ['S1'], controller.signal)
  const url = new URL(calls[0]!.url, 'http://localhost')
  assert.equal(url.pathname, '/api/react/v1/dashboard/revenue-report-snapshot')
  assert.equal(url.searchParams.get('compareMode'), 'ByWeek')
  assert.deepEqual(url.searchParams.getAll('branchCodes'), ['S1', 'S2'])
  assert.deepEqual(url.searchParams.getAll('focusBranchCodes'), ['S1'])
  assert.equal(url.searchParams.get('topN'), '100')
  assert.equal(calls[0]!.init?.signal, controller.signal, '聚合查询必须透传取消信号')
  assert.equal(snapshot.statisticStatus, 'Fresh')
  assert.equal(snapshot.cacheVersion, 'revenue-v1')
  assert.equal(snapshot.data.branches[0]?.branchCode, 'S1')
  assert.equal(snapshot.data.hourly[0]?.hour, '09:00')
  assert.equal(snapshot.data.weekly[0]?.level, 'week')
  assert.equal(snapshot.data.hourlyComparePending, true)
  assert.equal(snapshot.data.comparePeriodPending, true, '总体缺口可以由小时统计触发')
  assert.equal(snapshot.data.weeklyComparePending, false, 'Store 同期完整状态必须与小时缺口分别保留')
  assert.equal(snapshot.data.statisticsLastSuccessfulAtUtc, '2026-09-03T06:12:00Z', '统计发布时间必须随快照带回，决定累计对比的截止整点')
  assert.equal(snapshot.data.lastDay?.hourly[0]?.orderCount, 1, '区间最后一天的分店小时数据必须随快照带回')
  pending = true
  const waiting = await fetchRevenueReportSnapshot(period, ['S1', 'S2'], ['S1'], controller.signal)
  assert.equal(waiting.statisticStatus, 'Pending', 'Pending 整页快照必须交给查询 hook 轮询')
  assert.equal(waiting.data.statisticsLastSuccessfulAtUtc, null, '旧后端缺字段时归一为 null，由页面回退为不对齐')
} finally {
  globalThis.fetch = originalFetch
}

console.log('营业额整页快照参数、取消与统计包络：通过')
