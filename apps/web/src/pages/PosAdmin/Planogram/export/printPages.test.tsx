import { renderToStaticMarkup } from 'react-dom/server'
import enMessages from '../planogramMessages.en.json'
import zhMessages from '../planogramMessages.zh.json'
import PlanogramPrintPages from './PlanogramPrintPages'
import { fixtureDetail, fixtureItems, FIXTURE_PRODUCTS } from './sheetFixtures'
import { buildPdfModel, createPdfTranslator, DEFAULT_PDF_OPTIONS, planPdfPages, sourceFromDetail, type MessageTree, type PdfModel, type PdfOptions } from './sheetLogic'

// PDF 页面 DOM 渲染契约：用设计稿样例数据静态渲染（服务端渲染），核对页数、版面图 SVG（气泡 / 价格 / 孔位号 / 实拍图 / 冲突标红）、
// 执行清单（分组 / 列 / 双语 / 英文名回退星号）、页脚页码。出图 / 打印由真实浏览器验证（目测与真实 PDF 栅格对照）。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}
function count(haystack: string, needle: string) {
  return haystack.split(needle).length - 1
}

const translate = createPdfTranslator(zhMessages as unknown as MessageTree, enMessages as unknown as MessageTree)
const model = buildPdfModel(sourceFromDetail(fixtureDetail()))
const options = (patch: Partial<PdfOptions> = {}): PdfOptions => ({ ...DEFAULT_PDF_OPTIONS, ...patch })
const noImages = new Map<string, string>()

function render(patch: Partial<PdfOptions> = {}, overrides: { model?: PdfModel; images?: ReadonlyMap<string, string>; pageFilter?: (page: { pageNo: number }) => boolean } = {}) {
  const used = options(patch)
  const useModel = overrides.model ?? model
  const plan = planPdfPages(useModel, used)
  return {
    plan,
    html: renderToStaticMarkup(
      <PlanogramPrintPages model={useModel} plan={plan} options={used} images={overrides.images ?? noImages} translate={translate} pageFilter={overrides.pageFilter} />,
    ),
  }
}
const pagesOf = (html: string) => html.split('<section ').slice(1)

// ---------- 页数与结构（A3 横向 · 中文） ----------
const { plan, html } = render()
const pages = pagesOf(html)
assert(pages.length === 3 && plan.pageCount === 3, 'A3 横向：版面图 1 页 + 清单 2 页 = 3 页')
assert(count(html, 'class="planogram-pdf-page"') === 3, '每页一个 .planogram-pdf-page（printUtils 按它逐页截图）')
assert(pages[0].includes('data-page-kind="board"') && pages[1].includes('data-page-kind="checklist"') && pages[2].includes('data-page-kind="checklist"'), '先版面图后清单')
assert(pages[0].includes('width:1218px;height:861px'), 'A3 横向页面逻辑尺寸 1218 × 861（与纸张同比例）')
assert(html.includes('第 1 页 / 共 3 页') && html.includes('第 3 页 / 共 3 页'), '页脚页码')

// ---------- 版面图页 ----------
const board = pages[0]
assert(board.includes('商品陈列图 · 文具挂板 A1'), '标题')
assert(board.includes('货架 单面洞洞板 900×1200 · 白 · 2 节') && board.includes('版本 v3 · 已发布 2026-11-04') && board.includes('制图 admin'), '页眉第二行：货架 / 版本 / 制图')
assert(board.includes('HB Admin Platform'), '右上角平台名')
assert(board.includes('比例约 1 : 5.6') && board.includes('孔距 25 × 25 · 34 列 × 41 行 / 节') && board.includes('行号自板顶 1 起'), '右上角比例 / 孔距 / 编号说明')
assert(board.includes('节 1 · 900 mm') && board.includes('节 2 · 900 mm') && board.includes('底板 · 深 300 mm'), '节标题与底板文字')
assert(board.includes('class="planogram-pdf-board-svg"') && board.includes('font-family='), 'SVG 自带字体（截图时页面 CSS 不会跟进去）')
assert(board.includes('图例') && board.includes('上货要点') && board.includes('板面利用 39%'), '图例 / 合计 / 上货要点')
assert(board.includes('校验：无越界 · 无重叠 · 间距 ≥ 0 / 0 mm（发布版 v3 已通过）'), '校验行（已发布版本）')
assert(!board.includes('role="button"') && !board.includes('data-item-guid') && !board.includes('aria-label'), '打印版面图没有交互属性')
assert(!board.includes('#1677ff" stroke-width') && !board.includes('planogram-designer-item'), '没有选中手柄 / 设计器类名')
// 序号气泡：1..52 各一个，与清单行号一一对应
assert(count(board, 'r="6.5" fill="#1f1f1f"') === 52, '52 个序号气泡')
for (const n of [1, 26, 52]) assert(board.includes(`>${n}</text>`), `气泡序号 ${n}`)
assert(board.includes('$4.00') && board.includes('$3.50'), '价签价格')
assert(board.includes('fill="#1677ff"'), '孔位列号 / 行号（蓝色）')
assert(board.includes('>1211 ×15<') || board.includes('1211 ×15'), '货号后四位 × 每钩挂数（打印格式）')
assert(board.includes('<pattern'), '孔阵')
assert(count(board, '<svg') === 1, '一页一个 SVG')

