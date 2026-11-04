import assert from 'node:assert/strict'
import enMessages from '../planogramMessages.en.json'
import zhMessages from '../planogramMessages.zh.json'
import { fixtureDetail, fixtureItems, fixtureSnapshot, FIXTURE_HOOK, FIXTURE_PRODUCTS } from './sheetFixtures'
import {
  buildHeaderInfo,
  buildHeaderPreviewLine,
  buildPdfFileName,
  buildPdfModel,
  CHECKLIST_COLUMN_LABEL_KEYS,
  checklistColumns,
  checklistColumnWidths,
  bottomPanelHeight,
  cellContentWidth,
  checklistSummaryHeight,
  checklistSummaryTop,
  footerHeight,
  footerTop,
  hookCellLines,
  isNarrowFrame,
  productCellLines,
  computeBoardLayout,
  computeChecklistLayout,
  countErrorItems,
  createPdfTranslator,
  DEFAULT_PDF_OPTIONS,
  displayRowNumber,
  fixedScaleFor,
  formatMoney,
  formatMoneyTotal,
  formatPdfDate,
  hasPdfContent,
  interpolateMessage,
  localizedModelName,
  lookupMessage,
  makePdfText,
  normalizePdfFileName,
  pageFrameOf,
  paginateChecklist,
  PDF_PAPERS,
  planPdfPages,
  printNameFor,
  productNames,
  sourceFromDetail,
  sourceFromSnapshot,
  type MessageTree,
  type PdfOptions,
} from './sheetLogic'

// PDF 纯逻辑：数据来源统一成快照形状、序号与清单顺序、版面图比例与分页、清单分栏分页、文件名、按语言取词、双语英文名回退。
// 数据取自设计稿 05-pdf.html / plano.js（2 节 900×1200、双钩带价签 200、12 个商品、52 个挂钩）。

const zh = zhMessages as unknown as MessageTree
const en = enMessages as unknown as MessageTree
const translate = createPdfTranslator(zh, en)
const options = (patch: Partial<PdfOptions> = {}): PdfOptions => ({ ...DEFAULT_PDF_OPTIONS, ...patch })

// ---------------------------------------------------------------------------
// 1) 按语言取词
// ---------------------------------------------------------------------------
assert.equal(lookupMessage({ a: { b: 'x' } }, 'a.b'), 'x')
assert.equal(lookupMessage({ a: { b: 'x' } }, 'a.c'), undefined, '不存在的路径')
assert.equal(lookupMessage({ a: { b: 'x' } }, 'a'), undefined, '路径指向分支（不是字符串）也算没有')
assert.equal(interpolateMessage('v{{version}} · {{ date }}', { version: 3, date: '2026-11-04' }), 'v3 · 2026-11-04', '占位符替换（容许空格）')
assert.equal(interpolateMessage('v{{version}} {{missing}}', { version: 3 }), 'v3 {{missing}}', '缺参数的占位符原样保留，便于发现漏传')

assert.equal(translate('zh', 'planogram.pdf.title', { name: 'A1' }), '商品陈列图 · A1')
assert.equal(translate('en', 'planogram.pdf.title', { name: 'A1' }), 'Planogram · A1')
assert.equal(translate('en', 'planogram.pdf.no.such.key'), 'planogram.pdf.no.such.key', '缺键返回键名本身，不静默回退到另一种语言')

const zhText = makePdfText('zh', translate)
const enText = makePdfText('en', translate)
const biText = makePdfText('bi', translate)
assert.equal(zhText.one('planogram.pdf.legend.title'), '图例')
assert.equal(enText.one('planogram.pdf.legend.title'), 'Legend')
assert.equal(biText.one('planogram.pdf.legend.title'), '图例', '双语的 one() 取主语言中文')
assert.deepEqual(biText.line('planogram.pdf.legend.title'), ['图例', 'Legend'], '双语 line() 给中文 + 英文两行')
assert.deepEqual(zhText.line('planogram.pdf.legend.title'), ['图例'])
assert.equal(biText.joined('planogram.pdf.legend.title'), '图例 / Legend')
assert.equal(enText.joined('planogram.pdf.legend.title'), 'Legend')
assert.equal(
  biText.joinedBy('planogram.pdf.numbering', (language) => ({ gx: 10, gy: 10, from: translate(language, 'planogram.pdf.rowFromTop') })),
  '列号自左 1 起，行号自板顶 1 起 · 间距 10 / 10 mm / Cols from left, rows from top · Gap 10/10 mm',
  '句子里嵌的片段随语言变化',
)

// 英文用词（方案 11 节）：Bay / Col / Row / Hook / Per hook
assert.equal(translate('en', 'planogram.pdf.col.bay'), 'Bay')
assert.equal(translate('en', 'planogram.pdf.col.pos'), 'Col/Row')
assert.equal(translate('en', 'planogram.pdf.col.hook'), 'Hook')
assert.equal(translate('en', 'planogram.pdf.col.perHook'), 'Per hook')
assert.equal(translate('en', 'planogram.pdf.checklist.group', { bay: 1, row: 3 }), 'Bay 1 · hook row 3')
assert.equal(translate('zh', 'planogram.pdf.checklist.group', { bay: 1, row: 3 }), '节 1 · 第 3 行挂孔')

