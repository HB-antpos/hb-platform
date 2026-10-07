import assert from 'node:assert/strict'
import type { MonthlyStoreDailySales } from '../../../services/monthlyDailySalesService'
import { formatSydneyIsoDate } from '../../../utils/sydneyDate'
import {
  buildCsv,
  buildDayRows,
  buildFileName,
  buildSheetNames,
  clampMonth,
  compareBranchCode,
  compositionOf,
  currentMonthOf,
  dayTextParams,
  daysInMonth,
  defaultMonth,
  describeCoverage,
  displayTotals,
  escapeCsvField,
  formatAmount,
  formatCsvAmount,
  formatDollars,
  formatPercent,
  guardCsvText,
  listDates,
  matchesStoreSearch,
  monthTextParams,
  parseMonth,
  pickSelected,
  resolveFocus,
  sanitizeSheetName,
  selectionFingerprint,
  setCodesSelected,
  shiftMonth,
  summarizeSelection,
  summarizeStores,
  toCents,
  toggleExcluded,
  truncateSheetName,
  weekdayOf,
  CSV_BOM,
  MAX_SHEET_NAME_LENGTH,
} from './logic'

// ---------------------------------------------------------------------------
// 月份边界与默认月
// ---------------------------------------------------------------------------

assert.deepEqual(parseMonth('2026-09'), { year: 2026, month: 9 })
assert.equal(parseMonth('2026-13'), null)
assert.equal(parseMonth('2026-9'), null)
assert.equal(parseMonth(''), null)

assert.equal(daysInMonth('2026-09'), 30)
assert.equal(daysInMonth('2026-10'), 31)
assert.equal(daysInMonth('2024-02'), 29, '闰年二月 29 天')
assert.equal(daysInMonth('2025-02'), 28)
assert.equal(daysInMonth('bad'), 0)

// 默认月份 = 悉尼的上一个整月。
assert.equal(defaultMonth('2026-10-07'), '2026-09')
assert.equal(defaultMonth('2026-01-05'), '2025-12', '跨年回到上一年十二月')
assert.equal(defaultMonth('2025-02-01'), '2025-01')
assert.equal(defaultMonth('2025-01-15'), '2025-01', '不早于最早可选月份 2025-01')
// 悉尼 2026-10-01 00:30（UTC 还是 09-30 14:30）：默认月是 09 而不是 08，不受浏览器时区影响。
assert.equal(formatSydneyIsoDate(new Date('2026-09-30T14:30:00Z')), '2026-10-01')
assert.equal(defaultMonth(formatSydneyIsoDate(new Date('2026-09-30T14:30:00Z'))), '2026-09')
assert.equal(currentMonthOf('2026-10-07'), '2026-10')

// 步进：不晚于当月、不早于 2025-01，当月可选。
const today = '2026-10-07'
assert.equal(shiftMonth('2026-09', 1, today), '2026-10', '当月可选（进行中）')
assert.equal(shiftMonth('2026-10', 1, today), null, '不能晚于当前月')
assert.equal(shiftMonth('2025-02', -1, today), '2025-01')
assert.equal(shiftMonth('2025-01', -1, today), null, '不能早于 2025-01')
assert.equal(shiftMonth('2026-01', -1, today), '2025-12', '跨年向前')
assert.equal(shiftMonth('2025-12', 1, today), '2026-01', '跨年向后')
assert.equal(shiftMonth('bad', 1, today), null)
assert.equal(clampMonth('2024-06', today), '2025-01')
assert.equal(clampMonth('2027-01', today), '2026-10')
assert.equal(clampMonth('2026-05', today), '2026-05')
assert.equal(clampMonth('garbage', today), '2026-10')

// 星期按 UTC 日历计算：2026-09-01 是周二，09-05 周六，09-06 周日。
assert.equal(weekdayOf('2026-09-01'), 2)
assert.equal(weekdayOf('2026-09-05'), 6)
assert.equal(weekdayOf('2026-09-06'), 0)

// ---------------------------------------------------------------------------
// 金额：按「分」累加、显示格式
// ---------------------------------------------------------------------------

