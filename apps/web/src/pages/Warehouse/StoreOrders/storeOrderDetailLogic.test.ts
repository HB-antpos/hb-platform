import assert from 'node:assert/strict'

import { StoreOrderFlowStatus } from '../../../types/storeOrder'

import {
  buildStoreOrderDetailFilterChips,
  buildStoreOrderHeaderDraft,
  buildStoreOrderProgressSteps,
  computeStoreOrderShipProgress,
  describeStoreOrderDetailAllocDiff,
  diffStoreOrderHeaderDraft,
  formatStoreOrderDetailRange,
  isStoreOrderDetailPageScopedSum,
  resolveStoreOrderDetailAllCount,
  resolveStoreOrderDetailColumnOrderBase,
  resolveStoreOrderDetailFlowActions,
  resolveStoreOrderLineAssigneeState,
  summarizeStoreOrderEditedLines,
} from './storeOrderDetailLogic'

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

runTest('概况卡主操作：已提交开始配货（完成订单收进 ⋯），配货中完成订单，其余无主操作', () => {
  assert.deepEqual(resolveStoreOrderDetailFlowActions(StoreOrderFlowStatus.Submitted, true), {
    primary: 'startPicking',
    completeInMoreMenu: true,
  })
  assert.deepEqual(resolveStoreOrderDetailFlowActions(StoreOrderFlowStatus.Picking, true), {
    primary: 'completeOrder',
    completeInMoreMenu: false,
  })
  for (const status of [StoreOrderFlowStatus.Completed, StoreOrderFlowStatus.ShoppingCart, undefined, null]) {
    assert.deepEqual(resolveStoreOrderDetailFlowActions(status, true), { primary: null, completeInMoreMenu: false })
  }
})

runTest('概况卡主操作：没有订货管理权限时任何状态都没有写操作', () => {
  for (const status of [StoreOrderFlowStatus.Submitted, StoreOrderFlowStatus.Picking, StoreOrderFlowStatus.Completed]) {
    assert.deepEqual(resolveStoreOrderDetailFlowActions(status, false), { primary: null, completeInMoreMenu: false })
  }
})

runTest('三步进度只标出当前所处步骤，已完成是终态全部为完成', () => {
  const pick = (status?: StoreOrderFlowStatus) =>
    buildStoreOrderProgressSteps(status).map((step) => `${step.key}:${step.state}${step.current ? '*' : ''}`)

  assert.deepEqual(pick(StoreOrderFlowStatus.Submitted), ['submitted:active*', 'picking:pending', 'completed:pending'])
  assert.deepEqual(pick(StoreOrderFlowStatus.Picking), ['submitted:done', 'picking:active*', 'completed:pending'])
  assert.deepEqual(pick(StoreOrderFlowStatus.Completed), ['submitted:done', 'picking:done', 'completed:done*'])
  assert.deepEqual(pick(StoreOrderFlowStatus.ShoppingCart), ['submitted:pending', 'picking:pending', 'completed:pending'])
  assert.deepEqual(pick(undefined), ['submitted:pending', 'picking:pending', 'completed:pending'])
})

runTest('订单信息草稿由明细结果生成，空地址邮箱备注转成空字符串', () => {
  assert.deepEqual(
    buildStoreOrderHeaderDraft({
      storeCode: 'CMP09',
      orderDate: '2026-10-05T00:00:00',
      outboundDate: undefined,
      shippingFee: 85,
      storeAddress: undefined,
      storeContactEmail: 'a@b.c',
      remarks: undefined,
    }),
    {
      storeCode: 'CMP09',
      orderDate: '2026-10-05T00:00:00',
      outboundDate: undefined,
      shippingFee: 85,
      address: '',
      contactEmail: 'a@b.c',
      remarks: '',
    },
  )
  assert.deepEqual(buildStoreOrderHeaderDraft(null), {
    storeCode: undefined,
    orderDate: undefined,
    outboundDate: undefined,
    shippingFee: undefined,
    address: '',
    contactEmail: '',
    remarks: '',
  })
})