// PDF 文案块中英文键集合完全一致、没有空文案；所有列标题键都存在
function leafKeys(tree: MessageTree, prefix = ''): string[] {
  return Object.entries(tree).flatMap(([key, value]) => (typeof value === 'string' ? [`${prefix}${key}`] : leafKeys(value, `${prefix}${key}.`)))
}
const zhPdf = leafKeys((zh.planogram as MessageTree).pdf as MessageTree).sort()
const enPdf = leafKeys((en.planogram as MessageTree).pdf as MessageTree).sort()
assert.deepEqual(zhPdf, enPdf, 'planogram.pdf 中英文键集合一致')
for (const key of zhPdf) {
  assert.ok(lookupMessage(zh, `planogram.pdf.${key}`)?.length, `中文 ${key} 不能为空`)
  assert.ok(lookupMessage(en, `planogram.pdf.${key}`)?.length, `英文 ${key} 不能为空`)
}
for (const key of Object.values(CHECKLIST_COLUMN_LABEL_KEYS)) {
  assert.ok(lookupMessage(zh, key) && lookupMessage(en, key), `列标题 ${key} 中英文都有`)
}
// 英文版不能夹带中文（中文用词只允许出现在「语言」选项自己的标签里，那是界面文案，不在 pdf 块）
for (const key of enPdf) {
  assert.ok(!/[㐀-鿿]/.test(lookupMessage(en, `planogram.pdf.${key}`) ?? ''), `英文 PDF 文案 ${key} 里不能有中文`)
}

// ---------------------------------------------------------------------------
// 2) 名称：双语 / 英文名回退
// ---------------------------------------------------------------------------
assert.deepEqual(productNames({ productName: ' 胶棒 ', englishName: ' Glue ' }, 'X'), { zh: '胶棒', en: 'Glue' })
assert.deepEqual(productNames({ productName: '', englishName: '' }, 'X1'), { zh: 'X1', en: null }, '没有名字回退货号')
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: 'Glue' }, 'X', 'zh'), { primary: '胶棒', secondary: null })
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: 'Glue' }, 'X', 'en'), { primary: 'Glue', secondary: null })
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: 'Glue' }, 'X', 'bi'), { primary: '胶棒', secondary: 'Glue' }, '双语：中文一行、英文一行')
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: null }, 'X', 'en'), { primary: '胶棒*', secondary: null }, '英文名缺失：回退中文名并加 *')
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: '  ' }, 'X', 'bi'), { primary: '胶棒', secondary: '胶棒*' }, '双语里英文名缺失也回退并加 *')
assert.deepEqual(printNameFor({ productName: '胶棒', englishName: null }, 'X', 'zh'), { primary: '胶棒', secondary: null }, '中文版不受影响')

assert.equal(localizedModelName('zh', '双钩带价签 200 · 黑', 'HK-D-PT-200'), '双钩带价签 200 · 黑')
assert.equal(localizedModelName('en', '双钩带价签 200 · 黑', 'HK-D-PT-200'), 'HK-D-PT-200', '英文版型号名是中文时用型号编码')
assert.equal(localizedModelName('en', 'Double-prong 200', 'HK-D-PT-200'), 'Double-prong 200', '型号名本来就是英文就原样显示')
assert.equal(localizedModelName('bi', '双钩带价签 200', 'HK-D-PT-200'), '双钩带价签 200 (HK-D-PT-200)')
assert.equal(localizedModelName('en', '双钩', ''), '双钩', '没有编码时只能回退名称')

// ---------------------------------------------------------------------------
// 3) 行号显示
// ---------------------------------------------------------------------------
assert.equal(displayRowNumber({ rowNumberingFrom: 1, holeRows: 41 }, 5), 5, '自板顶：内部行号即显示行号')
assert.equal(displayRowNumber({ rowNumberingFrom: 2, holeRows: 41 }, 5), 37, '自底板：倒着数（41 − 5 + 1）')
assert.equal(displayRowNumber({ rowNumberingFrom: 2, holeRows: 41 }, 41), 1, '最底下一行是第 1 行')

// ---------------------------------------------------------------------------
// 4) 数据来源：当前保存版 → 快照形状；序号 / 分组 / 统计 / 冲突
// ---------------------------------------------------------------------------
const detail = fixtureDetail()
const currentSource = sourceFromDetail(detail)
assert.equal(currentSource.origin, 'current')
assert.equal(currentSource.published, true, '状态 = 已发布')
assert.equal(currentSource.snapshot.version, 3)
assert.equal(currentSource.snapshot.products.length, 12, '商品来自详情里的 productSpecs（含英文名 / 图片 / 零售价 / 规格）')
assert.equal(currentSource.snapshot.products[0].spec.widthMm > 0, true)
assert.deepEqual(currentSource.snapshot.hookModels.map((hook) => hook.guid), [FIXTURE_HOOK.guid], '挂钩型号只保留方案用到的')
assert.equal(currentSource.by, 'admin')
const draftSource = sourceFromDetail(fixtureDetail({ status: 0, version: 4 }))
assert.equal(draftSource.published, false, '草稿')
assert.equal(draftSource.snapshot.publishedAt, null)
assert.equal(draftSource.date, '2026-11-03T14:30:00Z', '草稿页眉日期 = 最后保存时间')
const snapSource = sourceFromSnapshot(fixtureSnapshot())
assert.equal(snapSource.origin, 'published')
assert.equal(snapSource.published && snapSource.by === 'admin', true, '已发布版本：制图人 = 发布人')

