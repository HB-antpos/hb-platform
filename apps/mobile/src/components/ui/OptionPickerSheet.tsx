import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { Button, Icon, Modal, Portal, Searchbar, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface OptionPickerItem {
  value: string;
  label: string;
  /** 同一分组的选项连续排列时显示一次分组标题。 */
  group?: string;
  icon?: string;
}

export interface OptionPickerSheetProps {
  visible: boolean;
  title: string;
  cancelLabel: string;
  options: OptionPickerItem[];
  selectedValue: string | null;
  searchable?: boolean;
  searchPlaceholder?: string;
  onSelect: (value: string) => void;
  onDismiss: () => void;
}

/**
 * 通用单选底部下拉：给筛选 chip 用，点一项立即生效。
 * 通过 Paper Portal 渲染，只能在页面层使用；若调用方本身在原生 Modal 里会被盖住。
 */
export function OptionPickerSheet({
  visible,
  title,
  cancelLabel,
  options,
  selectedValue,
  searchable = false,
  searchPlaceholder,
  onSelect,
  onDismiss,
}: OptionPickerSheetProps) {
  const { height } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const [keyword, setKeyword] = useState("");
  useEffect(() => {
    if (visible) setKeyword("");
  }, [visible]);

  const filtered = useMemo(() => {
    const needle = keyword.trim().toLowerCase();
    if (!needle) return options;
    return options.filter(
      (option) =>
        option.label.toLowerCase().includes(needle) ||
        option.value.toLowerCase().includes(needle),
    );
  }, [keyword, options]);
  const maxListHeight = Math.max(200, Math.min(520, height * 0.6));

  return (
    <Portal>
      <Modal
        visible={visible}
        onDismiss={onDismiss}
        style={styles.overlay}
        contentContainerStyle={[styles.sheet, { paddingBottom: Math.max(insets.bottom, 16) }]}
      >
        <View style={styles.handle} />
        <Text variant="titleMedium" style={styles.title}>
          {title}
        </Text>
        {searchable ? (
          <Searchbar
            placeholder={searchPlaceholder}
            value={keyword}
            onChangeText={setKeyword}
            style={styles.search}
            inputStyle={styles.searchInput}
          />
        ) : null}
        <ScrollView
          style={{ maxHeight: maxListHeight }}
          keyboardShouldPersistTaps="handled"
          bounces={false}
        >
          {filtered.map((option, index) => {
            const showGroup =
              option.group && (index === 0 || filtered[index - 1].group !== option.group);
            const selected = (selectedValue ?? "") === option.value;
            return (
              <View key={`${option.group ?? ""}:${option.value}`}>
                {showGroup ? <Text style={styles.group}>{option.group}</Text> : null}
                <Pressable
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                  onPress={() => onSelect(option.value)}
                  style={({ pressed }) => [styles.row, pressed ? styles.rowPressed : null]}
                >
                  {option.icon ? (
                    <Icon
                      source={option.icon}
                      size={18}
                      color={selected ? HB_COLORS.action : HB_COLORS.textSecondary}
                    />
                  ) : null}
                  {/* 不截断：供应商等长名称前缀相同的很多，截断后无法区分，允许换行、行高随内容增长。 */}
                  <Text style={[styles.rowText, selected ? styles.rowTextSelected : null]}>
                    {option.label}
                  </Text>
                  {selected ? <Icon source="check" size={18} color={HB_COLORS.action} /> : null}
                </Pressable>
              </View>
            );
          })}
        </ScrollView>
        <View style={styles.actions}>
          <Button onPress={onDismiss}>{cancelLabel}</Button>
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
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 10,
  },
  handle: {
    width: 44,
    height: 4,
    borderRadius: 2,
    backgroundColor: HB_COLORS.outline,
    alignSelf: "center",
    marginBottom: 12,
  },
  title: { fontWeight: "700", color: HB_COLORS.textPrimary, marginBottom: HB_SPACING.xs },
  search: { backgroundColor: HB_COLORS.surfaceMuted, marginBottom: HB_SPACING.xs },
  searchInput: { fontSize: 14, minHeight: 0 },
  group: {
    fontSize: 11,
    color: HB_COLORS.textSecondary,
    paddingTop: HB_SPACING.sm,
    paddingBottom: 4,
  },
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 46,
    paddingHorizontal: 4,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowText: { flex: 1, fontSize: 14, lineHeight: 20, color: HB_COLORS.textPrimary },
  rowTextSelected: { color: HB_COLORS.action, fontWeight: "600" },
  actions: { alignItems: "flex-end", marginTop: HB_SPACING.xs },
});
