import assert from 'node:assert/strict'

import {
  buildReturnPayloadFields,
  buildStoreOrderReturnBanner,
  canEnterNegativeAllocQuantity,
  computeReturnLinePreview,
  computeStoreOrderDocumentTotals,
  computeStoreOrderDraftTotals,
  computeStoreOrderGst,
  findPayloadMissingReturnReason,
  isStoreOrderReturnLine,
  isStoreOrderReturnReason,
  resolveReturnLineConflict,
  resolveStoreOrderDocumentType,
  resolveStoreOrderDocumentTypeOf,
  splitStoreOrderInvoiceLines,
  STORE_ORDER_RETURN_REASONS,
  toReturnAllocQuantity,
  withoutStoreOrderReturnLines,
} from './storeOrderReturnLogic'

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

// 设计图 ① 的示例：1 条主动配货（12 × 0.20 = 2.40）+ 退货 −5 × 1.50 + 退货 −2 × 3.20。
const sampleLines = [
  { detailGUID: 'bag', allocQuantity: 12, importPrice: 0.2, allocatedImportAmount: 2.4, volume: 0.001 },
  { detailGUID: 'bottle', allocQuantity: -5, importPrice: 1.5, allocatedImportAmount: -7.5, volume: 0.006 },
  { detailGUID: 'mug', allocQuantity: -2, importPrice: 3.2, allocatedImportAmount: -6.4, volume: 0.005 },
]
const sampleSaved = {
  netAmount: -11.5,
  shipAmount: 2.4,
  returnAmount: -13.9,
  netQuantity: 5,
  returnQuantity: 7,
  returnLineCount: 2,
}

runTest('退货行只看发货数是否为负；原因码集合与后端约定一致', () => {
  assert.equal(isStoreOrderReturnLine({ allocQuantity: -1 }), true)
  assert.equal(isStoreOrderReturnLine({ allocQuantity: 0 }), false)
  assert.equal(isStoreOrderReturnLine({ allocQuantity: undefined }), false)
  assert.deepEqual([...STORE_ORDER_RETURN_REASONS], ['damaged', 'wrongItem', 'overShipped', 'quality', 'nearExpiry', 'other'])
  assert.equal(isStoreOrderReturnReason('damaged'), true)
  assert.equal(isStoreOrderReturnReason('broken'), false)
  assert.equal(isStoreOrderReturnReason(undefined), false)
})

runTest('发货格放开负数：要退货权限且订货为 0，其余保持 ≥ 0', () => {
  assert.equal(canEnterNegativeAllocQuantity(true, 0), true)
  assert.equal(canEnterNegativeAllocQuantity(true, undefined), true)
  assert.equal(canEnterNegativeAllocQuantity(true, 12), false)
  assert.equal(canEnterNegativeAllocQuantity(false, 0), false)
})

runTest('汇总拆三段：发货金额 / 退货冲减 / 净额，GST 按净额（对照设计图 ①）', () => {
  const totals = computeStoreOrderDraftTotals({ saved: sampleSaved, lines: sampleLines, edits: {}, allLinesLoaded: true })
  assert.equal(totals.shipAmount, 2.4)
  assert.equal(totals.returnAmount, -13.9)
  assert.equal(totals.netAmount, -11.5)
  assert.equal(totals.gst, -1.15)
  assert.equal(totals.shipQuantity, 12)
  assert.equal(totals.returnQuantity, 7)
  assert.equal(totals.returnLineCount, 2)
  // 退回体积 = 5 × 0.006 + 2 × 0.005 = 0.04
  assert.equal(Number((totals.returnVolume ?? 0).toFixed(4)), 0.04)
  // 应退分店（含 GST）= −11.50 − 1.15 + 运费 0
  assert.equal(computeStoreOrderDocumentTotals(totals.netAmount, 0).total, -12.65)
})

runTest('汇总：没有退货行时与旧口径一致（发货 = 净额，退货 0，不出提示条）', () => {
  const totals = computeStoreOrderDraftTotals({
    saved: { netAmount: 100, netQuantity: 40 },
    lines: [{ detailGUID: 'a', allocQuantity: 40, importPrice: 2.5, allocatedImportAmount: 100 }],
    edits: {},
  })
  assert.equal(totals.shipAmount, 100)
  assert.equal(totals.returnAmount, 0)
  assert.equal(totals.netAmount, 100)
  assert.equal(totals.gst, 10)
  assert.equal(totals.returnLineCount, 0)
  assert.equal(totals.returnVolume, null)
  assert.equal(buildStoreOrderReturnBanner(totals), null)
})

