// 收银端设置子页面的路径与参数。纯字符串拼装，不依赖 expo-router。
export const POS_SETTINGS_ROUTES = {
  home: "/pos-settings",
  store: "/pos-settings/store",
  device: "/pos-settings/device",
} as const;

export function buildPosSettingsStoreHref(storeCode: string): string {
  return `${POS_SETTINGS_ROUTES.store}?storeCode=${encodeURIComponent(storeCode)}`;
}

export function buildPosSettingsDeviceHref(deviceCode: string): string {
  return `${POS_SETTINGS_ROUTES.device}?deviceCode=${encodeURIComponent(deviceCode)}`;
}

/** expo-router 的搜索参数可能是 string | string[] | undefined，统一取第一个非空值。 */
export function firstParam(value: string | string[] | undefined): string {
  const candidate = Array.isArray(value) ? value[0] : value;
  return typeof candidate === "string" ? candidate : "";
}
