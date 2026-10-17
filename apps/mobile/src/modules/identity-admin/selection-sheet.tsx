import { useState } from "react";
import { FlatList, Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { HB_COLORS } from "@/shared/theme/tokens";
import { AdminBottomSheet } from "./admin-bottom-sheet";
import { AdminEmpty, SearchField } from "./ui";
import { useIdentityUserCopy } from "./user-hooks";

/**
 * 单选弹层（筛选分店 / 角色、每页条数、新建权限的分类等）。
 *
 * 外观已统一为底部弹层（原来是居中弹窗，和别处的底部弹层两种并存）。
 * 导出名与必选 props 不变，其他模块只读复用；新增的 props 都是可选的。
 */
export function IdentitySelectionSheet({ title, value, options, onSelect, onDismiss, subtitle, searchable = true }: {
  title: string;
  value?: string;
  options: { value: string; label: string }[];
  onSelect: (value: string) => void;
  onDismiss: () => void;
  /** 标题下的一行说明（可选） */
  subtitle?: string;
  /** 选项很少（如每页条数）时可以关掉搜索框，默认保留 */
  searchable?: boolean;
}) {
  const c = useIdentityUserCopy();
  const [search, setSearch] = useState("");
  const keyword = search.trim().toLowerCase();
  const filtered = keyword ? options.filter((option) => option.label.toLowerCase().includes(keyword)) : options;
  return (
    <AdminBottomSheet
      title={title}
      subtitle={subtitle}
      closeLabel={c.close}
      onDismiss={onDismiss}
      scroll={false}
      header={searchable ? <SearchField value={search} onChange={setSearch} placeholder={c.filterSearch} /> : undefined}
    >
      <FlatList
        style={styles.list}
        data={filtered}
        keyExtractor={(option) => option.value}
        keyboardShouldPersistTaps="handled"
        ListEmptyComponent={<AdminEmpty text={c.noOptions} />}
        renderItem={({ item, index }) => {
          const selected = value === item.value;
          return (
            <Pressable
              accessibilityRole="radio"
              accessibilityState={{ checked: selected }}
              onPress={() => onSelect(item.value)}
              style={[styles.row, index > 0 && styles.divider]}
            >
              <Text style={[styles.label, selected && styles.labelSelected]} numberOfLines={2}>{item.label}</Text>
              {selected ? <Icon source="check" size={20} color={HB_COLORS.brand} /> : <View style={styles.checkSlot} />}
            </Pressable>
          );
        }}
      />
    </AdminBottomSheet>
  );
}

const styles = StyleSheet.create({
  list: { flexGrow: 0 },
  row: { alignItems: "center", flexDirection: "row", gap: 12, minHeight: 52, paddingHorizontal: 16, paddingVertical: 8 },
  divider: { borderTopColor: HB_COLORS.outlineMuted, borderTopWidth: StyleSheet.hairlineWidth },
  label: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 15 },
  labelSelected: { color: HB_COLORS.action, fontWeight: "700" },
  checkSlot: { height: 20, width: 20 },
});
