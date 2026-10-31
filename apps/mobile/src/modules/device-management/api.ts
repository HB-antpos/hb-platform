import { normalizeDeviceStatus } from "@/modules/device-management/status";
import type { PersistedDeviceSession } from "@/modules/device/types";
import type {
  AppDeviceHeartbeatPayload,
  AppDeviceStatus,
  AppDeviceStatusListResult,
  AppDeviceStatusQuery,
  AppDeviceStatusSummary,
  AppVersionDistribution,
  AppVersionState,
  DeviceManagementDevice,
  DeviceManagementListResult,
  DeviceManagementPagination,
  DeviceManagementQuery,
  DeviceVersionState,
  DeviceVersionSummary,
} from "@/modules/device-management/types";

async function getApiClient() {
  const { apiClient } = await import("@/shared/api/client");
  return apiClient;
}

function pick(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) {
      return raw[key];
    }
  }
  return undefined;
}

function asString(value: unknown): string | undefined {
  if (typeof value === "string") {
    const trimmed = value.trim();
    return trimmed || undefined;
  }

  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }

  return undefined;
}

function asNumber(value: unknown, fallback: number): number {
  const numericValue = typeof value === "string" && value.trim() ? Number(value) : value;
  return typeof numericValue === "number" && Number.isFinite(numericValue)
    ? numericValue
    : fallback;
}

function asBoolean(value: unknown, fallback = false): boolean {
  if (typeof value === "boolean") {
    return value;
  }

  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (["true", "1", "yes"].includes(normalized)) {
      return true;
    }
    if (["false", "0", "no"].includes(normalized)) {
      return false;
    }
  }

  return fallback;
}

function asOptionalNumber(value: unknown): number | undefined {
  const numericValue = typeof value === "string" && value.trim() ? Number(value) : value;
  return typeof numericValue === "number" && Number.isFinite(numericValue)
    ? numericValue
    : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function unwrapListPayload(payload: unknown): Record<string, unknown> {
  const root = asRecord(payload);
  if (!root) {
    return {};
  }

  const data = asRecord(root.data ?? root.Data);
  return data ?? root;
}

const DEVICE_VERSION_STATES: readonly DeviceVersionState[] = ["latest", "behind", "ahead", "notTargeted", "unreported"];
const APP_VERSION_STATES: readonly AppVersionState[] = ["latest", "behind", "unreported"];

/** 后端状态串只认已知取值，未知值丢弃（界面按「无状态」显示），避免新版后端值污染界面。 */
function asEnum<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  const text = asString(value);
  return text ? allowed.find((item) => item.toLowerCase() === text.toLowerCase()) : undefined;
}

function normalizeDevice(raw: unknown): DeviceManagementDevice | null {
  const record = asRecord(raw);
  if (!record) {
    return null;
  }

  const rawRegistrationId = pick(record, "id", "Id", "deviceId", "DeviceId");
  const registrationId = asOptionalNumber(rawRegistrationId);
  const id = asString(rawRegistrationId) ?? asString(pick(record, "hardwareId", "HardwareId"));

  if (!id) {
    return null;
  }

  return {
    id,
    registrationId: registrationId && Number.isSafeInteger(registrationId) && registrationId > 0 ? registrationId : undefined,
    hardwareId: asString(pick(record, "hardwareId", "HardwareId")) ?? "",
    systemDeviceNumber: asString(pick(record, "systemDeviceNumber", "SystemDeviceNumber")),
    deviceNumber: asString(pick(record, "deviceNumber", "DeviceNumber")),
    deviceType: asString(pick(record, "deviceType", "DeviceType")),
    deviceSystem: asString(pick(record, "deviceSystem", "DeviceSystem")),
    deviceName: asString(pick(record, "deviceName", "DeviceName", "name", "Name")),
    storeCode: asString(pick(record, "storeCode", "StoreCode")),
    storeName: asString(pick(record, "storeName", "StoreName")),
    status: normalizeDeviceStatus(pick(record, "status", "Status")),
    appVersion: asString(pick(record, "appVersion", "AppVersion")),
    targetAppVersion: asString(pick(record, "targetAppVersion", "TargetAppVersion")),
    versionState: asEnum(pick(record, "versionState", "VersionState"), DEVICE_VERSION_STATES),
    platform: asString(pick(record, "platform", "Platform")),
    lastSeenAt: asString(pick(record, "lastSeenAt", "LastSeenAt", "lastOnlineAt", "LastOnlineAt")),
    createdAt: asString(pick(record, "createdAt", "CreatedAt")),
    updatedAt: asString(pick(record, "updatedAt", "UpdatedAt", "lastModified", "LastModified")),
    allowTransactions: asBoolean(pick(record, "allowTransactions", "AllowTransactions", "是否允许交易"), true),
    remark: asString(pick(record, "remark", "Remark", "remarks", "Remarks", "备注")) ?? null,
    isOnline: asBoolean(pick(record, "isOnline", "IsOnline", "是否在线")),
    lastHeartbeatAt: asString(pick(record, "lastHeartbeatAt", "LastHeartbeatAt", "最后心跳时间")) ?? null,
    currentCashierName: asString(pick(record, "currentCashierName", "CurrentCashierName", "当前收银员姓名")) ?? null,
    cashierLoginAt: asString(pick(record, "cashierLoginAt", "CashierLoginAt", "收银员登录时间")) ?? null,
    createdBy: asString(pick(record, "createdBy", "CreatedBy", "创建人")) ?? null,
    lastModifiedBy: asString(pick(record, "lastModifiedBy", "LastModifiedBy", "最后修改人")) ?? null,
  };
}

