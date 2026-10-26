/**
 * 促销列表（设计稿 ①）：状态分段 + 名称搜索 + 扫码只看含某商品的促销 + 卡片列表 + 右下「新建促销」。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, FAB, Icon, IconButton, Switch, Text, TextInput } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StoreChip } from "@/components/ui/StoreChip";
import type { Store } from "@/modules/shop/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchPromotionStatusCounts, fetchPromotions, fetchStorePromotionsByProduct, setPromotionEnabled } from "./api";
import {
  canEditPromotionInStore,
  formatPromotionDate,
  getElapsedRatio,
  getPromotionStatus,
  isScopeVisible,
} from "./promotion-logic";
import type { TextInput as RNTextInput } from "react-native";
import { useScannerGuardedInput } from "@/modules/scanner/use-scanner-guarded-input";
import { resolveBarcodeToProduct } from "./promotion-product-lookup";
import { usePromotionScanner, useRegisterPromotionScanHandler } from "./promotion-scanner-bridge";
import {
  PROMOTION_STATUS_SEGMENTS,
  countPromotionsByStatus,
  filterProductPromotions,
  getListSortModel,
  getPromotionErrorKind,
  getRemainingKind,
  getSegmentCount,
  getSortCaptionKind,
} from "./promotion-screen-model";
import { Badge, PROMO_COLORS, PromotionScanSheet, RuleBlock, TABULAR } from "./promotion-ui";
import type { PromotionListItem, PromotionStatusCounts, PromotionStatusFilter } from "./types";

const PAGE_SIZE = 20;

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;
// Paper TextInput 的 ref 类型要求同时满足 RN TextInput 与 Paper 句柄，用 RN TextInput 声明即可。
type RNTextInputHandle = RNTextInput;

interface ProductFilter {
  productCode: string;
  label: string;
}

export interface PromotionListViewProps {
  t: TranslateFn;
  storeCode: string | null;
  store: Store | null;
  canSwitchStore: boolean;
  canEdit: boolean;
  /** 父组件在保存 / 复制后递增，触发列表与计数重新加载。 */
  reloadToken: number;
  onOpenStorePicker: () => void;
  onOpenItem: (item: PromotionListItem) => void;
  onCreate: () => void;
  onMessage: (message: string) => void;
  getErrorMessage: (error: unknown, fallbackKey: string) => string;
}

function formatToday(now: Date) {
  return `${String(now.getMonth() + 1).padStart(2, "0")}/${String(now.getDate()).padStart(2, "0")}`;
}

