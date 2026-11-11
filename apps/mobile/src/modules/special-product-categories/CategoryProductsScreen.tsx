import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Icon, Searchbar, Snackbar, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { formatMoney } from "@/modules/price-updates/presentation";
import { useAuthStore } from "@/store/auth-store";
import { withCode } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { specialCategoriesApi } from "./api";
import { isConflictError } from "./logic";
import { AddProductsSheet } from "./AddProductsSheet";
import { CategoryFormSheet } from "./CategoryFormSheet";
import {
  ColorDot,
  ProductThumb,
  ScreenHeader,
  SC_COLORS,
  SpecialCategoriesMessage,
  useApiErrorText,
  useSpecialCategoriesGuard,
} from "./shared";
import { specialCategoryKeys, useCategoryOverview, useCategoryProducts, useManagedStores } from "./use-special-product-categories";
import { UNCATEGORIZED_ID, type CategoryProduct, type SpecialCategory } from "./types";

function firstParam(value: string | string[] | undefined) {
  return (Array.isArray(value) ? value[0] : value) ?? "";
}

export function CategoryProductsScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const params = useLocalSearchParams<{ storeCode?: string; categoryId?: string; name?: string }>();
  const { blocked } = useSpecialCategoriesGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/special-product-categories"));
  if (blocked) return <SpecialCategoriesMessage message={blocked} onBack={goBack} />;
  return (
    <CategoryProductsContent
      key={`${userGuid}:${firstParam(params.storeCode)}:${firstParam(params.categoryId)}`}
      storeCode={firstParam(params.storeCode)}
      categoryId={firstParam(params.categoryId)}
      initialName={firstParam(params.name)}
      onBack={goBack}
    />
  );
}

interface ContentProps {
  storeCode: string;
  categoryId: string;
  initialName: string;
  onBack: () => void;
}

