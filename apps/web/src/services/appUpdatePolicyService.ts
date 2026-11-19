import type { ApiResponse } from '../types/api'
import type {
  AppUpdateApp,
  AppUpdateTargetScope,
  AppUpdateTargetStoreOption,
  IosAppStoreRelease,
  IosAppStoreReleaseCreateRequest,
  MobileAndroidLatestBuild,
  MobileAndroidNativeUpdatePolicy,
  MobileAndroidNativeUpdatePolicyRequest,
  MobileIosNativeUpdatePolicyRequest,
  NativeUpdatePolicy,
  PosIpadNativeUpdatePolicyRequest,
  PosIpadOtaAssignment,
  PosIpadOtaAssignmentCreateRequest,
  PosIpadOtaAssignmentTargetType,
  PosIpadOtaAssignmentUpdateRequest,
  PosIpadOtaRelease,
  PosIpadOtaRollout,
  PosIpadOtaRolloutRequest,
  PosIpadOtaTargetDeviceOption,
  PosIpadOtaTargetDevicePage,
  PosIpadOtaTargetDeviceQuery,
} from '../types/appUpdatePolicy'
import request, { unwrapApiData } from '../utils/request'

type TransportOptions = {
  params?: Record<string, unknown>
  signal?: AbortSignal
}

export interface AppUpdatePolicyTransport {
  get(url: string, options?: TransportOptions): Promise<unknown>
  post(url: string, payload?: unknown, options?: TransportOptions): Promise<unknown>
  put(url: string, payload?: unknown, options?: TransportOptions): Promise<unknown>
  delete(url: string, options?: TransportOptions): Promise<unknown>
}

const defaultTransport: AppUpdatePolicyTransport = {
  get: (url, options) => request.get(url, options),
  post: (url, payload, options) => request.post(url, payload, options),
  put: (url, payload, options) => request.put(url, payload, options),
  delete: (url, options) => request.delete(url, options),
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function text(raw: Record<string, unknown>, key: string) {
  const value = raw[key]
  return typeof value === 'string' ? value : ''
}

function nullableText(raw: Record<string, unknown>, key: string) {
  const value = text(raw, key).trim()
  return value || null
}

function boolean(raw: Record<string, unknown>, key: string) {
  return raw[key] === true
}

function number(raw: Record<string, unknown>, key: string) {
  const value = Number(raw[key])
  return Number.isFinite(value) ? value : 0
}

function nullableInt32(raw: Record<string, unknown>, key: string) {
  const value = raw[key]
  if (value === null || value === undefined || value === '') {
    return null
  }

  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= 2_147_483_647
    ? parsed
    : null
}

function targetScope(raw: Record<string, unknown>): AppUpdateTargetScope {
  return raw.targetScope === 'stores' ? 'stores' : 'all'
}

function stringArray(raw: Record<string, unknown>, key: string) {
  return Array.isArray(raw[key])
    ? (raw[key] as unknown[]).filter((value): value is string => typeof value === 'string')
    : []
}

function unwrap<T>(payload: unknown) {
  return unwrapApiData(payload as ApiResponse<T> | T)
}

export function normalizeIosAppStoreRelease(value: unknown): IosAppStoreRelease {
  const raw = asRecord(value)
  return {
    id: text(raw, 'id'),
    app: raw.app === 'pos-ipad'
      ? 'pos-ipad'
      : raw.app === 'pos-handheld'
        ? 'pos-handheld'
        : 'mobile-ios',
    appStoreId: text(raw, 'appStoreId'),
    bundleIdentifier: text(raw, 'bundleIdentifier'),
    version: text(raw, 'version'),
    buildNumber: text(raw, 'buildNumber'),
    storefront: text(raw, 'storefront'),
    appStoreUrl: text(raw, 'appStoreUrl'),
    appleVerifiedAtUtc: text(raw, 'appleVerifiedAtUtc'),
    createdAt: text(raw, 'createdAt'),
    createdBy: nullableText(raw, 'createdBy'),
  }
}

export function normalizeNativeUpdatePolicy(value: unknown): NativeUpdatePolicy {
  const raw = asRecord(value)
  return {
    id: nullableText(raw, 'id'),
    enabled: boolean(raw, 'enabled'),
    policyVersion: number(raw, 'policyVersion'),
    releaseId: nullableText(raw, 'releaseId'),
    latestVersion: nullableText(raw, 'latestVersion'),
    minimumSupportedVersion: nullableText(raw, 'minimumSupportedVersion'),
    minimumSupportedBuildNumber: nullableInt32(raw, 'minimumSupportedBuildNumber'),
    appStoreUrl: nullableText(raw, 'appStoreUrl'),
    releaseMessage: nullableText(raw, 'releaseMessage'),
    targetScope: targetScope(raw),
    targetStoreGuids: stringArray(raw, 'targetStoreGuids'),
    updatedAt: nullableText(raw, 'updatedAt'),
    updatedBy: nullableText(raw, 'updatedBy'),
  }
}

function normalizeMobileAndroidLatestBuild(value: unknown): MobileAndroidLatestBuild | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return null
  }

  const raw = asRecord(value)
  // 关键位置：versionCode 必须是正整数，否则视为没有可用公开包，避免前端放开一个装不上的最低构建号。
  const appBuildVersion = nullableInt32(raw, 'appBuildVersion')
  if (appBuildVersion === null || appBuildVersion < 1) {
    return null
  }

  return {
    easBuildId: text(raw, 'easBuildId'),
    appVersion: text(raw, 'appVersion'),
    appBuildVersion,
    completedAt: nullableText(raw, 'completedAt'),
  }
}

