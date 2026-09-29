export const APP_INSTALL_PERMISSION = "System.ViewMobileAppInstallLinks";

/** 安装页只认独立权限；iOS 审核演示模式没有真实版本数据，一律不开放。 */
export function canViewAppInstall(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
) {
  return isAuthenticated && !isReview && hasPermission(APP_INSTALL_PERMISSION);
}
