import assert from "node:assert/strict";
import zh from "../../../locales/zh/releaseOverview.json";
import en from "../../../locales/en/releaseOverview.json";
import type { NativePolicy, NativeRelease } from "../types";
import type { WpfRelease } from "../../wpf-versions/types";
import {
  RELEASE_LANES,
  buildAttentionItems,
  buildRecentChanges,
  buildReleaseLanes,
  buildReleaseOverview,
  buildTerminalStatus,
  describeLaneChange,
  formatReleaseTime,
  normalizeMobileAndroidNativePolicy,
  readHandheldManaged,
  readLaneAudit,
  shortIdentity,
  summarizeHandheldLane,
  summarizeIpadOtaLane,
  summarizeMobileAndroidNativeLane,
  summarizeMobileOtaLane,
  summarizeNativeLane,
  summarizeWpfLane,
  toReleaseTimestamp,
  type AuditedHandheldPolicy,
  type AuditedOtaPolicy,
  type ReleaseLaneSummary,
  type ReleaseOverviewSources,
} from "./logic";

const copy = zh;

// ---- 文案：中英文键一致（parity 脚本也会查，这里保证类型可互换）----
const enCopy: typeof zh = en;
assert.equal(enCopy.terminals.mobile, "Staff app");
assert.equal(zh.terminals.wpf, "WPF 收银端");

// ---- 线路目录 ----
assert.equal(RELEASE_LANES.length, 12, "Web 的 11 条 + WPF 预览通道");
assert.equal(new Set(RELEASE_LANES.map((lane) => lane.key)).size, 12);
for (const lane of RELEASE_LANES) {
  assert.ok(copy.lanes[lane.key], `${lane.key} 缺显示名`);
  assert.equal(lane.target.terminal, lane.terminal);
}
assert.deepEqual(
  RELEASE_LANES.find((lane) => lane.key === "handheld-ios-ota")?.target,
  { terminal: "handheld", channel: "ota", platform: "ios" },
);
assert.deepEqual(
  RELEASE_LANES.find((lane) => lane.key === "wpf-preview")?.target,
  { terminal: "wpf", wpfChannel: "preview" },
);

// ---- 工具函数 ----
assert.equal(shortIdentity("abcdef123456"), "abcdef123456", "12 位不截断");
assert.equal(shortIdentity("abcdef1234567"), "abcdef12…", "超过 12 位截前 8 位");
assert.equal(shortIdentity("  "), null);
assert.equal(
  toReleaseTimestamp("2026-10-09 12:00:00"),
  Date.parse("2026-10-09T12:00:00Z"),
  "无时区后缀按 UTC",
);
assert.ok(Number.isNaN(toReleaseTimestamp(null)));
{
  const local = new Date(Date.parse("2026-10-09T12:05:00Z"));
  const pad = (n: number) => String(n).padStart(2, "0");
  assert.equal(
    formatReleaseTime("2026-10-09T12:05:00Z", new Date(2026, 5, 1)),
    `${pad(local.getMonth() + 1)}-${pad(local.getDate())} ${pad(local.getHours())}:${pad(local.getMinutes())}`,
  );
  assert.match(formatReleaseTime("2025-01-02T00:00:00Z", new Date(2026, 5, 1)), /^2025-|^2025/);
  assert.equal(formatReleaseTime(null), "—");
  assert.equal(formatReleaseTime("garbage"), "garbage");
}
assert.deepEqual(readLaneAudit({ updatedAt: "2026-10-01T00:00:00Z", updatedBy: " admin " }), {
  updatedAt: "2026-10-01T00:00:00Z",
  updatedBy: "admin",
});
assert.deepEqual(readLaneAudit(null), { updatedAt: null, updatedBy: null });
assert.equal(readHandheldManaged({ id: "p1" }), true);
assert.equal(readHandheldManaged({ id: null, managed: true }), true);
assert.equal(readHandheldManaged({ id: null }), false);

