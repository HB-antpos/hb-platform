import { createContext, useContext } from "react";

import { applyPosTheme, type PosThemeId } from "./theme";

export type PosThemeController = Readonly<{
  /** 切换主题并（在应用内）持久化；Provider 外只改当前会话。 */
  selectTheme(themeId: PosThemeId): void;
}>;

const SESSION_ONLY_CONTROLLER: PosThemeController = {
  selectTheme: applyPosTheme,
};

/**
 * 主题切换入口。界面组件只调用 selectTheme，不直接碰本地存储：
 * 持久化由 AppProviders 注入，独立渲染（测试）时退化为只改内存。
 */
export const PosThemeControllerContext = createContext<PosThemeController>(
  SESSION_ONLY_CONTROLLER,
);

export function usePosThemeController(): PosThemeController {
  return useContext(PosThemeControllerContext);
}
