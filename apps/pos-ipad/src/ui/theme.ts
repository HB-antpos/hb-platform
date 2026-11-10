import { useSyncExternalStore } from "react";
import { StyleSheet } from "react-native";
import { MD3DarkTheme, MD3LightTheme, type MD3Theme } from "react-native-paper";

import {
  DEFAULT_POS_THEME_ID,
  isPosThemeId,
  POS_THEME_IDS,
  POS_THEMES,
  type PosPalette,
  type PosThemeDefinition,
  type PosThemeId,
} from "./theme-palettes";

export {
  DEFAULT_POS_THEME_ID,
  isPosThemeId,
  POS_THEME_IDS,
  POS_THEMES,
  type PosPalette,
  type PosThemeDefinition,
  type PosThemeId,
};

/**
 * 当前主题是模块级状态：样式表在渲染时按它惰性求值，组件用
 * usePosThemeVersion 订阅变化。不放进 React Context，是为了让没有包
 * Provider 的独立渲染（测试、外接客显的第二个 React surface）也能直接工作。
 */
let activeThemeId: PosThemeId = DEFAULT_POS_THEME_ID;
let themeVersion = 0;
const listeners = new Set<() => void>();

export function getActivePosThemeId(): PosThemeId {
  return activeThemeId;
}

export function getActivePosTheme(): PosThemeDefinition {
  return POS_THEMES[activeThemeId];
}

/** 切换当前主题；相同主题不触发重绘。只改内存状态，持久化由调用方负责。 */
export function applyPosTheme(themeId: PosThemeId): void {
  if (!isPosThemeId(themeId) || themeId === activeThemeId) return;
  activeThemeId = themeId;
  themeVersion += 1;
  for (const listener of [...listeners]) listener();
}

export function subscribePosTheme(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function getThemeVersion(): number {
  return themeVersion;
}

/**
 * 让组件在主题切换时重绘。
 *
 * 规则：凡是不会随父组件一起重绘的组件都要调用它——每个页面的根组件、
 * React.memo 包裹的组件、FlatList 的行组件。普通子组件跟着父组件重绘即可。
 */
export function usePosThemeVersion(): number {
  return useSyncExternalStore(
    subscribePosTheme,
    getThemeVersion,
    getThemeVersion,
  );
}

/** 读取当前主题定义，并在主题切换时重绘。 */
export function usePosTheme(): PosThemeDefinition {
  usePosThemeVersion();
  return POS_THEMES[activeThemeId];
}

/**
 * 始终指向当前主题色板的活引用。
 *
 * 只能在渲染期间或 createPosStyles 的工厂函数里读取；在模块顶层读取会把
 * 颜色定死在启动时的主题上。
 */
export const posColors: PosPalette = new Proxy({} as PosPalette, {
  get(_target, key) {
    return POS_THEMES[activeThemeId].palette[key as keyof PosPalette];
  },
  has(_target, key) {
    return key in POS_THEMES[activeThemeId].palette;
  },
  ownKeys() {
    return Reflect.ownKeys(POS_THEMES[activeThemeId].palette);
  },
  getOwnPropertyDescriptor(_target, key) {
    const value = POS_THEMES[activeThemeId].palette[key as keyof PosPalette];
    return value === undefined
      ? undefined
      : { configurable: true, enumerable: true, value, writable: false };
  },
});

type NamedStyles<T> = StyleSheet.NamedStyles<T>;

/**
 * 跟随主题的样式表：用法与 StyleSheet.create 相同，但传入的是工厂函数。
 *
 * 每套主题第一次用到时求值一次并缓存；返回的对象在读取 styles.xxx 时才
 * 解析到当前主题，所以组件里照旧写 styles.xxx 即可，换主题后重绘就拿到新色。
 */
export function createPosStyles<T extends NamedStyles<T> | NamedStyles<unknown>>(
  factory: (colors: PosPalette, theme: PosThemeDefinition) => T,
): T {
  const cache = new Map<PosThemeId, T>();
  const resolve = (): T => {
    const cached = cache.get(activeThemeId);
    if (cached) return cached;
    const theme = POS_THEMES[activeThemeId];
    const created = StyleSheet.create(factory(theme.palette, theme)) as T;
    cache.set(activeThemeId, created);
    return created;
  };

  return new Proxy({} as T, {
    get(_target, key) {
      return resolve()[key as keyof T];
    },
    has(_target, key) {
      return key in (resolve() as object);
    },
    ownKeys() {
      return Reflect.ownKeys(resolve() as object);
    },
    getOwnPropertyDescriptor(_target, key) {
      const value = resolve()[key as keyof T];
      return value === undefined
        ? undefined
        : { configurable: true, enumerable: true, value, writable: false };
    },
  });
}

/** 按主题生成 react-native-paper 的 MD3 主题，供 PaperProvider 使用。 */
export function buildPosPaperTheme(themeId: PosThemeId): MD3Theme {
  const theme = POS_THEMES[themeId];
  const colors = theme.palette;
  const base = theme.dark ? MD3DarkTheme : MD3LightTheme;
  return {
    ...base,
    roundness: 2,
    colors: {
      ...base.colors,
      primary: colors.accent,
      onPrimary: colors.onAccent,
      primaryContainer: colors.accentSoft,
      onPrimaryContainer: colors.ink,
      secondary: colors.blue,
      onSecondary: colors.onAccent,
      background: colors.canvas,
      surface: colors.surface,
      onSurface: colors.ink,
      outline: colors.border,
    },
  };
}

/** 默认主题的 Paper 主题；需要跟随切换的地方请用 buildPosPaperTheme。 */
export const posTheme: MD3Theme = buildPosPaperTheme(DEFAULT_POS_THEME_ID);

/**
 * 触控尺寸（pt）。44 是系统最低线，只用于次要的行内小键；
 * 收银场景的常规按钮从 56 起，每屏唯一的主按钮用 88。
 */
export const posTouch = Object.freeze({
  min: 44,
  small: 48,
  medium: 56,
  button: 64,
  large: 72,
  primary: 88,
});

export const posRadius = Object.freeze({
  small: 8,
  medium: 10,
  large: 12,
  sheet: 16,
});
