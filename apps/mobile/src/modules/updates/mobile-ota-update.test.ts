import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { URL } from "node:url";
import test from "node:test";

import { createAppUpdateMutualExclusion } from "./app-update-mutual-exclusion";
import {
  MOBILE_OTA_REQUIRED_CACHE_KEY,
  checkMobileOtaUpdate,
  readCachedMobileOtaRequiredDecision,
  tryClaimMobileOtaOptionalPrompt,
  type MobileOtaUpdateContext,
  type MobileOtaUpdateStorage,
} from "./mobile-ota-update";

test("可选 OTA 提示在互斥通知同步重入时只认领一次", () => {
  const coordinator = createAppUpdateMutualExclusion({
    otaInitializationPending: false,
  });
  const targetRef: { current: string | null } = { current: null };
  let attempts = 0;
  let prompts = 0;

  const prompt = () => {
    attempts += 1;
    if (attempts > 10) {
      throw new Error("检测到可选 OTA 提示同步递归重入");
    }
    if (tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "optional-target",
      () => coordinator.tryOwnPrompt("ota"),
    )) {
      prompts += 1;
    }
  };
  const unsubscribe = coordinator.subscribe(prompt);

  try {
    assert.doesNotThrow(prompt);
    assert.equal(prompts, 1);
    assert.equal(targetRef.current, "optional-target");
  } finally {
    unsubscribe();
  }
});

test("可选 OTA 提示锁竞争失败会回滚目标并允许稍后重试", () => {
  const coordinator = createAppUpdateMutualExclusion({
    otaInitializationPending: false,
  });
  const targetRef: { current: string | null } = { current: "previous-target" };

  assert.equal(coordinator.tryOwnPrompt("native"), true);
  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "next-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    false,
  );
  assert.equal(targetRef.current, "previous-target");

  coordinator.releasePrompt("native");
  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "next-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    true,
  );
  assert.equal(targetRef.current, "next-target");
  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "next-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    false,
  );
});

test("可选 OTA 重启失败后保留同一目标去重，手动检查仍可重试", () => {
  const coordinator = createAppUpdateMutualExclusion({
    otaInitializationPending: false,
  });
  const targetRef: { current: string | null } = { current: null };

  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "failed-reload-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    true,
  );

  // applyDownloadedDecision 失败时释放 prompt 锁，但必须保留 targetRef。
  coordinator.releasePrompt("ota");
  assert.equal(targetRef.current, "failed-reload-target");
  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "failed-reload-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    false,
  );

  // checkManually 会清空去重目标，允许用户主动重试同一版本。
  targetRef.current = null;
  assert.equal(
    tryClaimMobileOtaOptionalPrompt(
      targetRef,
      "failed-reload-target",
      () => coordinator.tryOwnPrompt("ota"),
    ),
    true,
  );
});

test("hook 的 reload 失败路径静默：不弹任何失败提示并保留目标去重", () => {
  const source = readFileSync(
    new URL("./use-mobile-ota-update.ts", import.meta.url),
    "utf8",
  );
  const failurePath = source.match(
    /console\.warn\("\[updates\] apply controlled Mobile OTA failed"[\s\S]*?const downloadedStillReady/,
  )?.[0];
  assert.ok(failurePath);
  assert.match(failurePath, /releasePrompt\("ota"\)/);
  assert.doesNotMatch(failurePath, /Alert\.alert|mobileOtaRestartFailed/);
  assert.doesNotMatch(failurePath, /updateCheckFailedTitle|updateCheckFailedMessage/);
  assert.doesNotMatch(failurePath, /optionalPromptTargetRef\.current\s*=\s*null/);
  assert.match(source, /await port\.reload\(\)/);
});

test("OTA 全程后台静默：边界组件不占屏，hook 只在下载完成后提醒且失败不提示", () => {
  const boundary = readFileSync(new URL("./MobileOtaUpdateBoundary.tsx", import.meta.url), "utf8");
  const hook = readFileSync(new URL("./use-mobile-ota-update.ts", import.meta.url), "utf8");

  // 不再有「检查中」整屏或「必须更新」拦截面板：边界只提供手动检查上下文并直接渲染业务页面。
  assert.doesNotMatch(boundary, /ActivityIndicator|SafeAreaView|Button|accessibilityViewIsModal/);
  assert.match(boundary, /MobileOtaManualCheckContext\.Provider value=\{onManualCheck\}/);
  // 强制 OTA 也不再点亮拦截门禁，也没有面板专用的下载/重启入口。
  assert.doesNotMatch(hook, /setOtaRequiredGate\(\s*(decision|snapshotRef)/);
  assert.doesNotMatch(hook, /downloadRequired|restartRequired/);
  // 提醒只出现在下载成功之后，required 与 optional 共用同一个提醒。
  assert.match(hook, /patchSnapshot\(\{ downloading: false, downloaded: true, lastError: null \}\);\s*promptRestart\(decision\);/);
  // 下载失败/未就绪只记录状态，不弹窗。
  const notDownloaded = hook.match(/if \(result\.state !== "downloaded"\) \{[\s\S]*?return;\s*\}/)?.[0];
  assert.ok(notDownloaded);
  assert.doesNotMatch(notDownloaded, /Alert/);
});

const context: MobileOtaUpdateContext = {
  apiBaseUrl: "https://hotbargain.vip/api",
  appKey: "mobile",
  platform: "Android",
  clientChannel: "production",
  runtimeVersion: "1.0.2",
  currentUpdateId: "11111111-1111-4111-8111-111111111111",
  currentUpdateGroupId: null,
};

const requiredDecision = {
  state: "required" as const,
  policyVersion: "7",
  appKey: "mobile" as const,
  platform: "Android" as const,
  required: true,
  clientChannel: "production" as const,
  releaseChannel: "mobile-production-android-release-20260827-abcd",
  runtimeVersion: "1.0.2",
  updateId: "33333333-3333-4333-8333-333333333333",
  updateGroupId: "44444444-4444-4444-8444-444444444444",
  releaseMessage: "必须更新",
};

function createMemoryStorage(): MobileOtaUpdateStorage & { records: Map<string, unknown> } {
  const records = new Map<string, unknown>();
  return {
    records,
    async getObject<T>(key: string) {
      return (records.get(key) as T | undefined) ?? null;
    },
    async setObject(key: string, value: unknown) {
      records.set(key, structuredClone(value));
    },
    async removeItem(key: string) {
      records.delete(key);
    },
  };
}

test("可信 required 会缓存，离线或失形响应继续门禁", async () => {
  const storage = createMemoryStorage();
  const online = await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });
  assert.equal(online.source, "server");
  assert.equal(online.decision?.state, "required");
  assert.ok(storage.records.has(MOBILE_OTA_REQUIRED_CACHE_KEY));

  const offline = await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => {
      throw new Error("offline");
    },
  });
  assert.equal(offline.source, "cache");
  assert.equal(offline.decision?.updateId, requiredDecision.updateId);
});

