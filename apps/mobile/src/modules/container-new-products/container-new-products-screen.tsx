import { useEffect, useMemo, useRef, useState } from "react";
import { Image, RefreshControl, ScrollView, StyleSheet, useWindowDimensions, View } from "react-native";
import { ActivityIndicator, Button, Card, SegmentedButtons, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useQuery } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ProductBarcodeImage } from "@/components/product-maintenance/ProductBarcodeImage";
import { useStores } from "@/modules/shop/use-stores";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { canViewContainerNewProducts } from "./access";
import { ARRIVAL_RANGE_FILTERS, deviceLocalToday, formatArrivalDateRange, matchesArrivalRange, type ArrivalRangeFilter } from "./arrival-range";
import { containerNewProductsQueryKey, getContainerNewProducts } from "./api";
import { compareByArrivalThenProductNo } from "./ordering";
import { paginate } from "./pagination";
import { countNewProductKinds } from "./summary";
import type { ContainerNewProductItem } from "./types";

function ScreenMessage({ message, onBack, retry }: { message: string; onBack: () => void; retry?: () => void }) {
  const { t } = useAppTranslation("containerNewProducts");
  return <SafeAreaView style={styles.message}><Text>{message}</Text>{retry ? <Button onPress={retry}>{t("actions.retry")}</Button> : null}<Button onPress={onBack}>{t("actions.back")}</Button></SafeAreaView>;
}

function formatQuantity(value: number) {
  return value.toLocaleString("en-AU", { maximumFractionDigits: 2 });
}

