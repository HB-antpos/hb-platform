import { STORE_CASH_PERMISSIONS } from "./constants";
import type { CashCapabilities } from "./types";

/**
 * 现金入口可见性：已登录、非 iOS 审核离线会话、且有 Cash.Overview.View。
 * 审核模式没有离线演示数据，真实资金流水不进审核壳。
 */
export function canViewStoreCash(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
): boolean {
  return isAuthenticated && !isReview && hasPermission(STORE_CASH_PERMISSIONS.overviewView);
}

/** 按 context.capabilities 决定按钮显隐，不自己从角色推断。 */
export interface CashActionAccess {
  canDeposit: boolean;
  canExpense: boolean;
  /** 日结选择、期初、盘点与存款同属 Cash.Deposit.Create。 */
  canManageBalance: boolean;
}

export function resolveCashActionAccess(capabilities: CashCapabilities): CashActionAccess {
  return {
    canDeposit: capabilities.canCreateDeposit,
    canExpense: capabilities.canCreateExpense,
    canManageBalance: capabilities.canCreateDeposit,
  };
}