// ---- 员工端 iOS / iPad 原生 ----
const nativeRelease = (id: string, version: string, build: string, createdAt: string): NativeRelease => ({
  id,
  app: "mobile-ios",
  appStoreId: "123456",
  bundleIdentifier: "x",
  version,
  buildNumber: build,
  storefront: "au",
  appStoreUrl: "",
  appleVerifiedAtUtc: createdAt,
  createdAt,
});
const nativePolicy = (overrides: Partial<NativePolicy> = {}): NativePolicy => ({
  id: "n1",
  enabled: true,
  policyVersion: 3,
  releaseId: "r1",
  latestVersion: "1.0.6",
  minimumSupportedVersion: null,
  minimumSupportedBuildNumber: null,
  appStoreUrl: null,
  releaseMessage: null,
  targetScope: "all",
  targetStoreGuids: [],
  updatedAt: "2026-10-08T01:00:00",
  updatedBy: "admin",
  ...overrides,
});
const releases = [
  nativeRelease("r1", "1.0.7", "63", "2026-10-01T00:00:00Z"),
  nativeRelease("r2", "1.0.8", "64", "2026-10-05T00:00:00Z"),
  nativeRelease("r3", "1.0.9", "65", "2026-10-07T00:00:00Z"),
];
{
  const lane = summarizeNativeLane("mobile-ios-native", nativePolicy(), releases, copy);
  assert.equal(lane.status, "pending", "有更晚登记的版本未投放 → 待处理");
  assert.equal(lane.target, "1.0.7 (63)");
  assert.equal(lane.mode, "optional");
  assert.equal(lane.scope, "全部设备");
  assert.equal(lane.detail, null);
  assert.deepEqual(lane.attentions, [{ kind: "pending-release", count: 2, latest: "1.0.9 (65)" }]);
  assert.deepEqual(lane.navTarget, { terminal: "mobile", channel: "native", platform: "ios" });

  const latest = summarizeNativeLane("mobile-ios-native", nativePolicy({ releaseId: "r3" }), releases, copy);
  assert.equal(latest.status, "active");
  assert.equal(latest.attentions.length, 0);

  const minimum = summarizeNativeLane(
    "mobile-ios-native",
    nativePolicy({ releaseId: "r3", minimumSupportedVersion: "1.0.5", minimumSupportedBuildNumber: 50 }),
    releases,
    copy,
  );
  assert.equal(minimum.mode, "minimum");
  assert.equal(minimum.minimum, "1.0.5 (50)");
  assert.equal(minimum.detail, "最低 1.0.5 (50)");

  const disabled = summarizeNativeLane(
    "ipad-ios-native",
    nativePolicy({ enabled: false, targetScope: "stores", targetStoreGuids: ["a"] }),
    releases,
    copy,
  );
  assert.equal(disabled.status, "inactive");
  assert.equal(disabled.mode, null, "未启用不显示更新方式");
  assert.equal(disabled.scope, null);
  assert.equal(disabled.attentions.length, 0, "未启用不产生待处理");

  const ipadStores = summarizeNativeLane(
    "ipad-ios-native",
    nativePolicy({ releaseId: "r3", targetScope: "stores", targetStoreGuids: ["a", "b"] }),
    releases,
    copy,
  );
  assert.equal(ipadStores.scope, "2 个分店");
  assert.equal(ipadStores.fullLabel, "iPad POS · iOS 原生");
}

// ---- 员工端安卓原生 ----
{
  const policy = normalizeMobileAndroidNativePolicy({
    enabled: true,
    minimumSupportedBuildNumber: 58,
    latestBuild: { appVersion: "1.0.10", appBuildVersion: "60" },
    updatedAt: "2026-10-08T10:00:00Z",
    updatedBy: "sean",
  });
  const lane = summarizeMobileAndroidNativeLane(policy, copy);
  assert.equal(lane.target, "1.0.10 (60)");
  assert.equal(lane.mode, "minimum");
  assert.equal(lane.detail, "最低 build 58");
  assert.equal(lane.status, "active");
  assert.equal(lane.updatedBy, "sean");

  const noBuild = normalizeMobileAndroidNativePolicy({ enabled: true, latestBuild: { appBuildVersion: 0 } });
  assert.equal(noBuild.latestBuild, null, "versionCode 非正整数视为没有公开包");
  assert.equal(summarizeMobileAndroidNativeLane(noBuild, copy).mode, "optional");
}

