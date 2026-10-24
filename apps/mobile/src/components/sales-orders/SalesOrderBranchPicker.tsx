import { useEffect, useMemo, useState } from "react";
import { FlatList, Pressable, StyleSheet, View } from "react-native";
import { Button, Checkbox, Divider, Icon, Searchbar, Text } from "react-native-paper";
import { InsightSheet } from "@/components/warehouse-product-insights/InsightSheet";
import { normalizeSelectedBranchCodes } from "@/modules/sales-orders/logic";
import type { SalesOrderBranch } from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { listSeparator } from "@/shared/i18n/punctuation";
import { sortStoresByName } from "@/shared/utils/name-sort";

export interface SalesOrderBranchPickerProps {
  branches: SalesOrderBranch[];
  loading: boolean;
  /** 空数组表示不限分店。 */
  selected: string[];
  onChange: (codes: string[]) => void;
}

/** 分店下拉：外层是一个只读选择框，点开后是带搜索的多选列表；门店多时不再平铺 chip。 */
export function SalesOrderBranchPicker({
  branches,
  loading,
  selected,
  onChange,
}: SalesOrderBranchPickerProps) {
  const { t, language } = useAppTranslation("salesOrders");
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<string[]>(selected);
  const [keyword, setKeyword] = useState("");

  useEffect(() => {
    if (open) {
      setDraft(selected);
      setKeyword("");
    }
  }, [open, selected]);

  const availableCodes = useMemo(() => branches.map((branch) => branch.storeCode), [branches]);
  const selectedNames = useMemo(() => {
    const byCode = new Map(branches.map((branch) => [branch.storeCode, branch.storeName]));
    return selected.map((code) => byCode.get(code) ?? code);
  }, [branches, selected]);
  const filtered = useMemo(() => {
    // 分店多选列表统一按分店名字排序（接口原序不保证）。
    const sorted = sortStoresByName(branches, language);
    const needle = keyword.trim().toLowerCase();
    if (!needle) return sorted;
    return sorted.filter(
      (branch) =>
        branch.storeName.toLowerCase().includes(needle) ||
        branch.storeCode.toLowerCase().includes(needle),
    );
  }, [branches, keyword, language]);
  const allSelected = draft.length === 0;

  const summary = loading
    ? t("filterSheet.branchesLoading")
    : selected.length === 0
      ? t("filterSheet.allBranches", { count: branches.length })
      : selected.length <= 2
        ? selectedNames.join(listSeparator())
        : t("filterSheet.branchesSelected", { count: selected.length, first: selectedNames[0] });

  const toggle = (code: string) => {
    const current = allSelected ? [] : draft;
    const next = current.includes(code)
      ? current.filter((item) => item !== code)
      : [...current, code];
    setDraft(next);
  };

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("filterSheet.branches")}
        onPress={() => setOpen(true)}
        style={styles.field}
      >
        <Icon source="store-outline" size={18} color={HB_COLORS.action} />
        <Text numberOfLines={1} style={styles.fieldText}>
          {summary}
        </Text>
        <Icon source="chevron-down" size={20} color={HB_COLORS.textSecondary} />
      </Pressable>
      <InsightSheet
        visible={open}
        title={t("filterSheet.branchPickerTitle")}
        subtitle={t("filterSheet.branchPickerSubtitle", {
          selected: allSelected ? branches.length : draft.length,
          total: branches.length,
        })}
        closeLabel={t("actions.close")}
        onClose={() => setOpen(false)}
        heightRatio={0.8}
      >
        <View style={styles.searchWrap}>
          <Searchbar
            placeholder={t("filterSheet.branchSearch")}
            value={keyword}
            onChangeText={setKeyword}
            style={styles.search}
            inputStyle={styles.searchInput}
          />
        </View>
        <Pressable
          accessibilityRole="button"
          onPress={() => setDraft([])}
          style={styles.row}
        >
          <Checkbox.Android status={allSelected ? "checked" : "unchecked"} color={HB_COLORS.brand} />
          <Text style={[styles.rowText, allSelected ? styles.rowTextSelected : null]}>
            {t("filterSheet.allBranches", { count: branches.length })}
          </Text>
        </Pressable>
        <Divider />
        <FlatList
          data={filtered}
          keyExtractor={(branch) => branch.storeCode}
          keyboardShouldPersistTaps="handled"
          ItemSeparatorComponent={Divider}
          ListEmptyComponent={
            <Text style={styles.empty}>
              {loading ? t("filterSheet.branchesLoading") : t("filterSheet.branchesEmpty")}
            </Text>
          }
          renderItem={({ item }) => {
            const checked = !allSelected && draft.includes(item.storeCode);
            return (
              <Pressable
                accessibilityRole="checkbox"
                accessibilityState={{ checked }}
                onPress={() => toggle(item.storeCode)}
                style={styles.row}
              >
                <Checkbox.Android
                  status={checked ? "checked" : "unchecked"}
                  color={HB_COLORS.brand}
                />
                <View style={styles.rowBody}>
                  <Text numberOfLines={1} style={[styles.rowText, checked ? styles.rowTextSelected : null]}>
                    {item.storeName}
                  </Text>
                  <Text style={styles.rowMeta}>{item.storeCode}</Text>
                </View>
              </Pressable>
            );
          }}
        />
        <View style={styles.footer}>
          <Button mode="outlined" compact onPress={() => setDraft([])} style={styles.footerButton}>
            {t("filterSheet.branchClear")}
          </Button>
          <Button
            mode="contained"
            onPress={() => {
              onChange(normalizeSelectedBranchCodes(draft, availableCodes));
              setOpen(false);
            }}
            style={styles.footerApply}
          >
            {t("filterSheet.branchConfirm", { count: allSelected ? branches.length : draft.length })}
          </Button>
        </View>
      </InsightSheet>
    </>
  );
}

const styles = StyleSheet.create({
  field: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  fieldText: { flex: 1, fontSize: 14, color: HB_COLORS.textPrimary },
  searchWrap: { padding: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  search: { backgroundColor: HB_COLORS.surfaceMuted },
  searchInput: { fontSize: 14, minHeight: 0 },
  row: {
    flexDirection: "row",
    alignItems: "center",
    paddingRight: HB_SPACING.md,
    paddingLeft: HB_SPACING.xxs,
    minHeight: 48,
  },
  rowBody: { flex: 1, minWidth: 0 },
  rowText: { fontSize: 14, color: HB_COLORS.textPrimary },
  rowTextSelected: { color: HB_COLORS.action, fontWeight: "600" },
  rowMeta: { fontSize: 11, color: HB_COLORS.textSecondary },
  empty: { padding: HB_SPACING.lg, textAlign: "center", color: HB_COLORS.textSecondary },
  footer: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerButton: { flex: 1 },
  footerApply: { flex: 2 },
});
