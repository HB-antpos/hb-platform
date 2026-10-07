import assert from 'node:assert/strict'
import ExcelJS from 'exceljs'
import type { MonthlyStoreDailySales } from '../../../services/monthlyDailySalesService'
import {
  downloadCompletedStores,
  exportCsvFile,
  fillStoreSheet,
  resumeWorkbookExport,
  startWorkbookExport,
  type ExportContext,
  type ExportDeps,
  type ExportProgress,
  type TextFn,
} from './export'
import { describeCoverage, summarizeStores, type StoreSummary } from './logic'
import zhMessages from './messages.zh.json'

// 用真实的中文消息文件做文案替身：键不存在或占位符没传都直接报错，顺便核对文案与代码一致。
const text: TextFn = (key, params = {}) => {
  let node: unknown = zhMessages
  for (const segment of key.split('.')) node = (node as Record<string, unknown> | undefined)?.[segment]
  assert.equal(typeof node, 'string', `缺少文案键 ${key}`)
  return (node as string).replace(/\{\{(\w+)\}\}/g, (_, name: string) => {
    assert.ok(name in params, `${key} 缺少参数 ${name}`)
    return String(params[name])
  })
}

const day = (date: string, revenue: number | null, card: number | null, cash: number | null, other: number | null) => ({ date, revenue, card, cash, other })

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
        day('2026-09-01', 100.1, 60.05, 30.03, 10.02),
        day('2026-09-02', 0, 0, 0, 0),
        day('2026-09-03', null, null, null, null),
        day('2026-09-04', 200.2, null, null, null),
      ],
    },
    {
      branchCode: '1002',
      branchName: 'Beta',
      days: ['01', '02', '03', '04'].map(date => day(`2026-09-${date}`, 100.1, 50, 40, 10.1)),
    },
    { branchCode: '1010', branchName: 'Gamma', days: [] },
  ],
}
const stores = summarizeStores(data)
const context: ExportContext = { ...describeCoverage(data), monthLabel: '2026 年 9 月' }

interface Captured { blob: Blob; fileName: string }
function captureDownloads() {
  const files: Captured[] = []
  return { files, download: (blob: Blob, fileName: string) => { files.push({ blob, fileName }) } }
}

async function readWorkbook(blob: Blob) {
  const workbook = new ExcelJS.Workbook()
  await workbook.xlsx.load(await blob.arrayBuffer())
  return workbook
}

function rowValues(sheet: ExcelJS.Worksheet, rowNumber: number, columns: number) {
  return Array.from({ length: columns }, (_, index) => sheet.getCell(rowNumber, index + 1).value)
}

// ---------------------------------------------------------------------------
// 1) 完整导出：工作表个数与顺序、汇总与单店合计一致、缺数留空、日期单元格、数字格式
// ---------------------------------------------------------------------------

const progress: ExportProgress[] = []
const first = captureDownloads()
const outcome = await startWorkbookExport({ stores, context, text }, item => progress.push(item), { download: first.download })
assert.equal(outcome.status, 'completed')
assert.equal(first.files.length, 1)
assert.equal(first.files[0].fileName, '月度日销售_2026-09.xlsx')
if (outcome.status === 'completed') {
  assert.equal(outcome.storeCount, 3)
  assert.equal(outcome.sheetCount, 4)
}

// 进度：按编码顺序逐店回调，最后是生成文件阶段。
assert.deepEqual(
  progress.map(item => [item.stage, item.index, item.total, item.storeName]),
  [['store', 1, 3, 'Alpha'], ['store', 2, 3, 'Beta'], ['store', 3, 3, 'Gamma'], ['finalize', 3, 3, '']],
)
assert.equal(text('monthlyDailySalesDownload.progress.store', { name: 'Bankstown', index: 3, total: 8 }), '正在生成 Bankstown（3 / 8）')
assert.ok(progress.every((item, index) => index === 0 || item.percent >= progress[index - 1].percent))
assert.equal(progress[progress.length - 1]?.percent, 1)

const workbook = await readWorkbook(first.files[0].blob)
assert.deepEqual(workbook.worksheets.map(sheet => sheet.name), ['汇总', 'Alpha', 'Beta', 'Gamma'], '汇总在第一页，其后每家分店一页')

