import assert from 'node:assert/strict'
import type {
  CashDaily,
  CashDepositListItem,
  CashExpenseListItem,
  CashOverview,
  CashOverviewRow,
} from '../../../types/storeCash'
import {
  buildCsv,
  buildDailyCsv,
  buildDepositsCsv,
  buildExpensesCsv,
  buildOverviewCsv,
  CSV_BOM,
  csvInt,
  csvMoney,
  csvText,
  encodeCsvCell,
  escapeCsvField,
  guardCsvFormula,
  type CsvText,
} from './csv'

// 文案函数：返回键名本身（带参数时拼上参数），便于断言表头用的是哪个键。
const tx: CsvText = (key, params) => (params
  ? `${key}(${Object.entries(params).map(([name, value]) => `${name}=${value}`).join(';')})`
  : key)

/** 去掉 BOM 后按 CRLF 拆行（最后一个空行去掉）。 */
function lines(content: string): string[] {
  assert.ok(content.startsWith(CSV_BOM), 'CSV 必须以 UTF-8 BOM 开头')
  assert.ok(content.endsWith('\r\n'), '以 CRLF 结尾')
  return content.slice(CSV_BOM.length).split('\r\n').slice(0, -1)
}

// ---------------------------------------------------------------------------
// RFC 4180 转义
// ---------------------------------------------------------------------------

assert.equal(escapeCsvField('plain'), 'plain')
assert.equal(escapeCsvField('a,b'), '"a,b"')
assert.equal(escapeCsvField('say "hi"'), '"say ""hi"""')
assert.equal(escapeCsvField('line1\nline2'), '"line1\nline2"')
assert.equal(escapeCsvField('line1\r\nline2'), '"line1\r\nline2"')

// ---------------------------------------------------------------------------
// 防公式注入：只对文本列生效
// ---------------------------------------------------------------------------

for (const prefix of ['=', '+', '-', '@', '\t', '\r']) {
  assert.equal(guardCsvFormula(`${prefix}SUM(A1)`), `'${prefix}SUM(A1)`, `文本以 ${JSON.stringify(prefix)} 开头要补单引号`)
}
assert.equal(guardCsvFormula('Normal'), 'Normal')
assert.equal(guardCsvFormula(''), '')
assert.equal(guardCsvFormula("'already"), "'already")
// 文本：先防护再转义（含逗号时整体加引号）。
assert.equal(encodeCsvCell(csvText('=HYPERLINK("http://x","y")')), '"\'=HYPERLINK(""http://x"",""y"")"')
assert.equal(encodeCsvCell(csvText('-12.50')), "'-12.50", '文本列里的负数样式内容也会被防护')
assert.equal(encodeCsvCell(csvText('\tcmd')), "'\tcmd")
assert.equal(encodeCsvCell(csvText('\rcmd')), '"\'\rcmd"', '以回车开头：补单引号后仍需加引号')
assert.equal(encodeCsvCell(csvText(null)), '')
// 数值列：负数直接输出，不补单引号。
assert.equal(encodeCsvCell(csvMoney(-12.5)), '-12.50', '金额负数不能被补单引号')
assert.equal(encodeCsvCell(csvMoney(1234567.891)), '1234567.89', '不带千分位与货币符号')
assert.equal(encodeCsvCell(csvMoney(1.005)), '1.01')
assert.equal(encodeCsvCell(csvMoney(-0.001)), '0.00', '不输出 -0.00')
assert.equal(encodeCsvCell(csvMoney(null)), '', '空值留空，不写 0')
assert.equal(encodeCsvCell(csvInt(-3)), '-3')
assert.equal(encodeCsvCell(csvInt(null)), '')
// 数值列里混进非数值内容时退回文本处理，保证不会漏防护。
assert.equal(encodeCsvCell({ kind: 'number', value: '=1+1' }), "'=1+1")

const sample = buildCsv(['A', '=B'], [[csvText('x,y'), csvMoney(-1)]])
assert.deepEqual(lines(sample), ['A,\'=B', '"x,y",-1.00'], '表头也做防护')

// ---------------------------------------------------------------------------
// 总览：每店一行 + 合计行；null 留空；T2 受限时表头注明
// ---------------------------------------------------------------------------

function overviewRow(overrides: Partial<CashOverviewRow>): CashOverviewRow {
  return {
    storeCode: 'S001',
    storeName: 'Store',
    storeToday: '2026-10-08',
    openingMissing: false,
    poolBalance: 100,
    uncoveredDayCount: 0,
    oldestUncoveredDate: null,
    depositOverdue: false,
    lastDepositDate: null,
    inflowCash: 50,
    closeVariance: -1.5,
    closeDayCount: 7,
    missingCloseDayCount: 0,
    depositTotal: 40,
    depositCount: 1,
    expenseTotal: 10,
    expenseByCategory: [
      { category: 'Salary', amount: 1 },
      { category: 'Purchase', amount: 2 },
      { category: 'T2', amount: 3 },
      { category: 'Other', amount: 4 },
    ],
    flaggedExpenseCount: 0,
    ...overrides,
  }
}

