import type {
  MobileAndroidLatestBuild,
  MobileAndroidNativeUpdatePolicy,
  MobileAndroidNativeUpdatePolicyRequest,
} from '../../../types/appUpdatePolicy'

export interface MobileAndroidNativePolicyFormValue {
  enabled: boolean
  minimumSupportedBuildNumber?: number | null
  releaseMessage?: string | null
}

// 与后端错误码一一对应，便于表单校验与服务端拒绝共用同一组提示文案。
export type MobileAndroidNativePolicyValidationError =
  | 'NO_LATEST_BUILD'
  | 'MINIMUM_BUILD_REQUIRED'
  | 'MINIMUM_BUILD_INVALID'
  | 'MINIMUM_BUILD_ABOVE_LATEST'
  | 'RELEASE_MESSAGE_TOO_LONG'

export interface MobileAndroidNativePolicyConfirmation {
  enabled: boolean
  minimumSupportedBuildNumber: number | null
  latestBuildLabel: string
  releaseMessage: string | null
}

export const MOBILE_ANDROID_RELEASE_MESSAGE_MAX_LENGTH = 1000

const INT32_MAX_VALUE = 2_147_483_647

// 后端保存失败时可能返回的业务错误码（版本冲突另由 isAppUpdatePolicyVersionConflict 处理）。
const SERVER_VALIDATION_ERROR_CODES = new Set<MobileAndroidNativePolicyValidationError>([
  'MINIMUM_BUILD_REQUIRED',
  'MINIMUM_BUILD_INVALID',
  'MINIMUM_BUILD_ABOVE_LATEST',
  'RELEASE_MESSAGE_TOO_LONG',
])

function normalizeText(value?: string | null) {
  const normalized = value?.trim()
  return normalized || null
}

function normalizeBuildNumber(value?: number | null) {
  return Number.isInteger(value) && Number(value) <= INT32_MAX_VALUE
    ? Number(value)
    : null
}

function readErrorCode(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null
  }

  const raw = value as Record<string, unknown>
  const code = raw.errorCode ?? raw.code
  if (typeof code === 'string') {
    return code
  }
  return readErrorCode(raw.data)
}

export function formatMobileAndroidBuildLabel(build?: MobileAndroidLatestBuild | null) {
  if (!build) {
    return '--'
  }
  return `${build.appVersion || '--'} (${build.appBuildVersion})`
}

export function buildMobileAndroidNativePolicyFormValue(
  policy: MobileAndroidNativeUpdatePolicy,
): MobileAndroidNativePolicyFormValue {
  return {
    enabled: policy.enabled,
    minimumSupportedBuildNumber: policy.minimumSupportedBuildNumber,
    releaseMessage: policy.releaseMessage,
  }
}

/**
 * 校验与后端 PUT 规则保持一致：
 * - 停用时不校验（保存时会清空最低构建号与说明）；
 * - 启用时必须存在公开 production 安卓包，且 1 ≤ 最低构建号 ≤ 公开包 versionCode，
 *   防止把所有设备锁死在一个装不上的版本。
 */
export function validateMobileAndroidNativePolicy(
  value: MobileAndroidNativePolicyFormValue,
  latestBuild: MobileAndroidLatestBuild | null,
): MobileAndroidNativePolicyValidationError | null {
  if (!value.enabled) {
    return null
  }

  const releaseMessage = normalizeText(value.releaseMessage)
  if (releaseMessage && releaseMessage.length > MOBILE_ANDROID_RELEASE_MESSAGE_MAX_LENGTH) {
    return 'RELEASE_MESSAGE_TOO_LONG'
  }
  if (!latestBuild) {
    return 'NO_LATEST_BUILD'
  }

  const minimum = value.minimumSupportedBuildNumber
  if (minimum === null || minimum === undefined) {
    return 'MINIMUM_BUILD_REQUIRED'
  }

  const normalizedMinimum = normalizeBuildNumber(minimum)
  if (normalizedMinimum === null || normalizedMinimum < 1) {
    return 'MINIMUM_BUILD_INVALID'
  }
  if (normalizedMinimum > latestBuild.appBuildVersion) {
    return 'MINIMUM_BUILD_ABOVE_LATEST'
  }
  return null
}

export function buildMobileAndroidNativePolicyRequest(
  value: MobileAndroidNativePolicyFormValue,
  expectedPolicyVersion: number,
): MobileAndroidNativeUpdatePolicyRequest {
  // 停用时与后端口径一致：最低构建号和更新说明一并清空，避免旧值残留在下次启用时被误用。
  if (!value.enabled) {
    return {
      expectedPolicyVersion,
      enabled: false,
      minimumSupportedBuildNumber: null,
      releaseMessage: null,
    }
  }

  return {
    expectedPolicyVersion,
    enabled: true,
    minimumSupportedBuildNumber: normalizeBuildNumber(value.minimumSupportedBuildNumber),
    releaseMessage: normalizeText(value.releaseMessage),
  }
}

export function buildMobileAndroidNativePolicyConfirmation(
  value: MobileAndroidNativePolicyFormValue,
  latestBuild: MobileAndroidLatestBuild | null,
): MobileAndroidNativePolicyConfirmation {
  // 确认弹窗展示的是即将 PUT 的规范化值，保证「看到的就是提交的」。
  const request = buildMobileAndroidNativePolicyRequest(value, 0)
  return {
    enabled: request.enabled,
    minimumSupportedBuildNumber: request.minimumSupportedBuildNumber,
    latestBuildLabel: formatMobileAndroidBuildLabel(latestBuild),
    releaseMessage: request.releaseMessage,
  }
}

/** 识别后端返回的业务校验错误码，未知错误返回 null 交由通用失败提示处理。 */
export function resolveMobileAndroidNativePolicySaveError(
  error: unknown,
): MobileAndroidNativePolicyValidationError | null {
  if (!error || typeof error !== 'object') {
    return null
  }

  const code = readErrorCode((error as { payload?: unknown }).payload)
  return code && SERVER_VALIDATION_ERROR_CODES.has(code as MobileAndroidNativePolicyValidationError)
    ? code as MobileAndroidNativePolicyValidationError
    : null
}

/** 校验错误码到文案 key 的映射，表单校验和服务端拒绝共用。 */
export function mobileAndroidNativePolicyErrorMessageKey(
  error: MobileAndroidNativePolicyValidationError,
) {
  const keys: Record<MobileAndroidNativePolicyValidationError, string> = {
    NO_LATEST_BUILD: 'system.appDownloads.updatePolicy.mobileAndroid.noLatestBuild',
    MINIMUM_BUILD_REQUIRED: 'system.appDownloads.updatePolicy.mobileAndroid.minimumBuildRequired',
    MINIMUM_BUILD_INVALID: 'system.appDownloads.updatePolicy.mobileAndroid.minimumBuildInvalid',
    MINIMUM_BUILD_ABOVE_LATEST: 'system.appDownloads.updatePolicy.mobileAndroid.minimumBuildAboveLatest',
    RELEASE_MESSAGE_TOO_LONG: 'system.appDownloads.updatePolicy.mobileAndroid.releaseMessageTooLong',
  }
  return keys[error]
}