// 汇总页
const summary = workbook.getWorksheet('汇总')!
assert.equal(summary.getCell(1, 1).value, '月度日销售汇总 · 2026 年 9 月')
assert.equal(summary.getCell(2, 1).value, '单位：澳元 · 统计截至 2026-09-04 · 已选 3 家分店 · 本月进行中（已出数 4 / 30 天）')
assert.deepEqual(rowValues(summary, 3, 7), ['分店编码', '分店', '营业额', '刷卡', '现金', '其他', '备注'])
assert.deepEqual(rowValues(summary, 4, 6), ['1001', 'Alpha', 300.3, 60.05, 30.03, 10.02])
assert.deepEqual(rowValues(summary, 5, 6), ['1002', 'Beta', 400.4, 200, 160, 40.4])
// 全部缺数的分店：金额留空，不写 0；备注说明原因。
assert.deepEqual(rowValues(summary, 6, 6), ['1010', 'Gamma', null, null, null, null])
assert.match(String(summary.getCell(4, 7).value), /缺数 1 天：09-03/)
assert.match(String(summary.getCell(4, 7).value), /无支付方式拆分 1 天：09-04/)
assert.match(String(summary.getCell(6, 7).value), /缺数 4 天：09-01、09-02、09-03、09-04/)
assert.equal(summary.getCell(5, 7).value, null, '没有问题的分店不写备注')
// 合计行：全部分店之和，刷卡 / 现金 / 其他不含缺数与无拆分的日子。
assert.equal(summary.getCell(7, 1).value, '合计')
assert.deepEqual(rowValues(summary, 7, 6).slice(2), [700.7, 260.05, 190.03, 50.42])
for (const column of [3, 4, 5, 6]) {
  assert.equal(summary.getCell(4, column).numFmt, '#,##0.00', '汇总金额格式')
  assert.equal(summary.getCell(7, column).numFmt, '#,##0.00')
}
const footnotes = [9, 10, 11].map(row => String(summary.getCell(row, 1).value))
assert.ok(footnotes[0].includes('缺数日表示该日统计尚未发布'))
assert.ok(footnotes[1].includes('无支付方式拆分的日子'))
assert.ok(footnotes[2].includes('本月还没结束') && footnotes[2].includes('2026-09-04'))
assert.equal(summary.views[0]?.state, 'frozen')
assert.equal((summary.views[0] as { ySplit?: number }).ySplit, 3, '汇总页冻结表头')

// 汇总与单店合计一致
const totalOf = (sheet: ExcelJS.Worksheet) => {
  let rowNumber = 3
  while (sheet.getCell(rowNumber, 1).value !== '合计') rowNumber += 1
  return rowValues(sheet, rowNumber, 6).slice(2)
}
for (const [index, name] of ['Alpha', 'Beta', 'Gamma'].entries()) {
  const sheet = workbook.getWorksheet(name)!
  assert.deepEqual(totalOf(sheet), rowValues(summary, 4 + index, 6).slice(2), `${name} 单店合计应与汇总页一致`)
}

// 单店页：Alpha（休业日、缺数日、无拆分日）
const alpha = workbook.getWorksheet('Alpha')!
assert.equal(alpha.getCell(1, 1).value, 'Alpha · 1001 · 2026 年 9 月')
assert.deepEqual(rowValues(alpha, 2, 7), ['日期', '星期', '营业额', '刷卡', '现金', '其他', '备注'])
const d1 = alpha.getCell(3, 1).value as Date
assert.ok(d1 instanceof Date, '日期是真正的日期单元格')
assert.equal(d1.getTime(), Date.UTC(2026, 8, 1), '按 UTC 构造，不差一天')
assert.equal(alpha.getCell(3, 1).numFmt, 'yyyy-mm-dd')
assert.equal(alpha.getCell(3, 2).value, '周二')
assert.deepEqual(rowValues(alpha, 3, 6).slice(2), [100.1, 60.05, 30.03, 10.02])
assert.deepEqual(rowValues(alpha, 4, 6).slice(2), [0, 0, 0, 0], '休业日写 0，不是空')
assert.deepEqual(rowValues(alpha, 5, 6).slice(2), [null, null, null, null], '缺数日全部留空')
assert.deepEqual(rowValues(alpha, 6, 6).slice(2), [200.2, null, null, null], '无拆分日只有营业额')
assert.equal(alpha.getCell(5, 7).value, '缺数（统计尚未发布，留空）')
assert.equal(alpha.getCell(6, 7).value, '无支付方式拆分（刷卡、现金、其他留空）')
assert.equal(alpha.getCell(3, 7).value, null)
assert.equal(alpha.getCell(3, 3).numFmt, '#,##0.00')
assert.equal(alpha.getCell(7, 1).value, '合计')
assert.deepEqual(rowValues(alpha, 7, 6).slice(2), [300.3, 60.05, 30.03, 10.02])
assert.equal((alpha.views[0] as { ySplit?: number }).ySplit, 2, '单店页冻结表头')
const alphaNotes = [9, 10, 11].map(row => alpha.getCell(row, 1).value)
assert.ok(String(alphaNotes[0]).includes('缺数日'))

