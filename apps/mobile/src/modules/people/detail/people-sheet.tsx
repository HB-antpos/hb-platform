import type { ReactNode } from "react";
import { useEffect, useState } from "react";
import { Keyboard, Platform, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { Button, Dialog, IconButton, Modal, Portal, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

/**
 * 人员详情用的底部弹层与确认框。
 *
 * 为什么不用 BusinessSheet：BusinessSheet 是原生 Modal，会压住 Paper Portal（Snackbar / Dialog 看不见），
 * 且 iOS 上「关闭一个原生 Modal 后立刻打开另一个」会被忽略（更多操作 → 个人码 / 停用确认正是这种连续切换）。
 * 这里和 PosPermissionSheet 一样走 Paper Portal + Modal，所有弹层都在同一个 Portal 体系内，切换不会被吞。
 * 调用方用条件渲染控制显示：关闭即卸载，保证每次打开都是干净状态（输入框、错误提示都不会残留）。
 */

function useIosKeyboardHeight() {
  const [height, setHeight] = useState(0);
  useEffect(() => {
    // Android 由 adjustResize 处理；iOS 的 Paper Modal 不会自动避让键盘，需要手动抬高底部弹层。
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

export interface PeopleSheetProps {
  title: string;
  subtitle?: string;
  closeLabel: string;
  onDismiss: () => void;
  children: ReactNode;
  /** 吸底操作区（取消 / 确认按钮），不随内容滚动。 */
  footer?: ReactNode;
  /** 为 false 时不能点遮罩 / 返回键 / ✕ 关闭（提交中）。 */
  dismissable?: boolean;
  testID?: string;
}

export function PeopleSheet({
  title,
  subtitle,
  closeLabel,
  onDismiss,
  children,
  footer,
  dismissable = true,
  testID,
}: PeopleSheetProps) {
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
        testID={testID}
      >
        <View style={styles.handle} />
        <View style={styles.header}>
          <View style={styles.heading}>
            <Text accessibilityRole="header" style={styles.title} numberOfLines={2}>{title}</Text>
            {subtitle ? <Text style={styles.subtitle} numberOfLines={1}>{subtitle}</Text> : null}
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

/**
 * 弹层底部操作区：默认一行等分（取消在左，主操作在右）；大字号 / 窄屏时改成纵向，避免按钮文字被截断。
 * 用法：<SheetActions stacked={stack}><Button style={SHEET_BUTTON_STYLE}>…</Button></SheetActions>
 */
export function SheetActions({ stacked = false, children }: { stacked?: boolean; children: ReactNode }) {
  return <View style={[styles.actions, stacked && styles.actionsStacked]}>{children}</View>;
}

/** 底部操作区里每个按钮的统一样式：横排时等分剩余宽度，纵排时撑满。 */
export const SHEET_BUTTON_STYLE = { borderRadius: HB_RADIUS.control, flexGrow: 1, minWidth: 96 } as const;

export interface ConfirmDialogProps {
  title: string;
  message: string;
  cancelLabel: string;
  confirmLabel: string;
  /** 危险操作（停用）：确认按钮红色实心。 */
  danger?: boolean;
  pending?: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  testID?: string;
}

/**
 * 统一的居中确认框（替换原生 Alert）：说清后果与可恢复性；取消在左，危险按钮红色实心在右，减少误触。
 * 同样条件渲染，关闭即卸载。
 */
export function ConfirmDialog({
  title,
  message,
  cancelLabel,
  confirmLabel,
  danger = false,
  pending = false,
  onCancel,
  onConfirm,
  testID,
}: ConfirmDialogProps) {
  return (
    <Portal>
      <Dialog visible dismissable={!pending} onDismiss={onCancel} style={styles.dialog} testID={testID}>
        <Dialog.Title style={styles.dialogTitle}>{title}</Dialog.Title>
        <Dialog.Content>
          <Text style={styles.dialogMessage}>{message}</Text>
        </Dialog.Content>
        <Dialog.Actions style={styles.dialogActions}>
          <Button mode="outlined" onPress={onCancel} disabled={pending} style={styles.dialogButton}>
            {cancelLabel}
          </Button>
          <Button
            mode="contained"
            buttonColor={danger ? HB_COLORS.danger : HB_COLORS.action}
            textColor={HB_COLORS.white}
            onPress={onConfirm}
            loading={pending}
            disabled={pending}
            style={styles.dialogButton}
          >
            {confirmLabel}
          </Button>
        </Dialog.Actions>
      </Dialog>
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
  header: { alignItems: "center", flexDirection: "row", gap: HB_SPACING.xs, minHeight: 60, paddingLeft: HB_SPACING.md, paddingRight: HB_SPACING.xxs },
  heading: { flex: 1, gap: 2, minWidth: 0 },
  title: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "700", lineHeight: 26 },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 13, lineHeight: 18 },
  close: { margin: 0 },
  scroll: { flexGrow: 0, flexShrink: 1 },
  content: { gap: HB_SPACING.sm, paddingBottom: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  footer: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
  },
  actions: { alignItems: "stretch", flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  actionsStacked: { flexDirection: "column", flexWrap: "nowrap" },
  dialog: { alignSelf: "center", backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.sheet, maxWidth: 420, width: "90%" },
  dialogTitle: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "800" },
  dialogMessage: { color: HB_COLORS.textSecondary, fontSize: 14, lineHeight: 21 },
  dialogActions: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md },
  dialogButton: { borderRadius: HB_RADIUS.control, minWidth: 84 },
});
