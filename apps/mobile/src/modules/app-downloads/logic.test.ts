import assert from "node:assert/strict";
import {
  buildConfirmationSummary,
  buildNativePolicyPayload,
  buildOtaPolicyPayload,
  buildStableAndroidDownloadUrl,
  diffNativePolicyForm,
  diffOtaPolicyForm,
  isPolicyConflict,
  mergeHandheldPolicyCandidates,
  parseBuildNumber,
  nativePolicyFormFrom,
  parseHandheldBuildNumber,
  pickHandheldIosDownload,
  pickNativeDownloadRelease,
  resolveAppDownloadsSection,
  validateHandheldCandidate,
  validateHandheldPolicy,
  validateNativePolicy,
  validateRegistration,
} from "./logic";
import type { HandheldCandidate, NativeRelease } from "./types";

const base = {
  enabled: true,
  releaseId: "r-1",
  minimumSupportedVersion: "1.2.3",
  minimumSupportedBuildNumber: "47",
  releaseMessage: "notes",
  targetScope: "all" as const,
  targetStoreGuids: [],
};

assert.equal(parseBuildNumber("47", true), 47);
assert.equal(parseBuildNumber("2147483648"), null);
assert.equal(parseHandheldBuildNumber("0"), null);
assert.equal(parseHandheldBuildNumber("47"), 47);
assert.equal(parseHandheldBuildNumber("2147483648"), null);
const candidateFixture = (
  id: string,
  lane: HandheldCandidate["lane"],
): HandheldCandidate => ({
  id,
  lane,
  platform: lane.startsWith("ios") ? "ios" : "android",
  kind: lane.endsWith("ota") ? "ota" : "native",
  version: "1.0.0",
  buildNumber: "47",
  runtimeVersion: null,
  channel: null,
  updateId: null,
  updateGroupId: null,
  message: null,
  dashboardUrl: null,
  downloadUrl: "https://example.test/download",
  appStoreUrl: null,
  createdAt: "2026-09-06T00:00:00Z",
  activatable: true,
  blockedReason: null,
});
const boundCandidate = candidateFixture("bound", "ios-native");
assert.deepEqual(
  mergeHandheldPolicyCandidates(
    [],
    [
      {
        lane: "ios-native",
        candidateId: "bound",
        candidate: boundCandidate,
      },
    ],
  ),
  [boundCandidate],
);
assert.deepEqual(
  mergeHandheldPolicyCandidates(
    [boundCandidate],
    [
      {
        lane: "ios-native",
        candidateId: "bound",
        candidate: candidateFixture("bound", "android-native"),
      },
    ],
  ),
  [boundCandidate],
);
assert.equal(
  validateRegistration("mobile-ios", {
    appStoreId: "123",
    buildNumber: "47",
    storefront: "au",
  }),
  "appStoreIdInvalid",
);
assert.equal(
  validateRegistration("pos-handheld", {
    appStoreId: "1234567890",
    buildNumber: "0",
    storefront: "au",
  }),
  "buildNumberInvalid",
);
assert.equal(
  validateRegistration("pos-handheld", {
    appStoreId: "1234567890",
    buildNumber: "2147483648",
    storefront: "au",
  }),
  null,
);
assert.equal(
  validateRegistration("pos-ipad", {
    appStoreId: "1234567890",
    buildNumber: "47",
    storefront: "australia",
  }),
  "storefrontInvalid",
);
assert.equal(
  validateNativePolicy({ ...base, releaseId: "" }),
  "releaseRequired",
);
assert.equal(
  validateNativePolicy({ ...base, minimumSupportedVersion: "" }),
  "minimumVersionRequired",
);
assert.equal(
  validateNativePolicy(
    { ...base, targetScope: "stores", targetStoreGuids: [] },
    true,
  ),
  "storesRequired",
);
assert.deepEqual(buildNativePolicyPayload(base, 3), {
  expectedPolicyVersion: 3,
  enabled: true,
  releaseId: "r-1",
  minimumSupportedVersion: "1.2.3",
  minimumSupportedBuildNumber: 47,
  releaseMessage: "notes",
});
assert.deepEqual(
  buildOtaPolicyPayload(
    {
      enabled: false,
      required: true,
      targetReleaseId: "r-2",
      releaseMessage: "x",
    },
    4,
  ),
  {
    expectedPolicyVersion: 4,
    enabled: false,
    required: false,
    targetReleaseId: null,
    releaseMessage: null,
  },
);
assert.equal(isPolicyConflict({ response: { status: 409 } }), true);
assert.equal(
  isPolicyConflict({
    response: { status: 400 },
    code: "APP_UPDATE_POLICY_VERSION_CONFLICT",
  }),
  true,
);
assert.equal(
  validateHandheldCandidate(
    { id: "candidate", activatable: false, blockedReason: "blocked" },
    true,
  ),
  "candidateBlocked",
);
assert.equal(
  validateHandheldCandidate(
    { id: "candidate", activatable: true, blockedReason: null },
    false,
  ),
  "candidateBlocked",
);
assert.equal(
  validateHandheldCandidate(
    {
      id: "candidate",
      activatable: true,
      blockedReason: "POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH",
    },
    false,
  ),
  null,
);
assert.equal(
  validateHandheldPolicy(
    { ...base, minimumSupportedBuildNumber: "0" },
    "android-native",
    { id: "candidate", activatable: true, blockedReason: null },
    true,
  ),
  "minimumBuildInvalid",
);
assert.deepEqual(
  buildConfirmationSummary(
    base,
    { releaseLabel: "1.2.3 (47)" },
    {
      enabled: "Enabled",
      disabled: "Disabled",
      release: "Release",
      noRelease: "None",
      required: "Required",
      optional: "Optional",
      allDevices: "All devices",
      minimumVersion: "Minimum version",
      minimumBuild: "Minimum Build",
      notes: "Notes",
      scope: "Scope",
      colon: ": ",
    },
  )[0],
  "Enabled: Enabled",
);
// iOS 原生下载入口：已启用策略优先，其次最新登记版本，无登记返回 null。
const nativeReleaseFixture = (id: string, version: string): NativeRelease => ({
  id,
  app: "mobile-ios",
  appStoreId: "6786073002",
  bundleIdentifier: "com.example.app",
  version,
  buildNumber: "47",
  storefront: "au",
  appStoreUrl: "https://apps.apple.com/au/app/id6786073002",
  appleVerifiedAtUtc: "2026-09-05T20:45:36Z",
  createdAt: "2026-09-05T20:45:36Z",
});
const nativeReleases = [
  nativeReleaseFixture("latest", "1.0.4"),
  nativeReleaseFixture("older", "1.0.3"),
];
assert.deepEqual(
  pickNativeDownloadRelease(nativeReleases, {
    enabled: true,
    releaseId: "older",
  }),
  { release: nativeReleases[1], active: true },
);
assert.deepEqual(
  pickNativeDownloadRelease(nativeReleases, {
    enabled: false,
    releaseId: "older",
  }),
  { release: nativeReleases[0], active: false },
);
assert.deepEqual(
  pickNativeDownloadRelease(nativeReleases, {
    enabled: true,
    releaseId: "gone",
  }),
  { release: nativeReleases[0], active: false },
);
assert.equal(
  pickNativeDownloadRelease([], { enabled: true, releaseId: "latest" }),
  null,
);
// 两级导航映射回原有数据分区
assert.equal(resolveAppDownloadsSection("mobile", "native"), "mobile-native");
assert.equal(resolveAppDownloadsSection("mobile", "ota"), "mobile-ota");
assert.equal(resolveAppDownloadsSection("ipad", "native"), "ipad-native");
assert.equal(resolveAppDownloadsSection("ipad", "ota"), "ipad-ota");
assert.equal(resolveAppDownloadsSection("handheld", "native"), "pos-handheld");
assert.equal(resolveAppDownloadsSection("handheld", "ota"), "pos-handheld");