assert.equal(toCents(456756.14), 45675614)
assert.equal(toCents(0.1 + 0.2), 30, '浮点误差应被四舍五入吸收')
assert.equal(toCents(0), 0)
assert.equal(toCents(null), null)
assert.equal(toCents(undefined), null)
assert.equal(toCents(Number.NaN), null)

assert.equal(formatDollars(45675614), '$456,756')
assert.equal(formatDollars(-150), '-$2', '正负对称四舍五入')
assert.equal(formatDollars(-20), '$0', '不出现 -$0')
assert.equal(formatDollars(0), '$0', '休业日合计是 $0 而不是「—」')
assert.equal(formatDollars(null), '—')
assert.equal(formatAmount(1204028), '12,040.28')
assert.equal(formatAmount(5), '0.05')
assert.equal(formatAmount(-5), '-0.05')
assert.equal(formatAmount(null), '—')
assert.equal(formatPercent(1, 3), '33.3%')
assert.equal(formatPercent(0, 100), '0.0%')
assert.equal(formatPercent(1, 0), '—')

// ---------------------------------------------------------------------------
// 夹具：9 月前 4 天；A 有休业日 / 缺数日 / 无拆分日，B 全部完整，C 接口一天都没返回
// ---------------------------------------------------------------------------

const data: MonthlyStoreDailySales = {
  month: '2026-09',
  daysInMonth: 30,
  countedThroughDate: '2026-09-04',
  countedDays: 4,
  stores: [
    {
      branchCode: '1001',
      branchName: 'Alpha',
      days: [
        { date: '2026-09-01', revenue: 100.1, card: 60.05, cash: 30.03, other: 10.02 },
        { date: '2026-09-02', revenue: 0, card: 0, cash: 0, other: 0 },
        { date: '2026-09-03', revenue: null, card: null, cash: null, other: null },
        { date: '2026-09-04', revenue: 200.2, card: null, cash: null, other: null },
      ],
    },
    {
      branchCode: '1002',
      branchName: 'Beta',
      days: ['01', '02', '03', '04'].map(day => ({ date: `2026-09-${day}`, revenue: 100.1, card: 50, cash: 40, other: 10.1 })),
    },
    { branchCode: '1010', branchName: 'Gamma', days: [] },
  ],
}

const coverage = describeCoverage(data)
assert.deepEqual(coverage, {
  month: '2026-09', daysInMonth: 30, countedDays: 4, throughDay: 4, throughDate: '2026-09-04', isPartial: true,
})
assert.equal(describeCoverage({ ...data, countedThroughDate: '2026-09-30', countedDays: 30 }).isPartial, false, '整月已出数不是进行中')
assert.equal(describeCoverage({ ...data, countedThroughDate: null, countedDays: 0 }).throughDay, 0)

// 逐日行：接口没返回的日子按缺数处理，不会凭空补 0。
const gammaRows = buildDayRows(data.stores[2], '2026-09', 4)
assert.equal(gammaRows.length, 4)
assert.ok(gammaRows.every(row => row.status === 'missing' && row.revenue === null && row.card === null))

// 拆分不全（只缺一项）也整体视为无拆分，三列都为空。
const partialSplit = buildDayRows({ branchCode: 'X', branchName: 'X', days: [{ date: '2026-09-01', revenue: 10, card: 6, cash: null, other: 1 }] }, '2026-09', 1)
assert.equal(partialSplit[0].status, 'noSplit')
assert.deepEqual([partialSplit[0].revenue, partialSplit[0].card, partialSplit[0].cash, partialSplit[0].other], [1000, null, null, null])

const summaries = summarizeStores(data)
assert.deepEqual(summaries.map(store => store.branchCode), ['1002', '1001', '1010'], '按营业额降序')
const [beta, alpha, gamma] = summaries

