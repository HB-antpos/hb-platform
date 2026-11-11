import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Icon, Snackbar, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { OptionPickerSheet } from "@/components/ui/OptionPickerSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { withCode } from "@/shared/i18n/punctuation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { specialCategoriesApi } from "./api";
import { CategoryFormSheet } from "./CategoryFormSheet";
import { DraggableCategoryList } from "./DraggableCategoryList";
import { applyLocalOrder, isConflictError, MAX_CATEGORIES_PER_STORE, pickInitialStore } from "./logic";
import {
  ScreenHeader,
  SC_COLORS,
  SPECIAL_CATEGORIES_PATHS,
  SpecialCategoriesMessage,
  useApiErrorText,
  useSpecialCategoriesGuard,
} from "./shared";
import { specialCategoryKeys, useCategoryOverview, useManagedStores } from "./use-special-product-categories";
import { UNCATEGORIZED_ID, type CategoryOverview, type SpecialCategory } from "./types";

/** 保存顺序前的防抖：连续点上移 / 下移只提交最后一次。 */
const REORDER_DEBOUNCE_MS = 400;

export function CategoryListScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const { blocked } = useSpecialCategoriesGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <SpecialCategoriesMessage message={blocked} onBack={goBack} />;
  // 账号变化时重建页面状态，避免把上一个账号的分店与分类留在屏幕上。
  return <CategoryListContent key={userGuid} onBack={goBack} />;
}

