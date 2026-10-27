export const localHistoryEnglishCopy = {
  "title": "Local sales history",
  "subtitle": "Orders stored securely on this iPad, available offline.",
  "action.back": "Back to sales",
  "action.refresh": "Refresh",
  "action.refreshing": "Refreshing…",
  "action.apply": "Apply filters",
  "action.loadMore": "Load more",
  "action.loadingMore": "Loading…",
  "action.reprint": "Reprint receipt",
  "action.reprinting": "Reprinting…",
  "filters.from": "From date",
  "filters.to": "To date",
  "filters.keyword": "Order or product",
  "filters.keywordPlaceholder": "Sequence, order ID, barcode or product",
  "filters.invalidDate": "From date cannot be later than to date.",
  "filters.quickToday": "Today",
  "filters.quickYesterday": "Yesterday",
  "filters.quickLast7Days": "Last 7 days",
  "filters.invalidQuery": "The history filters could not be applied.",
  "list.title": "Orders on this iPad",
  "list.sequence": "Local #{{sequence}}",
  "list.items": "{{count}} lines",
  "list.payment": "Payment",
  "details.title": "Order details",
  "receiptPreview.title": "Receipt preview",
  "receiptPreview.loading": "Loading receipt preview…",
  "receiptPreview.notFound": "This receipt preview is no longer available.",
  "receiptPreview.failed": "Receipt preview could not be loaded.",
  "receiptPreview.select": "Select an order to inspect its receipt preview.",
  "details.lines": "Items",
  "details.payments": "Payments",
  "details.quantity": "Qty {{quantity}}",
  "details.discount": "Discount {{amount}}",
  "details.total": "Total",
  "details.actual": "Paid",
  "details.notFound": "This local order is no longer available.",
  "details.failed": "Order details could not be loaded.",
  "details.select": "Select an order to inspect its items and payments.",
  "reprint.succeeded": "Reprint sent to the configured terminal printer.",
  "reprint.failed":
    "Receipt reprint could not be completed. Order details are unchanged.",
  "state.loading": "Loading local history…",
  "state.empty": "No local orders match these filters.",
  "state.failed": "Local history could not be loaded.",
  "state.unauthorized": "You do not have permission to view local history.",
  "state.unavailable": "Local history is not connected in this build.",
  "orderState.CompletedLocal": "Completed locally",
  "orderState.PendingSync": "Pending sync",
  "orderState.Syncing": "Syncing",
  "orderState.Synced": "Synced",
  "orderState.Blocked403": "Sync blocked",
  "orderState.Rejected": "Rejected",
  "method.cash": "Cash",
  "method.card": "Card",
  "method.voucher": "Voucher",
} as const;

export type LocalHistoryCopyKey = keyof typeof localHistoryEnglishCopy;

const localHistoryChineseCopy = {
  "title": "本机销售历史",
  "subtitle": "安全保存在此 iPad 上的订单，离线也可查看。",
  "action.back": "返回收银",
  "action.refresh": "刷新",
  "action.refreshing": "刷新中…",
  "action.apply": "应用筛选",
  "action.loadMore": "加载更多",
  "action.loadingMore": "加载中…",
  "action.reprint": "重打小票",
  "action.reprinting": "正在重打…",
  "filters.from": "开始日期",
  "filters.to": "结束日期",
  "filters.keyword": "订单或商品",
  "filters.keywordPlaceholder": "本机序号、订单号、条码或商品",
  "filters.invalidDate": "开始日期不能晚于结束日期。",
  "filters.quickToday": "今天",
  "filters.quickYesterday": "昨天",
  "filters.quickLast7Days": "近 7 天",
  "filters.invalidQuery": "无法应用当前历史筛选条件。",
  "list.title": "本机订单",
  "list.sequence": "本机 #{{sequence}}",
  "list.items": "{{count}} 行",
  "list.payment": "付款",
  "details.title": "订单详情",
  "receiptPreview.title": "小票预览",
  "receiptPreview.loading": "正在读取小票预览…",
  "receiptPreview.notFound": "该小票预览已不可用。",
  "receiptPreview.failed": "无法读取小票预览。",
  "receiptPreview.select": "请选择订单查看小票预览。",
  "details.lines": "商品",
  "details.payments": "付款",
  "details.quantity": "数量 {{quantity}}",
  "details.discount": "折扣 {{amount}}",
  "details.total": "合计",
  "details.actual": "实收",
  "details.notFound": "该本机订单已不可用。",
  "details.failed": "无法读取订单详情。",
  "details.select": "请选择订单查看商品和付款。",
  "reprint.succeeded": "已将重打请求发送到该终端已配置的打印机。",
  "reprint.failed": "小票重打未完成，订单详情未被修改。",
  "state.loading": "正在读取本机历史…",
  "state.empty": "没有符合筛选条件的本机订单。",
  "state.failed": "无法读取本机历史。",
  "state.unauthorized": "当前收银员无权查看本机历史。",
  "state.unavailable": "当前版本尚未接入本机历史。",
  "orderState.CompletedLocal": "本机已完成",
  "orderState.PendingSync": "待同步",
  "orderState.Syncing": "同步中",
  "orderState.Synced": "已同步",
  "orderState.Blocked403": "同步受阻",
  "orderState.Rejected": "已拒绝",
  "method.cash": "现金",
  "method.card": "银行卡",
  "method.voucher": "代金券",
} as const satisfies Record<LocalHistoryCopyKey, string>;

const copy = {
  en: localHistoryEnglishCopy,
  zh: localHistoryChineseCopy,
} as const;

export type LocalHistoryLocale = keyof typeof copy;

export function resolveLocalHistoryLocale(
  language?: string,
): LocalHistoryLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function localHistoryText(
  locale: LocalHistoryLocale,
  key: LocalHistoryCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = copy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/gu, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