function normalizePagination(raw: unknown): DeviceManagementPagination {
  const record = asRecord(raw) ?? {};
  const pageNumber = asNumber(pick(record, "pageNumber", "PageNumber", "page", "Page"), 1);
  const pageSize = asNumber(pick(record, "pageSize", "PageSize", "limit", "Limit"), 20);
  const totalCount = asNumber(pick(record, "totalCount", "TotalCount", "total", "Total"), 0);
  const totalPages = asNumber(
    pick(record, "totalPages", "TotalPages"),
    pageSize > 0 ? Math.ceil(totalCount / pageSize) : 0
  );

  return {
    pageNumber,
    pageSize,
    totalCount,
    totalPages,
  };
}

export function normalizeDeviceManagementListResponse(payload: unknown): DeviceManagementListResult {
  const listPayload = unwrapListPayload(payload);
  const devicesPayload = pick(listPayload, "devices", "Devices");
  const devices = Array.isArray(devicesPayload)
    ? devicesPayload.map(normalizeDevice).filter((item): item is DeviceManagementDevice => Boolean(item))
    : [];

  return {
    devices,
    pagination: normalizePagination(pick(listPayload, "pagination", "Pagination")),
  };
}

/** 版本多选合并成逗号串：去空白、去重、保持顺序；空数组不传，后端按「不过滤」处理。 */
export function joinVersionList(versions?: readonly string[] | null) {
  const unique = Array.from(new Set((versions ?? []).map((item) => item.trim()).filter(Boolean)));
  return unique.length > 0 ? unique.join(",") : undefined;
}

function stateParam<T extends string>(value?: T | "all" | null) {
  // all 只存在于前端 UI，不能传给后端的状态枚举。
  return value && value !== "all" ? value : undefined;
}

/** 设备注册列表与版本汇总共用的筛选条件（不含分页和版本条件）。 */
function buildRegistrationScopeParams(query: DeviceManagementQuery) {
  return {
    keyword: query.keyword?.trim() || undefined,
    storeCode: query.storeCode || undefined,
    status: query.status ?? undefined,
    deviceSystem: query.deviceSystem?.trim() || undefined,
    deviceType: query.deviceType?.trim() || undefined,
  };
}

export function buildListParams(query: DeviceManagementQuery) {
  return {
    page: query.pageNumber,
    pageSize: query.pageSize,
    ...buildRegistrationScopeParams(query),
    appVersions: joinVersionList(query.appVersions),
    versionState: stateParam(query.versionState),
  };
}

export async function fetchDeviceManagementDevices(
  query: DeviceManagementQuery = {}
): Promise<DeviceManagementListResult> {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/mobile/device-management/paged", { params: buildListParams(query) });
  return normalizeDeviceManagementListResponse(response.data);
}

export function normalizeDeviceVersionSummary(payload: unknown): DeviceVersionSummary {
  const record = unwrapListPayload(payload);
  const itemsPayload = pick(record, "items", "Items");
  const states = asRecord(pick(record, "states", "States")) ?? {};
  const count = (key: string) => asNumber(pick(states, key, key[0].toUpperCase() + key.slice(1)), 0);

  return {
    targetVersion: asString(pick(record, "targetVersion", "TargetVersion")),
    targetScope: asString(pick(record, "targetScope", "TargetScope")),
    total: asNumber(pick(record, "total", "Total"), 0),
    items: Array.isArray(itemsPayload)
      ? itemsPayload.flatMap((raw) => {
          const item = asRecord(raw);
          return item
            ? [{
                appVersion: asString(pick(item, "appVersion", "AppVersion")) ?? null,
                total: asNumber(pick(item, "total", "Total"), 0),
              }]
            : [];
        })
      : [],
    states: {
      latest: count("latest"),
      behind: count("behind"),
      ahead: count("ahead"),
      notTargeted: count("notTargeted"),
      unreported: count("unreported"),
      notApplicable: count("notApplicable"),
    },
  };
}

