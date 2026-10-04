import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import {
  Keyboard,
  Platform,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
} from "react-native";
import { IconButton, Modal, Portal, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/** POS 授权页的局部配色：基于 HB_COLORS 的浅色底，用于提示条、标记与改动点。 */
export const POS_PERMISSION_COLORS = {
  brandSoft: "#EAF2FF",
  successSoft: "#ECFDF3",
  dangerSoft: "#FEF3F2",
  dangerBorder: "#FDA29B",
  warningSoft: "#FFFAEB",
  warningBorder: "#FEDF89",
  changeDot: "#F79009",
  infoSoft: "#EFF8FF",
  infoBorder: "#B2DDFF",
  disabledText: "#98A2B3",
} as const;

function useIosKeyboardHeight() {
  const [height, setHeight] = useState(0);

  useEffect(() => {
    // Android 由 adjustResize 处理；iOS 的 Paper Modal 不会自动避让键盘，需要手动抬高底部弹层。
    if (Platform.OS !== "ios") return undefined;
    const show = Keyboard.addListener("keyboardWillShow", (event) =>
      setHeight(event.endCoordinates.height)
    );
    const hide = Keyboard.addListener("keyboardWillHide", () => setHeight(0));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);

  return height;
}

interface PosPermissionSheetProps {
  title: string;
  subtitle?: string;
  closeLabel: string;
  onDismiss: () => void;
  children: ReactNode;
  footer?: ReactNode;
  /** 列表上方的固定区（如搜索框），不随内容滚动。 */
  header?: ReactNode;
  dismissable?: boolean;
}

/**
 * 底部弹层：用 Paper Portal + Modal 实现，而不是原生 Modal。
 * 原生 Modal 会压住 Paper Portal（Snackbar、Menu、Dialog 不可见），这里保持在同一 Portal 体系内。
 * 调用方用条件渲染控制显示，关闭即卸载，保证每次打开都是干净状态。
 */
export function PosPermissionSheet({
  title,
  subtitle,
  closeLabel,
  onDismiss,
  children,
  footer,
  header,
  dismissable = true,
}: PosPermissionSheetProps) {
  const insets = useSafeAreaInsets();
  const { height: windowHeight } = useWindowDimensions();
  const keyboardHeight = useIosKeyboardHeight();
  const close = () => {
    if (dismissable) onDismiss();
  };
  const maxHeight = Math.max(
    320,
    windowHeight - insets.top - HB_SPACING.lg - keyboardHeight
  );

  return (
    <Portal>
      <Modal
        visible
        dismissable={dismissable}
        onDismiss={close}
        style={[styles.wrapper, { marginBottom: keyboardHeight }]}
        contentContainerStyle={[
          styles.sheet,
          {
            maxHeight,
            paddingBottom: keyboardHeight > 0 ? HB_SPACING.sm : Math.max(insets.bottom, HB_SPACING.sm),
          },
        ]}
      >
        <View style={styles.handle} />
        <View style={styles.header}>
          <View style={styles.heading}>
            <Text accessibilityRole="header" style={styles.title} numberOfLines={2}>
              {title}
            </Text>
            {subtitle ? (
              <Text style={styles.subtitle} numberOfLines={1}>
                {subtitle}
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
        <ScrollView
          style={styles.scroll}
          contentContainerStyle={styles.content}
          keyboardShouldPersistTaps="handled"
          keyboardDismissMode="on-drag"
        >
          {children}
        </ScrollView>
        {footer ? <View style={styles.footer}>{footer}</View> : null}
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  wrapper: { justifyContent: "flex-end", marginTop: 0 },
  sheet: {
    width: "100%",
    maxWidth: 680,
    alignSelf: "center",
    backgroundColor: HB_COLORS.white,
    borderTopLeftRadius: HB_RADIUS.sheet,
    borderTopRightRadius: HB_RADIUS.sheet,
    overflow: "hidden",
    justifyContent: "flex-start",
  },
  handle: {
    width: 40,
    height: 4,
    marginTop: 8,
    borderRadius: 2,
    backgroundColor: HB_COLORS.outline,
    alignSelf: "center",
  },
  header: {
    minHeight: 60,
    paddingLeft: HB_SPACING.md,
    paddingRight: HB_SPACING.xxs,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  heading: { flex: 1, minWidth: 0, gap: 2 },
  title: { fontSize: 18, lineHeight: 26, fontWeight: "700", color: HB_COLORS.textPrimary },
  subtitle: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  close: { margin: 0 },
  fixedHeader: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.xs },
  scroll: { flexGrow: 0, flexShrink: 1 },
  content: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.sm, gap: HB_SPACING.sm },
  footer: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
});
