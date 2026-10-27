export const dailyCloseEnglishCopy = {
  eyebrow: "STORE OPERATIONS",
  title: "Daily close",
  subtitle:
    "Local store, terminal and business-day totals; every save creates a separate archive.",
  "action.back": "Back",
  "action.count": "Count",
  "action.history": "History",
  "action.review": "Review summary",
  "action.backToCount": "Back to cash count",
  "action.refresh": "Refresh",
  "action.refreshing": "Loading…",
  "action.save": "Save & print",
  "action.reprint": "Reprint selected",
  "action.working": "Working…",
  "summary.title": "Business-day summary",
  "summary.hint":
    "The time window is a half-open interval [from, to) from local midnight in the store time zone.",
  "businessDate.accessibility": "Business date",
  "tender.method": "Method",
  "tender.values": "Sales / Refunds / Net",
  "metric.orders": "Orders",
  "metric.returnQuantity": "Return qty",
  "metric.cashRounding": "Cash rounding",
  "metric.expectedCash": "Expected cash",
  "summary.loading": "Loading local summary…",
  "summary.empty": "Select a business date and refresh",
  "count.title": "Cash count",
  "count.hint":
    "Enter non-negative whole-number quantities. The numeric keyboard opens automatically for counting; the focused field stays visible above it.",
  "count.notes": "Notes {{amount}}",
  "count.coins": "Coins {{amount}}",
  "count.countedCash": "Counted cash",
  "count.variance": "Variance {{amount}}",
  "permission.viewOnly": "View-only access: counting and saving are hidden.",
  "history.title": "Saved archives",
  "history.hint":
    "Multiple archives can be retained for one business date; reprinting always uses the selected frozen record.",
  "history.empty": "No saved archives",
  "archive.selected": "Selected archive",
  "archive.terminal": "Terminal: {{value}}",
  "archive.cashier": "Cashier: {{value}}",
  "archive.ordersReturns": "Orders: {{orders}} · Returns: {{returns}}",
  "archive.rounding": "Rounding {{amount}}",
  "archive.expected": "Expected {{amount}}",
  "archive.counted": "Counted {{amount}}",
  "denomination.accessibility": "{{denomination}} count",
  "method.cash": "Cash",
  "method.card": "Card",
  "method.voucher": "Voucher",
  "unavailable.title": "Daily close unavailable",
  "unavailable.subtitle":
    "The local archive or printing service is not configured.",
  "unavailable.back": "Back to sales",
  "status.invalid-business-date": "Invalid business date or store time zone.",
  "status.load-failed":
    "Local daily-close summary could not be loaded. Try again.",
  "status.permission-required": "Permission required.",
  "status.reprint-failed": "Reprint failed; the frozen archive is unchanged.",
  "status.reprint-printed": "Selected archive sent to print.",
  "status.save-failed": "Save failed; counts were preserved.",
  "status.saved-print-failed":
    "Saved safely; printing failed and can be retried from history.",
  "status.saved-printed": "Saved and sent to print.",
  "status.select-archive-required": "Select an archive first.",
} as const;

export type DailyCloseCopyKey = keyof typeof dailyCloseEnglishCopy;

const dailyCloseChineseCopy = {
  eyebrow: "门店运营",
  title: "日结",
  subtitle: "本机、本门店、本营业日汇总；每次保存形成独立冻结归档。",
  "action.back": "返回",
  "action.count": "点钞",
  "action.history": "历史",
  "action.review": "核对汇总",
  "action.backToCount": "返回现金点算",
  "action.refresh": "刷新",
  "action.refreshing": "刷新中…",
  "action.save": "保存并打印",
  "action.reprint": "补打所选归档",
  "action.working": "处理中…",
  "summary.title": "当日汇总",
  "summary.hint": "时间窗为门店本地午夜起止的半开区间 [from, to)。",
  "businessDate.accessibility": "营业日",
  "tender.method": "方式",
  "tender.values": "销售 / 退款 / 净额",
  "metric.orders": "订单",
  "metric.returnQuantity": "退货数量",
  "metric.cashRounding": "现金进位",
  "metric.expectedCash": "应有现金",
  "summary.loading": "正在读取本地汇总…",
  "summary.empty": "请选择营业日并刷新",
  "count.title": "现金点算",
  "count.hint":
    "只接受非负整数张数；金额始终以整数分币计算。进入点钞或点按任一面额会自动打开系统数字键盘；当前输入会滚至键盘上方。",
  "count.notes": "纸币 {{amount}}",
  "count.coins": "硬币 {{amount}}",
  "count.countedCash": "实点现金",
  "count.variance": "差额 {{amount}}",
  "permission.viewOnly": "当前权限仅允许查看；点钞和保存已隐藏。",
  "history.title": "冻结归档",
  "history.hint": "同一营业日可保留多次归档；补打始终使用所选冻结事实。",
  "history.empty": "暂无归档",
  "archive.selected": "所选归档",
  "archive.terminal": "终端：{{value}}",
  "archive.cashier": "收银员：{{value}}",
  "archive.ordersReturns": "订单：{{orders}} · 退货：{{returns}}",
  "archive.rounding": "进位 {{amount}}",
  "archive.expected": "应有 {{amount}}",
  "archive.counted": "实点 {{amount}}",
  "denomination.accessibility": "{{denomination}} 张数",
  "method.cash": "现金",
  "method.card": "银行卡",
  "method.voucher": "代金券",
  "unavailable.title": "日结暂不可用",
  "unavailable.subtitle": "本地归档或打印服务尚未接线，请返回销售页。",
  "unavailable.back": "返回销售",
  "status.invalid-business-date": "营业日或门店时区无效。",
  "status.load-failed": "本地日结汇总读取失败，请重试。",
  "status.permission-required": "当前收银员没有执行此操作的权限。",
  "status.reprint-failed": "补打失败；冻结归档未改变。",
  "status.reprint-printed": "所选归档已发送打印。",
  "status.save-failed": "归档未保存，点钞数量已保留。",
  "status.saved-print-failed": "归档与审计已保存，但打印失败，可从历史补打。",
  "status.saved-printed": "归档与审计已保存并发送打印。",
  "status.select-archive-required": "请先选择一个归档。",
} as const satisfies Record<DailyCloseCopyKey, string>;

const dailyCloseCopy = {
  en: dailyCloseEnglishCopy,
  zh: dailyCloseChineseCopy,
} as const;

export type DailyCloseLocale = keyof typeof dailyCloseCopy;

export function resolveDailyCloseLocale(language?: string): DailyCloseLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function dailyCloseText(
  locale: DailyCloseLocale,
  key: DailyCloseCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = dailyCloseCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
