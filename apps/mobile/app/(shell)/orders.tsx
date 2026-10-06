import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  FlatList,
  Image,
  Platform,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  type StyleProp,
  type ViewToken,
  type ViewStyle,
  View,
} from "react-native";
import { useQuery } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Card,
  Chip,
  Divider,
  Icon,
  IconButton,
  Portal,
  Modal,
  RadioButton,
  Snackbar,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveLocaleTag } from "@/shared/i18n/types";
import {
  DEFAULT_ORDER_LIST_PAGE_SIZE,
  filterOrderDetailLinesByItemNumber,
  formatOrderDate,
  getOrderDetailLineAllocatedImportAmount,
  getOrderDetailTotalAllocatedImportAmount,
  getOrderRowNumber,
} from "@/modules/orders/order-list-display";
import { buildOrderLineLabelPayload } from "@/modules/orders/order-label-payload";
import { fetchOrderDetail, fetchOrderList } from "@/modules/orders/store-order-api";
import {
  StoreOrderFlowStatus,
  type StoreOrderDetail,
  type StoreOrderDetailLine,
  type StoreOrderListItem,
} from "@/modules/orders/types";
import { printProductLabelPayload } from "@/modules/printer/api";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";

const HISTORY_STATUS_VALUES: StoreOrderFlowStatus[] = [
  StoreOrderFlowStatus.Submitted,
  StoreOrderFlowStatus.Picking,
  StoreOrderFlowStatus.Completed,
];
const PAGE_SIZE = DEFAULT_ORDER_LIST_PAGE_SIZE;

function formatNumber(value?: number, digits = 0) {
  if (value === undefined || value === null) {
    return "--";
  }

  return Number(value).toFixed(digits);
}

function formatMoney(value?: number) {
  return `$${formatNumber(value, 2)}`;
}

function StatusBadge({
  status,
  label,
}: {
  status?: StoreOrderFlowStatus;
  label: string;
}) {
  const toneMap: Record<string, { textColor: string; toneColor: string }> = {
    ShoppingCart: { textColor: "#5B5B5B", toneColor: "#ECECEC" },
    Submitted: { textColor: "#0958D9", toneColor: "#E6F4FF" },
    Completed: { textColor: "#237804", toneColor: "#F6FFED" },
    Picking: { textColor: "#AD6800", toneColor: "#FFF7E6" },
    Unknown: { textColor: "#5B5B5B", toneColor: "#ECECEC" },
  };

  const key =
    status === StoreOrderFlowStatus.ShoppingCart
      ? "ShoppingCart"
      : status === StoreOrderFlowStatus.Submitted
        ? "Submitted"
        : status === StoreOrderFlowStatus.Completed
          ? "Completed"
          : status === StoreOrderFlowStatus.Picking
            ? "Picking"
            : "Unknown";
  const meta = toneMap[key];

  return (
    <View style={[styles.statusBadge, { backgroundColor: meta.toneColor }]}>
      <Text variant="labelMedium" style={[styles.statusBadgeText, { color: meta.textColor }]}>
        {label}
      </Text>
    </View>
  );
}

function SummaryMetric({
  label,
  style,
  value,
}: {
  label: string;
  style?: StyleProp<ViewStyle>;
  value: string;
}) {
  return (
    <View style={[styles.summaryMetric, style]}>
      <Text variant="labelMedium" style={styles.summaryLabel} numberOfLines={1}>
        {label}
      </Text>
      <Text
        variant="titleMedium"
        style={styles.summaryValue}
        numberOfLines={1}
        adjustsFontSizeToFit
        minimumFontScale={0.72}
      >
        {value}
      </Text>
    </View>
  );
}

function OrderCardMetric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.orderCardMetric}>
      <Text variant="labelMedium" style={styles.orderCardMetricLabel} numberOfLines={1}>
        {label}
      </Text>
      <Text variant="titleMedium" style={styles.orderCardMetricValue} numberOfLines={1}>
        {value}
      </Text>
    </View>
  );
}

const OrderLineCard = memo(function OrderLineCard({
  isPrinting,
  item,
  onPrint,
  renderMedia,
  t,
}: {
  isPrinting: boolean;
  item: StoreOrderDetailLine;
  onPrint: (item: StoreOrderDetailLine) => void;
  renderMedia: boolean;
  t: (key: string, options?: Record<string, unknown>) => string;
}) {
  return (
    <View style={styles.detailItemCard}>
      <View style={styles.detailItemContent}>
        <View style={styles.detailItemHeader}>
          {renderMedia && item.productImage ? (
            <Image source={{ uri: item.productImage }} style={styles.detailProductImage} resizeMode="cover" />
          ) : (
            <View style={[styles.detailProductImage, styles.detailProductImagePlaceholder]}>
              <Text variant="labelSmall" style={styles.detailProductImageText} numberOfLines={2}>
                {item.itemNumber || item.productCode}
              </Text>
            </View>
          )}
          <View style={styles.detailItemMain}>
            <View style={styles.detailItemTopRow}>
              <View style={styles.detailItemTitleWrap}>
                <Text variant="titleSmall" style={styles.detailItemTitle} numberOfLines={3}>
                  {item.productName || item.productCode}
                </Text>
                <Text variant="bodySmall" style={styles.detailItemSubTitle}>
                  {t("fields.itemNumber", { value: item.itemNumber || "--" })}
                </Text>
                <Text variant="bodySmall" style={styles.detailItemSubTitle} numberOfLines={1}>
                  {t("fields.barcode", { value: item.barcode || "--" })}
                </Text>
              </View>
              <View style={styles.detailItemStatusWrap}>
                <Text variant="bodyMedium" style={styles.detailQtyText}>
                  {t("fields.orderedQty", { value: formatNumber(item.quantity) })}
                </Text>
                <Text variant="bodySmall" style={styles.detailAllocText}>
                  {t("fields.allocQty", { value: formatNumber(item.allocQuantity) })}
                </Text>
              </View>
              <Icon source="chevron-right" size={20} color="#667085" />
            </View>
          </View>
        </View>

        <View style={styles.detailMetaGrid}>
          <View style={styles.detailMetaCell}>
            <Text variant="labelSmall" style={styles.detailMetaLabel}>
              {t("fields.salesPrice")}
            </Text>
            <Text variant="bodyMedium">{formatMoney(item.price)}</Text>
          </View>
          <View style={styles.detailMetaCell}>
            <Text variant="labelSmall" style={styles.detailMetaLabel}>
              {t("fields.importPrice")}
            </Text>
            <Text variant="bodyMedium">{formatMoney(item.importPrice)}</Text>
          </View>
          <View style={styles.detailMetaCell}>
            <Text variant="labelSmall" style={styles.detailMetaLabel}>
              {t("fields.allocAmount")}
            </Text>
            <Text variant="bodyMedium">{formatMoney(getOrderDetailLineAllocatedImportAmount(item))}</Text>
          </View>
        </View>
        <View style={styles.detailItemActions}>
          <Button
            compact
            disabled={isPrinting}
            icon="printer-outline"
            loading={isPrinting}
            mode="outlined"
            onPress={() => onPrint(item)}
            style={styles.detailPrintButton}
            textColor="#1677FF"
          >
            {t("actions.printLabel")}
          </Button>
        </View>
      </View>
    </View>
  );
}, (prevProps, nextProps) => (
  prevProps.isPrinting === nextProps.isPrinting
  && prevProps.item === nextProps.item
  && prevProps.onPrint === nextProps.onPrint
  && prevProps.renderMedia === nextProps.renderMedia
  && prevProps.t === nextProps.t
));