// Android 稳定下载入口：按应用选接口，基址不可用时不生成链接
assert.equal(
  buildStableAndroidDownloadUrl(
    "https://hotbargain.vip/api/",
    "mobile",
    "preview",
  ),
  "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=preview",
);
assert.equal(
  buildStableAndroidDownloadUrl(
    "https://preview.example.com/api",
    "pos-handheld",
    "production",
  ),
  "https://preview.example.com/api/mobile-app-builds/pos-handheld/android-latest/download?profile=production",
);
// 本机 / 内网后端：其他设备访问不到，不生成稳定链接
for (const base of [
  "http://localhost:5002/api",
  "http://127.0.0.1:5002/api",
  "http://192.168.1.20:5002/api",
  "http://172.20.0.5/api",
  "http://10.0.0.8/api",
  "http://mac-mini.local:5002/api",
])
  assert.equal(
    buildStableAndroidDownloadUrl(base, "mobile", "production"),
    null,
  );
assert.equal(
  buildStableAndroidDownloadUrl("/api", "mobile", "production"),
  null,
);
assert.equal(
  buildStableAndroidDownloadUrl(undefined, "mobile", "production"),
  null,
);

// 手持 iOS 下载：已启用策略绑定的候选优先，否则取最新带商店链接的候选
const iosCandidate = (
  id: string,
  createdAt: string,
  appStoreUrl: string | null,
) => ({
  ...candidateFixture(id, "ios-native"),
  createdAt,
  appStoreUrl,
});
const handheldIos = [
  iosCandidate(
    "old",
    "2026-09-01T00:00:00Z",
    "https://apps.apple.com/au/app/id1",
  ),
  iosCandidate(
    "new",
    "2026-09-10T00:00:00Z",
    "https://apps.apple.com/au/app/id1",
  ),
  iosCandidate("no-url", "2026-09-12T00:00:00Z", null),
];
assert.deepEqual(
  pickHandheldIosDownload(
    [
      {
        lane: "ios-native",
        enabled: true,
        candidateId: "old",
        candidate: null,
      },
    ],
    handheldIos,
  ),
  { candidate: handheldIos[0], active: true },
);
assert.deepEqual(
  pickHandheldIosDownload(
    [
      {
        lane: "ios-native",
        enabled: false,
        candidateId: "old",
        candidate: null,
      },
    ],
    handheldIos,
  ),
  { candidate: handheldIos[1], active: false },
);
assert.equal(pickHandheldIosDownload([], [handheldIos[2]]), null);

