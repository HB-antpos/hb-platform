import {
  buildConflictRows,
  buildDuplicateRowIndex,
  collectImportErrors,
  computeImportSteps,
  computePiecesAfterSend,
  countImportRows,
  formatImportOldValue,
  getChangedImportFields,
  getDuplicatePartnerRowNumbers,
  getImportCellDiff,
  getImportRowClassName,
  getImportRowErrors,
  IMPORT_FILLER_MIN_WIDTH,
  isBlankImportRow,
  matchesImportFilter,
  mergeEnglishNamesById,
  mergeTranslationsById,
  resolveImportColumnWidths,
  resolveImportRowKind,
  snapshotTranslationTargets,
  splitProductsByResolution,
} from './importGridLogic'
import type { ProductImportItem } from './types'
import { createEmptyProduct } from './utils'

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

function makeRow(id: string, newProduct: Partial<ProductImportItem['newProduct']> = {}, overrides: Partial<ProductImportItem> = {}): ProductImportItem {
  return {
    ...createEmptyProduct(),
    id,
    ...overrides,
    newProduct: { quantity: 1, productCode: '', productName: '', ...newProduct },
  }
}

/* ------------------------------ 空行与错误口径 ------------------------------ */

assertEqual(isBlankImportRow(createEmptyProduct()), true, '「+10 行」产生的占位行是空行')
assertEqual(isBlankImportRow(makeRow('a', { quantity: 5 })), false, '只改了件数的行不是空行')
assertEqual(isBlankImportRow(makeRow('a', { productName: '塑料盒' })), false, '填了名称的行不是空行')
assertEqual(isBlankImportRow(makeRow('a', { domesticPrice: 0 })), false, '价格填了 0 也算有内容')
assertEqual(isBlankImportRow(makeRow('a', { productCode: '   ' })), true, '货号只有空白仍是空行')

// 规格第一节第 2 条：商品导入里只有「货号为空」是错误，零售价与名称可以留空
assertDeepEqual(getImportRowErrors(makeRow('a', { productCode: '', productName: '宠物食盆', domesticPrice: 4.8 })), { productCode: '货号不能为空' }, '有内容但货号为空是唯一的错误')
assertDeepEqual(getImportRowErrors(makeRow('a', { productCode: 'HB001', productName: '', oemPrice: undefined })), {}, '零售价与名称为空都不是错误')
assertDeepEqual(getImportRowErrors(createEmptyProduct()), {}, '空行不报错')
assertDeepEqual(getImportRowErrors(makeRow('a', { productCode: '  ', productName: '宠物食盆' })), { productCode: '货号不能为空' }, '货号只有空白同样是错误（按 trim 判断）')

/* ------------------------------ 行归类 / 统计 / 过滤 ------------------------------ */

const rows: ProductImportItem[] = [
  makeRow('n1', { productCode: 'N1', productName: '新1' }, { status: 'new', sentToContainer: true }),
  makeRow('n2', { productCode: 'N2', productName: '新2' }, { status: 'new' }),
  makeRow('u1', { productCode: 'U1', productName: '更1' }, { status: 'updated' }),
  makeRow('c1', { productCode: 'C1', productName: '无1' }, { status: 'unchanged' }),
  makeRow('d1', { productCode: 'D1', productName: '重1' }, { status: 'duplicate', isDuplicate: true }),
  makeRow('d2', { productCode: 'D1', productName: '重1' }, { status: 'duplicate', isDuplicate: true }),
  makeRow('b1', { productCode: 'B1', productName: '库重' }, { status: 'dbDuplicate' }),
  makeRow('e1', { productCode: '', productName: '宠物食盆' }, { status: 'error', sentToContainer: true }),
  makeRow('blank1'),
  makeRow('blank2'),
  // 检测之后才新增的行：默认 status 是 unchanged，但并没有检测结果
  makeRow('late', { productCode: 'LATE', productName: '后加' }, { status: 'unchanged' }),
  // 存量的 error 状态，但用户已经补上了货号
  makeRow('fixed', { productCode: 'FIXED', productName: '已补货号' }, { status: 'error' }),
]
const detectedIds = new Set(rows.filter((row) => row.id !== 'late').map((row) => row.id))

