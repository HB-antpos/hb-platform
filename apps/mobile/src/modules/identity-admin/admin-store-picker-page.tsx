import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { PeopleSearchBox } from "@/modules/people/people-frame";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import {
  filterStoreOptions,
  groupStoreOptionsByLetter,
  pickMyStoreOptions,
  splitStoreLabel,
  type StoreOption,
} from "./admin-store-picker-logic";

/**
 * 筛选弹层里的「选择分店」二级页：搜索（名称 / 编号）+「我的主分店」快捷项 + 按首字母分组的单选列表。
 * 点一家分店立即回传并由父级切回主页；搜索期间不显示快捷项和分组标题，直接给命中结果。
 */
export function AdminStorePickerPage({
  options,
  myStoreGuids,
  selected,
  onSelect,
}: {
  options: StoreOption[];
  /** 当前账号的主分店 GUID，用来生成「我的主分店」快捷项。 */
  myStoreGuids: readonly string[];
  /** 当前已选分店 GUID，空串 = 全部分店。 */
  selected: string;
  onSelect: (storeGuid: string) => void;
}) {
  const { t } = useAppTranslation("people");
  const [query, setQuery] = useState("");
  const searching = query.trim().length > 0;

  const mine = useMemo(() => pickMyStoreOptions(options, myStoreGuids), [myStoreGuids, options]);
  const matched = useMemo(() => filterStoreOptions(options, query), [options, query]);
  const groups = useMemo(() => groupStoreOptionsByLetter(matched), [matched]);

  return (
    <View style={styles.root}>
      <PeopleSearchBox value={query} onChangeText={setQuery} placeholder={t("sheet.storeSearch")} />

      {!searching ? (
        <StoreRow
          label={t("sheet.all")}
          caption={t("sheet.storeCount", { count: options.length })}
          selected={selected === ""}
          onPress={() => onSelect("")}
        />
      ) : null}

      {!searching && mine.length > 0 ? (
        <View style={styles.group}>
          <Text style={styles.groupTitle}>{t("sheet.myStores")}</Text>
          <View style={styles.chips}>
            {mine.map((option) => {
              const isSelected = option.value === selected;
              return (
                <Pressable
                  key={option.value}
                  accessibilityRole="radio"
                  accessibilityState={{ selected: isSelected, checked: isSelected }}
                  onPress={() => onSelect(option.value)}
                  style={[styles.chip, isSelected && styles.chipSelected]}
                >
                  <Text style={[styles.chipText, isSelected && styles.chipTextSelected]} numberOfLines={1}>{option.label}</Text>
                </Pressable>
              );
            })}
          </View>
        </View>
      ) : null}

      {matched.length === 0 ? <Text style={styles.empty}>{t("sheet.storeEmpty")}</Text> : null}

      {groups.map((group) => (
        <View key={group.letter} style={styles.group}>
          {!searching ? <Text style={styles.groupTitle}>{group.letter}</Text> : null}
          {group.items.map((option) => {
            const { name, code } = splitStoreLabel(option.label);
            return (
              <StoreRow
                key={option.value}
                label={name}
                caption={code}
                selected={option.value === selected}
                onPress={() => onSelect(option.value)}
              />
            );
          })}
        </View>
      ))}
    </View>
  );
}

function StoreRow({
  label,
  caption,
  selected,
  onPress,
}: {
  label: string;
  caption: string;
  selected: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ selected, checked: selected }}
      onPress={onPress}
      style={styles.row}
    >
      <Text style={[styles.rowLabel, selected && styles.rowLabelSelected]} numberOfLines={1}>{label}</Text>
      {caption ? <Text style={styles.rowCaption}>{caption}</Text> : null}
      {selected ? <Icon source="check" size={20} color={HB_COLORS.brand} /> : <View style={styles.checkSpacer} />}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { gap: 4 },
  group: { gap: 2 },
  groupTitle: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700", paddingTop: 10, paddingBottom: 2 },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 8, paddingVertical: 4 },
  chip: { minHeight: 36, paddingHorizontal: 14, borderRadius: 18, borderWidth: 1, borderColor: HB_COLORS.outline, justifyContent: "center", backgroundColor: HB_COLORS.white },
  chipSelected: { backgroundColor: "#EFF6FF", borderColor: "#B2CCFF" },
  chipText: { color: HB_COLORS.textPrimary, fontSize: 13.5 },
  chipTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  row: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  rowLabel: { flex: 1, color: HB_COLORS.textPrimary, fontSize: 15 },
  rowLabelSelected: { color: HB_COLORS.action, fontWeight: "700" },
  rowCaption: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  checkSpacer: { width: 20 },
  empty: { color: HB_COLORS.textSecondary, fontSize: 13.5, textAlign: "center", paddingVertical: 24 },
});
