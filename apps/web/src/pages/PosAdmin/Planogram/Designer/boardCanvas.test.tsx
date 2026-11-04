import { renderToStaticMarkup } from 'react-dom/server'
import type { ProductSpecListItemDto } from '../../../../types/planogram'
import { buildPlaced, validateLayout, type LayoutContext, type LayoutHook, type LayoutShelf, type LayoutSpec } from '../engine/geometry'
import BoardCanvas, { BoardPrintSvg, buildItemViews, canvasMetrics, fitZoomFor, printMetrics, type BoardCanvasLabels } from './BoardCanvas'
import { buildDropHint, conflictsByItem, createDesignerItem, type DesignerItem } from './designerLogic'

// 画布渲染契约：用设计稿样例数据静态渲染（服务端渲染，不跑指针交互），核对关键几何、冲突标记、选中覆盖层、显示开关与只读。
// 指针拖动 / 拖入 / 键盘交互在真实浏览器里验证（docs 里记录的目测），这里守住「渲染不崩 + 关键元素在」。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function count(haystack: string, needle: string) {
  return haystack.split(needle).length - 1
}

const shelf: LayoutShelf = {
  bayWidthMm: 900, totalHeightMm: 1200, uprightWidthMm: 30, baseTopHeightMm: 150,
  holePitchXMm: 25, holePitchYMm: 25, firstHoleLeftMm: 25, firstHoleTopMm: 25, holeCols: 33, holeRows: 41,
}
const hook: LayoutHook = {
  hasPriceTagArm: true, armLengthMm: 200, dropMm: 50, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26, tagBottomAboveArmMm: 10, holeSpanX: 2, holeSpanY: 1, loadKg: 3,
}
const specs: Record<string, LayoutSpec> = {
  PEN: { widthMm: 90, heightMm: 185, depthMm: 20, hangHoleOffsetMm: 10, unitWeightG: 20, hookQtyOverrides: {}, retailPrice: 3.5 },
  NOTE: { widthMm: 110, heightMm: 155, depthMm: 12, hangHoleOffsetMm: 10, unitWeightG: 95, hookQtyOverrides: {}, retailPrice: 2.5 },
}
const specRows: Record<string, ProductSpecListItemDto> = {
  PEN: { productCode: 'PEN', itemNumber: 'XD0001202', barcode: '1', productName: '圆珠笔 10 支', englishName: 'Ballpoint Pen 10pk', productImage: null, retailPrice: 3.5, hasSpec: true, referencedPlanCount: 1, spec: null },
  NOTE: { productCode: 'NOTE', itemNumber: 'XD0001211', barcode: '2', productName: '笔记本 A6', englishName: null, productImage: null, retailPrice: 2.5, hasSpec: true, referencedPlanCount: 1, spec: null },
}
const ctx: LayoutContext = { shelf, hooks: { H: hook }, specs, bayCount: 2, gaps: { gapXMm: 10, gapYMm: 10 } }

const item = (guid: string, productCode: string, bayIndex: number, col: number, row: number): DesignerItem =>
  createDesignerItem({ guid, productCode, hookModelGuid: 'H', bayIndex, holeCol: col, holeRow: row })
// i1 / i2 正常；i3 挂在底部，底边越过底板面（OUT_OF_BOARD）；i4 缺参数（PRODUCT_SPEC_MISSING）
const items = [item('i1', 'PEN', 1, 5, 3), item('i2', 'NOTE', 2, 8, 3), item('i3', 'PEN', 1, 12, 38), item('i4', 'GHOST', 1, 20, 3)]
const result = validateLayout(ctx, items)
const placed = buildPlaced(ctx, items)
const conflictsByGuid = conflictsByItem(result.conflicts)

const labels: BoardCanvasLabels = {
  group: '陈列画布',
  bayTitle: (bay, width) => `节 ${bay} · ${width} mm`,
  base: '底板 · 深 300 mm',
  specChanged: '参数已变',
  float: { aria: '快捷操作', copyRight: '向右复制', distribute: '整行均分', remove: '删除' },
  itemTitle: ({ name, code, qty, conflicts }) => [`${name}(${code}) 每钩 ${qty}`, ...conflicts].join(' | '),
}

