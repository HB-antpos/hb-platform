import type {
  AppOtaRelease,
  MobileOtaEnvironment,
  MobileOtaPlatform,
  MobileOtaPolicy,
  MobileOtaPolicyRequest,
} from '../../../types/mobileOtaPolicy'

export interface MobileOtaPolicyFormValue {
  enabled: boolean
  required?: boolean
  targetReleaseId?: string | null
  releaseMessage?: string | null
}

function normalizeText(value?: string | null) {
  const normalized = value?.trim()
  return normalized || null
}

export function buildMobileOtaPolicyRequest(
  value: MobileOtaPolicyFormValue,
  expectedPolicyVersion: number,
): MobileOtaPolicyRequest {
  if (!value.enabled) {
    return {
      expectedPolicyVersion,
      enabled: false,
      required: false,
      targetReleaseId: null,
      releaseMessage: null,
    }
  }

  return {
    expectedPolicyVersion,
    enabled: true,
    required: Boolean(value.required),
    targetReleaseId: normalizeText(value.targetReleaseId),
    releaseMessage: normalizeText(value.releaseMessage),
  }
}

export function isMobileOtaReleaseCompatibleWithLane(
  release: AppOtaRelease,
  environment: MobileOtaEnvironment,
  platform: MobileOtaPlatform,
) {
  return release.appKey === 'mobile'
    && release.environment === environment
    && release.platform === platform
    && release.clientChannel === environment
}

export type MobileOtaReleaseActivation =
  | { kind: 'primary' }
  | { kind: 'additional'; runtimeVersion: string | null }
  | null

/**
 * 判断某条发布是否正在被当前策略投放。
 * 策略除主目标外还按 Runtime 挂附加目标（例如 iOS 主目标 1.0.5、附加 1.0.6/1.0.7），
 * 只比对主目标会把正在下发的附加目标误显示为「已登记」。策略停用时两者都不再投放。
 */
export function resolveMobileOtaReleaseActivation(
  releaseId: string,
  policy: Pick<MobileOtaPolicy, 'enabled' | 'targetReleaseId' | 'additionalTargets'> | null,
): MobileOtaReleaseActivation {
  if (!policy?.enabled) {
    return null
  }
  if (policy.targetReleaseId === releaseId) {
    return { kind: 'primary' }
  }
  const additional = policy.additionalTargets.find((target) => target.targetReleaseId === releaseId)
  return additional ? { kind: 'additional', runtimeVersion: additional.targetRuntimeVersion } : null
}

export function formatMobileOtaReleaseLabel(release: AppOtaRelease) {
  return [
    release.runtimeVersion || '--',
    release.releaseChannel || '--',
    release.updateId ? release.updateId.slice(0, 8) : '--',
  ].join(' · ')
}

export function parseMobileOtaRevisionSnapshot(value: string) {
  try {
    const parsed = JSON.parse(value) as unknown
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : {}
  } catch {
    // 审计快照属于展示辅助信息，旧数据损坏时保留时间线而不是让整条 lane 崩溃。
    return {}
  }
}
