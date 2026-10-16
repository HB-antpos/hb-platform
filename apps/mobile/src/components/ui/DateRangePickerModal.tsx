import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, IconButton, Modal, Portal, Text } from "react-native-paper";
import {
  clearInvoiceDateRange,
  selectInvoiceDateRange,
  type InvoiceDateRangeValue,
} from "@/modules/local-supplier-invoices/date-range";

export type DateRangePickerValue = InvoiceDateRangeValue;

export interface DateRangePickerModalProps {
  visible: boolean;
  title: string;
  /** 当前已生效的区间；每次打开弹窗都以它为草稿起点。 */
  value: DateRangePickerValue;
  /** 点「应用」时回传草稿；取消 / 点遮罩不会改动外部状态。 */
  onApply: (next: DateRangePickerValue) => void;
  onCancel: () => void;
  cancelLabel: string;
  clearLabel: string;
  applyLabel: string;
  /** 周一到周日的列标题。 */
  weekdayLabels: string[];
  /** Intl 月份标题用的语言标签，如 zh-CN / en-AU。 */
  localeTag: string;
  /** 提供时显示「恢复默认」按钮，点击后把草稿设为该区间。 */
  presetRange?: { label: string; value: DateRangePickerValue };
}

interface CalendarCell {
  date: Date;
  dateString: string;
  isCurrentMonth: boolean;
}

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;
const GRID_DAYS = 42;

function pad2(value: number) {
  return value.toString().padStart(2, "0");
}