runTest('汇总：未保存修改按正负拆分差额（正数改成负数 = 发货减少、退货增加）', () => {
  const totals = computeStoreOrderDraftTotals({
    saved: sampleSaved,
    lines: sampleLines,
    edits: {
      // 袋子 12 → 0：发货金额 2.40 → 0
      bag: { allocQuantity: 0 },
      // 杯子 −2 → −4：退货冲减 −6.40 → −12.80
      mug: { allocQuantity: -4 },
      // 水瓶只改价 1.50 → 2.00：退货冲减 −7.50 → −10.00
      bottle: { importPrice: 2 },
    },
    allLinesLoaded: true,
  })
  assert.equal(totals.shipAmount, 0)
  assert.equal(totals.returnAmount, -22.8)
  assert.equal(totals.netAmount, -22.8)
  assert.equal(totals.shipQuantity, 0)
  assert.equal(totals.returnQuantity, 9)
  assert.equal(totals.returnLineCount, 2)
})

runTest('汇总：新录入负数行（原先是 0）计入退货行数与件数；改回正数则撤销', () => {
  const lines = [{ detailGUID: 'x', allocQuantity: 0, importPrice: 2, allocatedImportAmount: 0 }]
  const saved = { netAmount: 0, netQuantity: 0 }
  const negative = computeStoreOrderDraftTotals({ saved, lines, edits: { x: { allocQuantity: -3 } } })
  assert.equal(negative.returnAmount, -6)
  assert.equal(negative.returnQuantity, 3)
  assert.equal(negative.returnLineCount, 1)
  assert.equal(negative.shipQuantity, 0)
  const back = computeStoreOrderDraftTotals({ saved, lines, edits: { x: { allocQuantity: 4 } } })
  assert.equal(back.returnLineCount, 0)
  assert.equal(back.shipAmount, 8)
})

runTest('单据类型按净额判定：< 0 退款单，= 0 与 > 0 发票；后端给了 documentType 以后端为准', () => {
  assert.equal(resolveStoreOrderDocumentType(-0.01), 'creditNote')
  assert.equal(resolveStoreOrderDocumentType(-11.5), 'creditNote')
  assert.equal(resolveStoreOrderDocumentType(0), 'invoice')
  assert.equal(resolveStoreOrderDocumentType(-0.001), 'invoice') // 不足 1 分按 0 处理
  assert.equal(resolveStoreOrderDocumentType(69.9), 'invoice')
  assert.equal(resolveStoreOrderDocumentTypeOf({ totalAllocatedImportAmount: -5 }), 'creditNote')
  assert.equal(resolveStoreOrderDocumentTypeOf({ totalAllocatedImportAmount: 5 }), 'invoice')
  assert.equal(resolveStoreOrderDocumentTypeOf({ documentType: 'creditNote', totalAllocatedImportAmount: 5 }), 'creditNote')
  assert.equal(resolveStoreOrderDocumentTypeOf({ totalImportAmount: -1 }), 'creditNote')
})

runTest('发票金额：GST 按净额，退款单合计为负；净额 0 不出现 -0', () => {
  assert.deepEqual(computeStoreOrderDocumentTotals(69.9, 0), {
    subTotal: 69.9,
    gst: 6.99,
    freight: 0,
    total: 76.89,
    documentType: 'invoice',
  })
  // 设计图 ③B：−13.90 → GST −1.39、合计 −15.29
  const credit = computeStoreOrderDocumentTotals(-13.9, 0)
  assert.equal(credit.gst, -1.39)
  assert.equal(credit.total, -15.29)
  assert.equal(credit.documentType, 'creditNote')
  const zero = computeStoreOrderDocumentTotals(0, 0)
  assert.equal(Object.is(zero.gst, -0), false)
  assert.equal(Object.is(zero.total, -0), false)
  assert.equal(computeStoreOrderGst(undefined), 0)
})

runTest('提示条：只有退货行才出现，按净额区分红色退款单 / 发票含调整项 / 净额为 0', () => {
  const base = { returnLineCount: 2, returnQuantity: 7 }
  assert.equal(buildStoreOrderReturnBanner({ ...base, returnLineCount: 0, netAmount: 10 }), null)
  assert.equal(buildStoreOrderReturnBanner({ ...base, netAmount: -11.5 })?.tone, 'credit')
  assert.equal(buildStoreOrderReturnBanner({ ...base, netAmount: 55 })?.tone, 'invoice')
  assert.equal(buildStoreOrderReturnBanner({ ...base, netAmount: 0 })?.tone, 'zero')
})

