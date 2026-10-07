import dayjs from 'dayjs'
import type { ContainerMain } from '../../../types/container'
import {
  CONTAINER_STATUS_TAB_STATUSES,
  DEFAULT_CONTAINER_STATUS_TAB,
  buildArrivalCalendar,
  buildStatusTabCounts,
  describeEtaHint,
  formatContainerDate,
  getIsoWeekNumber,
  getLoadRatePercent,
  parseContainerDate,
  summarizeContainers,
} from './containersLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${message}\n实际: ${JSON.stringify(actual)}\n预期: ${JSON.stringify(expected)}`)
  }
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

// 2026-10-06 是周二，ISO 第 41 周（周一 10-05 ~ 周日 10-11）。
const today = dayjs('2026-10-06')

function container(patch: Partial<ContainerMain>): ContainerMain {
  return { id: 1, hguid: patch.货柜编号 ?? 'guid', ...patch }
}

async function main() {
  const failures: string[] = []

  const push = (failure: string | null) => {
    if (failure) failures.push(failure)
  }

  push(await runTest('状态页签映射到接口 Statuses，默认未完成 = 已装柜 + 运输中', () => {
    assertEqual(DEFAULT_CONTAINER_STATUS_TAB, 'open', '默认页签应为未完成')
    assertEqual(CONTAINER_STATUS_TAB_STATUSES.open, [0, 1], '未完成应过滤 0/1')
    assertEqual(CONTAINER_STATUS_TAB_STATUSES.cancelled, [7], '已取消应过滤 7')
    assert(CONTAINER_STATUS_TAB_STATUSES.all === undefined, '全部不应传 Statuses')
    assertEqual(
      buildStatusTabCounts({ loaded: 3, inTransit: 4, completed: 175, cancelled: 4, all: 186 }),
      { open: 7, loaded: 3, inTransit: 4, completed: 175, cancelled: 4, all: 186 },
      '未完成计数应由已装柜与运输中相加',
    )
  }))

  push(await runTest('日期解析截取日期部分，ISO 周号以周一为周首且跨年正确', () => {
    assertEqual(formatContainerDate('2026-10-27T00:00:00'), '2026-10-27', '应只取日期部分')
    assertEqual(formatContainerDate('2026-10-27T00:00:00Z'), '2026-10-27', '带 Z 的值不应被换算到前一天')
    assert(parseContainerDate('') === undefined && parseContainerDate('not-a-date') === undefined, '空值和无效值应返回 undefined')
    assertEqual(getIsoWeekNumber(dayjs('2026-10-05')), 41, '周一应属于本周')
    assertEqual(getIsoWeekNumber(dayjs('2026-10-11')), 41, '周日仍属于本周（周一为周首）')
    assertEqual(getIsoWeekNumber(dayjs('2026-10-12')), 42, '下周一进入下一周')
    assertEqual(getIsoWeekNumber(dayjs('2026-12-31')), 53, '2026-12-31 属于 ISO 第 53 周')
    assertEqual(getIsoWeekNumber(dayjs('2027-01-01')), 53, '2027-01-01 仍属于 2026 年 ISO 第 53 周')
  }))

  push(await runTest('预计到岸提示：只对未完成且未到货的货柜提示逾期/今天/还有 N 天', () => {
    const open = (eta: string) => container({ 状态: 1, 预计到岸日期: eta })
    assertEqual(describeEtaHint(open('2026-09-29T00:00:00'), today), { kind: 'overdue', days: 7 }, '早于今天应逾期')
    assertEqual(describeEtaHint(open('2026-10-06T00:00:00'), today), { kind: 'today' }, '当天应提示今天到岸')
    assertEqual(describeEtaHint(open('2026-10-10T00:00:00'), today), { kind: 'soon', days: 4 }, '7 天内提示还有 N 天')
    assertEqual(describeEtaHint(open('2026-10-19T00:00:00'), today), { kind: 'upcoming', days: 13, week: 43 }, '两周内提示周号与天数')
    assertEqual(describeEtaHint(open('2026-10-30T00:00:00'), today), { kind: 'week', week: 44 }, '更远只显示周号')
    assertEqual(
      describeEtaHint(container({ 状态: 2, 预计到岸日期: '2026-09-01T00:00:00' }), today),
      { kind: 'week', week: 36 },
      '已完成的历史货柜不能标逾期',
    )
    assertEqual(
      describeEtaHint(container({ 状态: 1, 预计到岸日期: '2026-09-01T00:00:00', 实际到货日期: '2026-09-03T00:00:00' }), today),
      { kind: 'week', week: 36 },
      '已有实际到货日期的不能标逾期',
    )
    assertEqual(describeEtaHint(container({ 状态: 0 }), today), { kind: 'none' }, '没有预计日期时不提示')
  }))

  push(await runTest('装载率按 68 m³ 标准柜计算，缺失体积不显示', () => {
    assertEqual(getLoadRatePercent(63.6), 94, '63.6 m³ 应约为 94%')
    assertEqual(getLoadRatePercent(69.578), 102, '超过标准柜照实显示')
    assertEqual(getLoadRatePercent(0), 0, '0 体积为 0%')
    assert(getLoadRatePercent(undefined) === undefined && getLoadRatePercent(null) === undefined, '缺失体积不应算成 0%')
  }))

  push(await runTest('合计只汇总传入的货柜（本页或勾选），空值按 0 计', () => {
    const totals = summarizeContainers([
      container({ 合计件数: 1186, 合计金额: 428640, 总体积: 63.6 }),
      container({ 合计件数: 1286, 总体积: 66.8 }),
      container({}),
    ])
    assertEqual(
      { ...totals, volume: Number(totals.volume.toFixed(2)) },
      { count: 3, pieces: 2472, amount: 428640, volume: 130.4 },
      '合计应逐项相加',
    )
  }))

  push(await runTest('到岸周历按预计到岸分到已逾期、本周与之后 5 周', () => {
    const calendar = buildArrivalCalendar(
      [
        container({ 货柜编号: 'OVERDUE', 状态: 1, 预计到岸日期: '2026-09-29T00:00:00', 总体积: 63.6 }),
        container({ 货柜编号: 'MONDAY-PAST', 状态: 1, 预计到岸日期: '2026-10-05T00:00:00', 总体积: 10 }),
        container({ 货柜编号: 'TODAY', 状态: 1, 预计到岸日期: '2026-10-06T00:00:00', 总体积: 66.8 }),
        container({ 货柜编号: 'SUNDAY', 状态: 0, 预计到岸日期: '2026-10-11T00:00:00', 总体积: 64.2 }),
        container({ 货柜编号: 'W43', 状态: 1, 预计到岸日期: '2026-10-19T00:00:00', 总体积: 67.9 }),
        container({ 货柜编号: 'W46-LAST', 状态: 0, 预计到岸日期: '2026-11-15T00:00:00', 总体积: 60 }),
        container({ 货柜编号: 'TOO-LATE', 状态: 0, 预计到岸日期: '2026-11-16T00:00:00' }),
        container({ 货柜编号: 'NO-ETA', 状态: 0 }),
        container({ 货柜编号: 'ARRIVED', 状态: 1, 预计到岸日期: '2026-10-07T00:00:00', 实际到货日期: '2026-10-06T00:00:00' }),
        container({ 货柜编号: 'DONE', 状态: 2, 预计到岸日期: '2026-10-07T00:00:00' }),
        container({ 货柜编号: 'CANCELLED', 状态: 7, 预计到岸日期: '2026-10-07T00:00:00' }),
      ],
      today,
    )

    assertEqual(calendar.columns.length, 7, '应有 7 列')
    const [overdue, thisWeek, w42, w43, , , w46] = calendar.columns
    assertEqual(overdue.items.map((item) => item.container.货柜编号), ['OVERDUE', 'MONDAY-PAST'], '早于今天（含本周已过去的日子）归已逾期')
    assertEqual(thisWeek.week, 41, '第二列是本周')
    assert(thisWeek.isCurrentWeek && !w42.isCurrentWeek, '只有本周列标记为本周')
    assertEqual(thisWeek.start?.format('MM-DD'), '10-05', '本周从周一开始')
    assertEqual(thisWeek.end?.format('MM-DD'), '10-11', '本周到周日结束')
    assertEqual(thisWeek.items.map((item) => item.container.货柜编号), ['TODAY', 'SUNDAY'], '今天到周日归本周')
    assertEqual(Number(thisWeek.totalVolume.toFixed(1)), 131, '本周体积合计')
    assertEqual(w42.items.length, 0, '没有到岸的周为空')
    assertEqual(w43.items.map((item) => item.container.货柜编号), ['W43'], 'W43 应包含 10-19')
    assertEqual(w46.week, 46, '最后一列是 W46')
    assertEqual(w46.items.map((item) => item.container.货柜编号), ['W46-LAST'], '最后一列包含其周日')
    assertEqual(calendar.outsideCount, 2, '晚于最后一列和没有预计日期的计入列外提示')
    assertEqual(calendar.lastDay.format('YYYY-MM-DD'), '2026-11-15', '列外提示的截止日')
  }))

  push(await runTest('到岸周历跨年时周号按 ISO 计算', () => {
    const calendar = buildArrivalCalendar(
      [container({ 货柜编号: 'NEW-YEAR', 状态: 1, 预计到岸日期: '2027-01-04T00:00:00' })],
      dayjs('2026-12-30'),
    )
    assertEqual(calendar.columns.slice(1).map((column) => column.week), [53, 1, 2, 3, 4, 5], '跨年周号应从 53 接到 1')
    assertEqual(calendar.columns[2].items.length, 1, '2027-01-04 应落在 W1')
  }))

  if (failures.length) throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  console.log('containersLogic.test: ok')
}

await main()