test("首次离线不得凭空锁死", async () => {
  const outcome = await checkMobileOtaUpdate({
    context,
    storage: createMemoryStorage(),
    fetchDecision: async () => {
      throw new Error("timeout");
    },
  });
  assert.equal(outcome.source, "none");
  assert.equal(outcome.decision, null);
});

test("required 缓存严格绑定中心/app/channel/platform/runtime/版本/目标", async () => {
  const storage = createMemoryStorage();
  await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });

  for (const changed of [
    { ...context, apiBaseUrl: "https://example.invalid/api" },
    { ...context, clientChannel: "preview" as const },
    { ...context, platform: "iOS" as const },
    { ...context, runtimeVersion: "2.0.0" },
  ]) {
    assert.equal(
      await readCachedMobileOtaRequiredDecision(storage, changed),
      null,
    );
  }
});

test("本地缓存即使自洽也不能把任意 release channel 伪装成 required", async () => {
  const storage = createMemoryStorage();
  await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });
  const cached = structuredClone(
    storage.records.get(MOBILE_OTA_REQUIRED_CACHE_KEY) as {
      decision: typeof requiredDecision;
      targetIdentity: string;
    },
  );
  cached.decision.releaseChannel = "mobile-preview-ios-release-attacker";
  cached.targetIdentity = JSON.stringify([
    cached.decision.policyVersion,
    cached.decision.releaseChannel,
    cached.decision.runtimeVersion,
    cached.decision.updateId,
    cached.decision.updateGroupId,
  ]);
  storage.records.set(MOBILE_OTA_REQUIRED_CACHE_KEY, cached);

  assert.equal(
    await readCachedMobileOtaRequiredDecision(storage, context),
    null,
  );
});

test("可信 none 与确认已运行目标 Update ID 都清除旧 required", async () => {
  const storage = createMemoryStorage();
  await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });

  const none = await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => ({
      state: "none",
      policyVersion: "9",
      appKey: "mobile",
      platform: "Android",
      required: false,
      clientChannel: "production",
      releaseChannel: null,
      runtimeVersion: "1.0.2",
      updateId: null,
      updateGroupId: null,
      releaseMessage: null,
    }),
  });
  assert.equal(none.decision?.state, "none");
  assert.equal(none.decision?.policyVersion, "9");
  assert.equal(storage.records.has(MOBILE_OTA_REQUIRED_CACHE_KEY), false);

  await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });
  const alreadyRunning = await checkMobileOtaUpdate({
    context: { ...context, currentUpdateId: requiredDecision.updateId },
    storage,
    fetchDecision: async () => requiredDecision,
  });
  assert.equal(alreadyRunning.alreadyRunningTarget, true);
  assert.equal(alreadyRunning.decision?.state, "none");
  assert.equal(storage.records.has(MOBILE_OTA_REQUIRED_CACHE_KEY), false);
});

test("冷启动已运行缓存目标时即使离线也清缓存，不得再次形成 required 门禁", async () => {
  const storage = createMemoryStorage();
  await checkMobileOtaUpdate({
    context,
    storage,
    fetchDecision: async () => requiredDecision,
  });

  const runningContext = {
    ...context,
    currentUpdateId: requiredDecision.updateId.toUpperCase(),
  };
  assert.equal(
    await readCachedMobileOtaRequiredDecision(storage, runningContext),
    null,
  );
  assert.equal(storage.records.has(MOBILE_OTA_REQUIRED_CACHE_KEY), false);

  const offline = await checkMobileOtaUpdate({
    context: runningContext,
    storage,
    fetchDecision: async () => {
      throw new Error("offline after verified reload");
    },
  });
  assert.equal(offline.source, "none");
  assert.equal(offline.decision, null);
});
