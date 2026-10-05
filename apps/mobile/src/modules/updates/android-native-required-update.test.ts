import assert from "node:assert/strict";
import test from "node:test";

import {
  ANDROID_NATIVE_REQUIRED_CACHE_KEY,
  fetchAndroidNativeUpdateDecision,
  getAndroidNativeUpdateBoundaryMode,
  normalizeAndroidNativeUpdateDecision,
  parseInstalledAndroidBuildNumber,
  readCachedAndroidNativeRequiredDecision,
  resolveAndroidNativeUpdateDecision,
  shouldCheckAndroidNativeRequiredUpdate,
  type AndroidNativeUpdateDecision,
  type AndroidNativeUpdateStorage,
} from "./android-native-required-update";

const REQUIRED_PAYLOAD = {
  success: true,
  data: {
    state: "required",
    policyVersion: "3",
    minimumSupportedBuildNumber: 63,
    latestVersion: "1.0.10",
    latestBuildNumber: 63,
    releaseMessage: "请安装 1.0.10",
  },
};

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  const storage: AndroidNativeUpdateStorage = {
    async getString(key) {
      return values.get(key) ?? null;
    },
    async setString(key, value) {
      values.set(key, value);
    },
    async removeItem(key) {
      values.delete(key);
    },
  };
  return { storage, values };
}

const scope = { apiBaseUrl: "https://hotbargain.vip/api", installedBuild: 56 };

test("已安装构建号只接受正整数字符串", () => {
  assert.equal(parseInstalledAndroidBuildNumber("56"), 56);
  assert.equal(parseInstalledAndroidBuildNumber(" 63 "), 63);
  for (const value of [null, undefined, "", "0", "-1", "1.0", "abc", "9".repeat(20)]) {
    assert.equal(parseInstalledAndroidBuildNumber(value), null, String(value));
  }
});

test("解析 ApiResponse 包装的 required 判定", () => {
  const decision = normalizeAndroidNativeUpdateDecision(REQUIRED_PAYLOAD, 56);
  assert.deepEqual(decision, REQUIRED_PAYLOAD.data);
});

test("required 不自洽时降级为 none，避免把已是最新的设备锁死", () => {
  // 已安装构建号已达到最低版本
  assert.equal(normalizeAndroidNativeUpdateDecision(REQUIRED_PAYLOAD, 63)?.state, "none");
  // 公开包低于最低版本（装不上任何满足要求的包）
  assert.equal(
    normalizeAndroidNativeUpdateDecision(
      { data: { ...REQUIRED_PAYLOAD.data, latestBuildNumber: 60 } },
      56,
    )?.state,
    "none",
  );
  // 缺最低版本或公开包构建号
  assert.equal(
    normalizeAndroidNativeUpdateDecision(
      { data: { ...REQUIRED_PAYLOAD.data, minimumSupportedBuildNumber: null } },
      56,
    )?.state,
    "none",
  );
  assert.equal(
    normalizeAndroidNativeUpdateDecision(
      { data: { ...REQUIRED_PAYLOAD.data, latestBuildNumber: null } },
      56,
    )?.state,
    "none",
  );
});

test("非法判定返回 null（按失败处理）", () => {
  assert.equal(normalizeAndroidNativeUpdateDecision(null, 56), null);
  assert.equal(normalizeAndroidNativeUpdateDecision({ data: { state: "optional", policyVersion: "1" } }, 56), null);
  assert.equal(normalizeAndroidNativeUpdateDecision({ data: { state: "none" } }, 56), null);
  assert.equal(
    normalizeAndroidNativeUpdateDecision({ data: { state: "none", policyVersion: "none" } }, 56)?.state,
    "none",
  );
});

test("判定接口带已安装构建号并跳过中心日志", async () => {
  const calls: unknown[][] = [];
  const decision = await fetchAndroidNativeUpdateDecision({
    async get(...args) {
      calls.push(args);
      return { data: REQUIRED_PAYLOAD };
    },
  }, 56);
  assert.equal(decision?.state, "required");
  assert.deepEqual(calls, [[
    "/app-updates/mobile-android",
    { params: { build: "56" }, headers: { "X-Skip-Center-Log": "1" } },
  ]]);
});

test("服务端 required 写入缓存，none 清除缓存", async () => {
  const { storage, values } = memoryStorage();
  const required = normalizeAndroidNativeUpdateDecision(REQUIRED_PAYLOAD, 56);
  const first = await resolveAndroidNativeUpdateDecision({
    fetchDecision: async () => required,
    storage,
    scope,
  });
  assert.equal(first.source, "server");
  assert.ok(values.get(ANDROID_NATIVE_REQUIRED_CACHE_KEY));

  const none: AndroidNativeUpdateDecision = {
    state: "none",
    policyVersion: "4",
    minimumSupportedBuildNumber: null,
    latestVersion: "1.0.10",
    latestBuildNumber: 63,
    releaseMessage: null,
  };
  const second = await resolveAndroidNativeUpdateDecision({
    fetchDecision: async () => none,
    storage,
    scope,
  });
  assert.equal(second.decision?.state, "none");
  assert.equal(values.has(ANDROID_NATIVE_REQUIRED_CACHE_KEY), false);
});

test("离线时只沿用同一 API 与同一已安装构建号的 required 缓存", async () => {
  const { storage } = memoryStorage();
  await resolveAndroidNativeUpdateDecision({
    fetchDecision: async () => normalizeAndroidNativeUpdateDecision(REQUIRED_PAYLOAD, 56),
    storage,
    scope,
  });
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    const offline = await resolveAndroidNativeUpdateDecision({
      fetchDecision: async () => { throw new Error("offline"); },
      storage,
      scope,
    });
    assert.equal(offline.source, "cache");
    assert.equal(offline.decision?.state, "required");

    // 装上新包后构建号变化，旧缓存不能再拦截
    assert.equal(
      await readCachedAndroidNativeRequiredDecision(storage, { ...scope, installedBuild: 63 }),
      null,
    );
    // 换 API 地址同样失效
    assert.equal(
      await readCachedAndroidNativeRequiredDecision(storage, { ...scope, apiBaseUrl: "https://other.example/api" }),
      null,
    );
    const noCache = await resolveAndroidNativeUpdateDecision({
      fetchDecision: async () => { throw new Error("offline"); },
      storage: memoryStorage().storage,
      scope,
    });
    assert.deepEqual(noCache, { decision: null, source: "none" });
  } finally {
    console.warn = originalWarn;
  }
});

test("损坏的缓存按无缓存处理", async () => {
  const { storage } = memoryStorage({ [ANDROID_NATIVE_REQUIRED_CACHE_KEY]: "{not json" });
  assert.equal(await readCachedAndroidNativeRequiredDecision(storage, scope), null);
});

test("拦截模式只在启用且 required 时生效，且只对正式包检查", () => {
  const required = normalizeAndroidNativeUpdateDecision(REQUIRED_PAYLOAD, 56);
  assert.equal(getAndroidNativeUpdateBoundaryMode({ enabled: true, decision: required }), "required");
  assert.equal(getAndroidNativeUpdateBoundaryMode({ enabled: false, decision: required }), "content");
  assert.equal(getAndroidNativeUpdateBoundaryMode({ enabled: true, decision: null }), "content");
  assert.equal(shouldCheckAndroidNativeRequiredUpdate("production"), true);
  assert.equal(shouldCheckAndroidNativeRequiredUpdate(null), true);
  assert.equal(shouldCheckAndroidNativeRequiredUpdate("preview"), false);
});