// ---------- 选项开关 ----------
const noBubbles = pagesOf(render({ bubbles: false }).html)[0]
assert(count(noBubbles, 'r="6.5" fill="#1f1f1f"') === 0, '关闭序号气泡')
const noPrice = pagesOf(render({ prices: false }).html)[0]
assert(!noPrice.includes('$4.00') && noPrice.includes('stroke="#8c8c8c"'), '关闭价格：价签框还在，价格不印')
const noCoords = pagesOf(render({ coords: false }).html)[0]
assert(!noCoords.includes('fill="#1677ff"'), '关闭孔位列 / 行号')
const boardOnly = render({ checklist: false })
assert(boardOnly.plan.pageCount === 1 && !boardOnly.html.includes('data-page-kind="checklist"'), '只导版面图')
const checklistOnly = render({ board: false })
assert(checklistOnly.plan.pageCount === 2 && !checklistOnly.html.includes('planogram-pdf-board-svg'), '只导清单')
assert(checklistOnly.html.includes('第 1 页 / 共 2 页'), '只导清单页码从 1 开始')

// ---------- 实拍图 ----------
const images = new Map([[FIXTURE_PRODUCTS.p3.code, 'data:image/png;base64,AAAA'], [FIXTURE_PRODUCTS.p9.code, 'data:image/png;base64,BBBB']])
const withImages = pagesOf(render({ images: true }, { images }).html)[0]
// 剪刀 p3 在设计稿里出现 4 次（节 1 两次、节 2 两次），尺子 p9 1 次
assert(count(withImages, '<image') === 5, `实拍图只画有图的商品：${count(withImages, '<image')}`)
assert(withImages.includes('href="data:image/png;base64,AAAA"'), '图片以 data URL 嵌入（独立 SVG 图片不能再加载外链）')
assert(count(board, '<image') === 0, '默认不印实拍图')

// ---------- 执行清单（中文） ----------
const list1 = pages[1]
assert(list1.includes('执行清单 · 文具挂板 A1') && list1.includes('按「节 → 行 → 列」顺序'), '清单标题与排序说明')
assert(list1.includes('孔位：列 / 行（双钩取左脚所在孔）') && list1.includes('每钩挂数 = 单钩陈列量'), '右上角说明')
assert(list1.includes('节 1 · 第 3 行挂孔') && list1.includes('节 1 · 第 13 行挂孔'), '分组标题')
for (const header of ['货号', '商品', '挂钩', '每钩', '合计', '零售', '备注']) assert(list1.includes(`>${header}</div>`), `列名 ${header}`)
assert(!list1.includes('>条码</div>') && !list1.includes('>已上货</div>'), '默认不含条码 / 已上货列')
assert(list1.includes('XD0001203') && list1.includes('剪刀 170mm') && list1.includes('双钩带价签 200') && list1.includes('$4.00'), '首行：货号 / 名称 / 挂钩 / 零售')
assert(list1.includes('>2 / 3<'), '孔位列 / 行')
assert(count(list1, '<table') === 2, '两栏：两张表')
assert(list1.includes('12 </b>') || list1.includes('<b>12</b>') || list1.includes('<b>12'), '合计行：商品数')
assert(list1.includes('节 1：31 钩') && list1.includes('节 2：21 钩'), '每节挂钩数')
assert(list1.includes('续第 3 页（#40–#52）'), '续页提示：下一页的页码与序号范围')
assert(!pages[2].includes('续第'), '最后一页没有续页提示')
assert(list1.includes('缺货或尺寸不符时，在「备注」栏记录后回传'), '页脚说明')
assert(count(list1, 'class="pdfp-badge"') + count(pages[2], 'class="pdfp-badge"') === 52, '清单序号徽标一共 52 个（含两页）')