runTest('订单信息差异：没改过返回空；日期只比较日期部分；运费按数值比较', () => {
  const baseline = buildStoreOrderHeaderDraft({
    storeCode: 'CMP09',
    orderDate: '2026-10-05T00:00:00',
    outboundDate: '2026-10-07T00:00:00',
    shippingFee: 85,
    storeAddress: 'Shop 12',
    storeContactEmail: 'a@b.c',
    remarks: '备注',
  })

  assert.deepEqual(diffStoreOrderHeaderDraft({ ...baseline }, baseline), [])
  // DatePicker 回写的 ISO / YYYY-MM-DD 与接口原值同一天，不算修改。
  assert.deepEqual(
    diffStoreOrderHeaderDraft({ ...baseline, orderDate: '2026-10-05T00:00:00.000Z', outboundDate: '2026-10-07' }, baseline),
    [],
  )
  assert.deepEqual(
    diffStoreOrderHeaderDraft({ ...baseline, shippingFee: 90, outboundDate: '2026-10-08', remarks: '' }, baseline),
    ['outboundDate', 'shippingFee', 'remarks'],
  )
  assert.deepEqual(diffStoreOrderHeaderDraft({ ...baseline, shippingFee: undefined }, baseline), ['shippingFee'])
  assert.deepEqual(
    diffStoreOrderHeaderDraft({ ...baseline, storeCode: 'BUR01', address: '', contactEmail: '' }, baseline),
    ['storeCode', 'address', 'contactEmail'],
  )
})

runTest('发货差异：未发、少发 N、主动配货 +N、一致', () => {
  assert.deepEqual(describeStoreOrderDetailAllocDiff(24, 0), { kind: 'unshipped' })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(24, undefined), { kind: 'unshipped' })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(24, 18), { kind: 'short', amount: 6 })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(0, 12), { kind: 'extra', amount: 12 })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(6, 10), { kind: 'extra', amount: 4 })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(48, 48), { kind: 'match' })
  assert.deepEqual(describeStoreOrderDetailAllocDiff(0, 0), { kind: 'match' })
})

runTest('未保存修改统计按整单保存 payload 计数：行数、发货数处数、进口价处数', () => {
  assert.deepEqual(summarizeStoreOrderEditedLines([]), { lineCount: 0, allocQuantityCount: 0, importPriceCount: 0 })
  assert.deepEqual(
    summarizeStoreOrderEditedLines([
      { quantity: 18, importPriceChanged: false },
      { quantity: undefined, importPriceChanged: true },
      { quantity: 0, importPriceChanged: true },
    ]),
    { lineCount: 3, allocQuantityCount: 2, importPriceCount: 2 },
  )
})

runTest('发货进度：百分比四舍五入，超过 100% 时进度条封顶，订货为 0 时不给百分比', () => {
  assert.deepEqual(computeStoreOrderShipProgress(1482, 1311), { percent: 88, barPercent: 88 })
  assert.deepEqual(computeStoreOrderShipProgress(10, 12), { percent: 120, barPercent: 100 })
  assert.deepEqual(computeStoreOrderShipProgress(0, 5), { percent: null, barPercent: 100 })
  assert.deepEqual(computeStoreOrderShipProgress(0, 0), { percent: null, barPercent: 0 })
  assert.deepEqual(computeStoreOrderShipProgress(undefined, undefined), { percent: null, barPercent: 0 })
})