const overview: CashOverview = {
  from: '2026-10-01',
  to: '2026-10-08',
  dailyCloseConnected: true,
  t2Restricted: true,
  rows: [
    overviewRow({ storeCode: 'S001', storeName: '=Evil, "Store"', poolBalance: null, inflowCash: null, closeVariance: null, openingMissing: true }),
    overviewRow({ storeCode: 'S002', storeName: 'Good', depositOverdue: true, uncoveredDayCount: 4, oldestUncoveredDate: '2026-10-02', lastDepositDate: '2026-10-01', flaggedExpenseCount: 2 }),
  ],
  totals: {
    poolBalance: null,
    inflowCash: null,
    closeVariance: null,
    depositTotal: 80,
    depositCount: 2,
    expenseTotal: 20,
    expenseByCategory: [{ category: 'Salary', amount: 2 }, { category: 'Purchase', amount: 4 }, { category: 'T2', amount: 6 }, { category: 'Other', amount: 8 }],
    uncoveredDayCount: 4,
    overdueStoreCount: 1,
    flaggedExpenseCount: 2,
  },
}
const overviewCsv = buildOverviewCsv(overview, tx, 14)
assert.equal(overviewCsv.rowCount, 2)
const overviewLines = lines(overviewCsv.content)
assert.equal(overviewLines.length, 4, '表头 + 2 店 + 合计行')
const overviewHeader = overviewLines[0].split(',')
assert.equal(overviewHeader.length, 20)
assert.ok(overviewHeader.includes('storeCash.csv.t2Restricted(days=14)'), 'T2 受限时表头注明只含最近 N 天')
assert.ok(!overviewHeader.includes('storeCash.category.T2'))
assert.equal(
  overviewLines[1],
  'S001,"\'=Evil, ""Store""",,storeCash.csv.yes,0,,storeCash.csv.no,,,7,0,1,40.00,10.00,1.00,2.00,3.00,4.00,0,',
  '不可算的金额留空；店名防注入并转义',
)
assert.equal(
  overviewLines[2],
  'S002,Good,100.00,storeCash.csv.no,4,2026-10-02,storeCash.csv.yes,50.00,-1.50,7,0,1,40.00,10.00,1.00,2.00,3.00,4.00,2,2026-10-01',
)
assert.equal(
  overviewLines[3],
  'storeCash.csv.totalRow,,,,4,,1,,,,,2,80.00,20.00,2.00,4.00,6.00,8.00,2,',
  '合计行：任一分店不可算时合计留空；逾期列写逾期分店数',
)
// 日结未接入：未存天数、逾期、日结天数留空（后端给的是 0），合计行同样留空。
const disconnectedLines = lines(buildOverviewCsv({ ...overview, dailyCloseConnected: false }, tx, 14).content)
assert.equal(
  disconnectedLines[2],
  'S002,Good,100.00,storeCash.csv.no,,2026-10-02,,50.00,-1.50,,,1,40.00,10.00,1.00,2.00,3.00,4.00,2,2026-10-01',
  '日结未接入时不写 0 / 否',
)
assert.equal(disconnectedLines[3], 'storeCash.csv.totalRow,,,,,,,,,,,2,80.00,20.00,2.00,4.00,6.00,8.00,2,')
const unrestricted = lines(buildOverviewCsv({ ...overview, t2Restricted: false, rows: [] }, tx, 14).content)
assert.ok(unrestricted[0].split(',').includes('storeCash.category.T2'), '不受限时表头就是 T2')
assert.equal(unrestricted.length, 1, '没有分店时只有表头，不写合计行')

// ---------------------------------------------------------------------------
// 按日明细：日结未接入时日结现金与是否有日结留空
// ---------------------------------------------------------------------------

const daily: CashDaily = {
  storeCode: 'S001',
  dailyCloseConnected: true,
  rows: [
    { businessDate: '2026-10-02', inflowCash: 0, hasClose: false, covered: false, coveredByDepositGuid: null, expenseTotal: 12.3, devices: [] },
    {
      businessDate: '2026-10-01',
      inflowCash: 300,
      hasClose: true,
      covered: true,
      coveredByDepositGuid: 'd1',
      expenseTotal: 0,
      devices: [
        { deviceCode: 'POS1', selectionMode: 'Manual', selectionStale: true, selectionOverlapWarning: false, selectionReason: 'x', selectedByName: null, selectedAtUtc: null, includedCash: 200, archives: [] },
        { deviceCode: 'POS2', selectionMode: 'Default', selectionStale: false, selectionOverlapWarning: false, selectionReason: null, selectedByName: null, selectedAtUtc: null, includedCash: 100, archives: [] },
      ],
    },
  ],
}
const dailyLines = lines(buildDailyCsv(daily, tx).content)
assert.equal(dailyLines.length, 3)
assert.equal(dailyLines[1], 'S001,2026-10-01,300.00,storeCash.csv.yes,storeCash.csv.yes,0.00,2,1,1', '按营业日升序')
assert.equal(dailyLines[2], 'S001,2026-10-02,0.00,storeCash.csv.no,,12.30,0,0,0', '没有日结时「已存」留空')
const dailyDisconnected = lines(buildDailyCsv({ ...daily, dailyCloseConnected: false }, tx).content)
assert.equal(dailyDisconnected[1], 'S001,2026-10-01,,,,0.00,2,1,1', '日结未接入：日结现金与是否有日结留空，不写 0 / 否')

