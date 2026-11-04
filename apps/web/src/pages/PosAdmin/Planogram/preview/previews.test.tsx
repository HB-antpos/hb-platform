import { renderToStaticMarkup } from 'react-dom/server'
import HookSidePreview from './HookSidePreview'
import ProductDimPreview from './ProductDimPreview'
import ShelfFrontPreview from './ShelfFrontPreview'

// 三个预览 SVG 的渲染契约：用设计图样例数据静态渲染，核对关键几何与文字；非法输入不崩、画空画布。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertIncludes(haystack: string, needle: string, message: string) {
  if (!haystack.includes(needle)) throw new Error(`${message}. Missing: ${needle}`)
}

function count(haystack: string, needle: string) {
  return haystack.split(needle).length - 1
}

// ---------- 货架正视 ----------
const shelf = {
  bayWidthMm: 900, totalHeightMm: 1200, baseTopHeightMm: 150, uprightWidthMm: 30,
  holePitchXMm: 25, holePitchYMm: 25, firstHoleLeftMm: 25, firstHoleTopMm: 25, holeCols: 34, holeRows: 41,
}
const shelfLabels = {
  bayWidth: '节宽 900',
  totalHeight: { name: '总高', value: '1200' },
  board: { name: '板面', value: '1050' },
  base: '底板 深 300 · 面高 150',
}
const shelfSvg = renderToStaticMarkup(<ShelfFrontPreview shelf={shelf} labels={shelfLabels} ariaLabel="shelf" />)
assertIncludes(shelfSvg, '<pattern', '有孔阵时生成孔 pattern')
assertIncludes(shelfSvg, 'clip-path', '孔阵被裁剪在板面内')
assertIncludes(shelfSvg, '节宽 900', '节宽尺寸线文字')
assertIncludes(shelfSvg, '底板 深 300 · 面高 150', '底板文字')
assertIncludes(shelfSvg, 'role="img"', '预览是图片角色')
assertIncludes(shelfSvg, 'aria-label="shelf"', '有读屏标签')
// 设计图缩放 0.13：节宽 900 → 117 px
assertIncludes(shelfSvg, 'width="117"', '一节货架按 0.13 缩放为 117 px 宽')
// 孔距 25 × 0.13 = 3.25 px，pattern 瓷砖宽 3.25
assertIncludes(shelfSvg, 'width="3.25"', '孔距按同一缩放')

const denseSvg = renderToStaticMarkup(<ShelfFrontPreview shelf={{ ...shelf, holePitchXMm: 5, holePitchYMm: 5 }} labels={shelfLabels} ariaLabel="x" />)
assertEqual(denseSvg.includes('<pattern'), false, '孔太密（缩放后孔距 < 1.6 px）不画孔阵')

const twoPatterns = renderToStaticMarkup(
  <>
    <ShelfFrontPreview shelf={shelf} labels={shelfLabels} ariaLabel="a" />
    <ShelfFrontPreview shelf={shelf} labels={shelfLabels} ariaLabel="b" />
  </>,
)
const ids = [...twoPatterns.matchAll(/<pattern id="([^"]+)"/g)].map((match) => match[1])
assertEqual(ids.length, 2, '两个预览各有一个 pattern')
assertEqual(new Set(ids).size, 2, '同页两个预览的 pattern id 不能重复（用 useId）')

for (const bad of [
  { ...shelf, bayWidthMm: Number.NaN },
  { ...shelf, totalHeightMm: 0 },
  { ...shelf, uprightWidthMm: 900 },
  { ...shelf, baseTopHeightMm: 1200 },
]) {
  const empty = renderToStaticMarkup(<ShelfFrontPreview shelf={bad} labels={shelfLabels} ariaLabel="x" />)
  assertEqual(empty.includes('<rect'), false, '非法尺寸画空画布，不抛异常')
}

// ---------- 挂钩侧视 ----------
const hook = {
  hasPriceTagArm: true, armLengthMm: 200, dropMm: 50, wireDiameterMm: 5, tipReserveMm: 20,
  tagWidthMm: 52, tagHeightMm: 26, tagBottomAboveArmMm: 10,
}
const hookLabels = {
  tag: '价签 52×26', tagBottom: '底边高 10', armLength: '臂长 L = 200', drop: 'H 50', reserve: '预留 20',
  formula: '每钩挂数 = ⌊(L − 预留) ÷ 包装厚⌋ = ⌊180 ÷ 20⌋ = 9',
}
const hookSvg = renderToStaticMarkup(<HookSidePreview hook={hook} labels={hookLabels} ariaLabel="hook" />)
assertEqual(count(hookSvg, 'fill="#fff1b8"'), 9, '样品包装厚 20、臂长 200、预留 20 → 画 9 张（⌊180 ÷ 20⌋）')
assertIncludes(hookSvg, '价签 52×26', '价签文字')
assertIncludes(hookSvg, '底边高 10', '底边高文字')
assertIncludes(hookSvg, '臂长 L = 200', '臂长尺寸线文字')
assertIncludes(hookSvg, '每钩挂数', '公式文字')
assertEqual(count(hookSvg, 'rx="1.5"'), 1, '有价签臂：画一个价签架')