export function normalizeMobileAndroidNativeUpdatePolicy(
  value: unknown,
): MobileAndroidNativeUpdatePolicy {
  const raw = asRecord(value)
  return {
    enabled: boolean(raw, 'enabled'),
    minimumSupportedBuildNumber: nullableInt32(raw, 'minimumSupportedBuildNumber'),
    releaseMessage: nullableText(raw, 'releaseMessage'),
    policyVersion: number(raw, 'policyVersion'),
    updatedAt: nullableText(raw, 'updatedAt'),
    updatedBy: nullableText(raw, 'updatedBy'),
    latestBuild: normalizeMobileAndroidLatestBuild(raw.latestBuild),
  }
}

export function normalizeAppUpdateStoreOption(value: unknown): AppUpdateTargetStoreOption {
  const raw = asRecord(value)
  return {
    storeGuid: text(raw, 'storeGuid'),
    storeCode: text(raw, 'storeCode'),
    storeName: text(raw, 'storeName'),
  }
}

export function normalizePosIpadOtaRelease(value: unknown): PosIpadOtaRelease {
  const raw = asRecord(value)
  return {
    id: text(raw, 'id'),
    environment: text(raw, 'environment'),
    updateGroupId: text(raw, 'updateGroupId'),
    iosUpdateId: text(raw, 'iosUpdateId'),
    channel: text(raw, 'channel'),
    runtimeVersion: text(raw, 'runtimeVersion'),
    gitCommitHash: nullableText(raw, 'gitCommitHash'),
    dashboardUrl: nullableText(raw, 'dashboardUrl'),
    publishedAtUtc: text(raw, 'publishedAtUtc'),
    isRollback: boolean(raw, 'isRollback'),
    rollbackOfReleaseId: nullableText(raw, 'rollbackOfReleaseId'),
    createdAt: text(raw, 'createdAt'),
    createdBy: nullableText(raw, 'createdBy'),
  }
}

export function normalizePosIpadOtaRollout(value: unknown): PosIpadOtaRollout {
  const raw = asRecord(value)
  const release = raw.release
  return {
    id: nullableText(raw, 'id'),
    enabled: boolean(raw, 'enabled'),
    policyVersion: number(raw, 'policyVersion'),
    releaseId: nullableText(raw, 'releaseId'),
    forceUpdate: boolean(raw, 'forceUpdate'),
    targetScope: targetScope(raw),
    targetStoreGuids: stringArray(raw, 'targetStoreGuids'),
    releaseMessage: nullableText(raw, 'releaseMessage'),
    release: release && typeof release === 'object'
      ? normalizePosIpadOtaRelease(release)
      : null,
    updatedAt: nullableText(raw, 'updatedAt'),
    updatedBy: nullableText(raw, 'updatedBy'),
  }
}

function assignmentTargetType(raw: Record<string, unknown>): PosIpadOtaAssignmentTargetType {
  return raw.targetType === 'device' ? 'device' : 'store'
}