// ---------------------------------------------------------------------------
// 存款 / 支出：时间按门店时区；图片只导出张数
// ---------------------------------------------------------------------------

const deposit: CashDepositListItem = {
  depositGuid: 'd1',
  storeCode: 'S001',
  depositDate: '2026-10-07',
  coveredFromDate: '2026-10-01',
  coveredToDate: '2026-10-06',
  totalAmount: 4321.5,
  slipCount: 2,
  imageCount: 3,
  slipSummaries: [
    { slipGuid: 's1', amount: 4000, slipNo: '=HYPERLINK', imageCount: 2 },
    { slipGuid: 's2', amount: 321.5, slipNo: null, imageCount: 1 },
  ],
  status: 'Voided',
  note: '+ 补存\n第二行',
  createdByName: '@张三',
  createdAtUtc: '2026-10-06T23:30:00Z',
  canVoid: false,
}
const depositCsv = buildDepositsCsv([deposit], tx, 'Australia/Sydney')
assert.equal(lines(depositCsv.content)[0].split(',').length, 14)
assert.equal(depositCsv.rowCount, 2, '每张存单一行')
assert.equal(
  depositCsv.content.split('\r\n')[1],
  'S001,2026-10-07,2026-10-01,2026-10-06,4321.50,2,1,4000.00,\'=HYPERLINK,2,\'@张三,2026-10-07 10:30,storeCash.status.Voided,"\'+ 补存\n第二行"',
  '存单号、录入人、备注防注入；备注含换行整体加引号；时间按门店时区',
)
assert.ok(
  depositCsv.content.includes('S001,2026-10-07,2026-10-01,2026-10-06,4321.50,2,2,321.50,,1,'),
  '第二张存单单独一行，存单号为空',
)
// 旧版后端没有存单摘要：仍导出一行，存单列留空。
const legacyDeposit = buildDepositsCsv([{ ...deposit, slipSummaries: undefined }], tx, 'Australia/Sydney')
assert.equal(legacyDeposit.rowCount, 1)
assert.ok(legacyDeposit.content.includes('4321.50,2,,,,,'))

const expenseItem: CashExpenseListItem = {
  expenseGuid: 'e1',
  storeCode: 'S002',
  expenseDate: '2026-10-05',
  category: 'T2',
  amount: -0.5,
  payeeName: '-Bob',
  note: null,
  reviewStatus: 'Flagged',
  reviewNote: '收据不清楚',
  reviewedByName: '财务',
  reviewedAtUtc: '2026-10-06T01:00:00Z',
  canReview: true,
  status: 'Active',
  imageCount: 1,
  createdByName: null,
  createdAtUtc: '2026-10-05T02:00:00Z',
  canVoid: true,
}
const expenseLines = lines(buildExpensesCsv([expenseItem, { ...expenseItem, expenseGuid: 'e2', category: 'Mystery', reviewStatus: 'None' }], tx, 'Australia/Perth').content)
assert.equal(expenseLines[0].split(',').length, 14)
assert.equal(
  expenseLines[1],
  "S002,2026-10-05,storeCash.category.T2,-0.50,'-Bob,,storeCash.review.Flagged,收据不清楚,财务,2026-10-06 09:00,,2026-10-05 10:00,storeCash.status.Active,1",
  '金额负数不补单引号；收款人以 - 开头要补；时间按珀斯时区',
)
assert.equal(expenseLines[2].split(',')[2], 'Mystery', '未知类别码原样输出')
assert.equal(expenseLines[2].split(',')[6], 'storeCash.review.None')

// T2 只叫 T2：CSV 的类别列与表头里没有别的叫法（禁用词用转义写，避免本文件自己出现）。
const forbiddenT2Names = [/\u5206\u7ea2/, /d\x69vidend/i]
for (const content of [overviewCsv.content, buildExpensesCsv([expenseItem], tx, 'Australia/Sydney').content]) {
  assert.ok(forbiddenT2Names.every((pattern) => !pattern.test(content)), 'CSV 不得出现 T2 的其他叫法')
}

console.log('storeCash csv.test: ok')
