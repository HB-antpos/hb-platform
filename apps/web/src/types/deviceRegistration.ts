export interface DeviceRegistrationItem {
  id: number
  hardwareId: string
  systemDeviceNumber: string
  storeCode?: string | null
  storeName?: string | null
  deviceType: string
  deviceSystem: string
  status: number
  statusDescription: string
  allowTransactions: boolean
  remark?: string | null
  createdAt?: string
  lastModified?: string | null
  createdBy?: string | null
  lastModifiedBy?: string | null
  isOnline: boolean
  lastHeartbeatAt?: string | null
  currentCashierId?: string | null
  currentCashierName?: string | null
  cashierLoginAt?: string | null
}

export interface DeviceRegistrationDetail extends DeviceRegistrationItem {}

export interface UpdateDeviceRegistrationPayload {
  deviceType: string
  deviceSystem: string
  allowTransactions: boolean
  remark?: string | null
}

export interface UpdateDeviceRegistrationApiPayload {
  设备类型: string
  设备系统: string
  是否允许交易: boolean
  备注?: string | null
}

export interface DeviceRegistrationPagedResult {
  devices: DeviceRegistrationItem[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export type AppDeviceOnlineState = 'all' | 'online' | 'offline'

export interface AppDeviceStatus {
  id: string
  hardwareId: string
  systemDeviceNumber?: string
  deviceSystem?: string
  platform?: string
  storeCode?: string
  appVersion?: string
  appBuildVersion?: string
  runtimeVersion?: string
  channel?: string
  updateId?: string
  updateSource?: string
  lastSeenAtUtc?: string
  isOnline: boolean
  lastAuthMode?: string
  lastSeenUserGuid?: string
  lastSeenUsername?: string
  lastSeenUserFullName?: string
  registeredDeviceId?: number
}

export interface AppDeviceStatusSummary {
  total: number
  online: number
  offline: number
  android: number
  ios: number
  unknownSystem: number
}

/** 版本分布的一行：同一系统、同一原生版本号 + 构建号。 */
export interface AppVersionDistributionItem {
  deviceSystem?: string
  appVersion?: string
  appBuildVersion?: string
  total: number
  online: number
  /** 当前跑 OTA 热更新包的设备数 */
  ota: number
  /** 当前跑安装包内置 bundle 的设备数 */
  embedded: number
}

export interface AppVersionDistribution {
  total: number
  items: AppVersionDistributionItem[]
}

export interface AppDeviceStatusPagedResult {
  devices: AppDeviceStatus[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface StoreOption {
  storeCode: string
  storeName: string
}

export interface EmergencyLoginGrantSummary {
  grantId: string
  storeCode: string
  businessDate: string
  keyId: string
  permissionProfile: 'AllPosTerminal'
  issuedBy: string
  reason: string
  issuedAtUtc: string
  expiresAtUtc: string
  revokedBy?: string | null
  revokedAtUtc?: string | null
  revokeReason?: string | null
  status: 'Active' | 'Expired' | 'Revoked'
}

export interface EmergencyLoginGrantCreateResponse {
  grant: EmergencyLoginGrantSummary
  token: string
}
