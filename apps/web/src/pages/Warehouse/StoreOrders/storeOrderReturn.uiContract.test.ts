import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'

import detailMessagesEn from './storeOrderDetailMessages.en.json'
import detailMessagesZh from './storeOrderDetailMessages.zh.json'
import invoiceMessagesEn from './storeOrderInvoiceMessages.en.json'
import invoiceMessagesZh from './storeOrderInvoiceMessages.zh.json'
import { STORE_ORDER_RETURN_REASONS } from './storeOrderReturnLogic'

/**
 * 分店订单退货（负数发货）的源码型契约：权限码、页面门禁、文案键与「无退货行时页面不变」的关键开关。
 * 页面是 antd 大组件，这里不渲染，只核对不能悄悄漂移的约定（视觉由截图对照设计图验收）。
 */

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

const root = process.cwd()
const read = (relative: string) => readFileSync(path.resolve(root, relative), 'utf8').replace(/\r\n/g, '\n')
const detailSource = read('src/pages/Warehouse/StoreOrders/Detail.tsx')
const invoiceSource = read('src/pages/Warehouse/StoreOrders/Invoice.tsx')
const modalSource = read('src/pages/Warehouse/StoreOrders/StoreOrderReturnLineModal.tsx')
const accessSource = read('src/utils/access.ts')
const permissionsSource = read('src/types/permissions.ts')
const backendPermissionsSource = read('../../services/backend/BlazorApp.Shared/Constants/Permissions.cs')

type Json = { [key: string]: Json | string }
function leaf(resource: Json, dottedPath: string): string | undefined {
  let node: Json | string | undefined = resource
  for (const segment of dottedPath.split('.')) {
    if (typeof node !== 'object' || node === null) {
      return undefined
    }
    node = node[segment]
  }
  return typeof node === 'string' ? node : undefined
}

runTest('权限码与后端一致，且没有别名：只认 Warehouse.StoreOrderReturn', () => {
  assert.ok(permissionsSource.includes("StoreOrderReturn: 'Warehouse.StoreOrderReturn'"))
  assert.ok(backendPermissionsSource.includes('public const string StoreOrderReturn = "Warehouse.StoreOrderReturn";'))
  assert.ok(
    accessSource.includes('const canReturnStoreOrderGoods = hasPermission(P.Warehouse.StoreOrderReturn)'),
    '退货权限不能并入 Warehouse.Manage / ManageOrders，管理员由 hasPermission 统一放行',
  )
  assert.ok(accessSource.includes('    canReturnStoreOrderGoods: false,'), '空权限对象应默认 false')
})

runTest('退货原因码在页面文案与单据文案里中英文都有对应翻译', () => {
  for (const reason of STORE_ORDER_RETURN_REASONS) {
    for (const [label, resource, key] of [
      ['明细页中文', detailMessagesZh, `warehouseUi.storeOrderDetail.returnReason.${reason}`],
      ['明细页英文', detailMessagesEn, `warehouseUi.storeOrderDetail.returnReason.${reason}`],
      ['单据中文', invoiceMessagesZh, `warehouseUi.storeOrderInvoice.reason.${reason}`],
      ['单据英文', invoiceMessagesEn, `warehouseUi.storeOrderInvoice.reason.${reason}`],
    ] as const) {
      assert.ok(leaf(resource as Json, key), `${label}缺少原因文案 ${key}`)
    }
  }
  // 字面量映射里每个原因码都要出现（页面消息契约只能静态核对字面量键）。
  const fieldsSource = read('src/pages/Warehouse/StoreOrders/StoreOrderReturnFields.tsx')
  for (const reason of STORE_ORDER_RETURN_REASONS) {
    assert.ok(fieldsSource.includes(`case '${reason}':`), `原因标签映射缺少 ${reason}`)
    assert.ok(invoiceSource.includes(`case '${reason}':`), `单据原因映射缺少 ${reason}`)
  }
})

runTest('发货格：无退货权限或订货 > 0 时保持 min={0}；负数只对 canReturnGoods 放开', () => {
  assert.ok(detailSource.includes('min={allowNegative ? undefined : 0}'))
  assert.ok(detailSource.includes('const allowNegative = isNegativeAllocAllowed(record)'))
  assert.ok(detailSource.includes('const canReturnGoods = canUseWarehouseManagerActions && access.canReturnStoreOrderGoods'))
  assert.ok(detailSource.includes('canEnterNegativeAllocQuantity(canReturnGoods, line.quantity)'))
})