const model = buildPdfModel(currentSource)
assert.equal(model.rows.length, 52)
assert.equal(model.errorCount, 0, '设计稿样例（间距 0）没有冲突')
assert.equal(model.groups.length, 8, '2 节 × 4 个行带 = 8 组')
assert.deepEqual(model.hooksPerBay, [{ bay: 1, count: 31 }, { bay: 2, count: 21 }])
assert.equal(model.mainHook?.guid, FIXTURE_HOOK.guid)
assert.equal(model.hookKinds, 1)
assert.equal(model.englishFallbackUsed, false)
// 序号 = 按「节 → 行 → 列」排序后的 1..N，设计器（Tab 顺序）与 PDF 一致
const order = model.rows.map((row) => [row.bay, row.row, row.col])
const sorted = [...order].sort((a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2])
assert.deepEqual(order, sorted, '清单按 节 → 行 → 列 排序')
assert.deepEqual(model.rows.map((row) => row.no), Array.from({ length: 52 }, (_, index) => index + 1), '序号 1..N 连续')
for (const row of model.rows) assert.equal(model.numbers.get(row.itemGuid), row.no, '气泡序号与清单行号一一对应')
assert.deepEqual(model.rows.slice(0, 3).map((row) => [row.no, row.bay, row.col, row.row, row.code, row.perHook]), [
  [1, 1, 2, 3, 'XD0001203', 12], [2, 1, 7, 3, 'XD0001203', 12], [3, 1, 12, 3, 'XD0001202', 9],
], '首行对应设计稿：节 1 · 列 2 / 行 3 · 剪刀 × 12')
assert.equal(model.rows[0].total, model.rows[0].perHook, '合计 = 面位 × 每钩，一钩一行所以面位恒为 1')
// 输入顺序不影响序号（乱序输入同一批明细）
const shuffled = fixtureDetail({ items: [...fixtureItems()].reverse() })
const shuffledModel = buildPdfModel(sourceFromDetail(shuffled))
assert.deepEqual(shuffledModel.rows.map((row) => row.itemGuid), model.rows.map((row) => row.itemGuid), '乱序输入排序结果一致')
// 完全相同位置时保持输入先后（稳定）
const twins = fixtureItems(2).map((item, index) => ({ ...item, guid: `t${index}`, bayIndex: 1, holeCol: 5, holeRow: 5 }))
const twinModel = buildPdfModel(sourceFromDetail(fixtureDetail({ items: twins })))
assert.deepEqual(twinModel.rows.map((row) => row.itemGuid), ['t0', 't1'], '同位置按原顺序')
assert.ok(twinModel.errorCount === 2 && twinModel.rows.every((row) => row.hasError), '同孔位冲突：两条都标 hasError（导出会在图上标红）')

// 货架「行号起算 = 自底板」：清单里显示倒数行号
const fromBase = buildPdfModel(sourceFromDetail(fixtureDetail({ shelf: { rowNumberingFrom: 2 } })))
assert.equal(fromBase.rows[0].row, 41 - 3 + 1, '自底板起算：内部第 3 行显示为 39')

// 英文名缺失 → englishFallbackUsed
const noEnglish = buildPdfModel(sourceFromDetail(fixtureDetail({ withoutEnglish: [FIXTURE_PRODUCTS.p9.code] })))
assert.equal(noEnglish.englishFallbackUsed, true)
assert.equal(noEnglish.rows.filter((row) => row.nameEn === null).length, 1, '尺子只挂 1 次')

assert.equal(countErrorItems([{ itemGuid: 'a', severity: 'error' }, { itemGuid: 'a', severity: 'error' }, { itemGuid: 'b', severity: 'warn' }]), 1, '冲突按条目去重，warn 不算')

// ---------------------------------------------------------------------------
// 5) 版面图比例与分页
// ---------------------------------------------------------------------------
const shelf = detail.shelfModel
const a3 = PDF_PAPERS['a3-landscape']
const a4l = PDF_PAPERS['a4-landscape']
const a4p = PDF_PAPERS['a4-portrait']
assert.equal(a3.board.widthPx, 1218)
assert.equal(a3.board.heightPx, 861)
// 页面宽高比必须与纸张一致（printUtils 的布局检查按它判定）
for (const paper of Object.values(PDF_PAPERS)) {
  for (const frame of [paper.board, paper.checklist]) {
    assert.ok(Math.abs(frame.heightPx / frame.widthPx - paper.heightMm / paper.widthMm) < 0.01, `${paper.key} 页面宽高比与纸张一致`)
  }
}

