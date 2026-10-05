import type { NativeAppUpdateApiClient } from "./native-app-update";

/**
 * Mobile 安卓原生「最低支持构建号」强制更新。
 *
 * 服务端按已安装 versionCode 判定 none / required；客户端只认 required，
 * 不自己比较策略版本。required 结果按「API 地址 + 已安装构建号」缓存，
 * 离线冷启动时继续拦截，换装新包（构建号变化）后缓存自动失效。
 */
export const ANDROID_NATIVE_REQUIRED_CACHE_KEY = "mobile-android-native-update:required:v1";

export type AndroidNativeUpdateDecision = Readonly<{
  state: "none" | "required";
  policyVersion: string;
  minimumSupportedBuildNumber: number | null;
  latestVersion: string | null;
  latestBuildNumber: number | null;
  releaseMessage: string | null;
}>;

export type AndroidNativeUpdateScope = Readonly<{
  apiBaseUrl: string;
  installedBuild: number;
}>;

export type AndroidNativeUpdateStorage = {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
};

export type AndroidNativeUpdateResolution = Readonly<{
  decision: AndroidNativeUpdateDecision | null;
  source: "server" | "cache" | "none";
}>;

type CachedRequiredRecord = Readonly<{
  apiBaseUrl: string;
  installedBuild: number;
  decision: AndroidNativeUpdateDecision;
}>;

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function asOptionalText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function asOptionalPositiveInteger(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : null;
}

/** Application.nativeBuildVersion 在安卓上是 versionCode 字符串；无法解析时不参与强制判定。 */
export function parseInstalledAndroidBuildNumber(value: string | null | undefined): number | null {
  if (typeof value !== "string" || !/^\d+$/.test(value.trim())) {
    return null;
  }
  const parsed = Number(value.trim());
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

/**
 * 校验服务端判定。required 还必须自洽：最低构建号与公开包构建号都要高于已安装构建号，
 * 否则按 none 处理，避免策略或数据异常把已是最新的设备锁死在拦截页。
 */
export function normalizeAndroidNativeUpdateDecision(
  payload: unknown,
  installedBuild: number,
): AndroidNativeUpdateDecision | null {
  const root = asRecord(payload);
  const body = asRecord(root?.data) ?? root;
  if (!body) {
    return null;
  }
  const state = body.state;
  if (state !== "none" && state !== "required") {
    return null;
  }
  const policyVersion = asOptionalText(body.policyVersion);
  if (!policyVersion) {
    return null;
  }
  const decision: AndroidNativeUpdateDecision = {
    state,
    policyVersion,
    minimumSupportedBuildNumber: asOptionalPositiveInteger(body.minimumSupportedBuildNumber),
    latestVersion: asOptionalText(body.latestVersion),
    latestBuildNumber: asOptionalPositiveInteger(body.latestBuildNumber),
    releaseMessage: asOptionalText(body.releaseMessage),
  };
  if (decision.state !== "required") {
    return decision;
  }
  const minimum = decision.minimumSupportedBuildNumber;
  const latest = decision.latestBuildNumber;
  if (minimum === null || latest === null || installedBuild >= minimum || latest < minimum) {
    return { ...decision, state: "none" };
  }
  return decision;
}

export async function fetchAndroidNativeUpdateDecision(
  apiClient: NativeAppUpdateApiClient,
  installedBuild: number,
): Promise<AndroidNativeUpdateDecision | null> {
  const response = await apiClient.get("/app-updates/mobile-android", {
    params: { build: String(installedBuild) },
    headers: { "X-Skip-Center-Log": "1" },
  });
  return normalizeAndroidNativeUpdateDecision(response.data, installedBuild);
}

export async function readCachedAndroidNativeRequiredDecision(
  storage: AndroidNativeUpdateStorage,
  scope: AndroidNativeUpdateScope,
): Promise<AndroidNativeUpdateDecision | null> {
  try {
    const raw = await storage.getString(ANDROID_NATIVE_REQUIRED_CACHE_KEY);
    if (!raw) {
      return null;
    }
    const record = asRecord(JSON.parse(raw)) as Partial<CachedRequiredRecord> | null;
    if (
      !record
      || record.apiBaseUrl !== scope.apiBaseUrl
      || record.installedBuild !== scope.installedBuild
    ) {
      return null;
    }
    const decision = normalizeAndroidNativeUpdateDecision(record.decision, scope.installedBuild);
    return decision?.state === "required" ? decision : null;
  } catch {
    return null;
  }
}

async function persistRequiredDecision(
  storage: AndroidNativeUpdateStorage,
  scope: AndroidNativeUpdateScope,
  decision: AndroidNativeUpdateDecision,
) {
  try {
    if (decision.state === "required") {
      const record: CachedRequiredRecord = {
        apiBaseUrl: scope.apiBaseUrl,
        installedBuild: scope.installedBuild,
        decision,
      };
      await storage.setString(ANDROID_NATIVE_REQUIRED_CACHE_KEY, JSON.stringify(record));
    } else {
      await storage.removeItem(ANDROID_NATIVE_REQUIRED_CACHE_KEY);
    }
  } catch (error) {
    // 缓存写失败不影响本次判定；内存中的 required 仍然生效。
    console.warn("[updates] persist Android native update state failed", error);
  }
}

/**
 * 服务端成功返回时以服务端为准并刷新缓存；请求失败或响应不合法时，
 * 只沿用同一作用域下缓存的 required，否则放行（fail-open）。
 */
export async function resolveAndroidNativeUpdateDecision(options: {
  fetchDecision: () => Promise<AndroidNativeUpdateDecision | null>;
  storage: AndroidNativeUpdateStorage;
  scope: AndroidNativeUpdateScope;
}): Promise<AndroidNativeUpdateResolution> {
  let serverDecision: AndroidNativeUpdateDecision | null = null;
  try {
    serverDecision = await options.fetchDecision();
  } catch (error) {
    console.warn("[updates] Android native update decision request failed", error);
  }
  if (serverDecision) {
    await persistRequiredDecision(options.storage, options.scope, serverDecision);
    return { decision: serverDecision, source: "server" };
  }
  const cached = await readCachedAndroidNativeRequiredDecision(options.storage, options.scope);
  return cached ? { decision: cached, source: "cache" } : { decision: null, source: "none" };
}

export function isAndroidNativeUpdateRequired(
  decision: AndroidNativeUpdateDecision | null | undefined,
) {
  return decision?.state === "required";
}

export function getAndroidNativeUpdateBoundaryMode(options: {
  enabled: boolean;
  decision: AndroidNativeUpdateDecision | null;
}): "content" | "required" {
  return options.enabled && isAndroidNativeUpdateRequired(options.decision) ? "required" : "content";
}

/** 强制原生更新只对正式包生效；preview/测试包与正式包共享 versionCode 计数，不能被正式策略拦截。 */
export function shouldCheckAndroidNativeRequiredUpdate(buildProfile: string | null | undefined) {
  return (buildProfile ?? "production").trim() === "production";
}