export function PromotionListView({
  t,
  storeCode,
  store,
  canSwitchStore,
  canEdit,
  reloadToken,
  onOpenStorePicker,
  onOpenItem,
  onCreate,
  onMessage,
  getErrorMessage,
}: PromotionListViewProps) {
  const [status, setStatus] = useState<PromotionStatusFilter>("active");
  const [keywordInput, setKeywordInput] = useState("");
  const [keyword, setKeyword] = useState("");
  const [productFilter, setProductFilter] = useState<ProductFilter | null>(null);
  const [items, setItems] = useState<PromotionListItem[]>([]);
  const [productItems, setProductItems] = useState<PromotionListItem[]>([]);
  const [counts, setCounts] = useState<PromotionStatusCounts | null>(null);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [togglingIds, setTogglingIds] = useState<ReadonlySet<string>>(new Set());
  const [scanVisible, setScanVisible] = useState(false);
  const [scanStatus, setScanStatus] = useState("");
  // 只认最后一次请求的结果：切分店 / 分段 / 关键字很快时，慢请求不能覆盖新结果。
  const requestRef = useRef(0);
  // 状态 / 剩余天数 / 进度按「最近一次加载」的时间计算，避免每次渲染都换基准。
  const [now, setNow] = useState(() => new Date());

  // 搜索框输入 300ms 后生效，减少逐字请求。
  useEffect(() => {
    const timer = setTimeout(() => setKeyword(keywordInput.trim()), 300);
    return () => clearTimeout(timer);
  }, [keywordInput]);

  const loadPage = useCallback(
    async (nextPage: number, mode: "replace" | "append" | "refresh") => {
      if (!storeCode) {
        setItems([]);
        setTotal(0);
        setCounts(null);
        return;
      }
      const requestId = ++requestRef.current;
      if (mode === "append") setLoadingMore(true);
      else if (mode === "refresh") setRefreshing(true);
      else setLoading(true);
      try {
        const [result, nextCounts] = await Promise.all([
          fetchPromotions({
            storeCode,
            keyword,
            status,
            page: nextPage,
            pageSize: PAGE_SIZE,
            sortModel: getListSortModel(status),
          }),
          // 计数只在首屏 / 刷新时取；翻页不变。
          mode === "append" ? Promise.resolve(null) : fetchPromotionStatusCounts(storeCode, keyword).catch(() => null),
        ]);
        if (requestId !== requestRef.current) return;
        // 后端已排除未绑定分店的促销；这里再过滤一次兼容旧后端。
        const visible = result.items.filter((item) => isScopeVisible(item.scopeType));
        setNow(new Date());
        setItems((current) => (mode === "append" ? [...current, ...visible] : visible));
        setTotal(result.total);
        setPage(nextPage);
        if (mode !== "append") setCounts(nextCounts);
      } catch (error) {
        if (requestId === requestRef.current) onMessage(getErrorMessage(error, "messages.loadFailed"));
      } finally {
        if (requestId === requestRef.current) {
          setLoading(false);
          setLoadingMore(false);
          setRefreshing(false);
        }
      }
    },
    [getErrorMessage, keyword, onMessage, status, storeCode],
  );

  const loadProductPromotions = useCallback(
    async (filter: ProductFilter, mode: "replace" | "refresh") => {
      if (!storeCode) return;
      const requestId = ++requestRef.current;
      if (mode === "refresh") setRefreshing(true);
      else setLoading(true);
      try {
        const result = await fetchStorePromotionsByProduct(filter.productCode, storeCode);
        if (requestId !== requestRef.current) return;
        setNow(new Date());
        setProductItems(result);
      } catch (error) {
        if (requestId === requestRef.current) onMessage(getErrorMessage(error, "messages.loadFailed"));
      } finally {
        if (requestId === requestRef.current) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [getErrorMessage, onMessage, storeCode],
  );

  useEffect(() => {
    if (productFilter) void loadProductPromotions(productFilter, "replace");
    else void loadPage(1, "replace");
  }, [loadPage, loadProductPromotions, productFilter, reloadToken]);

  // 切换分店后清掉商品筛选（同一商品在别的店未必有促销，避免误读）。
  useEffect(() => {
    setProductFilter(null);
  }, [storeCode]);

  const productModeItems = useMemo(
    () => (productFilter ? filterProductPromotions(productItems, status, now) : []),
    [now, productFilter, productItems, status],
  );
  const displayItems = productFilter ? productModeItems : items;
  const displayCounts = productFilter ? countPromotionsByStatus(productItems, now) : counts;
  const hasMore = !productFilter && items.length < total;

  const refresh = useCallback(() => {
    if (productFilter) void loadProductPromotions(productFilter, "refresh");
    else void loadPage(1, "refresh");
  }, [loadPage, loadProductPromotions, productFilter]);

  const handleToggle = useCallback(
    async (item: PromotionListItem, enable: boolean) => {
      if (!storeCode) return;
      setTogglingIds((current) => new Set(current).add(item.id));
      // 乐观更新：开关立即响应，失败再回滚。
      const patch = (list: PromotionListItem[], value: boolean) =>
        list.map((row) => (row.id === item.id ? { ...row, isEnabled: value } : row));
      setItems((list) => patch(list, enable));
      setProductItems((list) => patch(list, enable));
      try {
        await setPromotionEnabled(item.id, storeCode, enable);
        onMessage(t(enable ? "messages.enabled" : "messages.disabled", { name: item.name }));
        // 状态变了会换分段，重新拉一次计数与当前分段。
        refresh();
      } catch (error) {
        setItems((list) => patch(list, !enable));
        setProductItems((list) => patch(list, !enable));
        onMessage(
          getPromotionErrorKind(error) === "exclusiveConflict"
            ? t("messages.enableConflict")
            : getErrorMessage(error, "messages.enableFailed"),
        );
      } finally {
        setTogglingIds((current) => {
          const next = new Set(current);
          next.delete(item.id);
          return next;
        });
      }
    },
    [getErrorMessage, onMessage, refresh, storeCode, t],
  );

  /**
   * 扫商品条码 → 解析成商品编码 → 只看含该商品的促销。
   * 相机扫码把进度写在扫码弹层里；硬件扫码（扫码枪 / PDA）没有弹层，失败原因走页面提示条。
   */
  const handleBarcode = useCallback(
    async (barcode: string, fromHardware = false) => {
      if (!storeCode) return false;
      const report = (message: string) => (fromHardware ? onMessage(message) : setScanStatus(message));
      if (!fromHardware) setScanStatus(t("scan.looking", { barcode }));
      try {
        const result = await resolveBarcodeToProduct(barcode, storeCode);
        if (result.kind === "notFound") {
          report(t("scan.notFound", { barcode }));
          return false;
        }
        const product =
          result.kind === "single"
            ? { productCode: result.product.productCode, label: result.product.productName || barcode }
            : { productCode: result.candidates[0].productCode, label: result.candidates[0].productName || barcode };
        if (result.kind === "multiple") onMessage(t("scan.multipleUseFirst", { name: product.label }));
        setScanStatus("");
        setKeywordInput("");
        setProductFilter(product);
        return true;
      } catch (error) {
        report(getErrorMessage(error, "scan.failed"));
        return false;
      }
    },
    [getErrorMessage, onMessage, storeCode, t],
  );

  const scanner = usePromotionScanner();
  const handleHardwareScan = (barcode: string) => void handleBarcode(barcode, true);
  // 相机扫码弹层打开时由弹层处理，硬件扫码先不接。
  useRegisterPromotionScanHandler("list", handleHardwareScan, Boolean(storeCode) && !scanVisible);
  // 搜索框聚焦时扫码枪会把条码打进框里：按扫码处理（筛选含该商品的促销），不当作名称关键字。
  const searchGuard = useScannerGuardedInput<RNTextInputHandle>({
    value: keywordInput,
    onChangeText: (text) => {
      setKeywordInput(text);
      if (text && productFilter) setProductFilter(null);
    },
    onScan: handleHardwareScan,
    enabled: Boolean(storeCode),
  });

  const sortCaptionKind = getSortCaptionKind(status);
  const caption = [
    t("list.today", { date: formatToday(now) }),
    sortCaptionKind === "endAsc" ? t("list.sortEndAsc") : sortCaptionKind === "endDesc" ? t("list.sortEndDesc") : null,
  ]
    .filter(Boolean)
    .join(" · ");

  const renderCard = ({ item }: { item: PromotionListItem }) => {
    const itemStatus = getPromotionStatus(item, now);
    const editable = canEditPromotionInStore(item);
    const remaining = getRemainingKind(item, now);
    const ratio = itemStatus === "active" ? getElapsedRatio(item, now) : 0;
    const muted = itemStatus === "disabled" || itemStatus === "ended";
    const endText = remaining.kind === "longTerm" ? t("card.longTerm") : formatPromotionDate(item.effectiveEnd);
    const remainingText =
      remaining.kind === "daysLeft"
        ? t("card.daysLeft", { count: remaining.days })
        : remaining.kind === "startsIn"
          ? t("card.startsIn", { count: remaining.days })
          : remaining.kind === "ended"
            ? t("status.ended")
            : null;
    return (
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={item.name}
        onPress={() => onOpenItem(item)}
        style={({ pressed }) => [styles.card, muted && styles.cardMuted, pressed && styles.pressed]}
      >
        <RuleBlock
          quantity={item.applyQuantity}
          price={item.fixedPrice}
          label={t("card.ruleQuantity", { count: item.applyQuantity })}
        />
        <View style={styles.cardBody}>
          <View style={styles.cardTitleRow}>
            <Text style={styles.cardTitle} numberOfLines={2}>
              {item.name || t("fields.unnamed")}
            </Text>
            {editable && canEdit ? (
              <Switch
                value={item.isEnabled}
                disabled={togglingIds.has(item.id) || itemStatus === "ended"}
                onValueChange={(value) => void handleToggle(item, value)}
                accessibilityLabel={t("card.toggleLabel", { name: item.name })}
              />
            ) : item.scopeType === "MultiStore" ? (
              <View accessibilityLabel={t("card.lockedLabel")}>
                <Icon source="lock-outline" size={20} color={HB_COLORS.textSecondary} />
              </View>
            ) : null}
          </View>
          <View style={styles.badges}>
            {item.scopeType === "StoreOnly" ? (
              <Badge label={t("scope.storeOnly")} tone="blue" />
            ) : (
              <Badge label={t("scope.multiStoreCount", { count: item.storesCount })} />
            )}
            {item.isExclusive ? <Badge label={t("status.exclusive")} tone="red" /> : <Badge label={t("status.stackable")} />}
            <Badge label={t("card.productCount", { count: item.productsCount })} />
            {itemStatus === "disabled" ? <Badge label={t("status.disabled")} /> : null}
            {itemStatus === "upcoming" && status === "all" ? <Badge label={t("status.upcoming")} tone="blue" /> : null}
          </View>
          <Text style={[styles.cardDate, TABULAR]} numberOfLines={1}>
            {`${formatPromotionDate(item.effectiveStart)} — ${endText}`}
            {remainingText ? ` · ${remainingText}` : ""}
          </Text>
          {itemStatus === "active" && remaining.kind !== "longTerm" ? (
            <View style={styles.progressTrack} accessibilityRole="progressbar" accessibilityValue={{ min: 0, max: 100, now: Math.round(ratio * 100) }}>
              <View style={[styles.progressFill, { width: `${Math.round(ratio * 100)}%` }]} />
            </View>
          ) : null}
        </View>
      </Pressable>
    );
  };

  const listHeader = (
    <View style={styles.listHeader}>
      {productFilter ? (
        <View style={styles.productFilter}>
          <Icon source="barcode-scan" size={16} color={PROMO_COLORS.blueText} />
          <Text style={styles.productFilterText} numberOfLines={1}>
            {t("list.productFilter", { name: productFilter.label })}
          </Text>
          <IconButton
            icon="close"
            size={16}
            onPress={() => setProductFilter(null)}
            accessibilityLabel={t("list.clearProductFilter")}
            style={styles.productFilterClose}
          />
        </View>
      ) : null}
      <Text style={styles.caption}>{caption}</Text>
    </View>
  );

  return (
    <View style={styles.flex}>
      <View style={styles.header}>
        <View style={styles.headerRow}>
          <Text style={styles.title} numberOfLines={1}>
            {t("title")}
          </Text>
          <View style={styles.storeChip}>
            <StoreChip compact store={store} onPress={() => (canSwitchStore ? onOpenStorePicker() : undefined)} />
          </View>
        </View>
        <View style={styles.searchRow}>
          <TextInput
            mode="outlined"
            dense
            ref={searchGuard.inputRef}
            value={keywordInput}
            placeholder={t("filters.keyword")}
            onChangeText={searchGuard.onChangeText}
            onFocus={scanner.onVisibleInputFocus}
            onBlur={scanner.onVisibleInputBlur}
            returnKeyType="search"
            onSubmitEditing={searchGuard.guardSubmit(() => setKeyword(keywordInput.trim()))}
            style={styles.searchInput}
            outlineStyle={styles.searchOutline}
            left={<TextInput.Icon icon="magnify" />}
            right={keywordInput ? <TextInput.Icon icon="close-circle" onPress={() => setKeywordInput("")} /> : undefined}
          />
          <IconButton
            icon="barcode-scan"
            mode="outlined"
            size={22}
            disabled={!storeCode}
            onPress={() => {
              setScanStatus("");
              setScanVisible(true);
            }}
            accessibilityLabel={t("scan.listButton")}
            style={styles.scanButton}
            iconColor={HB_COLORS.action}
          />
        </View>
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.segments}>
          {PROMOTION_STATUS_SEGMENTS.map((segment) => {
            const selected = segment === status;
            const count = getSegmentCount(displayCounts, segment);
            return (
              <Pressable
                key={segment}
                accessibilityRole="tab"
                accessibilityState={{ selected }}
                onPress={() => setStatus(segment)}
                style={[styles.segment, selected && styles.segmentSelected]}
              >
                <Text style={[styles.segmentText, selected && styles.segmentTextSelected]}>{t(`segments.${segment}`)}</Text>
                {count != null && segment !== "all" && segment !== "ended" ? (
                  <Text style={[styles.segmentCount, selected && styles.segmentTextSelected, TABULAR]}>{count}</Text>
                ) : null}
              </Pressable>
            );
          })}
        </ScrollView>
      </View>

      {loading && !refreshing && displayItems.length === 0 ? (
        <ActivityIndicator style={styles.loading} />
      ) : (
        <FlatList
          data={displayItems}
          keyExtractor={(item) => item.id}
          renderItem={renderCard}
          ListHeaderComponent={listHeader}
          contentContainerStyle={styles.listContent}
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} />}
          onEndReachedThreshold={0.4}
          onEndReached={() => {
            if (hasMore && !loadingMore && !loading) void loadPage(page + 1, "append");
          }}
          ListEmptyComponent={
            !storeCode ? (
              <EmptyState title={t("empty.selectStoreTitle")} description={t("empty.selectStoreDescription")} />
            ) : (
              <EmptyState
                title={t("empty.noDataTitle")}
                description={productFilter ? t("empty.noProductPromotion") : t("empty.noDataDescription")}
              />
            )
          }
          ListFooterComponent={
            loadingMore ? (
              <ActivityIndicator style={styles.footerLoading} />
            ) : hasMore ? (
              <Button onPress={() => void loadPage(page + 1, "append")}>{t("list.loadMore")}</Button>
            ) : null
          }
        />
      )}

      {canEdit && storeCode ? (
        <FAB icon="plus" label={t("actions.create")} onPress={onCreate} style={styles.fab} color={HB_COLORS.white} />
      ) : null}

      <PromotionScanSheet
        visible={scanVisible}
        title={t("scan.listTitle")}
        subtitle={t("scan.listSubtitle")}
        defaultMode="single"
        statusText={scanStatus}
        permissionTitle={t("scan.permissionTitle")}
        permissionDescription={t("scan.permissionDescription")}
        grantLabel={t("scan.grantPermission")}
        onDismiss={() => setScanVisible(false)}
        onBarcode={handleBarcode}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  header: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    gap: HB_SPACING.sm,
    backgroundColor: HB_COLORS.background,
  },
  headerRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  title: { fontSize: 22, lineHeight: 30, fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  storeChip: { flexShrink: 1, maxWidth: "60%" },
  searchRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  searchInput: { flex: 1, backgroundColor: HB_COLORS.white },
  searchOutline: { borderRadius: HB_RADIUS.control },
  scanButton: { margin: 0, borderRadius: HB_RADIUS.control, borderColor: HB_COLORS.outline, width: 48, height: 48 },
  segments: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.xs },
  segment: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    minHeight: 36,
    paddingHorizontal: 14,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  segmentSelected: { backgroundColor: HB_COLORS.textPrimary, borderColor: HB_COLORS.textPrimary },
  segmentText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  segmentCount: { fontSize: 12, color: HB_COLORS.textSecondary },
  segmentTextSelected: { color: HB_COLORS.white },
  listHeader: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.xs },
  caption: { fontSize: 13, color: HB_COLORS.textSecondary },
  productFilter: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: PROMO_COLORS.blueBg,
  },
  productFilterText: { flex: 1, fontSize: 13, fontWeight: "600", color: PROMO_COLORS.blueText },
  productFilterClose: { margin: 0 },
  listContent: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs, paddingBottom: 112, gap: HB_SPACING.sm },
  loading: { marginTop: 48 },
  footerLoading: { marginVertical: HB_SPACING.md },
  card: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  cardMuted: { opacity: 0.72 },
  pressed: { opacity: 0.6 },
  cardBody: { flex: 1, minWidth: 0, gap: 6 },
  cardTitleRow: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: HB_SPACING.xs },
  cardTitle: { flex: 1, fontSize: 16, lineHeight: 22, fontWeight: "700", color: HB_COLORS.textPrimary },
  badges: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  cardDate: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  progressTrack: { height: 4, borderRadius: 2, backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden" },
  progressFill: { height: 4, borderRadius: 2, backgroundColor: HB_COLORS.success },
  fab: {
    position: "absolute",
    right: HB_SPACING.md,
    bottom: HB_SPACING.lg,
    backgroundColor: HB_COLORS.brand,
    borderRadius: 28,
  },
});
