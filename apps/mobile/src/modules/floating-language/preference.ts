import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  DEFAULT_FLOATING_POSITION,
  FLOATING_LONG_PRESS_MS,
  normalizeFloatingPosition,
  normalizeLongPressMs,
  type FloatingPosition,
} from "./geometry";

export const FLOATING_LANGUAGE_STORAGE_KEY = "hb.ui.floatingLanguage.v1";

export interface FloatingLanguagePreference {
  /** 是否显示语言悬浮按钮：默认开启；用户拖到底部隐藏或在设置页关闭后才保持关闭，可在设置页（或撤销提示）重新打开。 */
  enabled: boolean;
  position: FloatingPosition;
  /** 按住多久打开快速跳转（毫秒）：默认 2 秒，设置页可调。 */
  longPressMs: number;
}

interface FloatingLanguageState extends FloatingLanguagePreference {
  hydrated: boolean;
  setEnabled: (enabled: boolean) => void;
  setPosition: (position: FloatingPosition) => void;
  setLongPressMs: (longPressMs: number) => void;
}

type PreferenceField = keyof FloatingLanguagePreference;

/**
 * 读回本机存的偏好：只有用户明确关过（存了 false）才算关闭，没存过 / 存坏了都按默认「开启」；
 * 位置、按住时长坏了各自回落到默认。
 */
export function normalizeFloatingLanguagePreference(raw: unknown): FloatingLanguagePreference {
  const value = (raw ?? {}) as { enabled?: unknown; position?: unknown; longPressMs?: unknown };
  return {
    enabled: value.enabled !== false,
    position: normalizeFloatingPosition(value.position),
    longPressMs: normalizeLongPressMs(value.longPressMs),
  };
}

// 读盘完成前用户已经改过哪几项：这几项以用户这次的操作为准，其余项仍用存储里的值（按字段合并）。
const changedBeforeHydrate = new Set<PreferenceField>();

function persist(state: FloatingLanguagePreference & { hydrated?: boolean }) {
  // 读盘完成前不写：否则会用默认值盖掉存储里其它项的真实值；读盘合并后会统一写一次。
  if (state.hydrated === false) return;
  void AppAsyncStorage.setObject(FLOATING_LANGUAGE_STORAGE_KEY, {
    enabled: state.enabled,
    position: state.position,
    longPressMs: state.longPressMs,
  }).catch(() => undefined);
}

/**
 * 语言悬浮按钮是这台手机的偏好（设备级）：不含身份信息，退出登录 / 换账号都不清。
 */
export const useFloatingLanguageStore = create<FloatingLanguageState>((set, get) => ({
  enabled: true,
  position: DEFAULT_FLOATING_POSITION,
  longPressMs: FLOATING_LONG_PRESS_MS,
  hydrated: false,
  setEnabled: (enabled) => {
    changedBeforeHydrate.add("enabled");
    set({ enabled });
    persist(get());
  },
  setPosition: (position) => {
    changedBeforeHydrate.add("position");
    set({ position: normalizeFloatingPosition(position) });
    persist(get());
  },
  setLongPressMs: (longPressMs) => {
    changedBeforeHydrate.add("longPressMs");
    set({ longPressMs: normalizeLongPressMs(longPressMs) });
    persist(get());
  },
}));

let hydratePromise: Promise<void> | null = null;

/** 壳布局挂载时读一次；读失败保持默认（开启、2 秒），不阻塞启动。读盘完成前按钮不显示，已关闭的人不会看到一闪。 */
export function hydrateFloatingLanguagePreference() {
  if (!hydratePromise) {
    hydratePromise = AppAsyncStorage.getObject(FLOATING_LANGUAGE_STORAGE_KEY)
      .then((stored) => {
        const loaded = normalizeFloatingLanguagePreference(stored);
        const patch: Partial<FloatingLanguagePreference> = {};
        if (!changedBeforeHydrate.has("enabled")) patch.enabled = loaded.enabled;
        if (!changedBeforeHydrate.has("position")) patch.position = loaded.position;
        if (!changedBeforeHydrate.has("longPressMs")) patch.longPressMs = loaded.longPressMs;
        useFloatingLanguageStore.setState(patch);
      })
      .catch(() => undefined)
      .finally(() => {
        useFloatingLanguageStore.setState({ hydrated: true });
        // 读盘前改过的项此时才落盘（与存储里的其它项合并后一起写）。
        if (changedBeforeHydrate.size > 0) {
          persist(useFloatingLanguageStore.getState());
        }
      });
  }
  return hydratePromise;
}

/** 仅供单测：重置模块级状态。 */
export function resetFloatingLanguagePreferenceForTests() {
  changedBeforeHydrate.clear();
  hydratePromise = null;
  useFloatingLanguageStore.setState({
    enabled: true,
    position: DEFAULT_FLOATING_POSITION,
    longPressMs: FLOATING_LONG_PRESS_MS,
    hydrated: false,
  });
}
