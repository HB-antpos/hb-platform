import { FACE_MATCH_PERMISSIONS } from "./constants";
import type { FaceMatchConfig, FaceMatchPermissionFlags } from "./types";

type HasPermission = (permission: string) => boolean;

/**
 * 名单页入口可见性：已登录、非 iOS 审核离线会话，且持有名单 Manage / Approve / Review 任一权限。
 * 参考照片与命中抓拍是真实的生物识别信息，审核模式没有离线演示数据，不进审核壳。
 */
export function canViewFaceUnwelcome(
  isAuthenticated: boolean,
  hasPermission: HasPermission,
  isReview: boolean,
): boolean {
  return (
    isAuthenticated &&
    !isReview &&
    (hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeManage) ||
      hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeApprove) ||
      hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeReview))
  );
}

/** 设置页入口可见性：持有 FaceMatch.Manage 或 FaceCapture.Cameras.Manage 任一权限。 */
export function canViewFaceMatchSettings(
  isAuthenticated: boolean,
  hasPermission: HasPermission,
  isReview: boolean,
): boolean {
  return (
    isAuthenticated &&
    !isReview &&
    (hasPermission(FACE_MATCH_PERMISSIONS.manage) || hasPermission(FACE_MATCH_PERMISSIONS.camerasManage))
  );
}

export interface FaceSettingsSections {
  /** 门店档案区需要 FaceMatch.Manage。 */
  profile: boolean;
  /** 摄像头区需要 FaceCapture.Cameras.Manage。 */
  cameras: boolean;
}

/** 设置页两个区域各自的所需权限：入口只要任一权限，进来后每个区域仍按自己的权限显示。 */
export function resolveSettingsSections(hasPermission: HasPermission): FaceSettingsSections {
  return {
    profile: hasPermission(FACE_MATCH_PERMISSIONS.manage),
    cameras: hasPermission(FACE_MATCH_PERMISSIONS.camerasManage),
  };
}

export type FaceUnwelcomeTabKey = "entries" | "review";

/**
 * 按 GET /face-match/config 返回的权限标志决定名单页的分区（顺序即页面顺序）：
 * 条目：Manage 或 Approve；命中复核：Review。与 Web 的 getVisibleTabs 同口径。
 */
export function getVisibleUnwelcomeTabs(permissions: FaceMatchPermissionFlags): FaceUnwelcomeTabKey[] {
  const tabs: FaceUnwelcomeTabKey[] = [];
  if (permissions.unwelcomeManage || permissions.unwelcomeApprove) tabs.push("entries");
  if (permissions.unwelcomeReview) tabs.push("review");
  return tabs;
}

/** 功能未开启（缺腾讯云地域 / 凭据）时只能查看，写入类操作禁用。 */
export function isWriteBlocked(config: Pick<FaceMatchConfig, "enabled"> | null | undefined): boolean {
  return !config?.enabled;
}

/** 提交带照片的条目还需要私有存储桶已配置。 */
export function canSubmitEntries(
  config: Pick<FaceMatchConfig, "enabled" | "objectStoreConfigured" | "permissions"> | null | undefined,
): boolean {
  return Boolean(config && config.enabled && config.objectStoreConfigured && config.permissions.unwelcomeManage);
}
