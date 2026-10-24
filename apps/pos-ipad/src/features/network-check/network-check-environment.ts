import { sharedApiTransportSelector } from "@hb/pos-api-client/transport-failover";
import * as Application from "expo-application";
import Constants from "expo-constants";
import * as Network from "expo-network";
import * as Updates from "expo-updates";
import { Platform } from "react-native";

import type { NetworkCheckPorts } from "./network-check-runner";
import type { NetworkLocalInfo } from "./network-check-types";

/**
 * 真机端口：只用原生包里早已存在的模块（expo-network / expo-application / expo-updates），
 * 不引入新原生依赖，纯 JS 改动即可交付。
 */
export function createExpoNetworkCheckPorts(): NetworkCheckPorts {
  const perf = (globalThis as { performance?: { now?: () => number } }).performance;
  return {
    // 直接用全局 fetch：诊断要分别测三条入口，不能经过入口故障转移，也不能改动它的状态。
    fetch: (url, init) => fetch(url, init),
    now: typeof perf?.now === "function" ? () => perf.now!() : () => Date.now(),
    wallNow: () => Date.now(),
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    readLocalNetwork,
    readEntryState: () => ({
      preferred: sharedApiTransportSelector.getPreferredMode(),
      active: sharedApiTransportSelector.getActiveMode(),
      usingFallback: sharedApiTransportSelector.isUsingFallback(),
    }),
  };
}

async function readLocalNetwork(): Promise<NetworkLocalInfo> {
  const [state, ipAddress] = await Promise.all([
    Network.getNetworkStateAsync(),
    Network.getIpAddressAsync().catch(() => null),
  ]);
  return {
    known: true,
    connected: typeof state.isConnected === "boolean" ? state.isConnected : null,
    type: state.type ? String(state.type) : null,
    // 没有可用地址时 iOS 返回 0.0.0.0，当作没有。
    ipAddress: ipAddress && ipAddress !== "0.0.0.0" ? ipAddress : null,
  };
}

/** 设备描述：设备名（门店多台 iPad 时便于认出是哪台）+ 系统版本。 */
export function describeNetworkCheckDevice(): string {
  const kind = Platform.OS === "ios" && Platform.isPad ? "iPad" : Platform.OS;
  const deviceName =
    typeof Constants.deviceName === "string" ? Constants.deviceName.trim() : "";
  const name = deviceName && deviceName !== kind ? `${deviceName} (${kind})` : kind;
  const system = Platform.OS === "ios" ? "iPadOS" : Platform.OS;
  return `${name} · ${system} ${String(Platform.Version)}`;
}

/** App 版本：版本号 (build) · runtime · OTA 前 8 位（内置包为 embedded）。不依赖 POS runtime，启动失败页也能读。 */
export function describeNetworkCheckApp(): string {
  const version = Application.nativeApplicationVersion ?? "?";
  const build = Application.nativeBuildVersion ?? "?";
  const runtime = Updates.runtimeVersion?.trim() || "unknown";
  const ota =
    Updates.isEmbeddedLaunch === false && Updates.updateId
      ? Updates.updateId.slice(0, 8).toLowerCase()
      : "embedded";
  return `${version} (${build}) · runtime ${runtime} · OTA ${ota}`;
}