// ---- 员工端 OTA / iPad OTA：截断 ID、强制更新 ----
const otaPolicy = (overrides: Partial<AuditedOtaPolicy> = {}): AuditedOtaPolicy => ({
  id: "o1",
  environment: "production" as const,
  platform: "ios" as const,
  enabled: true,
  required: false,
  policyVersion: 1,
  targetReleaseId: "t1",
  targetRuntimeVersion: "1.0.7",
  releaseMessage: null,
  targetRelease: null,
  updatedAt: "2026-10-08T05:00:00Z",
  updatedBy: "ota-admin",
  ...overrides,
});
{
  const lane = summarizeMobileOtaLane(
    "mobile-ios-ota",
    otaPolicy({
      required: true,
      targetRelease: {
        id: "t1",
        appKey: "mobile",
        environment: "production",
        platform: "ios",
        clientChannel: "production",
        releaseChannel: "production",
        runtimeVersion: "1.0.8",
        updateGroupId: "0f4c1a2b-9d8e-4c7b-a6f5-1234567890ab",
        updateId: "u",
        message: null,
        dashboardUrl: null,
        publishedAtUtc: "",
        isRollback: false,
      },
    }),
    copy,
  );
  assert.equal(lane.target, "runtime 1.0.8", "优先取目标发布的 runtime");
  assert.equal(lane.detail, "0f4c1a2b…");
  assert.equal(lane.mode, "force");
  assert.equal(lane.scope, "全部设备");

  const fallback = summarizeMobileOtaLane("mobile-android-ota", otaPolicy({ platform: "android" }), copy);
  assert.equal(fallback.target, "runtime 1.0.7");
  assert.equal(fallback.detail, null);
  assert.equal(fallback.mode, "optional");

  const ipad = summarizeIpadOtaLane(
    {
      id: "i1",
      enabled: true,
      policyVersion: 1,
      releaseId: "x",
      forceUpdate: false,
      targetScope: "stores",
      targetStoreGuids: ["s1", "s2", "s3"],
      releaseMessage: null,
      release: {
        id: "x",
        environment: "production",
        updateGroupId: "short-id",
        iosUpdateId: "",
        channel: "production",
        runtimeVersion: "0.2.0",
        dashboardUrl: null,
        publishedAtUtc: "",
        isRollback: false,
      },
      updatedAt: null,
      updatedBy: null,
    },
    copy,
  );
  assert.equal(ipad.target, "runtime 0.2.0");
  assert.equal(ipad.detail, "short-id");
  assert.equal(ipad.scope, "3 个分店");
  assert.equal(ipad.status, "active", "iPad OTA 定向分店与 Web 一致，不算灰度待处理");
}

// ---- 手持 ----
const handheld = (overrides: Partial<AuditedHandheldPolicy> = {}): AuditedHandheldPolicy => ({
  id: "h1",
  lane: "android-native",
  enabled: true,
  required: false,
  policyVersion: 1,
  candidateId: "c1",
  candidateValid: true,
  blockedReason: null,
  candidate: {
    id: "c1",
    lane: "android-native",
    platform: "android",
    kind: "native",
    version: "0.1.1",
    buildNumber: "9",
    runtimeVersion: "0.1.1",
    channel: null,
    updateId: "abcdefghijklmnop",
    updateGroupId: null,
    message: null,
    dashboardUrl: null,
    downloadUrl: null,
    appStoreUrl: null,
    createdAt: "",
    activatable: true,
    blockedReason: null,
  },
  minimumSupportedVersion: "0.1.0",
  minimumSupportedBuildNumber: 7,
  releaseMessage: null,
  updatedAt: "2026-10-07T08:00:00Z",
  updatedBy: "pos-admin",
  managed: true,
  ...overrides,
});
{
  const native = summarizeHandheldLane(handheld(), copy);
  assert.equal(native.key, "handheld-android-native");
  assert.equal(native.target, "0.1.1 (9)");
  assert.equal(native.detail, "runtime 0.1.1 · 最低 0.1.0 (7)", "手持原生 detail 是 runtime + 最低版本");
  assert.equal(native.mode, "minimum");

  const required = summarizeHandheldLane(handheld({ required: true }), copy);
  assert.equal(required.mode, "force", "required 优先于最低版本");

  const ota = summarizeHandheldLane(
    handheld({ lane: "ios-ota", candidate: { ...handheld().candidate!, lane: "ios-ota", kind: "ota", platform: "ios" } }),
    copy,
  );
  assert.equal(ota.key, "handheld-ios-ota");
  assert.equal(ota.target, "runtime 0.1.1");
  assert.equal(ota.detail, "abcdefgh…", "OTA 不显示最低版本，只显示截断的 updateId");
  assert.equal(ota.minimum, null);

  const invalid = summarizeHandheldLane(
    handheld({ candidateValid: false, blockedReason: "POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH" }),
    copy,
  );
  assert.equal(invalid.status, "pending");
  assert.deepEqual(invalid.attentions, [
    { kind: "binding-invalid", reason: "POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH" },
  ]);

  const unmanaged = summarizeHandheldLane(handheld({ managed: false, candidateValid: false }), copy);
  assert.equal(unmanaged.status, "inactive", "后台未接管视同未启用");
  assert.equal(unmanaged.attentions.length, 0);
}