export async function fetchDeviceVersionSummary(
  query: Omit<DeviceManagementQuery, "pageNumber" | "pageSize" | "appVersions" | "versionState"> = {}
): Promise<DeviceVersionSummary> {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/mobile/device-management/version-summary", {
    params: buildRegistrationScopeParams(query),
  });
  return normalizeDeviceVersionSummary(response.data);
}

async function postDeviceAction(id: string | number, action: "activate" | "disable" | "lock") {
  const numericId = typeof id === "number" ? id : Number(id);
  if (!Number.isSafeInteger(numericId) || numericId <= 0) {
    throw new Error("DEVICE_REGISTRATION_ID_INVALID");
  }
  const apiClient = await getApiClient();
  await apiClient.post(`/mobile/device-management/${numericId}/${action}`);
}

export function activateDevice(id: string | number) {
  return postDeviceAction(id, "activate");
}

export function disableDevice(id: string | number) {
  return postDeviceAction(id, "disable");
}

export function lockDevice(id: string | number) {
  return postDeviceAction(id, "lock");
}

function normalizeAppDeviceStatus(raw: unknown): AppDeviceStatus | null {
  const record = asRecord(raw);
  if (!record) {
    return null;
  }

  const hardwareId = asString(pick(record, "hardwareId", "HardwareId"));
  const id = asString(pick(record, "id", "Id")) ?? hardwareId;
  if (!id || !hardwareId) {
    return null;
  }

  return {
    id,
    hardwareId,
    systemDeviceNumber: asString(pick(record, "systemDeviceNumber", "SystemDeviceNumber")),
    deviceSystem: asString(pick(record, "deviceSystem", "DeviceSystem")),
    platform: asString(pick(record, "platform", "Platform")),
    storeCode: asString(pick(record, "storeCode", "StoreCode")),
    storeName: asString(pick(record, "storeName", "StoreName")),
    appVersion: asString(pick(record, "appVersion", "AppVersion")),
    appBuildVersion: asString(pick(record, "appBuildVersion", "AppBuildVersion")),
    runtimeVersion: asString(pick(record, "runtimeVersion", "RuntimeVersion")),
    channel: asString(pick(record, "channel", "Channel")),
    updateId: asString(pick(record, "updateId", "UpdateId")),
    updateSource: asString(pick(record, "updateSource", "UpdateSource")),
    lastSeenAtUtc: asString(pick(record, "lastSeenAtUtc", "LastSeenAtUtc", "lastSeenAt", "LastSeenAt")),
    isOnline: asBoolean(pick(record, "isOnline", "IsOnline")),
    lastAuthMode: asString(pick(record, "lastAuthMode", "LastAuthMode")),
    lastSeenUserGuid: asString(pick(record, "lastSeenUserGuid", "LastSeenUserGuid")),
    lastSeenUsername: asString(pick(record, "lastSeenUsername", "LastSeenUsername")),
    lastSeenUserFullName: asString(pick(record, "lastSeenUserFullName", "LastSeenUserFullName")),
    registeredDeviceId: asOptionalNumber(pick(record, "registeredDeviceId", "RegisteredDeviceId")),
    latestAppVersion: asString(pick(record, "latestAppVersion", "LatestAppVersion")),
    latestAppBuildVersion: asString(pick(record, "latestAppBuildVersion", "LatestAppBuildVersion")),
    versionState: asEnum(pick(record, "versionState", "VersionState"), APP_VERSION_STATES),
  };
}

export function normalizeAppDeviceStatusListResponse(payload: unknown): AppDeviceStatusListResult {
  const listPayload = unwrapListPayload(payload);
  const devicesPayload = pick(listPayload, "items", "Items", "devices", "Devices", "data", "Data");
  const devices = Array.isArray(devicesPayload)
    ? devicesPayload
        .map(normalizeAppDeviceStatus)
        .filter((item): item is AppDeviceStatus => Boolean(item))
    : [];

  return {
    devices,
    pagination: normalizePagination(listPayload),
  };
}