runTest('已生效筛选条：文本、数值区间、上下架各成一个标签，并带上要清掉的键', () => {
  assert.deepEqual(buildStoreOrderDetailFilterChips(undefined), [])
  assert.deepEqual(buildStoreOrderDetailFilterChips({ itemNumber: '  ', quantityMin: Number.NaN }), [])
  assert.deepEqual(
    buildStoreOrderDetailFilterChips({
      itemNumber: ' HB31 ',
      locationCode: 'A-01',
      quantityMin: 1,
      allocQuantityMax: 0,
      importPriceMin: 2,
      importPriceMax: 5,
      isActive: false,
    }),
    [
      { field: 'itemNumber', text: 'HB31', removeKeys: ['itemNumber'] },
      { field: 'locationCode', text: 'A-01', removeKeys: ['locationCode'] },
      { field: 'quantity', min: 1, max: undefined, removeKeys: ['quantityMin', 'quantityMax'] },
      { field: 'allocQuantity', min: undefined, max: 0, removeKeys: ['allocQuantityMin', 'allocQuantityMax'] },
      { field: 'importPrice', min: 2, max: 5, removeKeys: ['importPriceMin', 'importPriceMax'] },
      { field: 'isActive', isActive: false, removeKeys: ['isActive'] },
    ],
  )
  assert.equal(formatStoreOrderDetailRange(1, 5), '1 – 5')
  assert.equal(formatStoreOrderDetailRange(1, undefined), '≥ 1')
  assert.equal(formatStoreOrderDetailRange(undefined, 0), '≤ 0')
  assert.equal(formatStoreOrderDetailRange(), '')
})

runTest('拣货负责人：整单没分配过不显示；做过分配但该行不在分段里显示未分配', () => {
  assert.equal(resolveStoreOrderLineAssigneeState({}, 'd-1'), 'none')
  assert.equal(resolveStoreOrderLineAssigneeState({ 'd-1': { segmentNo: 1 } }, 'd-1'), 'assigned')
  assert.equal(resolveStoreOrderLineAssigneeState({ 'd-1': { segmentNo: 1 } }, 'd-2'), 'unassigned')
})

runTest('预计销售额只覆盖本页：有筛选或整单超过一页时标为本页', () => {
  assert.equal(isStoreOrderDetailPageScopedSum({ itemsTotal: 128, pageItemCount: 128, hasActiveFilters: false }), false)
  assert.equal(isStoreOrderDetailPageScopedSum({ itemsTotal: 450, pageItemCount: 200, hasActiveFilters: false }), true)
  assert.equal(isStoreOrderDetailPageScopedSum({ itemsTotal: 6, pageItemCount: 6, hasActiveFilters: true }), true)
})

runTest('全部页签计数：无筛选用结果总数，有筛选改用整单 SKU 数', () => {
  assert.equal(resolveStoreOrderDetailAllCount({ itemsTotal: 128, totalSKU: 128, hasActiveFilters: false }), 128)
  assert.equal(resolveStoreOrderDetailAllCount({ itemsTotal: 6, totalSKU: 128, hasActiveFilters: true }), 128)
  assert.equal(resolveStoreOrderDetailAllCount({ itemsTotal: 6, totalSKU: undefined, hasActiveFilters: true }), 6)
})

runTest('列顺序基准：没拖过列序时切换订货体积列直接用新默认顺序，拖过的保留原顺序', () => {
  const withVolume = ['product', 'locationCode', 'quantity', 'allocatedImportAmount', 'orderVolume', 'allocVolume', 'actions']
  const withoutVolume = withVolume.filter((key) => key !== 'orderVolume')

  // 打开订货体积列：原顺序就是默认顺序 → 新列插在默认位置，而不是追加到末尾。
  assert.deepEqual(resolveStoreOrderDetailColumnOrderBase(withoutVolume, withVolume), withVolume)
  // 关闭订货体积列：同理回到不含该列的默认顺序。
  assert.deepEqual(resolveStoreOrderDetailColumnOrderBase(withVolume, withoutVolume), withoutVolume)
  // 拖过列序（数量列挪到货位前）则保留用户顺序，交给合并逻辑补齐。
  const customized = ['product', 'quantity', 'locationCode', 'allocatedImportAmount', 'allocVolume', 'actions']
  assert.equal(resolveStoreOrderDetailColumnOrderBase(customized, withVolume), customized)
  // 没有持久化值或值不是数组时原样返回，由合并逻辑回退默认顺序。
  assert.equal(resolveStoreOrderDetailColumnOrderBase(null, withVolume), null)
  assert.deepEqual(resolveStoreOrderDetailColumnOrderBase({ product: true }, withVolume), { product: true })
})

console.log('storeOrderDetailLogic.test: ok')
