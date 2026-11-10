import type { SpecialProductsStatusCode } from "./special-products-presenter";

export const specialProductsEnglishCopy = {
  "header.eyebrow": "Store operations",
  "header.title": "Special products",
  "header.subtitle": "Browse and add cached items offline; management actions require online access.",
  "action.back": "Back",
  "action.close": "Close",
  "action.refreshLocal": "Refresh local",
  "action.download": "Download",
  "action.working": "Working…",
  "offlineNote": "Offline: local browsing and cart access remain available; management writes are locked.",
  "catalog.title": "Local special products",
  "catalog.itemCount": "{{count}} items",
  "catalog.itemCountOne": "{{count}} item",
  "catalog.tapHint": "Tap a card to add it to the cart and return to sales",
  "catalog.unauthorized": "View permission required",
  "catalog.failed": "Local list unavailable",
  "catalog.empty": "No special products",
  "management.title": "Add product",
  "management.hint": "Candidates come from the local catalogue; marking still requires online access.",
  "management.searchLabel": "Search local products",
  "management.searchPlaceholder": "Name, barcode or product code",
  "management.searching": "Searching…",
  "management.search": "Search local",
  "management.empty": "Enter a query to find products to mark.",
  "management.mark": "Add",
  "organize.start": "Organize",
  "organize.done": "Done",
  "organize.banner": "Organize mode: use ↑ ↓ or long-press and drag to reorder; Remove takes a product out of special products. Tapping a card will not add it to the cart.",
  "unavailable.eyebrow": "Special products",
  "unavailable.title": "Feature unavailable",
  "unavailable.hint": "The local runtime has not provided the special products service. Return to sales.",
  "unavailable.back": "Back to sales",
  "row.add": "Add",
  "row.itemNumber": "Item no.: {{itemNumber}}",
  "row.remove": "Remove",
  "status.added-to-cart": "Added to cart",
  "status.add-to-cart-failed": "Could not add to cart",
  "status.download-complete": "Download complete",
  "status.download-failed": "Download did not complete",
  "status.load-failed": "Local list could not be read",
  "status.mark-complete": "Special product updated",
  "status.mark-failed": "Update did not complete",
  "status.online-required": "This management action requires online access",
  "status.permission-required": "Required permission is missing",
  "status.reorder-complete": "Local order saved",
  "status.reorder-failed": "Order was not saved",
  "status.search-failed": "Local candidate search failed",
} as const;

export type SpecialProductsCopyKey = keyof typeof specialProductsEnglishCopy;

const specialProductsChineseCopy = {
  "header.eyebrow": "门店运营",
  "header.title": "特殊商品",
  "header.subtitle": "本地列表可离线浏览与加购；管理操作需要在线。",
  "action.back": "返回",
  "action.close": "关闭",
  "action.refreshLocal": "刷新本地",
  "action.download": "下载更新",
  "action.working": "处理中…",
  "offlineNote": "离线模式：本地浏览与加购可用，管理写操作已锁定。",
  "catalog.title": "本地特殊商品",
  "catalog.itemCount": "{{count}} 项",
  "catalog.itemCountOne": "{{count}} 项",
  "catalog.tapHint": "点商品卡片 = 加入购物车并返回收银",
  "catalog.unauthorized": "没有查看权限",
  "catalog.failed": "本地列表读取失败",
  "catalog.empty": "暂无特殊商品",
  "management.title": "添加商品",
  "management.hint": "候选来自本地目录；真正标记时仍需在线。",
  "management.searchLabel": "搜索本地商品",
  "management.searchPlaceholder": "名称、条码或商品码",
  "management.searching": "搜索中…",
  "management.search": "搜索本地目录",
  "management.empty": "输入关键词查找可标记商品。",
  "management.mark": "标记",
  "organize.start": "整理",
  "organize.done": "完成整理",
  "organize.banner": "整理模式：用 ↑ ↓ 或长按拖动调整顺序，「取消标记」把商品移出特殊商品。此时点卡片不会加购。",
  "unavailable.eyebrow": "特殊商品",
  "unavailable.title": "功能暂不可用",
  "unavailable.hint": "本机运行时尚未提供特殊商品服务，请返回销售页。",
  "unavailable.back": "返回销售页",
  "row.add": "加购",
  "row.itemNumber": "货号：{{itemNumber}}",
  "row.remove": "取消标记",
  "status.added-to-cart": "已加入购物车",
  "status.add-to-cart-failed": "加购未完成",
  "status.download-complete": "下载完成",
  "status.download-failed": "下载未完成",
  "status.load-failed": "本地列表读取失败",
  "status.mark-complete": "特殊商品标记已更新",
  "status.mark-failed": "标记未完成",
  "status.online-required": "此管理操作需要在线",
  "status.permission-required": "当前收银员没有所需权限",
  "status.reorder-complete": "本地顺序已保存",
  "status.reorder-failed": "排序未保存",
  "status.search-failed": "本地候选搜索失败",
} as const satisfies Record<SpecialProductsCopyKey, string>;

const specialProductsCopy = {
  en: specialProductsEnglishCopy,
  zh: specialProductsChineseCopy,
} as const;

export type SpecialProductsLocale = keyof typeof specialProductsCopy;

export function resolveSpecialProductsLocale(language?: string): SpecialProductsLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function specialProductsText(
  locale: SpecialProductsLocale,
  key: SpecialProductsCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = specialProductsCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}

export function specialProductsStatusCopyKey(
  statusCode: SpecialProductsStatusCode,
): SpecialProductsCopyKey {
  return `status.${statusCode}`;
}
