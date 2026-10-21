import { readFileSync } from 'node:fs'
import type { ContainerDetail } from '../../../types/container'
import {
  CONTAINER_DETAIL_COLUMN_PASTE_KEYS,
  buildContainerDetailColumnPastePlan,
  buildContainerDetailFillPlan,
  getContainerDetailColumnPasteCurrentValue,
  isContainerDetailColumnEditableForRow,
  resolveContainerDetailFillTargetRows,
  parseContainerDetailColumnPasteText,
  parseContainerDetailColumnPasteValue,
} from './containerDetailColumnPaste'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}\nexpected: ${String(expected)}\nactual: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}\nexpected: ${expectedJson}\nactual: ${actualJson}`)
  }
}

function makeRow(overrides: Partial<ContainerDetail> & { id: number }): ContainerDetail {
  return {
    hguid: `hguid-${overrides.id}`,
    是否新商品: true,
    ...overrides,
  } as ContainerDetail
}

const rowKey = (row: ContainerDetail) => row.hguid || String(row.id)

// ---- 剪贴板文本解析 ----

assertDeepEqual(
  parseContainerDetailColumnPasteText('12.5\r\n\r\n$ 13,000.25\r\n\r\n'),
  { ok: true, values: ['12.5', '', '$ 13,000.25'] },
  '单列文本应保留中间空单元格并去掉末尾空行',
)

assertDeepEqual(
  parseContainerDetailColumnPasteText('1\t2\n3\t4\n'),
  { ok: false, reason: 'multiple_columns' },
  '多列数据必须拒绝，避免错位写入',
)

assertDeepEqual(
  parseContainerDetailColumnPasteText('1\t\n2\t\n'),
  { ok: true, values: ['1', '2'] },
  '尾随空制表符不应被当作多列',
)

assertDeepEqual(
  parseContainerDetailColumnPasteText('\n\n'),
  { ok: false, reason: 'empty' },
  '只有空行时应返回 empty',
)

assertDeepEqual(
  parseContainerDetailColumnPasteText('"Xmas Water Lantern\nChurch"\nSanta\n'),
  { ok: true, values: ['Xmas Water Lantern\nChurch', 'Santa'] },
  'Excel 引号包裹的单元格内换行应保留在同一个值内',
)

// ---- 单值解析 ----

assertDeepEqual(parseContainerDetailColumnPasteValue('importPrice', '$ 21.239'), { ok: true, value: 21.24 }, '进口价格应剥离货币符号并保留两位小数')
assertDeepEqual(parseContainerDetailColumnPasteValue('oemPrice', '1,299.5'), { ok: true, value: 1299.5 }, '零售价应剥离千分位')
assertDeepEqual(parseContainerDetailColumnPasteValue('oemPrice', '-3'), { ok: false }, '负数零售价应视为无效')
assertDeepEqual(parseContainerDetailColumnPasteValue('unitVolume', '0.12345'), { ok: true, value: 0.123 }, '单件体积保留三位小数')
assertDeepEqual(parseContainerDetailColumnPasteValue('packingQuantity', '12.0'), { ok: true, value: 12 }, '整数列接受 12.0 这类 Excel 输出')
assertDeepEqual(parseContainerDetailColumnPasteValue('packingQuantity', '12.5'), { ok: false }, '整数列拒绝小数')
assertDeepEqual(parseContainerDetailColumnPasteValue('middlePackQuantity', 'abc'), { ok: false }, '非数字应视为无效')
assertDeepEqual(parseContainerDetailColumnPasteValue('floatRate', '1.3'), { ok: true, value: 1.3 }, '调整浮率应按两位小数解析')
assertDeepEqual(parseContainerDetailColumnPasteValue('englishName', 'Xmas Lantern'), { ok: true, value: 'Xmas Lantern' }, '英文名称接受纯英文')
assertDeepEqual(parseContainerDetailColumnPasteValue('englishName', 'Xmas 灯笼'), { ok: false }, '英文名称含中文应视为无效')
assertDeepEqual(parseContainerDetailColumnPasteValue('remark', '  易碎 '), { ok: true, value: '易碎' }, '备注应去除首尾空白')
assertDeepEqual(parseContainerDetailColumnPasteValue('remark', '   '), { ok: false }, '空白文本不应生成值')

// ---- 当前值读取 ----

assertEqual(
  getContainerDetailColumnPasteCurrentValue('oemPrice', makeRow({ id: 1, 是否新商品: false, 贴牌价格: 10, warehouseOEMPrice: 12 })),
  10,
  '「本次零售价」当前值取货柜明细自己的价格，不再绑定仓库实时价',
)
assertEqual(
  getContainerDetailColumnPasteCurrentValue('lastOEMPrice', makeRow({ id: 1, 是否新商品: false, 贴牌价格: 10, warehouseOEMPrice: 12 })),
  12,
  '「已有零售价」当前值取仓库实时价',
)
assertEqual(
  getContainerDetailColumnPasteCurrentValue('warehouseImportPrice', makeRow({ id: 1, 是否新商品: false, 进口价格: 3, warehouseImportPrice: 4 })),
  4,
  '「已有进口价」当前值取仓库实时进货价',
)
assertDeepEqual(
  parseContainerDetailColumnPasteValue('lastOEMPrice', '$2.5'),
  { ok: true, value: 2.5 },
  '已有零售价列应按两位小数解析并剥离货币符号',
)
assertDeepEqual(
  [
    isContainerDetailColumnEditableForRow('importPrice', makeRow({ id: 1, 是否新商品: true })),
    isContainerDetailColumnEditableForRow('importPrice', makeRow({ id: 2, 是否新商品: false })),
    isContainerDetailColumnEditableForRow('oemPrice', makeRow({ id: 3, 是否新商品: false })),
    isContainerDetailColumnEditableForRow('warehouseImportPrice', makeRow({ id: 4, 是否新商品: false })),
    isContainerDetailColumnEditableForRow('lastOEMPrice', makeRow({ id: 5, 是否新商品: true })),
    isContainerDetailColumnEditableForRow('remark', makeRow({ id: 6, 是否新商品: false })),
  ],
  [true, false, false, true, false, true],
  '本次价只有新商品可改，已有价只有已有商品可改，其余列不受限',
)
assertEqual(
  getContainerDetailColumnPasteCurrentValue('englishName', makeRow({ id: 2, 商品信息: { 英文名称: 'Santa' } as ContainerDetail['商品信息'] })),
  'Santa',
  '英文名称当前值应回退到商品信息',
)

// ---- 粘贴计划 ----

const rows = [
  makeRow({ id: 1, 进口价格: 21.24 }),
  makeRow({ id: 2, 进口价格: 18.69 }),
  makeRow({ id: 3, 进口价格: 18.69 }),
  makeRow({ id: 4, 进口价格: 32.44 }),
  makeRow({ id: 5, hguid: undefined, 进口价格: 70.61 }),
]

const plan = buildContainerDetailColumnPastePlan({
  columnKey: 'importPrice',
  values: ['22', '', 'abc', '32.44', '71', '99'],
  rows,
  startRowKey: 'hguid-1',
  getRowKey: rowKey,
})
assertEqual(plan.error, undefined, '起始行存在时不应报错')
assertDeepEqual(
  plan.entries.map((entry) => [entry.row.hguid, entry.field, entry.value]),
  [['hguid-1', '进口价格', 22]],
  '只有有效且与当前值不同、并且已落库的行才生成补丁',
)
assertEqual(plan.appliedCount, 1, '应用数应为 1')
assertEqual(plan.skippedBlankCount, 1, '空单元格应跳过并计数')
assertEqual(plan.invalidCount, 2, '非法值与无 hguid 行都应计入无效')
assertDeepEqual(plan.invalidRowNumbers, [3, 5], '无效行号应对应粘贴数据中的行号')
assertEqual(plan.unchangedCount, 1, '与当前值相同的项应计入未变更')
assertEqual(plan.overflowCount, 1, '超出列表末尾的行应计数')

const middlePlan = buildContainerDetailColumnPastePlan({
  columnKey: 'remark',
  values: ['A', 'B'],
  rows,
  startRowKey: 'hguid-3',
  getRowKey: rowKey,
})
assertDeepEqual(
  middlePlan.entries.map((entry) => [entry.row.hguid, entry.value]),
  [['hguid-3', 'A'], ['hguid-4', 'B']],
  '应从起始行开始按显示顺序向下填充',
)

const missingPlan = buildContainerDetailColumnPastePlan({
  columnKey: 'remark',
  values: ['A'],
  rows,
  startRowKey: 'hguid-404',
  getRowKey: rowKey,
})
assertEqual(missingPlan.error, 'missing_target', '起始行不在列表中时应返回 missing_target')
assertEqual(missingPlan.entries.length, 0, '起始行缺失时不应生成补丁')

// ---- 已有价 / 本次价按商品类型跳过不可编辑行 ----

const mixedRows = [
  makeRow({ id: 21, 是否新商品: false, warehouseImportPrice: 5 }),
  makeRow({ id: 22, 是否新商品: true }),
  makeRow({ id: 23, 是否新商品: false, warehouseImportPrice: 6 }),
]
const warehousePlan = buildContainerDetailColumnPastePlan({
  columnKey: 'warehouseImportPrice',
  values: ['9', '9', '6'],
  rows: mixedRows,
  startRowKey: 'hguid-21',
  getRowKey: rowKey,
})
assertDeepEqual(
  warehousePlan.entries.map((entry) => [entry.row.hguid, entry.field, entry.value]),
  [['hguid-21', '仓库进口价', 9]],
  '已有进口价只给已有商品生成补丁，字段名是「仓库进口价」',
)
assertEqual(warehousePlan.notEditableCount, 1, '新商品行的已有价单元格应计入不可编辑并跳过')
assertEqual(warehousePlan.unchangedCount, 1, '与当前仓库价相同的行应计入未变更')
assertEqual(warehousePlan.invalidCount, 0, '不可编辑的行不算无效值')

// ---- 拖拽填充 ----

const fillRows = [
  makeRow({ id: 31, 备注: '易碎' }),
  makeRow({ id: 32 }),
  makeRow({ id: 33, 备注: '易碎' }),
  makeRow({ id: 34 }),
]
assertDeepEqual(
  resolveContainerDetailFillTargetRows({ rows: fillRows, sourceRowKey: 'hguid-31', endRowKey: 'hguid-33', getRowKey: rowKey }).map((row) => row.hguid),
  ['hguid-32', 'hguid-33'],
  '向下拖：目标是源行之后到终点行（含终点）',
)
assertDeepEqual(
  resolveContainerDetailFillTargetRows({ rows: fillRows, sourceRowKey: 'hguid-34', endRowKey: 'hguid-32', getRowKey: rowKey }).map((row) => row.hguid),
  ['hguid-33', 'hguid-32'],
  '向上拖：目标是源行之前到终点行（含终点），按离源行由近到远排序',
)
assertEqual(
  resolveContainerDetailFillTargetRows({ rows: fillRows, sourceRowKey: 'hguid-31', endRowKey: 'hguid-31', getRowKey: rowKey }).length,
  0,
  '终点就是源行时没有目标行',
)
assertEqual(
  resolveContainerDetailFillTargetRows({ rows: fillRows, sourceRowKey: 'hguid-31', endRowKey: 'missing', getRowKey: rowKey }).length,
  0,
  '终点不在列表里时没有目标行',
)
const fillPlan = buildContainerDetailFillPlan({
  columnKey: 'remark',
  sourceRow: fillRows[0],
  targetRows: resolveContainerDetailFillTargetRows({ rows: fillRows, sourceRowKey: 'hguid-31', endRowKey: 'hguid-34', getRowKey: rowKey }),
  getRowKey: rowKey,
})
assertDeepEqual(
  fillPlan.entries.map((entry) => [entry.row.hguid, entry.field, entry.value]),
  [['hguid-32', '备注', '易碎'], ['hguid-34', '备注', '易碎']],
  '源单元格的值复制到目标行，已经是同值的行不生成补丁',
)
assertEqual(fillPlan.unchangedCount, 1, '同值目标行计入未变更')
const blankSourceFill = buildContainerDetailFillPlan({
  columnKey: 'remark',
  sourceRow: fillRows[1],
  targetRows: [fillRows[2]],
  getRowKey: rowKey,
})
assertEqual(blankSourceFill.entries.length, 0, '源单元格没有值时不会清空目标单元格')
assertEqual(blankSourceFill.skippedBlankCount, 1, '源值为空按空单元格跳过')
const textFill = buildContainerDetailFillPlan({
  columnKey: 'englishName',
  sourceRow: makeRow({ id: 41, 英文名称: 'Santa Hat' }),
  targetRows: [makeRow({ id: 42 }), makeRow({ id: 43, 英文名称: 'Old' })],
  getRowKey: rowKey,
})
assertDeepEqual(
  textFill.entries.map((entry) => [entry.row.hguid, entry.field, entry.value]),
  [['hguid-42', '英文名称', 'Santa Hat'], ['hguid-43', '英文名称', 'Santa Hat']],
  '文本列（英文名称）同样可以拖拽填充',
)
const priceFillSkipsNotEditable = buildContainerDetailFillPlan({
  columnKey: 'lastOEMPrice',
  sourceRow: makeRow({ id: 51, 是否新商品: false, warehouseOEMPrice: 3.5 }),
  targetRows: [
    makeRow({ id: 52, 是否新商品: false, warehouseOEMPrice: 1 }),
    makeRow({ id: 53, 是否新商品: true }),
  ],
  getRowKey: rowKey,
})
assertDeepEqual(
  priceFillSkipsNotEditable.entries.map((entry) => [entry.row.hguid, entry.field, entry.value]),
  [['hguid-52', '仓库零售价', 3.5]],
  '已有零售价填充只写已有商品，新商品行被跳过',
)
assertEqual(priceFillSkipsNotEditable.notEditableCount, 1, '跳过的新商品行计入不可编辑')

// ---- 页面接线契约 ----

const pageSource = readFileSync('src/pages/Warehouse/ContainerDetail/index.tsx', 'utf8')
CONTAINER_DETAIL_COLUMN_PASTE_KEYS.forEach((columnKey) => {
  assert(
    pageSource.includes(`onPaste={(event) => handleEditableCellPaste(row, '${columnKey}', event)}`),
    `${columnKey} 列的可编辑单元格应接入列粘贴处理`,
  )
})
assert(
  pageSource.includes("if (parsed.values.length <= 1) return")
    && pageSource.includes('event.currentTarget.blur()')
    && pageSource.includes('applyContainerDetailColumnPaste(columnKey, parsed.values, rowKey(row))'),
  '单元格粘贴应只拦截多行数据，且先结束当前单元格编辑再批量写入',
)
assert(
  pageSource.includes("if (target.channel === 'pendingDraft') {")
    && pageSource.includes('markPendingDetailPatches(entries.map(')
    && pageSource.includes('enqueueAutoSavePatches(entries.map('),
  '列粘贴必须复用“保存明细”草稿与自动保存两条既有落库通道',
)
assert(
  pageSource.includes('const commitContainerDetailColumnPlan = (')
    && pageSource.includes("commitContainerDetailColumnPlan(columnKey, plan, 'paste')")
    && pageSource.includes("commitContainerDetailColumnPlan(columnKey, plan, 'fill')"),
  '粘贴与拖拽填充必须共用同一个提交函数，沿用草稿 / 自动保存两条既有通道',
)
assert(
  pageSource.includes('<ContainerDetailFillContext.Provider value={fillContextValue}>')
    && pageSource.includes('buildContainerDetailFillPlan({ columnKey, sourceRow, targetRows, getRowKey: rowKey })'),
  '页面应提供填充上下文，并用源单元格值生成填充计划',
)
CONTAINER_DETAIL_COLUMN_PASTE_KEYS.forEach((columnKey) => {
  assert(
    pageSource.includes(`columnKey="${columnKey}"`),
    `${columnKey} 列的可编辑输入框应包上拖拽填充容器`,
  )
})
assert(
  pageSource.includes('target.requiresCostParameters')
    && pageSource.includes('showCostRecalculateWarning(getContainerDetailCostMissingFields(container))'),
  '联动成本的列在粘贴前必须检查货柜成本参数',
)

console.log('containerDetailColumnPaste tests passed')