// 条码 / 已上货
const extra = pagesOf(render({ barcode: true, checkColumn: true }).html)
assert(extra[1].includes('>条码</div>') && extra[1].includes('>已上货</div>'), '勾选后多出条码列与已上货列')
assert(extra[1].includes('93D0001203'), '条码内容')
assert(count(extra[1], 'class="pdfp-checkbox"') > 10, '已上货列是空白方框')
assert(count(extra[1], '<table') === 1, '多了列两栏放不下 → 单栏')

// ---------- 英文 / 双语 ----------
const en = pagesOf(render({ lang: 'en' }).html)
assert(en[0].includes('Planogram · 文具挂板 A1') && en[0].includes('Legend') && en[0].includes('Set-up notes'), '英文标题 / 图例 / 要点')
assert(en[0].includes('Bay 1 · 900 mm') && en[0].includes('Base shelf · 300 mm deep'), '英文节标题 / 底板')
assert(en[0].includes('Shelf: G-S-PEG-90x30x120 · 2 bays') && en[0].includes('Hook: HK-D-PT-200'), '英文页眉：型号用编码')
assert(en[0].includes('Page 1 / 3'), '英文页码')
assert(en[0].includes('Scissors') && en[0].includes('Ballpoint'), '版面图上商品用英文名（框里按单词折行）')
assert(!en[0].includes('剪刀') && !en[0].includes('图例'), '英文版面图不夹带中文商品名 / 图例')
assert(en[1].includes('Set-up checklist · 文具挂板 A1') && en[1].includes('Bay 1 · hook row 3') && en[1].includes('>Per hook</div>') && en[1].includes('>Col/Row</div>'), '英文清单：Bay / Col / Row / Hook / Per hook')
assert(en[1].includes('Scissors 170mm') && !en[1].includes('剪刀'), '清单商品名用英文名')
assert(en[1].includes('HK-D-PT-200'), '清单挂钩列用型号编码')

const bi = pagesOf(render({ lang: 'bi' }).html)
const zhList1 = () => pages[1]
assert(bi[0].includes('商品陈列图 · 文具挂板 A1 / Planogram · 文具挂板 A1'), '双语标题')
assert(bi[0].includes('图例') && bi[0].includes('Legend'), '双语图例：中文一行、英文一行')
assert(bi[0].includes('class="is-secondary"') || bi[0].includes('is-secondary'), '英文行是次要样式')
assert(bi[1].includes('>节</div>') && bi[1].includes('>Bay</div>'), '双语列名：中文 + 英文两行')
assert(bi[1].includes('剪刀 170mm') && bi[1].includes('Scissors 170mm'), '双语商品名：中文一行、英文一行')
assert(bi[0].includes('剪刀') && bi[0].includes('Scissors'), '版面图上双语商品名（中文 + 英文）')
assert(bi[1].includes('第 1 页 / 共 3 页 / Page 1 / 3') || bi[0].includes('第 1 页 / 共 3 页 / Page 1 / 3'), '双语页脚页码')

