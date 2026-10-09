import dayjs from 'dayjs'

import type {
  CreateLinklyTerminalRequest,
  LinklyCloudCredentialAdminDto,
  LinklyTerminalAdminDto,
  LinklyTerminalManagementDto,
  PaymentTerminalEnvironment,
  PaymentTerminalEnvironmentStatusDto,
  UpdateLinklyTerminalRequest,
  UpdateSquareTokenRequest,
} from '../../../types/paymentTerminalSettings'
import { parseUserUtcTimestamp } from '../Users/time'

export interface SquareTokenFormValues {
  accessToken: string
  clearToken: boolean
}

export interface LinklyCredentialFormValues {
  username: string
  password: string
  clearCredential: boolean
}

export interface LinklyTerminalFormValues {
  laneNo: number
  displayName: string
  username: string
  password: string
}

export function createSquareTokenFormValues(): SquareTokenFormValues {
  return {
    accessToken: '',
    clearToken: false,
  }
}

export function createLinklyCredentialFormValues(
  credential?: LinklyCloudCredentialAdminDto | null,
): LinklyCredentialFormValues {
  return {
    username: credential?.username ?? '',
    password: '',
    clearCredential: false,
  }
}

export function createLinklyTerminalFormValues(
  terminal?: LinklyTerminalAdminDto | null,
): LinklyTerminalFormValues {
  return {
    laneNo: terminal?.laneNo ?? 1,
    displayName: terminal?.displayName ?? '',
    // 后端只返回掩码用户名；编辑时留空代表保留现有凭据。
    username: '',
    password: '',
  }
}

export function buildSquareTokenPayload(
  environment: PaymentTerminalEnvironment,
  values: SquareTokenFormValues,
): UpdateSquareTokenRequest {
  const payload: UpdateSquareTokenRequest = {
    environment,
    clearToken: values.clearToken,
  }

  // 清除或留空时不提交 token 明文；后端据此清除或保留原 token。
  const token = values.accessToken.trim()
  if (!values.clearToken && token) {
    payload.accessToken = token
  }

  return payload
}

export function buildCreateLinklyTerminalPayload(
  storeCode: string,
  environment: PaymentTerminalEnvironment,
  values: LinklyTerminalFormValues,
): CreateLinklyTerminalRequest {
  return {
    storeCode,
    environment,
    laneNo: values.laneNo,
    displayName: values.displayName.trim(),
    username: values.username.trim(),
    password: values.password.trim(),
  }
}

export function buildUpdateLinklyTerminalPayload(
  storeCode: string,
  environment: PaymentTerminalEnvironment,
  values: LinklyTerminalFormValues,
): UpdateLinklyTerminalRequest {
  const payload: UpdateLinklyTerminalRequest = {
    storeCode,
    environment,
    laneNo: values.laneNo,
    displayName: values.displayName.trim(),
  }

  const username = values.username.trim()
  const password = values.password.trim()
  if (username) {
    payload.username = username
  }
  if (password) {
    payload.password = password
  }
  return payload
}

export function canActivateLinklyConfiguration(management?: LinklyTerminalManagementDto | null) {
  if (!management || management.mode === 'Active') {
    return false
  }

  const readyTerminalIds = new Set(
    management.terminals
      .filter((terminal) => terminal.pairingState === 'Ready')
      .map((terminal) => terminal.terminalId),
  )
  if (readyTerminalIds.size === 0) {
    return false
  }

  const enabledDevices = management.devices.filter((device) => device.enabled)
  const selectedTerminalIds = enabledDevices.flatMap((device) => device.terminalId ? [device.terminalId] : [])
  // 未选择 Cloud 终端的 POS 可使用其他连接方式，不作为启用前置条件。
  return new Set(selectedTerminalIds).size === selectedTerminalIds.length
    && selectedTerminalIds.every((terminalId) => readyTerminalIds.has(terminalId))
}

export function getLinklyTerminalAssignmentOwner(
  management: LinklyTerminalManagementDto | null | undefined,
  terminalId: string,
  currentDeviceCode: string,
) {
  return management?.devices.find((device) => (
    device.deviceCode !== currentDeviceCode && device.terminalId === terminalId
  ))?.deviceCode ?? null
}

export function getEnvironmentStatus<T extends { environment: PaymentTerminalEnvironment }>(
  statuses: T[],
  environment: PaymentTerminalEnvironment,
): T | undefined {
  return statuses.find((status) => status.environment === environment)
}

export function isConfiguredStatus(status?: PaymentTerminalEnvironmentStatusDto | LinklyCloudCredentialAdminDto) {
  if (!status) {
    return false
  }
  return 'hasPassword' in status ? status.hasPassword : status.configured
}

export function resolvePaymentTerminalSettingsErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message.trim() ? error.message : fallback
}

