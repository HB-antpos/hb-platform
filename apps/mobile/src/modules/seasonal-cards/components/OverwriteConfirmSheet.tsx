import { ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { Button, Modal, Portal, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { SEASONAL_CARD_COLORS } from "./palette";

export interface OverwriteConfirmRow {
  key: string;
  label: string;
  previous: string;
  current: string;
  diff: string;
  changed: boolean;
}

/**
 * 覆盖确认：上次 / 本次 / 变化对比表 + 合计。
 * 用 Paper Portal + Modal 做成底部弹层，只能在页面层渲染——放进 BusinessSheet（原生 Modal）会被盖住。
 */
export function OverwriteConfirmSheet({
  visible,
  busy,
  title,
  comboLine,
  lastLine,
  headers,
  rows,
  total,
  note,
  cancelLabel,
  confirmLabel,
  onCancel,
  onConfirm,
}: {
  visible: boolean;
  busy: boolean;
  title: string;
  comboLine: string;
  lastLine: string;
  headers: { priceType: string; previous: string; current: string; diff: string };
  rows: OverwriteConfirmRow[];
  total: { label: string; previous: string; current: string; diff: string };
  note: string;
  cancelLabel: string;
  confirmLabel: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const insets = useSafeAreaInsets();
  const { height } = useWindowDimensions();

  return (
    <Portal>
      <Modal
        visible={visible}
        dismissable={!busy}
        onDismiss={onCancel}
        style={styles.overlay}
        contentContainerStyle={[
          styles.sheet,
          { paddingBottom: Math.max(insets.bottom, HB_SPACING.md) + HB_SPACING.xs },
        ]}
      >
        <View style={styles.handle} />
        <ScrollView style={{ maxHeight: height * 0.7 }} bounces={false}>
          <View style={styles.body}>
            <View style={styles.heading}>
              <Text accessibilityRole="header" style={styles.title}>
                {title}
              </Text>
              <Text style={styles.meta}>{comboLine}</Text>
              {lastLine ? <Text style={styles.meta}>{lastLine}</Text> : null}
            </View>

            <View style={styles.table}>
              <View style={[styles.tableRow, styles.tableHeader]}>
                <Text style={[styles.cellLabel, styles.headerText]}>{headers.priceType}</Text>
                <Text style={[styles.cellNumber, styles.headerText]}>{headers.previous}</Text>
                <Text style={[styles.cellNumber, styles.headerText]}>{headers.current}</Text>
                <Text style={[styles.cellNumber, styles.cellLast, styles.headerText]}>
                  {headers.diff}
                </Text>
              </View>
              {rows.map((row) => (
                <View key={row.key} style={[styles.tableRow, styles.tableBodyRow]}>
                  <Text style={[styles.cellLabel, row.changed ? null : styles.unchangedText]}>
                    {row.label}
                  </Text>
                  <Text style={[styles.cellNumber, row.changed ? null : styles.unchangedText]}>
                    {row.previous}
                  </Text>
                  <Text
                    style={[
                      styles.cellNumber,
                      styles.currentText,
                      row.changed ? null : styles.unchangedText,
                    ]}
                  >
                    {row.current}
                  </Text>
                  <Text
                    style={[
                      styles.cellNumber,
                      styles.cellLast,
                      row.changed ? styles.diffChanged : styles.unchangedText,
                    ]}
                  >
                    {row.diff}
                  </Text>
                </View>
              ))}
              <View style={[styles.tableRow, styles.tableFooter]}>
                <Text style={[styles.cellLabel, styles.totalText]}>{total.label}</Text>
                <Text style={[styles.cellNumber, styles.totalText]}>{total.previous}</Text>
                <Text style={[styles.cellNumber, styles.totalText]}>{total.current}</Text>
                <Text style={[styles.cellNumber, styles.cellLast, styles.totalText]}>
                  {total.diff}
                </Text>
              </View>
            </View>

            <Text style={styles.note}>{note}</Text>
          </View>
        </ScrollView>

        <View style={styles.actions}>
          <Button
            mode="outlined"
            disabled={busy}
            onPress={onCancel}
            style={styles.action}
            contentStyle={styles.actionContent}
            labelStyle={styles.actionLabel}
          >
            {cancelLabel}
          </Button>
          <Button
            mode="contained"
            loading={busy}
            disabled={busy}
            onPress={onConfirm}
            style={styles.action}
            contentStyle={styles.actionContent}
            labelStyle={styles.actionLabel}
          >
            {confirmLabel}
          </Button>
        </View>
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  overlay: { justifyContent: "flex-end" },
  sheet: {
    backgroundColor: HB_COLORS.white,
    borderTopLeftRadius: HB_RADIUS.sheet,
    borderTopRightRadius: HB_RADIUS.sheet,
    paddingHorizontal: 20,
    paddingTop: HB_SPACING.xs,
    gap: HB_SPACING.md,
  },
  handle: {
    alignSelf: "center",
    width: 36,
    height: 4,
    borderRadius: 2,
    backgroundColor: HB_COLORS.outline,
  },
  body: { gap: HB_SPACING.md },
  heading: { gap: 6 },
  title: { fontSize: 18, lineHeight: 26, fontWeight: "600", color: HB_COLORS.textPrimary },
  meta: { fontSize: 13, lineHeight: 20, color: HB_COLORS.textSecondary },
  table: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
    overflow: "hidden",
  },
  tableRow: { flexDirection: "row", alignItems: "center" },
  tableHeader: { backgroundColor: SEASONAL_CARD_COLORS.stepperBackground },
  tableBodyRow: { borderTopWidth: 1, borderTopColor: HB_COLORS.surfaceMuted },
  tableFooter: {
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
    backgroundColor: SEASONAL_CARD_COLORS.stepperBackground,
  },
  cellLabel: {
    flex: 1.3,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 10,
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textPrimary,
  },
  cellNumber: {
    flex: 1,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 10,
    textAlign: "right",
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  cellLast: { paddingRight: HB_SPACING.sm },
  headerText: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary, paddingVertical: 8 },
  currentText: { fontWeight: "600" },
  unchangedText: { color: SEASONAL_CARD_COLORS.disabledText, fontWeight: "400" },
  diffChanged: { color: SEASONAL_CARD_COLORS.selectedText, fontWeight: "600" },
  totalText: { fontWeight: "600" },
  note: { fontSize: 12, lineHeight: 19, color: SEASONAL_CARD_COLORS.mutedText },
  actions: { flexDirection: "row", gap: HB_SPACING.sm },
  action: { flex: 1, borderRadius: 10 },
  actionContent: { minHeight: 48 },
  actionLabel: { fontSize: 16, lineHeight: 22, marginHorizontal: 8 },
});
