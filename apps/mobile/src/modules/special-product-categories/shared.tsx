import { useCallback } from "react";
import { Image, StyleSheet, View } from "react-native";
import { Button, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAuthStore } from "@/store/auth-store";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PERMISSIONS } from "@/shared/utils/access";
import { productThumbnailUri } from "@/shared/utils/product-image";
import { errorCodeKey, isConflictError } from "./logic";

export const SPECIAL_CATEGORIES_PATHS = {
  list: "/(shell)/special-product-categories",
  category: "/(shell)/special-product-categories/category",
  sync: "/(shell)/special-product-categories/sync",
} as const;

/** 没有颜色的分类用中性灰点。 */
export const NO_COLOR_DOT = HB_COLORS.outline;

/**
 * 入口守卫：审核模式 / 未登录 / 没有「管理特殊商品」权限直接给出说明，不发请求。
 * 后端写接口按 SpecialProducts.Manage、读接口按 View 授权；本页是维护页，要求 Manage（Admin 由 hasPermission 放行）。
 */
export function useSpecialCategoriesGuard() {
  const { t } = useAppTranslation("specialProductCategories");
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const access = useAuthStore((state) => state.access);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const canManage = access.hasPermission(PERMISSIONS.PosTerminal.SpecialProductsManage);
  let blocked: string | null = null;
  if (review || sessionKind === "iosReview") blocked = t("messages.reviewUnavailable");
  else if (!isAuthenticated || !canManage) blocked = t("messages.notAllowed");
  return { blocked };
}

export function SpecialCategoriesMessage({ message, onBack }: { message: string; onBack: () => void }) {
  const { t } = useAppTranslation("specialProductCategories");
  return (
    <SafeAreaView style={styles.message}>
      <Text accessibilityLiveRegion="polite">{message}</Text>
      <Button onPress={onBack}>{t("actions.back")}</Button>
    </SafeAreaView>
  );
}

/**
 * 接口错误转文案：先按后端稳定错误码映射（同名 / 达到 50 个上限 / 排序冲突 / 分店越权 …），
 * 再处理 403 / 404 / 其它 409；后端业务校验失败的原因是中文，中文界面原样显示，英文界面退回 fallbackKey 对应的通用文案。
 */
export function useApiErrorText() {
  const { t, language } = useAppTranslation("specialProductCategories");
  return useCallback(
    (cause: unknown, fallbackKey: string) => {
      const codeKey = errorCodeKey(cause);
      if (codeKey) return t(`errors.${codeKey}`);
      const status = (cause as { response?: { status?: number } })?.response?.status;
      if (status === 403) return t("errors.forbidden");
      if (status === 404) return t("errors.notFound");
      if (isConflictError(cause)) return t("errors.conflict");
      return resolveLocalizedErrorMessage(cause, { t, language, fallbackKey, allowRawMessageInChinese: true });
    },
    [language, t],
  );
}

/** 顶部标题栏：返回 + 居中标题 + 右侧可选操作（自己渲染，因此路由隐藏原生 header）。 */
export function ScreenHeader({
  title,
  onBack,
  backIcon = "chevron-left",
  right,
}: {
  title: string;
  onBack: () => void;
  backIcon?: string;
  right?: React.ReactNode;
}) {
  const { t } = useAppTranslation("specialProductCategories");
  return (
    <View style={styles.header}>
      <IconButton icon={backIcon} accessibilityLabel={t("actions.back")} onPress={onBack} />
      <Text variant="titleMedium" style={styles.headerTitle} numberOfLines={1} accessibilityRole="header">
        {title}
      </Text>
      <View style={styles.headerRight}>{right}</View>
    </View>
  );
}

export function ColorDot({ color, size = 12 }: { color: string | null; size?: number }) {
  return <View style={{ width: size, height: size, borderRadius: size / 2, backgroundColor: color ?? NO_COLOR_DOT }} />;
}

/** 商品缩略图：有图片显示图片，没有时用首字母占位（与设计图一致）。 */
export function ProductThumb({ uri, name, size = 44 }: { uri: string | null; name: string; size?: number }) {
  const source = productThumbnailUri(uri, size * 3);
  return source ? (
    <Image source={{ uri: source }} style={[styles.thumb, { width: size, height: size }]} accessibilityIgnoresInvertColors />
  ) : (
    <View style={[styles.thumb, styles.thumbPlaceholder, { width: size, height: size }]}>
      <Text style={styles.thumbLetter}>{(name.trim().charAt(0) || "?").toUpperCase()}</Text>
    </View>
  );
}

export const SC_COLORS = {
  blueBg: "#EFF8FF",
  blueBorder: "#B2DDFF",
  warnBg: "#FFFAEB",
  warnBorder: "#FEDF89",
  dangerBg: "#FEF3F2",
  dangerBorder: "#FDA29B",
  successBg: "#ECFDF3",
  selectedBg: "#EAF2FF",
  uncategorized: "#B54708",
} as const;

const styles = StyleSheet.create({
  message: { flex: 1, gap: HB_SPACING.md, padding: HB_SPACING.lg, justifyContent: "center", backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingHorizontal: HB_SPACING.xs, minHeight: 56, backgroundColor: HB_COLORS.background },
  headerTitle: { flex: 1, textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  headerRight: { minWidth: 48, alignItems: "flex-end", justifyContent: "center", paddingRight: HB_SPACING.xs },
  thumb: { borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted },
  thumbPlaceholder: { alignItems: "center", justifyContent: "center" },
  thumbLetter: { color: HB_COLORS.textSecondary, fontWeight: "700", fontSize: 16 },
});
