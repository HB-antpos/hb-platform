import type { StoreOrderFlowStatus } from '../../../types/storeOrder'

/**
 * 订单流程状态的跨页同步。
 *
 * 订单列表、订单明细、配货单都是保活（keep-alive）页面：切走后实例仍然挂载，
 * 但切回时只要订单与查询条件没变就不会重新请求。某个页面改了订单流程状态
 * （例如打印配货单时自动「开始配货」，已提交 → 配货中）之后，其它页面会一直显示旧状态。
 *
 * 这里只同步 flowStatus 这一个字段：不触发重新请求，也不碰明细页里尚未保存的编辑内容
 * （重新加载明细会清空 editingRows 与表头草稿，所以不能用「切回就刷新」来解决）。
 */
export type StoreOrderFlowStatusListener = (orderGuid: string, flowStatus: StoreOrderFlowStatus) => void

const listeners = new Set<StoreOrderFlowStatusListener>()

/** 订阅订单流程状态变更；返回取消订阅函数，供 useEffect 清理使用。 */
export function subscribeStoreOrderFlowStatusChanged(listener: StoreOrderFlowStatusListener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** 通知所有已挂载（含保活隐藏）的页面：某订单的流程状态已变为 flowStatus。 */
export function publishStoreOrderFlowStatusChanged(orderGuid: string, flowStatus: StoreOrderFlowStatus) {
  if (!orderGuid) {
    return
  }
  // 复制一份再遍历：监听器内部触发的卸载可能在遍历过程中取消订阅。
  for (const listener of [...listeners]) {
    listener(orderGuid, flowStatus)
  }
}

function isSameOrder(left: string | undefined, right: string) {
  return Boolean(left) && left!.toLowerCase() === right.toLowerCase()
}

/**
 * 把新的流程状态合入单个订单（明细页的 detail）。
 * 不是同一订单或状态本就一致时原样返回同一个引用，避免无谓的重新渲染。
 */
export function applyFlowStatusToOrder<T extends { orderGUID?: string; flowStatus?: StoreOrderFlowStatus }>(
  order: T | null,
  orderGuid: string,
  flowStatus: StoreOrderFlowStatus,
): T | null {
  if (!order || !isSameOrder(order.orderGUID, orderGuid) || order.flowStatus === flowStatus) {
    return order
  }
  return { ...order, flowStatus }
}

/**
 * 把新的流程状态合入订单列表中的对应行。
 * 没有任何行需要变化时返回原数组引用；只替换命中的那一行，其余行保持原对象。
 */
export function applyFlowStatusToOrderList<T extends { orderGUID: string; flowStatus: StoreOrderFlowStatus }>(
  rows: T[],
  orderGuid: string,
  flowStatus: StoreOrderFlowStatus,
): T[] {
  let changed = false
  const nextRows = rows.map((row) => {
    if (!isSameOrder(row.orderGUID, orderGuid) || row.flowStatus === flowStatus) {
      return row
    }
    changed = true
    return { ...row, flowStatus }
  })
  return changed ? nextRows : rows
}