function formatDate(date: Date) {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

function parseDate(value?: string) {
  const match = value?.match(DATE_PATTERN);
  if (!match) {
    return undefined;
  }

  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const parsed = new Date(year, monthIndex, day);
  // 拒绝 2026-02-31 这类会被 Date 自动进位的非法日期。
  return parsed.getFullYear() === year && parsed.getMonth() === monthIndex && parsed.getDate() === day
    ? parsed
    : undefined;
}

function normalizeDate(value?: string) {
  const parsed = parseDate(value);
  return parsed ? formatDate(parsed) : undefined;
}

function monthStart(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function addMonths(date: Date, count: number) {
  return new Date(date.getFullYear(), date.getMonth() + count, 1);
}

function buildMonthGrid(displayMonth: Date): CalendarCell[] {
  const start = monthStart(displayMonth);
  const mondayOffset = (start.getDay() + 6) % 7;
  const gridStart = new Date(start.getFullYear(), start.getMonth(), 1 - mondayOffset);

  return Array.from({ length: GRID_DAYS }, (_, index) => {
    const date = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + index);
    return { date, dateString: formatDate(date), isCurrentMonth: date.getMonth() === start.getMonth() };
  });
}

function chunkWeeks<T>(items: T[]) {
  return Array.from({ length: Math.ceil(items.length / 7) }, (_, index) =>
    items.slice(index * 7, index * 7 + 7)
  );
}

function isInRange(date: string, from?: string, to?: string) {
  return Boolean(from && to && date >= from && date <= to);
}

/** 月历式日期区间选择：先点起点再点终点，自动排序；草稿在弹窗内，点「应用」才回传。 */
export function DateRangePickerModal({
  visible,
  title,
  value,
  onApply,
  onCancel,
  cancelLabel,
  clearLabel,
  applyLabel,
  weekdayLabels,
  localeTag,
  presetRange,
}: DateRangePickerModalProps) {
  const [draft, setDraft] = useState<DateRangePickerValue>({});
  const [displayMonth, setDisplayMonth] = useState(() => monthStart(new Date()));

  useEffect(() => {
    if (!visible) {
      return;
    }
    const from = normalizeDate(value.from);
    const to = normalizeDate(value.to);
    setDraft({ from, to });
    setDisplayMonth(monthStart(parseDate(from || to) ?? new Date()));
    // 只在打开瞬间取一次快照，弹窗打开期间外部 value 变化不应冲掉用户正在选的草稿。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  const weeks = useMemo(() => chunkWeeks(buildMonthGrid(displayMonth)), [displayMonth]);
  const today = formatDate(new Date());
  const monthTitle = useMemo(
    () => new Intl.DateTimeFormat(localeTag, { year: "numeric", month: "long" }).format(displayMonth),
    [displayMonth, localeTag]
  );
  const from = normalizeDate(draft.from);
  const to = normalizeDate(draft.to);

  return (
    <Portal>
      <Modal visible={visible} onDismiss={onCancel} contentContainerStyle={styles.modal}>
        <View style={styles.header}>
          <Text variant="titleMedium">{title}</Text>
          <Button onPress={onCancel}>{cancelLabel}</Button>
        </View>
        <View style={styles.calendarHeader}>
          <IconButton icon="chevron-left" size={22} onPress={() => setDisplayMonth((current) => addMonths(current, -1))} />
          <Text variant="titleMedium" style={styles.monthTitle}>{monthTitle}</Text>
          <IconButton icon="chevron-right" size={22} onPress={() => setDisplayMonth((current) => addMonths(current, 1))} />
        </View>
        <View style={styles.weekdayRow}>
          {weekdayLabels.map((label, index) => (
            <Text key={`${label}-${index}`} variant="labelSmall" style={styles.weekdayText}>{label}</Text>
          ))}
        </View>
        <View style={styles.grid}>
          {weeks.map((week, weekIndex) => (
            <View key={weekIndex} style={styles.weekRow}>
              {week.map((cell) => {
                const isStart = Boolean(from && cell.dateString === from);
                const isEnd = Boolean(to && cell.dateString === to);
                const inRange = isInRange(cell.dateString, from, to);

                return (
                  <Pressable
                    key={cell.dateString}
                    accessibilityRole="button"
                    accessibilityState={{ selected: isStart || isEnd || inRange }}
                    onPress={() => {
                      setDraft((current) => selectInvoiceDateRange(current, cell.dateString));
                      setDisplayMonth(monthStart(cell.date));
                    }}
                    style={({ pressed }) => [
                      styles.cell,
                      !cell.isCurrentMonth ? styles.cellOutside : null,
                      inRange ? styles.cellInRange : null,
                      cell.dateString === today ? styles.cellToday : null,
                      isStart || isEnd ? styles.cellSelected : null,
                      isStart && !to ? styles.cellSelected : null,
                      pressed ? styles.cellPressed : null,
                    ]}
                  >
                    <Text
                      variant="labelLarge"
                      style={[
                        styles.cellText,
                        !cell.isCurrentMonth ? styles.cellTextOutside : null,
                        isStart || isEnd ? styles.cellTextSelected : null,
                      ]}
                    >
                      {cell.date.getDate()}
                    </Text>
                  </Pressable>
                );
              })}
            </View>
          ))}
        </View>
        <View style={styles.actions}>
          <Button icon="close-circle-outline" mode="text" onPress={() => setDraft(clearInvoiceDateRange())}>
            {clearLabel}
          </Button>
          {presetRange ? (
            <Button
              icon="calendar-refresh-outline"
              mode="text"
              onPress={() => {
                setDraft({ ...presetRange.value });
                setDisplayMonth(monthStart(parseDate(presetRange.value.from) ?? new Date()));
              }}
            >
              {presetRange.label}
            </Button>
          ) : null}
          <Button icon="check" mode="contained" onPress={() => onApply({ from, to })}>
            {applyLabel}
          </Button>
        </View>
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  modal: {
    alignSelf: "center",
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    padding: 16,
    width: "92%",
  },
  header: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 8,
  },
  calendarHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 4,
  },
  monthTitle: { flex: 1, textAlign: "center" },
  weekdayRow: { flexDirection: "row", marginBottom: 8 },
  weekdayText: { color: "#667085", flex: 1, textAlign: "center" },
  grid: { gap: 6 },
  weekRow: { flexDirection: "row", gap: 6 },
  cell: {
    alignItems: "center",
    aspectRatio: 1,
    backgroundColor: "#FFFFFF",
    borderColor: "#D0D5DD",
    borderRadius: 8,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
  },
  cellOutside: { backgroundColor: "#F8FAFC", borderColor: "#EAECF0" },
  cellInRange: { backgroundColor: "#E8F1FF", borderColor: "#B2CCFF" },
  cellToday: { borderColor: "#2563EB" },
  cellSelected: { backgroundColor: "#2563EB", borderColor: "#2563EB" },
  cellPressed: { opacity: 0.8 },
  cellText: { color: "#101828" },
  cellTextOutside: { color: "#98A2B3" },
  cellTextSelected: { color: "#FFFFFF", fontWeight: "700" },
  actions: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    justifyContent: "space-between",
    marginTop: 8,
  },
});
