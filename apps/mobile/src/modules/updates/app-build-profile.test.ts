import assert from "node:assert/strict";
import {
  normalizeAppBuildProfile,
  shouldRunAutomaticAppUpdatesForProfile,
  shouldRunMobileOtaCheck,
} from "./app-build-profile";

function run() {
  assert.equal(normalizeAppBuildProfile(" Preview "), "preview", "构建 profile 应归一化大小写和空白");
  assert.equal(normalizeAppBuildProfile(null), "production", "缺省 profile 按生产包处理");

  assert.equal(
    shouldRunAutomaticAppUpdatesForProfile("production"),
    true,
    "正式包应保留自动更新"
  );
  assert.equal(
    shouldRunAutomaticAppUpdatesForProfile("preview"),
    true,
    "preview 包应自动检查 OTA 和 APK 更新"
  );
  assert.equal(
    shouldRunAutomaticAppUpdatesForProfile("development"),
    false,
    "开发包不应自动更新"
  );
  assert.equal(
    shouldRunAutomaticAppUpdatesForProfile("test"),
    false,
    "显式 test profile 不应自动更新"
  );

  const ota = { appReady: true, startupFailed: false, isIosReviewSession: false, profile: "production" };
  assert.equal(shouldRunMobileOtaCheck(ota), true, "未登录也要检查 OTA，新装 App 才收得到修复");
  assert.equal(shouldRunMobileOtaCheck({ ...ota, isIosReviewSession: true }), false, "审核演示会话必须完全离线");
  assert.equal(shouldRunMobileOtaCheck({ ...ota, appReady: false }), false, "启动未就绪不检查");
  assert.equal(shouldRunMobileOtaCheck({ ...ota, startupFailed: true }), false, "启动失败不检查");
  assert.equal(shouldRunMobileOtaCheck({ ...ota, profile: "development" }), false, "开发包保留固定快照");

  console.log("app-build-profile.test.ts: ok");
}

run();