// A：休业日（0）计入且算有拆分；缺数日不计入；无拆分日营业额计入、三列不计入。
assert.equal(alpha.totals.revenue, 30030, '100.10 + 0 + 200.20 按分累加 = 300.30，没有浮点尾差')
assert.equal(alpha.totals.revenueDays, 3)
assert.equal(alpha.totals.card, 6005)
assert.equal(alpha.totals.cash, 3003)
assert.equal(alpha.totals.other, 1002)
assert.equal(alpha.totals.splitRevenue, 10010)
assert.equal(alpha.totals.splitDays, 2, '休业日 0 也算有拆分')
assert.deepEqual(alpha.missingDates, ['2026-09-03'])
assert.deepEqual(alpha.noSplitDates, ['2026-09-04'])
assert.equal(formatAmount(alpha.totals.revenue), '300.30')
assert.equal(beta.totals.revenue, 40040)
assert.equal(beta.totals.splitDays, 4)
assert.deepEqual(beta.missingDates, [])

// C：一天都没有数据：合计项为 null（显示「—」/ 文件留空），不是 0。
assert.deepEqual(displayTotals(gamma.totals), { revenue: null, card: null, cash: null, other: null })
assert.equal(gamma.missingDates.length, 4)
assert.equal(compositionOf(gamma.totals), null)
// A 的营业额有值，但「其他」等三列只来自有拆分的日子。
assert.deepEqual(displayTotals(alpha.totals), { revenue: 30030, card: 6005, cash: 3003, other: 1002 })

const mix = compositionOf(beta.totals)!
assert.ok(Math.abs(mix.card + mix.cash + mix.other - 100) < 1e-9, '支付构成三段加起来是 100%')
assert.ok(Math.abs(mix.card - (5000 / 10010) * 100) < 1e-9)
assert.ok(compositionOf({ ...alpha.totals, card: 99999 })!.other >= 0, '刷卡异常偏大时其他段也不会是负数')

const all = summarizeSelection(summaries)
assert.equal(all.storeCount, 3)
assert.equal(all.totals.revenue, 70070)
assert.equal(all.totals.revenueDays, 7)
assert.equal(all.missingStoreDays, 5, 'A 缺 1 天 + C 缺 4 天')
assert.deepEqual(all.missingStores.map(store => store.branchCode), ['1001', '1010'])
assert.equal(all.noSplitStoreDays, 1)
assert.deepEqual(all.noSplitStores.map(store => store.branchCode), ['1001'])
assert.equal(all.csvRowCount, 12, '3 家 × 4 天，缺数日也占一行（金额留空）')
// 刷卡 / 现金 / 其他的合计不含无拆分日与缺数日，占比分母只含有拆分日的营业额。
assert.equal(all.totals.card, 6005 + 4 * 5000)
assert.equal(all.totals.splitRevenue, 10010 + 40040)
assert.equal(formatPercent(all.totals.card, all.totals.splitRevenue), `${(((6005 + 20000) / 50050) * 100).toFixed(1)}%`)

const onlyBeta = summarizeSelection([beta])
assert.equal(onlyBeta.missingStoreDays, 0)
assert.equal(onlyBeta.noSplitStoreDays, 0)

// ---------------------------------------------------------------------------
// 分店选择与搜索
// ---------------------------------------------------------------------------

assert.equal(matchesStoreSearch(alpha, ''), true)
assert.equal(matchesStoreSearch(alpha, '  alph '), true, '不区分大小写并忽略首尾空白')
assert.equal(matchesStoreSearch(alpha, '1001'), true, '编码也能搜')
assert.equal(matchesStoreSearch(alpha, 'beta'), false)

let excluded: ReadonlySet<string> = new Set()
assert.deepEqual(pickSelected(summaries, excluded).map(store => store.branchCode), ['1002', '1001', '1010'], '默认全选')
excluded = toggleExcluded(excluded, '1001')
assert.deepEqual(pickSelected(summaries, excluded).map(store => store.branchCode), ['1002', '1010'])
excluded = toggleExcluded(excluded, '1001')
assert.equal(excluded.size, 0)
// 清空 / 全选只作用于传入的（当前可见）编码。
excluded = setCodesSelected(new Set(), ['1001', '1002'], false)
assert.deepEqual([...excluded].sort(), ['1001', '1002'])
assert.deepEqual([...setCodesSelected(excluded, ['1001'], true)], ['1002'])
// 换月后新出现的分店默认勾选（它不在排除集合里）。
assert.equal(pickSelected([{ branchCode: '9999' }], excluded).length, 1)