const noTagSvg = renderToStaticMarkup(<HookSidePreview hook={{ ...hook, hasPriceTagArm: false }} labels={hookLabels} ariaLabel="hook" />)
assertEqual(noTagSvg.includes('rx="1.5"'), false, '无价签臂：不画价签架')
assertEqual(noTagSvg.includes('底边高 10'), false, '无价签臂：不标底边高')
assertEqual(count(noTagSvg, 'fill="#fff1b8"'), 9, '无价签臂时挂货样品照常')

const longSvg = renderToStaticMarkup(<HookSidePreview hook={{ ...hook, armLengthMm: 3000 }} labels={hookLabels} ariaLabel="x" />)
assertEqual(count(longSvg, 'fill="#fff1b8"') <= 60, true, '臂特别长时样品最多画 60 张')
const shortSvg = renderToStaticMarkup(<HookSidePreview hook={{ ...hook, armLengthMm: 20 }} labels={hookLabels} ariaLabel="x" />)
assertEqual(shortSvg.includes('fill="#fff1b8"'), false, '臂长不超过预留：一张也挂不下')
for (const bad of [{ ...hook, armLengthMm: 0 }, { ...hook, dropMm: Number.NaN }, { ...hook, tipReserveMm: -1 }]) {
  assertEqual(renderToStaticMarkup(<HookSidePreview hook={bad} labels={hookLabels} ariaLabel="x" />).includes('<path'), false, '非法输入画空画布')
}

// ---------- 包装尺寸 ----------
const dimLabels = {
  front: '正视', side: '侧视', width: 'W 110', height: { name: 'H', value: '155' }, depth: 'T 12', hangHole: '吊孔 10',
  footprint: '挂钩横向占位 = max(W, 价签 52)',
}
const dimSvg = renderToStaticMarkup(
  <ProductDimPreview widthMm={110} heightMm={155} depthMm={12} hangHoleOffsetMm={10} hangHoleType={2} labels={dimLabels} ariaLabel="dim" />,
)
assertIncludes(dimSvg, 'W 110', '宽尺寸线')
assertIncludes(dimSvg, 'T 12', '厚尺寸线')
assertIncludes(dimSvg, '侧视', '有厚度时画侧视')
assertIncludes(dimSvg, '吊孔 10', '欧式挂孔 + 偏移时标吊孔偏移')
assertIncludes(dimSvg, 'width="12" height="4"', '欧式挂孔画成 12 × 4 的长圆孔')

const roundSvg = renderToStaticMarkup(
  <ProductDimPreview widthMm={110} heightMm={155} depthMm={12} hangHoleOffsetMm={10} hangHoleType={1} labels={dimLabels} ariaLabel="dim" />,
)
assertIncludes(roundSvg, '<circle', '圆孔画圆')
const noHoleSvg = renderToStaticMarkup(
  <ProductDimPreview widthMm={110} heightMm={155} depthMm={12} hangHoleOffsetMm={10} hangHoleType={0} labels={dimLabels} ariaLabel="dim" />,
)
assertEqual(noHoleSvg.includes('吊孔 10'), false, '无吊孔不标偏移')
assertEqual(noHoleSvg.includes('<circle'), false, '无吊孔不画孔')
const noDepthSvg = renderToStaticMarkup(
  <ProductDimPreview widthMm={110} heightMm={155} depthMm={null} hangHoleOffsetMm={10} hangHoleType={2} labels={dimLabels} ariaLabel="dim" />,
)
assertEqual(noDepthSvg.includes('侧视'), false, '没填厚不画侧视')
const blankSvg = renderToStaticMarkup(
  <ProductDimPreview widthMm={null} heightMm={155} depthMm={12} hangHoleOffsetMm={10} hangHoleType={2} labels={dimLabels} ariaLabel="dim" />,
)
assertEqual(blankSvg.includes('<rect'), false, '宽缺失画空画布')

console.log('previews.test: ok')
