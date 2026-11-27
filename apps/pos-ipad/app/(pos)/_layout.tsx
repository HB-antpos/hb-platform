import { Stack } from "expo-router";

import { PosShellHost, posShellScreenLayout } from "@/ui/shell/pos-shell-host";
import { posColors, usePosThemeVersion } from "@/ui/theme";

/** 内容区切页淡入淡出的时长：够看出换了页，又不拖慢收银员连续点侧栏。 */
export const POS_SHELL_CONTENT_FADE_MS = 180;

/**
 * 带左侧栏的功能页（收银、特殊商品、侧栏各入口）共用的布局：
 * 顶栏 + 左侧栏常驻在 PosShellHost 里，切换页面时不卸载；
 * 下面这个路由栈只占内容区，切页时只有内容区淡入淡出。
 * 付款、登录、注册等子流程页面不在这个分组里，照旧整页进入。
 */
export default function PosShellLayout() {
  usePosThemeVersion();
  return (
    <PosShellHost>
      <Stack
        // 每一页包一层焦点跟踪：只有当前页能把自己的侧栏高亮 / 锁定等配置登记给外壳。
        screenLayout={posShellScreenLayout}
        screenOptions={{
          animation: "fade",
          animationDuration: POS_SHELL_CONTENT_FADE_MS,
          contentStyle: { backgroundColor: posColors.canvas },
          headerShown: false,
        }}
      />
    </PosShellHost>
  );
}
