import { memo, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, Image, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, type ViewToken, View } from "react-native";
import * as Clipboard from "expo-clipboard";
import { useLocalSearchParams, useRouter } from "expo-router";
import {
  ActivityIndicator,
  Button,
  Card,
  Icon,
  IconButton,
  Menu,
  Modal,
  Portal,
  RadioButton,
  Snackbar,
  Surface,
  Text,
  TextInput,
} from "react-native-paper";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  fetchInvoiceDetailsGrid,
  fetchInvoices,
} from "@/modules/local-supplier-invoices/api";
import {
  buildLocalSupplierInvoicesReturnParams,
  decodeLocalSupplierInvoicesReturnParams,
} from "@/modules/local-supplier-invoices/navigation";
import { getInvoiceInboundStatusLabel } from "@/modules/local-supplier-invoices/types";
import {
  clearInvoiceDateRange,
  formatInvoiceDateRangeDisplay,
  selectInvoiceDateRange,
  toInvoiceOrderDateFilters,
  type InvoiceDateRangeValue,
} from "@/modules/local-supplier-invoices/date-range";
import type {
  InvoiceDetailPageSize,
  InvoiceDetailPriceChangeFilter,
  InvoiceGridFilters,
  InvoiceGridSort,
  InvoiceListPageSize,
  LocalSupplierInvoice,
  LocalSupplierInvoiceItem,
} from "@/modules/local-supplier-invoices/types";
import type { Store } from "@/modules/shop/types";
import { bindDeviceStoreFilter, getDeviceBoundStoreCode } from "@/modules/shop/device-bound-store-filter";
import { useStores } from "@/modules/shop/use-stores";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

type SortOption = InvoiceGridSort & { labelKey: string };
type DetailPriceChangeOption = { value: InvoiceDetailPriceChangeFilter; labelKey: string };
type SupplierOption = { supplierCode: string; supplierName: string };
type CalendarCell = { date: Date; dateString: string; isCurrentMonth: boolean };
type EntityTagTone = "store" | "supplier" | "neutral";
type DetailPriceChange = "up" | "down" | null;
type DetailCountState = Record<InvoiceDetailPriceChangeFilter, number | null>;
type InvoiceReturnState = NonNullable<ReturnType<typeof decodeLocalSupplierInvoicesReturnParams>>;
type PendingInvoiceRestore = InvoiceReturnState & {
  listRequestKey: string;
  minimumListRequestId: number;
};
type CompletedListRequest = { id: number; key: string };

const LIST_PAGE_SIZES: InvoiceListPageSize[] = [20, 50, 100];
const DETAIL_PAGE_SIZES: InvoiceDetailPageSize[] = [50, 100, 200];
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;
const GRID_DAYS = 42;
const SORT_OPTIONS: SortOption[] = [
  { colId: "OrderDate", direction: "desc", labelKey: "filters.orderDateDesc" },
  { colId: "OrderDate", direction: "asc", labelKey: "filters.orderDateAsc" },
  { colId: "InvoiceNo", direction: "asc", labelKey: "filters.invoiceNoAsc" },
  { colId: "StoreName", direction: "asc", labelKey: "filters.storeNameAsc" },
  { colId: "SupplierName", direction: "asc", labelKey: "filters.supplierNameAsc" },
];
const DETAIL_PRICE_CHANGE_OPTIONS: DetailPriceChangeOption[] = [
  { value: "all", labelKey: "filters.allPriceChanges" },
  { value: "up", labelKey: "filters.priceUp" },
  { value: "down", labelKey: "filters.priceDown" },
];

function formatDate(value?: string | null) {
  if (!value) {
    return "--";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value.slice(0, 10);
  }
  return date.toLocaleDateString();
}

function formatMoney(value?: number | null) {
  return value == null || Number.isNaN(value) ? "--" : `$${value.toFixed(2)}`;
}

function formatNumber(value?: number | null) {
  return value == null || Number.isNaN(value) ? "--" : String(value);
}

function getDetailPriceChange(detail: LocalSupplierInvoiceItem): DetailPriceChange {
  const lastPurchasePrice = detail.lastPurchasePrice;
  const purchasePrice = detail.purchasePrice;

  if (
    lastPurchasePrice == null
    || Number.isNaN(lastPurchasePrice)
    || lastPurchasePrice <= 0
    || purchasePrice == null
    || Number.isNaN(purchasePrice)
  ) {
    return null;
  }

  if (purchasePrice > lastPurchasePrice) {
    return "up";
  }
  if (purchasePrice < lastPurchasePrice) {
    return "down";
  }
  return null;
}

function EntityTag({
  label,
  tone,
}: {
  label: string;
  tone: EntityTagTone;
}) {
  const toneStyles = ENTITY_TAG_STYLES[tone];

  return (
    <View style={[styles.entityTag, toneStyles.tag]}>
      <Text
        variant="labelMedium"
        numberOfLines={1}
        style={[styles.entityTagText, toneStyles.text]}
      >
        {label}
      </Text>
    </View>
  );
}

const InvoiceDetailRow = memo(function InvoiceDetailRow({
  detail,
  onCopy,
  onOpenProduct,
  renderImage,
  t,
}: {
  detail: LocalSupplierInvoiceItem;
  onCopy: (label: string, value: string) => void;
  onOpenProduct: (detail: LocalSupplierInvoiceItem) => void;
  renderImage: boolean;
  t: (key: string, options?: Record<string, unknown>) => string;
}) {
  const priceChange = getDetailPriceChange(detail);
  return (
    <View style={styles.detailRow}>
      <View style={styles.detailProductHeader}>
        {renderImage && detail.productImage ? (
          <Image source={{ uri: detail.productImage }} style={styles.productImage} />
        ) : (
          <View style={styles.productImagePlaceholder}>
            <Text variant="labelSmall" style={styles.productImagePlaceholderText} numberOfLines={2}>
              {detail.itemNumber || t("labels.noImage")}
            </Text>
          </View>
        )}
        <View style={styles.detailBody}>
          <View style={styles.detailProductNameRow}>
            <Text variant="titleSmall" style={styles.detailProductName} numberOfLines={2}>{detail.productName || "--"}</Text>
            {priceChange ? (
              <Text style={[
                styles.priceChangeBadge,
                priceChange === "up" ? styles.priceIncreaseBadge : styles.priceDecreaseBadge,
              ]}>
                {priceChange === "up" ? t("labels.priceIncrease") : t("labels.priceDecrease")}
              </Text>
            ) : null}
          </View>
          <View style={styles.detailProductMetaRow}>
            <Pressable
              accessibilityLabel={`${t("actions.copyItemNumber")}: ${detail.itemNumber || "--"}`}
              accessibilityRole="button"
              onPress={() => onCopy(t("labels.itemNumber"), detail.itemNumber)}
              style={styles.detailCopyTarget}
            >
              <Text variant="bodySmall" style={styles.detailProductMeta} numberOfLines={1}>{t("labels.itemNumber")} {detail.itemNumber || "--"}</Text>
            </Pressable>
            <Pressable
              accessibilityLabel={`${t("actions.copyBarcode")}: ${detail.barcode || "--"}`}
              accessibilityRole="button"
              onPress={() => onCopy(t("labels.barcode"), detail.barcode)}
              style={[styles.detailBarcodeMeta, styles.detailCopyTarget]}
            >
              <Text variant="bodySmall" style={styles.detailProductMeta} numberOfLines={1}>{t("labels.barcode")} {detail.barcode || "--"}</Text>
            </Pressable>
          </View>
        </View>
      </View>

      <View style={styles.detailPriceGrid}>
        <View style={styles.detailMetric}>
          <Text variant="labelSmall" style={styles.detailMetricLabel}>{t("labels.lastPurchasePrice")}</Text>
          <Text variant="bodyMedium" style={styles.detailMetricValue}>{formatMoney(detail.lastPurchasePrice)}</Text>
        </View>
        <View style={styles.detailMetric}>
          <Text variant="labelSmall" style={styles.detailMetricLabel}>{t("labels.purchasePrice")}</Text>
          <Text variant="bodyMedium" style={styles.detailMetricValue}>{formatMoney(detail.purchasePrice)}</Text>
        </View>
        <View style={styles.detailMetric}>
          <Text variant="labelSmall" style={styles.detailMetricLabel}>{t("labels.quantity")}</Text>
          <Text variant="bodyMedium" style={styles.detailMetricValue}>{formatNumber(detail.quantity)}</Text>
        </View>
      </View>

      <View style={styles.detailRowFooter}>
        <Text variant="bodySmall" style={styles.detailSubtotalLabel}>
          {t("labels.subtotal")} <Text style={styles.detailSubtotalValue}>{formatMoney(detail.amount)}</Text>
        </Text>
        <Button
          compact
          contentStyle={styles.detailEditButtonContent}
          icon="chevron-right"
          mode="text"
          onPress={() => onOpenProduct(detail)}
          textColor="#1677FF"
        >
          {t("actions.openProduct")}
        </Button>
      </View>
    </View>
  );
}, (previous, next) => (
  previous.detail === next.detail
  && previous.onCopy === next.onCopy
  && previous.onOpenProduct === next.onOpenProduct
  && previous.renderImage === next.renderImage
  && previous.t === next.t
));

function getPageCount(total: number, pageSize: number) {
  return Math.max(1, Math.ceil(total / pageSize));
}

function buildInvoiceListRequestKey({
  filters,
  page,
  pageSize,
  sort,
}: {
  filters: InvoiceGridFilters;
  page: number;
  pageSize: InvoiceListPageSize;
  sort: InvoiceGridSort;
}) {
  return JSON.stringify([
    page,
    pageSize,
    filters.storeCode ?? "",
    filters.supplierCode ?? "",
    filters.invoiceNo ?? "",
    filters.inboundStatus ?? "",
    filters.orderDateFrom ?? "",
    filters.orderDateTo ?? "",
    sort.colId,
    sort.direction,
  ]);
}

function pad2(value: number) {
  return value.toString().padStart(2, "0");
}

