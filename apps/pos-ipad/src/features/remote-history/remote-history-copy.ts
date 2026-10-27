export const remoteHistoryEnglishCopy = {
  "title": "Remote order history",
  "subtitle": "Read-only orders already recorded by the store backend.",
  "action.back": "Back to sales",
  "action.refresh": "Refresh",
  "action.refreshing": "Refreshing…",
  "action.apply": "Apply filters",
  "action.reprint": "Reprint receipt",
  "action.reprinting": "Reprinting…",
  "action.refund": "Refund this order",
  "filters.from": "From date",
  "filters.to": "To date",
  "filters.device": "Terminal",
  "filters.keyword": "Order or product",
  "filters.devicePlaceholder": "All terminals",
  "filters.keywordPlaceholder": "Order ID, barcode or product",
  "filters.invalidDate": "From date cannot be later than to date.",
  "filters.quickToday": "Today",
  "filters.quickYesterday": "Yesterday",
  "filters.quickLast7Days": "Last 7 days",
  "list.title": "Orders",
  "list.items": "{{count}} lines",
  "list.payment": "Payment",
  "list.status": "Status",
  "details.title": "Order details",
  "details.lines": "Items",
  "details.payments": "Payments",
  "details.quantity": "Qty {{quantity}}",
  "details.discount": "Discount {{amount}}",
  "details.total": "Total",
  "details.actual": "Paid",
  "details.notFound": "This order is no longer available.",
  "details.failed": "Order details could not be loaded.",
  "details.select": "Select an order to inspect its items and payments.",
  "readonly.note":
    "Order history stays read-only. Eligible sales can open the existing returns workflow; recall remains unavailable.",
  "reprint.succeeded": "Reprint sent to the configured terminal printer.",
  "reprint.failed": "Receipt reprint could not be completed. Order details are unchanged.",
  "state.loading": "Loading remote history…",
  "state.empty": "No remote orders match these filters.",
  "state.failed": "Remote history could not be loaded.",
  "state.offline": "Online only",
  "state.offlineHint":
    "Remote history requires a backend connection. Local sales history remains available offline.",
  "state.unauthorized": "You do not have permission to view remote history.",
  "state.unavailable": "Remote history is not connected in this build.",
  "method.cash": "Cash",
  "method.card": "Card",
  "method.voucher": "Voucher",
} as const;

export type RemoteHistoryCopyKey = keyof typeof remoteHistoryEnglishCopy;

const remoteHistoryChineseCopy = {
  "title": "远程订单历史",
  "subtitle": "只读查看门店后端已记录的订单。",
  "action.back": "返回收银",
  "action.refresh": "刷新",
  "action.refreshing": "刷新中…",
  "action.apply": "应用筛选",
  "action.reprint": "重打小票",
  "action.reprinting": "正在重打…",
  "action.refund": "退款此订单",
  "filters.from": "开始日期",
  "filters.to": "结束日期",
  "filters.device": "终端",
  "filters.keyword": "订单或商品",
  "filters.devicePlaceholder": "全部终端",
  "filters.keywordPlaceholder": "订单号、条码或商品",
  "filters.invalidDate": "开始日期不能晚于结束日期。",
  "filters.quickToday": "今天",
  "filters.quickYesterday": "昨天",
  "filters.quickLast7Days": "近 7 天",
  "list.title": "订单",
  "list.items": "{{count}} 行",
  "list.payment": "付款",
  "list.status": "状态",
  "details.title": "订单详情",
  "details.lines": "商品",
  "details.payments": "付款",
  "details.quantity": "数量 {{quantity}}",
  "details.discount": "折扣 {{amount}}",
  "details.total": "合计",
  "details.actual": "实收",
  "details.notFound": "该订单已不可用。",
  "details.failed": "无法读取订单详情。",
  "details.select": "请选择订单查看商品和付款。",
  "readonly.note": "订单历史保持只读；符合条件的销售单可进入既有退货流程，取单仍不可用。",
  "reprint.succeeded": "已将重打请求发送到该终端已配置的打印机。",
  "reprint.failed": "小票重打未完成，订单详情未被修改。",
  "state.loading": "正在读取远程历史…",
  "state.empty": "没有符合筛选条件的远程订单。",
  "state.failed": "无法读取远程历史。",
  "state.offline": "仅在线可用",
  "state.offlineHint": "远程历史需要连接后端；离线时仍可查看本地销售历史。",
  "state.unauthorized": "当前收银员无权查看远程历史。",
  "state.unavailable": "当前版本尚未接入远程历史。",
  "method.cash": "现金",
  "method.card": "银行卡",
  "method.voucher": "代金券",
} as const satisfies Record<RemoteHistoryCopyKey, string>;

const copy = {
  en: remoteHistoryEnglishCopy,
  zh: remoteHistoryChineseCopy,
} as const;

export type RemoteHistoryLocale = keyof typeof copy;

export function resolveRemoteHistoryLocale(
  language?: string,
): RemoteHistoryLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function remoteHistoryText(
  locale: RemoteHistoryLocale,
  key: RemoteHistoryCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = copy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/gu, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
