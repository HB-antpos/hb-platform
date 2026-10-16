import type { ReactNode } from "react";
import { KeyboardAvoidingView, Platform, StyleSheet, View } from "react-native";
import { IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { ADMIN_MONO_FONT } from "./admin-visuals";

/**
 * 角色 / 权限详情页共用的页面外壳（设计稿 A6）：
 * 顶栏 = 返回 + 标题 + 一行副标题（成员数 / 权限码），中间是内容区，底部是吸底操作栏。
 * 旧的 AdminScreen 只有标题没有副标题、底部是普通按钮区，这里单独做一个，不改旧组件。
 */
export function AdminDetailShell({
  title,
  subtitle,
  subtitleMono = false,
  onBack,
  action,
  children,
  footer,
}: {
  title: string;
  subtitle?: string;
  subtitleMono?: boolean;
  onBack: () => void;
  /** 顶栏右侧（刷新核对等） */
  action?: ReactNode;
  children: ReactNode;
  /** 吸底操作栏（StickyActionBar），整页共用一个 */
  footer?: ReactNode;
}) {
  const { language } = useAppTranslation();
  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right", "bottom"]}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <View style={styles.header}>
          <IconButton
            icon="chevron-left"
            accessibilityLabel={language.startsWith("zh") ? "返回" : "Back"}
            onPress={onBack}
          />
          <View style={styles.heading}>
            <Text accessibilityRole="header" style={styles.title} numberOfLines={1}>{title}</Text>
            {subtitle ? (
              <Text style={[styles.subtitle, subtitleMono && styles.mono]} numberOfLines={1}>{subtitle}</Text>
            ) : null}
          </View>
          {action ? <View style={styles.action}>{action}</View> : null}
        </View>
        <View style={styles.body}>{children}</View>
        {footer}
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.white, flex: 1 },
  flex: { flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomColor: HB_COLORS.outlineMuted,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    minHeight: 60,
    paddingRight: 8,
  },
  heading: { flex: 1, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 20, fontWeight: "700", lineHeight: 26 },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 17 },
  mono: { fontFamily: ADMIN_MONO_FONT },
  action: { alignItems: "flex-end", justifyContent: "center", minWidth: 44 },
  body: { backgroundColor: HB_COLORS.background, flex: 1 },
});