assert.equal(resolveFocus(summaries, '1001')?.branchCode, '1001')
assert.equal(resolveFocus(summaries, 'gone')?.branchCode, '1002', '点选的分店不在数据里时回到第一家')
assert.equal(resolveFocus([], null), undefined)

assert.equal(
  selectionFingerprint('2026-09', 'xlsx', ['1002', '1001']),
  selectionFingerprint('2026-09', 'xlsx', ['1001', '1002']),
  '指纹与勾选顺序无关',
)
assert.notEqual(selectionFingerprint('2026-09', 'xlsx', ['1001']), selectionFingerprint('2026-09', 'csv', ['1001']))
assert.notEqual(selectionFingerprint('2026-09', 'xlsx', ['1001']), selectionFingerprint('2026-08', 'xlsx', ['1001']))

assert.deepEqual(listDates(['a', 'b', 'c'], 2), { shown: ['a', 'b'], hidden: 1 })
assert.deepEqual(listDates(['a'], 2), { shown: ['a'], hidden: 0 })

assert.ok(compareBranchCode('2', '10') < 0, '编码按数字感知排序')
assert.ok(compareBranchCode('1001', '1002') < 0)
assert.equal(compareBranchCode('A1', 'A1'), 0)

// ---------------------------------------------------------------------------
// 文件名与工作表名
// ---------------------------------------------------------------------------

assert.equal(buildFileName('月度日销售', '2026-09', 'xlsx'), '月度日销售_2026-09.xlsx')
assert.equal(buildFileName('月度日销售', '2026-09', 'csv'), '月度日销售_2026-09.csv')

assert.equal(sanitizeSheetName('A/B[C]:D*E?F\\G'), 'ABCDEFG', '去掉 [ ] : * ? / \\')
assert.equal(sanitizeSheetName("'quoted'"), 'quoted', '不能以单引号开头或结尾')
assert.equal(sanitizeSheetName('  Mt   Druitt \n'), 'Mt Druitt')
assert.equal(sanitizeSheetName('[]'), '')

assert.equal(truncateSheetName('a'.repeat(40)).length, MAX_SHEET_NAME_LENGTH)
const emoji = `${'a'.repeat(30)}😀`
assert.equal(truncateSheetName(emoji), 'a'.repeat(30), '不在代理对中间切开')
assert.equal(truncateSheetName('Bankstown'), 'Bankstown')

const names = buildSheetNames([
  { branchCode: '1003', branchName: 'Bankstown' },
  { branchCode: '1002', branchName: 'Bankstown' },
  { branchCode: '1004', branchName: 'bankstown' },
  { branchCode: '1005', branchName: '汇总' },
  { branchCode: '1006', branchName: 'History' },
  { branchCode: '1007', branchName: '[:*?/\\]' },
  { branchCode: '1008', branchName: 'x'.repeat(50) },
  { branchCode: '1009', branchName: 'x'.repeat(50) },
], ['汇总'])
assert.equal(names.get('1002'), 'Bankstown', '按编码顺序处理，先到者保留原名')
assert.equal(names.get('1003'), 'Bankstown-1003', '重名追加编码后缀')
assert.equal(names.get('1004'), 'bankstown-1004', '大小写不同也算重名（Excel 不区分大小写）')
assert.equal(names.get('1005'), '汇总-1005', '与汇总页同名也要改')
assert.equal(names.get('1006'), 'History-1006', 'Excel 保留名 History')
assert.equal(names.get('1007'), '1007', '清洗后为空时回退为编码')
assert.equal(names.get('1008'), 'x'.repeat(31))
assert.equal(names.get('1009'), `${'x'.repeat(31 - '-1009'.length)}-1009`, '截断后仍重名，截断基础名再加后缀，总长不超过 31')
for (const name of names.values()) {
  assert.ok(name.length <= MAX_SHEET_NAME_LENGTH && name.length > 0, `工作表名长度应在 1..31: ${name}`)
}
assert.equal(new Set([...names.values()].map(name => name.toLowerCase())).size, names.size, '工作表名全部唯一')

