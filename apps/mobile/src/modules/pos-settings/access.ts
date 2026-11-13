import { POS_SETTINGS_PERMISSIONS } from "./constants";

/**
 * 收银端设置入口可见性：已登录、非 iOS 审核离线会话、且有 PosSettings.View。
 * 审核模式没有离线演示数据，真实门店收银配置不进审核壳。
 */
export function canViewPosSettings(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
): boolean {
  return isAuthenticated && !isReview && hasPermission(POS_SETTINGS_PERMISSIONS.view);
}

/** 只有 PosSettings.Manage（且能看）才能编辑；只有 View 时所有分段控件只读、不显示保存按钮。 */
export function canManagePosSettings(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
): boolean {
  return (
    canViewPosSettings(isAuthenticated, hasPermission, isReview)
    && hasPermission(POS_SETTINGS_PERMISSIONS.manage)
  );
}
