import request from '../../../utils/request'
import type { ReportPeriod } from '../ReportWorkbench/logic'
import type { ReportSnapshot } from '../ReportWorkbench/useReportQuery'
import type { RevenueBranch, RevenueHourly, RevenueLastDay, RevenueWeeklyNode } from './types'

interface RevenueEnvelope<T> {
  success?: boolean
  data?: T
  message?: string
  statisticsPending?: boolean
  statisticStatus?: string
  statisticMessage?: string | null
  statisticUpdatedAt?: string | null
  statisticsLastSuccessfulAtUtc?: string | null
  cacheVersion?: string | null
}

export interface RevenueReportSnapshot {
  branches: RevenueBranch[]
  hourly: RevenueHourly[]
  weekly: RevenueWeeklyNode[]
  /** 多日区间且最后一天是今天时才有；旧后端缺字段为 undefined。 */
  lastDay?: RevenueLastDay | null
  currentPeriodPending?: boolean
  comparePeriodPending?: boolean
  hourlyCurrentPending?: boolean
  hourlyComparePending?: boolean
  weeklyComparePending?: boolean
  /** 查询含今天时，今天最近一次营业额发布时间（UTC）；决定可与去年同一时刻比较的完整整点。 */
  statisticsLastSuccessfulAtUtc?: string | null
}

function unwrapRevenueSnapshot(payload: RevenueEnvelope<RevenueReportSnapshot>): ReportSnapshot<RevenueReportSnapshot> {
  if (payload.success === false) throw new Error(payload.message || '营业额数据加载失败')
  const data = payload.data
  if (!data || !Array.isArray(data.branches) || !Array.isArray(data.hourly) || !Array.isArray(data.weekly)) {
    throw new Error('营业额快照响应不完整')
  }
  return {
    data: { ...data, statisticsLastSuccessfulAtUtc: payload.statisticsLastSuccessfulAtUtc ?? null },
    statisticStatus: payload.statisticStatus || (payload.statisticsPending ? 'Pending' : 'Fresh'),
    statisticMessage: payload.statisticMessage ?? payload.message,
    statisticUpdatedAt: payload.statisticUpdatedAt,
    cacheVersion: payload.cacheVersion,
  }
}

/** 排名、时段与周层级一次读取，保证整页来自同一统计快照。 */
export async function fetchRevenueReportSnapshot(
  period: ReportPeriod,
  branchCodes: string[] | null,
  focusBranchCodes: string[] | null,
  signal: AbortSignal,
): Promise<ReportSnapshot<RevenueReportSnapshot>> {
  const payload = await request.get<RevenueEnvelope<RevenueReportSnapshot>>(
    '/api/react/v1/dashboard/revenue-report-snapshot',
    { signal, params: { ...period, branchCodes: branchCodes ?? undefined,
      focusBranchCodes: focusBranchCodes ?? undefined, topN: 100 } },
  )
  return unwrapRevenueSnapshot(payload)
}