// ---- WPF ----
const wpf = (overrides: Partial<WpfRelease>): WpfRelease => ({
  id: overrides.version ?? "x",
  version: "1.0.0",
  channel: "production",
  fileName: "",
  fileSize: null,
  sha256: null,
  installerType: "exe",
  installerArguments: null,
  downloadUrl: null,
  objectKey: null,
  releaseNotes: null,
  isActive: true,
  isCurrent: false,
  isRollback: false,
  forceUpdate: false,
  minimumSupportedVersion: null,
  targetVersion: null,
  targetScope: "all",
  targetStoreGuids: [],
  targetDeviceRegistrationIds: [],
  targetStoreSummaries: [],
  targetDeviceSummaries: [],
  policyUpdatedAt: null,
  policyUpdatedBy: null,
  createdAt: null,
  updatedAt: null,
  ...overrides,
});
{
  const rows = [
    wpf({ version: "1.0.60", isActive: true }),
    wpf({ version: "1.0.61", isActive: false }),
    wpf({
      version: "1.0.59",
      isCurrent: true,
      targetVersion: "1.0.59",
      minimumSupportedVersion: "1.0.50",
      targetScope: "stores",
      targetStoreGuids: ["s1042"],
      policyUpdatedAt: "2026-10-09T05:36:00Z",
      policyUpdatedBy: "sean",
    }),
  ];
  const lane = summarizeWpfLane("production", rows, copy);
  assert.equal(lane.key, "wpf-production");
  assert.equal(lane.target, "1.0.59");
  assert.equal(lane.mode, "minimum");
  assert.equal(lane.detail, "最低 1.0.50");
  assert.equal(lane.scope, "1 个分店");
  assert.equal(lane.status, "pending");
  assert.deepEqual(lane.attentions, [
    { kind: "partial-rollout", scope: "1 个分店" },
    { kind: "pending-release", count: 1, latest: "1.0.60" },
  ], "停用版本不算未投放");
  assert.deepEqual(lane.navTarget, { terminal: "wpf", wpfChannel: "production" });

  const forced = summarizeWpfLane(
    "preview",
    [wpf({ version: "2.0.0", isCurrent: true, targetVersion: "2.0.0", forceUpdate: true, targetScope: "devices", targetDeviceRegistrationIds: [1, 2] })],
    copy,
  );
  assert.equal(forced.key, "wpf-preview");
  assert.equal(forced.mode, "force");
  assert.equal(forced.scope, "2 台机器");
  assert.equal(forced.minimum, null);

  const minimumEqualsTarget = summarizeWpfLane(
    "production",
    [wpf({ version: "1.0.0", isCurrent: true, targetVersion: "1.0.0", minimumSupportedVersion: "1.0.0" })],
    copy,
  );
  assert.equal(minimumEqualsTarget.mode, "force", "最低版本 ≥ 目标等同强制");
  assert.equal(minimumEqualsTarget.status, "active");
  assert.equal(minimumEqualsTarget.scope, "全部设备");

  const empty = summarizeWpfLane("preview", [wpf({ version: "1.0.0" })], copy);
  assert.equal(empty.status, "inactive", "没有策略摘要的通道算未启用");
  assert.equal(empty.target, null);
}