runTest('写入退货字段：只有发货为负才带，备注去空白，发货 ≥ 0 一律不带', () => {
  assert.deepEqual(buildReturnPayloadFields(-5, { returnReason: 'damaged', returnDisposition: 1, returnNote: '  瓶盖开裂  ' }), {
    returnReason: 'damaged',
    returnDisposition: 1,
    returnNote: '瓶盖开裂',
  })
  assert.deepEqual(buildReturnPayloadFields(-5, { returnReason: 'other', returnDisposition: 0, returnNote: '   ' }), {
    returnReason: 'other',
    returnDisposition: 0,
  })
  assert.deepEqual(buildReturnPayloadFields(-5, {}), {})
  assert.deepEqual(buildReturnPayloadFields(3, { returnReason: 'damaged', returnDisposition: 1 }), {})
  assert.deepEqual(buildReturnPayloadFields(0, { returnReason: 'damaged' }), {})
  assert.equal(buildReturnPayloadFields(-1, { returnNote: 'x'.repeat(300) }).returnNote?.length, 200)
})

runTest('整单保存拦截：发货为负却缺退货原因的行会被找出来', () => {
  assert.equal(findPayloadMissingReturnReason([{ quantity: 3 }, { quantity: undefined }]), undefined)
  assert.equal(findPayloadMissingReturnReason([{ quantity: -2, returnReason: 'damaged' }]), undefined)
  const missing = { quantity: -2, returnReason: undefined }
  assert.equal(findPayloadMissingReturnReason([{ quantity: 1 }, missing]), missing)
  assert.equal(findPayloadMissingReturnReason([{ quantity: -2, returnReason: 'broken' }])?.quantity, -2)
})

runTest('添加退货商品：本单已有该商品时的冲突判定', () => {
  assert.equal(resolveReturnLineConflict(undefined), 'none')
  assert.equal(resolveReturnLineConflict(null), 'none')
  assert.equal(resolveReturnLineConflict({ quantity: 12, allocQuantity: 12 }), 'ordered')
  assert.equal(resolveReturnLineConflict({ quantity: 12, allocQuantity: 0 }), 'ordered')
  assert.equal(resolveReturnLineConflict({ quantity: 0, allocQuantity: 8 }), 'shipped')
  assert.equal(resolveReturnLineConflict({ quantity: 0, allocQuantity: -3 }), 'existingReturn')
  assert.equal(resolveReturnLineConflict({ quantity: 0, allocQuantity: 0 }), 'none')
})

runTest('冲减金额预览：本行 ex GST / GST / 合计与加入后整单净额（对照设计图 ②）', () => {
  assert.deepEqual(computeReturnLinePreview({ quantity: 5, unitPrice: 1.5, currentNetAmount: -3.25 }), {
    amount: -7.5,
    gst: -0.75,
    total: -8.25,
    netAfter: -10.75,
  })
  // 覆盖已有退货行：先扣掉旧金额（−6.40）再加上新金额。
  assert.equal(
    computeReturnLinePreview({ quantity: 5, unitPrice: 1.5, currentNetAmount: -11.5, existingReturnAmount: -6.4 }).netAfter,
    -12.6,
  )
  // 数量未填 / 非法时预览为 0，不出现 NaN。
  assert.deepEqual(computeReturnLinePreview({ quantity: undefined, unitPrice: 2, currentNetAmount: 10 }), {
    amount: 0,
    gst: 0,
    total: 0,
    netAfter: 10,
  })
})

runTest('退货数量输入是正数，写入记为 发货 −N；0 / 空 / 负数不能提交', () => {
  assert.equal(toReturnAllocQuantity(5), -5)
  assert.equal(toReturnAllocQuantity(5.9), -5)
  assert.equal(toReturnAllocQuantity(0), null)
  assert.equal(toReturnAllocQuantity(undefined), null)
  assert.equal(toReturnAllocQuantity(-2), null)
})

runTest('发票明细拆段：负数行进「退货 / Adjustments」段，其余留在发货段', () => {
  const { supplied, returned } = splitStoreOrderInvoiceLines([
    { id: 1, allocQuantity: 30 },
    { id: 2, allocQuantity: -5 },
    { id: 3, allocQuantity: 0 },
    { id: 4, allocQuantity: undefined },
  ])
  assert.deepEqual(supplied.map((item) => item.id), [1, 3, 4])
  assert.deepEqual(returned.map((item) => item.id), [2])
})

runTest('配货单：去掉退货行，总发货数量加回退回件数；没有退货行时原样返回', () => {
  const plain = { items: [{ allocQuantity: 3 }, { allocQuantity: 0 }], totalAllocQuantity: 3 }
  assert.equal(withoutStoreOrderReturnLines(plain), plain)
  const withReturns = {
    items: [{ allocQuantity: 12 }, { allocQuantity: -5 }, { allocQuantity: -2 }],
    totalAllocQuantity: 5,
    totalReturnQuantity: 7,
    returnLineCount: 2,
  }
  const result = withoutStoreOrderReturnLines(withReturns)
  assert.deepEqual(result.items, [{ allocQuantity: 12 }])
  assert.equal(result.totalAllocQuantity, 12)
})

console.log('storeOrderReturnLogic.test: ok')
