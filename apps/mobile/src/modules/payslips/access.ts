import type { Store } from "@/modules/shop/types";
import { PERMISSIONS } from "@/shared/utils/access";

type HasPermission = (permission: string) => boolean;

/**
 * 工资单入口可见性：已登录、非 iOS 审核离线会话，且持有「查看管辖分店」或「查看全部分店」任一权限
 * （与后端 FullAppMenu 的 payslips 的 AnyPermissions 一致；真正的数据范围由接口按权限过滤）。
 */
export function canViewPayslips(isAuthenticated: boolean, hasPermission: HasPermission, isReview: boolean): boolean {
  return (
    isAuthenticated &&
    !isReview &&
    (hasPermission(PERMISSIONS.Myob.Payslip.ViewManagedStore) || hasPermission(PERMISSIONS.Myob.Payslip.ViewAllStores))
  );
}

/** 持有「查看全部分店」（或管理员）时，分店筛选列出全部启用 POS 的分店；否则只列出自己管辖的（isPrimary）分店。 */
export function resolvePayslipStoreOptions(
  stores: readonly Store[],
  options: { isAdmin: boolean; hasPermission: HasPermission },
): Store[] {
  const posEnabled = stores.filter((store) => store.isActive === true);
  if (options.isAdmin || options.hasPermission(PERMISSIONS.Myob.Payslip.ViewAllStores)) return posEnabled;
  return posEnabled.filter((store) => "isPrimary" in store && store.isPrimary === true);
}