// ---- 汇总：单个数据源失败只影响自己 ----
const sources: ReleaseOverviewSources = {
  mobileIosNative: { state: "ready", value: { policy: nativePolicy({ releaseId: "r3" }), releases } },
  mobileAndroidNative: { state: "error" },
  mobileIosOta: { state: "ready", value: otaPolicy() },
  mobileAndroidOta: { state: "ready", value: otaPolicy({ platform: "android", enabled: false, updatedAt: "2026-10-09T09:00:00Z" }) },
  ipadNative: { state: "loading" },
  ipadOta: { state: "error" },
  handheld: {
    state: "ready",
    // 接口只返回了 3 条：缺的那条按读取失败处理。
    value: [
      handheld(),
      handheld({ lane: "ios-native", candidateValid: false, updatedAt: "2026-10-06T00:00:00Z" }),
      handheld({ lane: "android-ota", managed: false, updatedAt: null }),
    ],
  },
  wpfProduction: {
    state: "ready",
    value: [
      wpf({ version: "1.0.59", isCurrent: true, targetVersion: "1.0.59", policyUpdatedAt: "2026-10-09T05:36:00Z", policyUpdatedBy: "sean" }),
    ],
  },
  // wpfPreview 缺省 → loading
};
{
  const lanes = buildReleaseLanes(sources, copy);
  assert.deepEqual(lanes.map((lane) => lane.key), RELEASE_LANES.map((lane) => lane.key), "按目录顺序");
  const byKey = (key: string) => lanes.find((lane) => lane.key === key)!;
  assert.equal(byKey("mobile-android-native").status, "error");
  assert.equal(byKey("mobile-ios-native").status, "active", "邻近线路不受影响");
  assert.equal(byKey("ipad-ios-native").loading, true);
  assert.equal(byKey("ipad-ios-ota").status, "error");
  assert.equal(byKey("handheld-ios-ota").status, "error", "接口缺 lane 按读取失败");
  assert.equal(byKey("handheld-android-native").status, "active");
  assert.equal(byKey("handheld-ios-native").status, "pending");
  assert.equal(byKey("handheld-android-ota").status, "inactive");
  assert.equal(byKey("wpf-preview").loading, true);

  const status = buildTerminalStatus(lanes);
  assert.deepEqual(status, {
    mobile: "error",
    ipad: "error",
    handheld: "error",
    wpf: "active",
  }, "worst 汇总：读取失败 > 待处理 > 已激活 > 未启用；读取中的线路不参与");
  assert.equal(
    buildTerminalStatus(lanes.filter((lane) => lane.key !== "handheld-ios-ota")).handheld,
    "pending",
  );
  assert.equal(
    buildTerminalStatus(lanes.filter((lane) => lane.terminal !== "ipad" || lane.loading)).ipad,
    null,
    "全部读取中为 null",
  );

  const attention = buildAttentionItems(lanes, copy);
  assert.deepEqual(attention.map((item) => item.laneKey), ["handheld-ios-native"]);
  assert.equal(attention[0].title, "策略绑定失效");
  assert.equal(attention[0].laneLabel, "手持 POS · iOS 原生");
  assert.deepEqual(attention[0].navTarget, { terminal: "handheld", channel: "native", platform: "ios" });

  const recent = buildRecentChanges(lanes, 3);
  assert.deepEqual(
    recent.map((lane) => lane.key),
    ["mobile-android-ota", "wpf-production", "mobile-ios-ota"],
    "按更新时间倒序取前 N 条，未启用的线路也算一次策略变更",
  );
  assert.equal(describeLaneChange(recent[0], copy), "未启用");
  assert.equal(describeLaneChange(recent[1], copy), "1.0.59 · 可选更新 · 全部设备");
}

// ---- 待处理文案 ----
{
  const wpfLane = summarizeWpfLane(
    "production",
    [
      wpf({ version: "1.0.60" }),
      wpf({ version: "1.0.61" }),
      wpf({ version: "1.0.59", isCurrent: true, targetVersion: "1.0.59", targetScope: "devices", targetDeviceRegistrationIds: [7] }),
    ],
    copy,
  );
  const items = buildAttentionItems([wpfLane], copy);
  assert.deepEqual(items.map((item) => item.title), [
    "灰度中：仅 1 台机器",
    "1.0.61 等 2 个新版本未投放",
  ]);
  assert.equal(new Set(items.map((item) => item.id)).size, 2, "id 唯一");
  const enItems = buildAttentionItems([summarizeWpfLane("production", [
    wpf({ version: "1.0.60" }),
    wpf({ version: "1.0.59", isCurrent: true, targetVersion: "1.0.59", targetScope: "stores", targetStoreGuids: ["a", "b"] }),
  ], enCopy)], enCopy);
  assert.deepEqual(enItems.map((item) => item.title), [
    "Staged rollout: 2 store(s) only",
    "1 newer version(s) not rolled out, latest 1.0.60",
  ]);
}

// ---- 全部失败 ----
{
  const failed: ReleaseOverviewSources = Object.fromEntries(
    ["mobileIosNative", "mobileAndroidNative", "mobileIosOta", "mobileAndroidOta", "ipadNative", "ipadOta", "handheld", "wpfProduction", "wpfPreview"].map(
      (key) => [key, { state: "error" }],
    ),
  );
  const model = buildReleaseOverview(failed, copy);
  assert.ok(model.lanes.every((lane: ReleaseLaneSummary) => lane.status === "error"));
  assert.equal(model.attention.length, 0);
  assert.equal(model.recent.length, 0);
  assert.deepEqual(model.terminalStatus, { mobile: "error", ipad: "error", handheld: "error", wpf: "error" });
}

console.log("release overview logic.test.ts: ok");
