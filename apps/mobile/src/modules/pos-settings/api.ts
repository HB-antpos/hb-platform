// 收银端设置接口：基础路径 /react/v1/pos-settings（apiClient 的 baseURL 已含 /api）。
// apiClient 的响应拦截器已把 ApiResponse<T> 解包成 T，success=false 会直接抛错。
// 这里再做一层防御式规范化：缺字段、null、字符串布尔都能得到类型稳定的结果，页面不用到处判空。
import type {
  PosSettingClientKind,
  PosSettingDefinition,
  PosSettingDeviceDetail,
  PosSettingDeviceStatus,
  PosSettingDeviceStatusKind,
  PosSettingEffective,
  PosSettingLayer,
  PosSettingSource,
  PosSettingStoreDetail,
  PosSettingStoreSummary,
  PosSettingsPatch,
} from "./types";

const BASE_PATH = "/react/v1/pos-settings";

type ApiRecord = Record<string, unknown>;

async function getApiClient() {
  // 延迟加载，让规范化与请求构造的纯逻辑测试不必初始化 React Native 运行时。
  const { apiClient } = await import("@/shared/api/client");
  return apiClient;
}

// ───────────────────────── 基础取值 ─────────────────────────

function asRecord(value: unknown): ApiRecord {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as ApiRecord) : {};
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function asString(value: unknown, fallback = ""): string {
  if (typeof value === "string") return value;
  if (typeof value === "number" && Number.isFinite(value)) return String(value);
  return fallback;
}

function asNullableString(value: unknown): string | null {
  const text = asString(value).trim();
  return text ? text : null;
}

/** 严格布尔：true / false / "true" / "false"，其余（含 null）一律 undefined，表示「没有值」。 */
function asOptionalBoolean(value: unknown): boolean | undefined {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "true") return true;
    if (normalized === "false") return false;
  }
  return undefined;
}

function asBoolean(value: unknown, fallback = false): boolean {
  return asOptionalBoolean(value) ?? fallback;
}

function asInt(value: unknown, fallback = 0): number {
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? Math.trunc(parsed) : fallback;
}

/**
 * 时间字段约定是 UTC ISO 字符串。DateTime 的 Kind 为 Unspecified 时 .NET 序列化会漏掉末尾的 Z，
 * 客户端按本地时间解析就会整体偏移数小时，所以没有时区标记的 ISO 时间一律补 Z。
 */
function asNullableUtcString(value: unknown): string | null {
  const text = asString(value).trim();
  if (!text) return null;
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(text) ? `${text}Z` : text;
}

/** 「已设置的项」字典：只保留布尔值，null / 其他类型的项视为没设置。 */
export function normalizeBooleanMap(raw: unknown): Record<string, boolean> {
  const result: Record<string, boolean> = {};
  for (const [key, value] of Object.entries(asRecord(raw))) {
    const parsed = asOptionalBoolean(value);
    if (key && parsed !== undefined) result[key] = parsed;
  }
  return result;
}

// ───────────────────────── 规范化 ─────────────────────────

export function normalizePosSettingDefinition(raw: unknown): PosSettingDefinition {
  const item = asRecord(raw);
  return {
    key: asString(item.key).trim(),
    clientKinds: asArray(item.clientKinds)
      .map((kind) => asString(kind).trim().toLowerCase())
      .filter(Boolean),
    defaultWhenUnset: asOptionalBoolean(item.defaultWhenUnset) ?? null,
    isNew: asBoolean(item.isNew),
  };
}

export function normalizePosSettingsCatalog(raw: unknown): PosSettingDefinition[] {
  const seen = new Set<string>();
  return asArray(asRecord(raw).definitions)
    .map(normalizePosSettingDefinition)
    .filter((definition) => {
      if (!definition.key || seen.has(definition.key)) return false;
      seen.add(definition.key);
      return true;
    });
}

export function normalizePosSettingLayer(raw: unknown): PosSettingLayer {
  const item = asRecord(raw);
  return {
    scope: asString(item.scope),
    scopeKey: asString(item.scopeKey),
    values: normalizeBooleanMap(item.values),
    updatedAtUtc: asNullableUtcString(item.updatedAtUtc),
    updatedByName: asNullableString(item.updatedByName),
  };
}

const SOURCES: readonly PosSettingSource[] = ["global", "store", "device", "default", "none"];

export function normalizePosSettingEffective(raw: unknown): PosSettingEffective {
  const item = asRecord(raw);
  const source = asString(item.source).trim().toLowerCase();
  const value = asOptionalBoolean(item.value) ?? null;
  const known = (SOURCES as readonly string[]).includes(source) ? (source as PosSettingSource) : null;
  // 来源缺失或未知：有值按「本层」之外无法判断，保守显示为 none；没有值一定是 none。
  return { value, source: value === null ? "none" : (known ?? "none") };
}

function normalizeEffectiveMap(raw: unknown): Record<string, PosSettingEffective> {
  const result: Record<string, PosSettingEffective> = {};
  for (const [key, value] of Object.entries(asRecord(raw))) {
    if (key) result[key] = normalizePosSettingEffective(value);
  }
  return result;
}

function asClientKind(value: unknown): PosSettingClientKind {
  const text = asString(value).trim().toLowerCase();
  return text === "wpf" || text === "ipad" || text === "handheld" ? text : "other";
}

