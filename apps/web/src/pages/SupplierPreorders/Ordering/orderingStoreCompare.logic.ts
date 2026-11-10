// 供应商节日预订单「下单」页 · 展开行「各分店去年对比」表的纯逻辑（行顺序）。
// 不依赖 React / antd，便于 node 直接跑单测。

import type { SupplierPreorderStoreHistory } from '../../../types/supplierPreorder'

/**
 * 「各分店去年对比」表的行顺序：按去年销量从高到低。
 * 去年有记录的店在前（销量相同保持原顺序，后端按店号排）；去年没有记录的店（销量显示「—」）排最后，
 * 即使它是当前分店也不置顶——当前分店靠行高亮标出。返回新数组，不改入参。
 */
export function sortStoresBySalesDesc(stores: readonly SupplierPreorderStoreHistory[]): SupplierPreorderStoreHistory[] {
  return stores
    .map((store, index) => ({ store, index }))
    .sort((a, b) => {
      if (a.store.hasData !== b.store.hasData) return a.store.hasData ? -1 : 1
      if (a.store.hasData && a.store.salesQty !== b.store.salesQty) return b.store.salesQty - a.store.salesQty
      return a.index - b.index
    })
    .map((entry) => entry.store)
}
