import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { StoreOrderFlowStatus } from '../../../types/storeOrder'
import {
  applyFlowStatusToOrder,
  applyFlowStatusToOrderList,
  publishStoreOrderFlowStatusChanged,
  subscribeStoreOrderFlowStatusChanged,
} from './storeOrderFlowStatusSync'

function runTest(name: string, execute: () => void) {
  execute()
  console.log(`ok - ${name}`)
}

runTest('发布后所有订阅者都收到订单与新状态', () => {
  const received: Array<[string, StoreOrderFlowStatus]> = []
  const unsubscribeA = subscribeStoreOrderFlowStatusChanged((guid, status) => received.push([`a:${guid}`, status]))
  const unsubscribeB = subscribeStoreOrderFlowStatusChanged((guid, status) => received.push([`b:${guid}`, status]))

  publishStoreOrderFlowStatusChanged('order-1', StoreOrderFlowStatus.Picking)

  assert.deepEqual(received, [
    ['a:order-1', StoreOrderFlowStatus.Picking],
    ['b:order-1', StoreOrderFlowStatus.Picking],
  ])
  unsubscribeA()
  unsubscribeB()
})

runTest('取消订阅后不再收到通知', () => {
  let calls = 0
  const unsubscribe = subscribeStoreOrderFlowStatusChanged(() => {
    calls += 1
  })

  publishStoreOrderFlowStatusChanged('order-1', StoreOrderFlowStatus.Picking)
  unsubscribe()
  publishStoreOrderFlowStatusChanged('order-1', StoreOrderFlowStatus.Picking)

  assert.equal(calls, 1)
})

runTest('监听器在通知过程中取消订阅，不影响其它监听器收到通知', () => {
  const calls: string[] = []
  const unsubscribeFirst = subscribeStoreOrderFlowStatusChanged(() => {
    calls.push('first')
    unsubscribeFirst()
  })
  const unsubscribeSecond = subscribeStoreOrderFlowStatusChanged(() => calls.push('second'))

  publishStoreOrderFlowStatusChanged('order-1', StoreOrderFlowStatus.Picking)

  assert.deepEqual(calls, ['first', 'second'])
  unsubscribeSecond()
})

runTest('订单编号为空时不发布', () => {
  let calls = 0
  const unsubscribe = subscribeStoreOrderFlowStatusChanged(() => {
    calls += 1
  })

  publishStoreOrderFlowStatusChanged('', StoreOrderFlowStatus.Picking)

  assert.equal(calls, 0)
  unsubscribe()
})

runTest('明细：命中同一订单时只改 flowStatus，其余字段原样保留', () => {
  const detail = { orderGUID: 'order-1', orderNo: 'SO-1', flowStatus: StoreOrderFlowStatus.Submitted, remarks: '备注' }

  const next = applyFlowStatusToOrder(detail, 'order-1', StoreOrderFlowStatus.Picking)

  assert.deepEqual(next, { ...detail, flowStatus: StoreOrderFlowStatus.Picking })
  assert.equal(detail.flowStatus, StoreOrderFlowStatus.Submitted, '不得原地修改旧对象')
})

runTest('明细：订单编号大小写不同也视为同一订单', () => {
  const detail = { orderGUID: 'ABC-DEF', flowStatus: StoreOrderFlowStatus.Submitted }

  assert.equal(applyFlowStatusToOrder(detail, 'abc-def', StoreOrderFlowStatus.Picking)?.flowStatus, StoreOrderFlowStatus.Picking)
})

runTest('明细：不是同一订单、状态已一致或没有明细时原样返回同一个引用', () => {
  const detail = { orderGUID: 'order-1', flowStatus: StoreOrderFlowStatus.Picking }

  assert.equal(applyFlowStatusToOrder(detail, 'order-2', StoreOrderFlowStatus.Completed), detail)
  assert.equal(applyFlowStatusToOrder(detail, 'order-1', StoreOrderFlowStatus.Picking), detail)
  assert.equal(applyFlowStatusToOrder(null, 'order-1', StoreOrderFlowStatus.Picking), null)
})

