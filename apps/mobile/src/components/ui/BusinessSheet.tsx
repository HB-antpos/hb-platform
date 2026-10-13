import type { ReactNode } from "react";
import { useRef } from "react";
import {
  AccessibilityInfo,
  findNodeHandle,
  KeyboardAvoidingView,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text as NativeText,
  useWindowDimensions,
  View,
} from "react-native";
import { IconButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

interface BusinessSheetProps {
  visible: boolean;
  title: string;
  subtitle?: string;
  onDismiss: () => void;
  children: ReactNode;
  footer?: ReactNode;
  dismissable?: boolean;
}

/** 仅由新业务界面显式使用；不改变已有公共弹窗和设置页的默认行为。 */
export function BusinessSheet({
  visible,
  title,
  subtitle,
  onDismiss,
  children,
  footer,
  dismissable = true,
}: BusinessSheetProps) {
  const { t } = useAppTranslation("common");
  const insets = useSafeAreaInsets();
  const { height } = useWindowDimensions();
  const titleRef = useRef<NativeText>(null);
  const close = () => { if (dismissable) onDismiss(); };

  // 关闭即卸载内容，避免隐藏的相机或输入控件仍接受事件。
  if (!visible) return null;

  return (
    <Modal
      transparent
      visible
      animationType="none"
      statusBarTranslucent
      presentationStyle="overFullScreen"
      onRequestClose={close}
      onShow={() => {
        const target = findNodeHandle(titleRef.current);
        if (target) AccessibilityInfo.setAccessibilityFocus(target);
      }}
    >
      {/*
        Android 也必须用 padding：Expo 54 默认开启边到边，Modal 的独立窗口不会随键盘 adjustResize，
        behavior 为空时键盘会直接盖住底部弹层里的输入框。KeyboardAvoidingView 按「键盘与自身的实际重叠量」
        补 padding，若某些机型窗口已被系统缩小则重叠为 0，不会重复避让。
      */}
      <KeyboardAvoidingView style={styles.overlay} behavior="padding">
        <Pressable
          style={styles.backdrop}
          onPress={close}
          accessible={false}
          importantForAccessibility="no"
        />
        <View
          style={[styles.sheet, { maxHeight: height - insets.top - HB_SPACING.md, paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}
          accessibilityViewIsModal
        >
          <View style={styles.handle} />
          <View style={styles.header}>
            <View style={styles.heading}>
              <NativeText ref={titleRef} accessibilityRole="header" style={styles.title}>{title}</NativeText>
              {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
            </View>
            <IconButton
              icon="close"
              accessibilityLabel={t("actions.close")}
              onPress={close}
              disabled={!dismissable}
              iconColor={HB_COLORS.action}
              style={styles.close}
            />
          </View>
          <ScrollView
            style={styles.scroll}
            contentContainerStyle={styles.content}
            keyboardShouldPersistTaps="handled"
            keyboardDismissMode="on-drag"
            nestedScrollEnabled
          >
            {children}
          </ScrollView>
          {footer ? <View style={styles.footer}>{footer}</View> : null}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  overlay: { flex: 1, justifyContent: "flex-end", alignItems: "center" },
  backdrop: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(16,24,40,0.44)" },
  // flexShrink：键盘顶起后可用高度变小，弹层要允许收缩（内部 ScrollView 随之滚动），否则顶部会被挤出屏幕。
  sheet: { width: "100%", maxWidth: 680, flexShrink: 1, backgroundColor: HB_COLORS.white, borderTopLeftRadius: HB_RADIUS.sheet, borderTopRightRadius: HB_RADIUS.sheet, overflow: "hidden" },
  handle: { width: 44, height: 4, marginTop: 10, marginBottom: 4, borderRadius: 2, backgroundColor: HB_COLORS.outline, alignSelf: "center" },
  header: { paddingLeft: HB_SPACING.md, paddingRight: HB_SPACING.xs, paddingVertical: HB_SPACING.xs, minHeight: 64, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  heading: { flex: 1, minWidth: 0 },
  title: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  subtitle: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary, marginTop: 4 },
  close: { margin: 0 },
  scroll: { flexGrow: 0, flexShrink: 1 },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  footer: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted, gap: HB_SPACING.xs },
});