assertDeepEqual(
  rows.map((row) => resolveImportRowKind(row, detectedIds)),
  ['new', 'new', 'updated', 'unchanged', 'duplicate', 'duplicate', 'dbDuplicate', 'error', 'blank', 'blank', 'pending', 'pending'],
  '行归类：空行、检测后新增的行、已补货号的存量错误行都不能沿用旧状态',
)
assertEqual(resolveImportRowKind(rows[7], null), 'pending', '还没检测过时货号为空的行不报错')
assertEqual(resolveImportRowKind(rows[4], null), 'duplicate', '重复是检测前的本地判定，不依赖检测结果')

assertDeepEqual(
  countImportRows(rows, detectedIds),
  { all: 12, new: 2, updated: 1, unchanged: 1, duplicate: 2, dbDuplicate: 1, error: 1, sent: 2 },
  '统计条：空行与待检测行只计入「全部」；库内重复有独立计数；已发送与状态正交',
)

assertDeepEqual(rows.filter((row) => matchesImportFilter(row, 'new', detectedIds)).map((row) => row.id), ['n1', 'n2'], '过滤「新」')
assertDeepEqual(rows.filter((row) => matchesImportFilter(row, 'unchanged', detectedIds)).map((row) => row.id), ['c1'], '过滤「无变化」不包含空行和待检测行')
assertDeepEqual(rows.filter((row) => matchesImportFilter(row, 'dbDuplicate', detectedIds)).map((row) => row.id), ['b1'], '过滤「库内重复」')
assertDeepEqual(rows.filter((row) => matchesImportFilter(row, 'sent', detectedIds)).map((row) => row.id), ['n1', 'e1'], '过滤「已发送」按 sentToContainer')
assertEqual(rows.filter((row) => matchesImportFilter(row, 'all', detectedIds)).length, 12, '过滤「全部」不丢行')

assertEqual(getImportRowClassName('dbDuplicate', false), 'pi-row pi-row-dbDuplicate', '库内重复有独立行色 class')
assertEqual(getImportRowClassName('pending', true), 'pi-row pi-row-sent', '待检测的已发送行只带 sent 修饰')
assertEqual(getImportRowClassName('blank', false), 'pi-row', '空行没有状态色')

/* ------------------------------ 错误汇总 / 重复行号 ------------------------------ */

assertDeepEqual(
  collectImportErrors(rows, detectedIds),
  [{ rowId: 'e1', rowNumber: 8, field: 'productCode' }],
  '错误汇总的行号按整张表（含空行）从 1 起计，且只收货号为空',
)

const duplicateIndex = buildDuplicateRowIndex(rows)
assertDeepEqual(getDuplicatePartnerRowNumbers(duplicateIndex, rows[4], 5), [6], '第 5 行的重复伙伴是第 6 行')
assertDeepEqual(getDuplicatePartnerRowNumbers(duplicateIndex, rows[5], 6), [5], '第 6 行的重复伙伴是第 5 行')
assertDeepEqual(getDuplicatePartnerRowNumbers(duplicateIndex, rows[0], 1), [], '没有重复的行没有伙伴')

/* ------------------------------ 旧值并入单元格 ------------------------------ */

const updatedRow = makeRow('u', {
  productCode: 'HB017-8011', productName: '多功能收纳盒 大号', englishName: 'Storage Box Large', barcode: '6901234567939',
  domesticPrice: 6.8, oemPrice: 2.99, midPackQuantity: 12, casePackQuantity: 48, volume: 0.0014,
}, {
  status: 'updated',
  // diffFields 存的是后端字段名（englishProductName / middlePackQuantity / packingQuantity / unitVolume）
  diffFields: ['domesticPrice', 'englishProductName', 'middlePackQuantity', 'packingQuantity', 'unitVolume', 'productName'],
  matchedProduct: {
    productCode: 'LOC1', productName: '', englishProductName: 'Old English', domesticPrice: 6.5, oemPrice: 2.99,
    middlePackQuantity: 6, packingQuantity: 24, unitVolume: 0.0012, barcode: '6901234567939',
  },
})