const auto2 = computeBoardLayout(a3, shelf, 2, 'auto')
assert.equal(auto2.baysPerPage, 2, 'A3 横向一页 2 节')
assert.deepEqual(auto2.pages, [{ bayFrom: 1, bayTo: 2 }])
assert.ok(auto2.scale > 0.5 && auto2.scale < 0.55, `A3 自动比例约 0.52 px/mm（设计稿 0.52），实际 ${auto2.scale}`)
assert.equal(auto2.adjusted, false)
assert.ok(auto2.svgWidth <= a3.board.widthPx - 2 * 34 - 164 + 1e-6, 'SVG 不越过右侧说明栏')
const auto3 = computeBoardLayout(a3, shelf, 3, 'auto')
assert.deepEqual(auto3.pages, [{ bayFrom: 1, bayTo: 2 }, { bayFrom: 3, bayTo: 3 }], '3 节：第 2 页只有第 3 节（超过一页自动续页）')
assert.equal(auto3.scale, auto2.scale, '所有页共用同一个比例')
const auto5 = computeBoardLayout(a3, shelf, 5, 'auto')
assert.equal(auto5.pages.length, 3)
assert.deepEqual(auto5.pages[2], { bayFrom: 5, bayTo: 5 })
const a4Auto = computeBoardLayout(a4l, shelf, 2, 'auto')
assert.equal(a4Auto.baysPerPage, 1, 'A4 横向一页 1 节')
assert.equal(a4Auto.pages.length, 2)
const a4pAuto = computeBoardLayout(a4p, shelf, 2, 'auto')
assert.equal(a4pAuto.baysPerPage, 1, 'A4 纵向一页 1 节')
assert.ok(a4pAuto.svgHeight > a4pAuto.svgWidth, 'A4 纵向：竖排缩放（图是竖的）')
assert.ok(a4pAuto.svgHeight <= a4p.board.heightPx, '竖排图放得进页面高度')
assert.equal(computeBoardLayout(a3, shelf, 1, 'auto').baysPerPage, 1, '只有 1 节：每页最多 1 节')
assert.equal(computeBoardLayout(a3, shelf, 0, 'auto').pages.length, 1, '节数异常（0）也至少一页，不崩')

assert.equal(fixedScaleFor('auto'), null)
assert.ok(Math.abs((fixedScaleFor('1:5') as number) - 2.9 / 5) < 1e-9, '1:5 = 纸上 1 mm 对应实物 5 mm')
assert.ok(Math.abs((fixedScaleFor('1:10') as number) - 0.29) < 1e-9)
const tenth = computeBoardLayout(a3, shelf, 6, 'auto'.replace('auto', '1:10') as '1:10')
assert.equal(tenth.ratio, 10, '1:10 的纸上比例就是 10')
assert.equal(tenth.adjusted, false)
assert.ok(tenth.baysPerPage >= 3, `1:10 时 A3 一页能放 3 节以上，实际 ${tenth.baysPerPage}`)
assert.equal(tenth.pages.length, Math.ceil(6 / tenth.baysPerPage))
const fifth = computeBoardLayout(a3, shelf, 2, '1:5')
assert.equal(fifth.adjusted, true, '1:5 放不下 A3 的高度：自动缩小并标记 adjusted（界面会提示）')
assert.ok(fifth.scale < (fixedScaleFor('1:5') as number))
assert.ok(fifth.svgHeight <= a3.board.heightPx, '缩小后放得进页面')

// ---------------------------------------------------------------------------
// 6) 执行清单：列 / 版式 / 分栏分页
// ---------------------------------------------------------------------------
assert.deepEqual(checklistColumns({ barcode: false, checkColumn: false }), ['no', 'bay', 'pos', 'code', 'product', 'hook', 'perHook', 'total', 'retail', 'notes'])
assert.deepEqual(checklistColumns({ barcode: true, checkColumn: true }), ['no', 'bay', 'pos', 'code', 'barcode', 'product', 'hook', 'perHook', 'total', 'retail', 'checked', 'notes'], '条码在货号后、已上货在备注前')
const layoutZh = computeChecklistLayout(a3, options())
assert.equal(layoutZh.columnsPerPage, 2, 'A3 横向清单两栏')
assert.ok(layoutZh.productWidth >= 96, '两栏时商品名一列不小于最小宽度')
for (const lang of ['zh', 'en', 'bi'] as const) {
  const layout = computeChecklistLayout(a3, options({ lang }))
  assert.equal(layout.columnsPerPage, 2, `${lang}：A3 横向清单两栏（英文 / 双语的列标题更宽，也要放得下）`)
  assert.ok(layout.productWidth >= 96, `${lang}：商品名列不小于 96，实际 ${layout.productWidth}`)
  assert.equal(Object.values(layout.columnWidths).length, 12, '各列宽度齐全')
}
assert.ok(checklistColumnWidths('en').bay > checklistColumnWidths('zh').bay && checklistColumnWidths('en').perHook > checklistColumnWidths('zh').perHook, '英文列标题更长：节 / 每钩列放宽')
assert.equal(computeChecklistLayout(a3, options({ barcode: true })).columnsPerPage, 1, '加了条码列两栏放不下 → 改单栏')
assert.equal(computeChecklistLayout(a3, options({ checkColumn: true })).columnsPerPage, 1, '加了已上货列同理')
assert.equal(computeChecklistLayout(a4p, options()).columnsPerPage, 1, 'A4 纵向清单单栏')
assert.equal(computeChecklistLayout(a4l, options()).columnsPerPage, 2, 'A4 横向清单页按 A3 横向版式排（两栏）')
assert.ok(computeChecklistLayout(a3, options({ lang: 'bi' })).rowHeight > layoutZh.rowHeight, '双语行高更高（商品名两行）')
assert.ok(computeChecklistLayout(a3, options({ lang: 'bi' })).bodyHeight < layoutZh.bodyHeight, '双语表头更高，表体更矮')

