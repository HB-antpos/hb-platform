import { StyleSheet, Text, View } from "react-native";

import { MIN_TOUCH_TARGET } from "./sales-presenter";

import { PosPressable } from "@/ui/controls/pos-pressable";
import { posColors } from "@/ui/theme";

export type SalesSearchKeyAction =
  | "backspace"
  | "space"
  | "clear"
  | "search"
  | "close"
  | "system";

export type SalesSearchKey =
  | Readonly<{ kind: "char"; char: string }>
  | Readonly<{ kind: SalesSearchKeyAction }>;

export type SalesSearchKeyboardLabels = Readonly<{
  title: string;
  backspace: string;
  space: string;
  clear: string;
  search: string;
  close: string;
  system: string;
}>;

export type SalesSearchKeyboardProps = Readonly<{
  disabled?: boolean;
  labels: SalesSearchKeyboardLabels;
  onKeyPress(key: SalesSearchKey): void;
}>;

// 防止误触长按把搜索词撑得过长；商品名与货号都远小于此长度。
export const SALES_SEARCH_QUERY_MAX_LENGTH = 80;

type CharacterRow = Readonly<{
  chars: readonly string[];
  /** 行首尾各补的空白键宽（按键单位），让各行按键等宽并居中错位。 */
  inset: number;
}>;

// 每行合计 11 个按键单位：数字行 10 键 + 退格，字母行用两侧留白补齐。
const CHARACTER_ROWS: readonly CharacterRow[] = [
  { chars: ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"], inset: 0 },
  { chars: ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"], inset: 0.5 },
  { chars: ["A", "S", "D", "F", "G", "H", "J", "K", "L", "-"], inset: 0.5 },
  { chars: ["Z", "X", "C", "V", "B", "N", "M", ".", "/"], inset: 1 },
];

/**
 * 只处理会改变搜索词的按键；搜索、收起、系统键盘由界面层分发。
 * 空格不允许出现在开头或连续出现，避免产生肉眼不可见的无效搜索词。
 */
export function applySalesSearchKey(
  value: string,
  key: SalesSearchKey,
): string {
  switch (key.kind) {
    case "char":
      return value.length >= SALES_SEARCH_QUERY_MAX_LENGTH
        ? value
        : `${value}${key.char}`;
    case "space":
      if (
        value.length === 0 ||
        value.endsWith(" ") ||
        value.length >= SALES_SEARCH_QUERY_MAX_LENGTH
      ) {
        return value;
      }
      return `${value} `;
    case "backspace":
      return value.slice(0, -1);
    case "clear":
      return "";
    default:
      return value;
  }
}

export function SalesSearchKeyboard({
  disabled = false,
  labels,
  onKeyPress,
}: SalesSearchKeyboardProps) {
  return (
    <View
      accessibilityLabel={labels.title}
      style={styles.keyboard}
      testID="sales-search-keyboard"
    >
      {CHARACTER_ROWS.map((row, rowIndex) => (
        <View key={rowIndex} style={styles.row}>
          {row.inset > 0 ? <View style={{ flex: row.inset }} /> : null}
          {row.chars.map((char) => (
            <SearchKey
              accessibilityLabel={char}
              disabled={disabled}
              key={char}
              label={char}
              onPress={() => onKeyPress({ kind: "char", char })}
              sound="key"
              testID={`sales-search-keyboard-key-${char}`}
            />
          ))}
          {rowIndex === 0 ? (
            <SearchKey
              accessibilityLabel={labels.backspace}
              disabled={disabled}
              label="⌫"
              onPress={() => onKeyPress({ kind: "backspace" })}
              sound="key"
              testID="sales-search-keyboard-key-backspace"
            />
          ) : null}
          {row.inset > 0 ? <View style={{ flex: row.inset }} /> : null}
        </View>
      ))}
      <View style={styles.row}>
        <SearchKey
          accessibilityLabel={labels.system}
          disabled={disabled}
          flex={2}
          label={labels.system}
          onPress={() => onKeyPress({ kind: "system" })}
          sound="navigate"
          testID="sales-search-keyboard-key-system"
          tone="quiet"
        />
        <SearchKey
          accessibilityLabel={labels.space}
          disabled={disabled}
          flex={4.5}
          label={labels.space}
          onPress={() => onKeyPress({ kind: "space" })}
          sound="key"
          testID="sales-search-keyboard-key-space"
        />
        <SearchKey
          accessibilityLabel={labels.clear}
          disabled={disabled}
          flex={1.5}
          label={labels.clear}
          onPress={() => onKeyPress({ kind: "clear" })}
          sound="danger"
          testID="sales-search-keyboard-key-clear"
          tone="danger"
        />
        <SearchKey
          accessibilityLabel={labels.close}
          disabled={disabled}
          flex={1.5}
          label={labels.close}
          onPress={() => onKeyPress({ kind: "close" })}
          sound="navigate"
          testID="sales-search-keyboard-key-close"
          tone="quiet"
        />
        <SearchKey
          accessibilityLabel={labels.search}
          disabled={disabled}
          flex={1.5}
          label={labels.search}
          onPress={() => onKeyPress({ kind: "search" })}
          sound="tap"
          testID="sales-search-keyboard-key-search"
          tone="primary"
        />
      </View>
    </View>
  );
}

function SearchKey({
  accessibilityLabel,
  disabled,
  flex = 1,
  label,
  onPress,
  sound,
  testID,
  tone = "default",
}: Readonly<{
  accessibilityLabel: string;
  disabled: boolean;
  flex?: number;
  label: string;
  onPress(): void;
  sound: "tap" | "key" | "navigate" | "danger";
  testID: string;
  tone?: "default" | "quiet" | "danger" | "primary";
}>) {
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={({ pressed }) => [
        styles.key,
        { flex },
        tone === "quiet" && styles.quietKey,
        tone === "danger" && styles.dangerKey,
        tone === "primary" && styles.primaryKey,
        disabled && styles.keyDisabled,
        pressed && !disabled && styles.keyPressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={1}
        style={[
          styles.keyText,
          tone !== "default" && styles.functionKeyText,
          tone === "danger" && styles.dangerKeyText,
          tone === "primary" && styles.primaryKeyText,
          disabled && styles.keyTextDisabled,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

const KEY_HEIGHT = 52;

const styles = StyleSheet.create({
  keyboard: {
    gap: 8,
    width: "100%",
  },
  row: {
    flexDirection: "row",
    gap: 8,
  },
  // 与 SalesNumberKeypad 保持一致的按键外观，收银员在两套键盘间不需重新适应。
  key: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 4,
    borderWidth: 1,
    height: KEY_HEIGHT,
    justifyContent: "center",
    minWidth: MIN_TOUCH_TARGET,
    paddingHorizontal: 6,
  },
  quietKey: {
    backgroundColor: posColors.canvas,
  },
  dangerKey: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
  },
  primaryKey: {
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
  },
  keyDisabled: {
    backgroundColor: posColors.canvas,
    borderColor: posColors.border,
    opacity: 0.62,
  },
  keyPressed: {
    opacity: 0.72,
    transform: [{ scale: 0.98 }],
  },
  keyText: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "800",
  },
  functionKeyText: {
    fontSize: 16,
  },
  dangerKeyText: {
    color: posColors.red,
  },
  primaryKeyText: {
    color: "#FFFFFF",
  },
  keyTextDisabled: {
    color: posColors.mutedInk,
  },
});