export function normalizePosIpadOtaAssignment(value: unknown): PosIpadOtaAssignment {
  const raw = asRecord(value)
  const release = raw.release
  return {
    id: text(raw, 'id'),
    targetType: assignmentTargetType(raw),
    storeGuid: nullableText(raw, 'storeGuid'),
    storeCode: nullableText(raw, 'storeCode'),
    storeName: nullableText(raw, 'storeName'),
    deviceRegistrationId: nullableInt32(raw, 'deviceRegistrationId'),
    deviceNumber: nullableText(raw, 'deviceNumber'),
    deviceRemarks: nullableText(raw, 'deviceRemarks'),
    deviceAppVersion: nullableText(raw, 'deviceAppVersion'),
    enabled: boolean(raw, 'enabled'),
    expiresAtUtc: nullableText(raw, 'expiresAtUtc'),
    isExpired: boolean(raw, 'isExpired'),
    policyVersion: number(raw, 'policyVersion'),
    releaseId: text(raw, 'releaseId'),
    release: release && typeof release === 'object' && !Array.isArray(release)
      ? normalizePosIpadOtaRelease(release)
      : null,
    forceUpdate: boolean(raw, 'forceUpdate'),
    releaseMessage: nullableText(raw, 'releaseMessage'),
    note: nullableText(raw, 'note'),
    updatedAt: nullableText(raw, 'updatedAt'),
    updatedBy: nullableText(raw, 'updatedBy'),
  }
}

export function normalizePosIpadOtaTargetDevice(value: unknown): PosIpadOtaTargetDeviceOption {
  const raw = asRecord(value)
  return {
    deviceRegistrationId: nullableInt32(raw, 'deviceRegistrationId') ?? 0,
    systemDeviceNumber: text(raw, 'systemDeviceNumber'),
    storeCode: nullableText(raw, 'storeCode'),
    storeName: nullableText(raw, 'storeName'),
    remarks: nullableText(raw, 'remarks'),
    appVersion: nullableText(raw, 'appVersion'),
    isOnline: boolean(raw, 'isOnline'),
    lastHeartbeatAt: nullableText(raw, 'lastHeartbeatAt'),
  }
}

export function normalizePosIpadOtaTargetDevicePage(value: unknown): PosIpadOtaTargetDevicePage {
  const raw = asRecord(value)
  // 兼容 items / data 两种分页形态；没有 id 的行（空对象、非对象）直接丢弃，避免勾选到无效设备。
  const rows = Array.isArray(raw.items)
    ? raw.items
    : Array.isArray(raw.data)
      ? raw.data
      : []
  const items = rows
    .map(normalizePosIpadOtaTargetDevice)
    .filter((item) => item.deviceRegistrationId > 0)
  const total = Number(raw.total ?? raw.totalCount)
  const page = Number(raw.page ?? raw.pageIndex)
  const pageSize = Number(raw.pageSize)
  return {
    items,
    total: Number.isFinite(total) && total >= 0 ? total : items.length,
    page: Number.isInteger(page) && page >= 1 ? page : 1,
    pageSize: Number.isInteger(pageSize) && pageSize >= 1 ? pageSize : items.length,
  }
}

