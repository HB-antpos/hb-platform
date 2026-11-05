import {
  autoHoleCount,
  autoHoleGrid,
  boardSize,
  holeCenter,
  holeCountMismatch,
  holeGridMargins,
  previewScale,
} from './holeGrid'

// 孔阵自动换算与坐标：口径与后端自动换算一致（附录 A）：⌊(板面长 − 2×首孔边距) ÷ 孔距⌋ + 1。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertClose(actual: number, expected: number, message: string) {
  if (Math.abs(actual - expected) > 1e-9) {
    throw new Error(`${message}. Expected: ${expected}, received: ${actual}`)
  }
}

const shelf = {
  bayWidthMm: 900,
  totalHeightMm: 1200,
  baseTopHeightMm: 150,
  uprightWidthMm: 30,
  holePitchXMm: 25,
  holePitchYMm: 25,
  firstHoleLeftMm: 25,
  firstHoleTopMm: 25,
  holeCols: 34,
  holeRows: 41,
}

// 板面 = 节宽 − 立柱宽，总高 − 底板面离地高
const board = boardSize(shelf)
assertEqual(board.widthMm, 870, '板面宽')
assertEqual(board.heightMm, 1050, '板面高')

// 设计图样例：纵向 ⌊(1050 − 50) ÷ 25⌋ + 1 = 41 行；横向 ⌊(870 − 50) ÷ 25⌋ + 1 = 33 列。
// 设计图抽屉里写的是 34 列：那是手填值（最后一个孔距右边缘只剩 20 mm），公式本身算出来是 33，所以界面会提示「手填 34 ≠ 换算 33」。
assertEqual(autoHoleCount(1050, 25, 25), 41, '行数换算')
assertEqual(autoHoleCount(870, 25, 25), 33, '列数换算')
const auto = autoHoleGrid(shelf)
assertEqual(auto.cols, 33, 'autoHoleGrid 列')
assertEqual(auto.rows, 41, 'autoHoleGrid 行')

// 英寸孔距 25.4 mm、首孔边距 25.4 mm
assertEqual(autoHoleCount(870, 25.4, 25.4), 33, '25.4 孔距横向：⌊819.2 ÷ 25.4⌋ + 1 = 33')
assertEqual(autoHoleCount(1050, 25.4, 25.4), 40, '25.4 孔距纵向：⌊999.2 ÷ 25.4⌋ + 1 = 40')
// 小数孔距恰好整除时不被浮点误差少算一个孔：板面长 = 2 × 首孔 + 10 × 孔距
assertEqual(autoHoleCount(2 * 12.7 + 10 * 25.4, 12.7, 25.4), 11, '恰好整除 10 个孔距 → 11 个孔')

// 不合法输入返回 0，不抛异常
assertEqual(autoHoleCount(870, 25, 0), 0, '孔距 0')
assertEqual(autoHoleCount(870, 25, -5), 0, '孔距为负')
assertEqual(autoHoleCount(40, 25, 25), 0, '板面放不下首孔边距：0 个孔')
assertEqual(autoHoleCount(50, 25, 25), 1, '板面长刚好等于两个首孔边距：1 个孔')
assertEqual(autoHoleCount(870, -1, 25), 0, '首孔边距为负')
assertEqual(autoHoleCount(Number.NaN, 25, 25), 0, 'NaN')

// 孔心坐标：col / row 从 1 起，板面左上为原点
const first = holeCenter(shelf, 1, 1)
assertEqual(first.x, 25, '第 1 列 x = 首孔距左')
assertEqual(first.y, 25, '第 1 行 y = 首孔距顶')
const last = holeCenter(shelf, 34, 41)
assertEqual(last.x, 850, '第 34 列 x = 25 + 33 × 25')
assertEqual(last.y, 1025, '第 41 行 y = 25 + 40 × 25')
const inch = holeCenter({ ...shelf, holePitchXMm: 25.4, holePitchYMm: 25.4, firstHoleLeftMm: 12.7, firstHoleTopMm: 12.7 }, 3, 2)
assertClose(inch.x, 12.7 + 2 * 25.4, '小数孔距 x')
assertClose(inch.y, 12.7 + 25.4, '小数孔距 y')

// 孔阵余量：设计图手填 34 × 41 → 右侧只剩 20 mm、下侧 25 mm；超出板面为负
const margins = holeGridMargins(shelf)
assertEqual(margins.rightMm, 20, '右侧余量')
assertEqual(margins.bottomMm, 25, '下侧余量')
assertEqual(holeGridMargins({ ...shelf, holeCols: 40 }).rightMm < 0, true, '孔数填多了会超出板面')

// 手填孔数与换算值的差异提示
assertEqual(holeCountMismatch(34, 33)?.manual, 34, '手填 34 ≠ 换算 33 → 提示')
assertEqual(holeCountMismatch(34, 33)?.auto, 33, '提示里带换算值')
assertEqual(holeCountMismatch(33, 33), null, '一致不提示')
assertEqual(holeCountMismatch(null, 33), null, '没手填不提示（后端按换算值补）')
assertEqual(holeCountMismatch(0, 33), null, '手填 0 视为没填')

// 预览缩放：按框等比缩小，不放大
assertClose(previewScale(shelf, 130, 156), 0.13, '900 × 1200 在 130 × 156 框内 = 0.13')
assertEqual(previewScale({ bayWidthMm: 100, totalHeightMm: 100 }, 500, 500), 1, '小货架不放大超过 1:1')
assertEqual(previewScale({ bayWidthMm: 0, totalHeightMm: 100 }, 500, 500), 0, '尺寸缺失返回 0')

console.log('holeGrid.test: ok')
