const AUTOMATIC_UPDATE_DISABLED_PROFILES = new Set(["development", "test", "testing"]);

export function normalizeAppBuildProfile(value: unknown) {
  return typeof value === "string" && value.trim() ? value.trim().toLowerCase() : "production";
}

export function shouldRunAutomaticAppUpdatesForProfile(profile: unknown) {
  // preview 包需要跟随 OTA/APK 更新；development/test 包继续保留固定安装快照。
  return !AUTOMATIC_UPDATE_DISABLED_PROFILES.has(normalizeAppBuildProfile(profile));
}

/**
 * 受控 OTA 检查是否运行。和其它启动副作用不同，它不等登录：
 * 新装 App 跑的是原生包内置 JS，如果登录前收不到 OTA，内置 JS 的缺陷就挡住首次登录，永远修不到。
 * 审核模式「登录前不碰业务接口」的约定不受影响：这里只有一个匿名的版本判定，不带账号和设备信息。
 * 一旦进入审核演示会话立刻停止，演示期间保持完全离线。
 */
export function shouldRunMobileOtaCheck(input: {
  appReady: boolean;
  startupFailed: boolean;
  isIosReviewSession: boolean;
  profile: unknown;
}) {
  return (
    input.appReady
    && !input.startupFailed
    && !input.isIosReviewSession
    && shouldRunAutomaticAppUpdatesForProfile(input.profile)
  );
}
