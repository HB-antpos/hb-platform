// react-query 键：所有收银端设置数据都挂在 ["pos-settings"] 下，保存成功后整体失效。
export const posSettingsKeys = {
  all: ["pos-settings"] as const,
  catalog: () => ["pos-settings", "catalog"] as const,
  global: () => ["pos-settings", "global"] as const,
  stores: () => ["pos-settings", "stores"] as const,
  store: (storeCode: string) => ["pos-settings", "store", storeCode] as const,
  devices: (storeCode: string) => ["pos-settings", "devices", storeCode] as const,
  device: (deviceCode: string) => ["pos-settings", "device", deviceCode] as const,
};