export function normalizeAppDeviceStatusSummary(payload: unknown): AppDeviceStatusSummary {
  const record = unwrapListPayload(payload);
  return {
    total: asNumber(pick(record, "total", "Total"), 0),
    online: asNumber(pick(record, "online", "Online"), 0),
    offline: asNumber(pick(record, "offline", "Offline"), 0),
    android: asNumber(pick(record, "android", "Android"), 0),
    ios: asNumber(pick(record, "ios", "Ios", "iOS", "IOS"), 0),
    unknownSystem: asNumber(pick(record, "unknownSystem", "UnknownSystem"), 0),
  };
}

/** App 列表、汇总、版本分布共用的范围条件；版本分布只能用其中的分店 / 系统 / 关键词。 */
function buildAppDeviceScopeParams(query: AppDeviceStatusQuery) {
  return {
    keyword: query.keyword?.trim() || undefined,
    storeCode: query.storeCode || undefined,
    deviceSystem: query.deviceSystem?.trim() || undefined,
  };
}

function buildAppVersionParams(query: AppDeviceStatusQuery) {
  // 只在有值时才带键：没有版本条件时请求参数与改动前完全一致。
  const params: { appVersion?: string; appBuildVersion?: string; versionState?: AppVersionState } = {};
  const appVersion = query.appVersion?.trim();
  if (appVersion) {
    params.appVersion = appVersion;
    // 构建号只有和版本号一起才有意义，单独传没有对应的版本行。
    const appBuildVersion = query.appBuildVersion?.trim();
    if (appBuildVersion) params.appBuildVersion = appBuildVersion;
  }
  const versionState = stateParam(query.versionState);
  if (versionState) params.versionState = versionState;
  return params;
}

export function buildAppDeviceStatusListParams(query: AppDeviceStatusQuery = {}) {
  return {
    page: query.pageNumber,
    pageSize: query.pageSize,
    ...buildAppDeviceScopeParams(query),
    onlineState: query.onlineState && query.onlineState !== "all" ? query.onlineState : undefined,
    ...buildAppVersionParams(query),
  };
}

export async function fetchAppDeviceStatuses(
  query: AppDeviceStatusQuery = {}
): Promise<AppDeviceStatusListResult> {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/mobile/app-device-status/paged", {
    params: buildAppDeviceStatusListParams(query),
  });
  return normalizeAppDeviceStatusListResponse(response.data);
}

export async function fetchAppDeviceStatusSummary(
  query: Omit<AppDeviceStatusQuery, "onlineState" | "pageNumber" | "pageSize"> = {}
): Promise<AppDeviceStatusSummary> {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/mobile/app-device-status/summary", {
    // 汇总条要和下方列表同一口径，所以带上版本条件（在线状态 / 分页除外）。
    params: { ...buildAppDeviceScopeParams(query), ...buildAppVersionParams(query) },
  });
  return normalizeAppDeviceStatusSummary(response.data);
}

export function normalizeAppVersionDistribution(payload: unknown): AppVersionDistribution {
  const record = unwrapListPayload(payload);
  const itemsPayload = pick(record, "items", "Items");
  return {
    total: asNumber(pick(record, "total", "Total"), 0),
    items: Array.isArray(itemsPayload)
      ? itemsPayload.flatMap((raw) => {
          const item = asRecord(raw);
          return item
            ? [{
                deviceSystem: asString(pick(item, "deviceSystem", "DeviceSystem")),
                appVersion: asString(pick(item, "appVersion", "AppVersion")),
                appBuildVersion: asString(pick(item, "appBuildVersion", "AppBuildVersion")),
                total: asNumber(pick(item, "total", "Total"), 0),
                online: asNumber(pick(item, "online", "Online"), 0),
              }]
            : [];
        })
      : [],
  };
}

/** 按系统 + 版本 + 构建号统计设备数；只受分店 / 系统 / 关键词约束，点选版本后其余版本仍可切换。 */
export async function fetchAppVersionDistribution(
  query: Pick<AppDeviceStatusQuery, "keyword" | "storeCode" | "deviceSystem"> = {}
): Promise<AppVersionDistribution> {
  const apiClient = await getApiClient();
  const response = await apiClient.get("/mobile/app-device-status/version-distribution", {
    params: buildAppDeviceScopeParams(query),
  });
  return normalizeAppVersionDistribution(response.data);
}

export async function sendAppDeviceHeartbeat(
  payload: AppDeviceHeartbeatPayload,
  options?: { deviceSession?: PersistedDeviceSession | null }
) {
  const apiClient = await getApiClient();
  const deviceSession = options?.deviceSession;
  await apiClient.post("/mobile/app-device-status/heartbeat", payload, {
    headers:
      deviceSession?.hardwareId && deviceSession.authCode
        ? {
            "X-Device-Id": deviceSession.hardwareId,
            "X-Auth-Code": deviceSession.authCode,
          }
        : undefined,
  });
}
