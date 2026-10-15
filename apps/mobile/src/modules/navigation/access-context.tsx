import { createContext, useContext, type ReactNode } from "react";
import type { PriceTaskOverview } from "@/modules/price-updates/overview";

export interface AppNavigationAccessValue {
  orderedVisibleRouteNames: readonly string[];
  navigationErrorMessage: string | null;
  navigationLoading: boolean;
  pendingProfileReviewCount: number;
  /** 价格和标签未完成任务合计（账号 = 名下全部分店，设备 = 绑定分店），用于工作台入口角标。 */
  pendingPriceUpdateCount: number;
  /**
   * 价格和标签任务概览：每家分店的未完成 / 超期数，以及「管理分店有超期 → 报表锁定」的判定。
   * 工作台、报表页、每小时提醒、价格和标签页共用同一份，保证数字和锁定状态一致。
   */
  priceTasks: PriceTaskOverview;
  /** 立即重新拉取汇总（进入报表页时用，避免拿旧缓存误放行 / 误拦截）。 */
  refreshPriceTasks: () => Promise<PriceTaskOverview | null>;
  /** 当前分店 HB新品 列表里的商品品种数（按商品去重），用于工作台入口角标。 */
  newProductKindCount: number;
  /**
   * 当前登录人还没看的「黑名单命中」通知数（GET /notifications/unread-count?type=face.unwelcome-hit），
   * 用于工作台「人脸比对」入口角标、底部「工作台」页签红色徽标、合并页「抓拍 / 名单」页签。没有权限或取不到时为 0。
   */
  faceHitUnreadCount: number;
  isDeviceMode: boolean;
  isWarehouseStaffOnly: boolean;
}

const AppNavigationAccessContext = createContext<AppNavigationAccessValue | null>(null);

export function AppNavigationAccessProvider({
  value,
  children,
}: {
  value: AppNavigationAccessValue;
  children: ReactNode;
}) {
  return (
    <AppNavigationAccessContext.Provider value={value}>
      {children}
    </AppNavigationAccessContext.Provider>
  );
}

export function useAppNavigationAccess() {
  const context = useContext(AppNavigationAccessContext);
  if (!context) {
    throw new Error("useAppNavigationAccess 必须在 AppNavigationAccessProvider 内使用");
  }
  return context;
}
