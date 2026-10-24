import { useEffect, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Dialog, Icon, IconButton, Portal, Searchbar, Snackbar, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { OptionPickerSheet } from "@/components/ui/OptionPickerSheet";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchPricingStrategy, updatePricingStrategy } from "./api";
import { buildToggledPayload, countActiveFilters, createDefaultListFilters, PRICING_LIST_PAGE_SIZE } from "./logic";
import { QuickTryCard } from "./QuickTryCard";
import { PRICING_COLORS, PricingScreenMessage, useApiErrorText, usePricingGuard } from "./shared";
import { StrategyCard } from "./StrategyCard";
import type { PricingListFilters, PricingStrategyListItem } from "./types";
import { pricingStrategyKeys, usePricingScopeOptions, usePricingStrategyList } from "./use-pricing-strategies";

export const PRICING_EDIT_PATH = "/(shell)/pricing-strategies/edit" as const;

export function PricingStrategiesScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const { blocked, canEdit } = usePricingGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <PricingScreenMessage message={blocked} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的筛选与结果留在屏幕上。
  return <PricingStrategiesContent key={userGuid} canEdit={canEdit} onBack={goBack} />;
}

function PricingStrategiesContent({ canEdit, onBack }: { canEdit: boolean; onBack: () => void }) {
  const { t } = useAppTranslation("pricingStrategies");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const errorText = useApiErrorText();
  const [filters, setFilters] = useState<PricingListFilters>(createDefaultListFilters);
  const [keywordDraft, setKeywordDraft] = useState("");
  const [picker, setPicker] = useState<"store" | "supplier" | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [pendingToggle, setPendingToggle] = useState<{ item: PricingStrategyListItem; next: boolean } | null>(null);
  const [togglingId, setTogglingId] = useState<string | null>(null);
  const [snack, setSnack] = useState("");
  const query = usePricingStrategyList(filters);
  const { stores, suppliers, storeNames, supplierNames } = usePricingScopeOptions();

  // 搜索框防抖 400ms，避免每敲一个字发一次请求。
  useEffect(() => {
    const timer = setTimeout(() => setFilters((current) => (current.keyword === keywordDraft ? current : { ...current, keyword: keywordDraft })), 400);
    return () => clearTimeout(timer);
  }, [keywordDraft]);

  const openEdit = (id?: string) => router.push(id ? { pathname: PRICING_EDIT_PATH, params: { id } } : { pathname: PRICING_EDIT_PATH });

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await query.refetch();
    } finally {
      setRefreshing(false);
    }
  };

  // 列表上直接启停：取回完整策略、只改 isEnabled 再整体保存（与 Web 一致，后端按同一套规则校验）。
  const confirmToggle = async () => {
    const pending = pendingToggle;
    setPendingToggle(null);
    if (!pending) return;
    setTogglingId(pending.item.id);
    try {
      const detail = await fetchPricingStrategy(pending.item.id);
      await updatePricingStrategy(pending.item.id, buildToggledPayload(detail, pending.next));
      setSnack(t("toggle.success"));
      await queryClient.invalidateQueries({ queryKey: pricingStrategyKeys.all });
    } catch (cause) {
      setSnack(errorText(cause, "toggle.failed"));
    } finally {
      setTogglingId(null);
    }
  };

  const items = query.data?.items ?? [];
  const total = query.data?.total ?? 0;
  const filterCount = countActiveFilters(filters);
  const storeLabel = filters.storeCode ? (storeNames.get(filters.storeCode) ?? filters.storeCode) : t("list.allStores");
  const supplierLabel = filters.supplierCode ? (supplierNames.get(filters.supplierCode) ?? filters.supplierCode) : t("list.allSuppliers");

  const header = (
    <View>
      <QuickTryCard stores={stores} suppliers={suppliers} onOpenStrategy={(id) => openEdit(id)} />
      <View style={styles.searchRow}>
        <Searchbar
          value={keywordDraft}
          onChangeText={setKeywordDraft}
          placeholder={t("list.searchPlaceholder")}
          style={styles.search}
          inputStyle={styles.searchInput}
        />
      </View>
      <View style={styles.chips}>
        <FilterChip icon="storefront-outline" label={storeLabel} active={Boolean(filters.storeCode)} onPress={() => setPicker("store")} a11y={t("list.filterStore")} />
        <FilterChip icon="truck-outline" label={supplierLabel} active={Boolean(filters.supplierCode)} onPress={() => setPicker("supplier")} a11y={t("list.filterSupplier")} />
        {filterCount > 0 ? (
          <Pressable accessibilityRole="button" onPress={() => setFilters((current) => ({ ...current, storeCode: null, supplierCode: null }))} hitSlop={6}>
            <Text style={styles.clear}>{t("actions.resetFilters")}</Text>
          </Pressable>
        ) : null}
      </View>
      {query.data ? (
        <Text style={styles.count}>
          {t("list.count", { count: total })}
          {" · "}
          {t("list.matchOrder")}
        </Text>
      ) : null}
      {query.isRefetchError ? (
        <Pressable accessibilityRole="button" onPress={() => void query.refetch()}>
          <Text style={styles.refreshError}>{t("list.refreshFailed")}</Text>
        </Pressable>
      ) : null}
    </View>
  );

  let empty;
  if (query.isPending) {
    empty = (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={styles.stateText}>{t("list.loading")}</Text>
      </View>
    );
  } else if (query.isError) {
    empty = (
      <View style={styles.state}>
        <EmptyState title={t("list.loadFailed")} description={errorText(query.error, "list.loadFailed")} actionLabel={t("actions.retry")} onAction={() => void query.refetch()} />
      </View>
    );
  } else {
    empty = (
      <View style={styles.state}>
        <EmptyState
          title={t("list.emptyTitle")}
          description={t("list.emptyDescription")}
          actionLabel={filterCount > 0 || filters.keyword ? t("actions.resetFilters") : undefined}
          onAction={() => {
            setKeywordDraft("");
            setFilters(createDefaultListFilters());
          }}
        />
      </View>
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={onBack} />
        <Text variant="titleMedium" style={styles.title} numberOfLines={1}>
          {t("title")}
        </Text>
        {canEdit ? (
          <Button mode="contained" icon="plus" compact onPress={() => openEdit()} style={styles.createButton}>
            {t("actions.create")}
          </Button>
        ) : null}
      </View>
      {!canEdit ? <Text style={styles.readOnly}>{t("messages.readOnly")}</Text> : null}
      <FlatList
        data={items}
        keyExtractor={(item) => item.id}
        renderItem={({ item }) => (
          <StrategyCard
            item={item}
            storeNames={storeNames}
            supplierNames={supplierNames}
            canEdit={canEdit}
            toggling={togglingId === item.id}
            onPress={() => openEdit(item.id)}
            onToggle={(next) => setPendingToggle({ item, next })}
          />
        )}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        ListFooterComponent={
          <View style={{ paddingBottom: HB_SPACING.lg + insets.bottom }}>
            {total > items.length && items.length >= PRICING_LIST_PAGE_SIZE ? <Text style={styles.footer}>{t("list.truncated", { shown: items.length })}</Text> : null}
          </View>
        }
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        style={styles.list}
      />
      <OptionPickerSheet
        visible={picker !== null}
        title={picker === "store" ? t("list.filterStore") : t("list.filterSupplier")}
        cancelLabel={t("actions.cancel")}
        options={
          picker === "store"
            ? [{ value: "", label: t("list.allStores") }, ...stores.map((store) => ({ value: store.code, label: store.name }))]
            : [{ value: "", label: t("list.allSuppliers") }, ...suppliers.map((supplier) => ({ value: supplier.code, label: supplier.name }))]
        }
        selectedValue={(picker === "store" ? filters.storeCode : filters.supplierCode) ?? ""}
        searchable
        onSelect={(value) => {
          setFilters((current) => (picker === "store" ? { ...current, storeCode: value || null } : { ...current, supplierCode: value || null }));
          setPicker(null);
        }}
        onDismiss={() => setPicker(null)}
      />
      <Portal>
        <Dialog visible={pendingToggle !== null} onDismiss={() => setPendingToggle(null)}>
          <Dialog.Title>{pendingToggle?.next ? t("toggle.enableTitle") : t("toggle.disableTitle")}</Dialog.Title>
          <Dialog.Content>
            <Text>{pendingToggle?.item.name}</Text>
            <Text style={styles.dialogHint}>{t("toggle.impact")}</Text>
          </Dialog.Content>
          <Dialog.Actions>
            <Button onPress={() => setPendingToggle(null)}>{t("actions.cancel")}</Button>
            <Button onPress={() => void confirmToggle()}>{t("actions.confirm")}</Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
      <Snackbar visible={Boolean(snack)} onDismiss={() => setSnack("")} duration={3000}>
        {snack}
      </Snackbar>
    </SafeAreaView>
  );
}

