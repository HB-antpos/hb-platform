// 工时统计两屏的入口守卫（只给路由外壳用，Content 不读登录态，便于免登录预览）：
// iOS 审核会话不提供；未登录或没有「查看管理分店打卡」权限（canViewAttendancePunchRecords，与后端
// payroll/hours 的 Attendance.Punch.ViewManagedStore 一致）时给出说明，不发请求。
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

export function useHoursGuard(): string | null {
  const { t } = useAppTranslation("attendanceHours");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const canView = useAuthStore((state) => state.access.canViewAttendancePunchRecords);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!isAuthenticated || !canView) return t("messages.notAllowed");
  return null;
}
