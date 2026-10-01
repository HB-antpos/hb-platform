import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, HelperText, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { formatMonthDate, MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  buildArrivalDatePatch,
  containerStatusLabel,
  toDateOnly,
  type ArrivalDateDraft,
} from "./container-list-logic";
import type { ContainerMain, UpdateContainerRequest } from "./types";

export type ArrivalDateField = keyof ArrivalDateDraft;

const FIELD_LABELS: Record<ArrivalDateField, string> = {
  estimated: "预计到库",
  actual: "实际到库",
};

function addDays(date: string, days: number) {
  const [year, month, day] = date.split("-").map(Number);
  return formatMonthDate(new Date(year, month - 1, day + days));
}

interface ContainerArrivalDateSheetProps {
  container: ContainerMain | null;
  initialField: ArrivalDateField;
  saving: boolean;
  errorMessage: string;
  onDismiss: () => void;
  onSave: (patch: UpdateContainerRequest) => void;
}

/**
 * 在列表上直接修改预计/实际到库日期；两个日期共用一个日历，用顶部分段切换，一次保存。
 * 放在 BusinessSheet（原生 Modal）里，Paper Portal 类弹窗会被压住，所以日历内联、错误提示也留在弹层内。
 */
export function ContainerArrivalDateSheet({
  container,
  initialField,
  saving,
  errorMessage,
  onDismiss,
  onSave,
}: ContainerArrivalDateSheetProps) {
  const original = useMemo<ArrivalDateDraft>(
    () => ({
      estimated: toDateOnly(container?.预计到岸日期),
      actual: toDateOnly(container?.实际到货日期),
    }),
    [container],
  );
  const [draft, setDraft] = useState<ArrivalDateDraft>(original);
  const [field, setField] = useState<ArrivalDateField>(initialField);
  const today = useMemo(() => formatMonthDate(new Date()), []);

  // 每次打开新的货柜都从服务端当前值重新开始编辑。
  useEffect(() => {
    setDraft(original);
    setField(initialField);
  }, [original, initialField]);

  const patch = buildArrivalDatePatch(original, draft);
  const current = draft[field];
  const setCurrent = (value: string) => setDraft((prev) => ({ ...prev, [field]: value }));

  // 实际到库不能登记为未来日期；预计到库可以任意调整。
  const maxDate = field === "actual" ? today : undefined;
  const quickOptions = field === "actual"
    ? [
        { label: "今天", value: today },
        { label: "昨天", value: addDays(today, -1) },
      ]
    : [
        { label: "今天", value: today },
        { label: "明天", value: addDays(today, 1) },
        { label: "+7 天", value: addDays(current || today, 7) },
      ];

  const containerName = container?.货柜编号 || "未命名货柜";

  return (
    <BusinessSheet
      visible={Boolean(container)}
      title="修改到库日期"
      subtitle={`${containerName} · ${containerStatusLabel(container?.状态)}`}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={(
        <>
          {errorMessage ? <HelperText type="error" visible>{errorMessage}</HelperText> : null}
          <View style={styles.footerRow}>
            <Button mode="outlined" style={styles.footerButton} onPress={onDismiss} disabled={saving}>
              取消
            </Button>
            <Button
              mode="contained"
              style={[styles.footerButton, styles.footerPrimary]}
              loading={saving}
              disabled={saving || !patch}
              onPress={() => patch && onSave(patch)}
            >
              保存
            </Button>
          </View>
        </>
      )}
    >
      <View style={styles.segment} accessibilityRole="tablist">
        {(Object.keys(FIELD_LABELS) as ArrivalDateField[]).map((key) => {
          const selected = key === field;
          const value = draft[key];
          const changed = value !== original[key];
          return (
            <Pressable
              key={key}
              accessibilityRole="tab"
              accessibilityState={{ selected }}
              onPress={() => setField(key)}
              style={[styles.segmentItem, selected ? styles.segmentItemSelected : null]}
            >
              <Text style={[styles.segmentLabel, selected ? styles.segmentLabelSelected : null]}>
                {FIELD_LABELS[key]}
              </Text>
              <Text style={[styles.segmentValue, changed ? styles.segmentValueChanged : null]}>
                {value || (key === "actual" ? "未登记" : "未设置")}
                {changed ? " · 已修改" : ""}
              </Text>
            </Pressable>
          );
        })}
      </View>

      <MonthDatePicker value={current} allowEmpty maxDate={maxDate} onChange={setCurrent} />

      <View style={styles.quickRow}>
        {quickOptions.map((option) => (
          <Button
            key={option.label}
            compact
            mode={current === option.value ? "contained-tonal" : "outlined"}
            onPress={() => setCurrent(option.value)}
          >
            {option.label}
          </Button>
        ))}
        {current ? (
          <Button compact icon="calendar-remove-outline" textColor={HB_COLORS.danger} onPress={() => setCurrent("")}>
            清空
          </Button>
        ) : null}
      </View>
      {field === "actual" ? (
        <Text style={styles.hint}>登记实际到库不会自动修改货柜状态。</Text>
      ) : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  segment: {
    flexDirection: "row",
    gap: 4,
    padding: 4,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  segmentItem: {
    flex: 1,
    alignItems: "center",
    paddingVertical: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: "transparent",
  },
  segmentItemSelected: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.brand,
  },
  segmentLabel: {
    fontSize: 14,
    color: HB_COLORS.textSecondary,
  },
  segmentLabelSelected: {
    color: HB_COLORS.action,
    fontWeight: "600",
  },
  segmentValue: {
    marginTop: 2,
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  segmentValueChanged: {
    color: HB_COLORS.action,
  },
  quickRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  hint: {
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  footerRow: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
  },
  footerButton: {
    flex: 1,
  },
  footerPrimary: {
    flex: 2,
  },
});