function CategoryProductsContent({ storeCode, categoryId, initialName, onBack }: ContentProps) {
  const { t, language } = useAppTranslation("specialProductCategories");
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const errorText = useApiErrorText();
  const { stores } = useManagedStores();
  const isUncategorized = categoryId === UNCATEGORIZED_ID;

  const [keywordDraft, setKeywordDraft] = useState("");
  const [keyword, setKeyword] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [moveOpen, setMoveOpen] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [sheetError, setSheetError] = useState("");
  const [refreshing, setRefreshing] = useState(false);
  const [snack, setSnack] = useState("");

  // 搜索词防抖；搜索词变化后清掉勾选（避免对屏幕上已经看不到的商品误操作）。
  const keywordRef = useRef("");
  useEffect(() => {
    const timer = setTimeout(() => {
      if (keywordDraft === keywordRef.current) return;
      keywordRef.current = keywordDraft;
      setKeyword(keywordDraft);
      setSelected(new Set());
    }, 400);
    return () => clearTimeout(timer);
  }, [keywordDraft]);

  const overviewQuery = useCategoryOverview(storeCode);
  const categories = overviewQuery.data?.categories ?? [];
  const category: SpecialCategory | null = isUncategorized ? null : (categories.find((item) => item.id === categoryId) ?? null);
  // 分类已被删除（别处删的）：概览已加载但找不到。
  const missing = !isUncategorized && overviewQuery.isSuccess && !category;
  const title = isUncategorized ? t("list.uncategorized") : (category?.name ?? initialName);
  const storeName = stores.find((store) => store.storeCode === storeCode)?.storeName ?? storeCode;
  const storeLabel = withCode(storeName, storeCode, language);

  const productsQuery = useCategoryProducts(missing ? null : storeCode, categoryId, keyword);
  const items = useMemo(() => productsQuery.data?.pages.flatMap((page) => page.items) ?? [], [productsQuery.data]);
  const total = productsQuery.data?.pages[0]?.total ?? 0;
  const selectedCount = selected.size;
  const allSelected = items.length > 0 && items.every((item) => selected.has(item.productCode));

  const invalidateAll = () => queryClient.invalidateQueries({ queryKey: specialCategoryKeys.all });

  const toggle = (code: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(code)) next.delete(code);
      else next.add(code);
      return next;
    });

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([overviewQuery.refetch(), productsQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  const reportAssign = (result: { assignedCount: number; markedCount: number; skipped: unknown[] }, key: "moved" | "added") => {
    const skipped = result.skipped.length;
    setSnack(
      skipped > 0
        ? t(`result.${key}WithSkipped`, { count: result.assignedCount, skipped })
        : t(`result.${key}`, { count: result.assignedCount, marked: result.markedCount }),
    );
  };

  // 移到其他分类 / 未分类。
  const moveSelected = async (target: SpecialCategory | null) => {
    if (busy || selected.size === 0) return;
    setBusy(true);
    setSheetError("");
    try {
      const result = await specialCategoriesApi.assignProducts({
        storeCode,
        productCodes: [...selected],
        categoryId: target?.id ?? null,
        markAsSpecial: false,
      });
      setMoveOpen(false);
      setSelected(new Set());
      reportAssign(result, "moved");
      await invalidateAll();
    } catch (cause) {
      setSheetError(errorText(cause, "category.moveFailed"));
      // 别人刚改过（如同时首次归类同一商品）：重新加载当前列表。
      if (isConflictError(cause)) void invalidateAll();
    } finally {
      setBusy(false);
    }
  };

  const confirmUnmark = () => {
    if (busy || selected.size === 0) return;
    Alert.alert(t("category.unmarkTitle", { count: selected.size }), t("category.unmarkBody"), [
      { text: t("actions.cancel"), style: "cancel" },
      {
        text: t("category.unmarkAction"),
        style: "destructive",
        onPress: () => {
          setBusy(true);
          specialCategoriesApi
            .unmarkProducts(storeCode, [...selected])
            .then(async (result) => {
              setSelected(new Set());
              setSnack(t("category.unmarked", { count: result.unmarkedCount }));
              await invalidateAll();
            })
            .catch((cause) => {
              setSnack(errorText(cause, "category.unmarkFailed"));
              if (isConflictError(cause)) void invalidateAll();
            })
            .finally(() => setBusy(false));
        },
      },
    ]);
  };

  const addProducts = async (productCodes: string[]) => {
    if (busy || !category || productCodes.length === 0) return;
    setBusy(true);
    setSheetError("");
    try {
      // 一次调用：未标记的先标记为特殊商品，别的分类的移过来，追加到本分类末尾。
      const result = await specialCategoriesApi.assignProducts({ storeCode, productCodes, categoryId: category.id, markAsSpecial: true });
      setAddOpen(false);
      reportAssign(result, "added");
      await invalidateAll();
    } catch (cause) {
      setSheetError(errorText(cause, "add.submitFailed"));
      if (isConflictError(cause)) void invalidateAll();
    } finally {
      setBusy(false);
    }
  };

  const renameCategory = async (input: { name: string; color: string | null }) => {
    if (!category) return;
    await specialCategoriesApi.updateCategory(category.id, { storeCode, ...input });
    setEditOpen(false);
    setSnack(t("form.saved"));
    await invalidateAll();
  };

  const deleteCategory = async () => {
    if (!category) return;
    await specialCategoriesApi.deleteCategory(category.id, storeCode);
    setEditOpen(false);
    // 先返回再刷新：避免本页在分类已被删除、数据还没离开前闪出「分类已不存在」。
    onBack();
    void invalidateAll();
  };

  const moveTargets = categories.filter((item) => item.id !== categoryId);

  let empty;
  if (missing) {
    empty = <EmptyState title={t("category.missingTitle")} description={t("category.missingDescription")} actionLabel={t("actions.back")} onAction={onBack} />;
  } else if (productsQuery.isPending) {
    empty = (
      <View style={styles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
      </View>
    );
  } else if (productsQuery.isError) {
    empty = (
      <EmptyState
        title={t("category.loadFailed")}
        description={errorText(productsQuery.error, "category.loadFailed")}
        actionLabel={t("actions.retry")}
        onAction={() => void productsQuery.refetch()}
      />
    );
  } else {
    empty = (
      <EmptyState
        title={t(keyword ? "category.noMatchTitle" : isUncategorized ? "category.emptyUncategorizedTitle" : "category.emptyTitle")}
        description={t(keyword ? "category.noMatchDescription" : isUncategorized ? "category.emptyUncategorizedDescription" : "category.emptyDescription")}
      />
    );
  }

  const renderItem = ({ item }: { item: CategoryProduct }) => {
    const checked = selected.has(item.productCode);
    return (
      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{ checked }}
        onPress={() => toggle(item.productCode)}
        style={[styles.row, checked ? styles.rowChecked : null]}
      >
        <Icon source={checked ? "check-circle" : "checkbox-blank-circle-outline"} size={24} color={checked ? HB_COLORS.brand : HB_COLORS.outline} />
        <ProductThumb uri={item.productImage} name={item.displayName} />
        <View style={styles.rowText}>
          <Text style={styles.name} numberOfLines={2}>
            {item.displayName}
          </Text>
          <Text style={styles.code} numberOfLines={1}>
            {t("category.itemNumberLine", { itemNumber: item.itemNumber || "-", barcode: item.barcode || "-" })}
          </Text>
        </View>
        <Text style={styles.price}>{formatMoney(item.retailPrice)}</Text>
      </Pressable>
    );
  };

  const showBottomBar = !missing && !productsQuery.isPending && !productsQuery.isError;

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      <ScreenHeader
        title={title}
        onBack={onBack}
        right={
          category ? (
            <Button compact mode="text" onPress={() => setEditOpen(true)} accessibilityLabel={t("category.editAction")}>
              {t("category.editAction")}
            </Button>
          ) : null
        }
      />
      {!missing ? (
        <View style={styles.searchBox}>
          <Searchbar
            value={keywordDraft}
            onChangeText={setKeywordDraft}
            placeholder={t("category.searchPlaceholder")}
            style={styles.search}
            inputStyle={styles.searchInput}
          />
          <View style={styles.countRow}>
            <View style={styles.countTitle}>
              {category ? <ColorDot color={category.color} /> : null}
              <Text style={styles.countText}>
                {selectedCount > 0 ? t("category.countLineSelected", { total, selected: selectedCount }) : t("category.countLine", { total })}
              </Text>
            </View>
            {items.length > 0 ? (
              <Button
                compact
                mode="text"
                onPress={() => setSelected(allSelected ? new Set() : new Set(items.map((item) => item.productCode)))}
              >
                {t(allSelected ? "category.clearSelection" : "category.selectAll")}
              </Button>
            ) : null}
          </View>
        </View>
      ) : null}
      <FlatList
        data={missing ? [] : items}
        keyExtractor={(item) => item.productCode}
        renderItem={renderItem}
        ListEmptyComponent={empty}
        ListFooterComponent={productsQuery.isFetchingNextPage ? <ActivityIndicator style={styles.state} color={HB_COLORS.brand} /> : null}
        onEndReached={() => {
          if (productsQuery.hasNextPage && !productsQuery.isFetchingNextPage) void productsQuery.fetchNextPage();
        }}
        onEndReachedThreshold={0.4}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        style={styles.list}
        contentContainerStyle={items.length > 0 ? styles.listContent : undefined}
      />

      {showBottomBar && (selectedCount > 0 || category) ? (
        <View style={[BUSINESS_UI.footer, styles.footer, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
          {selectedCount > 0 ? (
            <>
              <Button
                mode="outlined"
                icon="tag-off-outline"
                textColor={HB_COLORS.danger}
                disabled={busy}
                onPress={confirmUnmark}
                style={[BUSINESS_UI.button, styles.footerButton, styles.dangerButton]}
                contentStyle={BUSINESS_UI.buttonContent}
              >
                {t("category.unmark")}
              </Button>
              <Button
                mode="contained"
                icon="folder-move-outline"
                disabled={busy}
                onPress={() => {
                  setSheetError("");
                  setMoveOpen(true);
                }}
                style={[BUSINESS_UI.button, styles.footerButton]}
                contentStyle={BUSINESS_UI.buttonContent}
              >
                {t("category.move")}
              </Button>
            </>
          ) : (
            <Button
              mode="contained"
              icon="plus"
              onPress={() => {
                setSheetError("");
                setAddOpen(true);
              }}
              style={[BUSINESS_UI.button, styles.footerButton]}
              contentStyle={BUSINESS_UI.buttonContent}
            >
              {t("category.addProducts")}
            </Button>
          )}
        </View>
      ) : null}

      <BusinessSheet
        visible={moveOpen}
        title={t("move.title", { count: selectedCount })}
        subtitle={t("move.subtitle")}
        onDismiss={() => setMoveOpen(false)}
        dismissable={!busy}
        footer={sheetError ? <Text style={styles.error}>{sheetError}</Text> : undefined}
      >
        {moveTargets.map((target) => (
          <MoveRow key={target.id} label={target.name} count={target.itemCount} color={target.color} disabled={busy} onPress={() => void moveSelected(target)} />
        ))}
        {!isUncategorized ? (
          <MoveRow label={t("list.uncategorized")} count={overviewQuery.data?.uncategorizedCount ?? 0} color={null} icon="help-circle-outline" disabled={busy} onPress={() => void moveSelected(null)} />
        ) : null}
        {moveTargets.length === 0 && isUncategorized ? <Text style={styles.moveEmpty}>{t("move.noCategories")}</Text> : null}
      </BusinessSheet>

      {category ? (
        <AddProductsSheet
          visible={addOpen}
          storeCode={storeCode}
          storeLabel={storeLabel}
          categoryId={category.id}
          categoryName={category.name}
          submitting={busy}
          errorMessage={sheetError}
          onSubmit={(codes) => void addProducts(codes)}
          onDismiss={() => setAddOpen(false)}
        />
      ) : null}
      <CategoryFormSheet
        visible={editOpen}
        category={category}
        existing={categories}
        onSubmit={renameCategory}
        onDelete={deleteCategory}
        onDismiss={() => setEditOpen(false)}
        errorText={errorText}
      />
      <Snackbar visible={Boolean(snack)} onDismiss={() => setSnack("")} duration={3500}>
        {snack}
      </Snackbar>
    </SafeAreaView>
  );
}

function MoveRow({
  label,
  count,
  color,
  icon,
  disabled,
  onPress,
}: {
  label: string;
  count: number;
  color: string | null;
  icon?: string;
  disabled: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable accessibilityRole="button" disabled={disabled} onPress={onPress} style={[styles.moveRow, disabled ? styles.moveRowDisabled : null]}>
      {icon ? <Icon source={icon} size={18} color={HB_COLORS.textSecondary} /> : <ColorDot color={color} />}
      <Text style={styles.moveName} numberOfLines={1}>
        {label}
      </Text>
      <Text style={styles.moveCount}>{count}</Text>
      <Icon source="chevron-right" size={20} color={HB_COLORS.textSecondary} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  searchBox: { paddingHorizontal: HB_SPACING.md, gap: HB_SPACING.xxs },
  search: { height: 44, backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: HB_COLORS.outline, elevation: 0 },
  searchInput: { minHeight: 42, fontSize: 14 },
  countRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", minHeight: 40 },
  countTitle: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, flexShrink: 1 },
  countText: { fontSize: 13, color: HB_COLORS.textSecondary },
  list: { flex: 1 },
  listContent: { backgroundColor: HB_COLORS.white, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  row: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, paddingHorizontal: HB_SPACING.md, paddingVertical: HB_SPACING.xs, minHeight: 68, backgroundColor: HB_COLORS.white, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  rowChecked: { backgroundColor: SC_COLORS.selectedBg },
  rowText: { flex: 1, minWidth: 0, gap: 2 },
  name: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  code: { fontSize: 12, color: HB_COLORS.textSecondary },
  price: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  state: { paddingVertical: HB_SPACING.lg, alignItems: "center" },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  dangerButton: { borderColor: SC_COLORS.dangerBorder, backgroundColor: SC_COLORS.dangerBg },
  error: { color: HB_COLORS.danger },
  moveRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 52, paddingHorizontal: HB_SPACING.sm, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.control },
  moveRowDisabled: { opacity: 0.5 },
  moveName: { flex: 1, fontSize: 15, color: HB_COLORS.textPrimary },
  moveCount: { fontSize: 13, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  moveEmpty: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
});
