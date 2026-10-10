import { useEffect, useMemo, useRef, useState } from "react";
import { Image, Pressable, RefreshControl, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { ActivityIndicator, Button, Card, SegmentedButtons, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ProductBarcodeImage } from "@/components/product-maintenance/ProductBarcodeImage";
import { normalizePageSize } from "@/components/ui/pagination/pagination-logic";
import { PaginationBar } from "@/components/ui/pagination/PaginationBar";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { canViewContainerNewProducts } from "./access";
import { ARRIVAL_RANGE_FILTERS, deviceLocalToday, formatArrivalDateRange, matchesArrivalRange, type ArrivalRangeFilter } from "./arrival-range";
import { containerNewProductsQueryKey, getContainerNewProducts } from "./api";
import { ContainerFilterSheet, ProductFilterSheet } from "./container-new-products-sheets";
import { countActiveFilters, DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS, matchesFilters, pruneSelectedContainers, summarizeContainers, type ContainerNewProductFilters } from "./filters";
import { compareByArrivalThenProductNo } from "./ordering";
import { CONTAINER_NEW_PRODUCTS_PAGE_SIZE, CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS, paginate, type ContainerNewProductsPageSize } from "./pagination";
import { peekRememberedPageSize, readRememberedPageSize, rememberPageSize } from "./page-size-storage";
import { countNewProductKinds } from "./summary";
import type { ContainerNewProductItem } from "./types";

const ACCENT_SOFT = "#EAF2FF";
const SUCCESS_SOFT = "#ECFDF3";
const WARNING_SOFT = "#FFFAEB";

type OpenSheet = "product" | "containers" | "stores" | null;

function ScreenMessage({ message, onBack, retry }: { message: string; onBack: () => void; retry?: () => void }) {
  const { t } = useAppTranslation("containerNewProducts");
  return <SafeAreaView style={styles.message}><Text>{message}</Text>{retry ? <Button onPress={retry}>{t("actions.retry")}</Button> : null}<Button onPress={onBack}>{t("actions.back")}</Button></SafeAreaView>;
}

function formatPrice(value: number) {
  return value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function FilterChip({ icon, label, active, onPress }: { icon: keyof typeof MaterialCommunityIcons.glyphMap; label: string; active: boolean; onPress: () => void }) {
  return <Pressable accessibilityRole="button" onPress={onPress} style={[styles.chip, active && styles.chipActive]}>
    <MaterialCommunityIcons name={icon} size={16} color={active ? HB_COLORS.action : HB_COLORS.textSecondary} />
    <Text style={[styles.chipText, active && styles.chipTextActive]} numberOfLines={1}>{label}</Text>
    <MaterialCommunityIcons name="chevron-down" size={16} color={active ? HB_COLORS.action : HB_COLORS.textSecondary} />
  </Pressable>;
}

function ProductCard({ item, imageSize, dateWidth }: { item: ContainerNewProductItem; imageSize: number; dateWidth: number }) {
  const { t } = useAppTranslation("containerNewProducts");
  const [failedImageUrl, setFailedImageUrl] = useState<string | null>(null);
  const date = formatArrivalDateRange(item.estimatedStoreArrivalDate, item.estimatedStoreArrivalDateEnd);
  const arrived = item.basis === "actual";
  return <Card style={styles.card} contentStyle={styles.cardContent} mode="contained">
    <View style={styles.cardRow}>
      <View style={[styles.imageFrame, { width: imageSize, height: imageSize }]}>{item.imageUrl && item.imageUrl !== failedImageUrl ? <Image source={{ uri: item.imageUrl }} style={styles.image} resizeMode="contain" onError={() => setFailedImageUrl(item.imageUrl)} /> : <Text style={styles.imagePlaceholder}>{t("states.noImage")}</Text>}</View>
      <View style={styles.details}>
        <Text variant="titleMedium" style={styles.productCode} numberOfLines={1}>{item.hbProductNo ?? item.productCode}</Text>
        {item.retailPrice !== null ? <Text variant="titleSmall" style={styles.retailPrice} numberOfLines={1}>{t("labels.retailPrice", { value: formatPrice(item.retailPrice) })}</Text> : null}
        <Text variant="bodyMedium" style={styles.containerCode} numberOfLines={1}>{t("labels.container", { code: item.containerNumber?.trim() || item.containerCode })}</Text>
        {/* 不向门店展示到货数量：数量只在接口里保留，卡片上不显示 */}
        {/* 新品/已有 与到仓状态放同一行，不挤占货号；到仓状态即后端 basis：已到仓按到仓库日期推算到店日，在途按预计到岸推算 */}
        <View style={styles.tagRow}>
          <Text style={[styles.typeTag, !item.isNewProduct && styles.typeTagExisting]}>{t(item.isNewProduct ? "labels.newTag" : "labels.existingTag")}</Text>
          <View style={[styles.statusPill, arrived ? styles.statusArrived : styles.statusTransit]}>
            <MaterialCommunityIcons name={arrived ? "check" : "truck-outline"} size={13} color={arrived ? HB_COLORS.success : HB_COLORS.warning} />
            <Text style={[styles.statusText, arrived ? styles.successText : styles.warningText]}>{t(arrived ? "labels.arrived" : "labels.transit")}</Text>
          </View>
        </View>
      </View>
      <View style={[styles.dateBlock, { width: dateWidth }]}>
        <Text variant="labelMedium" style={styles.dateLabel}>{t("labels.estimatedArrival")}</Text>
        {/* 到店区间分两行「起 / – 止」，窄屏日期列也放得下；旧版后端只有单日时保持大字单行 */}
        {date.end ? <>
          <Text variant="titleMedium" style={styles.date}>{date.start}</Text>
          <Text variant="titleMedium" style={styles.dateEnd}>{`– ${date.end}`}</Text>
        </> : <Text variant="titleLarge" style={styles.date}>{date.start}</Text>}
        <Text variant="bodySmall" style={styles.dateYear}>{date.year}</Text>
      </View>
    </View>
    {/* 条码放在卡片底部整宽显示，门店可直接对着屏幕扫码；没有条码不占位 */}
    {item.barcode ? <View style={styles.barcode}><ProductBarcodeImage value={item.barcode} compact /></View> : null}
  </Card>;
}

export function ContainerNewProductsScreen() {
  const { t } = useAppTranslation("containerNewProducts");
  const { width } = useWindowDimensions();
  const router = useRouter();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const { stores, selectedStore, selectStore, isDeviceMode, isStoreSelectionReady, isLoading: storesLoading, error: storesError } = useStores();
  const canSwitchStore = !isDeviceMode && stores.length > 1;
  const storeCode = selectedStore?.storeCode ?? null;
  // 页面连同已有商品一起取（约千余行），筛选、分页都在前端做；工作台角标另用只含新商品的缓存
  const query = useQuery({
    queryKey: containerNewProductsQueryKey(storeCode, true),
    queryFn: () => getContainerNewProducts(storeCode as string, true),
    enabled: canViewContainerNewProducts(isAuthenticated, hasPermission, isReview) && isStoreSelectionReady && Boolean(storeCode),
  });
  const orderedItems = useMemo(() => [...(query.data?.items ?? [])].sort(compareByArrivalThenProductNo), [query.data?.items]);
  const [rangeFilter, setRangeFilter] = useState<ArrivalRangeFilter>("all");
  const [filters, setFilters] = useState<ContainerNewProductFilters>(DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS);
  const [openSheet, setOpenSheet] = useState<OpenSheet>(null);
  const [noteExpanded, setNoteExpanded] = useState(false);
  const [pageSize, setPageSize] = useState<ContainerNewProductsPageSize>(() => peekRememberedPageSize() ?? CONTAINER_NEW_PRODUCTS_PAGE_SIZE);
  useEffect(() => {
    let cancelled = false;
    void readRememberedPageSize().then((value) => { if (!cancelled) setPageSize(value); });
    return () => { cancelled = true; };
  }, []);
  const containerOptions = useMemo(() => summarizeContainers(orderedItems), [orderedItems]);
  // 刷新或换店后已选货柜可能不在列表里了：只按仍存在的货柜筛，避免列表空了却看不到选中项
  const effectiveFilters = useMemo(() => ({ ...filters, containerCodes: pruneSelectedContainers(filters.containerCodes, containerOptions) }), [filters, containerOptions]);
  // 过去/未来以门店所在州的今天为界；旧版后端没有该字段时退回设备日期
  const localToday = query.data?.localToday ?? deviceLocalToday();
  const itemsMatchingFilters = useMemo(() => orderedItems.filter((item) => matchesFilters(item, effectiveFilters)), [orderedItems, effectiveFilters]);
  const rangeKinds = useMemo(() => Object.fromEntries(ARRIVAL_RANGE_FILTERS.map((filter) => [filter, countNewProductKinds(itemsMatchingFilters.filter((item) => matchesArrivalRange(item, filter, localToday)))])) as Record<ArrivalRangeFilter, number>, [itemsMatchingFilters, localToday]);
  const filteredItems = useMemo(() => itemsMatchingFilters.filter((item) => matchesArrivalRange(item, rangeFilter, localToday)), [itemsMatchingFilters, rangeFilter, localToday]);
  // 筛选面板的数量：按草稿里的商品类型/到仓状态 + 当前货柜和到店时间段统计品种数
  const countForProductSheet = (draft: Pick<ContainerNewProductFilters, "productType" | "warehouse">) =>
    countNewProductKinds(orderedItems.filter((item) => matchesFilters(item, { ...effectiveFilters, ...draft }) && matchesArrivalRange(item, rangeFilter, localToday)));
  const [requestedPage, setRequestedPage] = useState(1);
  const scrollRef = useRef<ScrollView>(null);
  // 换门店、切换筛选、改每页条数都回到第 1 页；刷新后条数变少由 paginate 夹回有效页
  useEffect(() => setRequestedPage(1), [storeCode, rangeFilter, filters, pageSize]);
  const pageSlice = useMemo(() => paginate(filteredItems, requestedPage, pageSize), [filteredItems, requestedPage, pageSize]);
  const goToPage = (page: number) => {
    setRequestedPage(page);
    scrollRef.current?.scrollTo({ y: 0, animated: false });
  };
  // 改每页条数：记住选择（下次进入沿用），页码由上面的 effect 回到第 1 页
  const changePageSize = (value: number) => {
    const next = normalizePageSize(value, CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS, CONTAINER_NEW_PRODUCTS_PAGE_SIZE);
    rememberPageSize(next);
    setPageSize(next);
    scrollRef.current?.scrollTo({ y: 0, animated: false });
  };
  const activeFilterCount = countActiveFilters(effectiveFilters);
  const containerChipLabel = effectiveFilters.containerCodes.length === 0
    ? t("chips.containersAll", { count: containerOptions.length })
    : effectiveFilters.containerCodes.length === 1
      ? containerOptions.find((option) => option.containerCode.toUpperCase() === effectiveFilters.containerCodes[0].toUpperCase())?.label ?? effectiveFilters.containerCodes[0]
      : t("chips.containersMany", { count: effectiveFilters.containerCodes.length });
  const imageSize = width < 360 ? 72 : 88;
  const dateWidth = width < 360 ? 88 : 100;
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));

  if (!canViewContainerNewProducts(isAuthenticated, hasPermission, isReview)) return <ScreenMessage message={t("messages.notAllowed")} onBack={goBack} />;
  if (storesError) return <ScreenMessage message={t("messages.storesFailed")} onBack={goBack} />;
  if (storesLoading || !isStoreSelectionReady) return <ScreenMessage message={t("states.loading")} onBack={goBack} />;
  // 全局分店选择没有选中时会默认第一个；仍为空说明账号没有可用分店
  if (!selectedStore) return <ScreenMessage message={t(stores.length > 0 ? "messages.selectStore" : "messages.noStores")} onBack={goBack} />;

  const hasData = query.isSuccess && orderedItems.length > 0;
  // 分页条：列表上方放完整条（含总数、每页条数），底部只放翻页行，翻到底不用滚回顶部。
  // 总数按列表行数（与分页口径一致），不是去重后的品种数。
  const pager = (compact: boolean) => <PaginationBar
    compact={compact}
    testID={compact ? "container-new-products-pagination-bottom" : "container-new-products-pagination"}
    page={pageSlice.page}
    pageCount={pageSlice.pageCount}
    total={filteredItems.length}
    pageSize={pageSize}
    pageSizeOptions={CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS}
    pageSizeHint={t("pageSizeHint")}
    onPageChange={goToPage}
    onPageSizeChange={changePageSize}
  />;

  return <SafeAreaView style={styles.safe} edges={["top"]}>
    <View style={styles.header}><Button compact onPress={goBack} icon="chevron-left" labelStyle={styles.backLabel}>{t("actions.back")}</Button><Text variant="headlineSmall" style={styles.title}>{t("title")}</Text><View style={styles.headerSpacer} /></View>
    <ScrollView ref={scrollRef} style={styles.body} contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={query.isFetching} onRefresh={() => void query.refetch()} />}>
      {/* 可选分店多于一个时门店卡片可点，打开分店列表切换 */}
      <Pressable
        accessibilityRole={canSwitchStore ? "button" : undefined}
        accessibilityLabel={canSwitchStore ? t("actions.switchStoreLabel", { store: selectedStore.storeName }) : undefined}
        disabled={!canSwitchStore}
        onPress={() => setOpenSheet("stores")}
        style={styles.storeCard}
      >
        <MaterialCommunityIcons name="store-outline" size={26} color={HB_COLORS.action} />
        <View style={styles.storeInfo}><Text variant="labelMedium" style={styles.muted}>{t("labels.currentStore")}</Text><Text variant="titleMedium" numberOfLines={1}>{selectedStore.storeName}</Text></View>
        {query.data?.stateCode ? <Text style={styles.state}>{query.data.stateCode}</Text> : null}
        {canSwitchStore ? <View style={styles.switchStore}><Text style={styles.switchStoreText}>{t("actions.switchStore")}</Text><MaterialCommunityIcons name="chevron-right" size={18} color={HB_COLORS.action} /></View> : null}
      </Pressable>
      {/* 日期说明默认收成一行，给列表让出首屏空间；点「说明」展开全文 */}
      <Pressable accessibilityRole="button" accessibilityState={{ expanded: noteExpanded }} onPress={() => setNoteExpanded((value) => !value)} style={styles.note}>
        <MaterialCommunityIcons name="clock-outline" size={18} color={HB_COLORS.action} />
        <Text style={styles.noteText}>{noteExpanded ? t("messages.dateNotice") : t("messages.dateNoticeShort")}</Text>
        <Text style={styles.noteToggle}>{noteExpanded ? t("actions.hideDates") : t("actions.howDates")}</Text>
      </Pressable>
      <View style={styles.rangeRow}><Text style={styles.rangeText}>{t("labels.range")}</Text><Text style={styles.sortText}>{t("labels.sortByArrival")}</Text></View>
      {hasData ? <SegmentedButtons value={rangeFilter} onValueChange={(value) => setRangeFilter(value as ArrivalRangeFilter)} density="small" buttons={ARRIVAL_RANGE_FILTERS.map((filter) => ({ value: filter, label: t(`filters.${filter}`, { count: rangeKinds[filter] }), labelStyle: styles.filterLabel }))} /> : null}
      {hasData ? <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.chipsScroll} contentContainerStyle={styles.chips}>
        <FilterChip icon="star-four-points-outline" label={t(`chips.productType.${effectiveFilters.productType}`)} active={effectiveFilters.productType !== DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS.productType} onPress={() => setOpenSheet("product")} />
        <FilterChip icon="warehouse" label={t(`chips.warehouse.${effectiveFilters.warehouse}`)} active={effectiveFilters.warehouse !== "any"} onPress={() => setOpenSheet("product")} />
        <FilterChip icon="train-car-container" label={containerChipLabel} active={effectiveFilters.containerCodes.length > 0} onPress={() => setOpenSheet("containers")} />
        {activeFilterCount > 0 ? <Button compact textColor={HB_COLORS.danger} onPress={() => setFilters(DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS)}>{t("chips.clearAll")}</Button> : null}
      </ScrollView> : null}
      {hasData && filteredItems.length > 0 ? pager(false) : null}
      {query.isLoading ? <View style={styles.center}><ActivityIndicator /><Text>{t("states.loading")}</Text></View>
        : query.isError ? <View style={styles.center}><Text>{(query.error as { code?: string })?.code === "STORE_STATE_UNKNOWN" ? t("states.stateUnknown") : t("states.loadFailed")}</Text><Button onPress={() => void query.refetch()}>{t("actions.retry")}</Button></View>
        : orderedItems.length === 0 ? <View style={styles.center}><Text>{t("states.empty")}</Text></View>
        : itemsMatchingFilters.length === 0 ? <View style={styles.center}><Text>{t("states.emptyFiltered")}</Text><Button onPress={() => setFilters(DEFAULT_CONTAINER_NEW_PRODUCT_FILTERS)}>{t("actions.clearFilters")}</Button></View>
        : filteredItems.length === 0 ? <View style={styles.center}><Text>{t(rangeFilter === "past" ? "states.emptyPast" : "states.emptyUpcoming")}</Text></View>
        : pageSlice.items.map((item, index) => <ProductCard key={`${item.containerCode}:${item.productCode}:${item.estimatedStoreArrivalDate}:${index}`} item={item} imageSize={imageSize} dateWidth={dateWidth} />)}
      {hasData && filteredItems.length > 0 ? pager(true) : null}
    </ScrollView>

    <ProductFilterSheet
      visible={openSheet === "product"}
      value={{ productType: effectiveFilters.productType, warehouse: effectiveFilters.warehouse }}
      countFor={countForProductSheet}
      onApply={(draft) => { setFilters((current) => ({ ...current, ...draft })); setOpenSheet(null); }}
      onDismiss={() => setOpenSheet(null)}
    />
    <ContainerFilterSheet
      visible={openSheet === "containers"}
      options={containerOptions}
      selected={effectiveFilters.containerCodes}
      onApply={(containerCodes) => { setFilters((current) => ({ ...current, containerCodes })); setOpenSheet(null); }}
      onDismiss={() => setOpenSheet(null)}
    />
    <StorePickerModal
      visible={openSheet === "stores"}
      presentation="sheet"
      stores={stores}
      selectedStoreCode={selectedStore.storeCode}
      title={t("common:labels.selectStore")}
      cancelLabel={t("common:actions.cancel")}
      onDismiss={() => setOpenSheet(null)}
      onSelectStore={async (store) => {
        setOpenSheet(null);
        if (!store || store.storeCode === selectedStore.storeCode) return;
        await selectStore(store).catch(() => undefined);
      }}
    />
  </SafeAreaView>;
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { minHeight: 64, backgroundColor: HB_COLORS.white, flexDirection: "row", alignItems: "center", justifyContent: "space-between", borderBottomWidth: 1, borderBottomColor: HB_COLORS.outlineMuted },
  backLabel: { color: HB_COLORS.action, fontSize: 16 },
  title: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  headerSpacer: { width: 80 },
  body: { flex: 1 },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  storeCard: { backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.sheet, paddingHorizontal: HB_SPACING.md, paddingVertical: HB_SPACING.sm, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  storeInfo: { flex: 1, minWidth: 0 },
  switchStore: { flexDirection: "row", alignItems: "center", minHeight: 44, paddingLeft: HB_SPACING.xxs },
  switchStoreText: { color: HB_COLORS.action, fontWeight: "600", fontSize: 14 },
  muted: { color: HB_COLORS.textSecondary },
  state: { color: HB_COLORS.action, backgroundColor: ACCENT_SOFT, paddingHorizontal: 14, paddingVertical: 6, borderRadius: 16, fontWeight: "700", overflow: "hidden" },
  note: { backgroundColor: ACCENT_SOFT, borderRadius: HB_RADIUS.surface, paddingHorizontal: HB_SPACING.sm, paddingVertical: 10, flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  noteText: { color: HB_COLORS.textSecondary, flex: 1, minWidth: 0, fontSize: 13 },
  noteToggle: { color: HB_COLORS.action, fontWeight: "600", fontSize: 13 },
  rangeRow: { flexDirection: "row", alignItems: "center", paddingVertical: 2, gap: HB_SPACING.sm },
  rangeText: { color: HB_COLORS.textSecondary, flex: 1, minWidth: 0, flexShrink: 1 },
  sortText: { color: HB_COLORS.action, fontWeight: "700", flexShrink: 0 },
  chipsScroll: { marginHorizontal: -HB_SPACING.md, flexGrow: 0 },
  chips: { paddingHorizontal: HB_SPACING.md, gap: HB_SPACING.xs, alignItems: "center" },
  chip: { height: 38, paddingHorizontal: HB_SPACING.sm, borderRadius: 19, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white, flexDirection: "row", alignItems: "center", gap: 6, maxWidth: 220 },
  chipActive: { borderColor: HB_COLORS.action, backgroundColor: ACCENT_SOFT },
  chipText: { color: HB_COLORS.textPrimary, fontSize: 14, flexShrink: 1 },
  chipTextActive: { color: HB_COLORS.action, fontWeight: "600" },
  card: { backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.surface },
  cardContent: { padding: HB_SPACING.sm, gap: HB_SPACING.sm },
  cardRow: { flexDirection: "row", alignItems: "center", minHeight: 96 },
  barcode: { paddingTop: HB_SPACING.sm, borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  filterLabel: { fontSize: 13, marginHorizontal: 0 },
  imageFrame: { width: 88, height: 88, borderRadius: 14, backgroundColor: HB_COLORS.surfaceMuted, justifyContent: "center", alignItems: "center", overflow: "hidden" },
  image: { width: "100%", height: "100%" },
  imagePlaceholder: { color: HB_COLORS.textSecondary, fontSize: 12 },
  details: { flex: 1, minWidth: 0, paddingHorizontal: HB_SPACING.sm, gap: 4 },
  productCode: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  tagRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 6 },
  typeTag: { color: HB_COLORS.action, backgroundColor: ACCENT_SOFT, fontSize: 12, fontWeight: "700", paddingHorizontal: 6, paddingVertical: 3, borderRadius: 6, overflow: "hidden" },
  typeTagExisting: { color: HB_COLORS.textSecondary, backgroundColor: HB_COLORS.surfaceMuted },
  containerCode: { color: HB_COLORS.textSecondary },
  retailPrice: { color: HB_COLORS.action, fontWeight: "700" },
  statusPill: { flexDirection: "row", alignItems: "center", gap: 4, paddingHorizontal: HB_SPACING.xs, paddingVertical: 3, borderRadius: 10 },
  statusArrived: { backgroundColor: SUCCESS_SOFT },
  statusTransit: { backgroundColor: WARNING_SOFT },
  statusText: { fontSize: 12, fontWeight: "600" },
  successText: { color: HB_COLORS.success },
  warningText: { color: HB_COLORS.warning },
  dateBlock: { width: 100, borderLeftWidth: 1, borderLeftColor: HB_COLORS.outlineMuted, paddingLeft: HB_SPACING.sm },
  dateLabel: { color: HB_COLORS.textSecondary },
  date: { color: HB_COLORS.action, fontWeight: "700", marginTop: 4 },
  dateEnd: { color: HB_COLORS.action, fontWeight: "700" },
  dateYear: { color: HB_COLORS.textSecondary, marginTop: 2 },
  center: { alignItems: "center", justifyContent: "center", padding: 48, gap: 12 },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
});
