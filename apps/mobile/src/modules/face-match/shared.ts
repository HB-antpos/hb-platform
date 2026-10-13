// 人脸比对页面共用的钩子：入口守卫、错误提示、门店名称映射。
import { useCallback, useMemo } from "react";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { canViewFaceMatchSettings, canViewFaceUnwelcome } from "./access";
import { describeFaceMatchError } from "./errors";
import type { ValidationIssue } from "./entry-form";

/** 名单页入口守卫：审核模式与无权限直接给出说明，不发请求。返回提示文案，null 表示放行。 */
export function useFaceUnwelcomeGuard(): string | null {
  const { t } = useAppTranslation("faceMatch");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!canViewFaceUnwelcome(isAuthenticated, hasPermission, review)) return t("messages.notAllowed");
  return null;
}

/** 设置页入口守卫。 */
export function useFaceSettingsGuard(): string | null {
  const { t } = useAppTranslation("faceMatch");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  if (review) return t("messages.reviewUnavailable");
  if (!canViewFaceMatchSettings(isAuthenticated, hasPermission, review)) return t("messages.notAllowed");
  return null;
}

/** 接口错误 → 面向用户的中英文提示（按业务错误码，不展示服务端 message / details 原文）。 */
export function useFaceErrorMessage(): (error: unknown) => string {
  const { t, language } = useAppTranslation("faceMatch");
  return useCallback((error: unknown) => describeFaceMatchError(t, language, error), [language, t]);
}

/** 校验结果 → 文案。 */
export function useIssueText(): (issue: ValidationIssue | null | undefined) => string | undefined {
  const { t } = useAppTranslation("faceMatch");
  return useCallback((issue) => (issue ? t(issue.key, issue.params) : undefined), [t]);
}

/** 门店编码 → 名称；名称缺失时用编码。 */
export function useStoreNameMap(stores: readonly Store[]): ReadonlyMap<string, string> {
  return useMemo(() => new Map(stores.map((store) => [store.storeCode, store.storeName || store.storeCode])), [stores]);
}

/** 「1008 · Bankstown」；没有名称只显示编码。 */
export function formatStoreLabel(storeCode: string, names: ReadonlyMap<string, string>): string {
  const name = names.get(storeCode);
  return name && name !== storeCode ? `${storeCode} · ${name}` : storeCode;
}
