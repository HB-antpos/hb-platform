import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, View } from "react-native";

import type { PosActiveDestination } from "@/ui/shell/pos-navigation";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

/** 加载超过这么久才显示转圈：设备身份核验通常几十毫秒就结束，避免每次切页都闪一下转圈。 */
export const POS_ROUTE_LOADING_INDICATOR_DELAY_MS = 300;

/**
 * 侧栏功能页「门禁已过、presenter 还在建」这一小段的占位。
 *
 * 中文注释：
 * 1. 以前这里用的是 BootstrapScreen（开机自检页），它向常驻外壳登记「不显示左侧栏」，
 *    于是每次点侧栏进这些页，左侧栏会先消失、内容区闪出启动检查页，再恢复——看起来整页在跳。
 * 2. 这里按 rail="auto" 登记侧栏并提前高亮将要进入的页，外壳的顶栏 / 左侧栏保持不动，
 *    内容区只铺页面底色；真页面建好后无缝接上。
 * 3. 门禁没过（运行时未就绪、未登录）时路由照旧 Redirect / BootstrapScreen，不走这里。
 */
export function PosRouteLoadingScreen({
  active,
}: Readonly<{ active: PosActiveDestination }>) {
  usePosThemeVersion();
  const { t } = useTranslation();
  const [indicatorVisible, setIndicatorVisible] = useState(false);
  useEffect(() => {
    const timer = setTimeout(
      () => setIndicatorVisible(true),
      POS_ROUTE_LOADING_INDICATOR_DELAY_MS,
    );
    return () => clearTimeout(timer);
  }, []);

  return (
    <PosScreenFrame active={active} testID="pos-route-loading">
      <View
        accessibilityLabel={t("shell.routeLoading")}
        accessible
        style={styles.body}
      >
        {indicatorVisible ? (
          <ActivityIndicator
            color={posColors.mutedInk}
            size="large"
            testID="pos-route-loading-indicator"
          />
        ) : null}
      </View>
    </PosScreenFrame>
  );
}

const styles = createPosStyles((colors) => ({
  body: {
    alignItems: "center",
    backgroundColor: colors.canvas,
    flex: 1,
    justifyContent: "center",
  },
}));
