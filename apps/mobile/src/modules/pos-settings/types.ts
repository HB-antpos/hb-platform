// 收银端设置下发接口类型，与 BlazorApp.Shared/DTOs/PosSettingsDtos.cs 一一对应（JSON 为 camelCase）。
// 值的约定：values / overrides 字典里只出现「已设置」的项（true = 开，false = 关）；没出现 = 该层没设置。
// 保存时 patch 里值为 null 表示「清除这一项」，没出现的项保持不变。

export type PosSettingScope = "global" | "store" | "device";

/** 生效值的来源：三层之一，或 default（服务端缺省值）/ none（三层都没设置、收银机沿用本机值）。 */
export type PosSettingSource = "global" | "store" | "device" | "default" | "none";

/** 设备端类型：wpf / ipad / handheld；other 表示未知。 */
export type PosSettingClientKind = "wpf" | "ipad" | "handheld" | "other";

/** applied = 已应用最新；pending = 有新改动还没应用；never = 从未回执（旧版本不支持，或还没联过网）。 */
export type PosSettingDeviceStatusKind = "applied" | "pending" | "never";

export interface PosSettingDefinition {
  key: string;
  clientKinds: string[];
  /** 三层都没设置时的缺省值；null = 收银机沿用本机值。 */
  defaultWhenUnset: boolean | null;
  isNew: boolean;
}

export interface PosSettingLayer {
  scope: string;
  scopeKey: string;
  values: Record<string, boolean>;
  updatedAtUtc: string | null;
  updatedByName: string | null;
}

export interface PosSettingEffective {
  /** null = 三层都没设置，收银机沿用本机值。 */
  value: boolean | null;
  source: PosSettingSource;
}

export interface PosSettingDeviceStatus {
  deviceCode: string;
  storeCode: string;
  clientKind: PosSettingClientKind;
  appVersion: string | null;
  status: PosSettingDeviceStatusKind;
  appliedAtUtc: string | null;
  hasDeviceOverride: boolean;
  offlineOverrideCount: number;
  lastOfflineOverrideAtUtc: string | null;
}

export interface PosSettingStoreSummary {
  storeCode: string;
  storeName: string;
  overrides: Record<string, boolean>;
  deviceOverrideCount: number;
  deviceTotal: number;
  deviceApplied: number;
}

export interface PosSettingStoreDetail {
  storeCode: string;
  storeName: string;
  overrides: Record<string, boolean>;
  effective: Record<string, PosSettingEffective>;
  devices: PosSettingDeviceStatus[];
  updatedAtUtc: string | null;
  updatedByName: string | null;
}

export interface PosSettingDeviceDetail {
  status: PosSettingDeviceStatus;
  storeName: string;
  overrides: Record<string, boolean>;
  effective: Record<string, PosSettingEffective>;
  /** 设备最近一次回执上报的实际生效值；从未回执为空对象。 */
  reported: Record<string, boolean>;
}

/** 保存请求体：true / false = 设置；null = 清除（回到跟随上一层）。 */
export type PosSettingsPatch = Record<string, boolean | null>;
