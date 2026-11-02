import { useEffect, useMemo, useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Dialog, HelperText, Portal, Text, TextInput } from "react-native-paper";

import {
  formatBirthdayParts,
  getBirthdayYearOptions,
  getMaxBirthdayDay,
  getMaxBirthdayMonth,
  parseBirthdayParts,
  updateBirthdayParts,
  type BirthdayParts,
} from "./birthday-picker";
import { calculateAge, validateBirthday } from "./birthday";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const ROW_HEIGHT = 44;
const COLUMN_HEIGHT = ROW_HEIGHT * 5;
const ACCENT = "#1256DB";

type ColumnKey = keyof BirthdayParts;

function PickerColumn({
  label,
  values,
  selected,
  format,
  onSelect,
}: {
  label: string;
  values: number[];
  selected: number;
  format: (value: number) => string;
  onSelect: (value: number) => void;
}) {
  const scrollRef = useRef<ScrollView>(null);
  const selectedIndex = Math.max(values.indexOf(selected), 0);

  useEffect(() => {
    // 选中项保持在列中间附近，打开或联动修正后都能直接看到当前值。
    scrollRef.current?.scrollTo({ y: Math.max(selectedIndex - 2, 0) * ROW_HEIGHT, animated: false });
  }, [selectedIndex, values.length]);

  return (
    <View style={styles.column}>
      <Text variant="labelMedium" style={styles.columnLabel}>{label}</Text>
      <ScrollView ref={scrollRef} style={styles.columnScroll} nestedScrollEnabled showsVerticalScrollIndicator={false}>
        {values.map((value) => {
          const active = value === selected;
          return (
            <Pressable
              key={value}
              accessibilityRole="button"
              accessibilityState={{ selected: active }}
              accessibilityLabel={`${label} ${format(value)}`}
              onPress={() => onSelect(value)}
              style={({ pressed }) => [styles.row, active && styles.rowActive, pressed && styles.rowPressed]}
            >
              <Text variant="bodyLarge" style={active ? styles.rowTextActive : undefined}>{format(value)}</Text>
            </Pressable>
          );
        })}
      </ScrollView>
    </View>
  );
}

/**
 * 年/月/日三列点选的生日弹窗（纯 JS，不依赖原生日期组件，OTA 可发布）。
 * 员工资料的 BirthdayPickerField 与人员管理的「新增员工 / 新增账号」共用。
 * `today` 决定「不能晚于今天」的边界（新增员工按悉尼当天）；`minYear` 收紧最早可选年份（新增员工为 1940）。
 */
export function BirthdayPickerDialog({
  visible,
  value,
  onDismiss,
  onConfirm,
  onClear,
  today,
  minYear,
}: {
  visible: boolean;
  value: string;
  onDismiss: () => void;
  onConfirm: (value: string) => void;
  /** 传入且已有值时显示「清除」。 */
  onClear?: () => void;
  today?: Date;
  minYear?: number;
}) {
  const { t } = useAppTranslation(["employeeProfile", "common"]);
  const [draft, setDraft] = useState<BirthdayParts>(() => parseBirthdayParts(value, today));
  // 每次打开都从当前值重新定位。
  useEffect(() => {
    if (visible) setDraft(parseBirthdayParts(value, today));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- today 只用于打开时定位，不随它重置草稿
  }, [visible, value]);
  const years = useMemo(
    () => getBirthdayYearOptions(today).filter((year) => minYear === undefined || year >= minYear),
    [minYear, today]
  );
  const months = useMemo(
    () => Array.from({ length: getMaxBirthdayMonth(draft.year, today) }, (_, index) => index + 1),
    [draft.year, today]
  );
  const days = useMemo(
    () => Array.from({ length: getMaxBirthdayDay(draft.year, draft.month, today) }, (_, index) => index + 1),
    [draft.month, draft.year, today]
  );
  const draftValue = formatBirthdayParts(draft);
  const draftError = validateBirthday(draftValue, today) ?? (minYear !== undefined && draft.year < minYear ? "tooOld" : null);
  const draftAge = draftError ? null : calculateAge(draftValue, today);
  const select = (key: ColumnKey, next: number) => setDraft((current) => updateBirthdayParts(current, { [key]: next }, today));
  const pad = (number: number) => String(number).padStart(2, "0");

  return (
    <Portal>
      <Dialog visible={visible} onDismiss={onDismiss}>
        <Dialog.Title>{t("birthdayPicker.title")}</Dialog.Title>
        <Dialog.Content style={styles.dialogContent}>
          <Text variant="titleMedium" style={styles.preview}>
            {draftAge === null ? draftValue : t("birthday.withAge", { date: draftValue, age: draftAge })}
          </Text>
          <View style={styles.columns}>
            <PickerColumn label={t("birthdayPicker.year")} values={years} selected={draft.year} format={String} onSelect={(next) => select("year", next)} />
            <PickerColumn label={t("birthdayPicker.month")} values={months} selected={draft.month} format={pad} onSelect={(next) => select("month", next)} />
            <PickerColumn label={t("birthdayPicker.day")} values={days} selected={draft.day} format={pad} onSelect={(next) => select("day", next)} />
          </View>
          {draftError ? <HelperText type="error" visible>{t(`birthday.errors.${draftError}`)}</HelperText> : null}
        </Dialog.Content>
        <Dialog.Actions>
          {value && onClear ? (
            <Button onPress={onClear}>{t("birthdayPicker.clear")}</Button>
          ) : null}
          <Button onPress={onDismiss}>{t("common:actions.cancel")}</Button>
          <Button
            mode="contained"
            buttonColor={ACCENT}
            disabled={Boolean(draftError)}
            onPress={() => onConfirm(draftValue)}
          >
            {t("common:actions.confirm")}
          </Button>
        </Dialog.Actions>
      </Dialog>
    </Portal>
  );
}

