import { allocateCappedColumns, rowSerialNumber } from './tableWidthLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertJson(actual: unknown, expected: unknown, message: string) {
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) {
    throw new Error(`${message}: expected ${e}, got ${a}`)
  }
}

const one = { min: 150, max: 300, weight: 1 }

// 单列：夹在 [min, max] 之间。
assertJson(allocateCappedColumns(100, [one]), [150], '可分配宽度不够时停在最窄值')
assertJson(allocateCappedColumns(150, [one]), [150], '正好等于最窄值')
assertJson(allocateCappedColumns(204, [one]), [204], '在范围内全给')
assertJson(allocateCappedColumns(300, [one]), [300], '正好等于最宽值')
assertJson(allocateCappedColumns(5000, [one]), [300], '再宽也封顶，富余交给表格里没设宽度的列')
assertJson(allocateCappedColumns(-20, [one]), [150], '负数可分配宽度按 0 处理')
assertJson(allocateCappedColumns(Number.NaN, [one]), [150], 'NaN 不会让列宽变成 NaN')

// 两列：先各给最窄值，剩余按权重分；一列封顶后剩下的继续给另一列。
const product = { min: 148, max: 340, weight: 0.62 }
const supplier = { min: 100, max: 200, weight: 0.38 }
assertJson(allocateCappedColumns(248, [product, supplier]), [148, 100], '只够最窄值')
const mid = allocateCappedColumns(409, [product, supplier])
assert(mid[0] > mid[1] && mid[0] + mid[1] <= 409, '剩余宽度按权重分，商品列更宽，总和不超过可分配宽度')
assert(mid[0] >= 148 && mid[1] >= 100, '都不小于最窄值')
assertJson(allocateCappedColumns(10000, [product, supplier]), [340, 200], '都封顶')
// 供应商先封顶：剩下的全部继续给商品列，直到商品列也封顶。
const supplierCapsFirst = allocateCappedColumns(100 + 148 + 300, [
  { min: 148, max: 400, weight: 1 },
  { min: 100, max: 120, weight: 1 },
])
assertJson(supplierCapsFirst, [400, 120], '一列封顶后剩余继续分给另一列')
assert(allocateCappedColumns(600, [product, supplier]).every((width, index) => width <= [340, 200][index]), '任何情况下都不超过各自的最宽值')
// 权重为 0 的列不参与分配，也不会死循环。
assertJson(allocateCappedColumns(900, [{ min: 100, max: 500, weight: 0 }, { min: 100, max: 500, weight: 0 }]), [100, 100], '权重全为 0 时都停在最窄值')

// 序号：跨页连续编号。
assert(rowSerialNumber(1, 50, 0) === 1, '第 1 页第 1 行是 1')
assert(rowSerialNumber(1, 50, 49) === 50, '第 1 页最后一行是 50')
assert(rowSerialNumber(2, 50, 0) === 51, '第 2 页第 1 行是 51（跨页连续）')
assert(rowSerialNumber(425, 50, 3) === 21204, '2 万多件商品时序号到 5 位')
assert(rowSerialNumber(0, 20, 0) === 1 && rowSerialNumber(Number.NaN, 20, 2) === 3, '页码非法时按第 1 页处理')
assert(rowSerialNumber(3, 0, 4) === 5, '每页条数非法时不产生 NaN')

console.log('DomesticPurchase tableWidthLogic.test.ts: ok')
