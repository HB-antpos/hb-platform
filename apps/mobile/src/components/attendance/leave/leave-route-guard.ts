// 申请假期路由外壳共用：登录态 / 权限守卫、门店参数解析、返回。Content 组件不读这些，便于免登录预览。
import { useLocalSearchParams, useRouter } from "expo-router";
import type { Href } from "expo-router";
import { isPaidLeaveType, type PaidLeaveType } from "@/modules/attendance/leave-types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

/** 没登录或没有本人考勤权限时返回提示文案；可用时返回 null。 */
export function useLeaveRouteGuard(): string | null {
  const { t } = useAppTranslation("attendanceLeave");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const canApply = useAuthStore((state) => state.access.canViewAttendancePersonal);
  if (!isAuthenticated || !canApply) return t("messages.notAllowed");
  return null;
}

function firstParam(value: string | string[] | undefined): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value;
  return raw && raw.trim() ? raw.trim() : undefined;
}

export function useLeaveRouteParams(): { storeCode?: string; type?: PaidLeaveType } {
  const params = useLocalSearchParams<{ storeCode?: string | string[]; type?: string | string[] }>();
  const type = firstParam(params.type);
  return { storeCode: firstParam(params.storeCode), type: isPaidLeaveType(type) ? type : undefined };
}

/** 返回上一页；直接打开（没有上一页）时回到「我的考勤」。 */
export function useLeaveGoBack() {
  const router = useRouter();
  return () => (router.canGoBack() ? router.back() : router.replace("/attendance-personal" as Href));
}

/** 隐藏子页不在 typed routes 生成物里，统一在这里转换类型。 */
export function leaveHomeHref(storeCode?: string): Href {
  return { pathname: "/attendance-leave", params: storeCode ? { storeCode } : {} } as unknown as Href;
}

export function paidLeaveHref(type: PaidLeaveType, storeCode?: string): Href {
  return { pathname: "/attendance-leave-paid", params: storeCode ? { type, storeCode } : { type } } as unknown as Href;
}