// ---------- 英文 / 双语清单：商品名折行不截断、表头可折行、页脚两行 ----------
assert(en[1].includes('<table class="pdfp-table is-latin-head is-en"'), '英文表格带 is-en：表头允许折行（不同机器字体宽度不同，宁可折行也不截断）')
assert(bi[1].includes('<table class="pdfp-table is-latin-head is-bi"') && !bi[1].includes('is-en'), '双语表头是固定两行（中文 + 英文），不折行；带 is-bi 收紧行距')
assert(en[1].includes('<td class="is-lines"><div>Ballpoint Pen</div><div>10pk</div></td>'), '英文商品名放不下一行：折成两行「Ballpoint Pen / 10pk」，而不是被截成「Ballpoint Pen 10p」')
assert(en[1].includes('<td class="is-lines"><div>Correction Tape</div><div>2pk</div></td>') || en[2].includes('<td class="is-lines"><div>Correction Tape</div><div>2pk</div></td>'), '「Correction Tape 2pk」完整显示')
assert(en[1].includes('<td class="is-lines"><div>Scissors 170mm</div></td>'), '放得下的名字仍是一行')
assert(en[1].includes('<td class="is-lines is-hook"><div>HK-D-PT-200</div></td>'), '英文版挂钩列只放型号编码')
assert(!zhList1().includes('is-lines"><div>剪刀'), '中文版商品名单元格不变（普通 td，不走折行逻辑）')
assert(zhList1().includes('<td><div>剪刀 170mm</div></td>'), '中文版商品名：一个 div 一整段（与以前一致）')
assert(bi[1].includes('<td class="is-lines"><div>剪刀 170mm</div><div class="is-secondary">Scissors 170mm</div></td>'), '双语商品名：中文一行、英文一行')
assert(bi[1].includes('<td class="is-lines is-hook"><div>双钩带价签 200</div><div class="is-secondary">HK-D-PT-200</div></td>'), '双语挂钩列：名称 + 型号编码两行')
// 页脚：英文 / 双语两行（高度 38 + is-multiline），中文单行（高度 24，版面与以前一致）
assert(en[0].includes('class="pdfp-foot is-multiline"') && en[0].includes('height:38px'), '英文版页脚允许两行')
assert(bi[0].includes('class="pdfp-foot is-multiline"') && bi[2].includes('class="pdfp-foot is-multiline"'), '双语版每页页脚都允许两行')
assert(pages[0].includes('class="pdfp-foot"') && pages[0].includes('height:24px') && !pages[0].includes('is-multiline'), '中文版页脚仍是单行 24')
assert(!pages[0].includes('pdfp-brand') && pages[0].includes('class="pdfp-foot-right"'), '中文版品牌行仍在页脚右侧')
assert(en[0].includes('class="pdfp-foot-right"') && !en[0].includes('pdfp-brand'), 'A3 横向英文版：品牌行在页脚右侧（宽页面放得下）')
assert(en[0].includes('Validation: no overflow · no overlap · gap ≥ 0 / 0 mm (published v3 passed)'), '英文页脚校验行文案完整')

// ---------- 英文名缺失：回退中文名 + 星号 + 页脚说明 ----------
const fallbackModel = buildPdfModel(sourceFromDetail(fixtureDetail({ withoutEnglish: [FIXTURE_PRODUCTS.p9.code] })))
const fallbackEn = pagesOf(render({ lang: 'en' }, { model: fallbackModel }).html)
assert(fallbackEn[0].includes('尺子 30cm*') || fallbackEn[0].includes('尺子'), '版面图上回退中文名')
assert(fallbackEn[1].includes('尺子 30cm*') || fallbackEn[2].includes('尺子 30cm*'), '清单里英文名缺失：中文名 + 星号')
assert(fallbackEn[0].includes('* No English name') && fallbackEn[1].includes('* No English name'), '页脚说明「* 无英文名」（版面图页与清单页）')
assert(fallbackEn[0].includes('class="pdfp-foot is-multiline"') && fallbackEn[1].includes('class="pdfp-foot is-multiline"'), '页脚说明所在的英文页脚允许两行，说明不会被挤掉')
const fallbackZh = pagesOf(render({ lang: 'zh' }, { model: fallbackModel }).html)
assert(!fallbackZh[0].includes('无英文名') && !fallbackZh.join('').includes('30cm*'), '中文版不加星号 / 说明')
const fallbackBi = pagesOf(render({ lang: 'bi' }, { model: fallbackModel }).html)
assert(fallbackBi[1].includes('尺子 30cm*') || fallbackBi[2].includes('尺子 30cm*'), '双语里英文行回退并加星号')

// ---------- 冲突标红 ----------
const twins = fixtureItems(2).map((item, index) => ({ ...item, guid: `t${index}`, bayIndex: 1, holeCol: 5, holeRow: 5 }))
const conflictModel = buildPdfModel(sourceFromDetail(fixtureDetail({ items: twins, status: 0, version: 2 })))
const conflictPages = pagesOf(render({}, { model: conflictModel }).html)
assert(conflictPages[0].includes('stroke="#ff4d4f"') && conflictPages[0].includes('>!</text>'), '版面图上冲突商品红框 + 红色徽标')
assert(conflictPages[0].includes('校验：有 2 处冲突，已在图上标红（发布前须解决）'), '校验行写明冲突数')
assert(conflictPages[0].includes('红框 = 冲突'), '图例里说明红框')
assert(conflictPages[1].includes('class="is-error"') && conflictPages[1].includes('pdfp-badge is-error'), '清单里冲突行标红')
assert(conflictPages[0].includes('版本 v2 · 草稿（未发布）'), '草稿版页眉')
assert(!pages[0].includes('红框 = 冲突'), '没有冲突时不加红框图例')