function OrderDetailContent({
  detail,
  listItem,
  itemNumberFilter,
  loading,
  errorMessage,
  localeTag,
  onClose,
  onItemNumberFilterChange,
  onPrintLine,
  onRetry,
  printingDetailGuid,
  statusLabel,
  t,
}: {
  detail?: StoreOrderDetail;
  listItem?: StoreOrderListItem;
  itemNumberFilter: string;
  loading: boolean;
  errorMessage?: string;
  localeTag: string;
  onClose: () => void;
  onItemNumberFilterChange: (value: string) => void;
  onPrintLine: (item: StoreOrderDetailLine) => void;
  onRetry: () => void;
  printingDetailGuid: string | null;
  statusLabel: (status?: StoreOrderFlowStatus) => string;
  t: (key: string, options?: Record<string, unknown>) => string;
}) {
  const filteredItems = useMemo(
    () => filterOrderDetailLinesByItemNumber(detail?.items ?? [], itemNumberFilter),
    [detail?.items, itemNumberFilter]
  );
  const [visibleDetailGuids, setVisibleDetailGuids] = useState<Set<string>>(() => new Set());
  const [infoExpanded, setInfoExpanded] = useState(false);
  const viewabilityConfig = useRef({ itemVisiblePercentThreshold: 10 });

  useEffect(() => {
    setVisibleDetailGuids(new Set());
  }, [detail?.orderGUID, itemNumberFilter]);

  useEffect(() => {
    setInfoExpanded(false);
  }, [detail?.orderGUID]);

  const onViewableItemsChanged = useRef(
    ({ viewableItems }: { viewableItems: ViewToken[] }) => {
      const nextVisibleGuids = new Set<string>();

      for (const viewableItem of viewableItems) {
        const detailLine = viewableItem.item as StoreOrderDetailLine | undefined;

        if (detailLine?.detailGUID) {
          nextVisibleGuids.add(detailLine.detailGUID);
        }
      }

      setVisibleDetailGuids((previous) => {
        if (previous.size === nextVisibleGuids.size) {
          let changed = false;

          for (const detailGuid of nextVisibleGuids) {
            if (!previous.has(detailGuid)) {
              changed = true;
              break;
            }
          }

          if (!changed) {
            return previous;
          }
        }

        return nextVisibleGuids;
      });
    }
  );

  // 只给可见行挂载重媒体内容，减少图片和条码组件同时存在的数量。
  const renderDetailItem = useCallback(
    ({ item }: { item: StoreOrderDetailLine }) => (
      <OrderLineCard
        isPrinting={printingDetailGuid === item.detailGUID}
        item={item}
        onPrint={onPrintLine}
        renderMedia={visibleDetailGuids.has(item.detailGUID)}
        t={t}
      />
    ),
    [onPrintLine, printingDetailGuid, t, visibleDetailGuids]
  );

  const renderDetailHeader = useCallback(
    () => (
      <View style={styles.detailHeaderContent}>
        <View style={styles.detailStoreBanner}>
          <View style={styles.detailStoreIcon}><Icon source="storefront-outline" size={20} color="#1677FF" /></View>
          <View style={styles.detailStoreBannerText}>
            <Text variant="bodyMedium" style={styles.detailStoreName} numberOfLines={1}>
              {listItem?.storeName || detail?.storeCode || "--"}
            </Text>
            <Text variant="labelSmall" style={styles.detailStoreCaption}>{t("detailStoreCaption")}</Text>
          </View>
        </View>

        <View style={styles.detailOrderSummary}>
          <View style={styles.detailTitleRow}>
            <Text variant="headlineSmall" style={styles.detailOrderNo}>{detail?.orderNo || "--"}</Text>
            <StatusBadge status={detail?.flowStatus} label={statusLabel(detail?.flowStatus)} />
          </View>
          <Text variant="bodySmall" style={styles.detailDateText}>
            {t("fields.orderDate")} {formatOrderDate(detail?.orderDate, localeTag)} · {t("fields.outboundDate")} {formatOrderDate(listItem?.outboundDate, localeTag)}
          </Text>

          <Pressable
            accessibilityRole="button"
            accessibilityState={{ expanded: infoExpanded }}
            onPress={() => setInfoExpanded((current) => !current)}
            style={styles.detailInfoToggle}
          >
            <Text variant="bodySmall" style={styles.detailInfoToggleText}>{t("detailInfo")}</Text>
            <Text style={styles.detailInfoChevron}>{infoExpanded ? "⌃" : "⌄"}</Text>
          </Pressable>
          {infoExpanded ? (
            <View style={styles.detailInfoBlock}>
              <Text variant="bodySmall" style={styles.detailInfoText}>{t("fields.storeAddress", { value: detail?.storeAddress || "--" })}</Text>
              <Text variant="bodySmall" style={styles.detailInfoText}>{t("fields.remarks", { value: detail?.remarks || "--" })}</Text>
              <Text variant="bodySmall" style={styles.detailInfoText}>{t("summary.orderVolume")}: {formatNumber(detail?.totalOrderVolume, 4)}</Text>
            </View>
          ) : null}

          <View style={styles.detailSummaryGrid}>
            <SummaryMetric label={t("summary.orderedQty")} value={formatNumber(detail?.totalQuantity)} />
            <SummaryMetric label={t("summary.allocQty")} value={formatNumber(detail?.totalAllocQuantity)} />
            <SummaryMetric label={t("summary.orderAmount")} value={formatMoney(detail?.totalAmount)} />
            <SummaryMetric label={t("summary.allocAmount")} value={formatMoney(getOrderDetailTotalAllocatedImportAmount(detail))} />
          </View>
        </View>

        <TextInput
          dense
          mode="outlined"
          placeholder={t("filters.itemNumberPlaceholder")}
          value={itemNumberFilter}
          onChangeText={onItemNumberFilterChange}
          autoCapitalize="none"
          autoCorrect={false}
          left={<TextInput.Icon icon="magnify" />}
          right={
            itemNumberFilter ? (
              <TextInput.Icon icon="close" onPress={() => onItemNumberFilterChange("")} />
            ) : undefined
          }
          style={styles.detailFilterInput}
        />

        <View style={styles.detailListHeader}>
          <Text variant="titleMedium" style={styles.detailSectionTitle}>{t("detailProductTitle")}</Text>
          <Text variant="bodySmall" style={styles.detailListHint}>
            {t("detailSkuCount", { count: filteredItems.length })}
          </Text>
        </View>
      </View>
    ),
    [detail, filteredItems.length, infoExpanded, itemNumberFilter, listItem?.outboundDate, listItem?.storeName, localeTag, onItemNumberFilterChange, statusLabel, t]
  );

  const renderDetailEmpty = useCallback(
    () => (itemNumberFilter.trim() ? (
      <EmptyState
        title={t("empty.noMatchingLinesTitle")}
        description={t("empty.noMatchingLinesDescription")}
      />
    ) : (
      <EmptyState title={t("empty.noLinesTitle")} description={t("empty.noLinesDescription")} />
    )),
    [itemNumberFilter, t]
  );
  const detailListExtraData = useMemo(
    () => ({
      printingDetailGuid,
      visibleDetailGuids,
    }),
    [printingDetailGuid, visibleDetailGuids]
  );

  if (loading) {
    return (
      <View style={styles.detailLoadingWrap}>
        <ActivityIndicator animating size="large" color="#1677FF" />
      </View>
    );
  }

  if (errorMessage) {
    return (
      <EmptyState
        title={t("empty.detailFailedTitle")}
        description={errorMessage}
        primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: onRetry }}
        secondaryAction={{ label: t("common:actions.close"), icon: "close", onPress: onClose }}
      />
    );
  }

  if (!detail) {
    return (
      <EmptyState
        title={t("empty.detailNotFoundTitle")}
        description={t("empty.detailNotFoundDescription")}
        primaryAction={{ label: t("common:actions.retry"), icon: "refresh", onPress: onRetry }}
        secondaryAction={{ label: t("common:actions.close"), icon: "close", onPress: onClose }}
      />
    );
  }

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.detailScreen}>
      <View style={styles.detailNavigationBar}>
        <IconButton accessibilityLabel={t("returnToList")} icon="chevron-left" onPress={onClose} style={styles.detailNavigationButton} />
        <Text variant="titleLarge" style={styles.detailNavigationTitle}>{t("detailTitle")}</Text>
        <IconButton icon="dots-horizontal" disabled style={styles.detailNavigationButton} />
      </View>
      <FlatList
        data={filteredItems}
        keyExtractor={(item) => item.detailGUID}
        renderItem={renderDetailItem}
        extraData={detailListExtraData}
        contentContainerStyle={[
          styles.detailListContent,
          filteredItems.length ? null : styles.detailListContentEmpty,
        ]}
        ListHeaderComponent={renderDetailHeader}
        ListEmptyComponent={renderDetailEmpty}
        ListFooterComponent={(
          <View style={styles.detailReturnBar}>
            <Button icon="chevron-left" mode="contained-tonal" onPress={onClose} style={styles.detailReturnButton} textColor="#1677FF">
              {t("returnToList")}
            </Button>
          </View>
        )}
        ItemSeparatorComponent={DetailItemSeparator}
        initialNumToRender={6}
        maxToRenderPerBatch={6}
        windowSize={7}
        // iOS 的 Portal Modal 内裁剪 FlatList 会偶发漏掉首行；保留虚拟化，仅在 Android 开启原生裁剪。
        removeClippedSubviews={Platform.OS === "android"}
        keyboardShouldPersistTaps="handled"
        onViewableItemsChanged={onViewableItemsChanged.current}
        viewabilityConfig={viewabilityConfig.current}
      />
    </SafeAreaView>
  );
}

