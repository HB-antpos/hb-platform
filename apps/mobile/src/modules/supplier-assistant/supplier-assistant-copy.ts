/**
 * 员工移动端「订货助手」页面文案（中英文）。
 *
 * 中文注释：与大屏收银 App 的 supplier-assistant-copy.ts 同一套措辞；
 * 移动端多了分店选择、相机扫码、电脑版网页切换和「需要更新 App」几条。
 * 工作台入口名称在 locales/*\/common.json（tabs）与 screens/workbench.json（routes）。
 */
export const supplierAssistantEnglishCopy = {
  "title": "Ordering assistant",
  "subtitle": "Product cards on the supplier site show this store's last order and sales.",
  "action.back": "Back",
  "action.reload": "Reload",
  "action.home": "Supplier home",
  "action.retry": "Try again",
  "action.closePanel": "Close",
  "action.scan": "Scan",
  "action.pickStore": "Choose store",
  "action.enableCamera": "Allow camera",
  "action.desktopSite": "Desktop site",
  "store.pickerTitle": "Which store are you ordering for?",
  "store.cancel": "Cancel",
  "state.loading": "Loading suppliers…",
  "state.empty": "No supplier sites are enabled yet. Ask head office to turn one on.",
  "state.failed": "Could not connect the ordering assistant. Check the network and try again.",
  "state.notAllowed": "Your account cannot use the ordering assistant. Ask your manager for ordering access.",
  "state.noStores": "No stores are available for your account.",
  "state.pickStore": "Choose the store you are ordering for. Product cards will show that store's orders and sales.",
  "state.unsupported": "This supplier site cannot be opened in the app yet.",
  "state.updateRequired": "Update the app to the latest version to use the ordering assistant.",
  "scan.title": "Scan a product barcode",
  "scan.noSearch": "This supplier site does not support barcode search yet.",
  "panel.title": "Purchase history",
  "panel.loading": "Loading purchase history…",
  "panel.failed": "Could not load purchase history.",
  "panel.noMatch": "This item number is not linked to a product in our system.",
  "panel.item": "Item {{itemNumber}} · Store {{storeCode}}",
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
  "title": "订货助手",
  "subtitle": "供应商网站的商品卡片上会显示本店上次订货和销量。",
  "action.back": "返回",
  "action.reload": "刷新",
  "action.home": "供应商首页",
  "action.retry": "重试",
  "action.closePanel": "关闭",
  "action.scan": "扫码",
  "action.pickStore": "选择分店",
  "action.enableCamera": "允许使用相机",
  "action.desktopSite": "电脑版网页",
  "store.pickerTitle": "为哪家分店订货？",
  "store.cancel": "取消",
  "state.loading": "正在加载供应商…",
  "state.empty": "还没有开通任何供应商网站，请联系总部开通。",
  "state.failed": "订货助手连接失败，请检查网络后重试。",
  "state.notAllowed": "你的账号不能使用订货助手，请联系店长开通订货权限。",
  "state.noStores": "你的账号没有可用的分店。",
  "state.pickStore": "请先选择要订货的分店，商品卡片上会显示这家分店的订货和销量。",
  "state.unsupported": "这个供应商网站暂时不能在 App 里打开。",
  "state.updateRequired": "请把 App 更新到最新版本后再使用订货助手。",
  "scan.title": "扫商品条码",
  "scan.noSearch": "这个供应商网站暂不支持扫码查找。",
  "panel.title": "采购记录",
  "panel.loading": "正在加载采购记录…",
  "panel.failed": "采购记录加载失败。",
  "panel.noMatch": "这个货号在系统里没有对应的商品。",
  "panel.item": "货号 {{itemNumber}} · 分店 {{storeCode}}",
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