// ---- 中文版版面不变（回归基线）：页脚 24、表体高度 623、A3 自动比例 0.522、清单 2 页（每栏 23 + 20 项）----
assert.equal(footerHeight('zh'), 24, '中文页脚仍是单行 24')
assert.equal(footerHeight('en'), 38, '英文页脚允许两行：38')
assert.equal(footerHeight('bi'), 38, '双语页脚允许两行：38')
assert.equal(footerHeight('en', a4p.board), 52, 'A4 纵向版面图页很窄（541 px 内容宽）：英文 / 双语页脚三行 52')
assert.equal(footerHeight('en', a4p.checklist), 38, 'A4 纵向清单页 861 宽：两行 38')
assert.equal(footerHeight('zh', a4p.board), 24, '中文版任何页面都是 24')
assert.equal(isNarrowFrame(a4p.board), true)
assert.equal(isNarrowFrame(a3.board), false)
assert.equal(footerTop(a4p.board, 'bi'), 861 - 26 - 52, 'A4 纵向双语页脚上沿')
assert.equal(footerTop(a3.board), 861 - 26 - 24, '中文版页脚上沿不变')
assert.equal(footerTop(a3.board, 'en'), 861 - 26 - 38, '英文版页脚上沿上移 14')
assert.equal(layoutZh.bodyHeight, 623, '中文版 A3 清单表体高度与以前完全一致')
assert.equal(computeBoardLayout(a3, shelf, 2, 'auto', 'zh').scale, auto2.scale, '中文版比例不变')
assert.ok(computeBoardLayout(a4p, shelf, 2, 'auto', 'zh').scale >= computeBoardLayout(a4p, shelf, 2, 'auto', 'en').scale, '英文页脚更高，A4 纵向版面图略小一点（高度受限）')
assert.ok(computeChecklistLayout(a3, options({ lang: 'en' })).bodyHeight === layoutZh.bodyHeight - 14, '英文版清单表体高度 = 中文版 − 14（页脚多 14）')

// ---- A4 纵向版面图页底部说明栏高度：英文图例折行更多（实测内容 128），不能再用中文的 112 ----
assert.equal(bottomPanelHeight('zh'), 112, '中文说明栏高度不变')
assert.equal(bottomPanelHeight('en'), 136, '英文说明栏 136：容纳折行后的图例（内容实测 128）')
assert.equal(bottomPanelHeight('bi'), 196, '双语说明栏高度不变')
assert.ok(computeBoardLayout(a4p, shelf, 2, 'auto', 'zh').scale >= computeBoardLayout(a4p, shelf, 2, 'auto', 'en').scale, '英文说明栏更高，A4 纵向版面图不会比中文版更大')

// ---- 清单页合计行位置：窄页（A4 纵向）双语页头折得更高，合计行与表格整体下移；其余组合不变 ----
assert.equal(checklistSummaryTop(a3.checklist, 'zh'), 106, 'A3 中文合计行顶边不变')
assert.equal(checklistSummaryTop(a3.checklist, 'bi'), 106, 'A3 双语页头只有 3 行，不用下移')
assert.equal(checklistSummaryTop(a4p.checklist, 'zh'), 106)
assert.equal(checklistSummaryTop(a4p.checklist, 'en'), 106, 'A4 纵向英文页头 2 行，不用下移')
assert.equal(checklistSummaryTop(a4p.checklist, 'bi'), 152, 'A4 纵向双语：页头底边实测 144，合计行下移到 152')
assert.equal(checklistSummaryHeight(a4p.checklist, 'bi'), 56, 'A4 纵向双语：合计行折两行，占 56')
assert.equal(checklistSummaryHeight(a4p.checklist, 'en'), 36)
assert.equal(computeChecklistLayout(a4p, options({ lang: 'bi' })).tableTop, 208, 'A4 纵向双语表格顶边 = 152 + 56')
assert.equal(computeChecklistLayout(a4p, options({ lang: 'en' })).tableTop, 142, 'A4 纵向英文表格顶边不变')
assert.equal(computeChecklistLayout(a3, options({ lang: 'bi' })).tableTop, 142, 'A3 双语表格顶边不变')
assert.equal(computeChecklistLayout(a4l, options({ lang: 'bi' })).tableTop, 142, 'A4 横向清单页按 A3 横向版式，不下移')
assert.ok(computeChecklistLayout(a4p, options({ lang: 'bi' })).bodyHeight < computeChecklistLayout(a4p, options({ lang: 'en' })).bodyHeight, 'A4 纵向双语表体更矮（表格下移 66 + 表头更高）')

