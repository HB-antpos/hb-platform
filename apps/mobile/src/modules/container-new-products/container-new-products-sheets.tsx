import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { formatArrivalDateRange } from "./arrival-range";
import { matchesContainerSearch, PRODUCT_TYPE_FILTERS, WAREHOUSE_FILTERS, type ContainerOption, type ProductTypeFilter, type WarehouseFilter } from "./filters";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS, parsePageInput, type ContainerNewProductsPageSize } from "./pagination";

function SectionLabel({ children }: { children: string }) {
  return <Text style={styles.sectionLabel}>{children}</Text>;
}

interface FilterDraft { productType: ProductTypeFilter; warehouse: WarehouseFilter }

/** 商品类型 + 到仓状态：先改草稿，点底部按钮才生效；按钮和选项上的数量按草稿实时计算。 */
export function ProductFilterSheet({ visible, value, countFor, onApply, onDismiss }: {
  visible: boolean;
  value: FilterDraft;
  /** 按给定商品类型、到仓状态（其余筛选沿用当前值）统计品种数 */
  countFor: (draft: FilterDraft) => number;
  onApply: (draft: FilterDraft) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerNewProducts");
  const [draft, setDraft] = useState(value);
  // 每次打开都从当前生效的筛选开始，关掉不保存的草稿不会残留
  useEffect(() => { if (visible) setDraft(value); }, [visible, value]);
  const productTitles: Record<ProductTypeFilter, { title: string; hint?: string }> = {
    new: { title: t("filterSheet.newTitle"), hint: t("filterSheet.newHint") },
    existing: { title: t("filterSheet.existingTitle"), hint: t("filterSheet.existingHint") },
    all: { title: t("filterSheet.allTitle") },
  };
  const warehouseLabel = (filter: WarehouseFilter) => filter === "any"
    ? t("filterSheet.warehouseAny")
    : t(filter === "arrived" ? "filterSheet.warehouseArrived" : "filterSheet.warehouseTransit", { count: countFor({ ...draft, warehouse: filter }) });

  return <BusinessSheet
    visible={visible}
    title={t("filterSheet.title")}
    onDismiss={onDismiss}
    footer={<View style={styles.footerRow}>
      <Button mode="outlined" onPress={() => setDraft({ productType: "new", warehouse: "any" })} style={styles.footerSecondary}>{t("filterSheet.reset")}</Button>
      <Button mode="contained" onPress={() => onApply(draft)} style={styles.footerPrimary} contentStyle={styles.footerButtonContent}>{t("filterSheet.apply", { count: countFor(draft) })}</Button>
    </View>}
  >
    <SectionLabel>{t("filterSheet.productType")}</SectionLabel>
    <View style={styles.optionGroup}>
      {PRODUCT_TYPE_FILTERS.map((filter, index) => {
        const selected = draft.productType === filter;
        return <Pressable
          key={filter}
          accessibilityRole="radio"
          accessibilityState={{ checked: selected }}
          onPress={() => setDraft((current) => ({ ...current, productType: filter }))}
          style={[styles.optionRow, index > 0 && styles.optionDivider, selected && styles.optionSelected]}
        >
          <MaterialCommunityIcons name={selected ? "radiobox-marked" : "radiobox-blank"} size={22} color={selected ? HB_COLORS.action : HB_COLORS.textSecondary} />
          <View style={styles.optionText}>
            <Text style={styles.optionTitle}>{productTitles[filter].title}</Text>
            {productTitles[filter].hint ? <Text style={styles.optionHint}>{productTitles[filter].hint}</Text> : null}
          </View>
          <Text style={[styles.optionCount, selected && styles.optionCountSelected]}>{countFor({ ...draft, productType: filter }).toLocaleString("en-AU")}</Text>
        </Pressable>;
      })}
    </View>

    <SectionLabel>{t("filterSheet.warehouse")}</SectionLabel>
    <View style={styles.segment}>
      {WAREHOUSE_FILTERS.map((filter, index) => {
        const selected = draft.warehouse === filter;
        return <Pressable
          key={filter}
          accessibilityRole="radio"
          accessibilityState={{ checked: selected }}
          onPress={() => setDraft((current) => ({ ...current, warehouse: filter }))}
          style={[styles.segmentButton, index > 0 && styles.segmentDivider, selected && styles.segmentSelected]}
        >
          <Text style={[styles.segmentText, filter === "arrived" && styles.successText, filter === "transit" && styles.warningText, selected && styles.segmentTextSelected]} numberOfLines={1}>{warehouseLabel(filter)}</Text>
        </Pressable>;
      })}
    </View>
    <Text style={styles.hint}>{t("filterSheet.warehouseHint")}</Text>
  </BusinessSheet>;
}

/** 货柜多选：按已到仓 / 在途分组，支持搜柜号；不选 = 全部货柜。 */
export function ContainerFilterSheet({ visible, options, selected, onApply, onDismiss }: {
  visible: boolean;
  options: readonly ContainerOption[];
  selected: readonly string[];
  onApply: (containerCodes: string[]) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerNewProducts");
  const [draft, setDraft] = useState<string[]>([]);
  const [keyword, setKeyword] = useState("");
  useEffect(() => {
    if (!visible) return;
    setDraft([...selected]);
    setKeyword("");
  }, [visible, selected]);
  const draftSet = useMemo(() => new Set(draft.map((code) => code.toUpperCase())), [draft]);
  const visibleOptions = useMemo(() => options.filter((option) => matchesContainerSearch(option, keyword)), [options, keyword]);
  const groups = [
    { key: "arrived", title: t("containerSheet.arrivedGroup", { count: options.filter((option) => option.basis === "actual").length }), items: visibleOptions.filter((option) => option.basis === "actual") },
    { key: "transit", title: t("containerSheet.transitGroup", { count: options.filter((option) => option.basis === "estimated").length }), items: visibleOptions.filter((option) => option.basis === "estimated") },
  ];
  const toggle = (code: string) => setDraft((current) => (draftSet.has(code.toUpperCase())
    ? current.filter((item) => item.toUpperCase() !== code.toUpperCase())
    : [...current, code]));

  return <BusinessSheet
    visible={visible}
    title={t("containerSheet.title")}
    subtitle={t("containerSheet.subtitle", { count: options.length })}
    onDismiss={onDismiss}
    footer={<View style={styles.footerRow}>
      <Button mode="outlined" onPress={() => onApply([])} style={styles.footerSecondary}>{t("containerSheet.allContainers")}</Button>
      <Button mode="contained" onPress={() => onApply(draft)} style={styles.footerPrimary} contentStyle={styles.footerButtonContent}>
        {draft.length ? t("containerSheet.apply", { count: draft.length }) : t("containerSheet.applyAll")}
      </Button>
    </View>}
  >
    <View style={styles.searchRow}>
      <View style={styles.searchBox}>
        <MaterialCommunityIcons name="magnify" size={20} color={HB_COLORS.textSecondary} />
        <TextInput
          value={keyword}
          onChangeText={setKeyword}
          placeholder={t("containerSheet.search")}
          placeholderTextColor={HB_COLORS.textSecondary}
          accessibilityLabel={t("containerSheet.search")}
          autoCapitalize="characters"
          autoCorrect={false}
          style={styles.searchInput}
        />
      </View>
      <Button compact disabled={!draft.length} onPress={() => setDraft([])}>{t("containerSheet.clear")}</Button>
    </View>
    {visibleOptions.length === 0 ? <Text style={styles.hint}>{t("containerSheet.noMatch")}</Text> : null}
    {groups.map((group) => group.items.length ? <View key={group.key} style={styles.containerGroup}>
      <View style={styles.groupHeader}>
        <MaterialCommunityIcons name={group.key === "arrived" ? "check-circle-outline" : "truck-outline"} size={16} color={group.key === "arrived" ? HB_COLORS.success : HB_COLORS.warning} />
        <Text style={[styles.groupTitle, group.key === "arrived" ? styles.successText : styles.warningText]}>{group.title}</Text>
      </View>
      {group.items.map((option) => {
        const checked = draftSet.has(option.containerCode.toUpperCase());
        const date = formatArrivalDateRange(option.estimatedStoreArrivalDate, option.estimatedStoreArrivalDateEnd);
        const range = date.end ? `${date.start} – ${date.end}` : date.start;
        return <Pressable
          key={option.containerCode}
          accessibilityRole="checkbox"
          accessibilityState={{ checked }}
          onPress={() => toggle(option.containerCode)}
          style={[styles.containerRow, checked && styles.optionSelected]}
        >
          <MaterialCommunityIcons name={checked ? "checkbox-marked" : "checkbox-blank-outline"} size={24} color={checked ? HB_COLORS.action : HB_COLORS.textSecondary} />
          <View style={styles.optionText}>
            <Text style={styles.optionTitle} numberOfLines={1}>{option.label}</Text>
            <Text style={styles.optionHint}>{t(option.basis === "actual" ? "containerSheet.storeRange" : "containerSheet.storeRangeEstimated", { range })}</Text>
          </View>
          <View style={styles.containerCounts}>
            <Text style={styles.newKinds}>{t("containerSheet.newKinds", { count: option.newKinds })}</Text>
            <Text style={styles.optionHint}>{t("containerSheet.existingKinds", { count: option.existingKinds })}</Text>
          </View>
        </Pressable>;
      })}
    </View> : null)}
  </BusinessSheet>;
}

/** 跳页 + 每页条数：输入页码或点页码格子跳转；改每页条数由页面回到第 1 页并记住选择。 */
export function PageSheet({ visible, page, pageCount, total, pageSize, onJump, onPageSizeChange, onDismiss }: {
  visible: boolean;
  page: number;
  pageCount: number;
  total: number;
  pageSize: ContainerNewProductsPageSize;
  onJump: (page: number) => void;
  onPageSizeChange: (pageSize: ContainerNewProductsPageSize) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerNewProducts");
  const [input, setInput] = useState("");
  useEffect(() => { if (visible) setInput(String(page)); }, [visible, page]);
  const target = parsePageInput(input, pageCount);
  // 页数不多时给出页码格子一键跳转；页数很多时只保留输入框，避免面板过长
  const pageButtons = pageCount <= 40 ? Array.from({ length: pageCount }, (_, index) => index + 1) : [];

  return <BusinessSheet visible={visible} title={t("pageSheet.title")} subtitle={t("pageSheet.subtitle", { total: total.toLocaleString("en-AU"), page, pageCount })} onDismiss={onDismiss}>
    <SectionLabel>{t("pageSheet.goTo")}</SectionLabel>
    <View style={styles.gotoRow}>
      <View style={styles.gotoBox}>
        <TextInput
          value={input}
          onChangeText={(value) => setInput(value.replace(/[^\d]/g, ""))}
          keyboardType="number-pad"
          returnKeyType="go"
          selectTextOnFocus
          accessibilityLabel={t("pageSheet.goTo")}
          onSubmitEditing={() => { if (target) onJump(target); }}
          style={styles.gotoInput}
        />
        <Text style={styles.gotoOf}>{t("pageSheet.of", { pageCount })}</Text>
      </View>
      <Button mode="contained" disabled={!target} onPress={() => { if (target) onJump(target); }} contentStyle={styles.gotoButtonContent}>{t("pageSheet.go")}</Button>
    </View>
    {pageButtons.length > 1 ? <View style={styles.pageGrid}>
      {pageButtons.map((number) => <Pressable
        key={number}
        accessibilityRole="button"
        accessibilityState={{ selected: number === page }}
        onPress={() => onJump(number)}
        style={[styles.pageCell, number === page && styles.pageCellCurrent]}
      >
        <Text style={[styles.pageCellText, number === page && styles.pageCellTextCurrent]}>{number}</Text>
      </Pressable>)}
    </View> : null}

    <SectionLabel>{t("pageSheet.pageSize")}</SectionLabel>
    <View style={styles.segment}>
      {CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS.map((option, index) => {
        const selected = option === pageSize;
        return <Pressable
          key={option}
          accessibilityRole="radio"
          accessibilityState={{ checked: selected }}
          onPress={() => onPageSizeChange(option)}
          style={[styles.segmentButton, index > 0 && styles.segmentDivider, selected && styles.segmentSelected]}
        >
          <Text style={[styles.segmentText, selected && styles.segmentTextSelected]}>{option}</Text>
        </Pressable>;
      })}
    </View>
    <Text style={styles.hint}>{t("pageSheet.pageSizeHint")}</Text>
  </BusinessSheet>;
}

const styles = StyleSheet.create({
  sectionLabel: { color: HB_COLORS.textSecondary, fontSize: 13, fontWeight: "600", letterSpacing: 0.4, textTransform: "uppercase", marginTop: HB_SPACING.xxs },
  optionGroup: { borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  optionRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.sm, paddingVertical: 10 },
  optionDivider: { borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  optionSelected: { backgroundColor: "#F5F9FF" },
  optionText: { flex: 1, minWidth: 0 },
  optionTitle: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "600" },
  optionHint: { color: HB_COLORS.textSecondary, fontSize: 13 },
  optionCount: { color: HB_COLORS.textSecondary, fontSize: 15 },
  optionCountSelected: { color: HB_COLORS.action, fontWeight: "700" },
  segment: { flexDirection: "row", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  segmentButton: { flex: 1, minHeight: 46, alignItems: "center", justifyContent: "center", paddingHorizontal: 4, backgroundColor: HB_COLORS.white },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outline },
  segmentSelected: { backgroundColor: HB_COLORS.action },
  segmentText: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  segmentTextSelected: { color: HB_COLORS.white },
  successText: { color: HB_COLORS.success },
  warningText: { color: HB_COLORS.warning },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12 },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerSecondary: { justifyContent: "center" },
  footerPrimary: { flex: 1 },
  footerButtonContent: { minHeight: 46 },
  searchRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs },
  searchBox: { flex: 1, height: 44, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.sm, borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.surface },
  searchInput: { flex: 1, minWidth: 0, fontSize: 15, color: HB_COLORS.textPrimary, paddingVertical: 0 },
  containerGroup: { gap: 2 },
  groupHeader: { flexDirection: "row", alignItems: "center", gap: 6, paddingTop: HB_SPACING.xs, paddingBottom: 2 },
  groupTitle: { fontSize: 13, fontWeight: "700" },
  containerRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.xs, paddingVertical: HB_SPACING.xs, borderRadius: HB_RADIUS.control },
  containerCounts: { alignItems: "flex-end" },
  newKinds: { color: HB_COLORS.action, fontSize: 14, fontWeight: "700" },
  gotoRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  gotoBox: { flex: 1, height: 50, flexDirection: "row", alignItems: "center", gap: 6, paddingHorizontal: HB_SPACING.sm, borderWidth: 2, borderColor: HB_COLORS.action, borderRadius: HB_RADIUS.surface },
  gotoInput: { flex: 1, minWidth: 0, fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary, paddingVertical: 0 },
  gotoOf: { color: HB_COLORS.textSecondary, fontSize: 16 },
  gotoButtonContent: { minHeight: 50, paddingHorizontal: HB_SPACING.xs },
  pageGrid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  pageCell: { width: 56, height: 44, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.white },
  pageCellCurrent: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  pageCellText: { color: HB_COLORS.textPrimary, fontSize: 15 },
  pageCellTextCurrent: { color: HB_COLORS.white, fontWeight: "700" },
});
