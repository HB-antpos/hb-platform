export type PaymentTerminalEnvironment = 'Production' | 'Sandbox'
export type LinklyConfigurationMode = 'Legacy' | 'Draft' | 'Active'
export type LinklyPairingState = 'Unpaired' | 'Ready' | 'Unknown' | 'NeedsRepair'

export interface PaymentTerminalEnvironmentStatusDto {
  environment: PaymentTerminalEnvironment
  configured: boolean
  enabled: boolean
  updatedAtUtc?: string | null
  updatedBy?: string | null
}

export interface PaymentTerminalStoreOptionDto {
  storeCode: string
  storeName: string
}

export interface LinklyCloudCredentialAdminDto {
  storeCode: string
  environment: PaymentTerminalEnvironment
  username?: string | null
  hasPassword: boolean
  updatedAtUtc?: string | null
  updatedBy?: string | null
}

export interface PaymentTerminalSettingsDto {
  // 全部门店共用的 Square Token 状态
  square: PaymentTerminalEnvironmentStatusDto[]
  // 当前所选门店的专用 Square Token 状态；configured=false 表示该店回落使用共用 Token
  storeSquare: PaymentTerminalEnvironmentStatusDto[]
  stores: PaymentTerminalStoreOptionDto[]
  selectedStoreCode?: string | null
  linkly: LinklyCloudCredentialAdminDto[]
}

export interface UpdateSquareTokenRequest {
  environment: PaymentTerminalEnvironment
  accessToken?: string
  clearToken: boolean
  // 不传 = 设置/清除共用 Token；传门店编码 = 只设置/清除该门店的专用 Token
  targetStoreCode?: string
}

export interface LinklyTerminalAdminDto {
  terminalId: string
  storeCode: string
  environment: PaymentTerminalEnvironment
  laneNo: number
  displayName: string
  usernameMasked: string
  hasPassword: boolean
  pairingState: LinklyPairingState
  lastHealthStatus?: string | null
  lastHealthAtUtc?: string | null
  selectedDeviceCount: number
  updatedAtUtc: string
  updatedBy?: string | null
}

export interface LinklyTerminalDeviceAdminDto {
  deviceCode: string
  deviceSystem: string
  enabled: boolean
  // 设备注册记录已不存在时仍保留选择，管理员必须显式解除，不能静默释放实体终端。
  deviceMissing: boolean
  terminalId?: string | null
  revision: number
}

export interface LinklyTerminalManagementDto {
  storeCode: string
  environment: PaymentTerminalEnvironment
  mode: LinklyConfigurationMode
  terminals: LinklyTerminalAdminDto[]
  devices: LinklyTerminalDeviceAdminDto[]
}

export interface CreateLinklyTerminalRequest {
  storeCode: string
  environment: PaymentTerminalEnvironment
  laneNo: number
  displayName: string
  username: string
  password: string
}

export interface UpdateLinklyTerminalRequest {
  storeCode: string
  environment: PaymentTerminalEnvironment
  laneNo: number
  displayName: string
  username?: string
  password?: string
}

export interface UpdateLinklyDeviceSelectionRequest {
  storeCode: string
  environment: PaymentTerminalEnvironment
  terminalId: string
  expectedRevision?: number
}

export interface DeleteLinklyDeviceSelectionRequest {
  storeCode: string
  environment: PaymentTerminalEnvironment
  expectedRevision: number
}

export interface ActivateLinklyConfigurationRequest {
  storeCode: string
  environment: PaymentTerminalEnvironment
}