// ---------- 纸张 ----------
const a4l = pagesOf(render({ paper: 'a4-landscape' }).html)
assert(a4l.length === 4 && a4l[0].includes('width:861px;height:609px') && a4l[1].includes('data-page-no="2"'), 'A4 横向：版面图每页 1 节（2 页），页面 861 × 609')
assert(a4l[2].includes('width:1218px;height:861px'), 'A4 横向的清单页按 A3 横向版式排（比例相同）')
const a4p = pagesOf(render({ paper: 'a4-portrait' }).html)
assert(a4p[0].includes('width:609px;height:861px') && a4p[0].includes('pdfp-panel-wrap is-bottom') && a4p[0].includes('pdfp-head is-compact'), 'A4 纵向：竖排页面、说明栏在下方、页头紧凑')
assert(a4p[2].includes('width:861px;height:1218px') && count(a4p[2], '<table') === 1, 'A4 纵向清单单栏')

const a4pEn = pagesOf(render({ paper: 'a4-portrait', lang: 'en' }).html)
const a4pBi = pagesOf(render({ paper: 'a4-portrait', lang: 'bi' }).html)
assert(a4pEn[0].includes('height:52px') && a4pEn[0].includes('class="pdfp-brand"') && !a4pEn[0].includes('class="pdfp-foot-right"'), 'A4 纵向英文版面图页：页脚 52（三行），品牌行挪到页码那一行')
assert(a4pBi[0].includes('height:52px') && a4pBi[0].includes('class="pdfp-brand"'), 'A4 纵向双语版面图页同上')
assert(a4pBi[0].includes('校验：无越界 · 无重叠 · 间距 ≥ 0 / 0 mm（发布版 v3 已通过） / Validation: no overflow · no overlap · gap ≥ 0 / 0 mm (published v3 passed)'), 'A4 纵向双语校验行中英文都完整保留（不再只留中文）')
assert(a4pEn[2].includes('height:38px') && a4pEn[2].includes('class="pdfp-foot-right"'), 'A4 纵向的清单页（861 宽）仍是两行页脚、品牌在右侧')

// A4 纵向双语清单页：页头折 4 行以上，合计行下移到 152、表格下移到 208（实测页头底边 144），不再压在页头上；英文 / 中文位置不变
const a4pBiChecklist = a4pBi.find((page) => page.includes('data-page-kind="checklist"')) ?? ''
assert(a4pBiChecklist.includes('class="pdfp-summary" style="left:34px;right:34px;top:152px"'), 'A4 纵向双语清单页合计行顶边 152')
assert(a4pBiChecklist.includes('class="pdfp-tables" style="left:34px;right:34px;top:208px;gap:16px"'), 'A4 纵向双语清单页表格顶边 208')
const a4pEnChecklist = a4pEn.find((page) => page.includes('data-page-kind="checklist"')) ?? ''
assert(a4pEnChecklist.includes('class="pdfp-summary" style="left:34px;right:34px;top:106px"') && a4pEnChecklist.includes('top:142px;gap:16px'), 'A4 纵向英文清单页合计行 / 表格位置不变')

// ---------- 预览缩略图只渲染第 1 页 ----------
const first = render({}, { pageFilter: (page) => page.pageNo === 1 })
assert(count(first.html, 'class="planogram-pdf-page"') === 1 && first.plan.pageCount === 3, '缩略图只渲染第 1 页，页码仍写「共 3 页」')

// ---------- 空方案 ----------
const emptyModel = buildPdfModel(sourceFromDetail(fixtureDetail({ items: [] })))
const empty = render({}, { model: emptyModel })
assert(empty.plan.pageCount === 2 && empty.html.includes('这个方案还没有商品'), '空方案：版面图 + 一页空清单')

console.log('printPages.test: ok')