// ---- 清单单元格折行：英文商品名最多 2 行，超出加「…」；双语中文 1 行 + 英文最多 2 行 ----
const widthsEn = checklistColumnWidths('en')
const widthsBi = checklistColumnWidths('bi')
assert.equal(cellContentWidth(100), 89, '内容宽度 = 列宽 − 左右内边距 10 − 边框 1')
const enLayout = computeChecklistLayout(a3, options({ lang: 'en' }))
const biLayout = computeChecklistLayout(a3, options({ lang: 'bi' }))
assert.deepEqual(productCellLines('en', { nameZh: '圆珠笔 10 支', nameEn: 'Ballpoint Pen 10pk' }, enLayout.productWidth), { lines: ['Ballpoint Pen', '10pk'], secondary: [] }, '英文名放不下一行就折成两行，不再被截成「Ballpoint Pen 10p」')
assert.deepEqual(productCellLines('en', { nameZh: '修正带 2 个', nameEn: 'Correction Tape 2pk' }, enLayout.productWidth), { lines: ['Correction Tape', '2pk'], secondary: [] }, '「Correction Tape 2pk」完整显示')
assert.deepEqual(productCellLines('en', { nameZh: '订书机', nameEn: 'Stapler' }, enLayout.productWidth), { lines: ['Stapler'], secondary: [] }, '短名字仍是一行')
const longName = productCellLines('en', { nameZh: 'x', nameEn: 'Extra Long Product Name With Many Many Words Here Surely Too Long' }, enLayout.productWidth)
assert.equal(longName.lines.length, 2, '最多 2 行')
assert.ok(longName.lines[1].endsWith('…'), '超出 2 行时末行加省略号（截图不支持 text-overflow，必须自己截）')
assert.deepEqual(productCellLines('en', { nameZh: '尺子 30cm', nameEn: null }, enLayout.productWidth).lines.join(' ').endsWith('*'), true, '英文名缺失：回退中文名并带 *')
assert.deepEqual(productCellLines('zh', { nameZh: '笔记本 A6 横线 80 页', nameEn: 'Notebook A6' }, layoutZh.productWidth), { lines: ['笔记本 A6 横线 80 页'], secondary: [] }, '中文版商品名单元格不变（一整段、不折行）')
const biProduct = productCellLines('bi', { nameZh: '笔记本 A6 横线 80 页', nameEn: 'Notebook A6 Ruled 80 Pages' }, biLayout.productWidth)
assert.equal(biProduct.lines.join('').replace(/\s/g, ''), '笔记本A6横线80页', '双语：中文长名字折成两行完整显示（内容一个字不少），不再被裁成「…横线 8」')
assert.equal(biProduct.lines.length, 2)
assert.ok(biProduct.secondary.length === 1 && biProduct.lines.length + biProduct.secondary.length <= 3, '双语：中文 2 行时英文名只剩 1 行，合计不超过 3 行（行高 36 放得下）')
const biVeryLong = productCellLines('bi', { nameZh: '这是一个非常非常非常非常非常长的商品名称用来测试省略号', nameEn: 'An Extremely Long English Product Name That Cannot Possibly Fit In Two Lines At All' }, biLayout.productWidth)
assert.equal(biVeryLong.lines.length, 2, '中文名最多 2 行')
assert.ok(biVeryLong.lines[1].endsWith('…'), '中文名超出 2 行加省略号')
assert.equal(biVeryLong.secondary.length, 1, '英文名取剩余 1 行')
assert.ok(biVeryLong.secondary[0].endsWith('…'), '英文名放不下也加省略号')
assert.deepEqual(productCellLines('bi', { nameZh: '剪刀 170mm', nameEn: 'Scissors 170mm' }, biLayout.productWidth), { lines: ['剪刀 170mm'], secondary: ['Scissors 170mm'] }, '双语短名字：中文一行、英文一行（与以前一致）')
assert.deepEqual(hookCellLines('zh', { hookName: '双钩带价签 200', hookCode: 'HK-D-PT-200' }, checklistColumnWidths('zh').hook), { lines: ['双钩带价签 200'], secondary: [] }, '中文挂钩名放得下一行')
assert.deepEqual(hookCellLines('zh', { hookName: '双钩带价签 · 臂长 200', hookCode: 'HK-D-PT-200' }, checklistColumnWidths('zh').hook), { lines: ['双钩带价签 · 臂长', '200'], secondary: [] }, '放不下一行就折两行，臂长「200」不会被截掉')
assert.equal(hookCellLines('zh', { hookName: '双钩带价签 · 臂长 200 · 黑色镀铬款', hookCode: 'X' }, checklistColumnWidths('zh').hook).lines.length, 2, '中文挂钩名最多折 2 行（保住臂长等区分信息，而不是截成「… 臂长 2」）')
assert.deepEqual(hookCellLines('en', { hookName: '双钩带价签 · 臂长 200', hookCode: 'HK-D-PT-200' }, widthsEn.hook), { lines: ['HK-D-PT-200'], secondary: [] }, '英文版挂钩列只放型号编码')
assert.deepEqual(hookCellLines('bi', { hookName: '双钩带价签 · 臂长 200', hookCode: 'HK-D-PT-200' }, widthsBi.hook), { lines: ['双钩带价签 · 臂长', '200'], secondary: ['HK-D-PT-200'] }, '双语：名称（放不下折两行，不会把「臂长」拆开）+ 型号编码')
assert.equal(hookCellLines('bi', { hookName: '双钩带价签 · 臂长 200 · 黑色镀铬款 · 加长版', hookCode: 'HK-D-PT-200' }, widthsBi.hook).lines.length, 2, '双语挂钩名最多 2 行 + 编码 1 行')

