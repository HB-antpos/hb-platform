/** 进货单明细（编辑）页的路由；与 router/routes.tsx 中的定义一致。 */
export const INVOICE_EDIT_ROUTE_PATH = '/pos-admin/local-supplier-invoices/:id'

interface RouteLike {
  path?: string
  params?: Record<string, string | undefined>
}

/**
 * 明细页实际绑定的进货单：只有当前路由确实是本页时才接受地址里的单号，否则沿用上一次的值。
 * KeepAlive 隐藏的标签页也会收到全局路由变化（如切到列表页），若跟着变成空值，
 * 会清掉进行中的后台任务（商品检测、粘贴等）的跟踪，任务完成后结果被丢弃，切回来也不会刷新。
 */
export function resolveInvoiceEditGuid(previous: string | undefined, route: RouteLike | null | undefined) {
  const id = route?.path === INVOICE_EDIT_ROUTE_PATH ? route.params?.id?.trim() : undefined
  return id ? id : previous
}