runTest('原因确认前不写退货草稿字段：onChange 只改发货数，确认 / 改回正数才动 returnReason', () => {
  const changeHandler = detailSource.slice(
    detailSource.indexOf('const handleAllocQuantityChange'),
    detailSource.indexOf('const handleConfirmReturnPopover'),
  )
  assert.ok(changeHandler.includes('setReturnPopover({ detailGUID: record.detailGUID, mode:'), '输入负数应弹出原因气泡')
  assert.ok(!/returnReason:\s*[A-Za-z]/.test(changeHandler.replace(/delete draft\.returnReason/g, '')), '输入负数时不应写入 returnReason')
  assert.ok(detailSource.includes('const handleRevertReturnPopover'), '应提供「改回正数」取消路径')
  assert.ok(detailSource.includes('delete rest.allocQuantity'), '取消应丢弃这次录入的负数发货')
})

runTest('整单保存与单行保存都在前端拦住缺原因的退货行，并只对退货行带退货字段', () => {
  assert.ok(detailSource.includes('findPayloadMissingReturnReason(payloads)'))
  assert.ok(detailSource.includes("t('warehouseUi.storeOrderDetail.returnReasonRequiredOnSave'"))
  assert.ok(detailSource.includes('buildReturnPayloadFields(effectiveAllocQuantity, {'))
  assert.ok(detailSource.includes('returnReason: item.returnReason,'))
})

runTest('无退货行时页面不变：提示条 / 退货页签 / 退货信息列 / 汇总拆分都以有退货行为前提', () => {
  assert.ok(detailSource.includes('{returnBanner && returnBannerValues ? ('))
  assert.ok(detailSource.includes('statSummary.returned > 0 || detailStatFilter ===') )
  assert.ok(detailSource.includes('(showReturnInfoColumn || column.key !==') )
  assert.ok(detailSource.includes('{draftTotals.returnLineCount > 0 ? ('))
  assert.ok(detailSource.includes('{canReturnGoods ? ('), '添加退货商品入口与弹窗只在有权限时渲染')
  assert.ok(detailSource.includes("key: 'return' as const"), '退货页签走后端 statFilter=return')
})

runTest('退货行不拣货：货位列显示固定说明，批量改发货数跳过退货行，配货单去掉退货行', () => {
  assert.ok(detailSource.includes("t('warehouseUi.storeOrderDetail.returnNoPicking')"))
  assert.ok(detailSource.includes("payload.type === 'allocQuantity' || payload.type === 'copyOrderQuantityToAllocQuantity'"))
  assert.ok(read('src/pages/Warehouse/StoreOrders/PickingList.tsx').includes('setOrder(withoutStoreOrderReturnLines(detail))'))
})

runTest('添加退货商品弹窗：先核对本单已有行，冲突时禁用提交，一步 batch-add 写入负数发货与退货三字段', () => {
  assert.ok(modalSource.includes('getStoreOrderDetailProductCodes(orderGUID)'))
  assert.ok(modalSource.includes("conflict === 'ordered' || conflict === 'shipped'"))
  assert.ok(modalSource.includes('batchAddStoreOrderLines({'))
  assert.ok(!modalSource.includes('batchUpdateStoreOrderLines'), '不再先加正数行再批量改负数')
  assert.ok(modalSource.includes('...buildReturnPayloadFields(returnAllocQuantity, {'))
  assert.ok(modalSource.includes('quantity: returnAllocQuantity'), '退货数量以负数发货写入')
})

runTest('发票页：单据类型按净额切换，退货行单独成段，GST 与合计走同一净额函数', () => {
  assert.ok(invoiceSource.includes('resolveStoreOrderDocumentTypeOf(order ?? {})'))
  assert.ok(invoiceSource.includes('computeStoreOrderDocumentTotals('))
  assert.ok(invoiceSource.includes('splitStoreOrderInvoiceLines('))
  assert.ok(invoiceSource.includes("t('warehouseUi.storeOrderInvoice.docTitleCredit')"))
  assert.ok(!invoiceSource.includes('subTotal * 0.1'), '发票页不应再自己现算 GST')
})

console.log('storeOrderReturn.uiContract.test: ok')