// ---------------------------------------------------------------------------
// CSV：BOM、转义、排序、空值
// ---------------------------------------------------------------------------

assert.equal(escapeCsvField('plain'), 'plain')
assert.equal(escapeCsvField('a,b'), '"a,b"')
assert.equal(escapeCsvField('say "hi"'), '"say ""hi"""')
assert.equal(escapeCsvField('line1\nline2'), '"line1\nline2"')
assert.equal(escapeCsvField('cr\rx'), '"cr\rx"')
assert.equal(guardCsvText('=SUM(A1)'), "'=SUM(A1)")
assert.equal(guardCsvText('+1'), "'+1")
assert.equal(guardCsvText('@x'), "'@x")
assert.equal(guardCsvText('Mt Druitt'), 'Mt Druitt')
assert.equal(formatCsvAmount(1204028), '12040.28')
assert.equal(formatCsvAmount(5), '0.05')
assert.equal(formatCsvAmount(0), '0.00', '休业日是 0.00，不是空')
assert.equal(formatCsvAmount(-5), '-0.05')
assert.equal(formatCsvAmount(null), '', '缺数留空')

const header = ['日期', '分店编码', '分店', '营业额', '刷卡', '现金', '其他']
const csvStores = summarizeStores({
  ...data,
  countedThroughDate: '2026-09-04',
  stores: [
    ...data.stores.slice(0, 2),
    {
      branchCode: '2',
      branchName: 'Odd, "Name"',
      days: [{ date: '2026-09-01', revenue: 10, card: 6, cash: 3, other: 1 }],
    },
    { branchCode: '10', branchName: '=cmd', days: [{ date: '2026-09-01', revenue: 1, card: 1, cash: 0, other: 0 }] },
  ],
})
const csv = buildCsv(csvStores, header)
assert.equal(csv.charCodeAt(0), 0xfeff, '以 BOM 开头')
assert.ok(csv.startsWith(`${CSV_BOM}日期,分店编码,分店,营业额,刷卡,现金,其他\r\n`))
assert.ok(csv.endsWith('\r\n'))
const lines = csv.slice(1).split('\r\n')
assert.equal(lines.pop(), '', '末尾以换行结束')
assert.equal(lines.length, 1 + 4 * 4, '表头 + 4 家 × 4 天')
// 先按编码（数字感知：2 < 10 < 1001）再按日期。
const keys = lines.slice(1).map(line => line.split(',').slice(0, 2).join('|'))
assert.deepEqual(keys.slice(0, 4), ['2026-09-01|2', '2026-09-02|2', '2026-09-03|2', '2026-09-04|2'])
assert.equal(lines[1], '2026-09-01,2,"Odd, ""Name""",10.00,6.00,3.00,1.00')
assert.ok(lines.includes("2026-09-01,10,'=cmd,1.00,1.00,0.00,0.00"), '公式前缀被防护')
assert.ok(lines.includes('2026-09-01,1001,Alpha,100.10,60.05,30.03,10.02'))
assert.ok(lines.includes('2026-09-02,1001,Alpha,0.00,0.00,0.00,0.00'), '休业日写 0.00')
assert.ok(lines.includes('2026-09-03,1001,Alpha,,,,'), '缺数日金额全部留空')
assert.ok(lines.includes('2026-09-04,1001,Alpha,200.20,,,'), '无拆分日营业额照写，三列留空')

// ---------------------------------------------------------------------------
// 文案参数
// ---------------------------------------------------------------------------

assert.deepEqual(monthTextParams('2026-09'), { year: 2026, month: 9, monthName: 'September' })
assert.deepEqual(dayTextParams('2026-09-12'), { month: 9, day: 12, monthName: 'Sep' })

console.log('monthlyDailySalesDownload logic: ok')
