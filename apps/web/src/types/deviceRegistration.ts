import type { PosApiEntryMode } from './posApiEntry'

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
  /** 客户端程序版本（WPF 随心跳上报）；老版本客户端未上报时为空 */
  appVersion?: string | null
  /** 收银机自开机起的秒数（WPF 随心跳上报）；快速启动下只有重启才清零，老版本客户端未上报时为空 */
  systemUptimeSeconds?: number | null
  /** WPF 进程工作集（MB，随心跳上报） */
  processWorkingSetMb?: number | null
  /** WPF 进程句柄数（随心跳上报） */
  processHandleCount?: number | null
  /** RustDesk 客户端版本（远程维护登记时上报）；没有远程维护记录时为空，仅分页列表返回 */
  rustDeskClientVersion?: string | null
  /** 最近一次请求实际走的 POS API 入口（服务器按请求来源识别）；没有记录时为空 */
  lastApiEntry?: PosApiEntryMode | null
  /** lastApiEntry 的记录时间（入口变化或距上次超过 10 分钟才会刷新，不是「回退开始时间」） */
  lastApiEntryAt?: string | null
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
  /** 远程维护下发的 RustDesk 安装包版本，低于它的「远程版本」标橙；未启用远程维护时为空 */
  rustDeskLatestVersion?: string | null
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
