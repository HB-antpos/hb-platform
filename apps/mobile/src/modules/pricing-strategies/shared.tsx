import { useCallback, useRef, useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAuthStore } from "@/store/auth-store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import type { DraftIssue } from "./logic";
import { parseDecimal } from "./logic";
import type { PricingIssue } from "./pricing-intervals";

export const PRICING_COLORS = {
  gold: "#B54708",
  goldBg: "#FFFAEB",
  goldBorder: "#FEDF89",
  blue: HB_COLORS.brand,
  blueBg: "#EFF8FF",
  blueBorder: "#B2DDFF",
  orange: "#F79009",
  legalBand: "#ECFDF3",
  okBorder: "#ABEFC6",
  dropBg: "#FFFAEB",
  muted: "#98A2B3",
};

/**
 * 入口守卫：审核模式与无查看权限直接给出说明，不发请求。canEdit 与后端 PricingStrategy.Edit 对齐，
 * 只有查看权限的账号可以看与试算，界面隐藏所有写操作（后端仍会再校验一次）。
 */
export function usePricingGuard() {
  const { t } = useAppTranslation("pricingStrategies");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const access = useAuthStore((state) => state.access);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const canView = access.hasPermission(PERMISSIONS.PricingStrategy.View);
  const canEdit = canView && access.hasPermission(PERMISSIONS.PricingStrategy.Edit);
  let blocked: string | null = null;
  if (review || sessionKind === "iosReview") blocked = t("messages.reviewUnavailable");
  else if (!isAuthenticated || !canView) blocked = t("messages.notAllowed");
  return { blocked, canEdit };
}

export function PricingScreenMessage({ message, onBack }: { message: string; onBack: () => void }) {
  const { t } = useAppTranslation("pricingStrategies");
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite">{message}</Text>
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

/** 区间逻辑 / 表单校验的问题码 → 当前语言的文案。 */
export function useIssueText() {
  const { t } = useAppTranslation("pricingStrategies");
  return useCallback(
    (issue: PricingIssue | DraftIssue | null | undefined): string => {
      if (!issue) return "";
      if ("scope" in issue) return issue.scope === "draft" ? t(`errors.${issue.code}`) : t(`errors.${issue.issue.code}`, issue.issue.params);
      return t(`errors.${issue.code}`, issue.params);
    },
    [t],
  );
}

/**
 * 接口错误转文案：403 / 404 单独说明；后端校验失败（200 + success:false）的原因是中文，中文界面原样显示，
 * 英文界面退回通用文案（不把中文原因直接显示给英文用户）。
 */
export function useApiErrorText() {
  const { t, language } = useAppTranslation("pricingStrategies");
  return useCallback(
    (cause: unknown, fallbackKey: string) => {
      const status = (cause as { response?: { status?: number } })?.response?.status;
      if (status === 403) return t("errors.forbidden");
      if (status === 404) return t("errors.notFound");
      return resolveLocalizedErrorMessage(cause, { t, language, fallbackKey, allowRawMessageInChinese: true });
    },
    [language, t],
  );
}

/**
 * 数字输入框：输入过程中只保存草稿、不校验，失焦或按键盘「完成」才提交。
 * 与 Web 的 NumberField 同一个理由：每敲一个字符就校验，会把「想输 20、敲到 2 时小于下限」这类中间状态拒掉，输入像被锁死。
 * onCommit 返回 false（校验不通过）时还原为原值，错误由外层显示。
 */
export function CommitNumberField({
  value,
  format,
  onCommit,
  label,
  disabled,
  right,
  accessibilityLabel,
}: {
  value: number;
  format: (value: number) => string;
  onCommit: (value: number) => boolean;
  label?: string;
  disabled?: boolean;
  right?: string;
  accessibilityLabel: string;
}) {
  const [draft, setDraftState] = useState<string | null>(null);
  // 草稿同时放在 ref 里：失焦与「完成」可能先后触发，闭包里的 state 还没更新。
  const draftRef = useRef<string | null>(null);
  const setDraft = (next: string | null) => {
    draftRef.current = next;
    setDraftState(next);
  };
  const commit = () => {
    const text = draftRef.current;
    setDraft(null);
    if (text === null) return;
    const parsed = parseDecimal(text);
    if (Number.isNaN(parsed) || Math.abs(parsed - value) < 1e-9) return;
    onCommit(parsed);
  };
  return (
    <TextInput
      mode="outlined"
      dense
      label={label}
      value={draft ?? format(value)}
      onChangeText={setDraft}
      onBlur={commit}
      onSubmitEditing={commit}
      keyboardType="decimal-pad"
      returnKeyType="done"
      disabled={disabled}
      accessibilityLabel={accessibilityLabel}
      right={right ? <TextInput.Affix text={right} /> : undefined}
      style={styles.input}
      outlineStyle={styles.outline}
    />
  );
}

/** 「1 2 3」小圆序号。 */
export function IndexBadge({ n, active }: { n: number; active?: boolean }) {
  return (
    <View style={[styles.badge, active ? styles.badgeActive : null]}>
      <Text style={[styles.badgeText, active ? styles.badgeTextActive : null]}>{n}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  message: { flex: 1, gap: HB_SPACING.md, padding: HB_SPACING.lg, justifyContent: "center", backgroundColor: HB_COLORS.background },
  input: { backgroundColor: HB_COLORS.white, fontVariant: ["tabular-nums"] },
  outline: { borderRadius: HB_RADIUS.control },
  badge: { width: 22, height: 22, borderRadius: 11, backgroundColor: HB_COLORS.surfaceMuted, alignItems: "center", justifyContent: "center" },
  badgeActive: { backgroundColor: HB_COLORS.brand },
  badgeText: { fontSize: 12, color: HB_COLORS.textSecondary, fontWeight: "600" },
  badgeTextActive: { color: HB_COLORS.white },
});
