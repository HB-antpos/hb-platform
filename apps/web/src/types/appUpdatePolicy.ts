export type AppUpdateApp = 'mobile-ios' | 'pos-ipad' | 'pos-handheld'
export type AppUpdateTargetScope = 'all' | 'stores'

export interface IosAppStoreRelease {
  id: string
  app: AppUpdateApp
  appStoreId: string
  bundleIdentifier: string
  version: string
  buildNumber: string
  storefront: string
  appStoreUrl: string
  appleVerifiedAtUtc: string
  createdAt: string
  createdBy: string | null
}

export interface IosAppStoreReleaseCreateRequest {
  app: AppUpdateApp
  appStoreId: string
  buildNumber: string
  storefront: string
}

export interface NativeUpdatePolicyRequest {
  expectedPolicyVersion: number
  enabled: boolean
  releaseId: string | null
  minimumSupportedVersion: string | null
  releaseMessage: string | null
}

export interface MobileIosNativeUpdatePolicyRequest extends NativeUpdatePolicyRequest {
  minimumSupportedBuildNumber: number | null
}

export interface PosIpadNativeUpdatePolicyRequest extends NativeUpdatePolicyRequest {
  minimumSupportedBuildNumber: number | null
  targetScope: AppUpdateTargetScope
  targetStoreGuids: string[]
}

export interface NativeUpdatePolicy {
  id: string | null
  enabled: boolean
  policyVersion: number
  releaseId: string | null
  latestVersion: string | null
  minimumSupportedVersion: string | null
  minimumSupportedBuildNumber: number | null
  appStoreUrl: string | null
  releaseMessage: string | null
  targetScope: AppUpdateTargetScope
  targetStoreGuids: string[]
  updatedAt: string | null
  updatedBy: string | null
}

export interface AppUpdateTargetStoreOption {
  storeGuid: string
  storeCode: string
  storeName: string
}

export interface PosIpadOtaRelease {
  id: string
  environment: string
  updateGroupId: string
  iosUpdateId: string
  channel: string
  runtimeVersion: string
  gitCommitHash: string | null
  dashboardUrl: string | null
  publishedAtUtc: string
  isRollback: boolean
  rollbackOfReleaseId: string | null
  createdAt: string
  createdBy: string | null
}

export interface PosIpadOtaRolloutRequest {
  expectedPolicyVersion: number
  enabled: boolean
  releaseId: string | null
  forceUpdate: boolean
  targetScope: AppUpdateTargetScope
  targetStoreGuids: string[]
  releaseMessage: string | null
}

export interface PosIpadOtaRollout {
  id: string | null
  enabled: boolean
  policyVersion: number
  releaseId: string | null
  forceUpdate: boolean
  targetScope: AppUpdateTargetScope
  targetStoreGuids: string[]
  releaseMessage: string | null
  release: PosIpadOtaRelease | null
  updatedAt: string | null
  updatedBy: string | null
}

// iPad OTA 覆盖层：在全局基线之上把某个分店 / 某台 iPad 单独指到另一个登记发布，优先级 设备 → 分店 → 基线。
export type PosIpadOtaAssignmentTargetType = 'store' | 'device'

export interface PosIpadOtaAssignment {
  id: string
  targetType: PosIpadOtaAssignmentTargetType
  storeGuid: string | null
  storeCode: string | null
  storeName: string | null
  deviceRegistrationId: number | null
  deviceNumber: string | null
  deviceRemarks: string | null
  deviceAppVersion: string | null
  enabled: boolean
  expiresAtUtc: string | null
  isExpired: boolean
  policyVersion: number
  releaseId: string
  release: PosIpadOtaRelease | null
  forceUpdate: boolean
  releaseMessage: string | null
  note: string | null
  updatedAt: string | null
  updatedBy: string | null
}

/** 创建覆盖：目标类型与目标必填；修改覆盖：目标不可变，只带版本与投放字段。 */
export interface PosIpadOtaAssignmentCreateRequest {
  targetType: PosIpadOtaAssignmentTargetType
  storeGuid: string | null
  deviceRegistrationId: number | null
  releaseId: string
  forceUpdate: boolean
  releaseMessage: string | null
  note: string | null
  expiresAtUtc: string | null
  enabled: boolean
}

export interface PosIpadOtaAssignmentUpdateRequest {
  expectedPolicyVersion: number
  releaseId: string
  forceUpdate: boolean
  releaseMessage: string | null
  note: string | null
  expiresAtUtc: string | null
  enabled: boolean
}

export interface PosIpadOtaTargetDeviceOption {
  deviceRegistrationId: number
  systemDeviceNumber: string
  storeCode: string | null
  storeName: string | null
  remarks: string | null
  appVersion: string | null
  isOnline: boolean
  lastHeartbeatAt: string | null
}

export interface PosIpadOtaTargetDeviceQuery {
  storeGuid?: string | null
  keyword?: string | null
  page?: number
  pageSize?: number
}

export interface PosIpadOtaTargetDevicePage {
  items: PosIpadOtaTargetDeviceOption[]
  total: number
  page: number
  pageSize: number
}

// Mobile 安卓原生「最低支持构建号」策略：只按 versionCode 判定，不登记发布事实，
// 目标包固定为当前公开的 production 安卓包（latestBuild）。
export interface MobileAndroidLatestBuild {
  easBuildId: string
  appVersion: string
  appBuildVersion: number
  completedAt: string | null
}

export interface MobileAndroidNativeUpdatePolicy {
  enabled: boolean
  minimumSupportedBuildNumber: number | null
  releaseMessage: string | null
  policyVersion: number
  updatedAt: string | null
  updatedBy: string | null
  latestBuild: MobileAndroidLatestBuild | null
}

export interface MobileAndroidNativeUpdatePolicyRequest {
  expectedPolicyVersion: number
  enabled: boolean
  minimumSupportedBuildNumber: number | null
  releaseMessage: string | null
}
