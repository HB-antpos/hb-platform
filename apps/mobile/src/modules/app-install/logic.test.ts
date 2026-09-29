import assert from "node:assert/strict";
import {
  defaultInstallPlatform,
  formatArtifactSize,
  parseAppInstallLinks,
  resolveAndroidInstallUrl,
} from "./logic";

const parsed = parseAppInstallLinks({
  success: true,
  data: {
    ios: {
      version: "1.0.6",
      buildNumber: "50",
      appStoreUrl: "https://apps.apple.com/au/app/id1",
      appleVerifiedAtUtc: "2026-09-22T01:00:00Z",
    },
    android: {
      easBuildId: "b-1",
      appVersion: "1.0.6",
      appBuildVersion: "51",
      downloadUrl: "https://cos.example.com/a.apk",
      artifactSize: 52428800,
      completedAt: "2026-09-23T01:00:00Z",
    },
  },
});
assert.equal(parsed.ios?.version, "1.0.6");
assert.equal(parsed.ios?.buildNumber, "50");
assert.equal(parsed.android?.downloadUrl, "https://cos.example.com/a.apk");
assert.equal(formatArtifactSize(parsed.android?.artifactSize ?? null), "50.0 MB");

// 任一端缺失或地址不是 http(s) 时视为暂无版本，不生成二维码。
const partial = parseAppInstallLinks({
  success: true,
  data: { ios: { version: "1.0.6", appStoreUrl: "itms-apps://x" }, android: null },
});
assert.equal(partial.ios, null);
assert.equal(partial.android, null);
assert.deepEqual(parseAppInstallLinks(null), { ios: null, android: null });

// 公网后端用稳定入口；本机/内网后端退回构建直链。
const android = parsed.android!;
assert.equal(
  resolveAndroidInstallUrl(android, "https://hotbargain.vip/api"),
  "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production",
);
assert.equal(
  resolveAndroidInstallUrl(android, "http://192.168.1.8:5002/api"),
  "https://cos.example.com/a.apk",
);

assert.equal(defaultInstallPlatform("android"), "android");
assert.equal(defaultInstallPlatform("ios"), "ios");
assert.equal(defaultInstallPlatform("web"), "ios");

console.log("app-install logic tests passed");
