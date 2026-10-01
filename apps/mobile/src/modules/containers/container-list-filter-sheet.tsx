import { useEffect, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, HelperText, IconButton, SegmentedButtons, Text, TextInput } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import {
  CONTAINER_SORT_OPTIONS,
  DATE_RANGE_FIELDS,
  DEFAULT_CONTAINER_SORT,
  findInvalidDateRange,
  type ContainerListFilters,
} from "./container-list-logic";

type DateBound = (typeof DATE_RANGE_FIELDS)[number]["start"] | (typeof DATE_RANGE_FIELDS)[number]["end"];

interface ContainerListFilterSheetProps {
  visible: boolean;
  filters: ContainerListFilters;
  onDismiss: () => void;
  onApply: (filters: ContainerListFilters) => void;
}

/** 列表的更多筛选：货号、三组日期区间（均为整天闭区间，可只填一边）与排序字段。 */
export function ContainerListFilterSheet({ visible, filters, onDismiss, onApply }: ContainerListFilterSheetProps) {
  const [draft, setDraft] = useState<ContainerListFilters>(filters);
  // 同一时间只展开一个日历，避免弹层过长。
  const [activeBound, setActiveBound] = useState<DateBound | null>(null);

  useEffect(() => {
    if (visible) {
      setDraft(filters);
      setActiveBound(null);
    }
  }, [visible, filters]);

  const invalidRange = findInvalidDateRange(draft);
  const setBound = (bound: DateBound, value: string) => setDraft((prev) => ({ ...prev, [bound]: value || undefined }));

  const reset = () => {
    // 重置只清弹层里的条件；货柜编号搜索和状态标签在页面上单独管理，保持不变。
    setDraft((prev) => ({
      containerNumberFilter: prev.containerNumberFilter,
      statuses: prev.statuses,
      dateType: DEFAULT_CONTAINER_SORT,
    }));
    setActiveBound(null);
  };

  return (
    <BusinessSheet
      visible={visible}
      title="筛选货柜"
      onDismiss={onDismiss}
      footer={(
        <>
          {invalidRange ? <HelperText type="error" visible>{invalidRange}的开始日期不能晚于结束日期</HelperText> : null}
          <View style={styles.footerRow}>
            <Button mode="outlined" style={styles.footerButton} onPress={reset}>重置</Button>
            <Button
              mode="contained"
              style={[styles.footerButton, styles.footerPrimary]}
              disabled={Boolean(invalidRange)}
              onPress={() => onApply({ ...draft, itemNumberFilter: draft.itemNumberFilter?.trim() || undefined })}
            >
              查看结果
            </Button>
          </View>
        </>
      )}
    >
      <TextInput
        mode="outlined"
        label="货号"
        placeholder="包含该货号的货柜"
        value={draft.itemNumberFilter ?? ""}
        onChangeText={(value) => setDraft((prev) => ({ ...prev, itemNumberFilter: value }))}
        autoCapitalize="characters"
        autoCorrect={false}
        dense
      />

      {DATE_RANGE_FIELDS.map((range) => (
        <View key={range.key} style={styles.rangeBlock}>
          <Text style={styles.sectionLabel}>{range.label}</Text>
          <View style={styles.rangeRow}>
            {([range.start, range.end] as const).map((bound, index) => {
              const value = draft[bound] ?? "";
              const active = activeBound === bound;
              return (
                <Pressable
                  key={bound}
                  accessibilityRole="button"
                  accessibilityLabel={`${range.label}${index === 0 ? "开始" : "结束"}日期`}
                  accessibilityState={{ expanded: active }}
                  onPress={() => setActiveBound(active ? null : bound)}
                  style={[styles.boundBox, active ? styles.boundBoxActive : null]}
                >
                  <Text style={styles.boundCaption}>{index === 0 ? "开始" : "结束"}</Text>
                  <View style={styles.boundValueRow}>
                    <Text style={value ? styles.boundValue : styles.boundPlaceholder}>{value || "不限"}</Text>
                    {value ? (
                      <IconButton
                        icon="close-circle"
                        size={16}
                        style={styles.boundClear}
                        iconColor={HB_COLORS.textSecondary}
                        accessibilityLabel={`清除${range.label}${index === 0 ? "开始" : "结束"}日期`}
                        onPress={() => setBound(bound, "")}
                      />
                    ) : null}
                  </View>
                </Pressable>
              );
            })}
          </View>
          {activeBound === range.start || activeBound === range.end ? (
            <MonthDatePicker
              value={draft[activeBound] ?? ""}
              allowEmpty
              // 开始日期不能晚于已选结束日期，结束日期不能早于已选开始日期。
              minDate={activeBound === range.end ? draft[range.start] : undefined}
              maxDate={activeBound === range.start ? draft[range.end] : undefined}
              onChange={(value) => {
                setBound(activeBound, value);
                setActiveBound(null);
              }}
            />
          ) : null}
        </View>
      ))}

      <View style={styles.rangeBlock}>
        <Text style={styles.sectionLabel}>排序（从新到旧）</Text>
        <SegmentedButtons
          value={draft.dateType ?? DEFAULT_CONTAINER_SORT}
          onValueChange={(value) => setDraft((prev) => ({ ...prev, dateType: value }))}
          buttons={CONTAINER_SORT_OPTIONS.map((option) => ({ value: option.value, label: option.label }))}
        />
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  rangeBlock: {
    gap: HB_SPACING.xs,
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  rangeRow: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  boundBox: {
    flex: 1,
    minHeight: 52,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  boundBoxActive: {
    borderColor: HB_COLORS.brand,
    backgroundColor: "#EAF2FF",
  },
  boundCaption: {
    fontSize: 11,
    color: HB_COLORS.textSecondary,
  },
  boundValueRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    minHeight: 24,
  },
  boundValue: {
    fontSize: 14,
    color: HB_COLORS.textPrimary,
  },
  boundPlaceholder: {
    fontSize: 14,
    color: HB_COLORS.textSecondary,
  },
  boundClear: {
    margin: 0,
    width: 24,
    height: 24,
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