function CategoryListContent({ onBack }: { onBack: () => void }) {
  const { t, language } = useAppTranslation("specialProductCategories");
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? null);
  const errorText = useApiErrorText();
  const { stores, loading: storesLoading, preferredStoreCode } = useManagedStores();

  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [storePickerOpen, setStorePickerOpen] = useState(false);
  const [formOpen, setFormOpen] = useState(false);
  const [reorderModeRaw, setReorderMode] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [localOrder, setLocalOrder] = useState<string[] | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [snack, setSnack] = useState("");

  // 默认分店：沿用全局已选分店（在管理范围内），否则第一家；当前选择不再可管理时重新挑选。
  useEffect(() => {
    if (storeCode && stores.some((store) => store.storeCode === storeCode)) return;
    setStoreCode(pickInitialStore(stores, preferredStoreCode));
  }, [preferredStoreCode, storeCode, stores]);

  const overviewQuery = useCategoryOverview(storeCode);
  const overview = overviewQuery.data;
  const categories = useMemo(() => applyLocalOrder(overview?.categories ?? [], localOrder), [overview?.categories, localOrder]);
  // 只剩 0 / 1 个分类时没有可排序的东西，自动退出排序模式。
  const reorderMode = reorderModeRaw && categories.length > 1;
  const storeName = stores.find((store) => store.storeCode === storeCode)?.storeName ?? storeCode ?? "";

  // ---------- 排序提交：乐观更新 + 防抖 + 串行；失败 / 冲突回滚到服务器顺序并提示 ----------
  const pendingOrderRef = useRef<string[] | null>(null);
  const inflightRef = useRef(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const storeCodeRef = useRef<string | null>(storeCode);
  storeCodeRef.current = storeCode;

  const flushOrder = useCallback(async () => {
    if (inflightRef.current) return; // 正在提交：循环里会接着提交最新顺序
    const targetStore = storeCodeRef.current;
    if (!targetStore) return;
    inflightRef.current = true;
    try {
      while (pendingOrderRef.current && storeCodeRef.current === targetStore) {
        const submitted = pendingOrderRef.current;
        const updated = await specialCategoriesApi.reorderCategories(targetStore, submitted);
        queryClient.setQueryData<CategoryOverview>(specialCategoryKeys.overview(userGuid, targetStore), (old) => (old ? { ...old, categories: updated } : old));
        // 期间没有新的改动才算同步完成；否则下一轮提交最新顺序。
        if (pendingOrderRef.current === submitted) {
          pendingOrderRef.current = null;
          setLocalOrder(null);
        }
      }
    } catch (cause) {
      pendingOrderRef.current = null;
      setLocalOrder(null);
      setSnack(isConflictError(cause) ? t("errors.orderConflict") : errorText(cause, "list.reorderFailed"));
      void queryClient.invalidateQueries({ queryKey: specialCategoryKeys.all });
    } finally {
      inflightRef.current = false;
    }
  }, [errorText, queryClient, t, userGuid]);

  const handleReorder = useCallback(
    (nextIds: string[]) => {
      pendingOrderRef.current = nextIds;
      setLocalOrder(nextIds);
      if (timerRef.current) clearTimeout(timerRef.current);
      timerRef.current = setTimeout(() => {
        timerRef.current = null;
        void flushOrder();
      }, REORDER_DEBOUNCE_MS);
    },
    [flushOrder],
  );

  // 离开页面时若还有没发出的顺序，立刻发出，不丢用户的调整。
  const flushOrderRef = useRef(flushOrder);
  flushOrderRef.current = flushOrder;
  useEffect(
    () => () => {
      if (timerRef.current) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
        void flushOrderRef.current();
      }
    },
    [],
  );

  const changeStore = (code: string) => {
    setStorePickerOpen(false);
    if (code === storeCode) return;
    if (timerRef.current) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
      void flushOrder(); // 先把上一家店没发出的顺序提交掉
    }
    pendingOrderRef.current = null;
    setLocalOrder(null);
    setReorderMode(false);
    setStoreCode(code);
  };

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await overviewQuery.refetch();
    } finally {
      setRefreshing(false);
    }
  };

  const openCategory = (category: SpecialCategory) => {
    if (!storeCode) return;
    router.push({ pathname: SPECIAL_CATEGORIES_PATHS.category, params: { storeCode, categoryId: category.id, name: category.name } });
  };
  const openUncategorized = () => {
    if (!storeCode) return;
    router.push({ pathname: SPECIAL_CATEGORIES_PATHS.category, params: { storeCode, categoryId: UNCATEGORIZED_ID } });
  };
  const openSync = () => {
    if (!storeCode) return;
    router.push({ pathname: SPECIAL_CATEGORIES_PATHS.sync, params: { storeCode } });
  };

  const createCategory = async (input: { name: string; color: string | null }) => {
    if (!storeCode) return;
    await specialCategoriesApi.createCategory({ storeCode, ...input });
    setFormOpen(false);
    setSnack(t("form.created", { name: input.name }));
    await queryClient.invalidateQueries({ queryKey: specialCategoryKeys.all });
  };

  const storeLabel = storeName ? withCode(storeName, storeCode ?? "", language) : t("list.noStore");
  const busy = storesLoading || (Boolean(storeCode) && overviewQuery.isPending);

  let body;
  if (!storeCode && !storesLoading) {
    body = <EmptyState title={t("list.noStoreTitle")} description={t("list.noStoreDescription")} />;
  } else if (busy) {
    body = (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={styles.stateText}>{t("list.loading")}</Text>
      </View>
    );
  } else if (overviewQuery.isError && !overview) {
    body = (
      <EmptyState
        title={t("list.loadFailed")}
        description={errorText(overviewQuery.error, "list.loadFailed")}
        actionLabel={t("actions.retry")}
        onAction={() => void overviewQuery.refetch()}
      />
    );
  } else {
    body = (
      <>
        <View style={styles.stats}>
          <Stat value={overview?.totalCount ?? 0} label={t("list.statProducts")} />
          <View style={styles.statDivider} />
          <Stat value={categories.length} label={t("list.statCategories")} />
          <View style={styles.statDivider} />
          <Stat value={overview?.uncategorizedCount ?? 0} label={t("list.statUncategorized")} accent />
        </View>

        <Text style={styles.sectionLabel}>{t(reorderMode ? "list.sectionReorderHint" : "list.sectionHint")}</Text>
        {categories.length > 0 ? (
          <DraggableCategoryList
            categories={categories}
            reorderMode={reorderMode}
            onOpen={openCategory}
            onReorder={handleReorder}
            onDraggingChange={setDragging}
          />
        ) : (
          <View style={styles.emptyCard}>
            <Text style={styles.emptyTitle}>{t("list.emptyTitle")}</Text>
            <Text style={styles.emptyText}>{t("list.emptyDescription")}</Text>
          </View>
        )}

        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("list.uncategorizedRow", { count: overview?.uncategorizedCount ?? 0 })}
          onPress={openUncategorized}
          disabled={reorderMode}
          style={styles.uncategorizedRow}
        >
          <Icon source="help-circle-outline" size={20} color={HB_COLORS.textSecondary} />
          <Text style={styles.uncategorizedName}>{t("list.uncategorized")}</Text>
          <Text style={styles.uncategorizedCount}>{overview?.uncategorizedCount ?? 0}</Text>
          <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
        </Pressable>
        <Text style={styles.footnote}>{t("list.uncategorizedHint")}</Text>
      </>
    );
  }

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <ScreenHeader
        title={t("title")}
        onBack={onBack}
        right={
          categories.length > 1 ? (
            <Button compact mode="text" onPress={() => setReorderMode((value) => !value)} accessibilityLabel={t(reorderMode ? "list.finishReorder" : "list.startReorder")}>
              {t(reorderMode ? "actions.done" : "list.startReorder")}
            </Button>
          ) : null
        }
      />
      <ScrollView
        style={styles.flex}
        contentContainerStyle={styles.content}
        scrollEnabled={!dragging}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
      >
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("list.storePicker", { store: storeLabel })}
          disabled={stores.length < 2}
          onPress={() => setStorePickerOpen(true)}
          style={styles.storeRow}
        >
          <Icon source="storefront-outline" size={20} color={HB_COLORS.textSecondary} />
          <Text style={styles.storeText} numberOfLines={1}>
            {storeLabel}
          </Text>
          {stores.length > 1 ? <Icon source="chevron-down" size={20} color={HB_COLORS.textSecondary} /> : null}
        </Pressable>
        {body}
      </ScrollView>

      {storeCode && !busy && !(overviewQuery.isError && !overview) ? (
        <View style={[BUSINESS_UI.footer, styles.footer, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
          <Button mode="outlined" icon="source-branch-sync" onPress={openSync} style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent}>
            {t("list.syncAction")}
          </Button>
          <Button mode="contained" icon="plus" onPress={() => (categories.length >= MAX_CATEGORIES_PER_STORE ? setSnack(t("list.limitReached", { max: MAX_CATEGORIES_PER_STORE })) : setFormOpen(true))} style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent}>
            {t("list.createAction")}
          </Button>
        </View>
      ) : null}

      <OptionPickerSheet
        visible={storePickerOpen}
        title={t("list.pickStoreTitle")}
        cancelLabel={t("actions.cancel")}
        options={stores.map((store) => ({ value: store.storeCode, label: withCode(store.storeName, store.storeCode, language) }))}
        selectedValue={storeCode}
        searchable={stores.length > 6}
        searchPlaceholder={t("list.searchStore")}
        onSelect={changeStore}
        onDismiss={() => setStorePickerOpen(false)}
      />
      <CategoryFormSheet
        visible={formOpen}
        existing={categories}
        onSubmit={createCategory}
        onDismiss={() => setFormOpen(false)}
        errorText={errorText}
      />
      <Snackbar visible={Boolean(snack)} onDismiss={() => setSnack("")} duration={3500}>
        {snack}
      </Snackbar>
    </SafeAreaView>
  );
}

