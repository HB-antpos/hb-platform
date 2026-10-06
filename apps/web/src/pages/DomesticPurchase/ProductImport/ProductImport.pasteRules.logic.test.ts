import {
  applyImportPaste,
  clearImportColumn,
  findRowIndexById,
  IMPORT_EDITABLE_COLUMNS,
  toEditableColumn,
} from './importPasteLogic'
import { parseProductImportPasteText, createEmptyProduct } from './utils'
import type { ProductImportItem } from './types'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

/* ------------------------------ 行定位：按 id 查，不能 parseInt ------------------------------ */

// 真实行 id 的形状：createEmptyProduct 生成 row_时间戳_随机串
const realRows = [createEmptyProduct(), createEmptyProduct(), createEmptyProduct()]
assertEqual(/^row_\d+_[a-z0-9]+$/.test(realRows[1].id), true, '行 id 形如 row_时间戳_随机串')
// 旧实现：String(parseInt(rowKey)) 对这种 id 得到 "NaN"，永远找不到行，表现为「无法确定行位置」
assertEqual(Number.isNaN(parseInt(realRows[1].id)), true, '复现旧缺陷：对 row_ 开头的 id 做 parseInt 得到 NaN')
assertEqual(findRowIndexById(realRows, realRows[0].id), 0, '按 id 找到第 0 行')
assertEqual(findRowIndexById(realRows, realRows[2].id), 2, '按 id 找到第 2 行')
assertEqual(findRowIndexById(realRows, 'row_missing'), -1, '找不到时返回 -1（页面据此提示无法确定行位置）')
assertEqual(findRowIndexById(realRows, null), -1, 'data-row-key 缺失时返回 -1')
assertEqual(findRowIndexById(realRows, ''), -1, '空 key 返回 -1')

assertEqual(toEditableColumn('productCode'), 'productCode', '货号列可粘贴')
assertEqual(toEditableColumn('status'), null, '状态列不可粘贴')
assertEqual(toEditableColumn('newImage'), null, '图片列不可粘贴')
assertEqual(toEditableColumn(null), null, '空列 key 不可粘贴')
assertDeepEqual(
  [...IMPORT_EDITABLE_COLUMNS],
  ['quantity', 'productCode', 'barcode', 'productName', 'englishName', 'domesticPrice', 'oemPrice', 'midPackQuantity', 'casePackQuantity', 'volume'],
  '可粘贴列顺序必须与表格从左到右的可编辑列一致',
)

/* ------------------------------ 粘贴写入 ------------------------------ */

const base: ProductImportItem[] = [createEmptyProduct(), createEmptyProduct()]

// 从第 1 行的「货号」列起粘贴 3 行 3 列，超出行数自动补空行
const pasted = applyImportPaste(
  base,
  1,
  'productCode',
  parseProductImportPasteText('HB001\t6901234567890\t硅胶杯\nHB002\t\t收纳盒\nHB003\t6901234567892\t衣架\n'),
)
assertEqual(pasted.length, 4, '起点第 1 行 + 3 行数据 = 共 4 行，自动补 2 行')
assertEqual(base.length, 2, '不修改入参数组')
assertEqual(pasted[0], base[0], '起点之前的行对象原样保留')
assertDeepEqual(pasted.slice(1).map((row) => [row.newProduct.productCode, row.newProduct.barcode, row.newProduct.productName]), [
  ['HB001', '6901234567890', '硅胶杯'],
  ['HB002', '', '收纳盒'],
  ['HB003', '6901234567892', '衣架'],
], '多列粘贴按可编辑列顺序写入，空单元格也覆盖目标格')
assertEqual(pasted[1].imageUrl, 'https://hbimgoss.hbupplier.com/productimg/HB001.jpg', '粘贴货号后按货号生成图片地址')
assertEqual(pasted[1].imageLoadStatus, 'loading', '图片状态重置为加载中')

// 数值清洗：币种符号、千分位、空值
const numeric = applyImportPaste(
  [createEmptyProduct()],
  0,
  'domesticPrice',
  [['¥ 1,234.50', '$3.49', '12', '48', '0.0012']],
)
assertDeepEqual(
  [numeric[0].newProduct.domesticPrice, numeric[0].newProduct.oemPrice, numeric[0].newProduct.midPackQuantity, numeric[0].newProduct.casePackQuantity, numeric[0].newProduct.volume],
  [1234.5, 3.49, 12, 48, 0.0012],
  '价格去掉币种与千分位，整数列取整，体积保留小数',
)
const cleared = applyImportPaste(numeric, 0, 'domesticPrice', [['', '']])
assertDeepEqual([cleared[0].newProduct.domesticPrice, cleared[0].newProduct.oemPrice], [undefined, undefined], '数值列的空单元格清成 undefined（不是 null / 0）')
assertEqual(cleared[0].newProduct.midPackQuantity, 12, '粘贴范围之外的列不受影响')

// 单列粘贴超出可编辑列范围时多余单元格被忽略
const overflow = applyImportPaste([createEmptyProduct()], 0, 'casePackQuantity', [['10', '0.5', '多余']])
assertDeepEqual([overflow[0].newProduct.casePackQuantity, overflow[0].newProduct.volume], [10, 0.5], '最后一个可编辑列之后的单元格被忽略')

// 件数、总体积等派生字段随粘贴更新
const quantity = applyImportPaste([createEmptyProduct()], 0, 'quantity', [['5', 'HB9', '', '', '', '', '', '', '', '0.002']])
assertEqual(quantity[0].calculated.totalProducts, 5, '件数粘贴后总件数同步')
assertEqual(quantity[0].calculated.totalVolume, 5 * 0.002, '总体积 = 件数 × 单件体积')

/* ------------------------------ 清空列 ------------------------------ */

const toClear = applyImportPaste(
  [createEmptyProduct(), createEmptyProduct()],
  0,
  'productName',
  [['甲', 'Alpha'], ['乙', 'Beta']],
)
const clearedNames = clearImportColumn(toClear, 'productName')
assertDeepEqual(clearedNames.map((row) => row.newProduct.productName), ['', ''], '文本列清成空串')
assertDeepEqual(clearedNames.map((row) => row.newProduct.englishName), ['Alpha', 'Beta'], '只清选中的列')
assertDeepEqual(clearImportColumn(toClear, 'domesticPrice').map((row) => row.newProduct.domesticPrice), [undefined, undefined], '数值列清成 undefined')

console.log('ProductImport.pasteRules.logic.test: ok')