const pagesZh = paginateChecklist(model.groups, layoutZh)
assert.equal(pagesZh.length, 2, '52 行在 A3 横向清单：2 页')
const flat = pagesZh.flatMap((page) => page.columns.flatMap((column) => column.filter((entry) => entry.kind === 'row').map((entry) => (entry as { row: { no: number } }).row.no)))
assert.deepEqual(flat, Array.from({ length: 52 }, (_, index) => index + 1), '每一行恰好出现一次、按序号顺序')
assert.equal(pagesZh[0].firstNo, 1)
assert.equal(pagesZh[0].lastNo + 1, pagesZh[1].firstNo, '页与页之间序号连续')
assert.equal(pagesZh[1].lastNo, 52)
for (const page of pagesZh) {
  assert.ok(page.columns.length <= layoutZh.columnsPerPage, '每页栏数不超过版式的栏数')
  for (const column of page.columns) {
    const used = column.reduce((sum, entry) => sum + (entry.kind === 'group' ? layoutZh.groupHeight : layoutZh.rowHeight), 0)
    assert.ok(used <= layoutZh.bodyHeight, `一栏的高度不超过表体可用高度（${used} ≤ ${layoutZh.bodyHeight}）`)
    assert.equal(column[0]?.kind, 'group', '每栏顶部先是分组标题（被栏切开的组会重复标题）')
    assert.notEqual(column[column.length - 1]?.kind, 'group', '分组标题后面一定有数据行，不留孤零零的标题')
    const rows = column.filter((entry) => entry.kind === 'row').length
    const isLastColumn = page === pagesZh[pagesZh.length - 1] && column === page.columns[page.columns.length - 1]
    // 满栏约 20 行（设计稿 20 行）；最后一栏装剩下的，可以少
    assert.ok(rows <= 26 && (isLastColumn || rows >= 15), `每栏约 20 行，实际 ${rows}`)
  }
}
// 被切开的组：下一栏顶部重复同一个分组标题
const firstColumn = pagesZh[0].columns[0]
const secondColumn = pagesZh[0].columns[1]
const lastGroupOfFirst = [...firstColumn].reverse().find((entry) => entry.kind === 'group') as { bay: number; row: number }
const firstGroupOfSecond = secondColumn[0] as { kind: string; bay: number; row: number }
const lastRowOfFirst = firstColumn[firstColumn.length - 1] as { kind: 'row'; row: { bay: number; row: number } }
const firstRowOfSecond = secondColumn[1] as { kind: 'row'; row: { bay: number; row: number } }
if (lastRowOfFirst.row.bay === firstRowOfSecond.row.bay && lastRowOfFirst.row.row === firstRowOfSecond.row.row) {
  assert.deepEqual([firstGroupOfSecond.bay, firstGroupOfSecond.row], [lastGroupOfFirst.bay, lastGroupOfFirst.row], '同一组被栏切开：下一栏重复分组标题')
}
// 单栏版式（A4 纵向）：一页一栏
const pagesPortrait = paginateChecklist(model.groups, computeChecklistLayout(a4p, options()))
assert.equal(pagesPortrait.length, 2)
assert.ok(pagesPortrait.every((page) => page.columns.length === 1))
// 极端：一栏连一行都放不下也要硬放一行（不死循环）
const tiny = paginateChecklist(model.groups.slice(0, 1), { columnsPerPage: 1, bodyHeight: 1, rowHeight: 27, groupHeight: 24 })
assert.equal(tiny.length >= 1 && tiny.flatMap((page) => page.columns.flat()).filter((entry) => entry.kind === 'row').length, model.groups[0].rows.length, '极端高度也不丢行')
assert.deepEqual(paginateChecklist([], layoutZh), [], '没有明细：没有清单页')