export function createAppUpdatePolicyService(transport: AppUpdatePolicyTransport) {
  return {
    async getIosAppStoreReleases(app: AppUpdateApp, signal?: AbortSignal) {
      const response = await transport.get('/api/app-update-releases/ios', {
        params: { app, storefront: 'au' },
        signal,
      })
      const payload = unwrap<unknown[]>(response)
      return Array.isArray(payload) ? payload.map(normalizeIosAppStoreRelease) : []
    },

    async createIosAppStoreRelease(
      payload: IosAppStoreReleaseCreateRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.post(
        '/api/app-update-releases/ios',
        payload,
        { signal },
      )
      return normalizeIosAppStoreRelease(unwrap(response))
    },

    async getMobileIosNativePolicy(signal?: AbortSignal) {
      const response = await transport.get('/api/app-update-policies/mobile-ios', { signal })
      return normalizeNativeUpdatePolicy(unwrap(response))
    },

    async saveMobileIosNativePolicy(
      payload: MobileIosNativeUpdatePolicyRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put(
        '/api/app-update-policies/mobile-ios',
        payload,
        { signal },
      )
      return normalizeNativeUpdatePolicy(unwrap(response))
    },

    async getMobileAndroidNativePolicy(signal?: AbortSignal) {
      const response = await transport.get('/api/app-update-policies/mobile-android', { signal })
      return normalizeMobileAndroidNativeUpdatePolicy(unwrap(response))
    },

    async saveMobileAndroidNativePolicy(
      payload: MobileAndroidNativeUpdatePolicyRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put(
        '/api/app-update-policies/mobile-android',
        payload,
        { signal },
      )
      return normalizeMobileAndroidNativeUpdatePolicy(unwrap(response))
    },

    async getPosIpadNativePolicy(signal?: AbortSignal) {
      const response = await transport.get(
        '/api/app-update-policies/pos-ipad/native',
        { signal },
      )
      return normalizeNativeUpdatePolicy(unwrap(response))
    },

    async savePosIpadNativePolicy(
      payload: PosIpadNativeUpdatePolicyRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put(
        '/api/app-update-policies/pos-ipad/native',
        payload,
        { signal },
      )
      return normalizeNativeUpdatePolicy(unwrap(response))
    },

    async getPosIpadStoreOptions(signal?: AbortSignal) {
      const response = await transport.get(
        '/api/app-update-policies/pos-ipad/store-options',
        { signal },
      )
      const payload = unwrap<unknown[]>(response)
      return Array.isArray(payload) ? payload.map(normalizeAppUpdateStoreOption) : []
    },

    async getPosIpadOtaReleases(signal?: AbortSignal) {
      const response = await transport.get('/api/pos-ipad/ota-releases', { signal })
      const payload = unwrap<unknown[]>(response)
      return Array.isArray(payload) ? payload.map(normalizePosIpadOtaRelease) : []
    },

    async getPosIpadOtaRollout(signal?: AbortSignal) {
      const response = await transport.get('/api/pos-ipad/ota-rollout', { signal })
      return normalizePosIpadOtaRollout(unwrap(response))
    },

    async savePosIpadOtaRollout(
      payload: PosIpadOtaRolloutRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put('/api/pos-ipad/ota-rollout', payload, { signal })
      return normalizePosIpadOtaRollout(unwrap(response))
    },

    async getPosIpadOtaAssignments(signal?: AbortSignal) {
      const response = await transport.get('/api/pos-ipad/ota-assignments', { signal })
      const payload = unwrap<unknown[]>(response)
      return Array.isArray(payload) ? payload.map(normalizePosIpadOtaAssignment) : []
    },

    async getPosIpadOtaTargetDevices(
      query: PosIpadOtaTargetDeviceQuery = {},
      signal?: AbortSignal,
    ) {
      const keyword = query.keyword?.trim()
      const response = await transport.get(
        '/api/pos-ipad/ota-assignments/target-options/devices',
        {
          // 空值不入参，避免把「全部分店 / 无关键字」误传成空字符串条件。
          params: {
            ...(query.storeGuid ? { storeGuid: query.storeGuid } : {}),
            ...(keyword ? { keyword } : {}),
            page: query.page ?? 1,
            pageSize: query.pageSize ?? 20,
          },
          signal,
        },
      )
      return normalizePosIpadOtaTargetDevicePage(unwrap(response))
    },

    async createPosIpadOtaAssignment(
      payload: PosIpadOtaAssignmentCreateRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.post('/api/pos-ipad/ota-assignments', payload, { signal })
      return normalizePosIpadOtaAssignment(unwrap(response))
    },

    async updatePosIpadOtaAssignment(
      id: string,
      payload: PosIpadOtaAssignmentUpdateRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put(
        `/api/pos-ipad/ota-assignments/${encodeURIComponent(id)}`,
        payload,
        { signal },
      )
      return normalizePosIpadOtaAssignment(unwrap(response))
    },

    async deletePosIpadOtaAssignment(
      id: string,
      expectedPolicyVersion: number,
      signal?: AbortSignal,
    ) {
      const response = await transport.delete(
        `/api/pos-ipad/ota-assignments/${encodeURIComponent(id)}`,
        { params: { expectedPolicyVersion }, signal },
      )
      unwrap<boolean>(response)
    },
  }
}

export const appUpdatePolicyService = createAppUpdatePolicyService(defaultTransport)
