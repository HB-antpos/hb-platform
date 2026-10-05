import type {
  WpfAppRelease,
  WpfReleasePolicyRequest,
  WpfUpdateTargetScope,
} from '../../../types/wpfVersion'

export const WPF_RELEASE_CHANNELS = ['production', 'preview'] as const

export function normalizeWpfReleaseChannel(channel?: string | null) {
  const normalized = channel?.trim().toLowerCase()
  return normalized || 'production'
}

export function isSupportedWpfInstallerFile(fileName: string) {
  return /\.(exe|msi)$/i.test(fileName.trim())
}

export function getWpfVersionErrorMessage(error: unknown, fallback: string) {
  if (error instanceof Error && error.message.trim()) {
    return error.message
  }

  return fallback
}

export function inferWpfInstallerType(fileName: string) {
  const normalized = fileName.trim().toLowerCase()
  return normalized.endsWith('.msi') ? 'msi' : 'exe'
}

export function getDefaultWpfInstallerArguments(fileName: string) {
  return inferWpfInstallerType(fileName) === 'msi'
    ? '/qn /norestart'
    : '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS'
}

export async function calculateFileSha256(file: Blob): Promise<string> {
  if (!globalThis.crypto?.subtle) {
    throw new Error('SHA-256 calculation is not available in this browser')
  }

  // 使用浏览器原生 Web Crypto 直接对安装包内容算哈希，避免人工录入校验值出错。
  const digest = await globalThis.crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, '0'))
    .join('')
}

export function buildWpfPolicyPayload(input: WpfReleasePolicyRequest): WpfReleasePolicyRequest {
  const targetScope = normalizeWpfTargetScope(input.targetScope)
  return {
    channel: normalizeWpfReleaseChannel(input.channel),
    targetVersion: input.targetVersion.trim(),
    minimumSupportedVersion: input.minimumSupportedVersion.trim(),
    forceUpdate: input.forceUpdate,
    isRollback: input.isRollback,
    rollbackConfirmed: input.rollbackConfirmed,
    targetScope,
    targetStoreGuids: targetScope === 'stores'
      ? normalizeUniqueStrings(input.targetStoreGuids)
      : [],
    targetDeviceRegistrationIds: targetScope === 'devices'
      ? normalizeUniquePositiveIntegers(input.targetDeviceRegistrationIds)
      : [],
  }
}

export function canSubmitWpfPolicy(
  input: Pick<WpfReleasePolicyRequest,
    'targetVersion' | 'minimumSupportedVersion' | 'targetScope' | 'targetStoreGuids' | 'targetDeviceRegistrationIds'>,
) {
  if (!input.targetVersion.trim() || !input.minimumSupportedVersion.trim()) {
    return false
  }

  const targetScope = normalizeWpfTargetScope(input.targetScope)
  if (targetScope === 'stores') {
    return normalizeUniqueStrings(input.targetStoreGuids).length > 0
  }
  if (targetScope === 'devices') {
    return normalizeUniquePositiveIntegers(input.targetDeviceRegistrationIds).length > 0
  }
  return true
}

export function canSubmitWpfPolicyEditor(input: {
  policy: WpfReleasePolicyRequest
  policyDataReady: boolean
  activeVersions: string[]
  targetOptionsLoading: boolean
  targetOptionsError: boolean
}) {
  if (!input.policyDataReady) {
    return false
  }

  const policy = buildWpfPolicyPayload(input.policy)
  const activeVersions = new Set(
    input.activeVersions.map((version) => version.trim()).filter(Boolean),
  )
  if (
    !canSubmitWpfPolicy(policy)
    || !activeVersions.has(policy.targetVersion)
    || !activeVersions.has(policy.minimumSupportedVersion)
    || getWpfPolicyRangeError(policy) === 'INVALID_VERSION_RANGE'
  ) {
    return false
  }

  return policy.targetScope === 'all'
    || (!input.targetOptionsLoading && !input.targetOptionsError)
}

export function getEffectiveWpfMinimumSupportedVersion(
  input: Pick<WpfReleasePolicyRequest, 'targetVersion' | 'minimumSupportedVersion'>,
) {
  const targetVersion = input.targetVersion.trim()
  const minimumSupportedVersion = input.minimumSupportedVersion.trim()

  if (!targetVersion) {
    return minimumSupportedVersion
  }

  if (!minimumSupportedVersion) {
    return targetVersion
  }

  const comparison = compareWpfVersion(minimumSupportedVersion, targetVersion)
  if (comparison !== null && comparison > 0) {
    return targetVersion
  }

  return minimumSupportedVersion
}

export function getWpfPolicyRangeError(
  input: Pick<WpfReleasePolicyRequest, 'targetVersion' | 'minimumSupportedVersion'>,
) {
  const targetVersion = input.targetVersion.trim()
  const minimumSupportedVersion = input.minimumSupportedVersion.trim()

  if (!targetVersion || !minimumSupportedVersion) {
    return null
  }

  // 中文注释：前端提交前先拦住 minimum > target，避免 set-current/rollback 复用旧 minimum 生成非法策略。
  const comparison = compareWpfVersion(minimumSupportedVersion, targetVersion)
  return comparison !== null && comparison > 0 ? 'INVALID_VERSION_RANGE' : null
}

