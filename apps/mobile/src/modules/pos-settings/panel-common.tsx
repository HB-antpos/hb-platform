// 各面板与详情页共用的加载 / 失败 / 空态与权限读取。
import { StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canManagePosSettings, canViewPosSettings } from "./access";

/** 当前账号的收银端设置权限：能看 / 能改（只有 View 时所有控件只读、不显示保存按钮）。 */
export function usePosSettingsAccess() {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  return {
    canView: canViewPosSettings(isAuthenticated, hasPermission, isReview),
    canManage: canManagePosSettings(isAuthenticated, hasPermission, isReview),
  };
}

export function LoadingState() {
  const { t } = useAppTranslation("posSettings");
  return (
    <View style={styles.center}>
      <ActivityIndicator />
      <Text style={styles.muted}>{t("states.loading")}</Text>
    </View>
  );
}

export function MessageState({ text, actionLabel, onAction }: { text: string; actionLabel?: string; onAction?: () => void }) {
  return (
    <View style={styles.center}>
      <Text style={styles.message}>{text}</Text>
      {actionLabel && onAction ? <Button mode="outlined" onPress={onAction}>{actionLabel}</Button> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  muted: { color: HB_COLORS.textSecondary },
  message: { color: HB_COLORS.textPrimary, textAlign: "center" },
});
