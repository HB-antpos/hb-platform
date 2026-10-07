import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../../", import.meta.url);
const moduleSource = await readFile(
  new URL("modules/hb-app-installer/android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerModule.kt", root),
  "utf8",
);

// Expo 全部模块的 AsyncFunction 共用一条线程；下载/整包校验直接阻塞在上面，
// 期间每个 API 请求读令牌都要排队，扫码、登录要等下载完才有结果（10-07 真机实测）。
test("下载、校验、安装前复验都移出 Expo 共享异步队列", () => {
  for (const [name, worker] of [
    ["downloadApk", "downloadApk"],
    ["verifyApk", "verifyDownloadedApk"],
    ["installVerifiedApk", "installVerifiedApk"],
  ]) {
    assert.match(
      moduleSource,
      new RegExp(`AsyncFunction\\("${name}"\\) Coroutine \\{ request: \\w+ ->\\s*runOffModulesQueue \\{ ${worker}\\(request\\) \\}`),
      `${name} 必须用 Coroutine 并经 runOffModulesQueue 执行`,
    );
  }
});