/**
 * 纯 JS 生日选择器：年/月/日三列点选，不依赖任何原生日期组件（OTA 可发布）。
 * 联动规则见 birthday-picker.ts，确认前仍以 birthday.ts 的 validateBirthday 兜底校验。
 */
export function BirthdayPickerField({
  label,
  value,
  onChange,
  confirmedLabel,
  disabled,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  /** 显示在年龄之后的「已确认」对照文案。 */
  confirmedLabel?: string;
  disabled?: boolean;
}) {
  const { t } = useAppTranslation(["employeeProfile", "common"]);
  const [visible, setVisible] = useState(false);
  const error = validateBirthday(value);
  const age = error ? null : calculateAge(value);

  const open = () => {
    if (disabled) return;
    setVisible(true);
  };

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={label}
        accessibilityState={{ disabled: Boolean(disabled) }}
        onPress={open}
        disabled={disabled}
      >
        <View pointerEvents="none">
          <TextInput
            mode="outlined"
            label={label}
            value={value}
            placeholder={t("birthdayPicker.placeholder")}
            editable={false}
            error={Boolean(error)}
            right={<TextInput.Icon icon="calendar-month-outline" />}
          />
        </View>
      </Pressable>
      {/* 有错误时提示原因，否则显示年龄并附上已确认的生日供对照。 */}
      <HelperText type={error ? "error" : "info"} visible>
        {error
          ? t(`birthday.errors.${error}`)
          : [age === null ? null : t("birthday.age", { age }), confirmedLabel].filter(Boolean).join(" · ")}
      </HelperText>
      <BirthdayPickerDialog
        visible={visible}
        value={value}
        onDismiss={() => setVisible(false)}
        onConfirm={(next) => { onChange(next); setVisible(false); }}
        onClear={() => { onChange(""); setVisible(false); }}
      />
    </>
  );
}

const styles = StyleSheet.create({
  dialogContent: { gap: HB_SPACING.sm },
  preview: { textAlign: "center", fontWeight: "700", color: HB_COLORS.textPrimary },
  columns: { flexDirection: "row", gap: HB_SPACING.xs },
  column: { flex: 1, gap: HB_SPACING.xxs },
  columnLabel: { textAlign: "center", color: HB_COLORS.textSecondary },
  columnScroll: {
    height: COLUMN_HEIGHT,
    borderRadius: HB_RADIUS.control,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  row: { height: ROW_HEIGHT, alignItems: "center", justifyContent: "center" },
  rowActive: { backgroundColor: "#EAF1FF" },
  rowPressed: { opacity: 0.6 },
  rowTextActive: { color: ACCENT, fontWeight: "700" },
});