// Gamma：整月缺数
const gamma = workbook.getWorksheet('Gamma')!
assert.deepEqual(rowValues(gamma, 3, 6).slice(2), [null, null, null, null])
assert.deepEqual(totalOf(gamma), [null, null, null, null], '全部缺数时合计也留空而不是 0')

// Beta：完整的店没有备注列，也没有说明行
const beta = workbook.getWorksheet('Beta')!
assert.deepEqual(rowValues(beta, 2, 7), ['日期', '星期', '营业额', '刷卡', '现金', '其他', null], '没有问题的店不出现备注列')
assert.equal(beta.getCell(7, 1).value, '合计')
// 没有缺数 / 无拆分说明，只有「本月进行中」这一条。
assert.match(String(beta.getCell(9, 1).value), /本月还没结束/)
assert.equal(beta.getCell(10, 1).value, null)

// ---------------------------------------------------------------------------
// 2) 周末淡灰底、本月已结束时没有「进行中」标注、单店页不依赖浏览器时区
// ---------------------------------------------------------------------------

const weekendData: MonthlyStoreDailySales = {
  month: '2026-09',
  daysInMonth: 30,
  countedThroughDate: '2026-09-30',
  countedDays: 30,
  stores: [{
    branchCode: '3001',
    branchName: 'Weekend',
    days: Array.from({ length: 30 }, (_, index) => day(`2026-09-${String(index + 1).padStart(2, '0')}`, 10, 6, 3, 1)),
  }],
}
const weekendStores = summarizeStores(weekendData)
const fullMonthContext: ExportContext = { ...describeCoverage(weekendData), monthLabel: '2026 年 9 月' }

const originalTimeZone = process.env.TZ
async function exportUnder(timeZone: string) {
  process.env.TZ = timeZone
  const captured = captureDownloads()
  const result = await startWorkbookExport({ stores: weekendStores, context: fullMonthContext, text }, undefined, { download: captured.download })
  assert.equal(result.status, 'completed')
  return readWorkbook(captured.files[0].blob)
}
try {
  for (const timeZone of ['Pacific/Kiritimati', 'America/Los_Angeles', 'UTC']) {
    const loaded = await exportUnder(timeZone)
    const sheet = loaded.getWorksheet('Weekend')!
    assert.equal((sheet.getCell(3, 1).value as Date).getTime(), Date.UTC(2026, 8, 1), `${timeZone}: 首日不应差一天`)
    assert.equal((sheet.getCell(32, 1).value as Date).getTime(), Date.UTC(2026, 8, 30), `${timeZone}: 末日不应差一天`)
    assert.equal(loaded.getWorksheet('汇总')!.getCell(2, 1).value, '单位：澳元 · 统计截至 2026-09-30 · 已选 1 家分店', '整月已出数没有「进行中」标注')
    assert.equal(loaded.getWorksheet('汇总')!.getCell(3, 7).value, null, '没有任何问题时汇总页没有备注列')
    assert.equal(loaded.getWorksheet('汇总')!.getCell(7, 1).value, null, '没有任何问题时也没有说明行')
    if (timeZone === 'UTC') {
      // 2026-09-05 周六、09-06 周日淡灰底，周一至周五不加底色。
      const fill = (row: number) => (sheet.getCell(row, 3).fill as ExcelJS.FillPattern | undefined)?.fgColor?.argb
      assert.equal(fill(3 + 4), 'FFF3F5F8')
      assert.equal(fill(3 + 5), 'FFF3F5F8')
      assert.equal(fill(3), undefined)
      assert.equal(fill(3 + 6), undefined)
    }
  }
} finally {
  if (originalTimeZone === undefined) delete process.env.TZ
  else process.env.TZ = originalTimeZone
}

// ---------------------------------------------------------------------------
// 3) 取消：不下载任何文件
// ---------------------------------------------------------------------------

{
  const captured = captureDownloads()
  const controller = new AbortController()
  const result = await startWorkbookExport(
    { stores, context, text, signal: controller.signal },
    item => { if (item.index === 2 && item.stage === 'store') controller.abort() },
    { download: captured.download },
  )
  assert.equal(result.status, 'cancelled')
  assert.equal(captured.files.length, 0, '取消后不应下载半成品')

  const pre = new AbortController()
  pre.abort()
  assert.equal((await startWorkbookExport({ stores, context, text, signal: pre.signal }, undefined, { download: captured.download })).status, 'cancelled')

  // 组装函数抛 AbortError 也按取消处理，而不是「导出中断」
  const aborting = await startWorkbookExport({ stores, context, text }, undefined, {
    download: captured.download,
    addStoreSheet: () => { throw new DOMException('aborted', 'AbortError') },
  })
  assert.equal(aborting.status, 'cancelled')
  assert.equal(captured.files.length, 0)
}

