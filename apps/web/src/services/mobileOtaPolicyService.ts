import type { ApiResponse } from '../types/api'
import type {
  AppOtaRelease,
  MobileOtaAdditionalTarget,
  MobileOtaEnvironment,
  MobileOtaPlatform,
  MobileOtaPolicy,
  MobileOtaPolicyRequest,
  MobileOtaPolicyRevision,
} from '../types/mobileOtaPolicy'
import request, { unwrapApiData } from '../utils/request'

type TransportOptions = {
  params?: Record<string, unknown>
  signal?: AbortSignal
}

export interface MobileOtaPolicyTransport {
  get(url: string, options?: TransportOptions): Promise<unknown>
  put(url: string, payload?: unknown, options?: TransportOptions): Promise<unknown>
}

const defaultTransport: MobileOtaPolicyTransport = {
  get: (url, options) => request.get(url, options),
  put: (url, payload, options) => request.put(url, payload, options),
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function text(raw: Record<string, unknown>, key: string) {
  const value = raw[key]
  if (typeof value === 'string') {
    return value
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return String(value)
  }
  return ''
}

function nullableText(raw: Record<string, unknown>, key: string) {
  const value = text(raw, key).trim()
  return value || null
}

function number(raw: Record<string, unknown>, key: string) {
  const value = Number(raw[key])
  return Number.isFinite(value) ? value : 0
}

function boolean(raw: Record<string, unknown>, key: string) {
  return raw[key] === true
}

function environment(value: unknown): MobileOtaEnvironment {
  return String(value).trim().toLowerCase() === 'preview' ? 'preview' : 'production'
}

function platform(value: unknown): MobileOtaPlatform {
  return String(value).trim().toLowerCase() === 'ios' ? 'ios' : 'android'
}

function unwrap<T>(payload: unknown) {
  return unwrapApiData(payload as ApiResponse<T> | T)
}

function payloadItems(payload: unknown) {
  if (Array.isArray(payload)) {
    return payload
  }
  const raw = asRecord(payload)
  return Array.isArray(raw.items) ? raw.items as unknown[] : []
}

export function normalizeAppOtaRelease(value: unknown): AppOtaRelease {
  const raw = asRecord(value)
  return {
    id: text(raw, 'id'),
    releaseBatchId: text(raw, 'releaseBatchId'),
    appKey: text(raw, 'appKey').trim().toLowerCase() === 'pos-handheld'
      ? 'pos-handheld'
      : 'mobile',
    environment: environment(raw.environment),
    clientChannel: text(raw, 'clientChannel'),
    releaseChannel: text(raw, 'releaseChannel'),
    easBranch: text(raw, 'easBranch'),
    projectName: text(raw, 'projectName'),
    platform: platform(raw.platform),
    runtimeVersion: text(raw, 'runtimeVersion'),
    updateGroupId: text(raw, 'updateGroupId'),
    updateId: text(raw, 'updateId'),
    message: nullableText(raw, 'message'),
    gitCommitHash: nullableText(raw, 'gitCommitHash'),
    dashboardUrl: nullableText(raw, 'dashboardUrl'),
    publishedAtUtc: text(raw, 'publishedAtUtc'),
    isRollback: boolean(raw, 'isRollback'),
    rollbackOfReleaseId: nullableText(raw, 'rollbackOfReleaseId'),
    factFingerprint: text(raw, 'factFingerprint'),
    legacy: boolean(raw, 'legacy'),
    registrationSource: nullableText(raw, 'registrationSource'),
    createdAt: text(raw, 'createdAt'),
    createdBy: nullableText(raw, 'createdBy'),
  }
}

function normalizeAdditionalTargets(value: unknown): MobileOtaAdditionalTarget[] {
  if (!Array.isArray(value)) {
    return []
  }
  // 附加目标只用于展示与确认；缺少发布 ID 的异常项直接丢弃，不让整条 lane 渲染失败。
  return value.flatMap((item) => {
    const raw = asRecord(item)
    const targetReleaseId = nullableText(raw, 'targetReleaseId')
    return targetReleaseId
      ? [{ targetReleaseId, targetRuntimeVersion: nullableText(raw, 'targetRuntimeVersion') }]
      : []
  })
}

export function normalizeMobileOtaPolicy(value: unknown): MobileOtaPolicy {
  const raw = asRecord(value)
  const targetRelease = raw.targetRelease && typeof raw.targetRelease === 'object'
    ? normalizeAppOtaRelease(raw.targetRelease)
    : null
  return {
    id: nullableText(raw, 'id'),
    environment: environment(raw.environment),
    platform: platform(raw.platform),
    enabled: boolean(raw, 'enabled'),
    required: boolean(raw, 'required'),
    policyVersion: number(raw, 'policyVersion'),
    targetReleaseId: nullableText(raw, 'targetReleaseId'),
    targetRuntimeVersion: nullableText(raw, 'targetRuntimeVersion')
      ?? targetRelease?.runtimeVersion
      ?? null,
    releaseMessage: nullableText(raw, 'releaseMessage'),
    targetRelease,
    additionalTargets: normalizeAdditionalTargets(raw.additionalTargets),
    updatedAt: nullableText(raw, 'updatedAt'),
    updatedBy: nullableText(raw, 'updatedBy'),
  }
}

export function normalizeMobileOtaPolicyRevision(value: unknown): MobileOtaPolicyRevision {
  const raw = asRecord(value)
  return {
    id: text(raw, 'id'),
    environment: environment(raw.environment),
    platform: platform(raw.platform),
    policyVersion: number(raw, 'policyVersion'),
    operation: nullableText(raw, 'operation') ?? nullableText(raw, 'action') ?? '',
    snapshotJson: nullableText(raw, 'snapshotJson')
      ?? JSON.stringify(raw.snapshot ?? {}),
    createdAt: text(raw, 'createdAt'),
    createdBy: nullableText(raw, 'createdBy'),
  }
}

export function createMobileOtaPolicyService(transport: MobileOtaPolicyTransport) {
  const policyBasePath = '/api/app-update-policies/mobile-ota'
  return {
    async getReleases(
      targetEnvironment: MobileOtaEnvironment,
      targetPlatform: MobileOtaPlatform,
      signal?: AbortSignal,
    ) {
      const response = await transport.get('/api/app-ota-releases', {
        params: {
          appKey: 'mobile',
          environment: targetEnvironment,
          platform: targetPlatform,
        },
        signal,
      })
      return payloadItems(unwrap(response))
        .filter((value) => {
          const raw = asRecord(value)
          return text(raw, 'appKey').trim().toLowerCase() === 'mobile'
            && text(raw, 'environment').trim().toLowerCase() === targetEnvironment
            && text(raw, 'platform').trim().toLowerCase() === targetPlatform
            && text(raw, 'clientChannel').trim().toLowerCase() === targetEnvironment
        })
        .map(normalizeAppOtaRelease)
    },

    async getPolicy(
      targetEnvironment: MobileOtaEnvironment,
      targetPlatform: MobileOtaPlatform,
      signal?: AbortSignal,
    ) {
      const response = await transport.get(
        `${policyBasePath}/${targetEnvironment}/${targetPlatform}`,
        { signal },
      )
      return normalizeMobileOtaPolicy(unwrap(response))
    },

    async savePolicy(
      targetEnvironment: MobileOtaEnvironment,
      targetPlatform: MobileOtaPlatform,
      payload: MobileOtaPolicyRequest,
      signal?: AbortSignal,
    ) {
      const response = await transport.put(
        `${policyBasePath}/${targetEnvironment}/${targetPlatform}`,
        payload,
        { signal },
      )
      return normalizeMobileOtaPolicy(unwrap(response))
    },

    async getRevisions(
      targetEnvironment: MobileOtaEnvironment,
      targetPlatform: MobileOtaPlatform,
      signal?: AbortSignal,
    ) {
      const response = await transport.get(
        `${policyBasePath}/${targetEnvironment}/${targetPlatform}/revisions`,
        { signal },
      )
      return payloadItems(unwrap(response)).map(normalizeMobileOtaPolicyRevision)
    },
  }
}

export const mobileOtaPolicyService = createMobileOtaPolicyService(defaultTransport)
