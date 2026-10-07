import assert from 'node:assert/strict'
import type { PreorderActivationSummary, PreorderTemplateSummary } from '../../../types/preorder'
import {
  PREORDER_LIVE_MAX_TEMPLATES,
  collectLiveActivations,
  countTemplatesByStatus,
  filterTemplates,
  formatCompactDate,
  formatCompactDateTime,
  formatMonthDayWeekdayTime,
  getActivationProgress,
  getActivationStatusTone,
  getActivationTiming,
  getCalendarDayDiff,
  pickCurrentActivation,
  resolveLatestActivationMs,
  runWithConcurrency,
  selectLiveBatchCandidates,
} from './preordersPage.logic'

function template(overrides: Partial<PreorderTemplateSummary>): PreorderTemplateSummary {
  return {
    templateGuid: 't',
    name: '模板',
    isEnabled: true,
    revision: 1,
    itemCount: 1,
    storeCount: 1,
    activationCount: 1,
    ...overrides,
  }
}

function activation(overrides: Partial<PreorderActivationSummary>): PreorderActivationSummary {
  return {
    activationGuid: 'a',
    templateGuid: 't',
    templateName: '模板',
    sequenceNumber: 1,
    activationNumber: 'PRE-1',
    startAtUtc: new Date(2026, 9, 1, 9, 0).toISOString(),
    endAtUtc: new Date(2026, 9, 8, 18, 0).toISOString(),
    estimatedArrivalDate: null,
    status: 'Active',
    targetStoreCount: 26,
    submittedCount: 18,
    noDemandCount: 0,
    pendingCount: 8,
    cancelledCount: 0,
    ...overrides,
  }
}

// 用本地时间构造，测试不受运行机器时区影响。
const now = new Date(2026, 9, 6, 9, 30).getTime()

// 批次状态配色：待开始橙、进行中蓝、已关闭绿、已取消灰。
assert.equal(getActivationStatusTone('Scheduled'), 'orange')
assert.equal(getActivationStatusTone('Active'), 'blue')
assert.equal(getActivationStatusTone('Closed'), 'green')
assert.equal(getActivationStatusTone('Cancelled'), 'gray')

// 候选模板：有最近激活时间时只取近 60 天；没有该字段时退回到有过批次的模板；从未激活的不拉。
{
  const candidates = selectLiveBatchCandidates([
    template({ templateGuid: 'recent', latestActivationAt: new Date(2026, 8, 20).toISOString() }),
    template({ templateGuid: 'old', latestActivationAt: new Date(2026, 6, 1).toISOString() }),
    template({ templateGuid: 'future', latestActivationAt: new Date(2026, 9, 10).toISOString() }),
    template({ templateGuid: 'no-field' }),
    template({ templateGuid: 'never', activationCount: 0 }),
  ], now)
  assert.deepEqual(candidates, { templateGuids: ['recent', 'future', 'no-field'], truncated: false })
}
{
  const many = Array.from({ length: PREORDER_LIVE_MAX_TEMPLATES + 3 }, (_, index) => template({ templateGuid: `t${index}` }))
  const candidates = selectLiveBatchCandidates(many, now)
  assert.equal(candidates.templateGuids.length, PREORDER_LIVE_MAX_TEMPLATES)
  assert.equal(candidates.truncated, true, '超过上限要标记为不完整')
}

// 并发上限：同时在跑的任务不超过 limit，单个失败不影响其他任务，结果按输入顺序返回。
{
  let running = 0
  let peak = 0
  const results = await runWithConcurrency([1, 2, 3, 4, 5, 6, 7], 3, async (value) => {
    running += 1
    peak = Math.max(peak, running)
    await new Promise((resolve) => setTimeout(resolve, value % 3))
    running -= 1
    if (value === 4) throw new Error('boom')
    return value * 10
  })
  assert.equal(peak, 3)
  assert.deepEqual(results.map((result) => (result.status === 'fulfilled' ? result.value : 'failed')), [10, 20, 30, 'failed', 50, 60, 70])
  assert.deepEqual(await runWithConcurrency([], 4, async () => 1), [])
}

// 日历天差按本地日期算，不按 24 小时算。
assert.equal(getCalendarDayDiff(new Date(2026, 9, 6, 23, 0).getTime(), new Date(2026, 9, 7, 1, 0).getTime()), 1)
assert.equal(getCalendarDayDiff(now, new Date(2026, 9, 6, 18, 0).getTime()), 0)

