// 收银端设置下发的接口数据结构，与后端 BlazorApp.Shared/DTOs/PosSettingsDtos.cs 一一对应（JSON camelCase）。
// Values 字典里只出现「已设置」的项（true = 开，false = 关）；没出现 = 该层没设置（跟随上一层）。

/** 适用端：wpf / ipad / handheld（设备端另有 other）。 */
export type PosClientKind = 'wpf' | 'ipad' | 'handheld' | 'other'

export type PosSettingScope = 'global' | 'store' | 'device'

/** 生效值来源：default = 服务端缺省（仅新功能）；none = 三层都没设置，收银机沿用本机值。 */
export type PosSettingSource = 'global' | 'store' | 'device' | 'default' | 'none'

/** 设备应用状态：applied 已应用最新；pending 待同步；never 从未回执。 */
export type PosDeviceStatusCode = 'applied' | 'pending' | 'never'

export interface PosSettingDefinition {
  key: string
  clientKinds: string[]
  /** 三层都没设置时的缺省值；null = 收银机沿用本机值。 */
  defaultWhenUnset: boolean | null
  isNew: boolean
}

export interface PosSettingsCatalog {
  definitions: PosSettingDefinition[]
}

export interface PosSettingLayer {
  scope: string
  scopeKey: string
  values: Record<string, boolean>
  updatedAtUtc: string | null
  updatedByName: string | null
}

/** 保存请求：true / false = 设置；null = 清除（回到跟随上一层）；没出现的项保持不变。 */
export interface SavePosSettingsRequest {
  values: Record<string, boolean | null>
}

export interface PosSettingEffectiveValue {
  value: boolean | null
  source: PosSettingSource | string
}

export interface PosSettingDeviceStatus {
  deviceCode: string
  storeCode: string
  clientKind: PosClientKind | string
  appVersion: string | null
  status: PosDeviceStatusCode | string
  appliedAtUtc: string | null
  hasDeviceOverride: boolean
  offlineOverrideCount: number
  lastOfflineOverrideAtUtc: string | null
}

export interface PosSettingStoreSummary {
  storeCode: string
  storeName: string
  overrides: Record<string, boolean>
  deviceOverrideCount: number
  deviceTotal: number
  deviceApplied: number
}

export interface PosSettingStoreDetail {
  storeCode: string
  storeName: string
  overrides: Record<string, boolean>
  effective: Record<string, PosSettingEffectiveValue>
  devices: PosSettingDeviceStatus[]
  updatedAtUtc: string | null
  updatedByName: string | null
}

export interface PosSettingDeviceDetail {
  status: PosSettingDeviceStatus
  storeName: string
  overrides: Record<string, boolean>
  effective: Record<string, PosSettingEffectiveValue>
  /** 设备最近一次回执上报的实际生效值；从未回执为空对象。 */
  reported: Record<string, boolean>
}

export interface PosSettingChange {
  id: number
  scope: PosSettingScope | string
  scopeKey: string
  settingKey: string
  /** null = 修改前没设置。 */
  oldValue: boolean | null
  /** null = 被清除。 */
  newValue: boolean | null
  changedAtUtc: string
  changedByName: string | null
}

export interface PosSettingChangeQuery {
  scope?: string
  scopeKey?: string
  limit?: number
}
