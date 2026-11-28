/**
 * 订货模式（不注册设备、员工账号登录）的固定配置。
 *
 * 中文注释：
 * 1. 网站与接口同在 hotbargain.vip（与浏览器订货插件的生产配置一致），网页会话 Cookie 也落在这个域。
 * 2. 订货模式只开放「仓库订货（/shop）」与「供应商订货助手」两个页签，其余网站页面一律拦回 /shop。
 */
export const ORDERING_WEB_ORIGIN = "https://hotbargain.vip";

/** 仓库订货页（订货前台）入口。 */
export const ORDERING_SHOP_PATH = "/shop";

/** 告诉网页「当前嵌在大屏收银 App 的订货模式里」：网页据此隐藏网站自己的退出、后台入口。 */
export const ORDERING_EMBED_MODE = "pos-ipad";

/** 网页里收 App 扫码结果的事件名（与 apps/web 的 posAppEmbed.ts 保持一致）。 */
export const ORDERING_SCAN_EVENT = "hb-pos-app:scan";

/** 闲置多久自动退出（员工在两个网页里没有任何点按 / 输入）。 */
export const ORDERING_IDLE_TIMEOUT_MS = 15 * 60_000;

/** 浏览器订货插件的 PKCE 客户端编号；后端 BrowserExtensionSessionGrantService.ClientId。 */
export const ORDERING_EXTENSION_CLIENT_ID = "hb-supplier-order";

/** 订货模式可停留的网站路径：仓库订货页及其子页。 */
export function isOrderingShopUrl(url: string | null | undefined): boolean {
  if (!url) return false;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  if (parsed.origin !== ORDERING_WEB_ORIGIN) return false;
  return parsed.pathname === ORDERING_SHOP_PATH || parsed.pathname.startsWith(`${ORDERING_SHOP_PATH}/`);
}

/** 网站跳到登录页：说明网页会话已失效（被踢下线、过期或在别处退出）。 */
export function isOrderingLoginUrl(url: string | null | undefined): boolean {
  if (!url) return false;
  try {
    const parsed = new URL(url);
    return parsed.origin === ORDERING_WEB_ORIGIN && parsed.pathname === "/login";
  } catch {
    return false;
  }
}