const noop = () => undefined
function render(overrides: Partial<Parameters<typeof BoardCanvas>[0]> = {}) {
  return renderToStaticMarkup(
    <BoardCanvas
      ctx={ctx}
      items={items}
      placed={placed}
      specs={specRows}
      conflictsByGuid={conflictsByGuid}
      selectedGuid={null}
      zoom={40}
      showHoles
      showRulers
      showTags
      snapMode="hole"
      readOnly={false}
      language="zh"
      labels={labels}
      conflictText={(conflict) => `冲突:${conflict.code}`}
      externalHint={null}
      describeHint={() => ({ ok: true, line1: '✓ 可放置', line2: '圆珠笔' })}
      onSelect={noop}
      onMoveItem={noop}
      onRejectMove={noop}
      onCopyRight={noop}
      onDistribute={noop}
      onRemove={noop}
      onZoomWheel={noop}
      onTabSelect={() => false}
      emptyText="没有货架"
      {...overrides}
    />,
  )
}

// ---------- 尺寸换算 ----------
const metrics = canvasMetrics(shelf, 2, 40, true)
assertEqual(metrics.scale, 0.4, '40% = 1 mm 对应 0.4 px')
assertEqual(metrics.ox, 34, '带标尺时左留白 34')
assertEqual(metrics.oy, 40, '带标尺时上留白 40')
assertEqual(metrics.width, 34 + 1800 * 0.4 + 30, '宽 = 左留白 + 两节 × 900 × 0.4 + 右留白')
assertEqual(canvasMetrics(shelf, 2, 40, false).oy, 24, '不带标尺时上留白只留节标题')
// 适应窗口：可视区 1000 × 600：宽度允许 (1000−34−30)/1800 = 0.52，高度允许 (600−40−14)/1200 = 0.455 → 取小者 45%
assertEqual(fitZoomFor(shelf, 2, { width: 1000, height: 600 }, true), 45, '适应窗口按宽高中较小者取整')
assertEqual(fitZoomFor(shelf, 2, { width: 100, height: 100 }, true), 25, '太小夹在 25%')
assertEqual(fitZoomFor(shelf, 1, { width: 4000, height: 4000 }, true), 100, '太大夹在 100%')

// ---------- 基本渲染 ----------
const html = render()
assert(html.includes('<svg'), '画布是 SVG')
assertEqual(count(html, 'data-item-guid='), 4, '4 个商品都要画出来（i4 缺参数用占位尺寸，保证仍看得见、点得到）')
for (const guid of ['i1', 'i2', 'i3', 'i4']) assert(html.includes(`data-item-guid="${guid}"`), `${guid} 在画布上`)
assert(html.includes('节 1 · 900 mm') && html.includes('节 2 · 900 mm'), '每一节有标题')
assert(html.includes('底板 · 深 300 mm'), '底板文字')
assert(html.includes('<pattern'), '显示孔位时有孔阵 pattern')
assert(html.includes('role="group"') && html.includes('aria-label="陈列画布"') && html.includes('tabindex="0"'), '画布可聚焦、有读屏标签')
assert(html.includes('width="784"'), 'SVG 宽度与换算一致')
assert(html.includes('圆珠笔 10 支(XD0001202) 每钩 9'), '商品有读屏名称 + 每钩挂数（建议值 ⌊180 ÷ 20⌋ = 9）')
// 价签：$3.50（圆珠笔）/ $2.50（笔记本）
assert(html.includes('$3.50') && html.includes('$2.50'), '价签显示零售价')
// 冲突：i3 越界、i4 缺参数 → 红色描边与红色徽标；正常商品是灰色描边
assert(count(html, '#ff4d4f') >= 4, '冲突商品有红框 / 红色徽标')
assert(html.includes('>!</text>'), '冲突徽标')
assert(html.includes('冲突:OUT_OF_BOARD') || html.includes('冲突:PRODUCT_SPEC_MISSING'), '冲突说明进入读屏标题')

// ---------- 显示开关 ----------
const noHoles = render({ showHoles: false })
assert(!noHoles.includes('<pattern'), '关闭孔位后没有孔阵 pattern')
const noRulers = render({ showRulers: false })
assert(!noRulers.includes('>300</text>') && html.includes('>300</text>'), '标尺刻度只在开启时出现')
const noTags = render({ showTags: false })
assert(!noTags.includes('$3.50') && !noTags.includes('$2.50'), '关闭价签后不画价格')
// 缩放到 25%：孔距 25 mm × 0.25 = 6.25 px 仍画孔阵；再缩小到孔距 < 4 px 才省略（这里货架孔距固定 25，所以 25% 仍有）
assert(render({ zoom: 25 }).includes('<pattern'), '25% 缩放仍画孔阵')

