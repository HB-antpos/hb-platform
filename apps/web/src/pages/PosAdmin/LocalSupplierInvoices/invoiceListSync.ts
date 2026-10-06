/**
 * 分店进货单列表与明细的跨页同步。
 *
 * 列表和明细都是保活（keep-alive）页面：切走后实例仍然挂载，切回列表时只要查询条件没变就不会重新请求。
 * 明细里做商品检测、粘贴、保存、执行操作、删除等之后，列表上的「是否检测」、明细数、新品、涨降价、金额
 * 以及状态分段与分店栏的单数都会变化，但列表一直显示旧值。
 *
 * 明细操作成功后发布「某张单已变化」；列表收到后标记过期，可见时立即刷新当前页与计数，
 * 隐藏在后台时等切回再刷新。列表没有未保存的编辑内容，所以整页重新请求是安全的。
 */
export type LocalSupplierInvoiceChangedListener = (invoiceGuid: string) => void

const listeners = new Set<LocalSupplierInvoiceChangedListener>()

/** 订阅进货单变化；返回取消订阅函数，供 useEffect 清理使用。 */
export function subscribeLocalSupplierInvoiceChanged(listener: LocalSupplierInvoiceChangedListener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** 通知所有已挂载（含保活隐藏）的页面：这张进货单的明细或表头已在服务端变化。 */
export function publishLocalSupplierInvoiceChanged(invoiceGuid: string | undefined) {
  if (!invoiceGuid) {
    return
  }
  // 复制一份再遍历：监听器内部触发的卸载可能在遍历过程中取消订阅。
  for (const listener of [...listeners]) {
    listener(invoiceGuid)
  }
}

/** 列表是否该现在刷新：收到过变化通知且列表当前可见。 */
export function shouldRefreshInvoiceList(stale: boolean, active: boolean) {
  return stale && active
}
