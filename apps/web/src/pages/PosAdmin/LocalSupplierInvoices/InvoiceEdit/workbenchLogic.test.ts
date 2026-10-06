import dayjs from 'dayjs'
import { DetailAction, type LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import {
  buildInvoiceDetailSnapshotIndex,
  countEditedInvoiceDetailRows,
  getEditedInvoiceDetailFields,
} from './detailDirtyState'
import { splitCreateProductDetailGuids } from './batchExecuteConfirm'
import { applyInvoiceDetailInlineEdit } from './inlineEdit'
import { buildInvoiceHeaderFormValues, isInvoiceHeaderDirty } from './invoiceHeaderForm'
import { canLinkMatchedProduct, isProductLinkConfirmed, mergeProductCheckResult } from './matchedProductLink'
import { buildPricingEditorChanges } from './pricingEditorChanges'
import {
  filterDetailsByProgressBucket,
  getDetailProgressBucket,
  getDetailProgressStats,
  getExecutedPercent,
  getPendingExecutionDetailGuids,
} from './progressBuckets'
import { filterInvoiceDetails, isBarcodeStatusAbnormal } from './statusFilters'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const details: LocalSupplierInvoiceItemDto[] = [
  { detailGUID: 'executed', existingProductCount: 1, barcodeStatus: 1, activityType: 99 },
  { detailGUID: 'create', existingProductCount: 0, barcodeStatus: 1, activityType: DetailAction.CreateProduct },
  { detailGUID: 'update', existingProductCount: 1, barcodeStatus: 1, activityType: DetailAction.UpdatePurchasePrice },
  { detailGUID: 'multi', existingProductCount: 1, barcodeStatus: 2, barcodeMatchCount: 0, activityType: DetailAction.AddMultiCode },
  { detailGUID: 'wait', existingProductCount: 1, barcodeStatus: 2, barcodeMatchCount: 2, activityType: DetailAction.WaitForOperation },
  // 未检测行即使被手工设成「新建商品」，也必须先归入未检测，不能进入「执行全部待执行」。
  { detailGUID: 'unchecked', activityType: DetailAction.CreateProduct },
  { detailGUID: 'none', existingProductCount: 1, barcodeStatus: 1, activityType: DetailAction.None },
]

// ---- 处理进度分段 ----
assertEqual(getDetailProgressBucket(details[0]), 'executed', '99 应归为已执行')
assertEqual(getDetailProgressBucket(details[1]), 'pending', '新建商品应归为待执行')
assertEqual(getDetailProgressBucket(details[4]), 'waiting', '等待操作应单独成段')
assertEqual(getDetailProgressBucket(details[5]), 'unchecked', '未检测行优先归入未检测')
assertEqual(getDetailProgressBucket(details[6]), 'none', '操作类型为无应归为无需操作')
assertEqual(
  getDetailProgressBucket(details[6], { none: DetailAction.UpdateItemNumber }),
  'pending',
  '行内改过的操作类型（rowActions）应覆盖明细原值',
)

const stats = getDetailProgressStats(details)
assertDeepEqual(
  { executed: stats.executed, pending: stats.pending, waiting: stats.waiting, unchecked: stats.unchecked, none: stats.none },
  { executed: 1, pending: 3, waiting: 1, unchecked: 1, none: 1 },
  '五段计数应互斥',
)
assertEqual(
  stats.executed + stats.pending + stats.waiting + stats.unchecked + stats.none,
  stats.total,
  '五段合计必须等于明细行数',
)
assertDeepEqual(
  stats.pendingByAction,
  {
    [DetailAction.CreateProduct]: 1,
    [DetailAction.UpdatePurchasePrice]: 1,
    [DetailAction.UpdateItemNumber]: 0,
    [DetailAction.AddMultiCode]: 1,
  },
  '待执行应按操作类型拆分计数',
)
assertDeepEqual(
  getPendingExecutionDetailGuids(details),
  ['create', 'update', 'multi'],
  '执行全部待执行只应包含已检测且待执行的行',
)
assertDeepEqual(
  filterDetailsByProgressBucket(details, 'waiting').map((item) => item.detailGUID),
  ['wait'],
  '按分段筛选应只保留该段',
)
assertEqual(filterDetailsByProgressBucket(details, 'all').length, details.length, '全部不应过滤')
assertEqual(getExecutedPercent(stats), 14, '已执行占比应四舍五入为整数百分比')
assertEqual(getExecutedPercent({ total: 0, executed: 0 }), 0, '空明细的已执行占比应为 0')

// ---- 条码异常组合筛选 ----
assert(isBarcodeStatusAbnormal(details[3]), '无匹配属于条码异常')
assert(isBarcodeStatusAbnormal(details[4]), '多匹配属于条码异常')
assert(!isBarcodeStatusAbnormal(details[2]), '条码正常不属于异常')
assertDeepEqual(
  filterInvoiceDetails(details, {
    searchText: '',
    priceFilter: 'all',
    productStatusFilter: 'all',
    barcodeStatusFilter: 'abnormal',
  }).map((item) => item.detailGUID),
  ['multi', 'wait'],
  '条码异常筛选应同时命中无匹配和多匹配',
)

// ---- 明细未保存修改 ----
const snapshot: LocalSupplierInvoiceItemDto[] = [
  { detailGUID: 'a', quantity: 10, purchasePrice: 1.2, retailPrice: 2.5, productName: 'Card', discountRate: 0.1 },
  { detailGUID: 'b', quantity: 5, purchasePrice: 3, retailPrice: undefined, productName: '', activityType: DetailAction.None },
]
const snapshotIndex = buildInvoiceDetailSnapshotIndex(snapshot)
assertEqual(countEditedInvoiceDetailRows(snapshot, snapshotIndex), 0, '与快照一致时不应有未保存修改')

let edited = applyInvoiceDetailInlineEdit(snapshot, 'a', 'quantity', 12)
edited = applyInvoiceDetailInlineEdit(edited, 'a', 'retailPrice', 2.99)
assertDeepEqual(
  getEditedInvoiceDetailFields(edited[0], snapshotIndex),
  ['quantity', 'retailPrice'],
  '应列出被修改的字段；金额随数量重算但不单独算作修改',
)
assertEqual(countEditedInvoiceDetailRows(edited, snapshotIndex), 1, '同一行改多个字段只算一行')

const actionOnly = snapshot.map((item) => (item.detailGUID === 'b' ? { ...item, activityType: DetailAction.CreateProduct } : item))
assertEqual(countEditedInvoiceDetailRows(actionOnly, snapshotIndex), 0, '操作类型会立即落库，不算未保存修改')

const emptyEquivalent = snapshot.map((item) => (item.detailGUID === 'b' ? { ...item, productName: undefined } : item))
assertEqual(countEditedInvoiceDetailRows(emptyEquivalent, snapshotIndex), 0, '空字符串与空值落库语义相同，不算修改')

const reverted = applyInvoiceDetailInlineEdit(edited, 'a', 'quantity', 10)
assertDeepEqual(
  getEditedInvoiceDetailFields(applyInvoiceDetailInlineEdit(reverted, 'a', 'retailPrice', 2.5)[0], snapshotIndex),
  [],
  '改回原值后不应再算未保存',
)

// ---- 表头未保存修改 ----
const invoice = {
  invoiceGUID: 'invoice-1',
  storeCode: 'S01',
  supplierCode: 'SUP01',
  orderDate: '2026-10-01T00:00:00',
  inboundDate: undefined,
  remarks: 'Halloween',
  createdAt: '2026-10-05T00:00:00Z',
}
const headerValues = buildInvoiceHeaderFormValues(invoice)
assert(!isInvoiceHeaderDirty(headerValues, invoice), '刚加载的表头不应算修改')
assert(!isInvoiceHeaderDirty({ ...headerValues, remarks: ' Halloween ' }, invoice), '首尾空格不影响落库，不算修改')
assert(isInvoiceHeaderDirty({ ...headerValues, inboundDate: dayjs('2026-10-06') }, invoice), '填写入库日期应算修改')
assert(isInvoiceHeaderDirty({ ...headerValues, supplierCode: 'SUP02' }, invoice), '更换供应商应算修改')
assert(!isInvoiceHeaderDirty(headerValues, null), '订单未加载时不应算修改')

// ---- 定价弹窗只提交改动过的字段 ----
const pricingDetail: LocalSupplierInvoiceItemDto = {
  detailGUID: 'p1',
  autoPricing: true,
  pricingFloatRate: 2.5,
  newAutoRetailPrice: 4.5,
  isSpecialProduct: undefined,
  discountRate: undefined,
}
const unchangedDraft = {
  autoPricing: true,
  pricingFloatRate: 2.5,
  newAutoRetailPrice: 4.5,
  isSpecialProduct: false,
  discountPercent: null,
}
assertDeepEqual(buildPricingEditorChanges(pricingDetail, unchangedDraft), [], '未改动时不应提交任何字段；特殊商品空值等同于否')
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, autoPricing: false, discountPercent: 10 }),
  [
    { field: 'autoPricing', value: false },
    { field: 'discountRate', value: 10 },
  ],
  '应只返回改动的字段，折扣率以百分比交给行内规整逻辑',
)
assertDeepEqual(
  buildPricingEditorChanges(pricingDetail, { ...unchangedDraft, pricingFloatRate: null, newAutoRetailPrice: null }),
  [],
  '数字留空表示不修改，不能把空值兜底成 0',
)
assertDeepEqual(
  buildPricingEditorChanges({ ...pricingDetail, discountRate: 0.1 }, { ...unchangedDraft, discountPercent: 10 }),
  [],
  '折扣率按百分比比较，0.1 与 10% 视为未改动',
)