function FilterChip({ icon, label, active, onPress, a11y }: { icon: string; label: string; active: boolean; onPress: () => void; a11y: string }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={`${a11y}: ${label}`} onPress={onPress} style={[styles.chip, active ? styles.chipActive : null]}>
      <Icon source={icon} size={15} color={active ? HB_COLORS.brand : HB_COLORS.textSecondary} />
      <Text numberOfLines={1} style={[styles.chipText, active ? styles.chipTextActive : null]}>
        {label}
      </Text>
      <Icon source="chevron-down" size={15} color={active ? HB_COLORS.brand : HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center", paddingRight: HB_SPACING.sm },
  title: { flex: 1, fontWeight: "700", color: HB_COLORS.textPrimary },
  createButton: { borderRadius: HB_RADIUS.control },
  readOnly: { fontSize: 12, color: HB_COLORS.textSecondary, marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  list: { flex: 1 },
  searchRow: { marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  search: { height: 42, backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: HB_COLORS.outline, elevation: 0 },
  searchInput: { minHeight: 40, fontSize: 14 },
  chips: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  chip: {
    flexShrink: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    minHeight: 34,
    paddingHorizontal: 12,
    borderRadius: 17,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  chipActive: { borderColor: PRICING_COLORS.blueBorder, backgroundColor: PRICING_COLORS.blueBg },
  chipText: { flexShrink: 1, fontSize: 13, color: HB_COLORS.textPrimary },
  chipTextActive: { color: HB_COLORS.brand, fontWeight: "600" },
  clear: { fontSize: 13, color: HB_COLORS.action },
  count: { fontSize: 12, color: PRICING_COLORS.muted, marginHorizontal: HB_SPACING.md, marginBottom: HB_SPACING.xs },
  refreshError: { fontSize: 13, color: HB_COLORS.action, textAlign: "center", paddingVertical: HB_SPACING.xs },
  state: { paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.lg, alignItems: "center", gap: HB_SPACING.sm },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary },
  footer: { fontSize: 12, color: HB_COLORS.textSecondary, textAlign: "center", paddingTop: HB_SPACING.sm },
  dialogHint: { marginTop: HB_SPACING.xs, color: HB_COLORS.textSecondary },
});
