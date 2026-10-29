import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import type { Store } from "@/modules/shop/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { sortStoresByName } from "@/shared/utils/name-sort";
import { filterStoreOptions, groupStoreOptionsByLetter, splitStoreLabel, type StoreOption } from "@/shared/utils/store-picker-logic";

/** 文案由调用方按自己的语言命名空间传入，避免公共组件绑定某个页面的 i18n。 */
export interface StoreMultiPickLabels {
  searchPlaceholder: string;
  emptyText: string;
  /** 「已选 N 家」之类，已格式化。 */
  selectedText: string;
  /** 传了就在最前面显示「全部分店」行（选中态 = 没选任何分店，点击 = onClear）。 */
  allRowLabel?: string;
  /** 「全部分店」行右侧的淡色补充，如「共 58 家」。 */
  allRowCaption?: string;
  /** 与 onSelectAll / onClear 配合：头部的「全选」「清空」按钮。 */
  selectAllLabel?: string;
  clearLabel?: string;
}

/**
 * 筛选弹层里的「选择分店」多选二级页：搜索（名称 / 编号）+ 按首字母分组的勾选列表。
 * 勾选逻辑全部交给调用方（各筛选有自己的联动，如换分店要清掉终端 / 员工），这里只负责展示与回调。
 */
export function StoreMultiPickPage({
  stores,
  selected,
  labels,
  onToggle,
  onClear,
  onSelectAll,
}: {
  stores: readonly Store[];
  /** 已选分店编号。 */
  selected: readonly string[];
  labels: StoreMultiPickLabels;
  onToggle: (storeCode: string) => void;
  onClear?: () => void;
  onSelectAll?: () => void;
}) {
  const [query, setQuery] = useState("");
  const options = useMemo<StoreOption[]>(
    () =>
      sortStoresByName(stores).map((store) => ({
        value: store.storeCode,
        label: store.storeName ? `${store.storeName} · ${store.storeCode}` : store.storeCode,
      })),
    [stores],
  );
  const groups = useMemo(() => groupStoreOptionsByLetter(filterStoreOptions(options, query)), [options, query]);
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  const searching = query.trim().length > 0;

  return (
    <View style={styles.root}>
      {stores.length > 6 ? (
        <TextInput
          mode="outlined"
          dense
          value={query}
          onChangeText={setQuery}
          placeholder={labels.searchPlaceholder}
          accessibilityLabel={labels.searchPlaceholder}
          left={<TextInput.Icon icon="magnify" />}
        />
      ) : null}

      {onSelectAll ? (
        <View style={styles.bulk}>
          <Text style={styles.bulkText}>{labels.selectedText}</Text>
          <View style={styles.bulkActions}>
            <Button compact onPress={onSelectAll}>{labels.selectAllLabel}</Button>
            <Button compact onPress={onClear}>{labels.clearLabel}</Button>
          </View>
        </View>
      ) : null}

      {labels.allRowLabel && !searching ? (
        <Row label={labels.allRowLabel} caption={labels.allRowCaption ?? ""} checked={selected.length === 0} onPress={() => onClear?.()} />
      ) : null}

      {groups.length === 0 ? <Text style={styles.empty}>{labels.emptyText}</Text> : null}

      {groups.map((group) => (
        <View key={group.letter}>
          {!searching ? <Text style={styles.groupTitle}>{group.letter}</Text> : null}
          {group.items.map((option) => {
            const { name, code } = splitStoreLabel(option.label);
            return (
              <Row
                key={option.value}
                label={name}
                caption={code}
                checked={selectedSet.has(option.value)}
                multi
                onPress={() => onToggle(option.value)}
              />
            );
          })}
        </View>
      ))}
    </View>
  );
}

function Row({ label, caption, checked, multi = false, onPress }: { label: string; caption: string; checked: boolean; multi?: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole={multi ? "checkbox" : "radio"}
      accessibilityState={{ checked, selected: checked }}
      onPress={onPress}
      style={styles.row}
    >
      <Text style={[styles.rowLabel, checked && styles.rowLabelOn]} numberOfLines={1}>{label}</Text>
      {caption ? <Text style={styles.rowCaption}>{caption}</Text> : null}
      {multi ? (
        <Icon source={checked ? "checkbox-marked" : "checkbox-blank-outline"} size={22} color={checked ? HB_COLORS.brand : HB_COLORS.outline} />
      ) : checked ? (
        <Icon source="check" size={20} color={HB_COLORS.brand} />
      ) : (
        <View style={styles.spacer} />
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { gap: 4 },
  bulk: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  bulkText: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  bulkActions: { flexDirection: "row", alignItems: "center" },
  groupTitle: { color: HB_COLORS.textSecondary, fontSize: 12, fontWeight: "700", paddingTop: 10, paddingBottom: 2 },
  row: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  rowLabel: { flex: 1, color: HB_COLORS.textPrimary, fontSize: 15 },
  rowLabelOn: { color: HB_COLORS.action, fontWeight: "700" },
  rowCaption: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  spacer: { width: 20 },
  empty: { color: HB_COLORS.textSecondary, fontSize: 13.5, textAlign: "center", paddingVertical: 24 },
});
