import { Redirect, router, type Href } from "expo-router";
import { useTranslation } from "react-i18next";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  resolvePosEntryRoute,
  useCashierLoginStore,
} from "@/features/cashier-login";
import { PosEntryScreen, useOrderingSessionStore } from "@/features/ordering";
import { toggleAppLanguage } from "@/i18n";
import { BootstrapScreen } from "@/ui/screens/bootstrap-screen";
import { usePosThemeVersion } from "@/ui/theme";

/**
 * App 首页。
 *
 * 中文注释：
 * - 收银员已登录：照旧直接进收银页，开店不用多点一下。
 * - 设备状态已确定（未注册 / 待审批 / 需恢复 / 已注册）：显示「收银」「订货」两个入口；
 *   收银去注册流程或收银员登录（由 resolvePosEntryRoute 决定），订货去员工账号登录。
 * - 运行环境还在启动：显示启动页。
 */
export default function IndexScreen() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const { i18n } = useTranslation();
  const activeCashier = useCashierLoginStore(
    (state) => state.activeCashier,
  );
  const orderingUser = useOrderingSessionStore((state) => state.user);
  const target = resolvePosEntryRoute(runtime.state, activeCashier);

  if (target === "/sales") {
    return <Redirect href={target as Href} />;
  }

  if (target) {
    return (
      <PosEntryScreen
        language={i18n.resolvedLanguage ?? i18n.language}
        onCashier={() => router.push(target as Href)}
        onOpenNetworkCheck={() => router.push("/network-check" as Href)}
        onOrdering={() =>
          router.push((orderingUser ? "/ordering" : "/ordering-login") as Href)
        }
        onSwitchLanguage={() => void toggleAppLanguage()}
        registered={target === "/login"}
      />
    );
  }

  return (
    <BootstrapScreen
      onOpenNetworkCheck={() => router.push("/network-check" as Href)}
    />
  );
}
