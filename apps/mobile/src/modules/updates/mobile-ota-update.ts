export const MOBILE_OTA_REQUIRED_CACHE_KEY = "mobile-ota-update:required:v1";

export type MobileOtaPlatform = "Android" | "iOS";
export type MobileOtaClientChannel = "production" | "preview";
export type MobileOtaUpdateState = "none" | "optional" | "required";
export type MobileOtaManualCheckResult = Readonly<{
  status: "update-ready" | "required" | "not-available" | "disabled" | "failed";
}>;

export function tryClaimMobileOtaOptionalPrompt(
  targetRef: { current: string | null },
  identity: string,
  tryOwnPrompt: () => boolean,
) {
  if (targetRef.current === identity) {
    return false;
  }

  const previousIdentity = targetRef.current;
  // 提示锁会同步通知订阅者；必须先登记目标，避免回调再次认领同一提示。
  targetRef.current = identity;
  if (!tryOwnPrompt()) {
    targetRef.current = previousIdentity;
    return false;
  }
  return true;
}

export type MobileOtaUpdateDecision = Readonly<{
  state: MobileOtaUpdateState;
  policyVersion: string;
  appKey: "mobile";
  platform: MobileOtaPlatform;
  required: boolean;
  clientChannel: MobileOtaClientChannel;
  releaseChannel: string | null;
  runtimeVersion: string;
  updateId: string | null;
  updateGroupId: string | null;
  releaseMessage: string | null;
}>;

export type MobileOtaUpdateContext = Readonly<{
  apiBaseUrl: string;
  appKey: "mobile";
  platform: MobileOtaPlatform;
  clientChannel: MobileOtaClientChannel;
  runtimeVersion: string;
  currentUpdateId: string | null;
  currentUpdateGroupId: string | null;
}>;

export interface MobileOtaUpdateStorage {
  getObject<T>(key: string): Promise<T | null>;
  setObject(key: string, value: unknown): Promise<void>;
  removeItem(key: string): Promise<void>;
}

export type MobileOtaUpdateCheckOutcome = Readonly<{
  source: "server" | "cache" | "memory" | "none";
  decision: MobileOtaUpdateDecision | null;
  alreadyRunningTarget: boolean;
  error?: unknown;
  storageError?: unknown;
}>;

type MobileOtaRequiredCacheRecord = Readonly<{
  schemaVersion: 1;
  scope: string;
  targetIdentity: string;
  decision: MobileOtaUpdateDecision;
}>;

function normalizedBaseUrl(value: string) {
  return value.trim().replace(/\/+$/, "");
}

function buildContextScope(context: MobileOtaUpdateContext) {
  // required 的离线延续必须绑定固定更新中心和完整客户端兼容范围。
  return JSON.stringify([
    normalizedBaseUrl(context.apiBaseUrl),
    context.appKey,
    context.clientChannel,
    context.platform,
    context.runtimeVersion.trim(),
  ]);
}

function buildTargetIdentity(decision: MobileOtaUpdateDecision) {
  return JSON.stringify([
    decision.policyVersion,
    decision.releaseChannel,
    decision.runtimeVersion,
    decision.updateId,
    decision.updateGroupId,
  ]);
}

function noneDecision(context: MobileOtaUpdateContext): MobileOtaUpdateDecision {
  return Object.freeze({
    state: "none",
    policyVersion: "none",
    appKey: "mobile",
    platform: context.platform,
    required: false,
    clientChannel: context.clientChannel,
    releaseChannel: null,
    runtimeVersion: context.runtimeVersion,
    updateId: null,
    updateGroupId: null,
    releaseMessage: null,
  });
}

function isRequiredDecisionForContext(
  value: unknown,
  context: MobileOtaUpdateContext,
): value is MobileOtaUpdateDecision {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return false;
  }
  const decision = value as Partial<MobileOtaUpdateDecision>;
  const expectedReleaseChannelPrefix =
    `mobile-${context.clientChannel}-${context.platform.toLowerCase()}-release-`;
  return (
    decision.state === "required"
    && decision.required === true
    && decision.appKey === context.appKey
    && decision.platform === context.platform
    && decision.clientChannel === context.clientChannel
    && decision.runtimeVersion === context.runtimeVersion
    && typeof decision.policyVersion === "string"
    && /^[1-9]\d*$/.test(decision.policyVersion)
    && typeof decision.releaseChannel === "string"
    && decision.releaseChannel.startsWith(expectedReleaseChannelPrefix)
    && decision.releaseChannel.length > expectedReleaseChannelPrefix.length
    && typeof decision.updateId === "string"
    && decision.updateId.length > 0
    && typeof decision.updateGroupId === "string"
    && decision.updateGroupId.length > 0
    && (decision.releaseMessage === null || typeof decision.releaseMessage === "string")
  );
}