/** 新增终端时默认填入最小的未占用 Lane 编号，避免默认值 1 与已有终端撞号。 */
export function suggestNextLaneNo(terminals: Pick<LinklyTerminalAdminDto, 'laneNo'>[]) {
  const used = new Set(terminals.map((terminal) => terminal.laneNo))
  let laneNo = 1
  while (used.has(laneNo) && laneNo < 9999) {
    laneNo += 1
  }
  return laneNo
}

/** 某台终端当前被哪些 POS 选为初始终端（含设备记录已缺失的选择）。 */
export function getTerminalDeviceCodes(
  management: LinklyTerminalManagementDto | null | undefined,
  terminalId: string,
) {
  return (management?.devices ?? [])
    .filter((device) => device.terminalId === terminalId)
    .map((device) => device.deviceCode)
}

export interface LinklySelectionIssue {
  deviceCode: string
  terminalId: string
}

export interface LinklyTerminalConflict {
  terminalId: string
  deviceCodes: string[]
}

export interface LinklyActivationChecklist {
  terminalCount: number
  readyTerminalCount: number
  /** 已启用且选择了 Cloud 终端的 POS 数量 */
  assignedDeviceCount: number
  /** 同一终端被多台已启用 POS 选中（阻止启用） */
  conflicts: LinklyTerminalConflict[]
  /** 已启用 POS 选中了尚未配对可用的终端（阻止启用） */
  notReadySelections: LinklySelectionIssue[]
  /** 设备注册记录已删除但仍占着终端（不阻止启用，但需要管理员显式清除） */
  orphanSelections: LinklySelectionIssue[]
  canActivate: boolean
}

/**
 * 把「能否启用多终端」拆成可展示的检查项，告诉管理员按钮为什么不可点。
 * canActivate 直接复用 canActivateLinklyConfiguration，保证清单与按钮状态永远同一口径。
 */
export function buildLinklyActivationChecklist(
  management: LinklyTerminalManagementDto | null | undefined,
): LinklyActivationChecklist {
  const terminals = management?.terminals ?? []
  const devices = management?.devices ?? []
  const readyTerminalIds = new Set(
    terminals.filter((terminal) => terminal.pairingState === 'Ready').map((terminal) => terminal.terminalId),
  )

  const enabledSelections = devices.filter((device) => device.enabled && device.terminalId)
  const devicesByTerminal = new Map<string, string[]>()
  for (const device of enabledSelections) {
    const terminalId = device.terminalId as string
    devicesByTerminal.set(terminalId, [...(devicesByTerminal.get(terminalId) ?? []), device.deviceCode])
  }

  return {
    terminalCount: terminals.length,
    readyTerminalCount: readyTerminalIds.size,
    assignedDeviceCount: enabledSelections.length,
    conflicts: [...devicesByTerminal.entries()]
      .filter(([, deviceCodes]) => deviceCodes.length > 1)
      .map(([terminalId, deviceCodes]) => ({ terminalId, deviceCodes })),
    notReadySelections: enabledSelections
      .filter((device) => !readyTerminalIds.has(device.terminalId as string))
      .map((device) => ({ deviceCode: device.deviceCode, terminalId: device.terminalId as string })),
    orphanSelections: devices
      .filter((device) => device.deviceMissing && device.terminalId)
      .map((device) => ({ deviceCode: device.deviceCode, terminalId: device.terminalId as string })),
    canActivate: canActivateLinklyConfiguration(management),
  }
}

export type TerminalHealthTone = 'healthy' | 'unhealthy' | 'other' | 'unchecked'

/** POS 上报的健康状态只有 Healthy / Unhealthy；其他非空值原样展示。 */
export function getTerminalHealthTone(status?: string | null): TerminalHealthTone {
  const normalized = status?.trim().toLowerCase()
  if (!normalized) return 'unchecked'
  if (normalized === 'healthy') return 'healthy'
  if (normalized === 'unhealthy') return 'unhealthy'
  return 'other'
}

export type ElapsedDescription =
  | { kind: 'never' }
  | { kind: 'justNow' }
  | { kind: 'minutes'; count: number }
  | { kind: 'hours'; count: number }
  | { kind: 'days'; count: number }

/** 把 UTC 时间描述为距今多久，用于「最近检测」这类只关心新鲜度的字段。 */
export function describeElapsedSince(value: string | null | undefined, now: Date): ElapsedDescription {
  const parsed = parseUserUtcTimestamp(value)
  if (!parsed) return { kind: 'never' }

  const elapsedMinutes = Math.max(0, dayjs(now).diff(parsed, 'minute'))
  if (elapsedMinutes < 1) return { kind: 'justNow' }
  if (elapsedMinutes < 60) return { kind: 'minutes', count: elapsedMinutes }
  if (elapsedMinutes < 24 * 60) return { kind: 'hours', count: Math.floor(elapsedMinutes / 60) }
  return { kind: 'days', count: Math.floor(elapsedMinutes / (24 * 60)) }
}