// 保存栏的修改统计：文本去空白比较，门店集合忽略顺序
const nativeBaseline = nativePolicyFormFrom({
  enabled: true,
  releaseId: "r-1",
  minimumSupportedVersion: null,
  minimumSupportedBuildNumber: null,
  releaseMessage: null,
  targetScope: "stores",
  targetStoreGuids: ["a", "b"],
});
assert.deepEqual(
  diffNativePolicyForm(nativeBaseline, nativeBaseline, true),
  [],
);
assert.deepEqual(
  diffNativePolicyForm(
    nativeBaseline,
    { ...nativeBaseline, releaseMessage: "  ", targetStoreGuids: ["b", "a"] },
    true,
  ),
  [],
);
assert.deepEqual(
  diffNativePolicyForm(
    nativeBaseline,
    {
      ...nativeBaseline,
      releaseId: "r-2",
      minimumSupportedVersion: "1.0.3",
      targetStoreGuids: ["a"],
    },
    true,
  ),
  ["release", "minimumVersion", "scope"],
);
assert.deepEqual(
  diffNativePolicyForm(nativeBaseline, {
    ...nativeBaseline,
    targetStoreGuids: ["a"],
  }),
  [],
);
const otaBaseline = {
  enabled: true,
  required: false,
  targetReleaseId: "u-1",
  releaseMessage: "",
};
assert.deepEqual(diffOtaPolicyForm(otaBaseline, otaBaseline), []);
assert.deepEqual(
  diffOtaPolicyForm(otaBaseline, {
    ...otaBaseline,
    required: true,
    targetReleaseId: "u-2",
  }),
  ["required", "release"],
);
console.log("app-downloads logic tests: ok");