function formatMonthDate(date: Date) {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

function parseMonthDate(value?: string) {
  const match = value?.match(DATE_PATTERN);
  if (!match) {
    return undefined;
  }

  const year = Number(match[1]);
  const monthIndex = Number(match[2]) - 1;
  const day = Number(match[3]);
  const parsed = new Date(year, monthIndex, day);

  if (
    parsed.getFullYear() !== year
    || parsed.getMonth() !== monthIndex
    || parsed.getDate() !== day
  ) {
    return undefined;
  }

  return parsed;
}

function normalizeMonthDate(value?: string | null) {
  const parsed = parseMonthDate(value ?? undefined);
  return parsed ? formatMonthDate(parsed) : undefined;
}

function getMonthStart(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function addMonths(date: Date, count: number) {
  return new Date(date.getFullYear(), date.getMonth() + count, 1);
}

function buildMonthGrid(displayMonth: Date): CalendarCell[] {
  const monthStart = getMonthStart(displayMonth);
  const mondayOffset = (monthStart.getDay() + 6) % 7;
  const gridStart = new Date(monthStart.getFullYear(), monthStart.getMonth(), 1 - mondayOffset);

  return Array.from({ length: GRID_DAYS }, (_, index) => {
    const date = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + index);
    return {
      date,
      dateString: formatMonthDate(date),
      isCurrentMonth: date.getMonth() === monthStart.getMonth(),
    };
  });
}

function chunkWeeks<T>(items: T[]) {
  return Array.from({ length: Math.ceil(items.length / 7) }, (_, index) =>
    items.slice(index * 7, index * 7 + 7)
  );
}

function compareMonthDate(left?: string, right?: string) {
  if (!left || !right) {
    return 0;
  }
  return left.localeCompare(right);
}

function isDateInRange(date: string, from?: string, to?: string) {
  if (!from || !to) {
    return false;
  }
  return compareMonthDate(date, from) >= 0 && compareMonthDate(date, to) <= 0;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function asString(value: unknown) {
  if (typeof value === "string") {
    return value.trim();
  }
  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }
  return "";
}

function normalizeSupplierOptions(payload: unknown): SupplierOption[] {
  const payloadRecord = asRecord(payload);
  const items = Array.isArray(payload)
    ? payload
    : Array.isArray(payloadRecord?.items)
      ? payloadRecord.items
      : Array.isArray(payloadRecord?.data)
        ? payloadRecord.data
        : [];
  const seen = new Set<string>();

  return items
    .map((item) => {
      const record = asRecord(item) ?? {};
      const supplierCode = asString(
        record.supplierCode ?? record.SupplierCode ?? record.localSupplierCode ?? record.LocalSupplierCode
      );
      const supplierName = asString(
        record.supplierName ?? record.SupplierName ?? record.localSupplierName ?? record.LocalSupplierName
      );

      if (!supplierCode) {
        return null;
      }

      return {
        supplierCode,
        supplierName: supplierName || supplierCode,
      };
    })
    .filter((item): item is SupplierOption => {
      if (!item || seen.has(item.supplierCode)) {
        return false;
      }
      seen.add(item.supplierCode);
      return true;
    })
    .sort((left, right) =>
      left.supplierName.localeCompare(right.supplierName, undefined, {
        sensitivity: "base",
      })
    );
}

function PageSizeMenu<T extends number>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: T[];
  value: T;
  onChange: (value: T) => void;
}) {
  const [visible, setVisible] = useState(false);
  return (
    <Menu
      visible={visible}
      onDismiss={() => setVisible(false)}
      anchor={
        <Button compact mode="outlined" icon="format-list-numbered" onPress={() => setVisible(true)}>
          {label}: {value}
        </Button>
      }
    >
      {options.map((option) => (
        <Menu.Item
          key={option}
          onPress={() => {
            onChange(option);
            setVisible(false);
          }}
          title={String(option)}
        />
      ))}
    </Menu>
  );
}

function DetailPageSizeMenu<T extends number>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: T[];
  value: T;
  onChange: (value: T) => void;
}) {
  const [visible, setVisible] = useState(false);
  return (
    <Menu
      visible={visible}
      onDismiss={() => setVisible(false)}
      anchor={
        <IconButton
          accessibilityLabel={`${label}: ${value}`}
          icon="dots-horizontal"
          onPress={() => setVisible(true)}
          style={styles.detailNavigationButton}
        />
      }
    >
      {options.map((option) => (
        <Menu.Item
          key={option}
          onPress={() => {
            onChange(option);
            setVisible(false);
          }}
          title={`${label}: ${option}`}
        />
      ))}
    </Menu>
  );
}

