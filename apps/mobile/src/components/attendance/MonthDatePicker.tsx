import { useEffect, useMemo, useState } from "react";
import {
  Pressable,
  StyleSheet,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import {
  Button,
  Card,
  IconButton,
  Modal,
  Portal,
  Surface,
  Text,
} from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { canNavigateToMonth, isMonthDateInRange } from "./month-date-picker-range";

const GRID_DAYS = 42;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

interface MonthDatePickerProps {
  value?: string;
  defaultValue?: string;
  disabled?: boolean;
  allowEmpty?: boolean;
  minDate?: string;
  maxDate?: string;
  onChange?: (date: string) => void;
  style?: StyleProp<ViewStyle>;
}

interface MonthDatePickerCardProps extends MonthDatePickerProps {
  title?: string;
  subtitle?: string;
}

interface MonthDatePickerFieldProps extends MonthDatePickerProps {
  label?: string;
  placeholder?: string;
  compact?: boolean;
  /** 紧凑模式下跟在日期后面的灰色小字（如星期）；未选日期时不显示。 */
  valueSuffix?: string;
}

function pad2(value: number) {
  return value.toString().padStart(2, "0");
}

export function formatMonthDate(date: Date) {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

function parseMonthDate(value?: string) {
  const match = value?.match(DATE_PATTERN);
  if (!match) {
    return undefined;
  }

  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const parsed = new Date(year, monthIndex, day);

  if (
    parsed.getFullYear() !== year ||
    parsed.getMonth() !== monthIndex ||
    parsed.getDate() !== day
  ) {
    return undefined;
  }

  return parsed;
}

export function normalizeMonthDate(value?: string) {
  return formatMonthDate(parseMonthDate(value) ?? new Date());
}

function getMonthStart(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function addMonths(date: Date, count: number) {
  return new Date(date.getFullYear(), date.getMonth() + count, 1);
}

function buildMonthGrid(displayMonth: Date) {
  const monthStart = getMonthStart(displayMonth);
  const mondayOffset = (monthStart.getDay() + 6) % 7;
  const gridStart = new Date(monthStart.getFullYear(), monthStart.getMonth(), 1 - mondayOffset);

  return Array.from({ length: GRID_DAYS }, (_, index) => {
    const date = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + index);
    return {
      date,
      dateString: formatMonthDate(date),
      isCurrentMonth: date.getMonth() === monthStart.getMonth(),
    };
  });
}

function chunkWeeks<T>(items: T[]) {
  return Array.from({ length: Math.ceil(items.length / 7) }, (_, index) => items.slice(index * 7, index * 7 + 7));
}

export function MonthDatePicker({
  value,
  defaultValue,
  disabled = false,
  allowEmpty = false,
  minDate,
  maxDate,
  onChange,
  style,
}: MonthDatePickerProps) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const isControlled = value !== undefined;
  const [internalValue, setInternalValue] = useState(() => normalizeMonthDate(defaultValue));
  const selectedDateString = isControlled && allowEmpty && !value
    ? ""
    : isControlled
      ? normalizeMonthDate(value)
      : internalValue;
  const selectedDate = parseMonthDate(selectedDateString) ?? new Date();
  const [displayMonth, setDisplayMonth] = useState(() => getMonthStart(selectedDate));

  useEffect(() => {
    if (isControlled) {
      setDisplayMonth(getMonthStart(parseMonthDate(value) ?? new Date()));
    }
  }, [isControlled, value]);

  const today = useMemo(() => formatMonthDate(new Date()), []);
  const monthCells = useMemo(() => buildMonthGrid(displayMonth), [displayMonth]);
  const weeks = useMemo(() => chunkWeeks(monthCells), [monthCells]);
  const monthTitle = `${displayMonth.getFullYear()}-${pad2(displayMonth.getMonth() + 1)}`;
  const previousMonth = addMonths(displayMonth, -1);
  const nextMonth = addMonths(displayMonth, 1);

  const selectDate = (date: Date) => {
    if (disabled || !isMonthDateInRange(formatMonthDate(date), minDate, maxDate)) {
      return;
    }

    const nextValue = formatMonthDate(date);
    if (!isControlled) {
      setInternalValue(nextValue);
    }
    setDisplayMonth(getMonthStart(date));
    onChange?.(nextValue);
  };

  return (
    <View style={[styles.container, style]}>
      <View style={styles.monthHeader}>
        <IconButton
          icon="chevron-left"
          size={22}
          onPress={() => setDisplayMonth(previousMonth)}
          disabled={disabled || !canNavigateToMonth(previousMonth, minDate, maxDate)}
        />
        <Text variant="titleMedium" style={styles.monthTitle}>
          {monthTitle}
        </Text>
        <IconButton
          icon="chevron-right"
          size={22}
          onPress={() => setDisplayMonth(nextMonth)}
          disabled={disabled || !canNavigateToMonth(nextMonth, minDate, maxDate)}
        />
      </View>

      <View style={styles.weekdayRow}>
        {Array.from({ length: 7 }, (_, index) => (
          <Text key={index} variant="labelSmall" style={styles.weekdayText}>
            {t(`weekdays.${index}`, ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][index])}
          </Text>
        ))}
      </View>

      <View style={styles.grid}>
        {weeks.map((week, weekIndex) => (
          <View key={weekIndex} style={styles.dateRow}>
            {week.map((cell) => {
              const isSelected = cell.dateString === selectedDateString;
              const isToday = cell.dateString === today;
              const isOutOfRange = !isMonthDateInRange(cell.dateString, minDate, maxDate);
              return (
                <Pressable
                  key={cell.dateString}
                  accessibilityRole="button"
                  accessibilityState={{ disabled: disabled || isOutOfRange, selected: isSelected }}
                  disabled={disabled || isOutOfRange}
                  onPress={() => selectDate(cell.date)}
                  style={({ pressed }) => [
                    styles.dateCell,
                    !cell.isCurrentMonth ? styles.outsideDateCell : null,
                    isToday ? styles.todayCell : null,
                    isSelected ? styles.selectedDateCell : null,
                    pressed && !disabled && !isOutOfRange ? styles.pressedDateCell : null,
                    disabled || isOutOfRange ? styles.disabledDateCell : null,
                  ]}
                >
                  <Text
                    variant="labelLarge"
                    style={[
                      styles.dateText,
                      !cell.isCurrentMonth ? styles.outsideDateText : null,
                      isSelected ? styles.selectedDateText : null,
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
    </View>
  );
}

export function MonthDatePickerCard({
  title,
  subtitle,
  ...pickerProps
}: MonthDatePickerCardProps) {
  const { t } = useAppTranslation(["attendance", "common"]);

  return (
    <Card mode="elevated" style={styles.card}>
      <Card.Title title={title ?? t("fields.workDate")} subtitle={subtitle} />
      <Card.Content>
        <MonthDatePicker {...pickerProps} />
      </Card.Content>
    </Card>
  );
}

export function MonthDatePickerField({
  value,
  defaultValue,
  disabled = false,
  minDate,
  maxDate,
  label,
  placeholder,
  compact = false,
  valueSuffix,
  onChange,
  style,
}: MonthDatePickerFieldProps) {
  const { t } = useAppTranslation(["attendance", "common"]);
  const isControlled = value !== undefined;
  const [internalValue, setInternalValue] = useState(() =>
    normalizeMonthDate(defaultValue),
  );
  const [visible, setVisible] = useState(false);
  const selectedValue = isControlled ? normalizeMonthDate(value) : internalValue;

  const openModal = () => {
    if (disabled) {
      return;
    }
    setVisible(true);
  };

  const closeModal = () => setVisible(false);

  const handleChange = (nextValue: string) => {
    if (!isControlled) {
      setInternalValue(nextValue);
    }
    onChange?.(nextValue);
    closeModal();
  };

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ disabled, expanded: visible }}
        disabled={disabled}
        onPress={openModal}
        style={({ pressed }) => [
          styles.fieldPressable,
          style,
          pressed && !disabled ? styles.fieldPressed : null,
        ]}
      >
        <Surface
          style={[
            styles.fieldSurface,
            compact ? styles.fieldSurfaceCompact : null,
            disabled ? styles.fieldSurfaceDisabled : null,
          ]}
          elevation={0}
        >
          <View style={styles.fieldTextBlock}>
            {!compact ? (
              <Text variant="labelMedium" style={styles.fieldLabel}>
                {label ?? t("fields.workDate")}
              </Text>
            ) : null}
            <Text
              variant={compact ? "bodyMedium" : "bodyLarge"}
              numberOfLines={compact ? 1 : undefined}
              style={selectedValue ? styles.fieldValue : styles.fieldPlaceholder}
            >
              {selectedValue || placeholder || t("fields.workDate")}
              {compact && selectedValue && valueSuffix ? (
                <Text style={styles.fieldSuffix}>{`  ${valueSuffix}`}</Text>
              ) : null}
            </Text>
          </View>
          <IconButton
            icon="calendar-month-outline"
            size={20}
            disabled={disabled}
            style={compact ? styles.fieldIconCompact : undefined}
          />
        </Surface>
      </Pressable>

      <Portal>
        <Modal
          visible={visible}
          onDismiss={closeModal}
          contentContainerStyle={styles.modalContainer}
        >
          <Surface style={styles.modalSurface} elevation={1}>
            <View style={styles.modalHeader}>
              <Text variant="titleMedium">{label ?? t("fields.workDate")}</Text>
              <Button onPress={closeModal}>{t("common:actions.cancel")}</Button>
            </View>
            <MonthDatePicker
              value={selectedValue}
              onChange={handleChange}
              disabled={disabled}
              minDate={minDate}
              maxDate={maxDate}
            />
          </Surface>
        </Modal>
      </Portal>
    </>
  );
}

const styles = StyleSheet.create({
  card: {
    borderRadius: 8,
  },
  container: {
    gap: 8,
  },
  dateCell: {
    alignItems: "center",
    aspectRatio: 1,
    borderColor: "transparent",
    borderRadius: 6,
    borderWidth: StyleSheet.hairlineWidth,
    flex: 1,
    justifyContent: "center",
    minHeight: 36,
  },
  dateRow: {
    flexDirection: "row",
    gap: 4,
  },
  dateText: {
    color: "#111827",
  },
  fieldLabel: {
    color: "#6B7280",
  },
  fieldPlaceholder: {
    color: "#9CA3AF",
  },
  fieldSuffix: {
    color: "#6B7280",
    fontSize: 12,
  },
  fieldPressed: {
    opacity: 0.9,
  },
  fieldPressable: {
    borderRadius: 8,
  },
  fieldSurface: {
    alignItems: "center",
    borderColor: "#D1D5DB",
    borderRadius: 8,
    borderWidth: 1,
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 56,
    paddingLeft: 16,
  },
  fieldSurfaceDisabled: {
    backgroundColor: "#F3F4F6",
    opacity: 0.7,
  },
  fieldSurfaceCompact: {
    minHeight: 44,
    paddingLeft: 12,
  },
  fieldIconCompact: {
    width: 44,
    height: 44,
    margin: 0,
  },
  fieldTextBlock: {
    flex: 1,
    gap: 2,
    paddingVertical: 10,
  },
  fieldValue: {
    color: "#111827",
  },
  disabledDateCell: {
    opacity: 0.5,
  },
  grid: {
    gap: 4,
  },
  monthHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  monthTitle: {
    flex: 1,
    textAlign: "center",
  },
  modalContainer: {
    margin: 20,
  },
  modalHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  modalSurface: {
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    gap: 12,
    padding: 16,
  },
  outsideDateCell: {
    backgroundColor: "#F9FAFB",
  },
  outsideDateText: {
    color: "#9CA3AF",
  },
  pressedDateCell: {
    backgroundColor: "#E0F2FE",
  },
  selectedDateCell: {
    backgroundColor: "#2563EB",
    borderColor: "#2563EB",
  },
  selectedDateText: {
    color: "#FFFFFF",
  },
  todayCell: {
    borderColor: "#22C55E",
  },
  weekdayRow: {
    flexDirection: "row",
    gap: 4,
  },
  weekdayText: {
    color: "#6B7280",
    flex: 1,
    textAlign: "center",
  },
});