// 「新建商品」与其余操作分开执行：按当前操作类型（行内改过的优先）分组，保持传入顺序。
{
  const splitDetails = [
    { detailGUID: 'c1', activityType: DetailAction.CreateProduct },
    { detailGUID: 'p1', activityType: DetailAction.UpdatePurchasePrice },
    { detailGUID: 'c2', activityType: DetailAction.UpdatePurchasePrice },
    { detailGUID: 'm1', activityType: DetailAction.AddMultiCode },
  ]
  const split = splitCreateProductDetailGuids(['c1', 'p1', 'c2', 'm1'], splitDetails, { c2: DetailAction.CreateProduct })
  assertDeepEqual(split.createGuids, ['c1', 'c2'], '新建商品行按当前操作类型识别，行内改成新建的也算')
  assertDeepEqual(split.otherGuids, ['p1', 'm1'], '其余操作保持传入顺序')
  assertDeepEqual(
    splitCreateProductDetailGuids(['x'], [], { x: DetailAction.CreateProduct }),
    { createGuids: ['x'], otherGuids: [] },
    '明细不在当前列表时按行内操作类型判断',
  )
}

// 选用已有商品：只开放给检测后主档不存在的行；检测结果合并只补空的上次进货价。
assert(canLinkMatchedProduct({ detailGUID: 'n', existingProductCount: 0, barcodeStatus: 2, barcodeMatchCount: 1 }), '主档不存在的行应允许选用已有商品')
assert(!canLinkMatchedProduct({ detailGUID: 'e', existingProductCount: 1, barcodeStatus: 1 }), '主档已存在的行不应允许选用')
assert(!canLinkMatchedProduct({ detailGUID: 'u' }), '未检测的行不应允许选用')
assert(!canLinkMatchedProduct({ detailGUID: 'c', existingProductCount: 0, isCreatedByThisInvoice: true }), '本单新品不应允许选用')
const linkedDetail = mergeProductCheckResult(
  { detailGUID: 'link', existingProductCount: 0, barcodeStatus: 2, barcodeMatchCount: 1, lastPurchasePrice: 2.8, activityType: DetailAction.WaitForOperation },
  {
    detailGuid: 'link',
    productStatus: 1,
    barcodeStatus: 1,
    existingProductCount: 1,
    barcodeMatchCount: 1,
    lastPurchasePrice: 3.5,
    productInfo: { productCode: 'P-LINK', storeProductCode: '1005P-LINK' },
    defaultAction: DetailAction.UpdatePurchasePrice,
  },
)
assertEqual(linkedDetail.productCode, 'P-LINK', '应回填所选商品编码')
assertEqual(linkedDetail.storeProductCode, '1005P-LINK', '应回填分店商品编码')
assertEqual(linkedDetail.existingProductCount, 1, '选用后主档应显示存在')
assertEqual(linkedDetail.lastPurchasePrice, 2.8, '已有上次进货价快照不能被检测覆盖')
assertEqual(linkedDetail.activityType, DetailAction.UpdatePurchasePrice, '默认操作应取检测结果')
assert(isProductLinkConfirmed({ detailGuid: 'x', productStatus: 1, barcodeStatus: 1, existingProductCount: 1, productInfo: { productCode: 'p-link ' } }, 'P-LINK'), '商品编码比较应忽略大小写与空白')
assert(!isProductLinkConfirmed({ detailGuid: 'x', productStatus: 2, barcodeStatus: 2, existingProductCount: 0 }, 'P-LINK'), '后端清掉关联时应判定未关联')
assert(!isProductLinkConfirmed(undefined, 'P-LINK'), '缺少检测结果时应判定未关联')

console.log('InvoiceEdit workbenchLogic tests: ok')
