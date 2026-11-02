export type RemoteOnlineStatus = 'online' | 'offline' | 'never'

export type RemoteServiceStatus =
  | 'notInstalled'
  | 'running'
  | 'stopped'
  | 'starting'
  | 'stopping'
  | 'checkFailed'

/** 设备上实际的 RustDesk 版本与中心下发版本的比对：一致 / 偏离 / 程序文件缺失 / 无法判断。 */
export type RemoteRustDeskVersionStatus = 'match' | 'drift' | 'missing' | 'unknown'

export interface RemoteMaintenanceDevice {
  id: string
  deviceRegistrationId: string
  storeCode: string
  deviceCode: string
  computerName: string
  rustdeskId: string
  clientVersion: string
  /** 心跳观察到的实际版本；null＝尚未上报，空串＝服务在但程序文件读不到 */
  observedRustDeskVersion: string | null
  rustDeskVersionStatus: RemoteRustDeskVersionStatus
  agentVersion: string
  onlineStatus: RemoteOnlineStatus
  serviceStatus: RemoteServiceStatus
  lastSeenAtUtc: string | null
  registeredAtUtc: string | null
  isStale: boolean
}

export interface RemoteMaintenanceDevicePage {
  items: RemoteMaintenanceDevice[]
  total: number
  page: number
  pageSize: number
  serverTimeUtc: string | null
}

export interface RemoteMaintenanceDeviceQuery {
  page: number
  pageSize: number
  keyword?: string
  storeCode?: string
  onlineStatus?: RemoteOnlineStatus
  serviceStatus?: RemoteServiceStatus
}

export interface RemoteMaintenanceArtifact {
  version: string
  fileName: string
  downloadUrl: string | null
  sha256: string
  sizeBytes: number
}

export interface RemoteMaintenanceManifest {
  idServer: string
  relayServer: string
  publicKey: string
  rustdesk: RemoteMaintenanceArtifact
  statusAgent: RemoteMaintenanceArtifact
}