assertDeepEqual(getImportCellDiff(updatedRow, 'domesticPrice'), { oldDisplay: '¥ 6.50' }, '国内价旧值带人民币符号、两位小数')
assertDeepEqual(getImportCellDiff(updatedRow, 'englishName'), { oldDisplay: 'Old English' }, '英文名的变化要能命中后端的 englishProductName（旧代码这一列的高亮从未生效）')
assertDeepEqual(getImportCellDiff(updatedRow, 'midPackQuantity'), { oldDisplay: '6' }, '中包的变化要能命中 middlePackQuantity')
assertDeepEqual(getImportCellDiff(updatedRow, 'casePackQuantity'), { oldDisplay: '24' }, '装箱的变化要能命中 packingQuantity')
assertDeepEqual(getImportCellDiff(updatedRow, 'volume'), { oldDisplay: '0.001' }, '体积的变化要能命中 unitVolume，三位小数')
assertDeepEqual(getImportCellDiff(updatedRow, 'productName'), { oldDisplay: null }, '库里原值为空时旧值为 null（页面显示「原：空」）')
assertEqual(getImportCellDiff(updatedRow, 'oemPrice'), null, '没出现在 diffFields 里的字段没有变化')
assertEqual(getImportCellDiff(makeRow('x', { productCode: 'X' }), 'domesticPrice'), null, '没有匹配商品时没有旧值')
assertDeepEqual(
  getChangedImportFields(updatedRow),
  ['productName', 'englishName', 'domesticPrice', 'midPackQuantity', 'casePackQuantity', 'volume'],
  '状态列里的变化字段按列顺序排列',
)

assertEqual(formatImportOldValue('oemPrice', 3.49), '$ 3.49', '零售价旧值带 $')
assertEqual(formatImportOldValue('domesticPrice', 0), null, '价格 0 视为库里没填')
assertEqual(formatImportOldValue('barcode', '  '), null, '空白文本视为空')
assertEqual(formatImportOldValue('productName', ' 收纳盒 大号 '), '收纳盒 大号', '文本旧值去掉首尾空白')

/* ------------------------------ 5 步分段 ------------------------------ */

const baseSteps = { hasSupplier: false, filledRowCount: 0, detected: false, needsDetection: false, duplicateGroupCount: 0, pendingCommitCount: 0, committedCount: 0, sentCount: 0 }
const stateOf = (steps: ReturnType<typeof computeImportSteps>) => steps.map((step) => `${step.key}:${step.state}`).join(',')

assertEqual(stateOf(computeImportSteps(baseSteps)), 'supplier:current,entry:todo,detect:todo,commit:todo,container:todo', '初始：供应商是当前步')
assertEqual(stateOf(computeImportSteps({ ...baseSteps, hasSupplier: true })), 'supplier:done,entry:current,detect:todo,commit:todo,container:todo', '选了供应商：录入是当前步')
assertEqual(stateOf(computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 9 })), 'supplier:done,entry:done,detect:current,commit:todo,container:todo', '录入了行：检测匹配是当前步')

const detectedSteps = computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 9, detected: true, pendingCommitCount: 5 })
assertEqual(stateOf(detectedSteps), 'supplier:done,entry:done,detect:done,commit:current,container:todo', '检测完成：入库是当前步')
assertDeepEqual(detectedSteps[2].summary, { kind: 'detectedAt' }, '检测完成的步骤带摘要')
assertDeepEqual(detectedSteps[3].summary, { kind: 'pendingCommit', count: 5 }, '入库步显示待入库条数')