// ---------------------------------------------------------------------------
// 4) 单店组装失败 → 导出中断；重试未完成的 / 只下载已完成的
// ---------------------------------------------------------------------------

{
  const built: string[] = []
  const failBeta: ExportDeps['addStoreSheet'] = (book, store, sheetName, ctx, translate) => {
    if (store.branchCode === '1002') throw new Error('boom')
    built.push(store.branchCode)
    fillStoreSheet(book.addWorksheet(sheetName), store, ctx, translate)
  }
  const captured = captureDownloads()
  const interrupted = await startWorkbookExport({ stores, context, text }, undefined, { download: captured.download, addStoreSheet: failBeta })
  assert.equal(interrupted.status, 'interrupted')
  assert.equal(captured.files.length, 0, '中断时不下载')
  if (interrupted.status !== 'interrupted') throw new Error('unreachable')
  assert.equal(interrupted.stage, 'store')
  assert.deepEqual(interrupted.failedStore, { branchCode: '1002', branchName: 'Beta' })
  assert.equal(interrupted.message, 'boom')
  assert.deepEqual([interrupted.done, interrupted.total, interrupted.pending], [1, 3, 2], '已完成 1 / 3 家，未完成 2 家')
  assert.deepEqual(interrupted.checkpoint.completed, ['1001'])
  assert.deepEqual(built, ['1001'])

  // 「只下载已完成的」：汇总页只列已完成的分店，并注明已选分店数。
  const partial = captureDownloads()
  const partialResult = await downloadCompletedStores(interrupted.checkpoint, { download: partial.download })
  assert.deepEqual([partialResult.storeCount, partialResult.sheetCount, partialResult.fileName], [1, 2, '月度日销售_2026-09.xlsx'])
  const partialBook = await readWorkbook(partial.files[0].blob)
  assert.deepEqual(partialBook.worksheets.map(sheet => sheet.name), ['汇总', 'Alpha'])
  assert.equal(partialBook.getWorksheet('汇总')!.getCell(2, 1).value,
    '单位：澳元 · 统计截至 2026-09-04 · 已选 3 家分店，本文件只含已完成的 1 家 · 本月进行中（已出数 4 / 30 天）')
  assert.equal(partialBook.getWorksheet('汇总')!.getCell(4, 1).value, '1001')
  assert.equal(partialBook.getWorksheet('汇总')!.getCell(5, 1).value, '合计')

  // 「重试未完成的」：沿用工作簿，只补做失败的和后面的；已完成的不再重做。
  const retried = captureDownloads()
  const resumeBuilt: string[] = []
  const resumeProgress: ExportProgress[] = []
  const resumed = await resumeWorkbookExport(interrupted.checkpoint, undefined, item => resumeProgress.push(item), {
    download: retried.download,
    addStoreSheet: (book, store, sheetName, ctx, translate) => {
      resumeBuilt.push(store.branchCode)
      fillStoreSheet(book.addWorksheet(sheetName), store, ctx, translate)
    },
  })
  assert.equal(resumed.status, 'completed')
  assert.deepEqual(resumeBuilt, ['1002', '1010'], '只重做未完成的')
  assert.deepEqual(resumeProgress.filter(item => item.stage === 'store').map(item => [item.index, item.storeName]), [[2, 'Beta'], [3, 'Gamma']])
  const retriedBook = await readWorkbook(retried.files[0].blob)
  assert.deepEqual(retriedBook.worksheets.map(sheet => sheet.name), ['汇总', 'Alpha', 'Beta', 'Gamma'])
  assert.equal(retriedBook.getWorksheet('汇总')!.getCell(2, 1).value,
    '单位：澳元 · 统计截至 2026-09-04 · 已选 3 家分店 · 本月进行中（已出数 4 / 30 天）', '全部完成后汇总页恢复完整说明')

  // 重试时再次失败，仍然保留已完成的工作表
  // 全部补齐后再次收尾（例如下载失败后重试）：汇总页被重建而不是重复追加。
  const again = await resumeWorkbookExport(interrupted.checkpoint, undefined, undefined, { download: retried.download, addStoreSheet: failBeta })
  assert.equal(again.status, 'completed')
  const againBook = await readWorkbook(retried.files[1].blob)
  assert.deepEqual(againBook.worksheets.map(sheet => sheet.name), ['汇总', 'Alpha', 'Beta', 'Gamma'])
}

