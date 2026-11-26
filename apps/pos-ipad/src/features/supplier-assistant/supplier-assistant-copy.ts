export const supplierAssistantEnglishCopy = {
  "eyebrow": "ORDERING ASSISTANT",
  "title": "Ordering assistant",
  "subtitle": "Open a supplier site. Product cards show this store's last order and sales.",
  "action.back": "Back",
  "action.reload": "Reload",
  "action.home": "Supplier home",
  "action.retry": "Try again",
  "action.closePanel": "Close",
  "store.label": "Store {{storeCode}}",
  "store.loading": "Connecting…",
  "state.loading": "Loading suppliers…",
  "state.empty": "No supplier sites are enabled yet. Ask head office to turn one on.",
  "state.tokenFailed": "Could not connect the ordering assistant. Check the network and try again.",
  "state.notEligible": "This device is not enabled for the ordering assistant.",
  "state.unsupported": "This supplier site cannot be opened in the app yet.",
  "scan.hint": "Scan a barcode to find the product on this supplier site.",
  "panel.title": "Purchase history",
  "panel.loading": "Loading purchase history…",
  "panel.failed": "Could not load purchase history.",
  "panel.noMatch": "This item number is not linked to a product in our system.",
  "panel.item": "Item {{itemNumber}}",
  "panel.latestPurchase": "Last order",
  "panel.salesSince": "Sold since",
  "panel.lastYear30": "Same 30 days last year",
  "panel.lastYear60": "Same 60 days last year",
  "panel.cycles": "Last purchase cycles",
  "panel.noCycles": "No orders in the last 12 months.",
  "panel.cycleOrdered": "Ordered {{date}} × {{quantity}}",
  "panel.cycleSold": "Sold {{quantity}} by {{date}}",
  "panel.none": "—",
} as const;

export type SupplierAssistantCopyKey = keyof typeof supplierAssistantEnglishCopy;

export const supplierAssistantChineseCopy = {
  "eyebrow": "订货助手",
  "title": "订货助手",
  "subtitle": "打开供应商网站，商品卡片上会显示本店上次订货和销量。",
  "action.back": "返回",
  "action.reload": "刷新",
  "action.home": "回到供应商首页",
  "action.retry": "重试",
  "action.closePanel": "关闭",
  "store.label": "分店 {{storeCode}}",
  "store.loading": "正在连接…",
  "state.loading": "正在加载供应商…",
  "state.empty": "还没有开通任何供应商网站，请联系总部开通。",
  "state.tokenFailed": "订货助手连接失败，请检查网络后重试。",
  "state.notEligible": "这台设备没有开通订货助手。",
  "state.unsupported": "这个供应商网站暂时不能在 App 里打开。",
  "scan.hint": "扫商品条码，可在当前供应商网站上找到这件商品。",
  "panel.title": "采购记录",
  "panel.loading": "正在加载采购记录…",
  "panel.failed": "采购记录加载失败。",
  "panel.noMatch": "这个货号在系统里没有对应的商品。",
  "panel.item": "货号 {{itemNumber}}",
  "panel.latestPurchase": "上次订货",
  "panel.salesSince": "至今销量",
  "panel.lastYear30": "去年同期 30 天",
  "panel.lastYear60": "去年同期 60 天",
  "panel.cycles": "最近几次采购周期",
  "panel.noCycles": "近 12 个月没有订货。",
  "panel.cycleOrdered": "{{date}} 订货 × {{quantity}}",
  "panel.cycleSold": "到 {{date}} 卖出 {{quantity}}",
  "panel.none": "—",
} as const satisfies Record<SupplierAssistantCopyKey, string>;

const supplierAssistantCopy = {
  en: supplierAssistantEnglishCopy,
  zh: supplierAssistantChineseCopy,
} as const;

export type SupplierAssistantLocale = keyof typeof supplierAssistantCopy;

export function resolveSupplierAssistantLocale(language?: string): SupplierAssistantLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function supplierAssistantText(
  locale: SupplierAssistantLocale,
  key: SupplierAssistantCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = supplierAssistantCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
