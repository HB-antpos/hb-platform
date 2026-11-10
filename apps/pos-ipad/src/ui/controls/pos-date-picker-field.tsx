import { MaterialCommunityIcons } from "@expo/vector-icons";
import DateTimePicker, {
  type DateTimePickerEvent,
} from "@react-native-community/datetimepicker";
import { useState } from "react";
import {
  Keyboard,
  Modal,
  StyleSheet,
  Text,
  View,
} from "react-native";

import {
  createPosStyles,
  getActivePosTheme,
  posColors,
  posRadius,
  posTouch,
  usePosThemeVersion,
} from "../theme";
import { PosButton } from "./pos-button";
import { PosPressable } from "./pos-pressable";

export type PosDatePickerLocale = "en" | "zh";

export interface PosDatePickerFieldProps {
  accessibilityLabel: string;
  allowClear?: boolean;
  disabled?: boolean;
  locale: PosDatePickerLocale;
  onChange(value: string | null): void;
  testID: string;
  value: string | null;
}

const copy = {
  en: {
    anyDate: "Any date",
    cancel: "Cancel",
    clear: "Clear",
    confirm: "Confirm",
    openHint: "Opens the date picker",
    selectDate: "Select date",
  },
  zh: {
    anyDate: "不限日期",
    cancel: "取消",
    clear: "清除",
    confirm: "确定",
    openHint: "打开日期选择器",
    selectDate: "请选择日期",
  },
} as const;

const LOCAL_DATE_KEY_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

function dateFromLocalKey(value: string | null): Date | null {
  const match = value?.match(LOCAL_DATE_KEY_PATTERN);
  if (!match) return null;

  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const date = new Date(year, monthIndex, day, 12);
  if (
    date.getFullYear() !== year ||
    date.getMonth() !== monthIndex ||
    date.getDate() !== day
  ) {
    return null;
  }
  return date;
}

function localKeyFromDate(date: Date): string {
  // 日历筛选必须使用设备本地年月日，不能经 UTC 序列化后再截断。
  const year = String(date.getFullYear()).padStart(4, "0");
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function PosDatePickerField({
  accessibilityLabel,
  allowClear = false,
  disabled = false,
  locale,
  onChange,
  testID,
  value,
}: Readonly<PosDatePickerFieldProps>) {
  usePosThemeVersion();
  const text = copy[locale];
  const [open, setOpen] = useState(false);
  const [draftDate, setDraftDate] = useState<Date>(
    () => dateFromLocalKey(value) ?? new Date(),
  );
  const displayValue = value ?? (allowClear ? text.anyDate : text.selectDate);

  const openPicker = (): void => {
    if (disabled) return;
    Keyboard.dismiss();
    setDraftDate(dateFromLocalKey(value) ?? new Date());
    setOpen(true);
  };

  const closePicker = (): void => {
    setOpen(false);
  };

  const confirm = (): void => {
    onChange(localKeyFromDate(draftDate));
    closePicker();
  };

  const clear = (): void => {
    onChange(null);
    closePicker();
  };

  const handlePickerChange = (
    _event: DateTimePickerEvent,
    nextDate?: Date,
  ): void => {
    if (nextDate && !Number.isNaN(nextDate.getTime())) {
      setDraftDate(nextDate);
    }
  };

  return (
    <>
      <PosPressable
        accessibilityHint={text.openHint}
        accessibilityLabel={accessibilityLabel}
        accessibilityRole="button"
        accessibilityState={{ disabled }}
        disabled={disabled}
        onPress={openPicker}
        style={({ pressed }) => [
          styles.trigger,
          disabled && styles.triggerDisabled,
          pressed && !disabled && styles.pressed,
        ]}
        sound="navigate"
        testID={testID}
      >
        <MaterialCommunityIcons
          accessible={false}
          color={
            disabled
              ? posColors.disabledInk
              : value
                ? posColors.ink
                : posColors.placeholder
          }
          name="calendar-outline"
          size={22}
        />
        <Text
          numberOfLines={1}
          style={[
            styles.triggerText,
            !value && styles.placeholderText,
            disabled && styles.triggerTextDisabled,
          ]}
        >
          {displayValue}
        </Text>
      </PosPressable>

      {open ? (
        <Modal
          animationType="fade"
          onRequestClose={closePicker}
          presentationStyle="overFullScreen"
          statusBarTranslucent
          supportedOrientations={["landscape-left", "landscape-right"]}
          testID={`${testID}-modal`}
          transparent
          visible
        >
          <View
            accessibilityViewIsModal
            style={styles.overlay}
            testID={`${testID}-overlay`}
          >
            <PosPressable
              accessible={false}
              onPress={closePicker}
              sound="navigate"
              style={styles.backdropDismissArea}
              testID={`${testID}-backdrop`}
            />
            <View style={styles.panel}>
              <Text numberOfLines={1} style={styles.title}>
                {accessibilityLabel}
              </Text>
              {/* 原生日历的明暗跟随当前主题，否则深色主题下是深底深字。 */}
              <DateTimePicker
                accentColor={posColors.accent}
                display="inline"
                locale={locale === "zh" ? "zh-CN" : "en-AU"}
                mode="date"
                onChange={handlePickerChange}
                style={styles.picker}
                testID={`${testID}-picker`}
                themeVariant={getActivePosTheme().dark ? "dark" : "light"}
                value={draftDate}
              />
              <View style={styles.actions}>
                {allowClear ? (
                  <PosButton
                    label={text.clear}
                    onPress={clear}
                    size="medium"
                    sound="danger"
                    style={styles.clearButton}
                    testID={`${testID}-clear`}
                    tone="info"
                  />
                ) : null}
                <PosButton
                  label={text.cancel}
                  onPress={closePicker}
                  size="medium"
                  sound="tap"
                  style={styles.actionButton}
                  testID={`${testID}-cancel`}
                />
                <PosButton
                  label={text.confirm}
                  onPress={confirm}
                  size="medium"
                  sound="navigate"
                  style={styles.actionButton}
                  testID={`${testID}-confirm`}
                  tone="primary"
                />
              </View>
            </View>
          </View>
        </Modal>
      ) : null}
    </>
  );
}

const styles = createPosStyles((colors) => ({
  // 触发器与输入框同一套外观：高 56、强描边、左侧日历图标。
  trigger: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: posTouch.medium,
    minWidth: 142,
    paddingHorizontal: 14,
  },
  triggerDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
  },
  triggerText: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  triggerTextDisabled: { color: colors.disabledInk },
  placeholderText: { color: colors.placeholder, fontWeight: "600" },
  pressed: { opacity: 0.62 },
  overlay: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  panel: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.sheet,
    borderWidth: 1,
    elevation: 12,
    maxWidth: "92%",
    padding: 22,
    shadowColor: colors.shadow,
    shadowOffset: { height: 8, width: 0 },
    shadowOpacity: 0.2,
    shadowRadius: 18,
    width: 420,
  },
  title: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
    marginBottom: 8,
  },
  picker: {
    alignSelf: "center",
    width: 360,
  },
  actions: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "flex-end",
    marginTop: 10,
  },
  actionButton: { minWidth: 104 },
  // 「清除」靠左，与取消 / 确定分开，避免误点。
  clearButton: { marginRight: "auto", minWidth: 104 },
}));