// ---------------------------------------------------------------------------
// 5) 默认组装失败时不留下半张表；生成 / 下载文件失败 → 可重试收尾，汇总页不重复
// ---------------------------------------------------------------------------

{
  // 默认实现：缺字段导致填充抛错时，工作簿里不能留下半张表。
  const alphaSummary = stores.find(store => store.branchCode === '1001')!
  const betaSummary = stores.find(store => store.branchCode === '1002')!
  const broken: StoreSummary = { ...alphaSummary, rows: [{ ...alphaSummary.rows[0], date: undefined as unknown as string }] }
  const captured = captureDownloads()
  const interrupted = await startWorkbookExport({ stores: [broken, betaSummary], context, text }, undefined, { download: captured.download })
  assert.equal(interrupted.status, 'interrupted')
  if (interrupted.status !== 'interrupted') throw new Error('unreachable')
  assert.equal(interrupted.failedStore?.branchCode, broken.branchCode)
  assert.deepEqual(interrupted.checkpoint.workbook.worksheets.map(sheet => sheet.name), [], '失败的那张表被移除')
}

{
  let failures = 1
  const captured = captureDownloads()
  const flaky: ExportDeps['download'] = (blob, fileName) => {
    if (failures-- > 0) throw new Error('disk full')
    captured.download(blob, fileName)
  }
  const result = await startWorkbookExport({ stores, context, text }, undefined, { download: flaky })
  assert.equal(result.status, 'interrupted')
  if (result.status !== 'interrupted') throw new Error('unreachable')
  assert.equal(result.stage, 'finalize')
  assert.equal(result.failedStore, null)
  assert.equal(result.pending, 0)
  assert.equal(result.message, 'disk full')

  const retried = await resumeWorkbookExport(result.checkpoint, undefined, undefined, { download: flaky })
  assert.equal(retried.status, 'completed')
  const book = await readWorkbook(captured.files[0].blob)
  assert.deepEqual(book.worksheets.map(sheet => sheet.name), ['汇总', 'Alpha', 'Beta', 'Gamma'], '重试收尾后只有一张汇总页')
}

// 重名 / 含非法字符的分店名：工作表名被清洗且唯一
{
  const messy = summarizeStores({
    ...data,
    stores: [
      { branchCode: '5001', branchName: 'Mt/Druitt:[A]', days: [day('2026-09-01', 1, 1, 0, 0)] },
      { branchCode: '5002', branchName: '汇总', days: [day('2026-09-01', 2, 1, 1, 0)] },
      { branchCode: '5003', branchName: '汇总', days: [day('2026-09-01', 3, 2, 1, 0)] },
    ],
  })
  const captured = captureDownloads()
  const result = await startWorkbookExport({ stores: messy, context, text }, undefined, { download: captured.download })
  assert.equal(result.status, 'completed')
  const book = await readWorkbook(captured.files[0].blob)
  assert.deepEqual(book.worksheets.map(sheet => sheet.name), ['汇总', 'MtDruittA', '汇总-5002', '汇总-5003'])
}

// ---------------------------------------------------------------------------
// 6) CSV
// ---------------------------------------------------------------------------

{
  const captured = captureDownloads()
  const result = exportCsvFile(stores, context, text, { download: captured.download })
  assert.deepEqual(result, { fileName: '月度日销售_2026-09.csv', rowCount: 12 })
  assert.equal(captured.files[0].fileName, '月度日销售_2026-09.csv')
  assert.equal(captured.files[0].blob.type, 'text/csv;charset=utf-8')
  const bytes = new Uint8Array(await captured.files[0].blob.arrayBuffer())
  assert.deepEqual([...bytes.slice(0, 3)], [0xef, 0xbb, 0xbf], 'UTF-8 带 BOM')
  const content = new TextDecoder('utf-8', { ignoreBOM: true }).decode(bytes)
  const lines = content.slice(1).split('\r\n')
  assert.equal(lines[0], '日期,分店编码,分店,营业额,刷卡,现金,其他')
  assert.equal(lines[1], '2026-09-01,1001,Alpha,100.10,60.05,30.03,10.02')
  assert.equal(lines[3], '2026-09-03,1001,Alpha,,,,')
  assert.equal(lines[4], '2026-09-04,1001,Alpha,200.20,,,')
  assert.equal(lines[9], '2026-09-01,1010,Gamma,,,,', '整店缺数的店也保留逐日行，金额留空')
}

console.log('monthlyDailySalesDownload export: ok')
