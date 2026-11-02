import type { DeviceStatus } from "@/modules/device-management/status";

/** 设备注册列表（Windows POS）相对 WPF 更新策略的版本状态。 */
export type DeviceVersionState = "latest" | "behind" | "ahead" | "notTargeted" | "unreported";
/** App 设备相对「该系统在用最高版本」的原生包状态。 */
export type AppVersionState = "latest" | "behind" | "unreported";
export type DeviceVersionStateFilter<T extends string> = T | "all";

export interface DeviceManagementQuery {
  pageNumber?: number;
  pageSize?: number;
  keyword?: string;
  storeCode?: string | null;
  status?: DeviceStatus | number | null;
  deviceSystem?: string | null;
  deviceType?: string | null;
  /** 指定版本（多选，规范化后的 1.0.96 形式）；空数组表示不按版本过滤。 */
  appVersions?: string[] | null;
  versionState?: DeviceVersionStateFilter<DeviceVersionState> | null;
}

export interface DeviceManagementDevice {
  id: string;
  /** 后端设备注册表的整数主键；硬件 ID 仅用于展示，绝不能用于写入路由。 */
  registrationId?: number;
  hardwareId: string;
  systemDeviceNumber?: string;
  deviceNumber?: string;
  deviceType?: string;
  deviceSystem?: string;
  deviceName?: string;
  storeCode?: string;
  storeName?: string;
  status: DeviceStatus;
  allowTransactions?: boolean;
  remark?: string | null;
  isOnline?: boolean;
  lastHeartbeatAt?: string | null;
  currentCashierName?: string | null;
  cashierLoginAt?: string | null;
  createdBy?: string | null;
  lastModifiedBy?: string | null;
  appVersion?: string;
  /** WPF 更新策略当前目标版本；仅 Windows POS 且有策略时才有。 */
  targetAppVersion?: string;
  versionState?: DeviceVersionState;
  platform?: string;
  lastSeenAt?: string;
  createdAt?: string;
  updatedAt?: string;
}

export interface DeviceManagementListItem extends DeviceManagementDevice {}

export interface DeviceManagementPagination {
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface DeviceManagementListResult {
  devices: DeviceManagementDevice[];
  pagination: DeviceManagementPagination;
}

export interface DeviceManagementListViewResult {
  items: DeviceManagementListItem[];
  total: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
}

export interface DeviceManagementActionPayload {
  id: string | number;
  hardwareId?: string;
  systemDeviceNumber?: string;
  storeCode?: string;
}

export type AppDeviceOnlineState = "all" | "online" | "offline";

export interface AppDeviceStatusQuery {
  pageNumber?: number;
  pageSize?: number;
  keyword?: string;
  storeCode?: string | null;
  deviceSystem?: string | null;
  onlineState?: AppDeviceOnlineState | null;
  /** 原生版本号 + 构建号精确匹配（后端既有参数），与 deviceSystem 搭配使用。 */
  appVersion?: string | null;
  appBuildVersion?: string | null;
  versionState?: DeviceVersionStateFilter<AppVersionState> | null;
}

/** 设备注册列表的版本汇总（策略目标版本、各版本设备数、各状态设备数）。 */
export interface DeviceVersionSummary {
  targetVersion?: string;
  targetScope?: string;
  total: number;
  /** appVersion 为 null 表示未上报版本的 Windows POS。 */
  items: { appVersion: string | null; total: number }[];
  states: Record<DeviceVersionState | "notApplicable", number>;
}

export interface AppVersionDistributionItem {
  deviceSystem?: string;
  appVersion?: string;
  appBuildVersion?: string;
  total: number;
  online: number;
}

export interface AppVersionDistribution {
  total: number;
  items: AppVersionDistributionItem[];
}

export interface AppDeviceStatus {
  id: string;
  hardwareId: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  platform?: string;
  storeCode?: string;
  /** 后端按分店代码回填；旧版后端没有此字段，由页面用已加载的分店列表兜底。 */
  storeName?: string;
  appVersion?: string;
  appBuildVersion?: string;
  runtimeVersion?: string;
  channel?: string;
  updateId?: string;
  updateSource?: string;
  lastSeenAtUtc?: string;
  isOnline: boolean;
  lastAuthMode?: string;
  lastSeenUserGuid?: string;
  lastSeenUsername?: string;
  lastSeenUserFullName?: string;
  registeredDeviceId?: number;
  /** 该设备所在系统当前在用的最高原生版本（后端按全部设备计算）。 */
  latestAppVersion?: string;
  latestAppBuildVersion?: string;
  versionState?: AppVersionState;
}

export interface AppDeviceStatusSummary {
  total: number;
  online: number;
  offline: number;
  android: number;
  ios: number;
  unknownSystem: number;
}

export interface AppDeviceStatusListResult {
  devices: AppDeviceStatus[];
  pagination: DeviceManagementPagination;
}

export interface AppDeviceHeartbeatPayload {
  hardwareId: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  platform?: string;
  storeCode?: string;
  appVersion?: string;
  appBuildVersion?: string;
  runtimeVersion?: string;
  channel?: string;
  updateId?: string;
  updateSource?: string;
}
