import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  DEFAULT_FLOATING_POSITION,
  normalizeFloatingPosition,
  type FloatingPosition,
} from "./geometry";

export const FLOATING_LANGUAGE_STORAGE_KEY = "hb.ui.floatingLanguage.v1";

export interface FloatingLanguagePreference {
  /** 是否显示语言悬浮按钮：默认关闭，只能在设置页（或撤销隐藏）打开。 */
  enabled: boolean;
  position: FloatingPosition;
}

interface FloatingLanguageState extends FloatingLanguagePreference {
  hydrated: boolean;
  setEnabled: (enabled: boolean) => void;
  setPosition: (position: FloatingPosition) => void;
}

/** 读回本机存的偏好：只有明确存了 true 才算开启；位置坏了回落到默认。 */
export function normalizeFloatingLanguagePreference(raw: unknown): FloatingLanguagePreference {
  const value = (raw ?? {}) as { enabled?: unknown; position?: unknown };
  return {
    enabled: value.enabled === true,
    position: normalizeFloatingPosition(value.position),
  };
}

// 读盘完成前用户已经改过某一项：这一项以这次的操作为准，其余项仍用存储里的值（按字段合并）。
let enabledChangedBeforeHydrate = false;
let positionChangedBeforeHydrate = false;

function persist(state: FloatingLanguagePreference & { hydrated?: boolean }) {
  // 读盘完成前不写：否则会用默认值盖掉存储里另一项的真实值；读盘合并后会统一写一次。
  if (state.hydrated === false) return;
  void AppAsyncStorage.setObject(FLOATING_LANGUAGE_STORAGE_KEY, {
    enabled: state.enabled,
    position: state.position,
  }).catch(() => undefined);
}

/**
 * 语言悬浮按钮是这台手机的偏好（设备级）：不含身份信息，退出登录 / 换账号都不清。
 */
export const useFloatingLanguageStore = create<FloatingLanguageState>((set, get) => ({
  enabled: false,
  position: DEFAULT_FLOATING_POSITION,
  hydrated: false,
  setEnabled: (enabled) => {
    enabledChangedBeforeHydrate = true;
    set({ enabled });
    persist(get());
  },
  setPosition: (position) => {
    positionChangedBeforeHydrate = true;
    set({ position: normalizeFloatingPosition(position) });
    persist(get());
  },
}));

let hydratePromise: Promise<void> | null = null;

/** 壳布局挂载时读一次；读失败保持默认（关闭），不阻塞启动。 */
export function hydrateFloatingLanguagePreference() {
  if (!hydratePromise) {
    hydratePromise = AppAsyncStorage.getObject(FLOATING_LANGUAGE_STORAGE_KEY)
      .then((stored) => {
        const loaded = normalizeFloatingLanguagePreference(stored);
        const patch: Partial<FloatingLanguagePreference> = {};
        if (!enabledChangedBeforeHydrate) patch.enabled = loaded.enabled;
        if (!positionChangedBeforeHydrate) patch.position = loaded.position;
        useFloatingLanguageStore.setState(patch);
      })
      .catch(() => undefined)
      .finally(() => {
        useFloatingLanguageStore.setState({ hydrated: true });
        // 读盘前改过的项此时才落盘（与存储里的另一项合并后一起写）。
        if (enabledChangedBeforeHydrate || positionChangedBeforeHydrate) {
          persist(useFloatingLanguageStore.getState());
        }
      });
  }
  return hydratePromise;
}

/** 仅供单测：重置模块级状态。 */
export function resetFloatingLanguagePreferenceForTests() {
  enabledChangedBeforeHydrate = false;
  positionChangedBeforeHydrate = false;
  hydratePromise = null;
  useFloatingLanguageStore.setState({
    enabled: false,
    position: DEFAULT_FLOATING_POSITION,
    hydrated: false,
  });
}
