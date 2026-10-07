import {
  buildGradeCountPlan,
  collectGradeCounts,
  formatPriceRangeSummary,
  getGradeWarehouseMismatch,
  gradeFilterToTab,
  gradeTabToFilter,
  hasNonGradeFilters,
  isGradeKey,
} from './productGradeView'

function assertEqual<T>(actual: T, expected: T, message: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${message}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

// 等级页签与接口参数互转。
assertEqual(gradeTabToFilter('all'), undefined, '「全部」页签不带等级参数')
assertEqual(gradeTabToFilter('C'), 'C', '等级页签直接作为 grade 参数')
assertEqual(gradeFilterToTab(undefined), 'all', '没有等级条件时落在「全部」')
assertEqual(gradeFilterToTab('B'), 'B', '已有等级条件时选中对应页签')
assertEqual(gradeFilterToTab('E'), 'all', '后端扩展的非常规等级不对应任何页签')
assertEqual(isGradeKey('A'), true, 'A 是常规等级')
assertEqual(isGradeKey('a'), false, '等级区分大小写')

// 等级与仓库状态不一致。
assertEqual(getGradeWarehouseMismatch('A', false), 'coreDelisted', 'A 核心却下架应标记')
assertEqual(getGradeWarehouseMismatch('B', false), 'coreDelisted', 'B 观察却下架应标记')
assertEqual(getGradeWarehouseMismatch('D', true), 'noStockListed', 'D 无货却上架应标记')
assertEqual(getGradeWarehouseMismatch('A', true), null, 'A 上架是正常状态')
assertEqual(getGradeWarehouseMismatch('D', false), null, 'D 下架是正常状态')
assertEqual(getGradeWarehouseMismatch('C', true), null, 'C 有货上架不算不一致')
assertEqual(getGradeWarehouseMismatch('C', false), null, 'C 有货下架不算不一致')
assertEqual(getGradeWarehouseMismatch('A', null), null, '仓库状态未知时不判定')
assertEqual(getGradeWarehouseMismatch('D', undefined), null, '没有仓库状态字段时不判定')

// 是否有除等级外的筛选。
assertEqual(hasNonGradeFilters({}, ''), false, '无条件')
assertEqual(hasNonGradeFilters({ grade: 'A' }, '  '), false, '只有等级（页签）和空白关键词不算筛选')
assertEqual(hasNonGradeFilters({ grade: 'A' }, 'HB1'), true, '关键词算筛选')
assertEqual(hasNonGradeFilters({ supplierCode: 'XD0213' }), true, '供应商算筛选')
assertEqual(hasNonGradeFilters({ warehouseIsActive: false }), true, '仓库状态为「下架」也是有效筛选')
assertEqual(hasNonGradeFilters({ uncategorizedOnly: true }), true, '只看未分类算筛选')
assertEqual(hasNonGradeFilters({ domesticPriceMin: 0 }), true, '价格下限为 0 也是有效筛选')
assertEqual(hasNonGradeFilters({ categoryGuid: undefined, hbProductNo: '' }), false, '空值不算筛选')

// 计数请求计划：5 个页签 + 有筛选时多一条不带筛选的总数。
assertEqual(
  buildGradeCountPlan(false).map((item) => [item.key, item.grade ?? null, item.withFilters]),
  [
    ['all', null, true],
    ['A', 'A', true],
    ['B', 'B', true],
    ['C', 'C', true],
    ['D', 'D', true],
  ],
  '无筛选时只发 5 条页签计数',
)
const filteredPlan = buildGradeCountPlan(true)
assertEqual(filteredPlan.length, 6, '有筛选时多发一条总数请求')
assertEqual(
  [filteredPlan[5].key, filteredPlan[5].grade ?? null, filteredPlan[5].withFilters],
  ['graded', null, false],
  '页头已分级总数不带任何筛选',
)

// 汇总计数：失败的不给数；无筛选时总数取「全部」页签。
const plain = buildGradeCountPlan(false)
assertEqual(
  collectGradeCounts(plain, [
    { status: 'fulfilled', value: { total: 1862 } },
    { status: 'fulfilled', value: { total: 412 } },
    { status: 'rejected', reason: new Error('timeout') },
    { status: 'fulfilled', value: { total: 498 } },
    { status: 'fulfilled', value: { total: 0 } },
  ]),
  { all: 1862, A: 412, C: 498, D: 0, graded: 1862 },
  '失败的页签不显示计数，0 照常显示',
)
assertEqual(
  collectGradeCounts(filteredPlan, [
    { status: 'fulfilled', value: { total: 12 } },
    { status: 'fulfilled', value: { total: 3 } },
    { status: 'fulfilled', value: { total: 4 } },
    { status: 'fulfilled', value: { total: 5 } },
    { status: 'fulfilled', value: { total: 0 } },
    { status: 'fulfilled', value: { total: 1862 } },
  ]),
  { all: 12, A: 3, B: 4, C: 5, D: 0, graded: 1862 },
  '有筛选时总数取不带筛选的那条，不能用筛选后的「全部」',
)
assertEqual(
  collectGradeCounts(filteredPlan, [
    { status: 'fulfilled', value: { total: 12 } },
    { status: 'rejected', reason: 'x' },
    { status: 'rejected', reason: 'x' },
    { status: 'rejected', reason: 'x' },
    { status: 'rejected', reason: 'x' },
    { status: 'rejected', reason: 'x' },
  ]),
  { all: 12 },
  '总数请求失败时不能退回用筛选后的数',
)

// 价格区间摘要。
assertEqual(formatPriceRangeSummary(1, 5), '1.00 – 5.00', '双侧区间')
assertEqual(formatPriceRangeSummary(0, undefined), '≥ 0.00', '只有下限（含 0）')
assertEqual(formatPriceRangeSummary(undefined, 9.5), '≤ 9.50', '只有上限')
assertEqual(formatPriceRangeSummary(undefined, undefined), undefined, '无区间')

console.log('productGradeView.test: ok')
