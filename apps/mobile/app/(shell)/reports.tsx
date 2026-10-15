import { useEffect, useRef, type ComponentProps } from "react";
import { Redirect } from "expo-router";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import { buildPriceUpdatesRoute } from "@/modules/price-updates/navigation";
import { ReportsHubScreen } from "@/modules/reports/reports-hub-screen";

type RedirectHref = ComponentProps<typeof Redirect>["href"];

export default function ReportsRoute() {
  const { priceTasks, refreshPriceTasks } = useAppNavigationAccess();

  // 进入报表时重新拉一次汇总：缓存最长 30 秒旧，别人刚处理完或刚产生超期都要立即生效，
  // 结果会经上下文回流到下面的 reportsLocked，页面随之放行或拦截。
  const refreshedRef = useRef(false);
  useEffect(() => {
    if (refreshedRef.current) return;
    refreshedRef.current = true;
    void refreshPriceTasks().catch(() => undefined);
  }, [refreshPriceTasks]);

  // 店长管理的分店有超期任务：不能进入报表，直接跳到「价格和标签 › 超期」。
  // 在路由层拦截（而不只是工作台入口），通知、深链接、底部页签等任何入口都绕不过去；
  // Redirect 是 replace，返回键回到进入前的页面，不会在报表页和价格页之间来回弹。
  // 汇总没拿到（加载中 / 接口失败）时 reportsLocked 为 false：故障不能把人挡在报表外。
  if (priceTasks.reportsLocked) {
    return (
      <Redirect
        href={
          buildPriceUpdatesRoute({
            storeCode: priceTasks.firstOverdueStoreCode,
            overdueOnly: true,
          }) as unknown as RedirectHref
        }
      />
    );
  }

  return <ReportsHubScreen />;
}