function Stat({ value, label, accent = false }: { value: number; label: string; accent?: boolean }) {
  return (
    <View style={styles.stat}>
      <Text style={[styles.statValue, accent ? styles.statAccent : null]}>{value}</Text>
      <Text style={styles.statLabel}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  flex: { flex: 1 },
  content: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.lg, gap: HB_SPACING.sm },
  storeRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 52, paddingHorizontal: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.surface },
  storeText: { flex: 1, fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  stats: { flexDirection: "row", backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, paddingVertical: HB_SPACING.sm },
  stat: { flex: 1, alignItems: "center", gap: 2 },
  statDivider: { width: StyleSheet.hairlineWidth, backgroundColor: HB_COLORS.outlineMuted },
  statValue: { fontSize: 24, lineHeight: 30, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  statAccent: { color: SC_COLORS.uncategorized },
  statLabel: { fontSize: 12, color: HB_COLORS.textSecondary },
  sectionLabel: { fontSize: 12, color: HB_COLORS.textSecondary, marginTop: HB_SPACING.xs },
  emptyCard: { padding: HB_SPACING.md, gap: 4, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  emptyTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  emptyText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textSecondary },
  uncategorizedRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 52, paddingHorizontal: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  uncategorizedName: { flex: 1, fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  uncategorizedCount: { fontSize: 14, fontWeight: "700", color: SC_COLORS.uncategorized, fontVariant: ["tabular-nums"] },
  footnote: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  state: { paddingTop: HB_SPACING.xl, alignItems: "center", gap: HB_SPACING.sm },
  stateText: { fontSize: 13, color: HB_COLORS.textSecondary },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
});
