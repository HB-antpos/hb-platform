import { createContext, useContext, type ReactNode } from "react";

export interface AppNavigationAccessValue {
  orderedVisibleRouteNames: readonly string[];
  navigationErrorMessage: string | null;
  navigationLoading: boolean;
  pendingProfileReviewCount: number;
  /** 当前分店未完成的价格更新任务数，用于工作台入口角标。 */
  pendingPriceUpdateCount: number;
  /** 当前分店 HB新品 列表里的商品品种数（按商品去重），用于工作台入口角标。 */
  newProductKindCount: number;
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
