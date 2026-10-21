// 「自动更新」本机偏好（设置页开关，默认开启）。
// 关闭后：不再自动检查 / 下载 / 提醒可选的 OTA、APK、App Store 更新；
// 总部标记的「强制更新」不受影响，仍照常检查并拦截。手动「检查更新」也不受影响。
// 只存一个布尔值（"1" / "0"），是设备级偏好，不含身份信息，退出登录 / 换账号都不清。
// 读写失败只是不记住，不影响更新通道；读不到按默认「开启」。
import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";

export const AUTO_UPDATE_STORAGE_KEY = "hb.updates.autoUpdate.v1";

/** 只有明确存了 "0" 才算关闭；没存过、存坏了都按默认「开启」。 */
export function parseAutoUpdateEnabled(raw: string | null | undefined): boolean {
  return raw !== "0";
}

/**
 * 更新 hook 实际使用的「允许自动更新可选版本」判定。
 * 读盘完成前一律按「关」处理：宁可晚一点放开，也不能让已关闭的用户在冷启动被默认值触发可选更新。
 */
export function isAutoUpdateAllowed(state: { hydrated: boolean; enabled: boolean }): boolean {
  return state.hydrated && state.enabled;
}

interface AutoUpdatePreferenceState {
  enabled: boolean;
  hydrated: boolean;
  setEnabled: (enabled: boolean) => void;
}

// 读盘完成前用户已经点过开关：以用户这次的选择为准，不被旧值覆盖。
let changedBeforeHydrate = false;

export const useAutoUpdatePreferenceStore = create<AutoUpdatePreferenceState>((set, get) => ({
  enabled: true,
  hydrated: false,
  setEnabled: (enabled) => {
    changedBeforeHydrate = true;
    set({ enabled });
    // 读盘完成前不写：读盘合并后会统一落盘一次。
    if (get().hydrated) {
      void AppAsyncStorage.setString(AUTO_UPDATE_STORAGE_KEY, enabled ? "1" : "0").catch(() => undefined);
    }
  },
}));

let hydratePromise: Promise<void> | null = null;

/** 启动时读盘一次；永不 reject（读失败按默认开启），可放心并入启动就绪任务。 */
export function hydrateAutoUpdatePreference(): Promise<void> {
  if (!hydratePromise) {
    hydratePromise = (async () => {
      let loaded = true;
      try {
        loaded = parseAutoUpdateEnabled(await AppAsyncStorage.getString(AUTO_UPDATE_STORAGE_KEY));
      } catch {
        loaded = true;
      }
      if (changedBeforeHydrate) {
        useAutoUpdatePreferenceStore.setState({ hydrated: true });
        void AppAsyncStorage
          .setString(AUTO_UPDATE_STORAGE_KEY, useAutoUpdatePreferenceStore.getState().enabled ? "1" : "0")
          .catch(() => undefined);
      } else {
        useAutoUpdatePreferenceStore.setState({ enabled: loaded, hydrated: true });
      }
    })();
  }
  return hydratePromise;
}

/** 仅供测试：清空内存状态，模拟冷启动。 */
export function resetAutoUpdatePreferenceForTest(): void {
  changedBeforeHydrate = false;
  hydratePromise = null;
  useAutoUpdatePreferenceStore.setState({ enabled: true, hydrated: false });
}
