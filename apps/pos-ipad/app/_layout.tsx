import { useKeepAwake } from "expo-keep-awake";
import { Stack } from "expo-router";
import * as SplashScreen from "expo-splash-screen";

import { AppProviders } from "@/app-providers";
import "@/core/peripherals/customer-display/native";
import { PosNavigationBridge } from "@/features/navigation/pos-navigation-bridge";
import { ZellerAppBoundary } from "@/features/zeller-terminal/zeller-app-boundary";
import { ScannerRouteProvider } from "@/ui/scanner/scanner-route-bridge";
import { usePosThemeVersion } from "@/ui/theme";

// 外接客显会创建第二个 React surface；主路由加载后显式移除启动遮罩，
// 避免原生自动隐藏通知被第二个 surface 消费后留下白色覆盖层。
void SplashScreen.hideAsync().catch(() => undefined);

export default function RootLayout() {
  usePosThemeVersion();
  // POS 在前台运行时保持屏幕常亮，避免闲置触发 iPadOS 自动锁屏。
  useKeepAwake();

  return (
    <AppProviders>
      <ScannerRouteProvider>
        {/* 全 App 唯一的 Zeller SDK 挂载点：默认不加载，设置里用到或运行时需要时才加载。 */}
        <ZellerAppBoundary>
          {/* 侧栏 / 面板导航桥：收银页 push，其他页 replace。 */}
          <PosNavigationBridge>
            <Stack screenOptions={{ headerShown: false }} />
          </PosNavigationBridge>
        </ZellerAppBoundary>
      </ScannerRouteProvider>
    </AppProviders>
  );
}