const redetectSteps = computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 9, detected: true, needsDetection: true, pendingCommitCount: 5, sentCount: 2 })
assertEqual(stateOf(redetectSteps), 'supplier:done,entry:done,detect:current,commit:todo,container:todo', '检测之后有改动：步骤回退到「检测匹配」，后面各步变回待办')
assertDeepEqual([redetectSteps[2].summary, redetectSteps[2].warn], [{ kind: 'needRedetect' }, true], '回退后用摘要 + 警示色提示需要重新检测，不再靠隐藏按钮')
assertDeepEqual(redetectSteps[4].summary, { kind: 'sent', count: 2 }, '已发送的事实在回退后仍然保留')

const duplicateSteps = computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 4, detected: false, duplicateGroupCount: 2 })
assertDeepEqual([duplicateSteps[2].state, duplicateSteps[2].summary, duplicateSteps[2].warn], ['current', { kind: 'duplicates', count: 2 }, true], '被重复货号拦下时第 3 步提示重复组数')

const committedSteps = computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 9, detected: true, committedCount: 5 })
assertEqual(stateOf(committedSteps), 'supplier:done,entry:done,detect:done,commit:done,container:current', '入库完成：发货柜（可选）成为当前步')
assertDeepEqual([committedSteps[3].summary, committedSteps[4].summary], [{ kind: 'committed', count: 5 }, { kind: 'optional' }], '入库完成摘要与发货柜「可选」')
const sentSteps = computeImportSteps({ ...baseSteps, hasSupplier: true, filledRowCount: 9, detected: true, committedCount: 5, sentCount: 3 })
assertEqual(stateOf(sentSteps), 'supplier:done,entry:done,detect:done,commit:done,container:done', '已发送到货柜：全部完成')

/* ------------------------------ 翻译结果按 id 合并 ------------------------------ */

const translationRows = [
  makeRow('A', { productCode: 'A', productName: '塑形泥' }),
  makeRow('B', { productCode: 'B', productName: '收纳盒', englishName: '' }),
  makeRow('C', { productCode: 'C', productName: 'Canvas Frame', englishName: 'Canvas Frame' }),
]
const translations = { 塑形泥: 'Modeling Clay', 收纳盒: 'Storage Box' }

assertDeepEqual(snapshotTranslationTargets(translationRows, []).map((target) => target.id), ['A', 'B'], '无选中行：全部含中文名称的行是目标')
assertDeepEqual(snapshotTranslationTargets(translationRows, ['B']).map((target) => target.id), ['B'], '有选中行：只翻译选中的')

const targets = snapshotTranslationTargets(translationRows, [])

// 翻译期间：用户改了 A 的名称、删掉了 B、又在最前面新增了一行
const afterEdit = [
  makeRow('NEW', { productCode: 'NEW', productName: '期间新增' }),
  makeRow('A', { productCode: 'A', productName: '塑形泥（改）' }),
  translationRows[2],
]
const merged = mergeTranslationsById(afterEdit, targets, translations)
assertEqual(merged.appliedCount, 0, '被改过名称的行不能被旧翻译结果覆盖')
assertEqual(merged.editedCount, 1, '统计被期间编辑挡下的行数（已删除的行直接忽略）')
assertDeepEqual(merged.products.map((row) => row.newProduct.englishName), [undefined, undefined, 'Canvas Frame'], '期间新增 / 被改的行英文名保持原样')
assertEqual(merged.products[0], afterEdit[0], '期间新增的行对象原样保留（没有被旧数组整体覆盖）')

// 翻译期间用户给 B 手填了英文名：不能覆盖；A 未被改动，正常应用
const afterEnglish = [
  translationRows[0],
  makeRow('B', { productCode: 'B', productName: '收纳盒', englishName: 'My Box' }),
  translationRows[2],
]
const mergedEnglish = mergeTranslationsById(afterEnglish, targets, translations)
assertEqual(mergedEnglish.appliedCount, 1, '只有未被改动的 A 被写入')
assertEqual(mergedEnglish.editedCount, 1, 'B 因英文名被手填而跳过')
assertDeepEqual(mergedEnglish.products.map((row) => row.newProduct.englishName), ['Modeling Clay', 'My Box', 'Canvas Frame'], '手填的英文名保留')

/* ------------------------------ 看图英文名按 id 合并 ------------------------------ */