export default function LocalSupplierInvoicesScreen() {
  const { t, language } = useAppTranslation(["localSupplierInvoices", "common", "attendance"]);
  const getErrorMessage = useCallback((error: unknown, fallbackKey: string) => (
    resolveLocalizedErrorMessage(error, {
      language,
      t,
      fallbackKey,
    })
  ), [language, t]);
  const router = useRouter();
  const {
    stores,
    selectedStoreCode,
    isSelectedStoreAuto,
    isDeviceMode,
    isStoreSelectionReady,
    isLoading: storesLoading,
  } = useStores();
  const searchParams = useLocalSearchParams<{
    source?: string | string[];
    returnInvoiceGuid?: string | string[];
    returnDetailGuid?: string | string[];
    returnDetailsPage?: string | string[];
    returnDetailsPageSize?: string | string[];
    returnDetailPriceChangeFilter?: string | string[];
    returnDetailSearch?: string | string[];
    returnListPage?: string | string[];
    returnListPageSize?: string | string[];
    returnFilterStoreCode?: string | string[];
    returnFilterSupplierCode?: string | string[];
    returnFilterInvoiceNo?: string | string[];
    returnFilterInboundStatus?: string | string[];
    returnFilterOrderDateFrom?: string | string[];
    returnFilterOrderDateTo?: string | string[];
    returnSortColId?: string | string[];
    returnSortDirection?: string | string[];
  }>();
  const [draftFilters, setDraftFilters] = useState<InvoiceGridFilters>({});
  const [filters, setFilters] = useState<InvoiceGridFilters>({});
  const [sort, setSort] = useState<InvoiceGridSort>({ colId: "OrderDate", direction: "desc" });
  const [sortMenuVisible, setSortMenuVisible] = useState(false);
  const [filtersExpanded, setFiltersExpanded] = useState(false);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [supplierPickerVisible, setSupplierPickerVisible] = useState(false);
  const [dateRangeModalVisible, setDateRangeModalVisible] = useState(false);
  const [dateRangeSnapshot, setDateRangeSnapshot] = useState<InvoiceDateRangeValue | null>(null);
  const [dateRangeDisplayMonth, setDateRangeDisplayMonth] = useState(() => getMonthStart(new Date()));
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([]);
  const [suppliersLoading, setSuppliersLoading] = useState(false);
  const [suppliersLoaded, setSuppliersLoaded] = useState(false);
  const [pageSize, setPageSize] = useState<InvoiceListPageSize>(20);
  const [page, setPage] = useState(1);
  const [items, setItems] = useState<LocalSupplierInvoice[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [selectedInvoice, setSelectedInvoice] = useState<LocalSupplierInvoice | null>(null);
  const [details, setDetails] = useState<LocalSupplierInvoiceItem[]>([]);
  const [detailsTotal, setDetailsTotal] = useState(0);
  const [detailCounts, setDetailCounts] = useState<DetailCountState>({ all: null, up: null, down: null });
  const [detailsPage, setDetailsPage] = useState(1);
  const [detailsPageSize, setDetailsPageSize] = useState<InvoiceDetailPageSize>(50);
  const [detailPriceChangeFilter, setDetailPriceChangeFilter] =
    useState<InvoiceDetailPriceChangeFilter>("all");
  const [detailSearch, setDetailSearch] = useState("");
  const [detailSearchQuery, setDetailSearchQuery] = useState("");
  const [invoiceInfoExpanded, setInvoiceInfoExpanded] = useState(false);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [snackbar, setSnackbar] = useState("");
  const [initialStoreScopeReady, setInitialStoreScopeReady] = useState(false);
  const pendingRestoreRef = useRef<PendingInvoiceRestore | null>(null);
  const handledRestoreKeyRef = useRef<string | null>(null);
  const initialStoreScopeAppliedRef = useRef(false);
  const suppliersLoadingRef = useRef(false);
  const listRequestIdRef = useRef(0);
  const detailRequestIdRef = useRef(0);
  const detailCountCacheRef = useRef(new Map<string, DetailCountState>());
  const [completedListRequest, setCompletedListRequest] = useState<CompletedListRequest | null>(null);
  const pendingDetailAnchorRef = useRef<string | null>(null);
  const detailAnchorRetryRef = useRef(0);
  const detailAnchorRetryTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const detailAnchorRetryFrameRef = useRef<number | null>(null);
  const detailListScrollRef = useRef<FlatList<LocalSupplierInvoiceItem>>(null);
  const detailItemsRef = useRef(details);
  detailItemsRef.current = details;
  const [visibleDetailGuids, setVisibleDetailGuids] = useState<Set<string>>(() => new Set());
  const detailViewabilityConfig = useRef({ itemVisiblePercentThreshold: 10 });
  const onDetailViewableItemsChanged = useRef(
    ({ viewableItems }: { viewableItems: ViewToken[] }) => {
      const nextVisibleGuids = new Set<string>();
      for (const viewableItem of viewableItems) {
        const detail = viewableItem.item as LocalSupplierInvoiceItem | undefined;
        if (detail?.detailGuid) {
          nextVisibleGuids.add(detail.detailGuid);
        }
      }
      if (pendingDetailAnchorRef.current && nextVisibleGuids.has(pendingDetailAnchorRef.current)) {
        pendingDetailAnchorRef.current = null;
        detailAnchorRetryRef.current = 0;
        if (detailAnchorRetryTimeoutRef.current) {
          clearTimeout(detailAnchorRetryTimeoutRef.current);
          detailAnchorRetryTimeoutRef.current = null;
        }
        if (detailAnchorRetryFrameRef.current != null) {
          cancelAnimationFrame(detailAnchorRetryFrameRef.current);
          detailAnchorRetryFrameRef.current = null;
        }
      }
      setVisibleDetailGuids((previous) => {
        if (previous.size === nextVisibleGuids.size && [...nextVisibleGuids].every((guid) => previous.has(guid))) {
          return previous;
        }
        return nextVisibleGuids;
      });
    }
  );
  const deviceBoundStoreCode = getDeviceBoundStoreCode({ isDeviceMode, selectedStoreCode });

  const restoreState = useMemo(
    () => decodeLocalSupplierInvoicesReturnParams({
      source: searchParams.source,
      returnInvoiceGuid: searchParams.returnInvoiceGuid,
      returnDetailGuid: searchParams.returnDetailGuid,
      returnDetailsPage: searchParams.returnDetailsPage,
      returnDetailsPageSize: searchParams.returnDetailsPageSize,
      returnDetailPriceChangeFilter: searchParams.returnDetailPriceChangeFilter,
      returnDetailSearch: searchParams.returnDetailSearch,
      returnListPage: searchParams.returnListPage,
      returnListPageSize: searchParams.returnListPageSize,
      returnFilterStoreCode: searchParams.returnFilterStoreCode,
      returnFilterSupplierCode: searchParams.returnFilterSupplierCode,
      returnFilterInvoiceNo: searchParams.returnFilterInvoiceNo,
      returnFilterInboundStatus: searchParams.returnFilterInboundStatus,
      returnFilterOrderDateFrom: searchParams.returnFilterOrderDateFrom,
      returnFilterOrderDateTo: searchParams.returnFilterOrderDateTo,
      returnSortColId: searchParams.returnSortColId,
      returnSortDirection: searchParams.returnSortDirection,
    }),
    [
      searchParams.returnDetailsPage,
      searchParams.returnDetailsPageSize,
      searchParams.returnDetailGuid,
      searchParams.returnDetailPriceChangeFilter,
      searchParams.returnDetailSearch,
      searchParams.returnFilterInvoiceNo,
      searchParams.returnFilterInboundStatus,
      searchParams.returnFilterOrderDateFrom,
      searchParams.returnFilterOrderDateTo,
      searchParams.returnFilterStoreCode,
      searchParams.returnFilterSupplierCode,
      searchParams.returnInvoiceGuid,
      searchParams.returnListPage,
      searchParams.returnListPageSize,
      searchParams.returnSortColId,
      searchParams.returnSortDirection,
      searchParams.source,
    ]
  );

  const sortLabel = useMemo(() => {
    const match = SORT_OPTIONS.find(
      (option) => option.colId === sort.colId && option.direction === sort.direction
    );
    return t(match?.labelKey ?? "filters.orderDateDesc");
  }, [sort, t]);

  const listPageCount = getPageCount(total, pageSize);
  const detailsPageCount = getPageCount(detailsTotal, detailsPageSize);
  const selectedStore = useMemo(
    () => stores.find((store) => store.storeCode === (draftFilters.storeCode ?? "")) ?? null,
    [draftFilters.storeCode, stores]
  );
  const appliedStore = useMemo(
    () => stores.find((store) => store.storeCode === (filters.storeCode ?? "")) ?? null,
    [filters.storeCode, stores]
  );
  const selectedSupplier = useMemo(
    () =>
      suppliers.find((supplier) => supplier.supplierCode === (draftFilters.supplierCode ?? "")) ??
      null,
    [draftFilters.supplierCode, suppliers]
  );
  const monthCells = useMemo(() => buildMonthGrid(dateRangeDisplayMonth), [dateRangeDisplayMonth]);
  const calendarWeeks = useMemo(() => chunkWeeks(monthCells), [monthCells]);
  const today = useMemo(() => formatMonthDate(new Date()), []);
  const weekdayLabels = useMemo(
    () =>
      Array.from({ length: 7 }, (_, index) =>
        t(`attendance:weekdays.${index}`, ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][index])
      ),
    [t]
  );
  const monthTitle = useMemo(() => {
    const locale = language === "zh" ? "zh-CN" : "en-AU";
    return new Intl.DateTimeFormat(locale, {
      year: "numeric",
      month: "long",
    }).format(dateRangeDisplayMonth);
  }, [dateRangeDisplayMonth, language]);
  const orderDateRangeLabel = useMemo(() => {
    const from = normalizeMonthDate(draftFilters.orderDateFrom);
    const to = normalizeMonthDate(draftFilters.orderDateTo);

    return formatInvoiceDateRangeDisplay(
      { from, to },
      {
        allDatesLabel: t("filters.allDates"),
        formatFrom: (date) => t("filters.dateRangeFrom", { date }),
        formatTo: (date) => t("filters.dateRangeTo", { date }),
        rangeSeparator: " ~ ",
      }
    ).text;
  }, [draftFilters.orderDateFrom, draftFilters.orderDateTo, t]);

  const bindDeviceStore = useCallback(
    (nextFilters: InvoiceGridFilters) =>
      bindDeviceStoreFilter(nextFilters, {
        isDeviceMode,
        selectedStoreCode,
        storeField: "storeCode",
      }),
    [isDeviceMode, selectedStoreCode]
  );

  const loadInvoices = useCallback(
    async (refresh = false) => {
      const requestId = ++listRequestIdRef.current;
      if (!initialStoreScopeReady || !isStoreSelectionReady || (isDeviceMode && !selectedStoreCode)) {
        setLoading(false);
        setRefreshing(false);
        return;
      }

      const requestFilters = bindDeviceStore(filters);
      const requestKey = buildInvoiceListRequestKey({
        filters: requestFilters,
        page,
        pageSize,
        sort,
      });

      if (refresh) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }
      try {
        const result = await fetchInvoices({ page, pageSize, filters: requestFilters, sort });
        if (requestId !== listRequestIdRef.current) {
          return;
        }
        setItems(result.items);
        setTotal(result.total);
        setCompletedListRequest({ id: requestId, key: requestKey });
      } catch (error) {
        if (requestId === listRequestIdRef.current) {
          setSnackbar(getErrorMessage(error, "messages.loadFailed"));
        }
      } finally {
        if (requestId === listRequestIdRef.current) {
          setLoading(false);
          setRefreshing(false);
        }
      }
    },
    [bindDeviceStore, filters, getErrorMessage, initialStoreScopeReady, isDeviceMode, isStoreSelectionReady, page, pageSize, selectedStoreCode, sort]
  );

  const loadDetails = useCallback(async () => {
    const requestId = ++detailRequestIdRef.current;
    const invoiceGuid = selectedInvoice?.invoiceGuid;
    if (!invoiceGuid) {
      setDetailsLoading(false);
      return;
    }

    setDetailsLoading(true);
    try {
      const keyword = detailSearchQuery.trim();
      const countCacheKey = JSON.stringify([invoiceGuid, keyword]);
      const cachedCounts = detailCountCacheRef.current.get(countCacheKey);
      const nextCounts: DetailCountState = cachedCounts
        ? { ...cachedCounts }
        : {
            all: null,
            up: keyword ? null : selectedInvoice?.priceIncreaseItemCount ?? 0,
            down: keyword ? null : selectedInvoice?.priceDecreaseItemCount ?? 0,
          };
      setDetailCounts(nextCounts);
      const result = await fetchInvoiceDetailsGrid(invoiceGuid, {
        page: detailsPage,
        pageSize: detailsPageSize,
        priceChange: detailPriceChangeFilter,
        keyword,
      });
      nextCounts[detailPriceChangeFilter] = result.total;

      if (requestId !== detailRequestIdRef.current) {
        return;
      }
      // 主明细成功后立即展示；辅助标签统计失败不能阻塞用户查看商品。
      setDetails(result.items);
      setDetailsTotal(result.total);
      setDetailCounts({ ...nextCounts });
      setDetailsLoading(false);

      const missingCountFilters = DETAIL_PRICE_CHANGE_OPTIONS
        .map((option) => option.value)
        .filter((filter) => nextCounts[filter] == null);
      const missingCountResults = await Promise.allSettled(
        missingCountFilters.map(async (priceChange) => ({
          priceChange,
          result: await fetchInvoiceDetailsGrid(invoiceGuid, {
            page: 1,
            pageSize: 50,
            priceChange,
            keyword,
          }),
        }))
      );
      for (const countResult of missingCountResults) {
        if (countResult.status === "fulfilled") {
          nextCounts[countResult.value.priceChange] = countResult.value.result.total;
        }
      }

      if (requestId !== detailRequestIdRef.current) {
        return;
      }
      detailCountCacheRef.current.set(countCacheKey, { ...nextCounts });
      setDetailCounts({ ...nextCounts });
    } catch (error) {
      if (requestId === detailRequestIdRef.current) {
        setSnackbar(getErrorMessage(error, "messages.detailsLoadFailed"));
      }
    } finally {
      if (requestId === detailRequestIdRef.current) {
        setDetailsLoading(false);
      }
    }
  }, [detailPriceChangeFilter, detailSearchQuery, detailsPage, detailsPageSize, getErrorMessage, selectedInvoice?.invoiceGuid, selectedInvoice?.priceDecreaseItemCount, selectedInvoice?.priceIncreaseItemCount]);

  const loadSuppliers = useCallback(async () => {
    if (suppliersLoadingRef.current) {
      return;
    }

    setSuppliersLoading(true);
    suppliersLoadingRef.current = true;
    try {
      const module = (await import("@/modules/local-supplier-invoices/api")) as {
        fetchActiveLocalSuppliers?: () => Promise<unknown>;
      };

      if (typeof module.fetchActiveLocalSuppliers !== "function") {
        throw new Error(t("messages.suppliersSourceUnavailable"));
      }

      const payload = await module.fetchActiveLocalSuppliers();
      setSuppliers(normalizeSupplierOptions(payload));
      setSuppliersLoaded(true);
    } catch (error) {
      setSnackbar(getErrorMessage(error, "messages.suppliersLoadFailed"));
    } finally {
      suppliersLoadingRef.current = false;
      setSuppliersLoading(false);
    }
  }, [getErrorMessage, t]);

  useEffect(() => {
    void loadInvoices();
  }, [loadInvoices]);

  useEffect(() => {
    void loadDetails();
  }, [loadDetails]);

  useEffect(() => {
    setVisibleDetailGuids(new Set());
  }, [detailPriceChangeFilter, detailSearchQuery, detailsPage, selectedInvoice?.invoiceGuid]);

  const scrollToPendingDetailAnchor = useCallback(() => {
    const detailGuid = pendingDetailAnchorRef.current;
    const currentDetails = detailItemsRef.current;
    if (!detailGuid || !currentDetails.length) {
      return;
    }
    const index = currentDetails.findIndex((detail) => detail.detailGuid === detailGuid);
    if (index < 0) {
      return;
    }
    detailListScrollRef.current?.scrollToIndex({ animated: false, index, viewPosition: 0 });
  }, []);

  useEffect(() => {
    if (!pendingDetailAnchorRef.current || !details.length) {
      return;
    }
    detailAnchorRetryRef.current = 0;
    detailAnchorRetryFrameRef.current = requestAnimationFrame(() => {
      detailAnchorRetryFrameRef.current = null;
      scrollToPendingDetailAnchor();
    });
    return () => {
      if (detailAnchorRetryFrameRef.current != null) {
        cancelAnimationFrame(detailAnchorRetryFrameRef.current);
        detailAnchorRetryFrameRef.current = null;
      }
      if (detailAnchorRetryTimeoutRef.current) {
        clearTimeout(detailAnchorRetryTimeoutRef.current);
        detailAnchorRetryTimeoutRef.current = null;
      }
    };
  }, [details, scrollToPendingDetailAnchor]);

  useEffect(() => () => {
    if (detailAnchorRetryTimeoutRef.current) {
      clearTimeout(detailAnchorRetryTimeoutRef.current);
    }
    if (detailAnchorRetryFrameRef.current != null) {
      cancelAnimationFrame(detailAnchorRetryFrameRef.current);
    }
  }, []);

  useEffect(() => {
    const nextQuery = detailSearch.trim();
    if (nextQuery === detailSearchQuery) {
      return;
    }
    const timeout = setTimeout(() => {
      setDetailsPage(1);
      setDetailSearchQuery(nextQuery);
    }, 350);
    return () => clearTimeout(timeout);
  }, [detailSearch, detailSearchQuery]);

  useEffect(() => {
    if (!restoreState) {
      return;
    }

    const restoreKey = JSON.stringify(
      buildLocalSupplierInvoicesReturnParams({
        returnInvoiceGuid: restoreState.returnInvoiceGuid,
        returnDetailGuid: restoreState.returnDetailGuid,
        returnDetailsPage: restoreState.returnDetailsPage,
        returnDetailsPageSize: restoreState.returnDetailsPageSize,
        returnDetailPriceChangeFilter: restoreState.returnDetailPriceChangeFilter,
        returnDetailSearch: restoreState.returnDetailSearch,
        returnListPage: restoreState.returnListPage,
        returnListPageSize: restoreState.returnListPageSize,
        filters: restoreState.filters,
        sort: restoreState.sort,
      })
    );

    if (handledRestoreKeyRef.current === restoreKey) {
      return;
    }

    handledRestoreKeyRef.current = restoreKey;
    const restoredFilters = bindDeviceStore(restoreState.filters);
    const listRequestKey = buildInvoiceListRequestKey({
      filters: restoredFilters,
      page: restoreState.returnListPage,
      pageSize: restoreState.returnListPageSize,
      sort: restoreState.sort,
    });
    pendingRestoreRef.current = {
      ...restoreState,
      filters: restoredFilters,
      listRequestKey,
      minimumListRequestId: listRequestIdRef.current + 1,
    };
    setDraftFilters(restoredFilters);
    setFilters(restoredFilters);
    setSort(restoreState.sort);
    setPageSize(restoreState.returnListPageSize);
    setPage(restoreState.returnListPage);
    detailRequestIdRef.current += 1;
    setSelectedInvoice(null);
    setDetails([]);
    setDetailsTotal(0);
    setDetailsPage(restoreState.returnDetailsPage);
    setDetailsPageSize(restoreState.returnDetailsPageSize);
    setDetailPriceChangeFilter(restoreState.returnDetailPriceChangeFilter);
    setDetailSearch(restoreState.returnDetailSearch);
    setDetailSearchQuery(restoreState.returnDetailSearch);
    pendingDetailAnchorRef.current = restoreState.returnDetailGuid ?? null;
    initialStoreScopeAppliedRef.current = true;
    setInitialStoreScopeReady(true);
  }, [bindDeviceStore, restoreState]);

  useEffect(() => {
    if (
      initialStoreScopeAppliedRef.current ||
      !isStoreSelectionReady ||
      restoreState ||
      isDeviceMode
    ) {
      return;
    }

    // 全局自动默认的分店不算用户选择，筛选仍默认全部分店
    const initialStoreCode = selectedStoreCode && !isSelectedStoreAuto && stores.some(
      (store) => store.storeCode === selectedStoreCode,
    )
      ? selectedStoreCode
      : undefined;
    const initialFilters = initialStoreCode ? { storeCode: initialStoreCode } : {};
    setDraftFilters(initialFilters);
    setFilters(initialFilters);
    initialStoreScopeAppliedRef.current = true;
    setInitialStoreScopeReady(true);
  }, [isDeviceMode, isSelectedStoreAuto, isStoreSelectionReady, restoreState, selectedStoreCode, stores]);

  useEffect(() => {
    if (!deviceBoundStoreCode) {
      return;
    }

    setDraftFilters((current) =>
      current.storeCode === deviceBoundStoreCode
        ? current
        : { ...current, storeCode: deviceBoundStoreCode }
    );
    setFilters((current) =>
      current.storeCode === deviceBoundStoreCode
        ? current
        : { ...current, storeCode: deviceBoundStoreCode }
    );
    initialStoreScopeAppliedRef.current = true;
    setInitialStoreScopeReady(true);
  }, [deviceBoundStoreCode]);

  useEffect(() => {
    const pendingRestore = pendingRestoreRef.current;
    if (
      !pendingRestore
      || !completedListRequest
      || completedListRequest.id < pendingRestore.minimumListRequestId
      || completedListRequest.key !== pendingRestore.listRequestKey
      || page !== pendingRestore.returnListPage
      || pageSize !== pendingRestore.returnListPageSize
      || JSON.stringify(filters) !== JSON.stringify(pendingRestore.filters)
      || JSON.stringify(sort) !== JSON.stringify(pendingRestore.sort)
    ) {
      return;
    }

    const matchedInvoice = items.find((item) => item.invoiceGuid === pendingRestore.returnInvoiceGuid);
    pendingRestoreRef.current = null;

    if (!matchedInvoice) {
      setSnackbar(t("messages.restoreInvoiceMissing"));
      return;
    }

    setSelectedInvoice(matchedInvoice);
    setDetailCounts({
      all: null,
      up: matchedInvoice.priceIncreaseItemCount,
      down: matchedInvoice.priceDecreaseItemCount,
    });
  }, [completedListRequest, filters, items, page, pageSize, sort, t]);

  const applyFilters = useCallback(() => {
    setPage(1);
    setFilters(bindDeviceStore(draftFilters));
  }, [bindDeviceStore, draftFilters]);

  const applyInboundStatus = useCallback((inboundStatus?: 0 | 1 | 2) => {
    const nextFilters = bindDeviceStore({ ...filters, inboundStatus });
    setDraftFilters((current) => ({ ...current, inboundStatus }));
    setFilters(nextFilters);
    setPage(1);
  }, [bindDeviceStore, filters]);

  const clearFilters = useCallback(() => {
    const emptyFilters = bindDeviceStore({});
    setDraftFilters(emptyFilters);
    setFilters(emptyFilters);
    setPage(1);
  }, [bindDeviceStore]);

  const openDetails = useCallback((invoice: LocalSupplierInvoice) => {
    detailRequestIdRef.current += 1;
    detailCountCacheRef.current.clear();
    setSelectedInvoice(invoice);
    setDetails([]);
    setDetailsTotal(0);
    setDetailCounts({
      all: null,
      up: invoice.priceIncreaseItemCount,
      down: invoice.priceDecreaseItemCount,
    });
    setDetailsPage(1);
    setDetailsPageSize(50);
    setDetailPriceChangeFilter("all");
    setDetailSearch("");
    setDetailSearchQuery("");
    setInvoiceInfoExpanded(false);
    pendingDetailAnchorRef.current = null;
  }, []);

  const closeDetails = useCallback(() => {
    detailRequestIdRef.current += 1;
    setDetailsLoading(false);
    setSelectedInvoice(null);
  }, []);

  const copyValue = useCallback(
    async (label: string, value: string) => {
      if (!value.trim()) {
        return;
      }

      try {
        await Clipboard.setStringAsync(value);
        setSnackbar(t("messages.copied", { label }));
      } catch {
        setSnackbar(t("messages.copyFailed"));
      }
    },
    [t]
  );

  const openProduct = useCallback(
    (detail: LocalSupplierInvoiceItem) => {
      const keyword = detail.barcode || detail.itemNumber || detail.productCode;
      if (!keyword) {
        setSnackbar(t("messages.missingProductKeyword"));
        return;
      }

      try {
        const pushParams = {
          pathname: "/(shell)/product-query",
          params: {
            productCode: detail.productCode,
            keyword,
            storeCode: detail.storeCode || selectedInvoice?.storeCode || "",
            ...buildLocalSupplierInvoicesReturnParams({
              returnInvoiceGuid: detail.invoiceGuid || selectedInvoice?.invoiceGuid || "",
              returnDetailGuid: detail.detailGuid,
              returnDetailsPage: detailsPage,
              returnDetailsPageSize: detailsPageSize,
              returnDetailPriceChangeFilter: detailPriceChangeFilter,
              returnDetailSearch: detailSearchQuery,
              returnListPage: page,
              returnListPageSize: pageSize,
              filters,
              sort,
            }),
          },
        } as Parameters<typeof router.push>[0];

        detailRequestIdRef.current += 1;
        detailCountCacheRef.current.clear();
        setSelectedInvoice(null);

        const navigate = () => {
          router.push(pushParams);
        };

        if (typeof requestAnimationFrame === "function") {
          requestAnimationFrame(navigate);
        } else {
          setTimeout(navigate, 0);
        }
      } catch (error) {
        setSnackbar(getErrorMessage(error, "messages.productOpenFailed"));
      }
    },
    [detailPriceChangeFilter, detailSearchQuery, detailsPage, detailsPageSize, filters, getErrorMessage, page, pageSize, router, selectedInvoice?.invoiceGuid, selectedInvoice?.storeCode, sort, t]
  );

  const handleCopyDetailValue = useCallback(
    (label: string, value: string) => void copyValue(label, value),
    [copyValue]
  );
  const renderInvoiceDetailItem = useCallback(
    ({ item }: { item: LocalSupplierInvoiceItem }) => (
      <InvoiceDetailRow
        detail={item}
        onCopy={handleCopyDetailValue}
        onOpenProduct={openProduct}
        renderImage={visibleDetailGuids.has(item.detailGuid)}
        t={t}
      />
    ),
    [handleCopyDetailValue, openProduct, t, visibleDetailGuids]
  );
  const detailListExtraData = useMemo(
    () => ({ visibleDetailGuids }),
    [visibleDetailGuids]
  );

  const openSupplierPicker = useCallback(() => {
    setSupplierPickerVisible(true);
    if (!suppliersLoaded) {
      void loadSuppliers();
    }
  }, [loadSuppliers, suppliersLoaded]);

  const handleSelectStore = useCallback((store: Store | null) => {
    setDraftFilters((current) => ({
      ...current,
      storeCode: deviceBoundStoreCode ?? store?.storeCode ?? undefined,
    }));
    setStorePickerVisible(false);
  }, [deviceBoundStoreCode]);

  const handleSelectSupplier = useCallback((supplierCode?: string) => {
    setDraftFilters((current) => ({
      ...current,
      supplierCode: supplierCode || undefined,
    }));
    setSupplierPickerVisible(false);
  }, []);

  const updateOrderDateRange = useCallback((nextRange: InvoiceDateRangeValue) => {
    const nextFilters = toInvoiceOrderDateFilters(nextRange);
    setDraftFilters((current) => ({
      ...current,
      orderDateFrom: nextFilters.orderDateFrom,
      orderDateTo: nextFilters.orderDateTo,
    }));
  }, []);

  const openDateRangeModal = useCallback(() => {
    const from = normalizeMonthDate(draftFilters.orderDateFrom);
    const to = normalizeMonthDate(draftFilters.orderDateTo);
    setDateRangeSnapshot({ from, to });
    const displayMonth = parseMonthDate(from || to) ?? new Date();
    setDateRangeDisplayMonth(getMonthStart(displayMonth));
    setDateRangeModalVisible(true);
  }, [draftFilters.orderDateFrom, draftFilters.orderDateTo]);

  const closeDateRangeModal = useCallback(() => {
    setDateRangeModalVisible(false);
    setDateRangeSnapshot(null);
  }, []);

  const cancelDateRangeModal = useCallback(() => {
    if (dateRangeSnapshot) {
      updateOrderDateRange(dateRangeSnapshot);
    }
    closeDateRangeModal();
  }, [closeDateRangeModal, dateRangeSnapshot, updateOrderDateRange]);

  const applyDateRangeModal = useCallback(() => {
    closeDateRangeModal();
  }, [closeDateRangeModal]);

  const clearDateRange = useCallback(() => {
    updateOrderDateRange(clearInvoiceDateRange());
  }, [updateOrderDateRange]);

  const selectRangeDate = useCallback(
    (dateString: string) => {
      const from = normalizeMonthDate(draftFilters.orderDateFrom);
      const to = normalizeMonthDate(draftFilters.orderDateTo);
      updateOrderDateRange(selectInvoiceDateRange({ from, to }, dateString));

      setDateRangeDisplayMonth(getMonthStart(parseMonthDate(dateString) ?? new Date()));
    },
    [draftFilters.orderDateFrom, draftFilters.orderDateTo, updateOrderDateRange]
  );

  const renderPagination = (
    currentPage: number,
    pageCount: number,
    onChangePage: (page: number) => void
  ) => (
    <View style={styles.pagination}>
      <Button
        compact
        disabled={currentPage <= 1}
        icon="chevron-left"
        mode="outlined"
        onPress={() => onChangePage(Math.max(1, currentPage - 1))}
      >
        {t("common:actions.back")}
      </Button>
      <Text variant="bodyMedium">
        {currentPage} / {pageCount}
      </Text>
      <Button
        compact
        contentStyle={styles.nextButtonContent}
        disabled={currentPage >= pageCount}
        icon="chevron-right"
        mode="outlined"
        onPress={() => onChangePage(Math.min(pageCount, currentPage + 1))}
      >
        {t("actions.loadMore")}
      </Button>
    </View>
  );

  const renderDetailPagination = () => (
    <View style={styles.detailPagination}>
      <IconButton
        accessibilityLabel={t("common:actions.back")}
        disabled={detailsPage <= 1}
        icon="chevron-left"
        onPress={() => setDetailsPage(Math.max(1, detailsPage - 1))}
        size={18}
        style={styles.detailPaginationButton}
      />
      <Text variant="bodySmall" style={styles.detailPaginationText}>{detailsPage} / {detailsPageCount}</Text>
      <IconButton
        accessibilityLabel={t("actions.loadMore")}
        disabled={detailsPage >= detailsPageCount}
        icon="chevron-right"
        onPress={() => setDetailsPage(Math.min(detailsPageCount, detailsPage + 1))}
        size={18}
        style={styles.detailPaginationButton}
      />
    </View>
  );

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.container}
        refreshControl={
          <RefreshControl refreshing={refreshing} onRefresh={() => void loadInvoices(true)} />
        }
      >
        <View style={styles.header}>
          <Text variant="headlineSmall" style={styles.title}>
            {t("title")}
          </Text>
          <Text variant="bodyMedium" style={styles.subtitle}>
            {t("subtitle")}
          </Text>
          <View style={styles.scopeBar}>
            <IconButton icon="store-outline" size={18} style={styles.scopeIcon} />
            <Text variant="labelLarge" style={styles.scopeText}>
              {filters.storeCode
                ? appliedStore?.storeName || filters.storeCode
                : t("filters.allStores")}
            </Text>
            <Text variant="bodySmall" style={styles.scopeHint}>
              {isDeviceMode ? t("filters.fixedStore") : t("filters.managedStores")}
            </Text>
          </View>
        </View>

        <View style={styles.quickSearchRow}>
          <TextInput
            dense
            mode="outlined"
            placeholder={t("filters.invoiceSearchPlaceholder")}
            value={draftFilters.invoiceNo ?? ""}
            onChangeText={(value) => setDraftFilters((current) => ({ ...current, invoiceNo: value }))}
            onSubmitEditing={applyFilters}
            left={<TextInput.Icon icon="magnify" />}
            right={<TextInput.Icon icon="arrow-right" onPress={applyFilters} />}
            style={styles.quickSearchInput}
          />
          <IconButton
            accessibilityLabel={t("filters.moreFilters")}
            icon={filtersExpanded ? "filter-minus-outline" : "filter-variant"}
            mode={filtersExpanded ? "contained" : "outlined"}
            selected={filtersExpanded}
            onPress={() => setFiltersExpanded((visible) => !visible)}
            style={styles.quickFilterButton}
          />
        </View>

        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.inboundTabs}
        >
          {([
            { value: undefined, label: t("filters.allInboundStatuses") },
            { value: 0 as const, label: t("labels.inboundStatusValues.notReceived") },
            { value: 1 as const, label: t("labels.inboundStatusValues.partial") },
            { value: 2 as const, label: t("labels.inboundStatusValues.received") },
          ]).map((option) => {
            const selected = filters.inboundStatus === option.value;
            return (
              <Button
                key={option.value ?? "all"}
                compact
                mode={selected ? "contained" : "text"}
                onPress={() => applyInboundStatus(option.value)}
                style={styles.inboundTab}
              >
                {option.label}
              </Button>
            );
          })}
        </ScrollView>

        {filtersExpanded ? <View style={styles.filterPanel}>
          <View style={styles.filterGrid}>
            <Pressable
              disabled={Boolean(deviceBoundStoreCode)}
              onPress={() => setStorePickerVisible(true)}
              style={[styles.filterInput, deviceBoundStoreCode ? styles.disabledField : null]}
            >
              <Surface style={styles.pickerField} elevation={0}>
                <View style={styles.pickerFieldText}>
                  <Text variant="labelMedium" style={styles.pickerFieldLabel}>
                    {t("filters.storeCode")}
                  </Text>
                  <View style={styles.pickerTagRow}>
                    <EntityTag
                      label={selectedStore?.storeName || draftFilters.storeCode || t("filters.allStores")}
                      tone={draftFilters.storeCode ? "store" : "neutral"}
                    />
                  </View>
                </View>
                {storesLoading ? (
                  <ActivityIndicator size="small" />
                ) : (
                  <IconButton icon="store-outline" size={20} />
                )}
              </Surface>
            </Pressable>
            <Pressable onPress={openSupplierPicker} style={styles.filterInput}>
              <Surface style={styles.pickerField} elevation={0}>
                <View style={styles.pickerFieldText}>
                  <Text variant="labelMedium" style={styles.pickerFieldLabel}>
                    {t("filters.supplierCode")}
                  </Text>
                  <View style={styles.pickerTagRow}>
                    <EntityTag
                      label={selectedSupplier?.supplierName || draftFilters.supplierCode || t("filters.allSuppliers")}
                      tone={draftFilters.supplierCode ? "supplier" : "neutral"}
                    />
                  </View>
                </View>
                {suppliersLoading ? (
                  <ActivityIndicator size="small" />
                ) : (
                  <IconButton icon="truck-outline" size={20} />
                )}
              </Surface>
            </Pressable>
            <Pressable onPress={openDateRangeModal} style={styles.filterInput}>
              <Surface style={styles.pickerField} elevation={0}>
                <View style={styles.pickerFieldText}>
                  <Text variant="labelMedium" style={styles.pickerFieldLabel}>
                    {t("filters.orderDateRange")}
                  </Text>
                  <Text
                    variant="bodyLarge"
                    numberOfLines={1}
                    style={
                      draftFilters.orderDateFrom || draftFilters.orderDateTo
                        ? undefined
                        : styles.pickerPlaceholder
                    }
                  >
                    {orderDateRangeLabel}
                  </Text>
                </View>
                <IconButton icon="calendar-month-outline" size={20} />
              </Surface>
            </Pressable>
          </View>

          <View style={styles.filterActions}>
            <PageSizeMenu
              label={t("filters.pageSize")}
              options={LIST_PAGE_SIZES}
              value={pageSize}
              onChange={(value) => {
                setPageSize(value);
                setPage(1);
              }}
            />
            <Menu
              visible={sortMenuVisible}
              onDismiss={() => setSortMenuVisible(false)}
              anchor={
                <Button compact icon="sort" mode="outlined" onPress={() => setSortMenuVisible(true)}>
                  {sortLabel}
                </Button>
              }
            >
              {SORT_OPTIONS.map((option) => (
                <Menu.Item
                  key={`${option.colId}-${option.direction}`}
                  onPress={() => {
                    setSort({ colId: option.colId, direction: option.direction });
                    setPage(1);
                    setSortMenuVisible(false);
                  }}
                  title={t(option.labelKey)}
                />
              ))}
            </Menu>
            <Button compact icon="filter-check" mode="contained" onPress={applyFilters}>
              {t("common:actions.search")}
            </Button>
            <Button compact icon="filter-remove" mode="outlined" onPress={clearFilters}>
              {t("common:actions.clear")}
            </Button>
          </View>
        </View> : null}

        {!loading && items.length ? (
          <View style={styles.listSummary}>
            <Text variant="labelLarge" style={styles.listSummaryText}>
              {t("labels.invoiceCount", { count: total })}
            </Text>
            <Text variant="bodySmall" style={styles.listSummaryCurrency}>AUD</Text>
          </View>
        ) : null}

        {loading ? (
          <View style={styles.loadingBox}>
            <ActivityIndicator />
            <Text variant="bodyMedium">{t("common:loading")}</Text>
          </View>
        ) : items.length ? (
          <View style={styles.invoiceList}>
            {items.map((invoice) => {
              const inboundStatusLabel = getInvoiceInboundStatusLabel(invoice.inboundStatus);
              return (
              <Card key={invoice.invoiceGuid} mode="contained" style={styles.invoiceCard}>
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={`${invoice.supplierName || invoice.supplierCode}, ${invoice.invoiceNo}`}
                  onPress={() => openDetails(invoice)}
                  style={({ pressed }) => [styles.invoiceRow, pressed ? styles.invoiceRowPressed : null]}
                >
                  <View style={styles.invoiceRowMain}>
                    <View style={styles.invoiceHeadingRow}>
                      <Text variant="titleMedium" style={styles.invoiceSupplier} numberOfLines={1}>
                        {invoice.supplierName || invoice.supplierCode || "--"}
                      </Text>
                      <Text variant="titleMedium" style={styles.invoiceAmount}>
                        {formatMoney(invoice.totalAmount)}
                      </Text>
                    </View>
                    <View style={styles.invoiceIdentityRow}>
                      <Text variant="bodyMedium" style={styles.invoiceNumber} numberOfLines={1}>
                        {invoice.invoiceNo || invoice.invoiceGuid}
                      </Text>
                      <View style={[
                        styles.inboundStatusBadge,
                        inboundStatusLabel === "received" ? styles.inboundStatusBadgeSuccess : null,
                        inboundStatusLabel === "partial" ? styles.inboundStatusBadgeWarning : null,
                      ]}>
                        <Text
                          variant="labelSmall"
                          style={[
                            styles.inboundStatusBadgeText,
                            inboundStatusLabel === "received" ? styles.inboundStatusBadgeTextSuccess : null,
                            inboundStatusLabel === "partial" ? styles.inboundStatusBadgeTextWarning : null,
                          ]}
                        >
                          {t(`labels.inboundStatusValues.${inboundStatusLabel}`)}
                        </Text>
                      </View>
                    </View>
                    <Text variant="bodySmall" style={styles.invoiceSecondaryMeta} numberOfLines={1}>
                      {invoice.storeName || invoice.storeCode || "--"} · {formatDate(invoice.orderDate)}
                    </Text>
                    <View style={styles.invoiceFooterRow}>
                      <Text variant="bodySmall" style={styles.invoiceSecondaryMeta}>
                        {t("labels.receivedAmount")}: {formatMoney(invoice.receivedTotalAmount)}
                      </Text>
                      {invoice.priceIncreaseItemCount > 0 ? (
                        <Text variant="labelMedium" style={styles.priceIncreaseText}>
                          {t("labels.priceIncrease")}: {formatNumber(invoice.priceIncreaseItemCount)}
                        </Text>
                      ) : null}
                      {invoice.priceDecreaseItemCount > 0 ? (
                        <Text variant="labelMedium" style={styles.priceDecreaseText}>
                          {t("labels.priceDecrease")}: {formatNumber(invoice.priceDecreaseItemCount)}
                        </Text>
                      ) : null}
                    </View>
                  </View>
                  <View style={styles.invoiceChevron}>
                    <Text style={styles.invoiceChevronText}>›</Text>
                  </View>
                </Pressable>
              </Card>
              );
            })}
          </View>
        ) : (
          <EmptyState
            title={t("messages.empty")}
            primaryAction={{ label: t("common:actions.refresh"), icon: "refresh", onPress: () => void loadInvoices() }}
          />
        )}

        {items.length ? renderPagination(page, listPageCount, setPage) : null}
      </ScrollView>

      <Portal>
        <Modal
          visible={Boolean(selectedInvoice)}
          onDismiss={closeDetails}
          style={styles.detailModalOverlay}
          contentContainerStyle={styles.modal}
        >
          <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.detailScreen}>
            <View style={styles.detailNavigationBar}>
              <IconButton
                accessibilityLabel={t("actions.returnToInvoices")}
                icon="chevron-left"
                onPress={closeDetails}
                style={styles.detailNavigationButton}
              />
              <Text variant="titleLarge" style={styles.detailNavigationTitle}>{t("detailTitle")}</Text>
              <DetailPageSizeMenu
                label={t("labels.detailPageSize")}
                options={DETAIL_PAGE_SIZES}
                value={detailsPageSize}
                onChange={(value) => {
                  setDetailsPageSize(value);
                  setDetailsPage(1);
                }}
              />
            </View>

            <View style={styles.detailInvoiceCard}>
              <View style={styles.detailInvoiceHeadingRow}>
                <View style={styles.detailStoreIcon}><Icon source="storefront-outline" size={20} color="#1677FF" /></View>
                <View style={styles.detailInvoiceIdentity}>
                  <Text variant="titleMedium" style={styles.detailSupplierName} numberOfLines={1}>
                    {selectedInvoice?.supplierName || selectedInvoice?.supplierCode || "--"}
                  </Text>
                  <Text variant="bodySmall" style={styles.detailContextText} numberOfLines={1}>
                    {selectedInvoice?.invoiceNo || "--"} · {selectedInvoice?.storeName || selectedInvoice?.storeCode || "--"}
                  </Text>
                </View>
                <View style={[
                  styles.inboundStatusBadge,
                  getInvoiceInboundStatusLabel(selectedInvoice?.inboundStatus) === "received" ? styles.inboundStatusBadgeSuccess : null,
                  getInvoiceInboundStatusLabel(selectedInvoice?.inboundStatus) === "partial" ? styles.inboundStatusBadgeWarning : null,
                ]}>
                  <Text style={[
                    styles.inboundStatusBadgeText,
                    getInvoiceInboundStatusLabel(selectedInvoice?.inboundStatus) === "received" ? styles.inboundStatusBadgeTextSuccess : null,
                    getInvoiceInboundStatusLabel(selectedInvoice?.inboundStatus) === "partial" ? styles.inboundStatusBadgeTextWarning : null,
                  ]}>
                    {t(`labels.inboundStatusValues.${getInvoiceInboundStatusLabel(selectedInvoice?.inboundStatus)}`)}
                  </Text>
                </View>
              </View>

              <View style={styles.detailInvoiceMetaRow}>
                <Text variant="bodySmall" style={styles.detailContextText} numberOfLines={1}>
                  {t("labels.orderDate")} {formatDate(selectedInvoice?.orderDate)}
                </Text>
                <Pressable
                  accessibilityRole="button"
                  accessibilityState={{ expanded: invoiceInfoExpanded }}
                  onPress={() => setInvoiceInfoExpanded((current) => !current)}
                  style={styles.detailInfoToggle}
                >
                  <Text variant="bodySmall" style={styles.detailInfoToggleText}>{t("labels.invoiceInfo")}</Text>
                  <Text style={styles.detailInfoChevron}>{invoiceInfoExpanded ? "⌃" : "⌄"}</Text>
                </Pressable>
              </View>
              {invoiceInfoExpanded ? (
                <ScrollView
                  contentContainerStyle={styles.detailInfoPanelContent}
                  nestedScrollEnabled
                  showsVerticalScrollIndicator
                  style={styles.detailInfoPanel}
                >
                  <Text variant="bodySmall" style={styles.detailInfoText}>{t("labels.inboundDate")}: {formatDate(selectedInvoice?.inboundDate)}</Text>
                  <Text variant="bodySmall" style={styles.detailInfoText}>{t("labels.remarks")}: {selectedInvoice?.remarks || "--"}</Text>
                </ScrollView>
              ) : null}

              <View style={styles.detailAmountGrid}>
                <View style={styles.detailAmountCell}>
                  <Text variant="labelSmall" style={styles.detailMetricLabel}>{t("labels.invoiceAmount")}</Text>
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.72}
                    numberOfLines={1}
                    variant="titleMedium"
                    style={styles.detailAmountValue}
                  >
                    {formatMoney(selectedInvoice?.totalAmount)}
                  </Text>
                </View>
                <View style={styles.detailAmountCell}>
                  <Text variant="labelSmall" style={styles.detailMetricLabel}>{t("labels.receivedAmount")}</Text>
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.72}
                    numberOfLines={1}
                    variant="titleMedium"
                    style={styles.detailAmountValue}
                  >
                    {formatMoney(selectedInvoice?.receivedTotalAmount)}
                  </Text>
                </View>
                <View style={[styles.detailAmountCell, styles.detailAmountCellLast]}>
                  <Text variant="labelSmall" style={styles.detailMetricLabel}>
                    {t("labels.detailsCount", { count: detailCounts.all ?? "--" })}
                  </Text>
                  <Text
                    accessibilityLabel={t("labels.detailSummary", {
                      count: detailCounts.all ?? "--",
                      up: detailCounts.up ?? "--",
                      down: detailCounts.down ?? "--",
                    })}
                    adjustsFontSizeToFit
                    minimumFontScale={0.72}
                    numberOfLines={1}
                    variant="bodyMedium"
                    style={styles.detailCountValue}
                  >
                    {t("labels.priceChangeSummary", {
                      up: detailCounts.up ?? "--",
                      down: detailCounts.down ?? "--",
                    })}
                  </Text>
                </View>
              </View>
            </View>

            <TextInput
              dense
              mode="outlined"
              placeholder={t("filters.detailSearchFull")}
              value={detailSearch}
              onChangeText={setDetailSearch}
              left={<TextInput.Icon icon="magnify" />}
              style={styles.detailSearch}
            />

            <View style={styles.detailFilterTabs}>
              {DETAIL_PRICE_CHANGE_OPTIONS.map((option) => {
                const selected = detailPriceChangeFilter === option.value;
                const count = detailCounts[option.value] ?? "--";
                return (
                  <Pressable
                    key={option.value}
                    accessibilityRole="button"
                    accessibilityState={{ selected }}
                    onPress={() => {
                      setDetailPriceChangeFilter(option.value);
                      setDetailsPage(1);
                    }}
                    style={[styles.detailFilterTab, selected ? styles.detailFilterTabSelected : null]}
                  >
                    <Text variant="bodyMedium" style={[styles.detailFilterTabText, selected ? styles.detailFilterTabTextSelected : null]}>
                      {t(option.labelKey)} {count}
                    </Text>
                  </Pressable>
                );
              })}
            </View>

            {detailsLoading ? (
              <View style={styles.loadingBox}>
                <ActivityIndicator />
                <Text variant="bodyMedium">{t("common:loading")}</Text>
              </View>
            ) : details.length ? (
              <FlatList
                ref={detailListScrollRef}
                data={details}
                keyExtractor={(detail) => detail.detailGuid}
                renderItem={renderInvoiceDetailItem}
                extraData={detailListExtraData}
                style={styles.detailListScroll}
                contentContainerStyle={styles.detailList}
                initialNumToRender={6}
                maxToRenderPerBatch={6}
                windowSize={7}
                keyboardShouldPersistTaps="handled"
                removeClippedSubviews={Platform.OS === "android"}
                onViewableItemsChanged={onDetailViewableItemsChanged.current}
                viewabilityConfig={detailViewabilityConfig.current}
                onContentSizeChange={() => {
                  if (pendingDetailAnchorRef.current) {
                    if (detailAnchorRetryFrameRef.current != null) {
                      cancelAnimationFrame(detailAnchorRetryFrameRef.current);
                    }
                    detailAnchorRetryFrameRef.current = requestAnimationFrame(() => {
                      detailAnchorRetryFrameRef.current = null;
                      scrollToPendingDetailAnchor();
                    });
                  }
                }}
                onScrollToIndexFailed={({ averageItemLength, index }) => {
                  detailListScrollRef.current?.scrollToOffset({
                    animated: false,
                    offset: Math.max(0, averageItemLength * index),
                  });
                  if (pendingDetailAnchorRef.current && detailAnchorRetryRef.current < 4) {
                    detailAnchorRetryRef.current += 1;
                    if (detailAnchorRetryTimeoutRef.current) {
                      clearTimeout(detailAnchorRetryTimeoutRef.current);
                    }
                    // 给 FlatList 一个批次的渲染和测量时间，再执行精确定位。
                    detailAnchorRetryTimeoutRef.current = setTimeout(() => {
                      detailAnchorRetryTimeoutRef.current = null;
                      scrollToPendingDetailAnchor();
                    }, 80 * detailAnchorRetryRef.current);
                  }
                }}
                ListFooterComponent={(
                  <>
                    {renderDetailPagination()}
                    <View style={styles.detailReturnBar}>
                      <Button icon="chevron-left" mode="contained-tonal" onPress={closeDetails} style={styles.detailReturnButton} textColor="#1677FF">
                        {t("actions.returnToInvoices")}
                      </Button>
                    </View>
                  </>
                )}
              />
            ) : (
              <EmptyState title={detailSearchQuery ? t("messages.detailSearchEmpty") : t("messages.detailsEmpty")} />
            )}

          </SafeAreaView>
        </Modal>
      </Portal>

      <StorePickerModal
        presentation="sheet"
        visible={storePickerVisible}
        stores={stores}
        selectedStoreCode={draftFilters.storeCode ?? null}
        title={t("filters.storePickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        includeAllOption={!deviceBoundStoreCode}
        allLabel={t("filters.allStores")}
        renderAllLabel={(label) => <EntityTag label={label} tone="neutral" />}
        renderStoreLabel={(store) => (
          <EntityTag label={store.storeName || store.storeCode} tone="store" />
        )}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={handleSelectStore}
      />

      <Portal>
        <Modal
          visible={supplierPickerVisible}
          onDismiss={() => setSupplierPickerVisible(false)}
          contentContainerStyle={styles.pickerModal}
        >
          <View style={styles.pickerModalHeader}>
            <Text variant="titleMedium">{t("filters.supplierPickerTitle")}</Text>
            <Button onPress={() => setSupplierPickerVisible(false)}>
              {t("common:actions.cancel")}
            </Button>
          </View>

          <ScrollView
            style={styles.pickerModalList}
            contentContainerStyle={styles.pickerModalListContent}
          >
            <View style={styles.pickerRow}>
              <RadioButton
                value="all-suppliers"
                status={!draftFilters.supplierCode ? "checked" : "unchecked"}
                onPress={() => handleSelectSupplier()}
              />
              <Pressable
                accessibilityRole="button"
                onPress={() => handleSelectSupplier()}
                style={styles.pickerRowButton}
              >
                <EntityTag label={t("filters.allSuppliers")} tone="neutral" />
              </Pressable>
            </View>

            {suppliersLoading ? (
              <View style={styles.loadingBox}>
                <ActivityIndicator />
                <Text variant="bodyMedium">{t("messages.suppliersLoading")}</Text>
              </View>
            ) : suppliers.length ? (
              suppliers.map((supplier) => (
                <View key={supplier.supplierCode} style={styles.pickerRow}>
                  <RadioButton
                    value={supplier.supplierCode}
                    status={
                      draftFilters.supplierCode === supplier.supplierCode
                        ? "checked"
                        : "unchecked"
                    }
                    onPress={() => handleSelectSupplier(supplier.supplierCode)}
                  />
                  <Pressable
                    accessibilityRole="button"
                    onPress={() => handleSelectSupplier(supplier.supplierCode)}
                    style={styles.pickerRowButton}
                  >
                    <EntityTag label={supplier.supplierName} tone="supplier" />
                  </Pressable>
                </View>
              ))
            ) : (
              <View style={styles.emptyPickerState}>
                <Text variant="bodyMedium">{t("messages.suppliersEmpty")}</Text>
                <Button icon="refresh" mode="outlined" onPress={() => void loadSuppliers()}>
                  {t("common:actions.retry")}
                </Button>
              </View>
            )}
          </ScrollView>
        </Modal>
      </Portal>

      <Portal>
        <Modal
          visible={dateRangeModalVisible}
          onDismiss={cancelDateRangeModal}
          contentContainerStyle={styles.dateModal}
        >
          <View style={styles.pickerModalHeader}>
            <Text variant="titleMedium">{t("filters.dateRangeTitle")}</Text>
            <Button onPress={cancelDateRangeModal}>
              {t("common:actions.cancel")}
            </Button>
          </View>
          <View style={styles.calendarHeader}>
            <IconButton
              icon="chevron-left"
              size={22}
              onPress={() => setDateRangeDisplayMonth((current) => addMonths(current, -1))}
            />
            <Text variant="titleMedium" style={styles.calendarMonthTitle}>
              {monthTitle}
            </Text>
            <IconButton
              icon="chevron-right"
              size={22}
              onPress={() => setDateRangeDisplayMonth((current) => addMonths(current, 1))}
            />
          </View>
          <View style={styles.weekdayRow}>
            {weekdayLabels.map((label, index) => (
              <Text key={`${label}-${index}`} variant="labelSmall" style={styles.weekdayText}>
                {label}
              </Text>
            ))}
          </View>
          <View style={styles.calendarGrid}>
            {calendarWeeks.map((week, weekIndex) => (
              <View key={weekIndex} style={styles.calendarWeekRow}>
                {week.map((cell) => {
                  const from = normalizeMonthDate(draftFilters.orderDateFrom);
                  const to = normalizeMonthDate(draftFilters.orderDateTo);
                  const isSelectedStart = Boolean(from && cell.dateString === from);
                  const isSelectedEnd = Boolean(to && cell.dateString === to);
                  const isSingleSelected = isSelectedStart && !to;
                  const isInRange = isDateInRange(cell.dateString, from, to);
                  const isToday = cell.dateString === today;

                  return (
                    <Pressable
                      key={cell.dateString}
                      accessibilityRole="button"
                      accessibilityState={{ selected: isSelectedStart || isSelectedEnd || isInRange }}
                      onPress={() => selectRangeDate(cell.dateString)}
                      style={({ pressed }) => [
                        styles.calendarDateCell,
                        !cell.isCurrentMonth ? styles.calendarDateCellOutside : null,
                        isInRange ? styles.calendarDateCellInRange : null,
                        isToday ? styles.calendarDateCellToday : null,
                        isSelectedStart || isSelectedEnd ? styles.calendarDateCellSelected : null,
                        isSingleSelected ? styles.calendarDateCellSingleSelected : null,
                        pressed ? styles.calendarDateCellPressed : null,
                      ]}
                    >
                      <Text
                        variant="labelLarge"
                        style={[
                          styles.calendarDateText,
                          !cell.isCurrentMonth ? styles.calendarDateTextOutside : null,
                          isSelectedStart || isSelectedEnd ? styles.calendarDateTextSelected : null,
                        ]}
                      >
                        {cell.date.getDate()}
                      </Text>
                    </Pressable>
                  );
                })}
              </View>
            ))}
          </View>
          <View style={styles.dateModalActions}>
            <Button
              icon="close-circle-outline"
              mode="text"
              onPress={clearDateRange}
            >
              {t("common:actions.clear")}
            </Button>
            <Button icon="check" mode="contained" onPress={applyDateRangeModal}>
              {t("filters.applyDateRange")}
            </Button>
          </View>
        </Modal>
      </Portal>

      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500}>
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  container: {
    gap: 10,
    padding: 16,
    paddingTop: 12,
    paddingBottom: 24,
  },
  header: {
    gap: 4,
  },
  title: {
    fontWeight: "700",
  },
  subtitle: {
    color: "#667085",
  },
  scopeBar: {
    alignItems: "center",
    backgroundColor: "#EAF2FF",
    borderColor: "#B8D4FF",
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: 2,
    minHeight: 42,
    paddingRight: 10,
  },
  scopeIcon: { margin: 0 },
  scopeText: { color: "#175CD3", flexShrink: 1 },
  scopeHint: { color: "#667085", marginLeft: "auto" },
  quickSearchRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  quickSearchInput: {
    flex: 1,
    backgroundColor: "#FFFFFF",
  },
  quickFilterButton: {
    width: 48,
    height: 48,
    margin: 0,
  },
  inboundTabs: {
    alignItems: "center",
    gap: 4,
    minHeight: 42,
    paddingRight: 8,
  },
  inboundTab: {
    borderRadius: 8,
  },
  filterPanel: {
    backgroundColor: "#FFFFFF",
    borderColor: "#E4E7EC",
    borderRadius: 12,
    borderWidth: 1,
    gap: 12,
    padding: 12,
  },
  filterGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  filterInput: {
    flexGrow: 1,
    minWidth: 150,
  },
  disabledField: { opacity: 0.8 },
  filterActions: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  pickerField: {
    alignItems: "center",
    backgroundColor: "#FFFFFF",
    borderColor: "#79747E",
    borderRadius: 4,
    borderWidth: 1,
    flexDirection: "row",
    minHeight: 56,
    paddingLeft: 16,
  },
  pickerFieldLabel: {
    color: "#49454F",
  },
  pickerFieldText: {
    flex: 1,
    gap: 2,
    minWidth: 0,
    paddingVertical: 8,
  },
  pickerTagRow: {
    alignItems: "flex-start",
    minWidth: 0,
  },
  pickerPlaceholder: {
    color: "#9CA3AF",
  },
  loadingBox: {
    alignItems: "center",
    gap: 8,
    justifyContent: "center",
    padding: 28,
  },
  invoiceList: {
    gap: 1,
  },
  invoiceCard: {
    backgroundColor: "#FFFFFF",
    borderRadius: 0,
  },
  invoiceRow: {
    minHeight: 126,
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  invoiceRowPressed: {
    backgroundColor: "#F2F4F7",
  },
  invoiceRowMain: {
    flex: 1,
    minWidth: 0,
    gap: 5,
  },
  invoiceHeadingRow: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 10,
  },
  invoiceSupplier: {
    flex: 1,
    minWidth: 0,
    color: "#101828",
    fontWeight: "700",
  },
  invoiceAmount: {
    color: "#101828",
    fontWeight: "800",
  },
  invoiceIdentityRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  invoiceNumber: {
    flexShrink: 1,
    color: "#475467",
  },
  inboundStatusBadge: {
    borderRadius: 6,
    backgroundColor: "#FFF1E8",
    paddingHorizontal: 7,
    paddingVertical: 2,
  },
  inboundStatusBadgeSuccess: {
    backgroundColor: "#E9F8EF",
  },
  inboundStatusBadgeWarning: {
    backgroundColor: "#FFF8E1",
  },
  inboundStatusBadgeText: {
    color: "#9A3412",
    fontWeight: "700",
  },
  inboundStatusBadgeTextSuccess: {
    color: "#067647",
  },
  inboundStatusBadgeTextWarning: {
    color: "#B54708",
  },
  invoiceSecondaryMeta: {
    color: "#667085",
  },
  invoiceFooterRow: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 10,
  },
  invoiceChevron: {
    width: 24,
    alignItems: "flex-end",
  },
  invoiceChevronText: {
    color: "#667085",
    fontSize: 28,
    lineHeight: 30,
  },
  listSummary: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    paddingHorizontal: 4,
    paddingVertical: 2,
  },
  listSummaryText: {
    color: "#475467",
  },
  listSummaryCurrency: {
    color: "#667085",
  },
  invoiceContent: {
    gap: 8,
  },
  invoiceMeta: {
    gap: 4,
  },
  priceIncreaseText: {
    color: "#B42318",
    fontWeight: "700",
  },
  priceDecreaseText: {
    color: "#027A48",
    fontWeight: "700",
  },
  statusText: { color: "#475467" },
  entityTagRow: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
  },
  entityTag: {
    alignSelf: "flex-start",
    borderRadius: 999,
    borderWidth: 1,
    maxWidth: "100%",
    paddingHorizontal: 10,
    paddingVertical: 4,
  },
  entityTagText: {
    fontWeight: "700",
  },
  storeTag: {
    backgroundColor: "#EAF2FF",
    borderColor: "#B8D4FF",
  },
  storeTagText: {
    color: "#175CD3",
  },
  supplierTag: {
    backgroundColor: "#E9F8EF",
    borderColor: "#ABEFC6",
  },
  supplierTagText: {
    color: "#067647",
  },
  neutralTag: {
    backgroundColor: "#F2F4F7",
    borderColor: "#D0D5DD",
  },
  neutralTagText: {
    color: "#475467",
  },
  pagination: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 12,
    justifyContent: "center",
    paddingVertical: 8,
  },
  nextButtonContent: {
    flexDirection: "row-reverse",
  },
  modal: {
    alignSelf: "stretch",
    backgroundColor: "#FFFFFF",
    height: "100%",
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
  detailNavigationTitle: {
    color: "#101828",
    flex: 1,
    fontWeight: "700",
  },
  detailStoreIcon: {
    alignItems: "center",
    backgroundColor: "#DCEEFF",
    borderRadius: 8,
    height: 32,
    justifyContent: "center",
    width: 32,
  },
  detailInvoiceCard: {
    backgroundColor: "#F8FBFF",
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
    gap: 0,
    paddingHorizontal: 16,
    paddingTop: 6,
  },
  detailInvoiceHeadingRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
    minHeight: 44,
  },
  detailInvoiceIdentity: { flex: 1, gap: 0, minWidth: 0 },
  detailSupplierName: {
    color: "#101828",
    fontSize: 19,
    fontWeight: "700",
    lineHeight: 23,
  },
  detailContextText: { color: "#667085" },
  detailInvoiceMetaRow: {
    alignItems: "center",
    borderBottomColor: "#DCE5EF",
    borderBottomWidth: 1,
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 44,
  },
  detailInfoToggle: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "flex-end",
    minHeight: 44,
    minWidth: 96,
    paddingLeft: 12,
  },
  detailInfoToggleText: { color: "#1677FF", fontWeight: "700" },
  detailInfoChevron: { color: "#1677FF", fontSize: 18, marginLeft: 4 },
  detailInfoPanel: {
    backgroundColor: "#F8FAFC",
    maxHeight: 112,
  },
  detailInfoPanelContent: {
    gap: 4,
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  detailInfoText: { color: "#475467" },
  detailAmountGrid: {
    flexDirection: "row",
    paddingVertical: 8,
  },
  detailAmountCell: {
    borderRightColor: "#DCE5EF",
    borderRightWidth: 1,
    flex: 1,
    gap: 0,
    minWidth: 0,
    paddingHorizontal: 8,
  },
  detailAmountCellLast: { borderRightWidth: 0 },
  detailAmountValue: { color: "#101828", fontWeight: "700", lineHeight: 24 },
  detailCountValue: { color: "#101828", fontWeight: "700", lineHeight: 24 },
  detailSearch: {
    backgroundColor: "#FFFFFF",
    height: 44,
    marginHorizontal: 16,
    marginTop: 6,
  },
  detailFilterTabs: {
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
    flexDirection: "row",
    marginTop: 4,
    paddingHorizontal: 16,
  },
  detailFilterTab: {
    alignItems: "center",
    borderBottomColor: "transparent",
    borderBottomWidth: 3,
    flex: 1,
    minHeight: 44,
    justifyContent: "center",
  },
  detailFilterTabSelected: { borderBottomColor: "#1677FF" },
  detailFilterTabText: { color: "#475467" },
  detailFilterTabTextSelected: { color: "#1677FF", fontWeight: "700" },
  detailListScroll: { flex: 1 },
  detailList: { paddingBottom: 4 },
  detailRow: {
    backgroundColor: "#FFFFFF",
    borderBottomColor: "#EAECF0",
    borderBottomWidth: 1,
    gap: 8,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  detailProductHeader: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 10,
  },
  productImage: {
    backgroundColor: "#EEF2F6",
    borderRadius: 8,
    height: 62,
    width: 62,
  },
  productImagePlaceholder: {
    alignItems: "center",
    backgroundColor: "#EEF2F6",
    borderRadius: 8,
    height: 62,
    justifyContent: "center",
    padding: 6,
    width: 62,
  },
  productImagePlaceholderText: {
    color: "#667085",
    textAlign: "center",
  },
  detailBody: {
    flex: 1,
    gap: 5,
    minWidth: 0,
  },
  detailProductNameRow: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 6,
    justifyContent: "space-between",
  },
  detailProductName: {
    color: "#101828",
    flex: 1,
    fontSize: 17,
    fontWeight: "700",
    lineHeight: 21,
  },
  detailProductMetaRow: { flexDirection: "row", gap: 10, minHeight: 44 },
  detailProductMeta: {
    color: "#667085",
    fontSize: 12,
    lineHeight: 16,
  },
  detailBarcodeMeta: { flex: 1, minWidth: 0 },
  detailCopyTarget: { justifyContent: "center", minHeight: 44, minWidth: 44 },
  detailPriceGrid: {
    flexDirection: "row",
    marginLeft: 72,
  },
  detailMetric: {
    borderRightColor: "#EAECF0",
    borderRightWidth: 1,
    flex: 1,
    gap: 2,
    paddingHorizontal: 6,
  },
  detailMetricLabel: {
    color: "#667085",
    fontSize: 13,
    lineHeight: 18,
  },
  detailMetricValue: {
    color: "#101828",
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 19,
  },
  priceChangeBadge: {
    borderRadius: 999,
    fontWeight: "700",
    overflow: "hidden",
    paddingHorizontal: 7,
    paddingVertical: 3,
  },
  priceIncreaseBadge: {
    backgroundColor: "#FFF1E8",
    color: "#B42318",
  },
  priceDecreaseBadge: {
    backgroundColor: "#ECFDF3",
    color: "#027A48",
  },
  detailRowFooter: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "flex-end",
    marginLeft: 72,
  },
  detailSubtotalLabel: { color: "#667085", marginRight: "auto" },
  detailSubtotalValue: { color: "#101828", fontWeight: "700" },
  detailEditButtonContent: { flexDirection: "row-reverse" },
  detailReturnBar: {
    backgroundColor: "#FFFFFF",
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  detailReturnButton: {
    backgroundColor: "#EEF6FF",
    borderRadius: 5,
  },
  detailPagination: {
    alignItems: "center",
    backgroundColor: "#FFFFFF",
    borderTopColor: "#EAECF0",
    borderTopWidth: 1,
    flexDirection: "row",
    justifyContent: "center",
    minHeight: 34,
  },
  detailPaginationButton: { height: 44, margin: 0, width: 44 },
  detailPaginationText: { color: "#475467", minWidth: 44, textAlign: "center" },
  pickerModal: {
    alignSelf: "center",
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    maxHeight: "82%",
    padding: 16,
    width: "88%",
  },
  pickerModalHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 8,
  },
  pickerModalList: {
    flexGrow: 0,
  },
  pickerModalListContent: {
    paddingBottom: 4,
  },
  pickerRow: {
    alignItems: "center",
    flexDirection: "row",
    minHeight: 48,
  },
  pickerRowButton: {
    flex: 1,
  },
  pickerRowButtonContent: {
    justifyContent: "flex-start",
  },
  emptyPickerState: {
    alignItems: "center",
    gap: 12,
    paddingVertical: 24,
  },
  dateModal: {
    alignSelf: "center",
    backgroundColor: "#FFFFFF",
    borderRadius: 12,
    padding: 16,
    width: "92%",
  },
  dateModalActions: {
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: 8,
  },
  calendarHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 4,
  },
  calendarMonthTitle: {
    flex: 1,
    textAlign: "center",
  },
  weekdayRow: {
    flexDirection: "row",
    marginBottom: 8,
  },
  weekdayText: {
    color: "#667085",
    flex: 1,
    textAlign: "center",
  },
  calendarGrid: {
    gap: 6,
  },
  calendarWeekRow: {
    flexDirection: "row",
    gap: 6,
  },
  calendarDateCell: {
    alignItems: "center",
    aspectRatio: 1,
    backgroundColor: "#FFFFFF",
    borderColor: "#D0D5DD",
    borderRadius: 8,
    borderWidth: 1,
    flex: 1,
    justifyContent: "center",
  },
  calendarDateCellOutside: {
    backgroundColor: "#F8FAFC",
    borderColor: "#EAECF0",
  },
  calendarDateCellInRange: {
    backgroundColor: "#E8F1FF",
    borderColor: "#B2CCFF",
  },
  calendarDateCellToday: {
    borderColor: "#2563EB",
  },
  calendarDateCellSelected: {
    backgroundColor: "#2563EB",
    borderColor: "#2563EB",
  },
  calendarDateCellSingleSelected: {
    backgroundColor: "#2563EB",
  },
  calendarDateCellPressed: {
    opacity: 0.8,
  },
  calendarDateText: {
    color: "#101828",
  },
  calendarDateTextOutside: {
    color: "#98A2B3",
  },
  calendarDateTextSelected: {
    color: "#FFFFFF",
    fontWeight: "700",
  },
});

const ENTITY_TAG_STYLES = {
  neutral: {
    tag: styles.neutralTag,
    text: styles.neutralTagText,
  },
  store: {
    tag: styles.storeTag,
    text: styles.storeTagText,
  },
  supplier: {
    tag: styles.supplierTag,
    text: styles.supplierTagText,
  },
};