function formatPrice(value: number) {
  return value.toLocaleString("en-AU", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function ProductCard({ item, basisLabel, basis, imageSize, dateWidth }: { item: ContainerNewProductItem; basisLabel: string; basis: ContainerNewProductItem["basis"]; imageSize: number; dateWidth: number }) {
  const { t } = useAppTranslation("containerNewProducts");
  const [failedImageUrl, setFailedImageUrl] = useState<string | null>(null);
  const date = formatArrivalDateRange(item.estimatedStoreArrivalDate, item.estimatedStoreArrivalDateEnd);
  return <Card style={styles.card} contentStyle={styles.cardContent} mode="contained">
    <View style={styles.cardRow}>
      <View style={[styles.imageFrame, { width: imageSize, height: imageSize }]}>{item.imageUrl && item.imageUrl !== failedImageUrl ? <Image source={{ uri: item.imageUrl }} style={styles.image} resizeMode="contain" onError={() => setFailedImageUrl(item.imageUrl)} /> : <Text style={styles.imagePlaceholder}>{t("states.noImage")}</Text>}</View>
      <View style={styles.details}>
        <Text variant="titleMedium" style={styles.productCode} numberOfLines={1}>{item.hbProductNo ?? item.productCode}</Text>
        {item.retailPrice !== null ? <Text variant="titleSmall" style={styles.retailPrice} numberOfLines={1}>{t("labels.retailPrice", { value: formatPrice(item.retailPrice) })}</Text> : null}
        <Text variant="bodyMedium" style={styles.containerCode} numberOfLines={1}>{t("labels.container", { code: item.containerNumber?.trim() || item.containerCode })}</Text>
        {item.quantity !== null ? <Text variant="bodyMedium" style={styles.quantity} numberOfLines={1}>{t("labels.quantity", { value: formatQuantity(item.quantity) })}</Text> : null}
        <Text variant="bodySmall" style={[styles.basis, basis === "estimated" && styles.estimatedBasis]}>{basisLabel}</Text>
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
  const { selectedStore, isStoreSelectionReady, isLoading: storesLoading, error: storesError } = useStores();
  const storeCode = selectedStore?.storeCode ?? null;
  const query = useQuery({
    queryKey: containerNewProductsQueryKey(storeCode),
    queryFn: () => getContainerNewProducts(storeCode as string),
    enabled: canViewContainerNewProducts(isAuthenticated, hasPermission, isReview) && isStoreSelectionReady && Boolean(storeCode),
  });
  const orderedItems = useMemo(() => [...(query.data?.items ?? [])].sort(compareByArrivalThenProductNo), [query.data?.items]);
  const [rangeFilter, setRangeFilter] = useState<ArrivalRangeFilter>("all");
  // 过去/未来以门店所在州的今天为界；旧版后端没有该字段时退回设备日期
  const localToday = query.data?.localToday ?? deviceLocalToday();
  const rangeKinds = useMemo(() => Object.fromEntries(ARRIVAL_RANGE_FILTERS.map((filter) => [filter, countNewProductKinds(orderedItems.filter((item) => matchesArrivalRange(item, filter, localToday)))])) as Record<ArrivalRangeFilter, number>, [orderedItems, localToday]);
  const filteredItems = useMemo(() => orderedItems.filter((item) => matchesArrivalRange(item, rangeFilter, localToday)), [orderedItems, rangeFilter, localToday]);
  const [requestedPage, setRequestedPage] = useState(1);
  const scrollRef = useRef<ScrollView>(null);
  // 换门店、切换筛选回到第 1 页；刷新后条数变少由 paginate 夹回有效页
  useEffect(() => setRequestedPage(1), [storeCode, rangeFilter]);
  const pageSlice = useMemo(() => paginate(filteredItems, requestedPage), [filteredItems, requestedPage]);
  const goToPage = (page: number) => {
    setRequestedPage(page);
    scrollRef.current?.scrollTo({ y: 0, animated: false });
  };
  const imageSize = width < 360 ? 72 : 88;
  const dateWidth = width < 360 ? 88 : 100;
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));

  if (!canViewContainerNewProducts(isAuthenticated, hasPermission, isReview)) return <ScreenMessage message={t("messages.notAllowed")} onBack={goBack} />;
  if (storesError) return <ScreenMessage message={t("messages.storesFailed")} onBack={goBack} />;
  if (storesLoading || !isStoreSelectionReady) return <ScreenMessage message={t("states.loading")} onBack={goBack} />;
  if (!selectedStore) return <ScreenMessage message={t("messages.selectStore")} onBack={goBack} />;

  return <SafeAreaView style={styles.safe} edges={["top"]}>
    <View style={styles.header}><Button compact onPress={goBack} icon="chevron-left" labelStyle={styles.backLabel}>{t("actions.back")}</Button><Text variant="headlineSmall" style={styles.title}>{t("title")}</Text><View style={styles.headerSpacer} /></View>
    <ScrollView ref={scrollRef} style={styles.body} contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={query.isFetching} onRefresh={() => void query.refetch()} />}>
      <View style={styles.storeCard}><MaterialCommunityIcons name="store-outline" size={30} color={HB_COLORS.action} /><View style={styles.storeInfo}><Text variant="labelMedium" style={styles.muted}>{t("labels.currentStore")}</Text><Text variant="titleLarge">{selectedStore.storeName}</Text></View>{query.data?.stateCode ? <Text style={styles.state}>{query.data.stateCode}</Text> : null}</View>
      <View style={styles.note}><MaterialCommunityIcons name="clock-outline" size={22} color={HB_COLORS.action} /><Text style={styles.noteText}>{t("messages.dateNotice")}</Text></View>
      <View style={styles.rangeRow}><Text style={styles.rangeText}>{t("labels.range")}</Text><Text style={styles.sortText}>{t("labels.sortByArrival")}</Text></View>
      {query.isSuccess && orderedItems.length > 0 ? <SegmentedButtons value={rangeFilter} onValueChange={(value) => setRangeFilter(value as ArrivalRangeFilter)} density="small" buttons={ARRIVAL_RANGE_FILTERS.map((filter) => ({ value: filter, label: t(`filters.${filter}`, { count: rangeKinds[filter] }), labelStyle: styles.filterLabel }))} /> : null}
      {query.isLoading ? <View style={styles.center}><ActivityIndicator /><Text>{t("states.loading")}</Text></View> : query.isError ? <View style={styles.center}><Text>{(query.error as { code?: string })?.code === "STORE_STATE_UNKNOWN" ? t("states.stateUnknown") : t("states.loadFailed")}</Text><Button onPress={() => void query.refetch()}>{t("actions.retry")}</Button></View> : orderedItems.length === 0 ? <View style={styles.center}><Text>{t("states.empty")}</Text></View> : filteredItems.length === 0 ? <View style={styles.center}><Text>{t(rangeFilter === "past" ? "states.emptyPast" : "states.emptyUpcoming")}</Text></View> : pageSlice.items.map((item, index) => <ProductCard key={`${item.containerCode}:${item.productCode}:${item.estimatedStoreArrivalDate}:${index}`} item={item} basis={item.basis} basisLabel={item.basis === "actual" ? t("labels.actualBasis") : t("labels.estimatedBasis")} imageSize={imageSize} dateWidth={dateWidth} />)}
      {query.isSuccess && filteredItems.length > 0 ? <View style={styles.pagination}>
        <Button disabled={pageSlice.page <= 1} onPress={() => goToPage(pageSlice.page - 1)}>{t("pagination.previous")}</Button>
        <Text variant="bodyMedium" style={styles.pageText}>{t("pagination.page", { page: pageSlice.page, pageCount: pageSlice.pageCount, total: countNewProductKinds(filteredItems) })}</Text>
        <Button disabled={pageSlice.page >= pageSlice.pageCount} onPress={() => goToPage(pageSlice.page + 1)}>{t("pagination.next")}</Button>
      </View> : null}
    </ScrollView>
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
  storeCard: { backgroundColor: HB_COLORS.white, borderRadius: HB_RADIUS.sheet, padding: HB_SPACING.md, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  storeInfo: { flex: 1, minWidth: 0 },
  muted: { color: HB_COLORS.textSecondary },
  state: { color: HB_COLORS.action, backgroundColor: "#EAF2FF", paddingHorizontal: 16, paddingVertical: 8, borderRadius: 20, fontWeight: "700" },
  note: { backgroundColor: "#EAF2FF", borderRadius: HB_RADIUS.surface, padding: HB_SPACING.md, flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  noteText: { color: HB_COLORS.textSecondary, flex: 1, minWidth: 0 },
  rangeRow: { flexDirection: "row", alignItems: "center", paddingVertical: 2, gap: HB_SPACING.sm },
  rangeText: { color: HB_COLORS.textSecondary, flex: 1, minWidth: 0, flexShrink: 1 },
  sortText: { color: HB_COLORS.action, fontWeight: "700", flexShrink: 0 },
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
  containerCode: { color: HB_COLORS.textSecondary },
  quantity: { color: HB_COLORS.textPrimary, fontWeight: "600" },
  retailPrice: { color: HB_COLORS.action, fontWeight: "700" },
  basis: { color: HB_COLORS.success },
  estimatedBasis: { color: HB_COLORS.warning },
  dateBlock: { width: 100, borderLeftWidth: 1, borderLeftColor: HB_COLORS.outlineMuted, paddingLeft: HB_SPACING.sm },
  dateLabel: { color: HB_COLORS.textSecondary },
  date: { color: HB_COLORS.action, fontWeight: "700", marginTop: 4 },
  dateEnd: { color: HB_COLORS.action, fontWeight: "700" },
  dateYear: { color: HB_COLORS.textSecondary, marginTop: 2 },
  pagination: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingVertical: HB_SPACING.xs },
  pageText: { color: HB_COLORS.textSecondary, flex: 1, textAlign: "center" },
  center: { alignItems: "center", justifyContent: "center", padding: 48, gap: 12 },
  message: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
});