// 看图结果按行 id 给出：同名不同图的两行可以拿到不同英文名
const sameNameRows = [
  makeRow('X', { productCode: 'X', productName: '收纳盒' }),
  makeRow('Y', { productCode: 'Y', productName: '收纳盒' }),
]
const sameNameTargets = snapshotTranslationTargets(sameNameRows, [])
const byId = mergeEnglishNamesById(sameNameRows, sameNameTargets, { X: 'Blue Plastic Storage Box', Y: 'Clear Storage Box 3 Pack' })
assertEqual(byId.appliedCount, 2, '按 id 合并时两行都写入')
assertDeepEqual(byId.products.map((row) => row.newProduct.englishName), ['Blue Plastic Storage Box', 'Clear Storage Box 3 Pack'], '同名不同图的行各自拿到自己的英文名')

// 期间改名 / 手填英文名的行不覆盖；空结果与仍含中文的结果计入 skipped
const imageMerged = mergeEnglishNamesById(afterEnglish, targets, { A: 'Modeling Clay Set', B: 'Storage Box' })
assertEqual(imageMerged.appliedCount, 1, '只有未被改动的 A 被写入')
assertEqual(imageMerged.editedCount, 1, 'B 因英文名被手填而跳过')
assertDeepEqual(imageMerged.products.map((row) => row.newProduct.englishName), ['Modeling Clay Set', 'My Box', 'Canvas Frame'], '手填的英文名保留')

const imageSkipped = mergeEnglishNamesById(translationRows, targets, { A: '塑形泥', B: undefined })
assertEqual(imageSkipped.appliedCount, 0, '无效结果不写入')
assertEqual(imageSkipped.skippedCount, 2, '含中文与空结果都计入跳过')
assertEqual(imageSkipped.products, translationRows, '没有写入时返回原数组，避免无谓重渲染')

const imageAfterDelete = mergeEnglishNamesById(afterEdit, targets, { A: 'Modeling Clay', B: 'Storage Box' })
assertEqual(imageAfterDelete.editedCount, 1, '期间被删除的 B 直接忽略，只统计被改名的 A')
assertEqual(imageAfterDelete.products[0], afterEdit[0], '期间新增的行对象原样保留')

// 期间无编辑：与原函数行为一致
const mergedPlain = mergeTranslationsById(translationRows, targets, translations)
assertDeepEqual([mergedPlain.appliedCount, mergedPlain.editedCount, mergedPlain.skippedCount], [2, 0, 0], '无期间编辑时全部应用')

/* ------------------------------ 货柜冲突 ------------------------------ */

const conflictProducts = [
  makeRow('p1', { productCode: 'HB-1', productName: '硅胶折叠碗', quantity: 4 }, { matchedProduct: { productCode: 'LOC-1' } }),
  makeRow('p2', { productCode: 'HB-2', productName: '收纳盒', quantity: 6 }, { matchedProduct: { productCode: 'LOC-2' } }),
  makeRow('p3', { productCode: 'HB-3', productName: '衣架', quantity: 3 }, { matchedProduct: { productCode: 'LOC-3' } }),
]
const conflictRows = buildConflictRows(conflictProducts, [{ productCode: 'LOC-1', existingPieces: 10 }, { productCode: 'LOC-2', existingPieces: 6 }])
assertDeepEqual(
  conflictRows,
  [
    { productCode: 'LOC-1', hbProductNo: 'HB-1', productName: '硅胶折叠碗', existingPieces: 10, incomingPieces: 4 },
    { productCode: 'LOC-2', hbProductNo: 'HB-2', productName: '收纳盒', existingPieces: 6, incomingPieces: 6 },
  ],
  '冲突行按本地商品编码对回本次发送的行，带出货号、名称与本次件数',
)
assertEqual(computePiecesAfterSend(conflictRows[0], 'increase'), 14, '增加：已有 10 + 本次 4')
assertEqual(computePiecesAfterSend(conflictRows[1], 'override'), 6, '覆盖：以本次件数为准')
assertEqual(computePiecesAfterSend({ existingPieces: undefined, incomingPieces: 5 }, 'increase'), 5, '已有件数未知时按 0 计')

