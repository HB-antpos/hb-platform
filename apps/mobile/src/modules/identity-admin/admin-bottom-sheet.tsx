import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import { Keyboard, Platform, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { IconButton, Modal, Portal, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { ADMIN_MONO_FONT } from "./admin-visuals";

/**
 * iOS 的 Paper Modal 不会自动避让键盘，需要手动抬高底部弹层；
 * Android 由 adjustResize 处理（与 POS 授权页的底部弹层一致），这里不重复处理。
 */
function useIosKeyboardHeight() {
  const [height, setHeight] = useState(0);
  useEffect(() => {
    if (Platform.OS !== "ios") return undefined;
    const show = Keyboard.addListener("keyboardWillShow", (event) => setHeight(event.endCoordinates.height));
    const hide = Keyboard.addListener("keyboardWillHide", () => setHeight(0));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);
  return height;
}

export interface AdminBottomSheetProps {
  title: string;
  subtitle?: string;
  /** 跟在副标题后面、需要等宽显示的编码（如权限码）：「营业额报表 · Reports.Revenue.View」 */
  subtitleCode?: string;
  closeLabel: string;
  onDismiss: () => void;
  /** 保存中不允许点遮罩 / 返回键 / 关闭按钮把弹层关掉 */
  dismissable?: boolean;
  /** 标题下方的固定区（搜索框、已选计数），不随列表滚动 */
  header?: ReactNode;
  /** 弹层内的就地提示（保存失败等）。原生 Snackbar 在弹层打开时会被盖住，所以失败原因必须放在这里。 */
  notice?: ReactNode;
  footer?: ReactNode;
  /** 默认用 ScrollView 包住内容；内容自带 FlatList 时传 false，避免虚拟列表嵌进 ScrollView */
  scroll?: boolean;
  children: ReactNode;
}

/**
 * 「人员管理」里统一的底部弹层（设计稿要求所有选项类弹层都是底部弹层）。
 *
 * 用 Paper Portal + Modal 实现而不是原生 Modal：
 * 1. 原生 Modal 会压住 Paper Portal（Dialog / Menu 不可见），且 iOS 上「关闭一个原生 Modal 后立刻开另一个」会被忽略；
 * 2. 沿用旧版 IdentitySelectionSheet 的层级，调用方在选中后立刻跳转页面 / 打开别的弹层都不会出问题。
 * 调用方用条件渲染控制显示，关闭即卸载，保证每次打开都是干净状态。
 */
export function AdminBottomSheet({
  title,
  subtitle,
  subtitleCode,
  closeLabel,
  onDismiss,
  dismissable = true,
  header,
  notice,
  footer,
  scroll = true,
  children,
}: AdminBottomSheetProps) {
  const insets = useSafeAreaInsets();
  const { height: windowHeight } = useWindowDimensions();
  const keyboardHeight = useIosKeyboardHeight();
  const close = () => {
    if (dismissable) onDismiss();
  };
  const maxHeight = Math.max(320, windowHeight - insets.top - HB_SPACING.lg - keyboardHeight);

  return (
    <Portal>
      <Modal
        visible
        dismissable={dismissable}
        onDismiss={close}
        style={[styles.wrapper, { marginBottom: keyboardHeight }]}
        contentContainerStyle={[
          styles.sheet,
          { maxHeight, paddingBottom: keyboardHeight > 0 ? HB_SPACING.sm : Math.max(insets.bottom, HB_SPACING.sm) },
        ]}
      >
        <View style={styles.handle} />
        <View style={styles.titleRow}>
          <View style={styles.heading}>
            <Text accessibilityRole="header" style={styles.title} numberOfLines={2}>{title}</Text>
            {subtitle || subtitleCode ? (
              <Text style={styles.subtitle} numberOfLines={1}>
                {subtitle}
                {subtitle && subtitleCode ? " · " : ""}
                {subtitleCode ? <Text style={styles.mono}>{subtitleCode}</Text> : null}
              </Text>
            ) : null}
          </View>
          <IconButton
            icon="close"
            accessibilityLabel={closeLabel}
            onPress={close}
            disabled={!dismissable}
            iconColor={HB_COLORS.textSecondary}
            style={styles.close}
          />
        </View>
        {header ? <View style={styles.fixedHeader}>{header}</View> : null}
        {notice ? <View style={styles.notice}>{notice}</View> : null}
        {scroll ? (
          <ScrollView
            style={styles.scroll}
            contentContainerStyle={styles.content}
            keyboardShouldPersistTaps="handled"
            keyboardDismissMode="on-drag"
          >
            {children}
          </ScrollView>
        ) : (
          <View style={styles.custom}>{children}</View>
        )}
        {footer ? <View style={styles.footer}>{footer}</View> : null}
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  wrapper: { justifyContent: "flex-end", marginTop: 0 },
  sheet: {
    alignSelf: "center",
    backgroundColor: HB_COLORS.white,
    borderTopLeftRadius: HB_RADIUS.sheet,
    borderTopRightRadius: HB_RADIUS.sheet,
    justifyContent: "flex-start",
    maxWidth: 680,
    overflow: "hidden",
    width: "100%",
  },
  handle: { alignSelf: "center", backgroundColor: HB_COLORS.outline, borderRadius: 2, height: 4, marginTop: 8, width: 40 },
  titleRow: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, minHeight: 60, paddingLeft: HB_SPACING.md, paddingRight: HB_SPACING.xxs },
  heading: { flex: 1, gap: 2, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "700", lineHeight: 26 },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 18 },
  mono: { fontFamily: ADMIN_MONO_FONT },
  close: { margin: 0 },
  fixedHeader: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  notice: { paddingBottom: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  scroll: { flexGrow: 0, flexShrink: 1 },
  content: { gap: HB_SPACING.sm, paddingBottom: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  custom: { flexShrink: 1, minHeight: 0 },
  footer: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
  },
});
