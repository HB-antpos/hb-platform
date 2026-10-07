import { useCallback } from "react";
import { StyleSheet } from "react-native";
import { Button, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAuthStore } from "@/store/auth-store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { classifyDailyCloseError, describeBusinessDate } from "./logic";

/** 入口守卫：审核模式与无权限直接给出说明，不发请求。返回提示文案，null 表示放行。 */
export function useDailyClosesGuard() {
  const { t } = useAppTranslation("dailyCloses");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const canView = useAuthStore((state) => state.access.canViewDailyCloseRecords);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  if (review || sessionKind === "iosReview") return t("messages.reviewUnavailable");
  if (!isAuthenticated || !canView) return t("messages.notAllowed");
  return null;
}

export function DailyCloseScreenMessage({ message, onBack }: { message: string; onBack: () => void }) {
  const { t } = useAppTranslation("dailyCloses");
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite">{message}</Text>
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

/**
 * 接口错误转友好文案：日期区间超限（INVALID_QUERY）、无权限（403）、不存在 / 不在可见分店内（404）单独说明，
 * 其余走通用本地化错误（网络、超时、5xx）。fallbackKey 区分列表与明细的兜底文案。
 */
export function useDailyCloseErrorMessage(fallbackKey: "errors.loadFailed" | "errors.detailFailed") {
  const { t, language } = useAppTranslation("dailyCloses");
  return useCallback(
    (cause: unknown) => {
      const kind = classifyDailyCloseError(cause);
      if (kind === "invalidQuery") return t("errors.invalidQuery");
      if (kind === "forbidden") return t("errors.forbidden");
      if (kind === "notFound") return t("errors.notFound");
      return resolveLocalizedErrorMessage(cause, { t, language, fallbackKey, allowRawMessageInChinese: false });
    },
    [fallbackKey, language, t],
  );
}

/** 营业日显示：「10月6日 周二」/「Tue 6 Oct」；日期无效时回退显示原字符串。 */
export function useBusinessDateLabel() {
  const { t } = useAppTranslation("dailyCloses");
  return useCallback(
    (businessDate: string) => {
      const parts = describeBusinessDate(businessDate);
      if (!parts) return businessDate || "-";
      return t("section.dayHeading", { month: t(`months.${parts.month}`), day: parts.day, weekday: t(`weekdays.${parts.weekday}`) });
    },
    [t],
  );
}

const styles = StyleSheet.create({
  message: { flex: 1, gap: HB_SPACING.md, padding: HB_SPACING.lg, justifyContent: "center", backgroundColor: HB_COLORS.background },
});