// ---------- 选中覆盖层 + 浮动工具条 ----------
const selected = render({ selectedGuid: 'i1' })
assert(selected.includes('#1677ff'), '选中项有蓝色描边 / 手柄')
assertEqual(count(selected, 'fill="#fff" stroke="#1677ff"'), 4, '选中覆盖层画 4 个角手柄')
assert(selected.includes('role="toolbar"') && selected.includes('向右复制') && selected.includes('整行均分'), '可编辑时显示浮动工具条')
assert(!render({ selectedGuid: 'i1', readOnly: true }).includes('role="toolbar"'), '只读时不显示浮动工具条')
assert(!render({ selectedGuid: 'i4' }).includes('role="toolbar"'), '没有占位几何（缺参数）的条目不显示浮动工具条')
// 选中不改变商品的 DOM 顺序（否则浏览器会丢掉 pointer capture）：选中前后 data-item-guid 的出现顺序一致
const order = (markup: string) => [...markup.matchAll(/data-item-guid="([^"]+)"/g)].map((match) => match[1]).join(',')
assertEqual(order(selected), order(html), '选中不重排商品 DOM')
// 冲突项被放到后面画（红框不被盖住）
assert(order(html).indexOf('i3') > order(html).indexOf('i1'), '有冲突的商品后画')

// ---------- 落点幽灵 ----------
const ok = buildDropHint(ctx, placed, { xMm: 915 + 600, yMm: 301 }, { productCode: 'NOTE', hookModelGuid: 'H' })
assert(ok !== null, '能算出落点')
const ghostHtml = render({ externalHint: ok })
assert(ghostHtml.includes('✓ 可放置') && ghostHtml.includes('stroke-dasharray="4 3"'), '落点提示：气泡文字 + 虚线幽灵框')
const bad = buildDropHint(ctx, placed, { xMm: 15 + 25 * 4 + 5, yMm: 75 + 20 }, { productCode: 'PEN', hookModelGuid: 'H' })
assert(bad !== null && !bad.ok, '压在已有商品上不可放')
assert(render({ externalHint: bad, describeHint: () => ({ ok: false, line1: '✕ 不可放置' }) }).includes('✕ 不可放置'), '不可放置的气泡')

// ---------- 没有货架 ----------
const empty = render({ ctx: null, items: [], placed: [] })
assert(empty.includes('没有货架') && !empty.includes('<svg'), '货架型号缺失时显示说明文字而不是画空画布')

// ---------- 英文名 ----------
assert(render({ language: 'en' }).includes('Ballpoint Pen 10pk(XD0001202)'), '英文界面优先英文名，没有英文名回退中文名')
assert(render({ language: 'en' }).includes('笔记本 A6(XD0001211)'), '没有英文名时回退中文名')

// ---------- 批 3：打印（PDF）模式 ----------
const pm = printMetrics(shelf, 2, 0.5)
assertEqual(pm.ox, 36, '打印版左留白 36（标尺 + 行号栏）')
assertEqual(pm.oy, 44, '打印版上留白 44（标尺 + 节标题）')
assertEqual(pm.width, 36 + 1800 * 0.5 + 10, '打印版宽 = 左留白 + 两节 + 右留白 10')
assertEqual(pm.height, 44 + 1200 * 0.5 + 10, '打印版高')

