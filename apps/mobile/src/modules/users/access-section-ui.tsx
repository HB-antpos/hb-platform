import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Searchbar, Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";

/** 授权页四个分段共用的小部件。颜色只用 tokens，不手写十六进制。 */

export function AccessSearchBox({
  value,
  onChangeText,
  placeholder,
}: {
  value: string;
  onChangeText: (value: string) => void;
  placeholder: string;
}) {
  return (
    <Searchbar
      value={value}
      onChangeText={onChangeText}
      placeholder={placeholder}
      accessibilityLabel={placeholder}
      autoCorrect={false}
      autoCapitalize="none"
      style={styles.search}
      inputStyle={styles.searchInput}
    />
  );
}

/** 角色勾选框：勾选放右侧；锁定（店长由分店派生）显示小锁。 */
export function AccessCheckbox({
  checked,
  locked = false,
  disabled = false,
}: {
  checked: boolean;
  locked?: boolean;
  disabled?: boolean;
}) {
  return (
    <View
      style={[
        styles.checkbox,
        checked && styles.checkboxChecked,
        locked && !checked && styles.checkboxLocked,
        disabled && styles.checkboxDisabled,
      ]}
    >
      {locked && !checked ? (
        <Icon source="lock" size={14} color={HB_COLORS.textSecondary} />
      ) : checked ? (
        <Icon source="check" size={16} color={HB_COLORS.white} />
      ) : null}
    </View>
  );
}

/** 卡片内一行的统一容器：第一行不画上分隔线。 */
export function AccessRowFrame({
  first,
  children,
  onPress,
  disabled,
  accessibilityRole,
  accessibilityLabel,
  accessibilityHint,
  accessibilityState,
  testID,
}: {
  first: boolean;
  children: ReactNode;
  onPress?: () => void;
  disabled?: boolean;
  accessibilityRole?: "checkbox" | "button";
  accessibilityLabel?: string;
  accessibilityHint?: string;
  accessibilityState?: { checked?: boolean; disabled?: boolean };
  testID?: string;
}) {
  const body = (
    <View style={[styles.rowFrame, !first && styles.rowDivider]} testID={testID}>
      {children}
    </View>
  );
  if (!onPress) return body;
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      accessibilityRole={accessibilityRole}
      accessibilityLabel={accessibilityLabel}
      accessibilityHint={accessibilityHint}
      accessibilityState={accessibilityState}
      style={({ pressed }) => [pressed && styles.pressed]}
    >
      {body}
    </Pressable>
  );
}

export function AccessEmptyText({ children }: { children: ReactNode }) {
  return <Text style={styles.emptyText}>{children}</Text>;
}

export const accessSectionStyles = StyleSheet.create({
  stack: { gap: 10 },
});

const styles = StyleSheet.create({
  search: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    elevation: 0,
    height: 44,
  },
  searchInput: { fontSize: 14, minHeight: 44 },
  checkbox: {
    alignItems: "center",
    borderColor: HB_COLORS.outline,
    borderRadius: 7,
    borderWidth: 1.5,
    height: 24,
    justifyContent: "center",
    width: 24,
  },
  checkboxChecked: { backgroundColor: HB_COLORS.brand, borderColor: HB_COLORS.brand },
  checkboxLocked: { backgroundColor: HB_COLORS.surfaceMuted, borderColor: HB_COLORS.outlineMuted },
  checkboxDisabled: { opacity: 0.55 },
  rowFrame: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
    minHeight: 58,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  rowDivider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  emptyText: {
    color: HB_COLORS.textSecondary,
    fontSize: 13.5,
    lineHeight: 20,
    paddingVertical: 24,
    textAlign: "center",
  },
});