export function isWpfRollbackTarget(
  targetVersion: string,
  releases: Array<Pick<WpfAppRelease, 'version' | 'isCurrent' | 'targetVersion'>>,
) {
  // 回滚确认以后台当前策略目标为基准，不能用当前分页里的最新活跃版本推断。
  const baselineVersion =
    releases.find((item) => item.targetVersion)?.targetVersion ??
    releases.find((item) => item.isCurrent)?.version ??
    null

  if (!baselineVersion) {
    return false
  }

  const comparison = compareWpfVersion(targetVersion, baselineVersion)
  return comparison !== null && comparison < 0
}

export function getWpfCurrentVersionText(
  releases: Array<Pick<WpfAppRelease, 'version' | 'isCurrent' | 'targetVersion'>>,
) {
  // 当前策略目标可能不在当前分页内，摘要优先展示后端附带的 targetVersion，不能退回分页第一行。
  const policyTarget = releases.find((item) => item.targetVersion?.trim())?.targetVersion?.trim()
  if (policyTarget) {
    return policyTarget
  }

  return releases.find((item) => item.isCurrent)?.version ?? null
}

export function getWpfPolicySummary(
  releases: Array<
    Pick<WpfAppRelease, 'channel' | 'version' | 'isCurrent' | 'targetVersion' | 'minimumSupportedVersion' | 'forceUpdate'>
    & Partial<Pick<WpfAppRelease,
      | 'targetScope'
      | 'targetStoreGuids'
      | 'targetDeviceRegistrationIds'
      | 'targetStoreSummaries'
      | 'targetDeviceSummaries'
      | 'policyUpdatedAt'
      | 'policyUpdatedBy'>>
  >,
) {
  // 策略元数据由后端随每行返回；当前目标不在当前分页时，不能用第一页发布行本身推断策略。
  const policyCarrier = releases.find((item) => item.targetVersion?.trim())
    ?? releases.find((item) => item.isCurrent)
  if (!policyCarrier) {
    return null
  }

  const targetVersion = policyCarrier.targetVersion?.trim()
    || (policyCarrier.isCurrent ? policyCarrier.version.trim() : '')
  if (!targetVersion) {
    return null
  }

  const currentRelease = releases.find((item) => item.isCurrent || item.version.trim() === targetVersion)
  const targetScope = normalizeWpfTargetScope(policyCarrier.targetScope)
  const targetStoreGuids = targetScope === 'stores'
    ? normalizeUniqueStrings(policyCarrier.targetStoreGuids ?? [])
    : []
  const targetDeviceRegistrationIds = targetScope === 'devices'
    ? normalizeUniquePositiveIntegers(policyCarrier.targetDeviceRegistrationIds ?? [])
    : []
  const targetStoreGuidSet = new Set(targetStoreGuids.map((value) => value.toLowerCase()))
  const targetDeviceIdSet = new Set(targetDeviceRegistrationIds)

  return {
    channel: normalizeWpfReleaseChannel((currentRelease ?? policyCarrier).channel),
    targetVersion,
    minimumSupportedVersion: policyCarrier.minimumSupportedVersion?.trim() || targetVersion,
    forceUpdate: Boolean(policyCarrier.forceUpdate || currentRelease?.forceUpdate),
    targetScope,
    targetStoreGuids,
    targetDeviceRegistrationIds,
    targetStoreSummaries: targetScope === 'stores'
      ? (policyCarrier.targetStoreSummaries ?? [])
        .filter((item) => targetStoreGuidSet.has(item.storeGuid.toLowerCase()))
      : [],
    targetDeviceSummaries: targetScope === 'devices'
      ? (policyCarrier.targetDeviceSummaries ?? [])
        .filter((item) => targetDeviceIdSet.has(item.deviceRegistrationId))
      : [],
    policyUpdatedAt: policyCarrier.policyUpdatedAt ?? null,
    policyUpdatedBy: policyCarrier.policyUpdatedBy ?? null,
  }
}

export function normalizeWpfTargetScope(value?: string | null): WpfUpdateTargetScope {
  const normalized = value?.trim().toLowerCase()
  if (normalized === 'stores' || normalized === 'devices') {
    return normalized
  }
  return 'all'
}

function normalizeUniqueStrings(values: string[]) {
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))].sort()
}

function normalizeUniquePositiveIntegers(values: number[]) {
  return [...new Set(values.filter((value) => Number.isInteger(value) && value > 0))].sort((left, right) => left - right)
}

export function compareWpfVersion(left: string, right: string) {
  const leftParts = parseWpfVersion(left)
  const rightParts = parseWpfVersion(right)
  if (!leftParts || !rightParts) {
    return null
  }

  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index += 1) {
    const delta = (leftParts[index] ?? 0) - (rightParts[index] ?? 0)
    if (delta !== 0) {
      return delta
    }
  }
  return 0
}

function parseWpfVersion(version: string) {
  const match = version.trim().match(/^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$/i)
  if (!match) {
    return null
  }

  return match.slice(1).map((part) => Number(part ?? 0))
}
