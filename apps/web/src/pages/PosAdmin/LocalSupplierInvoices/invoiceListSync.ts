/**
 * 分店进货单列表与明细的跨页同步。
 *
 * 列表和明细都是保活（keep-alive）页面：切走后实例仍然挂载，切回列表时只要查询条件没变就不会重新请求。
 * 明细里做商品检测、粘贴、保存、执行操作、删除等之后，列表上的「是否检测」、明细数、新品、涨降价、金额
 * 以及状态分段与分店栏的单数都会变化，但列表一直显示旧值。
 *
 * 明细操作成功后发布「某张单已变化」；列表收到后防抖片刻即在后台静默刷新当前页（用户通常还在明细页，
 * 返回时数据已是新的），若此时已切回列表则立即刷新。刷新全程不显示转圈、不清空旧行，
 * 分段/分店计数只在受影响时才补算。列表没有未保存的编辑内容，所以整页重新请求是安全的。
 */
export type LocalSupplierInvoiceChangedListener = (invoiceGuid: string) => void

const listeners = new Set<LocalSupplierInvoiceChangedListener>()

/** 明细连续操作（粘贴 → 检测 → 执行）时合并成一次后台刷新的等待时长。 */
export const INVOICE_LIST_REFRESH_DEBOUNCE_MS = 1000

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

/**
 * 列表是否该跳过防抖、立刻刷新：收到过变化通知且列表当前可见（用户刚切回，别再让他等）。
 * 列表隐藏时由防抖定时器在后台刷新。
 */
export function shouldRefreshInvoiceList(stale: boolean, active: boolean) {
  return stale && active
}

/** 判定计数是否受影响所需的行字段：都是分段计数/分店计数的筛选条件或分组依据。 */
export interface InvoiceListCountKeyRow {
  invoiceGUID: string
  storeCode?: string
  supplierCode?: string
  invoiceNo?: string
  orderDate?: string
  createdAt?: string
  isProductChecked?: boolean
}

/**
 * 明细变化后，列表上方的分段计数（全部/待检测）与左侧分店单数是否需要重算。
 *
 * 这些计数只取决于「哪些单落在筛选条件内、各自属于哪个分店、是否已检测」。对每张变化的单：
 * - 刷新前后有一边不在当前页：可能被筛选条件排除/纳入了（或本来就不在这一页），无法判断，保守重算；
 * - 两边都在、但分店/供应商/单号/订单日期/是否检测任一变化：成员关系或分组变了，重算；
 * - 其余（金额、明细数、备注、涨降价等变化）不影响任何计数，跳过这 3 路较重的请求。
 */
export function invoiceChangeAffectsListCounts(
  changedGuids: readonly string[],
  previousRows: readonly InvoiceListCountKeyRow[],
  nextRows: readonly InvoiceListCountKeyRow[],
): boolean {
  if (changedGuids.length === 0) {
    return false
  }
  const previousByGuid = new Map(previousRows.map((row) => [row.invoiceGUID, row]))
  const nextByGuid = new Map(nextRows.map((row) => [row.invoiceGUID, row]))
  return changedGuids.some((guid) => {
    const previous = previousByGuid.get(guid)
    const next = nextByGuid.get(guid)
    if (!previous || !next) {
      return true
    }
    return (
      previous.storeCode !== next.storeCode
      || previous.supplierCode !== next.supplierCode
      || previous.invoiceNo !== next.invoiceNo
      || previous.orderDate !== next.orderDate
      || previous.createdAt !== next.createdAt
      || Boolean(previous.isProductChecked) !== Boolean(next.isProductChecked)
    )
  })
}