// 进行中：剩 2 天紧急、剩 5 天不紧急、当天截止紧急。
assert.deepEqual(getActivationTiming(activation({}), now), { kind: 'closesIn', days: 2, urgent: true })
assert.deepEqual(
  getActivationTiming(activation({ endAtUtc: new Date(2026, 9, 11, 18, 0).toISOString() }), now),
  { kind: 'closesIn', days: 5, urgent: false },
)
assert.deepEqual(
  getActivationTiming(activation({ endAtUtc: new Date(2026, 9, 6, 18, 0).toISOString() }), now),
  { kind: 'closesToday', urgent: true },
)
// 待开始：为期按日历天，至少 1 天。
assert.deepEqual(
  getActivationTiming(activation({
    status: 'Scheduled',
    startAtUtc: new Date(2026, 9, 10, 9, 0).toISOString(),
    endAtUtc: new Date(2026, 9, 17, 18, 0).toISOString(),
  }), now),
  { kind: 'starts', durationDays: 7, urgent: false },
)
assert.deepEqual(
  getActivationTiming(activation({
    status: 'Scheduled',
    startAtUtc: new Date(2026, 9, 10, 9, 0).toISOString(),
    endAtUtc: new Date(2026, 9, 10, 18, 0).toISOString(),
  }), now),
  { kind: 'starts', durationDays: 1, urgent: false },
)
assert.deepEqual(getActivationTiming(activation({ status: 'Closed' }), now), { kind: 'none', urgent: false })
assert.deepEqual(getActivationTiming(activation({ endAtUtc: 'bad' }), now), { kind: 'none', urgent: false })

// 进度：只拆已处理 / 未提交，异常值收敛到 [0, target]。
assert.deepEqual(getActivationProgress(activation({})), { target: 26, responded: 18, pending: 8, respondedPercent: 69.2 })
assert.deepEqual(getActivationProgress(activation({ targetStoreCount: 0, pendingCount: 0 })), { target: 0, responded: 0, pending: 0, respondedPercent: 0 })
assert.deepEqual(getActivationProgress(activation({ targetStoreCount: 5, pendingCount: 9 })), { target: 5, responded: 0, pending: 5, respondedPercent: 0 })

// 当前批次：进行中优先（最早截止），没有进行中再取最早开始的待开始批次。
{
  const scheduled = activation({ activationGuid: 's', status: 'Scheduled', startAtUtc: new Date(2026, 9, 12).toISOString() })
  const laterActive = activation({ activationGuid: 'a2', endAtUtc: new Date(2026, 9, 20).toISOString() })
  const soonerActive = activation({ activationGuid: 'a1', endAtUtc: new Date(2026, 9, 9).toISOString() })
  const closed = activation({ activationGuid: 'c', status: 'Closed' })
  assert.equal(pickCurrentActivation([scheduled, laterActive, closed, soonerActive])?.activationGuid, 'a1')
  assert.equal(pickCurrentActivation([closed, scheduled])?.activationGuid, 's')
  assert.equal(pickCurrentActivation([closed]), undefined)

  const live = collectLiveActivations({
    t1: [scheduled, closed, laterActive],
    t2: [soonerActive, soonerActive],
  })
  assert.deepEqual(live.map((item) => item.activationGuid), ['a1', 'a2', 's'], '进行中在前、按截止排序，待开始在后，并去重')
}

// 最近激活：摘要字段优先，否则取期号最大一期的开始时间。
{
  const summaryAt = new Date(2026, 8, 1).toISOString()
  assert.equal(resolveLatestActivationMs(template({ latestActivationAt: summaryAt })), Date.parse(summaryAt))
  const latestStart = new Date(2026, 9, 1, 9, 0).toISOString()
  assert.equal(
    resolveLatestActivationMs(template({}), [
      activation({ sequenceNumber: 1, startAtUtc: new Date(2026, 7, 1).toISOString() }),
      activation({ sequenceNumber: 3, startAtUtc: latestStart }),
    ]),
    Date.parse(latestStart),
  )
  assert.equal(resolveLatestActivationMs(template({}), []), undefined)
  assert.equal(resolveLatestActivationMs(template({})), undefined)
}

// 模板过滤与计数。
{
  const rows = [
    template({ templateGuid: '1', name: 'Christmas 圣诞装饰', isEnabled: true }),
    template({ templateGuid: '2', name: '农历新年', isEnabled: true }),
    template({ templateGuid: '3', name: 'christmas lights', isEnabled: false }),
  ]
  assert.deepEqual(countTemplatesByStatus(rows), { all: 3, enabled: 2, disabled: 1 })
  assert.deepEqual(filterTemplates(rows, '  CHRISTMAS ', 'all').map((row) => row.templateGuid), ['1', '3'])
  assert.deepEqual(filterTemplates(rows, 'christmas', 'enabled').map((row) => row.templateGuid), ['1'])
  assert.deepEqual(filterTemplates(rows, '', 'disabled').map((row) => row.templateGuid), ['3'])
}

// 日期显示：同年省略年份，跨年带年份；星期按语言。
assert.equal(formatMonthDayWeekdayTime(new Date(2026, 9, 8, 18, 0).getTime(), 'en-US'), '10-08 Thu 18:00')
assert.equal(formatMonthDayWeekdayTime(new Date(2026, 9, 8, 18, 0).getTime(), 'zh-CN'), '10-08 周四 18:00')
assert.equal(formatCompactDate(new Date(2026, 9, 1).getTime(), now), '10-01')
assert.equal(formatCompactDate(new Date(2025, 0, 8).getTime(), now), '2025-01-08')
assert.equal(formatCompactDateTime(new Date(2026, 8, 30, 16, 12).getTime(), now), '09-30 16:12')
assert.equal(formatCompactDateTime(new Date(2025, 3, 1, 9, 0).getTime(), now), '2025-04-01')

console.log('preordersPage.logic.test: ok')