runTest('列表：只替换命中的那一行，其余行保持原对象', () => {
  const rows = [
    { orderGUID: 'order-1', flowStatus: StoreOrderFlowStatus.Submitted },
    { orderGUID: 'order-2', flowStatus: StoreOrderFlowStatus.Submitted },
  ]

  const next = applyFlowStatusToOrderList(rows, 'order-2', StoreOrderFlowStatus.Picking)

  assert.notEqual(next, rows)
  assert.equal(next[0], rows[0])
  assert.deepEqual(next[1], { orderGUID: 'order-2', flowStatus: StoreOrderFlowStatus.Picking })
  assert.equal(rows[1].flowStatus, StoreOrderFlowStatus.Submitted, '不得原地修改旧数组')
})

runTest('列表：没有行需要变化时返回原数组引用', () => {
  const rows = [{ orderGUID: 'order-1', flowStatus: StoreOrderFlowStatus.Picking }]

  assert.equal(applyFlowStatusToOrderList(rows, 'order-9', StoreOrderFlowStatus.Picking), rows)
  assert.equal(applyFlowStatusToOrderList(rows, 'order-1', StoreOrderFlowStatus.Picking), rows)
  assert.deepEqual(applyFlowStatusToOrderList([], 'order-1', StoreOrderFlowStatus.Picking), [])
})

// ---- 接线契约：项目没有 jsdom，页面接线用源码断言兜底，防止以后有人删掉发布点或订阅点 ----

function readSource(relativePath: string) {
  return readFileSync(resolve(process.cwd(), relativePath), 'utf8')
}

runTest('配货单页在 start-picking 成功后（且在是否仍激活的判断之前）发布「配货中」', () => {
  const source = readSource('src/pages/Warehouse/StoreOrders/PickingList.tsx')

  const startPickingAt = source.indexOf('await startPickingStoreOrder(order.orderGUID)')
  const publishAt = source.indexOf('publishStoreOrderFlowStatusChanged(order.orderGUID, StoreOrderFlowStatus.Picking)')
  const activeGuardAt = source.indexOf('if (!activeRef.current) {', startPickingAt)

  assert.ok(startPickingAt >= 0, '应保留 start-picking 调用')
  assert.ok(publishAt > startPickingAt, '发布必须在 start-picking 成功之后')
  assert.ok(publishAt < activeGuardAt, '页面被隐藏时也要发布，所以必须在 activeRef 判断之前')
})

runTest('明细页订阅并只合入 flowStatus，不通过重新加载来同步', () => {
  const source = readSource('src/pages/Warehouse/StoreOrders/Detail.tsx')
  const subscribeAt = source.indexOf('subscribeStoreOrderFlowStatusChanged((orderGuid, flowStatus) => {')

  assert.ok(subscribeAt >= 0, '明细页必须订阅流程状态变更')
  const handlerSource = source.slice(subscribeAt, subscribeAt + 200)
  assert.ok(handlerSource.includes('applyFlowStatusToOrder(current, orderGuid, flowStatus)'))
  assert.ok(!handlerSource.includes('loadDetail'), '重新加载会清空未保存的编辑，不能用来同步状态')
})

runTest('订单列表页订阅并把状态合入对应行', () => {
  const source = readSource('src/pages/Warehouse/StoreOrders/index.tsx')
  const subscribeAt = source.indexOf('subscribeStoreOrderFlowStatusChanged((orderGuid, flowStatus) => {')

  assert.ok(subscribeAt >= 0, '订单列表页必须订阅流程状态变更')
  assert.ok(source.slice(subscribeAt, subscribeAt + 200).includes('applyFlowStatusToOrderList(current, orderGuid, flowStatus)'))
})

console.log('storeOrderFlowStatusSync.test: ok')