const perItemSplit = splitProductsByResolution(conflictProducts, { perItem: { 'LOC-2': 'override' } })
assertDeepEqual(
  [perItemSplit.override.map((row) => row.id), perItemSplit.increase.map((row) => row.id)],
  [['p2'], ['p1', 'p3']],
  '逐项选择以本地商品编码为键（旧代码用货号查表，「覆盖」从未生效）；没冲突的行走「增加」',
)
assertDeepEqual(splitProductsByResolution(conflictProducts, { global: 'override' }).override.map((row) => row.id), ['p1', 'p2', 'p3'], '全部覆盖')
assertDeepEqual(splitProductsByResolution(conflictProducts, { global: 'increase' }).increase.map((row) => row.id), ['p1', 'p2', 'p3'], '全部增加')

/* ------------------------------ 列宽分配 ------------------------------ */

const FIXED_WIDTH = 444 // 勾选 + 件数 + 图 + 货号 + 条码 + 国内价，与 index.tsx 里这些列的 width 一致
const sumWidths = (result: ReturnType<typeof resolveImportColumnWidths>) => Object.values(result.widths).reduce((sum, width) => sum + width, 0)

// 窄屏（容器 ≤ 最小宽度）：保持原来的列宽，基础合计正好等于最小宽度 968，不出现横向滚动条
const narrow = resolveImportColumnWidths(960, 968, true)
assertEqual(narrow.baseTotal, 968, '检测后基础列宽合计应等于表格最小宽度')
assertDeepEqual(
  narrow.widths,
  { oemPrice: 64, midPackQuantity: 52, casePackQuantity: 52, volume: 68, status: 126, productName: 70, englishName: 92 },
  '窄屏列宽保持原值',
)
assertEqual(narrow.leftover, 0, '窄屏没有剩余空间')
assertDeepEqual(resolveImportColumnWidths(0, 968, true).widths, narrow.widths, '没量到容器宽度（0）时按窄屏处理')

// 中屏：多出的空间先补足后面的窄列（装箱 / 体积 / 状态），再给名称列
const middle = resolveImportColumnWidths(1100, 968, true)
assertDeepEqual(
  [middle.widths.casePackQuantity, middle.widths.volume, middle.widths.status],
  [68, 84, 162],
  '后面的窄列应先涨到可完整显示的宽度',
)
assertEqual(middle.widths.productName + middle.widths.englishName > 162, true, '后列补足后仍有空间时才放宽名称列')
assertEqual(FIXED_WIDTH + sumWidths(middle) + middle.leftover, 1100, '分配后列宽 + 剩余 = 容器宽度')

// 宽屏：名称列有上限，不再独吞剩余空间；剩余空间留给空白填充列
const wide = resolveImportColumnWidths(2350, 968, true)
assertEqual(wide.widths.productName, 280, '商品名称列封顶')
assertEqual(wide.widths.englishName, 360, '英文名称列封顶')
assertEqual(wide.widths.status, 162, '状态列封顶')
assertEqual(wide.leftover >= IMPORT_FILLER_MIN_WIDTH, true, '超宽屏应留出空白填充列的空间')
assertEqual(FIXED_WIDTH + sumWidths(wide) + wide.leftover, 2350, '超宽屏列宽 + 剩余 = 容器宽度')

// 检测前没有状态列，只有一个 44 的删除列；表格仍按最小宽度 968 起算
const beforeDetect = resolveImportColumnWidths(960, 968, false)
assertEqual('status' in beforeDetect.widths, false, '检测前不分配状态列宽度')
assertEqual(beforeDetect.baseTotal, 886, '检测前基础列宽合计：固定列 444 + 删除列 44 + 后列 236 + 名称列 162')
assertEqual(FIXED_WIDTH + 44 + sumWidths(beforeDetect) + beforeDetect.leftover, 968, '检测前列宽 + 剩余 = 最小宽度')

console.log('ProductImport.gridRules.logic.test: ok')
