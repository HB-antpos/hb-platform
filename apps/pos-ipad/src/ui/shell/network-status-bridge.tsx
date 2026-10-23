import { createFailoverFetch } from "@hb/pos-api-client/transport-failover";
import Constants from "expo-constants";
import * as Network from "expo-network";
import { useEffect } from "react";
import { AppState } from "react-native";

import {
  mapReachabilityToConnectivity,
  resolveBackendAwareConnectivity,
} from "./network-status";
import {
  type ConnectivityStatus,
  usePosShellStore,
} from "./pos-shell-store";

import { createSettingsApiHealthProbe } from "@/core/runtime/expo-settings-configuration";
import { resolveHbposApiUrl } from "@/core/runtime/runtime-config";
import { ExpoSecureStoreAdapter } from "@/core/security/expo-secure-store";
import {
  normalizeTrustedApiOrigins,
  PosPublicRuntimeConfigurationStore,
} from "@/core/security/pos-public-runtime-configuration";


/** 后端健康探测周期：后端停止后最多一个周期内收银页翻转为离线。 */
const BACKEND_PROBE_INTERVAL_MS = 30_000;
/** 单次后端健康探测超时。 */
const BACKEND_PROBE_TIMEOUT_MS = 5_000;

type HbposExtraConfig = Readonly<{
  hbpos?: Readonly<{
    apiBaseUrl?: string;
    trustedApiOrigins?: readonly string[];
  }>;
}>;

/**
 * 解析当前 API 基础地址（与组合根 createExpoPosRuntimeServices 一致：
 * Keychain 持久化配置优先，app.config extra 兜底）。
 */
export async function resolveCurrentApiBaseUrl(): Promise<string> {
  const extra = Constants.expoConfig?.extra as HbposExtraConfig | undefined;
  const trustedApiOrigins = normalizeTrustedApiOrigins([
    ...(extra?.hbpos?.trustedApiOrigins ?? []),
    ...(extra?.hbpos?.apiBaseUrl
      ? [extra.hbpos.apiBaseUrl]
      : [resolveHbposApiUrl(undefined)]),
  ]);
  try {
    const store = new PosPublicRuntimeConfigurationStore(
      new ExpoSecureStoreAdapter(),
      trustedApiOrigins,
    );
    const persisted = await store.load();
    return resolveHbposApiUrl(
      persisted.apiBaseUrl ?? extra?.hbpos?.apiBaseUrl,
    );
  } catch {
    // Keychain 读取失败不阻断网络状态显示，退回配置/默认地址。
    return resolveHbposApiUrl(extra?.hbpos?.apiBaseUrl);
  }
}

/**
 * 网络状态桥接：把“后端可达性”纳入收银页 connectivity 判定。
 *
 * 背景：仅靠 expo-network 只能判断设备网络，后端服务停止（如 API 进程退出）
 * 时设备 Wi-Fi 仍正常，收银页会误报“在线”。这里叠加后端 /api/v1/health
 * 探测：以后端实际可达性为准，避免系统网络状态误报导致可用后端被判离线。
 *
 * 触发时机：挂载、系统网络变化、30 秒周期、App 回到前台。
 */
export function NetworkStatusBridge() {
  const setConnectivity = usePosShellStore(
    (state) => state.setConnectivity,
  );

  useEffect(() => {
    let active = true;
    // 最近一次设备网络状态（expo-network 判定结果）。
    let deviceStatus: ConnectivityStatus = "checking";
    // 最近一次后端 health 探测结果（null = 尚未完成探测）。
    let backendReachable: boolean | null = null;
    let probeTimer: ReturnType<typeof setInterval> | null = null;
    // 每次 probe 分配单调代次；多来源并发时只允许最新结果发布。
    let probeGeneration = 0;

    // 健康探测经入口感知的 fetch：边缘入口不通时改探直连，避免整台设备被误判离线进入仅现金模式。
    const probe = createSettingsApiHealthProbe(
      createFailoverFetch((url, init) => fetch(url, init)),
    );

    // 依据最近一次设备状态与后端探测结果发布最终 connectivity。
    const publish = () => {
      if (!active) return;
      setConnectivity(
        resolveBackendAwareConnectivity(deviceStatus, backendReachable),
      );
    };

    // 探测后端 health：结果写入 backendReachable 并重新发布。
    const probeBackend = async () => {
      const generation = ++probeGeneration;
      try {
        const apiBaseUrl = await resolveCurrentApiBaseUrl();
        const controller = new AbortController();
        const timeoutId = setTimeout(
          () => controller.abort(),
          BACKEND_PROBE_TIMEOUT_MS,
        );
        let ok = false;
        try {
          ok = await probe(
            `${apiBaseUrl}/api/v1/health`,
            controller.signal,
          );
        } finally {
          clearTimeout(timeoutId);
        }
        if (!active || generation !== probeGeneration) return;
        backendReachable = ok;
      } catch {
        if (!active || generation !== probeGeneration) return;
        backendReachable = false;
      }
      publish();
    };

    // 设备网络变化后旧探测不再代表当前链路，先失效再立即重新验证后端。
    const applyDeviceState = (state: Parameters<typeof mapReachabilityToConnectivity>[0]) => {
      deviceStatus = mapReachabilityToConnectivity(state);
      backendReachable = null;
      probeGeneration += 1;
      publish();
      void probeBackend();
    };

    // 挂载：系统网络状态仅作提示；即使读取失败也必须实测 POS 后端。
    void Network.getNetworkStateAsync()
      .then((state) => {
        if (active) applyDeviceState(state);
      })
      .catch(() => {
        if (active) {
          deviceStatus = "checking";
          backendReachable = null;
          probeGeneration += 1;
          publish();
          void probeBackend();
        }
      });

    const subscription = Network.addNetworkStateListener((state) => {
      applyDeviceState(state);
    });

    // 周期探测后端：后端停止/恢复后最多一个周期内状态翻转。
    probeTimer = setInterval(() => {
      void probeBackend();
    }, BACKEND_PROBE_INTERVAL_MS);

    // App 回到前台立即探测一次，避免等待周期。
    const appStateSubscription = AppState.addEventListener(
      "change",
      (next) => {
        if (next === "active") {
          void probeBackend();
        }
      },
    );

    return () => {
      active = false;
      // 卸载使全部在途 probe 失效，避免异步完成后覆盖下一次挂载状态。
      probeGeneration += 1;
      subscription.remove();
      appStateSubscription.remove();
      if (probeTimer) {
        clearInterval(probeTimer);
      }
    };
  }, [setConnectivity]);

  return null;
}
