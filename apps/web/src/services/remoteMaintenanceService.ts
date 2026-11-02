import request, { unwrapApiData } from '../utils/request'
import type {
  RemoteMaintenanceDevice,
  RemoteMaintenanceDevicePage,
  RemoteMaintenanceDeviceQuery,
  RemoteMaintenanceManifest,
  RemoteOnlineStatus,
  RemoteRustDeskVersionStatus,
  RemoteServiceStatus,
} from '../types/remoteMaintenance'

const BASE_PATH = '/api/remote-maintenance/admin'

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function text(raw: Record<string, unknown>, key: string) {
  return typeof raw[key] === 'string' ? raw[key] as string : ''
}

function nullableText(raw: Record<string, unknown>, key: string) {
  const value = text(raw, key).trim()
  return value || null
}

function finiteNumber(raw: Record<string, unknown>, key: string, fallback = 0) {
  const value = Number(raw[key])
  return Number.isFinite(value) ? value : fallback
}

function onlineStatus(raw: Record<string, unknown>): RemoteOnlineStatus {
  return raw.onlineStatus === 'online' || raw.onlineStatus === 'offline'
    ? raw.onlineStatus
    : 'never'
}

function serviceStatus(raw: Record<string, unknown>): RemoteServiceStatus {
  const value = raw.serviceStatus
  return value === 'notInstalled' || value === 'running' || value === 'stopped' ||
    value === 'starting' || value === 'stopping' || value === 'checkFailed'
    ? value
    : 'checkFailed'
}

function rustDeskVersionStatus(raw: Record<string, unknown>): RemoteRustDeskVersionStatus {
  const value = raw.rustDeskVersionStatus
  // 旧版后端没有这个字段，按“无法判断”处理，不误报。
  return value === 'match' || value === 'drift' || value === 'missing' ? value : 'unknown'
}

export function normalizeRemoteMaintenanceDevice(value: unknown): RemoteMaintenanceDevice {
  const raw = asRecord(value)
  return {
    id: text(raw, 'id'),
    deviceRegistrationId: text(raw, 'deviceRegistrationId') || String(raw.deviceRegistrationId ?? ''),
    storeCode: text(raw, 'storeCode'),
    deviceCode: text(raw, 'deviceCode'),
    computerName: text(raw, 'computerName'),
    rustdeskId: text(raw, 'rustdeskId'),
    clientVersion: text(raw, 'clientVersion'),
    // 空串有含义（程序文件读不到），只有字段缺失或非字符串才是 null。
    observedRustDeskVersion: typeof raw.observedRustDeskVersion === 'string' ? raw.observedRustDeskVersion.trim() : null,
    rustDeskVersionStatus: rustDeskVersionStatus(raw),
    agentVersion: text(raw, 'agentVersion'),
    onlineStatus: onlineStatus(raw),
    serviceStatus: serviceStatus(raw),
    lastSeenAtUtc: nullableText(raw, 'lastSeenAtUtc'),
    registeredAtUtc: nullableText(raw, 'registeredAtUtc'),
    isStale: raw.isStale === true,
  }
}

export function normalizeRemoteMaintenanceDevicePage(value: unknown): RemoteMaintenanceDevicePage {
  const raw = asRecord(unwrapApiData(value as never))
  const items = Array.isArray(raw.items)
    ? raw.items.map(normalizeRemoteMaintenanceDevice)
    : []
  return {
    items,
    total: Math.max(0, finiteNumber(raw, 'total', finiteNumber(raw, 'totalCount'))),
    page: Math.max(1, finiteNumber(raw, 'page', finiteNumber(raw, 'pageIndex', 1))),
    pageSize: Math.max(1, finiteNumber(raw, 'pageSize', 10)),
    serverTimeUtc: nullableText(raw, 'serverTimeUtc'),
  }
}

function normalizeArtifact(value: unknown) {
  const raw = asRecord(value)
  return {
    version: text(raw, 'version'),
    fileName: text(raw, 'fileName'),
    downloadUrl: nullableText(raw, 'downloadUrl'),
    sha256: text(raw, 'sha256'),
    sizeBytes: Math.max(0, finiteNumber(raw, 'sizeBytes')),
  }
}

export function normalizeRemoteMaintenanceManifest(value: unknown): RemoteMaintenanceManifest {
  const raw = asRecord(unwrapApiData(value as never))
  return {
    idServer: text(raw, 'idServer'),
    relayServer: text(raw, 'relayServer'),
    publicKey: text(raw, 'publicKey'),
    rustdesk: normalizeArtifact(raw.rustdesk),
    statusAgent: normalizeArtifact(raw.statusAgent),
  }
}

export async function getRemoteMaintenanceDevices(
  query: RemoteMaintenanceDeviceQuery,
  signal?: AbortSignal,
) {
  const response = await request.get<unknown>(`${BASE_PATH}/devices`, {
    params: { ...query },
    signal,
  })
  return normalizeRemoteMaintenanceDevicePage(response)
}

export async function getRemoteMaintenanceCredential(id: string, signal?: AbortSignal) {
  const response = await request.get<unknown>(`${BASE_PATH}/devices/${encodeURIComponent(id)}/credential`, {
    signal,
    cache: 'no-store',
  })
  const raw = asRecord(unwrapApiData(response as never))
  return typeof raw.password === 'string' ? raw.password : ''
}

export async function getRemoteMaintenanceManifest(signal?: AbortSignal) {
  const response = await request.get<unknown>(`${BASE_PATH}/manifest`, { signal })
  return normalizeRemoteMaintenanceManifest(response)
}

export function getRemoteMaintenanceArtifactUrl(kind: 'rustdesk' | 'status-agent') {
  return `${BASE_PATH}/artifacts/${kind}`
}