/** 未知状态一律按 never（尚未同步）处理：最保守，不会把没确认的设备显示成已应用。 */
function asDeviceStatus(value: unknown): PosSettingDeviceStatusKind {
  const text = asString(value).trim().toLowerCase();
  return text === "applied" || text === "pending" ? text : "never";
}

export function normalizePosSettingDeviceStatus(raw: unknown): PosSettingDeviceStatus {
  const item = asRecord(raw);
  return {
    deviceCode: asString(item.deviceCode),
    storeCode: asString(item.storeCode),
    clientKind: asClientKind(item.clientKind),
    appVersion: asNullableString(item.appVersion),
    status: asDeviceStatus(item.status),
    appliedAtUtc: asNullableUtcString(item.appliedAtUtc),
    hasDeviceOverride: asBoolean(item.hasDeviceOverride),
    offlineOverrideCount: Math.max(0, asInt(item.offlineOverrideCount)),
    lastOfflineOverrideAtUtc: asNullableUtcString(item.lastOfflineOverrideAtUtc),
  };
}

export function normalizePosSettingDeviceList(raw: unknown): PosSettingDeviceStatus[] {
  return asArray(raw).map(normalizePosSettingDeviceStatus).filter((device) => device.deviceCode);
}

export function normalizePosSettingStoreSummary(raw: unknown): PosSettingStoreSummary {
  const item = asRecord(raw);
  return {
    storeCode: asString(item.storeCode),
    storeName: asString(item.storeName),
    overrides: normalizeBooleanMap(item.overrides),
    deviceOverrideCount: Math.max(0, asInt(item.deviceOverrideCount)),
    deviceTotal: Math.max(0, asInt(item.deviceTotal)),
    deviceApplied: Math.max(0, asInt(item.deviceApplied)),
  };
}

export function normalizePosSettingStoreList(raw: unknown): PosSettingStoreSummary[] {
  return asArray(raw).map(normalizePosSettingStoreSummary).filter((store) => store.storeCode);
}

export function normalizePosSettingStoreDetail(raw: unknown): PosSettingStoreDetail {
  const item = asRecord(raw);
  return {
    storeCode: asString(item.storeCode),
    storeName: asString(item.storeName),
    overrides: normalizeBooleanMap(item.overrides),
    effective: normalizeEffectiveMap(item.effective),
    devices: normalizePosSettingDeviceList(item.devices),
    updatedAtUtc: asNullableUtcString(item.updatedAtUtc),
    updatedByName: asNullableString(item.updatedByName),
  };
}

export function normalizePosSettingDeviceDetail(raw: unknown): PosSettingDeviceDetail {
  const item = asRecord(raw);
  return {
    status: normalizePosSettingDeviceStatus(item.status),
    storeName: asString(item.storeName),
    overrides: normalizeBooleanMap(item.overrides),
    effective: normalizeEffectiveMap(item.effective),
    reported: normalizeBooleanMap(item.reported),
  };
}

// ───────────────────────── 请求 ─────────────────────────

function path(...segments: string[]): string {
  return [BASE_PATH, ...segments.map((segment) => encodeURIComponent(segment))].join("/");
}

/** 保存请求体：{ values: { key: true | false | null } }。 */
export function buildSaveBody(patch: PosSettingsPatch): { values: PosSettingsPatch } {
  return { values: { ...patch } };
}

export async function fetchPosSettingsCatalog(): Promise<PosSettingDefinition[]> {
  const client = await getApiClient();
  const response = await client.get(path("catalog"));
  return normalizePosSettingsCatalog(response.data);
}

export async function fetchPosSettingsGlobal(): Promise<PosSettingLayer> {
  const client = await getApiClient();
  const response = await client.get(path("global"));
  return normalizePosSettingLayer(response.data);
}

export async function savePosSettingsGlobal(patch: PosSettingsPatch): Promise<PosSettingLayer> {
  const client = await getApiClient();
  const response = await client.put(path("global"), buildSaveBody(patch));
  return normalizePosSettingLayer(response.data);
}

export async function fetchPosSettingsStores(): Promise<PosSettingStoreSummary[]> {
  const client = await getApiClient();
  const response = await client.get(path("stores"));
  return normalizePosSettingStoreList(response.data);
}

export async function fetchPosSettingsStore(storeCode: string): Promise<PosSettingStoreDetail> {
  const client = await getApiClient();
  const response = await client.get(path("stores", storeCode));
  return normalizePosSettingStoreDetail(response.data);
}

export async function savePosSettingsStore(storeCode: string, patch: PosSettingsPatch): Promise<PosSettingStoreDetail> {
  const client = await getApiClient();
  const response = await client.put(path("stores", storeCode), buildSaveBody(patch));
  return normalizePosSettingStoreDetail(response.data);
}

export async function fetchPosSettingsDevices(storeCode: string): Promise<PosSettingDeviceStatus[]> {
  const client = await getApiClient();
  const response = await client.get(path("devices"), { params: { storeCode } });
  return normalizePosSettingDeviceList(response.data);
}

export async function fetchPosSettingsDevice(deviceCode: string): Promise<PosSettingDeviceDetail> {
  const client = await getApiClient();
  const response = await client.get(path("devices", deviceCode));
  return normalizePosSettingDeviceDetail(response.data);
}

export async function savePosSettingsDevice(deviceCode: string, patch: PosSettingsPatch): Promise<PosSettingDeviceDetail> {
  const client = await getApiClient();
  const response = await client.put(path("devices", deviceCode), buildSaveBody(patch));
  return normalizePosSettingDeviceDetail(response.data);
}