export async function readCachedMobileOtaRequiredDecision(
  storage: MobileOtaUpdateStorage,
  context: MobileOtaUpdateContext,
) {
  try {
    const cached = await storage.getObject<MobileOtaRequiredCacheRecord>(
      MOBILE_OTA_REQUIRED_CACHE_KEY,
    );
    if (
      !cached
      || cached.schemaVersion !== 1
      || cached.scope !== buildContextScope(context)
      || !isRequiredDecisionForContext(cached.decision, context)
      || cached.targetIdentity !== buildTargetIdentity(cached.decision)
    ) {
      return null;
    }
    if (
      context.currentUpdateId
      && cached.decision.updateId
      && cached.decision.updateId.toLowerCase()
        === context.currentUpdateId.toLowerCase()
    ) {
      // reload 成功后的首次启动可能离线；当前运行 Update ID 本身足以证明目标已采用。
      await storage.removeItem(MOBILE_OTA_REQUIRED_CACHE_KEY);
      return null;
    }
    return Object.freeze({ ...cached.decision });
  } catch {
    // 损坏或不可读缓存不能凭空形成强制门禁。
    return null;
  }
}

async function saveRequiredDecision(
  storage: MobileOtaUpdateStorage,
  context: MobileOtaUpdateContext,
  decision: MobileOtaUpdateDecision,
) {
  const record: MobileOtaRequiredCacheRecord = Object.freeze({
    schemaVersion: 1,
    scope: buildContextScope(context),
    targetIdentity: buildTargetIdentity(decision),
    decision,
  });
  await storage.setObject(MOBILE_OTA_REQUIRED_CACHE_KEY, record);
}

export async function checkMobileOtaUpdate(input: {
  context: MobileOtaUpdateContext;
  storage: MobileOtaUpdateStorage;
  fetchDecision: (signal?: AbortSignal) => Promise<MobileOtaUpdateDecision>;
  signal?: AbortSignal;
  memoryRequiredDecision?: MobileOtaUpdateDecision | null;
}): Promise<MobileOtaUpdateCheckOutcome> {
  let decision: MobileOtaUpdateDecision;
  try {
    decision = await input.fetchDecision(input.signal);
    if (input.signal?.aborted) {
      throw Object.assign(new Error("Mobile OTA check was aborted"), {
        name: "AbortError",
      });
    }
  } catch (error) {
    if (input.signal?.aborted) {
      throw error;
    }
    const cached = await readCachedMobileOtaRequiredDecision(
      input.storage,
      input.context,
    );
    const memory = isRequiredDecisionForContext(
      input.memoryRequiredDecision,
      input.context,
    )
      ? input.memoryRequiredDecision
      : null;
    const fallback = cached ?? memory;
    return Object.freeze({
      source: cached ? "cache" : memory ? "memory" : "none",
      decision: fallback,
      alreadyRunningTarget: false,
      error,
    });
  }

  const alreadyRunningTarget = Boolean(
    decision.state !== "none"
    && decision.updateId
    && input.context.currentUpdateId
    && decision.updateId.toLowerCase() === input.context.currentUpdateId.toLowerCase(),
  );

  let storageError: unknown;
  try {
    if (decision.state === "required" && !alreadyRunningTarget) {
      await saveRequiredDecision(input.storage, input.context, decision);
    } else {
      // 可信 none、optional 或已运行目标都能解除同 scope 的旧 required。
      await input.storage.removeItem(MOBILE_OTA_REQUIRED_CACHE_KEY);
    }
  } catch (error) {
    // 当前可信服务端 required 仍然生效；仅离线延续能力受存储故障影响。
    storageError = error;
  }

  return Object.freeze({
    source: "server",
    decision: alreadyRunningTarget ? noneDecision(input.context) : decision,
    alreadyRunningTarget,
    storageError,
  });
}