const printNumbers = new Map([['i1', 1], ['i2', 2], ['i3', 3], ['i4', 4]])
const printNames = (info: ProductSpecListItemDto | undefined, code: string) => ({ primary: info?.productName ?? code, secondary: info?.englishName ?? null })
function renderPrint(overrides: Partial<Parameters<typeof BoardPrintSvg>[0]> = {}) {
  return renderToStaticMarkup(
    <BoardPrintSvg
      ctx={ctx}
      items={items}
      specs={specRows}
      conflictsByGuid={conflictsByGuid}
      numbers={printNumbers}
      bayFrom={1}
      bayTo={2}
      scale={0.5}
      options={{ bubbles: true, prices: true, coords: true }}
      images={new Map()}
      lang="zh"
      nameOf={printNames}
      labels={{ bayTitle: (bay, width) => `节 ${bay} · ${width} mm`, base: '底板 · 深 300 mm' }}
      rowLabel={(row) => row}
      {...overrides}
    />,
  )
}
const printHtml = renderPrint()
assert(printHtml.includes('<svg') && printHtml.includes('font-family='), '打印版是带字体的 SVG')
for (const forbidden of ['data-item-guid', 'role="button"', 'tabindex', 'class="planogram-designer', 'aria-label', 'aria-pressed']) assert(!printHtml.includes(forbidden), `打印版没有交互属性 / 设计器类名：${forbidden}`)
assert(!printHtml.includes('#1677ff" stroke-width') && !printHtml.includes('stroke="#1677ff"'), '没有选中手柄')
assertEqual(count(printHtml, 'r="6.5" fill="#1f1f1f"'), 4, '序号气泡：每个商品一个（含有冲突的）')
for (const n of [1, 2, 3, 4]) assert(printHtml.includes(`>${n}</text>`), `气泡序号 ${n}`)
assert(printHtml.includes('1202 ×9'), '打印版货号标签：「1202 ×9」（不是设计器的「1202·×9」）')
assert(printHtml.includes('$3.50') && printHtml.includes('$2.50'), '价签价格')
assert(count(printHtml, '#ff4d4f') >= 2, '冲突商品在打印版里仍标红')
assert(printHtml.includes('fill="#1677ff"'), '孔位列号 / 行号（蓝色）')
assert(printHtml.includes('>6</text>') && printHtml.includes('>31</text>'), '列号每 5 列一个：1、6、…、31')
assert(!renderPrint({ options: { bubbles: false, prices: true, coords: true } }).includes('r="6.5" fill="#1f1f1f"'), '关闭气泡')
const printNoPrice = renderPrint({ options: { bubbles: true, prices: false, coords: true } })
assert(!printNoPrice.includes('$3.50') && printNoPrice.includes('stroke="#8c8c8c"'), '关闭价格：价签框仍在，价格不印')
assert(!renderPrint({ options: { bubbles: true, prices: true, coords: false } }).includes('fill="#1677ff"'), '关闭孔位列 / 行号')
// 行号起算 = 自底板：显示行号倒着数（标签每 5 行：显示 1、6、11…）
const fromBase = renderPrint({ rowLabel: (row) => 41 - row + 1 })
assert(fromBase.includes('>41</text>') === false || fromBase.includes('>36</text>'), '自底板起算时行号标签按倒数')
// 实拍图：只画有图的商品，data URL 直接嵌入
const printImages = renderPrint({ images: new Map([['PEN', 'data:image/png;base64,AAAA']]) })
assertEqual(count(printImages, '<image'), 2, '圆珠笔出现 2 次（i1、i3）→ 2 张图；其它商品没有图不画')
assert(printImages.includes('href="data:image/png;base64,AAAA"'), '图片以 data URL 嵌入')
// 只画本页的节：第 2 节单独一页
const bay2 = renderPrint({ bayFrom: 2, bayTo: 2 })
assert(bay2.includes('节 2 · 900 mm') && !bay2.includes('节 1 · 900 mm'), '只画第 2 节的标题')
assertEqual(count(bay2, 'r="6.5" fill="#1f1f1f"'), 1, '第 2 节只有 i2 一个气泡')
assert(bay2.includes('>900</text>') && bay2.includes('>1200</text>') && bay2.includes('>1800</text>'), '标尺显示整体坐标：第 2 节页从 900 起、到 1800')
assert(bay2.includes(`width="${36 + 900 * 0.5 + 10}"`), '单节页 SVG 宽度只含 1 节')
// 打印用的条目视图与设计器同一个函数：显示名 / 价格 / 冲突一致
const views = buildItemViews({ ctx, items, specs: specRows, conflictsByGuid, conflictText: (conflict) => conflict.code, nameOf: printNames })
assertEqual(views.length, 4, '4 个条目都有视图')
assertEqual(views[0].name, '圆珠笔 10 支', '主显示名')
assertEqual(views[0].secondaryName, 'Ballpoint Pen 10pk', '双语时的第二行')
assertEqual(views[0].qty, 9, '每钩挂数与设计器一致（建议值 ⌊180 ÷ 20⌋）')
assert(views[2].hasError && views[3].hasError, '越界 / 缺参数条目 hasError')

console.log('boardCanvas.test: ok')
