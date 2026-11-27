import { formatRuntimeAppVersion } from "@hb/pos-sync/core/sync/runtime-status-reporter";

/**
 * iPad 心跳上报的版本号：`0.2.0 (22)`，在跑 OTA 时再带 `+ota.<前 8 位>`。
 *
 * 中文注释：
 * 1. 服务端 OTA 只按 runtimeVersion（0.2.0）判定，分不出原生包 build；带上 build 号后，
 *    后台设备列表与 OTA 选设备时能直接看出每台装的是哪个原生包（避开 build ≤18 会闪退的设备）。
 * 2. build 号只接受 1～10 位数字，异常值不拼，保持旧格式，避免把脏值当成真实版本展示。
 * 3. 后端列宽 50，超长会整条丢弃；这里的格式远低于上限。
 */
export function formatIpadHeartbeatAppVersion(
  appVersion: string,
  nativeBuild: string | null | undefined,
  update: Readonly<{ updateId: string | null; isEmbeddedLaunch: boolean }>,
): string {
  const base = appVersion.trim();
  const build = nativeBuild?.trim() ?? "";
  const withBuild = /^\d{1,10}$/u.test(build) ? `${base} (${build})` : base;
  return formatRuntimeAppVersion(withBuild, update);
}