function DetailItemSeparator() {
  return <View style={styles.detailItemSeparator} />;
}

export default function Orders() {
  const { t, language } = useAppTranslation(["orders", "common"]);
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const localeTag = resolveLocaleTag(language);
  const {
    stores,
    selectedStore,
    selectedStoreCode,
    isSelectedStoreAuto,
    selectStore,
    isDeviceMode,
    isStoreSelectionReady,
    debugInfo: storesDebugInfo,
    isLoading: storesLoading,
  } = useStores();
  const [allStoresSelected, setAllStoresSelected] = useState(false);
  const [initializedOrderScopeKey, setInitializedOrderScopeKey] = useState<string | null>(null);
  const [selectedStatus, setSelectedStatus] = useState<"all" | StoreOrderFlowStatus>("all");
  const [pageNumber, setPageNumber] = useState(1);
  const [selectedOrderGuid, setSelectedOrderGuid] = useState<string | null>(null);
  const [filtersVisible, setFiltersVisible] = useState(false);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [itemNumberFilter, setItemNumberFilter] = useState("");
  const [ordersRefreshing, setOrdersRefreshing] = useState(false);
  const [printingDetailGuid, setPrintingDetailGuid] = useState<string | null>(null);
  const [snackbar, setSnackbar] = useState("");
  const [orderKeyword, setOrderKeyword] = useState("");
  const [submittedOrderKeyword, setSubmittedOrderKeyword] = useState("");
  const orderStoreScopeKey = useMemo(
    () => [
      isDeviceMode ? "device" : "account",
      storesDebugInfo.userGuid,
      stores.map((store) => store.storeCode).sort().join(","),
    ].join(":"),
    [isDeviceMode, stores, storesDebugInfo.userGuid]
  );
  const orderScopeReady = isStoreSelectionReady && initializedOrderScopeKey === orderStoreScopeKey;

  useEffect(() => {
    if (!isStoreSelectionReady || initializedOrderScopeKey === orderStoreScopeKey) {
      return;
    }
    // 没选过分店（含全局自动默认的第一个）时仍默认看全部分店
    setAllStoresSelected(!isDeviceMode && (!selectedStoreCode || isSelectedStoreAuto) && stores.length > 0);
    setInitializedOrderScopeKey(orderStoreScopeKey);
  }, [initializedOrderScopeKey, isDeviceMode, isSelectedStoreAuto, isStoreSelectionReady, orderStoreScopeKey, selectedStoreCode, stores.length]);

  useEffect(() => {
    const timeout = setTimeout(() => {
      setPageNumber(1);
      setSubmittedOrderKeyword(orderKeyword.trim());
    }, 350);
    return () => clearTimeout(timeout);
  }, [orderKeyword]);

  const scopedStoreCodes = useMemo(
    () => (allStoresSelected ? stores.map((store) => store.storeCode) : []),
    [allStoresSelected, stores]
  );
  const scopedStoreKey = scopedStoreCodes.join(",");
  const hasOrderScope = Boolean(selectedStoreCode) || scopedStoreCodes.length > 0;

  const statusLabel = useCallback(
    (status?: StoreOrderFlowStatus) => {
      switch (status) {
        case StoreOrderFlowStatus.ShoppingCart:
          return t("statuses.shoppingCart");
        case StoreOrderFlowStatus.Submitted:
          return t("statuses.submitted");
        case StoreOrderFlowStatus.Completed:
          return t("statuses.completed");
        case StoreOrderFlowStatus.Picking:
          return t("statuses.picking");
        default:
          return t("statuses.unknown");
      }
    },
    [t]
  );

  const statusList = useMemo(
    () => (selectedStatus === "all" ? HISTORY_STATUS_VALUES : [selectedStatus]),
    [selectedStatus]
  );

  const handleSelectStatus = useCallback((status: "all" | StoreOrderFlowStatus) => {
    setPageNumber(1);
    setSelectedStatus(status);
  }, []);

  const handleSelectOrderStore = useCallback((store: Store) => {
    setStorePickerVisible(false);
    void selectStore(store)
      .then(() => {
        setPageNumber(1);
        setAllStoresSelected(false);
      })
      .catch((error) => {
        setSnackbar(getErrorMessage(error, "messages.storeSelectFailed"));
      });
  }, [getErrorMessage, selectStore]);

  useEffect(() => {
    setPageNumber(1);
  }, [selectedStatus, selectedStoreCode, scopedStoreKey]);

  const ordersQuery = useQuery({
    queryKey: ["storeOrders", storesDebugInfo.userGuid, allStoresSelected, selectedStoreCode, scopedStoreKey, statusList.join(","), submittedOrderKeyword, pageNumber],
    enabled: isStoreSelectionReady && orderScopeReady && hasOrderScope,
    queryFn: ({ signal }) =>
      fetchOrderList({
        storeCode: allStoresSelected ? undefined : selectedStoreCode ?? undefined,
        storeCodes: allStoresSelected ? scopedStoreCodes : undefined,
        pageNumber,
        pageSize: PAGE_SIZE,
        statusList,
        keyword: submittedOrderKeyword || undefined,
      }, signal),
  });

  const detailQuery = useQuery({
    queryKey: ["storeOrderDetail", selectedOrderGuid],
    enabled: Boolean(selectedOrderGuid),
    queryFn: () => fetchOrderDetail(selectedOrderGuid!),
  });

  const refetchOrders = ordersQuery.refetch;
  const handleRefreshOrders = useCallback(async () => {
    if (!hasOrderScope) {
      return;
    }

    setOrdersRefreshing(true);
    try {
      await refetchOrders();
    } finally {
      setOrdersRefreshing(false);
    }
  }, [hasOrderScope, refetchOrders]);

  const orderItems = ordersQuery.data?.items ?? [];
  const selectedOrderItem = useMemo(
    () => ordersQuery.data?.items.find((item) => item.orderGUID === selectedOrderGuid),
    [ordersQuery.data?.items, selectedOrderGuid]
  );
  const total = ordersQuery.data?.total ?? 0;
  const canGoPrevPage = pageNumber > 1;
  const canGoNextPage = pageNumber * PAGE_SIZE < total;

  const handleCloseDetail = useCallback(() => {
    setSelectedOrderGuid(null);
    setItemNumberFilter("");
  }, []);

  const handleSelectOrder = useCallback((orderGuid: string) => {
    setItemNumberFilter("");
    setSelectedOrderGuid(orderGuid);
  }, []);

  const handlePrintLine = useCallback(
    async (item: StoreOrderDetailLine) => {
      setPrintingDetailGuid(item.detailGUID);
      try {
        // 订单明细中的 Print label 统一走普通商品标签模板。
        await printProductLabelPayload(buildOrderLineLabelPayload(item));
        setSnackbar(t("messages.printSuccess"));
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.printFailed"));
      } finally {
        setPrintingDetailGuid(null);
      }
    },
    [getErrorMessage, t]
  );
  const handlePrintDetailLine = useCallback(
    (item: StoreOrderDetailLine) => void handlePrintLine(item),
    [handlePrintLine]
  );

  const renderOrderCard = ({ item, index }: { item: StoreOrderListItem; index: number }) => (
    <Pressable onPress={() => handleSelectOrder(item.orderGUID)}>
      <Card mode="outlined" style={styles.orderCard}>
        <Card.Content style={styles.orderCardContent}>
          <View style={styles.orderHeader}>
            <View style={styles.orderHeaderLeft}>
              <View style={styles.orderNoRow}>
                <Text variant="labelMedium" style={styles.orderRowNumber}>
                  #{getOrderRowNumber(pageNumber, PAGE_SIZE, index)}
                </Text>
                <Text variant="titleMedium" style={styles.orderNo}>
                  {item.orderNo || "--"}
                </Text>
              </View>
              <View style={styles.orderMetaRow}>
                <Text variant="bodySmall" style={styles.orderStoreText}>
                  {item.storeName || item.storeCode || "--"}
                </Text>
              </View>
              <View style={styles.orderDateGrid}>
                <View style={styles.orderDateItem}>
                  <Text variant="labelSmall" style={styles.orderDateLabel}>
                    {t("fields.orderDate")}
                  </Text>
                  <Text variant="bodySmall" style={styles.orderDateText}>
                    {formatOrderDate(item.orderDate, localeTag)}
                  </Text>
                </View>
                <View style={styles.orderDateItem}>
                  <Text variant="labelSmall" style={styles.orderDateLabel}>
                    {t("fields.outboundDate")}
                  </Text>
                  <Text variant="bodySmall" style={styles.orderDateText}>
                    {formatOrderDate(item.outboundDate, localeTag)}
                  </Text>
                </View>
              </View>
            </View>
            <StatusBadge status={item.flowStatus} label={statusLabel(item.flowStatus)} />
          </View>

          <View style={styles.orderSummaryRow}>
            <OrderCardMetric label={t("summary.orderedQty")} value={formatNumber(item.totalQuantity)} />
            <OrderCardMetric label={t("summary.allocQty")} value={formatNumber(item.totalAllocQuantity)} />
            <OrderCardMetric label={t("summary.orderAmount")} value={formatMoney(item.totalOrderAmount)} />
          </View>

          <Divider style={styles.orderDivider} />

          <View style={styles.orderFooter}>
            <Text variant="bodySmall" style={styles.orderFooterText}>
              {t("summary.allocAmount")} {formatMoney(item.importTotalAmount)}
            </Text>
            <Text variant="bodySmall" style={styles.orderFooterLink}>
              {t("viewDetail")}
            </Text>
          </View>
        </Card.Content>
      </Card>
    </Pressable>
  );

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <View style={styles.headerRow}>
        <Text variant="titleLarge" style={styles.pageTitle}>
          {t("title")}
        </Text>
        <IconButton
          icon="filter-variant"
          size={20}
          mode="contained-tonal"
          onPress={() => setFiltersVisible(true)}
          style={styles.filterButton}
        />
      </View>

      <Pressable
        accessibilityRole="button"
        style={styles.scopeBar}
        onPress={() => setStorePickerVisible(true)}
        disabled={isDeviceMode}
      >
        <View style={styles.scopeBarIcon}>
          <Text style={styles.scopeBarIconText}>⌂</Text>
        </View>
        <View style={styles.scopeBarTextWrap}>
          <Text variant="labelSmall" style={styles.scopeBarLabel}>{t("scopeLabel")}</Text>
          <Text variant="bodyMedium" style={styles.scopeBarValue} numberOfLines={1}>
            {allStoresSelected ? t("allManagedStores") : selectedStore?.storeName || t("selectStore")}
          </Text>
        </View>
        <Text style={styles.scopeBarChevron}>›</Text>
      </Pressable>
      <View style={styles.searchRow}>
        <TextInput
          mode="outlined"
          dense
          value={orderKeyword}
          onChangeText={setOrderKeyword}
          placeholder={t("searchPlaceholder")}
          left={<TextInput.Icon icon="magnify" />}
          right={orderKeyword ? <TextInput.Icon icon="close" onPress={() => setOrderKeyword("")} /> : undefined}
          style={styles.searchInput}
        />
      </View>
      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        style={styles.statusTabsScroll}
        contentContainerStyle={styles.statusTabsRow}
      >
        <Chip
          hitSlop={4}
          selected={selectedStatus === "all"}
          mode={selectedStatus === "all" ? "flat" : "outlined"}
          onPress={() => handleSelectStatus("all")}
          style={styles.statusTab}
        >{t("filters.allHistory")}</Chip>
        {HISTORY_STATUS_VALUES.map((status) => (
          <Chip
            key={status}
            hitSlop={4}
            selected={selectedStatus === status}
            mode={selectedStatus === status ? "flat" : "outlined"}
            onPress={() => handleSelectStatus(status)}
            style={styles.statusTab}
          >{statusLabel(status)}</Chip>
        ))}
      </ScrollView>

      {!hasOrderScope && !storesLoading ? (
        <EmptyState title={t("empty.selectStoreTitle")} description={t("empty.selectStoreDescription")} />
      ) : (
        <FlatList
          data={orderItems}
          keyExtractor={(item) => item.orderGUID}
          renderItem={renderOrderCard}
          contentContainerStyle={styles.listContent}
          refreshControl={
            <RefreshControl refreshing={ordersRefreshing} onRefresh={() => void handleRefreshOrders()} />
          }
          ListHeaderComponent={
            <View style={styles.listHeader}>
              <Text variant="titleSmall" style={styles.listHeaderTitle}>
                {allStoresSelected ? t("allManagedStores") : selectedStore?.storeName || t("listTitle")}
              </Text>
              <Text variant="bodySmall" style={styles.listHeaderMeta}>
                {t("summary.total", { count: total })}
              </Text>
            </View>
          }
          ListEmptyComponent={
            ordersQuery.isLoading ? (
              <View style={styles.loadingWrap}>
                <ActivityIndicator animating size="large" color="#1677FF" />
              </View>
            ) : ordersQuery.isError ? (
              <EmptyState
                title={t("empty.listFailedTitle")}
                description={resolveLocalizedErrorMessage(ordersQuery.error, {
                  t,
                  language,
                  fallbackKey: "empty.noHistoryDescription",
                })}
                primaryAction={{
                  label: t("common:actions.retry"),
                  icon: "refresh",
                  onPress: () => void handleRefreshOrders(),
                }}
              />
            ) : (
              <EmptyState
                title={hasOrderScope ? t("empty.noHistoryTitle") : t("empty.noAccessTitle")}
                description={t("empty.noHistoryDescription")}
              />
            )
          }
          ListFooterComponent={
            total ? (
              <View style={styles.paginationWrap}>
                <Button mode="outlined" disabled={!canGoPrevPage} onPress={() => setPageNumber((value) => value - 1)}>
                  {t("pagination.previous")}
                </Button>
                <Text variant="bodyMedium" style={styles.pageNumberText}>
                  {t("pagination.page", { page: pageNumber })}
                </Text>
                <Button mode="outlined" disabled={!canGoNextPage} onPress={() => setPageNumber((value) => value + 1)}>
                  {t("pagination.next")}
                </Button>
              </View>
            ) : null
          }
        />
      )}

      <Portal>
        <Modal
          visible={filtersVisible}
          onDismiss={() => setFiltersVisible(false)}
          contentContainerStyle={styles.filtersModalContent}
        >
          <ScrollView contentContainerStyle={styles.filtersModalScroll}>
            <View style={styles.filtersModalHeader}>
              <Text variant="titleMedium" style={styles.filtersModalTitle}>
                {t("filterTitle")}
              </Text>
              <Button compact onPress={() => setFiltersVisible(false)}>
                {t("common:actions.close")}
              </Button>
            </View>

            <View style={styles.filtersSection}>
              <Text variant="labelLarge" style={styles.filtersSectionTitle}>
                {t("filters.store")}
              </Text>
              <Button
                mode="outlined"
                icon="storefront-outline"
                contentStyle={styles.storeSelectorButtonContent}
                style={styles.storeSelectorButton}
                onPress={() => setStorePickerVisible(true)}
              >
                {allStoresSelected ? t("allManagedStores") : selectedStore?.storeName || t("selectStore")}
              </Button>
              <Text variant="bodySmall" style={styles.filtersCurrentText}>
                {t("filters.currentStore", { store: allStoresSelected ? t("allManagedStores") : selectedStore?.storeName || t("common:na") })}
              </Text>
            </View>

            <View style={styles.filtersSection}>
              <Text variant="labelLarge" style={styles.filtersSectionTitle}>
                {t("filters.status")}
              </Text>
              <View style={styles.filterChipsGrid}>
                <Chip
                  selected={selectedStatus === "all"}
                  mode={selectedStatus === "all" ? "flat" : "outlined"}
                  onPress={() => handleSelectStatus("all")}
                  style={styles.filterChip}
                >
                  {t("filters.allHistory")}
                </Chip>
                {HISTORY_STATUS_VALUES.map((status) => (
                  <Chip
                    key={status}
                    selected={selectedStatus === status}
                    mode={selectedStatus === status ? "flat" : "outlined"}
                    onPress={() => handleSelectStatus(status)}
                    style={styles.filterChip}
                  >
                    {statusLabel(status)}
                  </Chip>
                ))}
              </View>
            </View>
          </ScrollView>
        </Modal>
        <Modal
          visible={storePickerVisible}
          onDismiss={() => setStorePickerVisible(false)}
          contentContainerStyle={styles.storePickerModalContent}
        >
          <View style={styles.storePickerHeader}>
            <View style={styles.storePickerTitleWrap}>
              <Text variant="titleMedium" style={styles.filtersModalTitle}>
                {t("filters.chooseStore")}
              </Text>
              <Text variant="bodySmall" style={styles.filtersCurrentText}>
                {t("filters.currentStore", { store: allStoresSelected ? t("allManagedStores") : selectedStore?.storeName || t("common:na") })}
              </Text>
            </View>
            <Button compact onPress={() => setStorePickerVisible(false)}>
              {t("common:actions.close")}
            </Button>
          </View>

          {storesLoading ? (
            <View style={styles.storePickerLoading}>
              <ActivityIndicator animating color="#1677FF" />
              <Text variant="bodyMedium" style={styles.filtersCurrentText}>
                {t("common:loading")}
              </Text>
            </View>
          ) : stores.length ? (
            <FlatList
              data={stores}
              keyExtractor={(store) => store.storeCode}
              contentContainerStyle={styles.storePickerListContent}
              ListHeaderComponent={!isDeviceMode && stores.length > 1 ? (
                <Pressable
                  style={[styles.storePickerRow, allStoresSelected ? styles.storePickerRowSelected : null]}
                  onPress={() => {
                    setAllStoresSelected(true);
                    setPageNumber(1);
                    setStorePickerVisible(false);
                  }}
                >
                  <RadioButton
                    value="all-managed-stores"
                    status={allStoresSelected ? "checked" : "unchecked"}
                    onPress={() => {
                      setAllStoresSelected(true);
                      setPageNumber(1);
                      setStorePickerVisible(false);
                    }}
                  />
                  <View style={styles.storePickerRowTextWrap}>
                    <Text variant="bodyMedium" style={styles.storePickerStoreName}>{t("allManagedStores")}</Text>
                    <Text variant="bodySmall" style={styles.filtersCurrentText}>{t("allManagedStoresHint", { count: stores.length })}</Text>
                  </View>
                </Pressable>
              ) : null}
              renderItem={({ item: store }) => {
                const selected = !allStoresSelected && store.storeCode === selectedStoreCode;

                return (
                  <Pressable
                    style={[styles.storePickerRow, selected ? styles.storePickerRowSelected : null]}
                    onPress={() => handleSelectOrderStore(store)}
                  >
                    <RadioButton
                      value={store.storeCode}
                      status={selected ? "checked" : "unchecked"}
                      onPress={() => handleSelectOrderStore(store)}
                    />
                    <View style={styles.storePickerRowTextWrap}>
                      <Text variant="bodyMedium" style={styles.storePickerStoreName}>
                        {store.storeName || store.storeCode}
                      </Text>
                      <Text variant="bodySmall" style={styles.filtersCurrentText}>
                        {store.storeCode}
                      </Text>
                    </View>
                  </Pressable>
                );
              }}
            />
          ) : (
            <Text variant="bodyMedium" style={styles.filtersCurrentText}>
              {t("filters.noStores")}
            </Text>
          )}
        </Modal>
        <Modal
          visible={Boolean(selectedOrderGuid)}
          onDismiss={handleCloseDetail}
          style={styles.detailModalOverlay}
          contentContainerStyle={styles.modalContent}
        >
          <OrderDetailContent
            detail={detailQuery.data}
            listItem={selectedOrderItem}
            itemNumberFilter={itemNumberFilter}
            loading={!detailQuery.data && (detailQuery.isLoading || detailQuery.isFetching)}
            errorMessage={
              detailQuery.error
                ? getErrorMessage(detailQuery.error, "empty.detailFailedDescription")
                : undefined
            }
            localeTag={localeTag}
            onClose={handleCloseDetail}
            onItemNumberFilterChange={setItemNumberFilter}
            onPrintLine={handlePrintDetailLine}
            onRetry={() => void detailQuery.refetch()}
            printingDetailGuid={printingDetailGuid}
            statusLabel={statusLabel}
            t={t}
          />
        </Modal>
      </Portal>
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  headerRow: {
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  pageTitle: {
    color: "#0F172A",
    fontWeight: "700",
  },
  filterButton: {
    margin: 0,
  },
  scopeBar: {
    marginHorizontal: 16,
    marginBottom: 8,
    minHeight: 52,
    paddingHorizontal: 12,
    borderRadius: 12,
    backgroundColor: "#EAF3FF",
    borderWidth: 1,
    borderColor: "#CFE3FF",
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  scopeBarIcon: {
    width: 30,
    height: 30,
    borderRadius: 15,
    backgroundColor: "#1677FF",
    alignItems: "center",
    justifyContent: "center",
  },
  scopeBarIconText: { color: "#FFFFFF", fontSize: 17, fontWeight: "700" },
  scopeBarTextWrap: { flex: 1, minWidth: 0, gap: 1 },
  scopeBarLabel: { color: "#5B7BA3", fontWeight: "700" },
  scopeBarValue: { color: "#0F172A", fontWeight: "700" },
  scopeBarChevron: { color: "#1677FF", fontSize: 28, lineHeight: 28 },
  searchRow: { paddingHorizontal: 16, marginBottom: 4 },
  searchInput: { backgroundColor: "#FFFFFF", height: 46 },
  // 横向 ScrollView 默认会参与纵向 flex 收缩；锁定交叉轴高度，避免 Chip 被压成一条窄缝。
  statusTabsScroll: { flexGrow: 0, flexShrink: 0, minHeight: 48 },
  statusTabsRow: { alignItems: "center", gap: 8, minHeight: 48, paddingHorizontal: 16 },
  statusTab: { borderRadius: 10, backgroundColor: "#FFFFFF" },
  filterChip: {
    backgroundColor: "#FFFFFF",
  },
  storeSelectorButton: {
    alignSelf: "stretch",
  },
  storeSelectorButtonContent: {
    flexDirection: "row-reverse",
    justifyContent: "space-between",
  },
  filtersCurrentText: {
    color: "#64748B",
  },
  listContent: {
    paddingHorizontal: 16,
    paddingTop: 4,
    paddingBottom: 20,
    gap: 12,
    flexGrow: 1,
  },
  listHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginBottom: 2,
  },
  listHeaderTitle: {
    color: "#0F172A",
    fontWeight: "600",
  },
  listHeaderMeta: {
    color: "#64748B",
  },
  loadingWrap: {
    paddingVertical: 48,
    alignItems: "center",
    justifyContent: "center",
  },
  orderCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 10,
    borderColor: "#E4E7EC",
  },
  orderCardContent: {
    gap: 10,
  },
  orderHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 12,
  },
  orderHeaderLeft: {
    flex: 1,
    gap: 4,
  },
  orderNoRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  orderRowNumber: {
    color: "#1677FF",
    fontWeight: "700",
  },
  orderNo: {
    flex: 1,
    color: "#0F172A",
    fontWeight: "700",
  },
  orderMetaRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    columnGap: 6,
    rowGap: 2,
  },
  orderStoreText: {
    color: "#64748B",
  },
  orderDateGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  orderDateItem: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  orderDateLabel: {
    color: "#94A3B8",
    fontWeight: "700",
  },
  orderDateText: {
    color: "#B45309",
    fontWeight: "700",
  },
  statusBadge: {
    alignSelf: "flex-start",
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 999,
  },
  statusBadgeText: {
    fontWeight: "700",
  },
  orderSummaryRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 10,
  },
  orderCardMetric: {
    flex: 1,
    minWidth: 0,
    gap: 6,
  },
  orderCardMetricLabel: {
    color: "#94A3B8",
    fontWeight: "700",
    lineHeight: 18,
  },
  orderCardMetricValue: {
    color: "#0F172A",
    fontSize: 20,
    fontWeight: "700",
    lineHeight: 26,
  },
  orderDivider: {
    backgroundColor: "#E2E8F0",
  },
  orderFooter: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    gap: 12,
  },
  orderFooterText: {
    color: "#475569",
  },
  orderFooterLink: {
    color: "#1677FF",
    fontWeight: "700",
  },
  paginationWrap: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    marginTop: 2,
  },
  pageNumberText: {
    color: "#475569",
  },
  filtersModalContent: {
    marginHorizontal: 16,
    marginVertical: 84,
    backgroundColor: "#FFFFFF",
    borderRadius: 16,
    overflow: "hidden",
  },
  filtersModalScroll: {
    padding: 16,
    gap: 16,
  },
  filtersModalHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  filtersModalTitle: {
    color: "#0F172A",
    fontWeight: "700",
  },
  filtersSection: {
    gap: 10,
  },
  filtersSectionTitle: {
    color: "#475569",
  },
  storePickerModalContent: {
    marginHorizontal: 16,
    marginVertical: 84,
    backgroundColor: "#FFFFFF",
    borderRadius: 16,
    maxHeight: "78%",
    overflow: "hidden",
    padding: 16,
    gap: 14,
  },
  storePickerHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  storePickerTitleWrap: {
    flex: 1,
    gap: 4,
  },
  storePickerLoading: {
    minHeight: 160,
    alignItems: "center",
    justifyContent: "center",
    gap: 10,
  },
  storePickerListContent: {
    gap: 8,
    paddingBottom: 4,
  },
  storePickerRow: {
    minHeight: 56,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingRight: 12,
  },
  storePickerRowSelected: {
    borderColor: "#1677FF",
    backgroundColor: "#EFF6FF",
  },
  storePickerRowTextWrap: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  storePickerStoreName: {
    color: "#0F172A",
    fontWeight: "700",
  },
  filterChipsGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  modalContent: {
    alignSelf: "stretch",
    backgroundColor: "#FFFFFF",
    height: "100%",
    overflow: "hidden",
    width: "100%",
  },
  // Paper Modal 默认再加一层安全区外边距；这里由内部 SafeAreaView 统一处理，避免明细容器上下被重复压缩。
  detailModalOverlay: {
    justifyContent: "flex-start",
    marginBottom: 0,
    marginTop: 0,
    paddingBottom: 0,
    paddingTop: 0,
  },
  detailScreen: { backgroundColor: "#FFFFFF", flex: 1 },
  detailNavigationBar: {
    alignItems: "center",
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
    flexDirection: "row",
    minHeight: 52,
    paddingHorizontal: 8,
  },
  detailNavigationButton: { margin: 0 },
  detailNavigationTitle: { color: "#101828", flex: 1, fontWeight: "700" },
  detailLoadingWrap: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    minHeight: 240,
  },
  detailListContent: {
    paddingBottom: 12,
  },
  detailListContentEmpty: {
    flexGrow: 1,
  },
  detailHeaderContent: {
    backgroundColor: "#FFFFFF",
  },
  detailStoreBanner: {
    alignItems: "center",
    backgroundColor: "#EEF6FF",
    flexDirection: "row",
    gap: 10,
    marginHorizontal: 16,
    marginTop: 8,
    minHeight: 48,
    paddingHorizontal: 12,
  },
  detailStoreIcon: {
    alignItems: "center",
    backgroundColor: "#DCEEFF",
    borderRadius: 8,
    height: 32,
    justifyContent: "center",
    width: 32,
  },
  detailStoreBannerText: { flex: 1, minWidth: 0 },
  detailStoreName: { color: "#101828", fontWeight: "700" },
  detailStoreCaption: { color: "#667085" },
  detailOrderSummary: {
    gap: 8,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  detailTitleRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 12,
  },
  detailOrderNo: {
    color: "#101828",
    fontWeight: "700",
  },
  detailDateText: { color: "#667085" },
  detailInfoToggle: {
    alignItems: "center",
    backgroundColor: "#F5F8FC",
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 44,
    paddingHorizontal: 12,
  },
  detailInfoToggleText: { color: "#475467", fontWeight: "600" },
  detailInfoChevron: { color: "#667085", fontSize: 18 },
  detailInfoBlock: {
    backgroundColor: "#F8FAFC",
    gap: 4,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  detailInfoText: { color: "#475467" },
  detailSummaryGrid: {
    borderTopColor: "#EAECF0",
    borderTopWidth: 1,
    columnGap: 0,
    flexDirection: "row",
    flexWrap: "wrap",
    marginTop: 2,
    paddingTop: 10,
  },
  summaryMetric: {
    gap: 2,
    minHeight: 52,
    paddingHorizontal: 4,
    width: "50%",
  },
  summaryLabel: { color: "#667085" },
  summaryValue: { color: "#101828", fontWeight: "700" },
  detailFilterInput: {
    backgroundColor: "#FFFFFF",
    height: 46,
    marginHorizontal: 16,
    marginTop: 2,
  },
  detailListHeader: {
    alignItems: "center",
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: 10,
    paddingBottom: 8,
    paddingHorizontal: 16,
  },
  detailSectionTitle: { color: "#101828", fontWeight: "700" },
  detailListHint: { color: "#667085" },
  detailItemCard: {
    backgroundColor: "#FFFFFF",
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
  },
  detailItemSeparator: { height: 0 },
  detailItemContent: {
    gap: 10,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  detailItemHeader: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 10,
  },
  detailProductImage: {
    width: 62,
    height: 62,
    borderRadius: 8,
    backgroundColor: "#F1F5F9",
  },
  detailProductImagePlaceholder: {
    alignItems: "center",
    justifyContent: "center",
    padding: 6,
    borderWidth: 1,
    borderColor: "#E2E8F0",
  },
  detailProductImageText: {
    color: "#64748B",
    textAlign: "center",
    fontSize: 12,
    lineHeight: 17,
  },
  detailItemMain: {
    flex: 1,
    minWidth: 0,
  },
  detailItemTopRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 12,
  },
  detailItemTitleWrap: {
    flex: 1,
    minWidth: 0,
    gap: 1,
  },
  detailItemTitle: {
    color: "#0F172A",
    fontWeight: "700",
    fontSize: 15,
    lineHeight: 20,
  },
  detailItemSubTitle: {
    color: "#64748B",
    fontSize: 12,
    lineHeight: 16,
  },
  detailItemStatusWrap: {
    alignItems: "flex-end",
    justifyContent: "flex-start",
    gap: 2,
    minWidth: 58,
  },
  detailQtyText: {
    color: "#0F172A",
    fontWeight: "700",
    fontSize: 14,
    lineHeight: 19,
  },
  detailAllocText: {
    color: "#B54708",
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 16,
  },
  detailMetaGrid: {
    flexDirection: "row",
    gap: 0,
  },
  detailItemActions: {
    alignItems: "stretch",
  },
  detailPrintButton: {
    borderColor: "#1677FF",
    borderRadius: 5,
    width: "100%",
  },
  detailMetaCell: {
    borderRightColor: "#EAECF0",
    borderRightWidth: 1,
    flex: 1,
    gap: 2,
    paddingHorizontal: 8,
  },
  detailMetaLabel: {
    color: "#667085",
    fontSize: 12,
    lineHeight: 16,
  },
  detailReturnBar: {
    backgroundColor: "#FFFFFF",
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  detailReturnButton: {
    backgroundColor: "#EEF6FF",
    borderRadius: 5,
  },
});
