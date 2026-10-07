import type { LocationItem } from '../../../types/location'
import { buildLocationFilterQuery } from './columnFilters'
import {
  EMPTY_LOCATION_FILTERS,
  OTHER_ZONE_KEY,
  buildLocationListColumnFilters,
  buildZoneRack,
  collectLocationDistribution,
  countMoreLocationFilters,
  getRackCellState,
  getUsageRatePercent,
  parseLocationCode,
  summarizeLocationZones,
  toDistributionEntry,
  type LocationDistributionEntry,
} from './locationsLogic'

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

function entry(code: string, used: boolean, enabled = true): LocationDistributionEntry {
  return { code, used, enabled }
}

async function main() {
  const failures: string[] = []
  const push = (failure: string | null) => {
    if (failure) failures.push(failure)
  }

  push(await runTest('货位编码按 区-排-列-层 解析，不符合格式的返回 undefined', () => {
    assertEqual(
      parseLocationCode(' a-03-12-02 '),
      { zone: 'A', rowText: '03', row: 3, columnText: '12', column: 12, levelText: '02', level: 2 },
      '应解析出区、排、列、层，区统一大写',
    )
    assertEqual(parseLocationCode('B1-1-2-3')?.zone, 'B1', '区可以带数字')
    for (const bad of ['A-03-12', 'A-03-12-02-01', 'A-0X-12-02', '', undefined, null, 'A 03 12 02']) {
      assert(parseLocationCode(bad) === undefined, `不符合格式应归其他：${String(bad)}`)
    }
  }))

  push(await runTest('分布条目只保留编码、启用与是否有货', () => {
    const item: LocationItem = { locationGuid: 'g1', locationCode: ' A-01-01-01 ', status: 1, products: [{ productCode: 'P1' }] }
    assertEqual(toDistributionEntry(item), { code: 'A-01-01-01', enabled: true, used: true }, '启用且有商品')
    assertEqual(
      toDistributionEntry({ locationGuid: 'g2', locationCode: 'A-01-01-02', status: null, products: [] }),
      { code: 'A-01-01-02', enabled: false, used: false },
      'status 不是 1 视为停用（与列表口径一致），无商品为空位',
    )
  }))

  push(await runTest('按区汇总货位数、已用、停用，其他放最后', () => {
    const { all, zones } = summarizeLocationZones([
      entry('B-01-01-01', true),
      entry('A-01-01-01', true),
      entry('A-01-01-02', false),
      entry('A-01-02-01', false, false),
      entry('A10-01-01-01', true),
      entry('A2-01-01-01', false),
      entry('BAD', true),
    ])
    assertEqual(all, { key: 'all', total: 7, used: 4, disabled: 1 }, '全部汇总')
    assertEqual(zones.map((zone) => zone.key), ['A', 'A2', 'A10', 'B', OTHER_ZONE_KEY], '区按自然顺序排列，其他在最后')
    assertEqual(zones[0], { key: 'A', total: 3, used: 1, disabled: 1 }, 'A 区汇总')
    assertEqual(getUsageRatePercent(zones[0]), 33, '已用率四舍五入')
    assertEqual(getUsageRatePercent({ total: 0, used: 0 }), 0, '空区为 0%')
  }))

  push(await runTest('货架格按启用层判断全满/部分空/整列空/停用', () => {
    assertEqual(getRackCellState(3, 3), 'full', '启用层都有货')
    assertEqual(getRackCellState(3, 1), 'partial', '部分启用层空')
    assertEqual(getRackCellState(3, 0), 'empty', '启用层都没货')
    assertEqual(getRackCellState(0, 0), 'off', '整列停用')
  }))

  push(await runTest('货架图：行=排、格=列，缺列补空位，停用层不计入是否满', () => {
    const rack = buildZoneRack(
      [
        entry('A-03-12-01', true),
        entry('A-03-12-02', true),
        entry('A-03-12-03', false, false),
        entry('A-03-13-01', true),
        entry('A-03-13-02', false),
        entry('A-03-15-01', false),
        entry('A-01-12-01', false, false),
        entry('B-01-01-01', true),
        entry('junk', true),
      ],
      'A',
    )
    assert(rack, '应生成 A 区货架图')
    assertEqual(rack.columns, [12, 13, 14, 15], '列号补齐为连续区间')
    assertEqual(rack.rows.map((row) => row.rowText), ['01', '03'], '排按数字升序，保留原始编码文本用于前缀筛选')
    const row03 = rack.rows[1]
    assertEqual(
      row03.cells.map((cell) => cell?.state ?? null),
      ['full', 'partial', null, 'empty'],
      '12 列启用层都有货（停用层不算空）、13 列部分空、14 列不存在、15 列整列空',
    )
    assertEqual(row03.cells[0]?.disabledLevels, 1, '应记录停用层数')
    assertEqual(rack.rows[0].cells[0]?.state, 'off', '整列停用')
    assert(buildZoneRack([entry('B-01-01-01', true)], 'A') === undefined, '没有该区货位时返回 undefined')
  }))

  push(await runTest('工具栏状态转换为接口 filters：区/排前缀与货位代码搜索同时生效', () => {
    const filters = buildLocationListColumnFilters({
      ...EMPTY_LOCATION_FILTERS,
      keyword: ' 12 ',
      zone: 'A',
      zoneRow: '03',
      locationType: 1,
      usage: false,
      status: 0,
      updatedBy: ' Sean ',
      updatedAtFrom: '2026-10-01',
      updatedAtTo: '2026-10-05',
    })
    assertEqual(filters.locationCode, ['__filter:starts:A-03-', '__filter:contains:12'], '区排前缀与关键字都应落在 locationCode')
    const query = buildLocationFilterQuery(filters)
    assertEqual(query.isUsed, false, '空位应映射到顶层 IsUsed=false')
    assertEqual(query.filters?.locationType, ['1'], '拣货位发送 1')
    assertEqual(query.filters?.status, ['0'], '停用发送 0')
    assertEqual(query.filters?.updatedBy, ['__filter:contains:Sean'], '更新人按包含')
    assertEqual(query.filters?.updatedAt, ['gte:2026-10-01', 'lte:2026-10-05'], '更新时间按区间')
    assert(!query.filters?.usage, 'usage 不能重复进入 filters')
  }))

  push(await runTest('按其他字段搜索时关键字落到对应字段，匹配方式沿用列头 token', () => {
    const filters = buildLocationListColumnFilters({
      ...EMPTY_LOCATION_FILTERS,
      searchField: 'productItemNumber',
      searchMode: 'starts',
      keyword: 'HB31',
      zone: 'B',
    })
    assertEqual(filters.locationCode, ['__filter:starts:B-'], '只有区前缀留在 locationCode')
    assertEqual(filters.productItemNumber, ['__filter:starts:HB31'], '货号按开头是')
    assertEqual(buildLocationListColumnFilters(EMPTY_LOCATION_FILTERS), {}, '空状态不应生成任何 filters，便于清空')
    assertEqual(countMoreLocationFilters({ ...EMPTY_LOCATION_FILTERS, status: 1, searchMode: 'eq', updatedAtTo: '2026-10-01' }), 3, '更多筛选角标计数')
  }))

  push(await runTest('分布统计按页顺序读取、去重，超过上限标记截断，取消后停止', async () => {
    const makeItems = (from: number, count: number): LocationItem[] =>
      Array.from({ length: count }, (_, index) => ({
        locationGuid: `g${from + index}`,
        locationCode: `A-01-01-${String(from + index).padStart(2, '0')}`,
        status: 1,
        products: (from + index) % 2 ? [{ productCode: 'P' }] : [],
      }))
    const requested: number[] = []
    const progress: number[] = []
    const result = await collectLocationDistribution(
      async (pageNumber, pageSize) => {
        requested.push(pageNumber)
        const start = (pageNumber - 1) * pageSize
        // 第 2 页与第 1 页重叠 1 条，模拟翻页期间数据移动。
        return { items: makeItems(pageNumber === 2 ? start - 1 : start, pageNumber === 3 ? 1 : pageSize), total: 7 }
      },
      { pageSize: 3, onProgress: (loaded) => progress.push(loaded) },
    )
    assert(result, '应返回结果')
    assertEqual(requested, [1, 2, 3], '按页顺序读取到总数为止')
    assertEqual(result.entries.length, 6, '重叠的条目按 locationGuid 去重')
    assertEqual(result.total, 7, '总数取接口 total')
    assertEqual(result.truncated, false, '未超过上限')
    assertEqual(progress, [3, 5, 6], '每页回报进度')

    const capped = await collectLocationDistribution(async () => ({ items: makeItems(0, 2), total: 10 }), { pageSize: 2, maxPages: 2 })
    assertEqual(capped?.truncated, true, '超过页数上限应标记截断')

    let calls = 0
    const cancelled = await collectLocationDistribution(
      async () => {
        calls += 1
        return { items: makeItems(0, 2), total: 10 }
      },
      { pageSize: 2, isCancelled: () => calls >= 1 },
    )
    assert(cancelled === undefined && calls === 1, '取消后应立即停止，不再请求后续页')
  }))

  if (failures.length) throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  console.log('locationsLogic.test: ok')
}

await main()
