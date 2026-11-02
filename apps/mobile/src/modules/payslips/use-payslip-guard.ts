// 工资单页的入口守卫（只给路由外壳用）：iOS 审核会话不提供；未登录时给出说明，不发请求。
// 不再要求查看权限：没有权限的普通员工也能进，只看绑定到自己的工资单（范围由接口判定）。
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { canViewPayslips } from "./access";

export function usePayslipGuard(): string | null {
  const { t } = useAppTranslation("payslips");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!canViewPayslips(isAuthenticated, review)) return t("messages.notAllowed");
  return null;
}
