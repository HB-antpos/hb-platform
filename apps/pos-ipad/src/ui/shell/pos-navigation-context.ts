import { createContext, useContext } from "react";

import type { PosNavModel } from "./pos-app-rail";

export type PosNavigationContextValue = Readonly<{
  /** 当前收银员可用的入口、侧栏固定项和各入口的跳转。 */
  model: PosNavModel;
  /** 回到收银页（等同各页面原来的「返回收银」）。 */
  openSales(): void;
  switchLanguage(): void;
  language: string;
  appVersionLabel: string | null;
}>;

/**
 * 应用内由路由层的 PosNavigationBridge 提供。
 * 没有 Provider（页面组件单独渲染、单元测试）时为 null：页面不显示左侧栏，
 * 只保留自己的「返回」按钮，行为与改版前一致。
 */
export const PosNavigationContext =
  createContext<PosNavigationContextValue | null>(null);

export function usePosNavigation(): PosNavigationContextValue | null {
  return useContext(PosNavigationContext);
}