// ---------------------------------------------------------------------------
// 7) 整体分页
// ---------------------------------------------------------------------------
const planAll = planPdfPages(model, options())
assert.equal(planAll.boardPageCount, 1)
assert.equal(planAll.checklistPageCount, 2)
assert.equal(planAll.pageCount, 3, '版面图 1 页 + 清单 2 页（与设计稿「共 3 页」一致）')
assert.deepEqual(planAll.pages.map((page) => [page.kind, page.pageNo]), [['board', 1], ['checklist', 2], ['checklist', 3]], '页码连续，先版面图后清单')
assert.equal(planAll.rowCount, 52)
assert.deepEqual(pageFrameOf(a4l, 'board'), a4l.board)
assert.deepEqual(pageFrameOf(a4l, 'checklist'), a4l.checklist)
const boardOnly = planPdfPages(model, options({ checklist: false }))
assert.equal(boardOnly.pageCount, 1)
assert.equal(boardOnly.checklistPageCount, 0)
const listOnly = planPdfPages(model, options({ board: false }))
assert.equal(listOnly.boardPageCount, 0)
assert.equal(listOnly.pages[0].pageNo, 1, '只导清单：页码从 1 开始')
assert.equal(hasPdfContent(options({ board: false, checklist: false })), false, '两项都不选没有内容')
assert.equal(planPdfPages(model, options({ board: false, checklist: false })).pageCount, 0)
const emptyModel = buildPdfModel(sourceFromDetail(fixtureDetail({ items: [] })))
const emptyPlan = planPdfPages(emptyModel, options())
assert.equal(emptyPlan.checklistPageCount, 1, '空方案：清单仍有 1 页（只有表头与合计），不会导出一份空 PDF')
assert.equal(emptyModel.errorCount, 0)
const longPlan = planPdfPages(buildPdfModel(sourceFromDetail(fixtureDetail({ bayCount: 5 }))), options())
assert.equal(longPlan.boardPageCount, 3, '5 节：A3 每页 2 节，3 页版面图')

// ---------------------------------------------------------------------------
// 8) 页眉 / 文件名 / 金额
// ---------------------------------------------------------------------------
const published = buildHeaderInfo(model, zhText)
assert.equal(published.title, '商品陈列图 · 文具挂板 A1')
assert.deepEqual(published.meta, [
  '货架 单面洞洞板 900×1200 · 白 · 2 节',
  '挂钩 双钩带价签 200',
  '版本 v3 · 已发布 2026-11-04',
  '制图 admin',
], '页眉第二行：货架 / 挂钩 / 版本日期 / 制图（日期取悉尼时区：UTC 11-03 14:30 = 悉尼 11-04）')
const draftHeader = buildHeaderInfo(buildPdfModel(draftSource), zhText)
assert.equal(draftHeader.meta[2], '版本 v4 · 草稿（未发布）· 保存于 2026-11-04')
const enHeader = buildHeaderInfo(model, enText)
assert.equal(enHeader.title, 'Planogram · 文具挂板 A1', '方案名是用户输入的，原样显示')
assert.equal(enHeader.meta[0], 'Shelf: G-S-PEG-90x30x120 · 2 bays', '英文版货架名是中文时用型号编码')
assert.equal(enHeader.meta[1], 'Hook: HK-D-PT-200')
assert.equal(enHeader.meta[2], 'v3 · Published 2026-11-04')
assert.equal(buildHeaderInfo(model, biText).title, '商品陈列图 · 文具挂板 A1 / Planogram · 文具挂板 A1')
assert.equal(buildHeaderPreviewLine(model, zhText), '文具挂板 A1 · 单面洞洞板 900×1200 · 白 · 间距 0 / 0 · v3 · 2026-11-04 · 制图 admin · 页码', '弹窗里的页眉文案预览')
assert.equal(formatPdfDate('2026-11-03T14:30:00Z'), '2026-11-04')
assert.equal(formatPdfDate(null), '—')

// 文件名：Planogram_<方案名>_v<版本>_<zh|en|bi>_<yyyymmdd>.pdf，日期取悉尼当天
const fixedDate = new Date('2026-11-04T20:00:00Z') // 悉尼已经是 11-05 07:00
assert.equal(buildPdfFileName({ planName: '文具挂板 A1', version: 3, lang: 'zh', date: fixedDate }), 'Planogram_文具挂板_A1_v3_zh_20261105.pdf')
assert.equal(buildPdfFileName({ planName: 'A/B:C*D?', version: 12, lang: 'en', date: fixedDate }), 'Planogram_A_B_C_D__v12_en_20261105.pdf', '方案名里的非法字符换成 _')
assert.equal(buildPdfFileName({ planName: '   ', version: 1, lang: 'bi', date: fixedDate }), 'Planogram_plan_v1_bi_20261105.pdf', '方案名为空回退 plan')
assert.equal(normalizePdfFileName('我的陈列图', 'x.pdf'), '我的陈列图.pdf', '补 .pdf 后缀')
assert.equal(normalizePdfFileName('abc.PDF', 'x.pdf'), 'abc.pdf', '已有 .pdf 后缀不重复')
assert.equal(normalizePdfFileName('a/b\\c.pdf', 'x.pdf'), 'a_b_c.pdf', '路径分隔符换成 _')
assert.equal(normalizePdfFileName('   ', 'x.pdf'), 'x.pdf', '空名回退默认名')

assert.equal(formatMoney(4), '$4.00')
assert.equal(formatMoney(1291.5), '$1,291.50')
assert.equal(formatMoney(-7.5), '-$7.50')
assert.equal(formatMoneyTotal(1291), '$1,291', '整数金额不带小数（设计稿「$1 291」）')
assert.equal(formatMoneyTotal(1291.5), '$1,291.50')

console.log('sheetLogic.test: ok')
