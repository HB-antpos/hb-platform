// 带薪假子页的路由外壳：按 type 参数分到「带薪病假」或「带薪年假（休息 / 发放）」；登录态守卫在这里，Content 只收 props。
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { AnnualLeaveContent } from "./AnnualLeaveScreen";
import { LeaveScreenMessage } from "./leave-ui";
import { useLeaveGoBack, useLeaveRouteGuard, useLeaveRouteParams } from "./leave-route-guard";
import { PaidSickLeaveContent } from "./PaidSickLeaveScreen";

export function PaidLeaveScreen() {
  const { t } = useAppTranslation("attendanceLeave");
  const { storeCode, type } = useLeaveRouteParams();
  const blocked = useLeaveRouteGuard();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const goBack = useLeaveGoBack();
  if (blocked) return <LeaveScreenMessage title={t("title")} message={blocked} onBack={goBack} />;
  if (!type) return <LeaveScreenMessage title={t("title")} message={t("messages.unknownType")} onBack={goBack} />;
  // 提交成功后列表已刷新，直接回到「申请假期」首页。
  if (type === "PaidSick") {
    return <PaidSickLeaveContent key={userGuid} storeCode={storeCode} onBack={goBack} onSubmitted={goBack} />;
  }
  return <AnnualLeaveContent key={`${userGuid}-${type}`} storeCode={storeCode} type={type} onBack={goBack} onSubmitted={goBack} />;
}
