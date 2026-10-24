import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Searchbar, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { NamedOption } from "./types";

/**
 * 多选面板（分店 / 供应商）：顶部搜索，逐项勾选，「完成」才写回（中途关闭不改变原选择）。
 * 已选但不在选项里的编号（例如供应商已停用）仍列在最前面，可以取消勾选，避免保存时被悄悄丢掉。
 * 调用方只在打开时挂载本组件，草稿从挂载那一刻的 selected 初始化。
 */
export function MultiSelectSheet({
  title,
  options,
  selected,
  onDone,
  onClose,
}: {
  title: string;
  options: NamedOption[];
  selected: string[];
  onDone: (codes: string[]) => void;
  onClose: () => void;
}) {
  const { t } = useAppTranslation("pricingStrategies");
  const [draft, setDraft] = useState<string[]>(selected);
  const [keyword, setKeyword] = useState("");

  const all = useMemo(() => {
    const known = new Set(options.map((option) => option.code));
    const orphans = selected.filter((code) => !known.has(code)).map((code) => ({ code, name: code }));
    return [...orphans, ...options];
  }, [options, selected]);
  const filtered = useMemo(() => {
    const needle = keyword.trim().toLowerCase();
    return needle ? all.filter((option) => option.name.toLowerCase().includes(needle) || option.code.toLowerCase().includes(needle)) : all;
  }, [all, keyword]);
  const chosen = new Set(draft);
  const toggle = (code: string) => setDraft((current) => (current.includes(code) ? current.filter((item) => item !== code) : [...current, code]));

  return (
    <BusinessSheet
      visible
      title={title}
      subtitle={t("editor.selectedCount", { count: draft.length })}
      onDismiss={onClose}
      footer={
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.button} onPress={() => setDraft([])} disabled={!draft.length}>
            {t("editor.clearSelection")}
          </Button>
          <Button mode="contained" style={[styles.button, styles.primary]} onPress={() => onDone(draft)}>
            {t("actions.done")}
          </Button>
        </View>
      }
    >
      <Searchbar value={keyword} onChangeText={setKeyword} placeholder={t("editor.searchPlaceholder")} style={styles.search} inputStyle={styles.searchInput} />
      {filtered.map((option) => {
        const on = chosen.has(option.code);
        return (
          <Pressable
            key={option.code}
            accessibilityRole="checkbox"
            accessibilityState={{ checked: on }}
            onPress={() => toggle(option.code)}
            style={({ pressed }) => [styles.row, pressed ? styles.pressed : null]}
          >
            <Icon source={on ? "checkbox-marked" : "checkbox-blank-outline"} size={22} color={on ? HB_COLORS.brand : HB_COLORS.outline} />
            <Text style={styles.name} numberOfLines={1}>
              {option.name}
            </Text>
            {option.name !== option.code ? <Text style={styles.code}>{option.code}</Text> : null}
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  search: { height: 42, marginBottom: HB_SPACING.xs, backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, elevation: 0 },
  searchInput: { minHeight: 40, fontSize: 14 },
  row: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, minHeight: 46, paddingHorizontal: 4, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  name: { flex: 1, fontSize: 15, color: HB_COLORS.textPrimary },
  code: { fontSize: 12, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: HB_SPACING.sm },
  button: { borderRadius: HB_RADIUS.control, flex: 1 },
  primary: { flex: 2 },
});
