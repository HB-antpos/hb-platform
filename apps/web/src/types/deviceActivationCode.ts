import type { StoreOption } from './deviceRegistration'

/** AndroidTablet：安卓平板上的大屏收银 App（与 iPadOS 同一个 App），开通码与 iPadOS 分开签发。 */
export type DeviceActivationSystem = 'Windows' | 'iPadOS' | 'AndroidTablet' | 'Android' | 'iOS'
export type DeviceActivationStatus = 'Available' | 'Expired' | 'Revoked' | 'Consumed'
export type DeviceActivationConsumptionKind = 'Initial' | 'Rebind'

export interface DeviceActivationCodeSummary {
  grantId: string
  storeCode: string
  storeName?: string | null
  deviceSystem: DeviceActivationSystem
  status: DeviceActivationStatus
  createdAtUtc: string
  createdBy: string
  reason: string
  expiresAtUtc: string
  revokedAtUtc?: string | null
  revokedBy?: string | null
  revokeReason?: string | null
  consumedAtUtc?: string | null
  consumedHardwareId?: string | null
  consumedDeviceCode?: string | null
  consumptionKind?: DeviceActivationConsumptionKind | null
  previousStoreCode?: string | null
  previousDeviceCode?: string | null
  targetUserGuid?: string | null
  targetUsername?: string | null
  targetFullName?: string | null
}

export interface DeviceActivationCodePagedResult {
  items: DeviceActivationCodeSummary[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export interface DeviceActivationCodeCreatePayload {
  storeCode: string
  deviceSystem: DeviceActivationSystem
  validForMinutes: 30 | 120 | 1440
  reason: string
}

export interface DeviceActivationCodeCreateResponse {
  grant: DeviceActivationCodeSummary
  activationCode: string
}

export interface MobileDeviceActivationCodeCreatePayload {
  storeCode: string
  deviceSystem: Extract<DeviceActivationSystem, 'Android' | 'iOS'>
  targetUserGuid: string
  validForMinutes: 30 | 120 | 1440
  reason: string
}

export interface MobileDeviceActivationManageableAccount {
  userGuid: string
  username: string
  fullName?: string | null
}

export type DeviceActivationManageableStore = StoreOption
