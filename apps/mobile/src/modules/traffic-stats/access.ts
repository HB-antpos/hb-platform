import { PERMISSIONS } from "@/shared/utils/access";

type HasPermission = (permission: string) => boolean;

/**
 * 客流统计入口可见性：已登录、非 iOS 审核离线会话，且持有 FaceMatch.Traffic.View
 * （与后端 FullAppMenu 的 traffic-stats、接口策略同一权限）。审核模式没有离线演示数据，不进审核壳。
 */
export function canViewTrafficStats(isAuthenticated: boolean, hasPermission: HasPermission, isReview: boolean): boolean {
  return isAuthenticated && !isReview && hasPermission(PERMISSIONS.FaceMatch.Traffic.View);
}
