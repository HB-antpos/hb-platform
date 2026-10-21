import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  InteractionManager,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
  type LayoutChangeEvent,
} from "react-native";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  IconButton,
  Modal,
  Portal,
  Text,
  TextInput,
} from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  buildProductReportDateQuery,
  fetchChinaSupplierBranchTotals,
  fetchProductBranchBreakdown,
  fetchProductReportProductRows,
  fetchProductReportStoreOptions,
  fetchProductReportTotalRevenue,
  fetchSupplierBranchBreakdown,
  fetchSupplierReportRows,
  getProductReportCacheVersionState,
  getProductReportCacheVersionSyncDecision,
  type ChinaSupplierBranchTotalRow,
  type ProductBranchBreakdownRow,
  type ProductReportCostStatus,
  type ProductReportProductPage,
  type ProductReportProductRow,
  type ProductReportSnapshot,
  type ProductReportTotalRevenue,
  type SupplierBranchBreakdownRow,
  type SupplierReportKind,
  type SupplierReportRow,
} from "@/modules/product-report/api";
import {
  getCustomProductReportRange,
  getDefaultProductReportRange,
  getProductReportQuickRange,
  isValidProductReportDateRange,
  type ProductReportQuickRangeKey,
} from "@/modules/product-report/date-ranges";
import {
  DEFAULT_REPORT_SORT,
  getReportSortKey,
  sortReportRows,
  toggleReportSort,
  type ReportSort,
  type ReportSortField,
  type ReportSortValues,
} from "@/modules/product-report/sorting";
import { formatWholeMoney } from "@/modules/reports/format";
import {
  getCashierEnabledStoreCodes,
  getCashierScopedBranchCodes,
} from "@/modules/reports/cashier-enabled-store-scope";
import { GrowthPill, REPORT_UI } from "@/modules/reports/report-card-parts";
import { REPORT_QUERY_OPTIONS } from "@/modules/reports/report-config";
import {
  ReportLoadPerformanceTimer,
  ReportLoadVisibilityGate,
  discardReportNavigationStart,
  hasUsableSuccessfulReportCache,
  recordReportLoadPerformance,
  type ReportLoadCacheState,
  type ReportLoadPerformanceMeasurement,
} from "@/modules/reports/report-load-performance";
import {
  createReportSnapshotKey,
  formatReportSnapshotTime,
  getCompleteReportSnapshot,
  isReportScopeValid,
  saveCompleteReportSnapshot,
  type CompleteReportSnapshot,
} from "@/modules/reports/report-snapshot";
import { PRODUCT_PAGE_SIZE, PRODUCT_PAGE_SIZE_OPTIONS, SUPPLIER_PAGE_SIZE, SUPPLIER_PAGE_SIZE_OPTIONS, getPageRows } from "@/modules/product-report/pagination";
import {
  buildChinaBranchShareRows,
  mergeSupplierBranchSales,
  summarizeChinaGoods,
  summarizeProductPage,
} from "@/modules/product-report/china-goods-share";
import { ChinaBranchShareSection, ChinaGoodsSummaryCard } from "@/modules/product-report/china-goods-sections";
import { LINKED_COLORS, LINKED_STALE_OPACITY } from "@/modules/product-report/linked-colors";
import { LinkedFilterBar, type LinkedAnchor, type LinkedAnchorKey } from "@/modules/product-report/linked-filter-bar";
import {
  CompactPager,
  FilterChip,
  MetricGrid,
  SegmentToggle,
  SortChips,
} from "@/modules/product-report/product-report-card-parts";
import {
  formatCount,
  formatGrossMarginRate,
  formatNullableMoney,
  formatNullableWholeMoney,
  formatShare,
} from "@/modules/product-report/product-report-format";
import {
  PRODUCT_BRANCH_SORT_VALUES,
  ProductBranchRow,
  ProductReportCard,
} from "@/modules/product-report/product-report-rows";
import { SupplierDetailSheet } from "@/modules/product-report/supplier-detail-sheet";
import { SupplierReportTable, type SupplierTableRow } from "@/modules/product-report/supplier-report-table";
import {
  FRESH_LINKED_SECTIONS,
  filterRowsByBranch,
  findRowPage,
  getLinkedTransitionStaleness,
  getScopedRevenue,
  isSameLinkedCode,
  type LinkedSectionStaleness,
  type ProductReportDisplayContext,
} from "@/modules/product-report/linked-selection";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";

// 页面上商品卡的「商品分店数据」下钻；供应商的商品明细 / 分店数据由 SupplierDetailSheet 负责。
type Drilldown = { type: "product"; product: ProductReportProductRow };

interface ProductReportScreenProps {
  embedded?: boolean;
  onRefreshFreshness?: () => Promise<unknown>;
  onRefreshReport?: () => Promise<unknown>;
  reportNavigationActionId?: number | null;
}

interface ProductPageSummary {
  currentSales: number;
  compareSales: number;
  currentGrossProfit: number | null;
  compareGrossProfit: number | null;
  currentGrossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  currentCostStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

const MAIN_REPORT_CACHE_VERSION_REFETCH_LIMIT = 2;

// 吸顶联动条在主 ScrollView 直接子元素中的位置：（标题 +）澳洲/中国分段 → 日期卡 → 联动条。
// ScrollView 用 Children.toArray 计数，null 子元素会被丢掉，所以前两个子元素必须始终存在。
const LINKED_FILTER_BAR_INDEX = 2;

type SupplierMetricSortRow = Pick<
  SupplierReportRow,
  "revenue" | "compareRevenue" | "totalQuantity" | "compareTotalQuantity" | "averagePrice" | "compareAveragePrice"
>;

// 供应商表与供应商分店弹窗字段相同；均价在数量为 0 时是 null，排序时统一排在最后。
const SUPPLIER_METRIC_SORT_VALUES: ReportSortValues<SupplierMetricSortRow> = {
  amount: (row) => [row.revenue, row.compareRevenue],
  quantity: (row) => [row.totalQuantity, row.compareTotalQuantity],
  unitPrice: (row) => [row.averagePrice, row.compareAveragePrice],
};

// 商品明细沿用原表头顺序：数量 / 金额 / 均价（供应商表的排序在表头里点按，见 supplier-report-table.tsx）。
const PRODUCT_SORT_FIELDS: readonly ReportSortField[] = ["quantity", "amount", "unitPrice"];
const SORT_FIELD_LABEL_KEYS: Record<ReportSortField, string> = {
  amount: "productReport.columns.amount",
  quantity: "productReport.columns.quantity",
  unitPrice: "productReport.columns.averagePrice",
};

// 中国供应商页签的商品明细默认按数量降序（用户 2026-09-22 确认）；澳洲页签保持金额降序。
const CHINA_PRODUCT_DEFAULT_SORT: ReportSort = { field: "quantity", order: "desc" };

function getDefaultProductSort(kind: SupplierReportKind): ReportSort {
  return kind === "china" ? CHINA_PRODUCT_DEFAULT_SORT : DEFAULT_REPORT_SORT;
}

type CompleteProductMainReport = {
  totalRevenue: ProductReportTotalRevenue;
  supplier: ProductReportSnapshot<SupplierReportRow[]>;
  product: ProductReportSnapshot<ProductReportProductPage>;
  // 仅中国供应商页签请求；澳洲页签为 null。
  chinaBranchTotals: ProductReportSnapshot<ChinaSupplierBranchTotalRow[]> | null;
  // 中国页签选中供应商时，分店表改为该供应商在各分店的销售；未选供应商或澳洲页签为 null。
  chinaSupplierBranches: ProductReportSnapshot<SupplierBranchBreakdownRow[]> | null;
};

// 最近一次完整展示的主报表及其条件：联动点选后新数据到齐前，用它做局部过渡，只让受影响的区块变淡。
type DisplayedMainReport = {
  context: ProductReportDisplayContext;
  report: CompleteProductMainReport;
};

function formatRowNumber(value: number) {
  return String(value).padStart(2, "0");
}

function mergeCostStatuses(statuses: readonly ProductReportCostStatus[]): ProductReportCostStatus {
  if (statuses.some((status) => status === "Missing")) return "Missing";
  if (statuses.some((status) => status === "Complete")) return "Complete";
  return "NoActivity";
}

// 标题只放供应商名称，代码由下方副行单独展示，避免中国供应商代码上下重复。
function getSupplierTitle(row: SupplierReportRow) {
  return row.supplierName.trim() || row.supplierCode;
}

// 后端缺名时会把 supplierName 回退成 supplierCode，此时副行再显示一次代码就成了重复。
function shouldShowSupplierCode(row: SupplierReportRow) {
  const name = row.supplierName.trim();
  return name !== "" && name !== row.supplierCode.trim();
}

/** 澳洲页签顶部「本页商品汇总」：营业额 / 毛利额 / 毛利率 × 本期 / 同期 的小矩阵，字段与原表一致。 */
function ProductPageSummaryCard({
  summary,
  caption,
}: {
  summary: ProductPageSummary;
  caption: string;
}) {
  const { t } = useAppTranslation("common");
  const costPendingLabel = t("productReport.states.costPending");
  const noActivityLabel = t("productReport.states.costNoActivity");
  const metrics = [
    {
      key: "sales",
      label: t("productReport.metrics.revenue"),
      current: formatWholeMoney(summary.currentSales),
      compare: formatWholeMoney(summary.compareSales),
    },
    {
      key: "grossProfit",
      label: t("productReport.metrics.grossProfit"),
      current: formatNullableWholeMoney(summary.currentGrossProfit),
      compare: formatNullableWholeMoney(summary.compareGrossProfit),
    },
    {
      key: "grossMargin",
      label: t("productReport.metrics.grossMarginRate"),
      current: formatGrossMarginRate(summary.currentGrossMarginRate, summary.currentCostStatus, costPendingLabel, noActivityLabel),
      compare: formatGrossMarginRate(summary.compareGrossMarginRate, summary.compareCostStatus, costPendingLabel, noActivityLabel),
    },
  ];
  const rows = [
    { key: "current", label: t("reports.metrics.current"), current: true },
    { key: "compare", label: t("productReport.metrics.compare"), current: false },
  ];

  return (
    <View style={styles.card}>
      <View style={styles.cardTitleRow}>
        <Text style={styles.cardTitle}>{t("productReport.sections.pageSummary")}</Text>
        <Text style={styles.caption} numberOfLines={1}>{caption}</Text>
      </View>
      <View style={styles.summaryMatrixRow}>
        <View style={styles.summaryMatrixLabel} />
        {metrics.map((metric) => (
          <Text key={metric.key} style={[styles.summaryMatrixCell, styles.summaryMatrixHead]} numberOfLines={1}>
            {metric.label}
          </Text>
        ))}
      </View>
      {rows.map((row) => (
        <View key={row.key} style={styles.summaryMatrixRow}>
          <Text style={[styles.summaryMatrixLabel, row.current ? styles.summaryMatrixLabelCurrent : null]} numberOfLines={1}>
            {row.label}
          </Text>
          {metrics.map((metric) => (
            <Text
              key={metric.key}
              style={[styles.summaryMatrixCell, row.current ? styles.summaryMatrixCurrent : styles.summaryMatrixCompare]}
              numberOfLines={1}
              adjustsFontSizeToFit
              minimumFontScale={0.7}
            >
              {row.current ? metric.current : metric.compare}
            </Text>
          ))}
        </View>
      ))}
    </View>
  );
}

export function ProductReportScreen({
  embedded = false,
  onRefreshFreshness,
  onRefreshReport,
  reportNavigationActionId = null,
}: ProductReportScreenProps) {
  const { t } = useAppTranslation("common");
  const { height, width } = useWindowDimensions();
  const queryClient = useQueryClient();
  const accountIdentity = useAuthStore((state) => state.isAuthenticated
    ? state.user?.userGuid || state.user?.userGUID || ""
    : "");
  const productLoadTimer = useRef(new ReportLoadPerformanceTimer()).current;
  const productLoadActiveRef = useRef(false);
  const productLoadSessionKeyRef = useRef<object | null>(null);
  const firstProductReportDataVisibleRef = useRef(false);
  // 首数据指标只认真实明细行，汇总卡可见不能代替供应商或商品行可见。
  const firstSupplierReportRowRef = useRef<View>(null);
  const firstProductReportRowRef = useRef<View>(null);
  const drilldownLoadGate = useRef(new ReportLoadVisibilityGate()).current;
  const drilldownLoadQueryKeyRef = useRef<readonly unknown[] | null>(null);
  const drilldownLoadKindRef = useRef<"product" | null>(null);
  const drilldownRequestGenerationRef = useRef(0);
  const drilldownPhysicalRowVisibleRef = useRef(false);
  const drilldownPhysicalPresentationReadyRef = useRef(false);
  const completeMainReportSnapshotsRef = useRef(
    new Map<string, CompleteReportSnapshot<CompleteProductMainReport>>(),
  ).current;
  const completeDrilldownSnapshotsRef = useRef(
    new Map<string, CompleteReportSnapshot<SupplierBranchBreakdownRow[] | ProductBranchBreakdownRow[]>>(),
  ).current;
  const previousScopeCodesRef = useRef("");
  const mainReportVersionSyncRef = useRef<{ sessionKey: object | null; attemptCount: number }>({
    sessionKey: null,
    attemptCount: 0,
  });
  const scrollRef = useRef<ScrollView>(null);
  // 各区块在滚动内容里的纵向位置与吸顶联动条高度，供锚点跳转时让区块标题落在联动条下方。
  const sectionOffsetsRef = useRef<Record<LinkedAnchorKey, number>>({ branches: 0, suppliers: 0, products: 0 });
  const linkedBarHeightRef = useRef(0);
  const lastDisplayedMainReportRef = useRef<DisplayedMainReport | null>(null);
  // 已选供应商在新分店无销售时不在当前排行里，联动条与提示仍要显示它的名称。
  const supplierNamesRef = useRef(new Map<string, string>());
  const supplierPageAnchorRef = useRef<SupplierReportRow[] | null>(null);
  const [kind, setKind] = useState<SupplierReportKind>("australia");
  const [range, setRange] = useState(() => getDefaultProductReportRange());
  const [draftStartDate, setDraftStartDate] = useState(range.startDate);
  const [draftEndDate, setDraftEndDate] = useState(range.endDate);
  const [selectedStoreCode, setSelectedStoreCode] = useState<string | undefined>();
  const [isStoreModalVisible, setStoreModalVisible] = useState(false);
  const [selectedSupplierCode, setSelectedSupplierCode] = useState<string | null>(null);
  const [supplierPage, setSupplierPage] = useState(1);
  const [productPage, setProductPage] = useState(1);
  // 每页条数：供应商表前端切页，商品明细交给后端分页；切换时回到第 1 页（见 handle*PageSizeChange）。
  const [supplierPageSize, setSupplierPageSize] = useState<number>(SUPPLIER_PAGE_SIZE);
  const [productPageSize, setProductPageSize] = useState<number>(PRODUCT_PAGE_SIZE);
  const [productSearchDraft, setProductSearchDraft] = useState("");
  const [productSearch, setProductSearch] = useState("");
  const [drilldown, setDrilldown] = useState<Drilldown | null>(null);
  // 点某家供应商后打开的弹窗（先选商品明细 / 分店数据）；null 表示未打开。
  const [sheetSupplier, setSheetSupplier] = useState<SupplierReportRow | null>(null);
  // 各表独立记住排序：主表排序在切换筛选时保留，分店弹窗每次打开回到默认金额降序。
  const [supplierSort, setSupplierSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);
  const [productSort, setProductSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);
  const [drilldownSort, setDrilldownSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);
  const productSortKey = getReportSortKey(productSort);
  const [mainReportVersionSyncExhausted, setMainReportVersionSyncExhausted] = useState(false);

  const dateRangeValid = isValidProductReportDateRange(draftStartDate, draftEndDate);
  const activeRange = useMemo(
    () => (dateRangeValid ? getCustomProductReportRange(draftStartDate, draftEndDate) ?? range : range),
    [dateRangeValid, draftEndDate, draftStartDate, range]
  );

  const storeOptionsQuery = useQuery({
    queryKey: ["product-report", "stores", accountIdentity],
    queryFn: ({ signal }) => fetchProductReportStoreOptions({ signal }),
    ...REPORT_QUERY_OPTIONS,
    enabled: Boolean(accountIdentity),
  });
  const cashierEnabledStoreCodes = useMemo(
    () => getCashierEnabledStoreCodes(storeOptionsQuery.data ?? []),
    [storeOptionsQuery.data],
  );
  const cashierStoreScopeVersion = storeOptionsQuery.dataUpdatedAt;
  const reportScopeValid = isReportScopeValid(accountIdentity, storeOptionsQuery, cashierEnabledStoreCodes);
  const branchCodes = useMemo(
    () => getCashierScopedBranchCodes(cashierEnabledStoreCodes, selectedStoreCode),
    [cashierEnabledStoreCodes, selectedStoreCode],
  );

  // 权限范围重验期间仍可展示同条件的完整快照；重验失败会同步关闭显示门槛。
  const snapshotQueryParams = useMemo(
    () => reportScopeValid && dateRangeValid && branchCodes.length > 0
      ? buildProductReportDateQuery(activeRange, branchCodes) : null,
    [activeRange, branchCodes, dateRangeValid, reportScopeValid],
  );
  const drilldownSnapshotQueryParams = useMemo(
    () => reportScopeValid && dateRangeValid
      ? buildProductReportDateQuery(activeRange, cashierEnabledStoreCodes) : null,
    [activeRange, cashierEnabledStoreCodes, dateRangeValid, reportScopeValid],
  );

  const queryParams = useMemo(
    // 收银启用门店白名单是业务查询的安全边界；未就绪、为空或旧选择失效时一律不回退到全店请求。
    () => (
      dateRangeValid
      && activeRange
      && storeOptionsQuery.isSuccess
      && !storeOptionsQuery.isFetching
      && branchCodes.length > 0
        ? buildProductReportDateQuery(activeRange, branchCodes)
        : null
    ),
    [activeRange, branchCodes, dateRangeValid, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess],
  );
  // 三表联动：总营业额、分店中国货合计和「选中供应商在各分店」固定按全部收银启用分店取数，
  // 选中分店只在前端切片（汇总卡、占比分母），切换分店时不必重取；只有供应商表与商品明细跟随所选分店。
  const allBranchQueryParams = useMemo(
    () => (
      dateRangeValid
      && activeRange
      && storeOptionsQuery.isSuccess
      && !storeOptionsQuery.isFetching
      && cashierEnabledStoreCodes.length > 0
        ? buildProductReportDateQuery(activeRange, cashierEnabledStoreCodes)
        : null
    ),
    [activeRange, cashierEnabledStoreCodes, dateRangeValid, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess],
  );
  useLayoutEffect(() => {
    if (reportNavigationActionId === null) return;
    // 白名单仍在加载时保留导航计时；否则会把真实首屏请求之前的点击起点提前丢弃。
    if (dateRangeValid && storeOptionsQuery.isPending) return;
    if (
      !dateRangeValid
      || storeOptionsQuery.isError
      || (storeOptionsQuery.isSuccess && cashierEnabledStoreCodes.length === 0)
    ) {
      discardReportNavigationStart("product", reportNavigationActionId);
    }
  }, [cashierEnabledStoreCodes.length, dateRangeValid, reportNavigationActionId, storeOptionsQuery.isError, storeOptionsQuery.isPending, storeOptionsQuery.isSuccess]);

  useEffect(() => {
    if (
      storeOptionsQuery.isFetching
      || !storeOptionsQuery.isSuccess
      || !selectedStoreCode
      || branchCodes.length > 0
    ) return;
    // 门店在刷新期间被停用时，先阻断旧范围请求，再清空与旧选择相关的局部状态。
    setSelectedStoreCode(undefined);
    setSelectedSupplierCode(null);
    setSupplierPage(1);
    setProductPage(1);
    setDrilldown(null);
    setSheetSupplier(null);
  }, [branchCodes.length, selectedStoreCode, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess]);

  useEffect(() => {
    if (storeOptionsQuery.isFetching) {
      // 重验期间隐藏选择器，但保留下钻意图；成功后仅在新白名单仍有效时恢复。
      setStoreModalVisible(false);
      return;
    }
    if (
      storeOptionsQuery.isSuccess
      && cashierEnabledStoreCodes.length > 0
    ) return;
    // 白名单不可用时关闭下钻，避免展示上一次范围的缓存明细。
    setDrilldown(null);
    setSheetSupplier(null);
    setStoreModalVisible(false);
  }, [cashierEnabledStoreCodes.length, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess]);

  // 商品分店弹窗也要完整分店数据，不继承顶部单店筛选。
  const productBranchQueryParams = useMemo(
    () => (
      dateRangeValid
      && activeRange
      && storeOptionsQuery.isSuccess
      && !storeOptionsQuery.isFetching
      && cashierEnabledStoreCodes.length > 0
        ? buildProductReportDateQuery(activeRange, cashierEnabledStoreCodes)
        : null
    ),
    [activeRange, cashierEnabledStoreCodes, dateRangeValid, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess],
  );
  const supplierFilterCodes = useMemo(
    () => (selectedSupplierCode ? [selectedSupplierCode] : undefined),
    [selectedSupplierCode]
  );
  const totalRevenueQueryKey = useMemo(
    () => ["product-report", "total-revenue", accountIdentity, cashierStoreScopeVersion, allBranchQueryParams] as const,
    [accountIdentity, allBranchQueryParams, cashierStoreScopeVersion],
  );
  const supplierQueryKey = useMemo(
    () => ["product-report", "suppliers", accountIdentity, kind, cashierStoreScopeVersion, queryParams] as const,
    [accountIdentity, cashierStoreScopeVersion, kind, queryParams],
  );
  const productQueryKey = useMemo(
    () => [
      "product-report",
      "products",
      accountIdentity,
      kind,
      cashierStoreScopeVersion,
      queryParams,
      supplierFilterCodes,
      productSearch,
      productPage,
      productPageSize,
      productSortKey,
    ] as const,
    [accountIdentity, cashierStoreScopeVersion, kind, productPage, productPageSize, productSearch, productSortKey, queryParams, supplierFilterCodes],
  );
  const isChinaKind = kind === "china";
  const chinaBranchTotalsQueryKey = useMemo(
    () => ["product-report", "china-branch-totals", accountIdentity, cashierStoreScopeVersion, allBranchQueryParams] as const,
    [accountIdentity, allBranchQueryParams, cashierStoreScopeVersion],
  );
  // 中国页签选中供应商后，分店表显示该供应商在全部授权分店的销售；它和分店合计一样不随所选分店变化。
  const linkedSupplierCode = isChinaKind ? selectedSupplierCode : null;
  const hasChinaSupplierBranches = Boolean(linkedSupplierCode);
  const chinaSupplierBranchesQueryKey = useMemo(
    () => [
      "product-report",
      "china-supplier-branch-link",
      accountIdentity,
      cashierStoreScopeVersion,
      linkedSupplierCode,
      allBranchQueryParams,
    ] as const,
    [accountIdentity, allBranchQueryParams, cashierStoreScopeVersion, linkedSupplierCode],
  );
  const productLoadSessionKey = useMemo(
    () => ({
      totalRevenueQueryKey,
      supplierQueryKey,
      productQueryKey,
      // 中国页签多一块分店中国货合计，它同样属于首屏主报表的同一次加载会话。
      chinaBranchTotalsQueryKey: isChinaKind ? chinaBranchTotalsQueryKey : null,
      chinaSupplierBranchesQueryKey: hasChinaSupplierBranches ? chinaSupplierBranchesQueryKey : null,
    }),
    [
      chinaBranchTotalsQueryKey,
      chinaSupplierBranchesQueryKey,
      hasChinaSupplierBranches,
      isChinaKind,
      productQueryKey,
      supplierQueryKey,
      totalRevenueQueryKey,
    ],
  );
  const mainReportSnapshotKey = useMemo(
    () => snapshotQueryParams
      ? createReportSnapshotKey({
          accountIdentity,
          tab: "product",
          period: range.key,
          startDate: snapshotQueryParams.startDate,
          endDate: snapshotQueryParams.endDate,
          compareStartDate: snapshotQueryParams.compareStartDate,
          compareEndDate: snapshotQueryParams.compareEndDate,
          compareMode: snapshotQueryParams.compareMode,
          branchCodes: snapshotQueryParams.branchCodes ?? [],
          supplierKind: kind,
          supplierCode: selectedSupplierCode,
          search: productSearch,
          page: productPage,
          pageSize: productPageSize,
          // 商品明细由后端排序后分页，不同排序的完整快照不能互相替代。
          sort: productSortKey,
        })
      : null,
    [accountIdentity, kind, productPage, productPageSize, productSearch, productSortKey, snapshotQueryParams, range.key, selectedSupplierCode],
  );

  const startProductLoad = useCallback((cacheState: ReportLoadCacheState) => {
    productLoadTimer.start(cacheState, "product");
    productLoadActiveRef.current = true;
  }, [productLoadTimer]);
  const ensureProductLoadStarted = useCallback(() => {
    if (
      !queryParams
      || (
        productLoadSessionKeyRef.current === productLoadSessionKey
        && productLoadActiveRef.current
      )
    ) return;
    productLoadSessionKeyRef.current = productLoadSessionKey;
    firstProductReportDataVisibleRef.current = false;
    const cachedTotalRevenue = queryClient.getQueryData<ProductReportTotalRevenue>(totalRevenueQueryKey);
    const cachedSupplierRows = queryClient.getQueryData<ProductReportSnapshot<SupplierReportRow[]>>(supplierQueryKey);
    const cachedProductPage = queryClient.getQueryData<ProductReportSnapshot<ProductReportProductPage>>(productQueryKey);
    const cachedChinaBranchTotals = isChinaKind
      ? queryClient.getQueryData<ProductReportSnapshot<ChinaSupplierBranchTotalRow[]>>(chinaBranchTotalsQueryKey)
      : undefined;
    const hasCompleteChinaBranchCache = !isChinaKind || hasUsableSuccessfulReportCache(
      queryClient.getQueryState(chinaBranchTotalsQueryKey)?.status,
      cachedChinaBranchTotals,
      (cachedChinaBranchTotals) => cachedChinaBranchTotals.isComplete,
    );
    const cachedChinaSupplierBranches = hasChinaSupplierBranches
      ? queryClient.getQueryData<ProductReportSnapshot<SupplierBranchBreakdownRow[]>>(chinaSupplierBranchesQueryKey)
      : undefined;
    const hasCompleteChinaSupplierBranchCache = !hasChinaSupplierBranches || hasUsableSuccessfulReportCache(
      queryClient.getQueryState(chinaSupplierBranchesQueryKey)?.status,
      cachedChinaSupplierBranches,
      (cachedChinaSupplierBranches) => cachedChinaSupplierBranches.isComplete,
    );
    const hasCompleteCache =
      hasUsableSuccessfulReportCache(
        queryClient.getQueryState(totalRevenueQueryKey)?.status,
        cachedTotalRevenue,
        (cachedTotalRevenue) => cachedTotalRevenue.isComplete,
      )
      && hasUsableSuccessfulReportCache(
        queryClient.getQueryState(supplierQueryKey)?.status,
        cachedSupplierRows,
        (cachedSupplierRows) => cachedSupplierRows.isComplete,
      )
      && hasUsableSuccessfulReportCache(
        queryClient.getQueryState(productQueryKey)?.status,
        cachedProductPage,
        (cachedProductPage) => cachedProductPage.isComplete,
      )
      && hasCompleteChinaBranchCache
      && hasCompleteChinaSupplierBranchCache
      && getProductReportCacheVersionState([
        cachedTotalRevenue,
        cachedSupplierRows,
        cachedProductPage,
        ...(isChinaKind ? [cachedChinaBranchTotals] : []),
        ...(hasChinaSupplierBranches ? [cachedChinaSupplierBranches] : []),
      ]) === "aligned"
      && (cachedSupplierRows.data.length > 0 || cachedProductPage.data.rows.length > 0);
    startProductLoad(hasCompleteCache ? "warm" : "cold");
  }, [
    chinaBranchTotalsQueryKey,
    chinaSupplierBranchesQueryKey,
    hasChinaSupplierBranches,
    isChinaKind,
    productLoadSessionKey,
    productQueryKey,
    queryClient,
    queryParams,
    startProductLoad,
    supplierQueryKey,
    totalRevenueQueryKey,
  ]);
  const failProductLoad = useCallback(() => {
    if (productLoadSessionKeyRef.current !== productLoadSessionKey) return;
    productLoadTimer.fail();
    productLoadActiveRef.current = false;
  }, [productLoadSessionKey, productLoadTimer]);

  const totalRevenueQuery = useQuery({
    queryKey: totalRevenueQueryKey,
    queryFn: async ({ signal }) => {
      ensureProductLoadStarted();
      try {
        return await fetchProductReportTotalRevenue(allBranchQueryParams!, { signal });
      } catch (error) {
        failProductLoad();
        throw error;
      }
    },
    enabled: Boolean(allBranchQueryParams),
    ...REPORT_QUERY_OPTIONS,
  });

  const supplierQuery = useQuery({
    queryKey: supplierQueryKey,
    queryFn: async ({ signal }) => {
      ensureProductLoadStarted();
      try {
        return await fetchSupplierReportRows(kind, queryParams!, 1000, { signal });
      } catch (error) {
        failProductLoad();
        throw error;
      }
    },
    enabled: Boolean(queryParams),
    ...REPORT_QUERY_OPTIONS,
  });

  const totalRevenueStatisticsPending =
    totalRevenueQuery.isLoading || (
      totalRevenueQuery.data !== undefined
      && !totalRevenueQuery.data.isComplete
      && (!totalRevenueQuery.data.pollingExhausted || totalRevenueQuery.isFetching)
    );
  const totalRevenueStatisticsIncomplete =
    totalRevenueQuery.data !== undefined
    && !totalRevenueQuery.data.isComplete
    && totalRevenueQuery.data.pollingExhausted
    && !totalRevenueQuery.isFetching;

  const productQuery = useQuery({
    queryKey: productQueryKey,
    queryFn: async ({ signal }) => {
      ensureProductLoadStarted();
      try {
        return await fetchProductReportProductRows(
          kind,
          queryParams!,
          supplierFilterCodes,
          productPage,
          productPageSize,
          productSearch,
          productSort,
          { signal },
        );
      } catch (error) {
        failProductLoad();
        throw error;
      }
    },
    enabled: Boolean(queryParams),
    placeholderData: keepPreviousData,
    ...REPORT_QUERY_OPTIONS,
  });
  // 中国页签第 4 块主数据：分店中国货合计。与总额、供应商、商品共用统计批次版本，四块对齐后才整体展示。
  const chinaBranchTotalsQuery = useQuery({
    queryKey: chinaBranchTotalsQueryKey,
    queryFn: async ({ signal }) => {
      ensureProductLoadStarted();
      try {
        return await fetchChinaSupplierBranchTotals(allBranchQueryParams!, { signal });
      } catch (error) {
        failProductLoad();
        throw error;
      }
    },
    enabled: Boolean(allBranchQueryParams) && isChinaKind,
    ...REPORT_QUERY_OPTIONS,
  });
  // 中国页签选中供应商时的第 5 块主数据：该供应商在各分店的销售，作为分店表的分子。
  // cacheVersion 只由日期决定、与分店和供应商参数无关，因此可以并入同一统计批次校验。
  const chinaSupplierBranchesQuery = useQuery({
    queryKey: chinaSupplierBranchesQueryKey,
    queryFn: async ({ signal }) => {
      ensureProductLoadStarted();
      try {
        return await fetchSupplierBranchBreakdown("china", allBranchQueryParams!, linkedSupplierCode!, { signal });
      } catch (error) {
        failProductLoad();
        throw error;
      }
    },
    enabled: Boolean(allBranchQueryParams) && hasChinaSupplierBranches,
    ...REPORT_QUERY_OPTIONS,
  });
  const mainReportCacheVersionState = getProductReportCacheVersionState([
    totalRevenueQuery.data,
    supplierQuery.data,
    productQuery.data,
    ...(isChinaKind ? [chinaBranchTotalsQuery.data] : []),
    ...(hasChinaSupplierBranches ? [chinaSupplierBranchesQuery.data] : []),
  ]);
  const mainReportQueriesFetching =
    totalRevenueQuery.isFetching
    || supplierQuery.isFetching
    || productQuery.isFetching
    || (isChinaKind && chinaBranchTotalsQuery.isFetching)
    || (hasChinaSupplierBranches && chinaSupplierBranchesQuery.isFetching);
  const refetchMainReport = useCallback(() => Promise.all([
    queryClient.refetchQueries({ queryKey: totalRevenueQueryKey, exact: true, type: "active" }),
    queryClient.refetchQueries({ queryKey: supplierQueryKey, exact: true, type: "active" }),
    queryClient.refetchQueries({ queryKey: productQueryKey, exact: true, type: "active" }),
    ...(isChinaKind
      ? [queryClient.refetchQueries({ queryKey: chinaBranchTotalsQueryKey, exact: true, type: "active" })]
      : []),
    ...(hasChinaSupplierBranches
      ? [queryClient.refetchQueries({ queryKey: chinaSupplierBranchesQueryKey, exact: true, type: "active" })]
      : []),
  ]), [
    chinaBranchTotalsQueryKey,
    chinaSupplierBranchesQueryKey,
    hasChinaSupplierBranches,
    isChinaKind,
    productQueryKey,
    queryClient,
    supplierQueryKey,
    totalRevenueQueryKey,
  ]);
  const resetMainReportVersionSync = useCallback(() => {
    mainReportVersionSyncRef.current = { sessionKey: productLoadSessionKey, attemptCount: 0 };
    setMainReportVersionSyncExhausted(false);
  }, [productLoadSessionKey]);

  useEffect(() => {
    if (!queryParams) return;
    let syncState = mainReportVersionSyncRef.current;
    if (syncState.sessionKey !== productLoadSessionKey) {
      syncState = { sessionKey: productLoadSessionKey, attemptCount: 0 };
      mainReportVersionSyncRef.current = syncState;
      setMainReportVersionSyncExhausted(false);
    }

    const decision = getProductReportCacheVersionSyncDecision(
      mainReportCacheVersionState,
      syncState.attemptCount,
      mainReportQueriesFetching,
      MAIN_REPORT_CACHE_VERSION_REFETCH_LIMIT,
    );
    if (decision === "ready") {
      syncState.attemptCount = 0;
      setMainReportVersionSyncExhausted(false);
      return;
    }
    if (decision === "exhausted") {
      setMainReportVersionSyncExhausted(true);
      failProductLoad();
      return;
    }
    if (decision !== "refetch") return;

    // 三块并发结果不属于同一批次时整组重取；次数受限，期间继续隐藏所有业务行。
    syncState.attemptCount += 1;
    setMainReportVersionSyncExhausted(false);
    void refetchMainReport();
  }, [
    failProductLoad,
    mainReportCacheVersionState,
    mainReportQueriesFetching,
    productLoadSessionKey,
    queryParams,
    refetchMainReport,
  ]);
  const mainReportCacheVersionMismatch = mainReportCacheVersionState === "mismatch";
  const mainReportVersionSyncExhaustedForSession =
    mainReportVersionSyncRef.current.sessionKey === productLoadSessionKey
    && mainReportVersionSyncExhausted;
  const mainReportStatisticsPending =
    storeOptionsQuery.isFetching
    || totalRevenueStatisticsPending
    || supplierQuery.isLoading
    || productQuery.isLoading
    || (
      mainReportCacheVersionMismatch
      && (mainReportQueriesFetching || !mainReportVersionSyncExhaustedForSession)
    )
    || (
      supplierQuery.data !== undefined
      && !supplierQuery.data.isComplete
      && (!supplierQuery.data.pollingExhausted || supplierQuery.isFetching)
    )
    || (
      productQuery.data !== undefined
      && !productQuery.data.isComplete
      && (!productQuery.data.pollingExhausted || productQuery.isFetching)
    )
    || (
      isChinaKind
      && (
        chinaBranchTotalsQuery.isLoading
        || (
          chinaBranchTotalsQuery.data !== undefined
          && !chinaBranchTotalsQuery.data.isComplete
          && (!chinaBranchTotalsQuery.data.pollingExhausted || chinaBranchTotalsQuery.isFetching)
        )
      )
    )
    || (
      hasChinaSupplierBranches
      && (
        chinaSupplierBranchesQuery.isLoading
        || (
          chinaSupplierBranchesQuery.data !== undefined
          && !chinaSupplierBranchesQuery.data.isComplete
          && (!chinaSupplierBranchesQuery.data.pollingExhausted || chinaSupplierBranchesQuery.isFetching)
        )
      )
    );
  const mainReportStatisticsIncomplete =
    totalRevenueStatisticsIncomplete
    || (
      mainReportCacheVersionMismatch
      && mainReportVersionSyncExhaustedForSession
      && !mainReportQueriesFetching
    )
    || (
      supplierQuery.data !== undefined
      && !supplierQuery.data.isComplete
      && supplierQuery.data.pollingExhausted
      && !supplierQuery.isFetching
    )
    || (
      productQuery.data !== undefined
      && !productQuery.data.isComplete
      && productQuery.data.pollingExhausted
      && !productQuery.isFetching
    )
    || (
      isChinaKind
      && chinaBranchTotalsQuery.data !== undefined
      && !chinaBranchTotalsQuery.data.isComplete
      && chinaBranchTotalsQuery.data.pollingExhausted
      && !chinaBranchTotalsQuery.isFetching
    )
    || (
      hasChinaSupplierBranches
      && chinaSupplierBranchesQuery.data !== undefined
      && !chinaSupplierBranchesQuery.data.isComplete
      && chinaSupplierBranchesQuery.data.pollingExhausted
      && !chinaSupplierBranchesQuery.isFetching
    );
  // 后端明确标记统计更新失败（区间内某天对账失败，需后台恢复）：与「补算尚未完成」区分开，
  // 否则用户会一直点「重试」却永远没有结果。
  const mainReportStatisticsFailed =
    totalRevenueQuery.data?.statisticStatus === "Failed"
    || supplierQuery.data?.statisticStatus === "Failed"
    || productQuery.data?.statisticStatus === "Failed"
    || (isChinaKind && chinaBranchTotalsQuery.data?.statisticStatus === "Failed")
    || (hasChinaSupplierBranches && chinaSupplierBranchesQuery.data?.statisticStatus === "Failed");
  const mainReportRequestError =
    storeOptionsQuery.isError
    || totalRevenueQuery.isError
    || supplierQuery.isError
    || productQuery.isError
    || (isChinaKind && chinaBranchTotalsQuery.isError)
    || (hasChinaSupplierBranches && chinaSupplierBranchesQuery.isError);

  const mainReportCurrentComplete =
    reportScopeValid && dateRangeValid && !mainReportRequestError
    && !productQuery.isPlaceholderData
    && totalRevenueQuery.data?.isComplete === true
    && supplierQuery.data?.isComplete === true
    && productQuery.data?.isComplete === true
    && (!isChinaKind || chinaBranchTotalsQuery.data?.isComplete === true)
    && (!hasChinaSupplierBranches || chinaSupplierBranchesQuery.data?.isComplete === true)
    && mainReportCacheVersionState === "aligned"
    && !mainReportQueriesFetching;
  const currentMainReport: CompleteProductMainReport | undefined = mainReportCurrentComplete
    ? {
        totalRevenue: totalRevenueQuery.data!,
        supplier: supplierQuery.data!,
        product: productQuery.data!,
        chinaBranchTotals: isChinaKind ? chinaBranchTotalsQuery.data ?? null : null,
        chinaSupplierBranches: hasChinaSupplierBranches ? chinaSupplierBranchesQuery.data ?? null : null,
      }
    : undefined;
  useLayoutEffect(() => {
    if (!mainReportSnapshotKey || !mainReportCurrentComplete) return;
    saveCompleteReportSnapshot(
      completeMainReportSnapshotsRef,
      mainReportSnapshotKey,
      {
        totalRevenue: totalRevenueQuery.data!,
        supplier: supplierQuery.data!,
        product: productQuery.data!,
        chinaBranchTotals: isChinaKind ? chinaBranchTotalsQuery.data ?? null : null,
        chinaSupplierBranches: hasChinaSupplierBranches ? chinaSupplierBranchesQuery.data ?? null : null,
      },
      {
        statisticUpdatedAt:
          totalRevenueQuery.data?.statisticUpdatedAt
          ?? supplierQuery.data?.statisticUpdatedAt
          ?? productQuery.data?.statisticUpdatedAt
          ?? null,
        cacheVersion: totalRevenueQuery.data?.cacheVersion ?? null,
      },
    );
  }, [
    chinaBranchTotalsQuery.data,
    chinaSupplierBranchesQuery.data,
    completeMainReportSnapshotsRef,
    hasChinaSupplierBranches,
    isChinaKind,
    mainReportCurrentComplete,
    mainReportSnapshotKey,
    productQuery.data,
    supplierQuery.data,
    totalRevenueQuery.data,
  ]);
  useLayoutEffect(() => {
    const scopeFingerprint = JSON.stringify([accountIdentity, cashierEnabledStoreCodes]);
    if (previousScopeCodesRef.current !== ""
      && previousScopeCodesRef.current !== scopeFingerprint) {
      // 授权门店范围变化代表权限边界变化，旧范围的内存快照必须立即失效。
      completeMainReportSnapshotsRef.clear();
      completeDrilldownSnapshotsRef.clear();
      lastDisplayedMainReportRef.current = null;
      setDrilldown(null);
      setSheetSupplier(null);
    }
    previousScopeCodesRef.current = scopeFingerprint;
    if (!reportScopeValid) {
      completeMainReportSnapshotsRef.clear();
      completeDrilldownSnapshotsRef.clear();
      lastDisplayedMainReportRef.current = null;
      setDrilldown(null);
      setSheetSupplier(null);
    }
  }, [
    accountIdentity,
    reportScopeValid,
    cashierStoreScopeVersion,
    cashierEnabledStoreCodes,
    completeDrilldownSnapshotsRef,
    completeMainReportSnapshotsRef,
    storeOptionsQuery.isError,
  ]);
  const mainReportSnapshot = mainReportSnapshotKey
    ? getCompleteReportSnapshot(completeMainReportSnapshotsRef, mainReportSnapshotKey)
    : undefined;
  const mainReportHasSnapshot = mainReportSnapshot !== undefined;
  const linkedScopeKey = useMemo(
    () => JSON.stringify([accountIdentity, cashierEnabledStoreCodes]),
    [accountIdentity, cashierEnabledStoreCodes],
  );
  const requestedDisplayContext = useMemo<ProductReportDisplayContext | null>(
    () => snapshotQueryParams
      ? {
          scopeKey: linkedScopeKey,
          kind,
          startDate: snapshotQueryParams.startDate,
          endDate: snapshotQueryParams.endDate,
          compareStartDate: snapshotQueryParams.compareStartDate,
          compareEndDate: snapshotQueryParams.compareEndDate,
          compareMode: snapshotQueryParams.compareMode,
          branchCode: selectedStoreCode ?? null,
          supplierCode: selectedSupplierCode,
          productSearch,
          productPage,
          productPageSize,
          productSort: productSortKey,
        }
      : null,
    [kind, linkedScopeKey, productPage, productPageSize, productSearch, productSortKey, selectedStoreCode, selectedSupplierCode, snapshotQueryParams],
  );
  // 联动点选或明细翻页后，新条件的数据到齐前沿用最近一次完整展示的结果：
  // 只有依赖变化的区块变淡并显示「更新中」，其余区块保持可读可点，避免整页闪成加载态或空表。
  const lastDisplayedMainReport = lastDisplayedMainReportRef.current;
  const linkedTransitionStaleness = !mainReportCurrentComplete
    && !mainReportHasSnapshot
    && reportScopeValid
    && requestedDisplayContext
    && lastDisplayedMainReport
    ? getLinkedTransitionStaleness(lastDisplayedMainReport.context, requestedDisplayContext)
    : null;
  const linkedTransition = linkedTransitionStaleness && lastDisplayedMainReport
    ? { ...lastDisplayedMainReport, staleness: linkedTransitionStaleness }
    : null;
  const displayedMainReport: CompleteProductMainReport | undefined =
    currentMainReport ?? mainReportSnapshot?.data ?? linkedTransition?.report;
  // 表格里的数据属于哪组条件：实时结果与快照就是当前条件，过渡期间仍是上一次展示的条件。
  const displayedContext = currentMainReport || mainReportHasSnapshot
    ? requestedDisplayContext
    : linkedTransition?.context ?? null;
  const linkedStaleness: LinkedSectionStaleness = linkedTransition?.staleness ?? FRESH_LINKED_SECTIONS;
  const mainReportHasDisplay = mainReportHasSnapshot || linkedTransition !== null;
  useLayoutEffect(() => {
    // 记录最近一次完整展示（实时结果或同条件快照），作为下一次联动点选的过渡底图。
    if (!requestedDisplayContext || !displayedMainReport || !(currentMainReport || mainReportHasSnapshot)) return;
    lastDisplayedMainReportRef.current = { context: requestedDisplayContext, report: displayedMainReport };
  });
  const supplierRows = useMemo(
    () => displayedMainReport?.supplier.data ?? [],
    [displayedMainReport?.supplier.data],
  );
  useEffect(() => {
    // 供应商只能从已展示的行里点选，所以在行展示后缓存名称即可覆盖所有已选供应商。
    supplierRows.forEach((row) => supplierNamesRef.current.set(row.supplierCode, getSupplierTitle(row)));
  }, [supplierRows]);
  const getSupplierLabel = (supplierCode: string | null | undefined) =>
    supplierCode ? supplierNamesRef.current.get(supplierCode) ?? supplierCode : null;
  const getStoreLabel = (storeCode: string | null | undefined) =>
    storeCode
      ? storeOptionsQuery.data?.find((item) => isSameLinkedCode(item.value, storeCode))?.label ?? storeCode
      : null;
  const selectedSupplierLabel = getSupplierLabel(selectedSupplierCode);
  // 供应商表是全量数据、前端分页：先排序再分页，# 即当前排序下的名次；小计与顺序无关，仍用原数组。
  const sortedSupplierRows = useMemo(
    () => sortReportRows(supplierRows, supplierSort, SUPPLIER_METRIC_SORT_VALUES, (row) => row.supplierCode),
    [supplierRows, supplierSort],
  );
  const supplierPageCount = Math.max(1, Math.ceil(sortedSupplierRows.length / supplierPageSize));
  const supplierPageRows = getPageRows(sortedSupplierRows, supplierPage, supplierPageSize);
  const supplierSubtotal = supplierRows.reduce((sum, row) => sum + row.revenue, 0);
  const supplierCompareSubtotal = supplierRows.reduce((sum, row) => sum + row.compareRevenue, 0);
  // 总营业额固定按全部授权分店取数：汇总卡按当前所选分店即时切片；
  // 供应商表的「占总营业」分母则跟随表里数据所属的分店，过渡期间两者可能暂时不同。
  const summaryRevenue = getScopedRevenue(displayedMainReport?.totalRevenue, selectedStoreCode);
  const supplierScopeBranchCode = displayedContext?.branchCode ?? null;
  const supplierScopeRevenue = getScopedRevenue(displayedMainReport?.totalRevenue, supplierScopeBranchCode);
  const selectedSupplierMissing = Boolean(selectedSupplierCode && selectedStoreCode)
    && displayedMainReport !== undefined
    && !linkedStaleness.supplierTable
    && !supplierRows.some((row) => isSameLinkedCode(row.supplierCode, selectedSupplierCode));
  // 换分店后供应商排行重排：若已选供应商仍在榜上，翻到它所在的页，让高亮行看得见；用户手动翻页不受影响。
  useEffect(() => {
    if (supplierPageAnchorRef.current === supplierRows) return;
    supplierPageAnchorRef.current = supplierRows;
    if (!selectedSupplierCode) return;
    const page = findRowPage(sortedSupplierRows, supplierPageSize, (row) => isSameLinkedCode(row.supplierCode, selectedSupplierCode));
    if (page !== null) setSupplierPage(page);
  }, [selectedSupplierCode, sortedSupplierRows, supplierPageSize, supplierRows]);
  // 明细的行号与「本页合计」说明按表里实际显示的那一页计算，过渡期间不把旧行标成新页码。
  const displayedProductPage = displayedContext?.productPage ?? productPage;
  const displayedProductPageSize = displayedContext?.productPageSize ?? productPageSize;
  const productSectionLoading = !mainReportHasDisplay
    && (productQuery.isLoading || productQuery.isPlaceholderData);

  const completeProductLoad = useCallback(() => {
    if (!firstProductReportDataVisibleRef.current) return;
    const measurement = productLoadTimer.markFirstRowVisible();
    if (!measurement) return;
    productLoadActiveRef.current = false;
    recordReportLoadPerformance("product", measurement);
  }, [productLoadTimer]);

  const markProductDataVisible = useCallback(() => {
    if (!productLoadActiveRef.current) return;
    [firstSupplierReportRowRef, firstProductReportRowRef].forEach((rowRef) => {
      rowRef.current?.measureInWindow((x, y, measuredWidth, measuredHeight) => {
        if (!productLoadActiveRef.current) return;
        if (
          measuredWidth <= 0
          || measuredHeight <= 0
          || x >= width
          || x + measuredWidth <= 0
          || y >= height
          || y + measuredHeight <= 0
        ) return;
        firstProductReportDataVisibleRef.current = true;
        completeProductLoad();
      });
    });
  }, [completeProductLoad, height, width]);

  const scheduleProductDataVisibilityCheck = useCallback(() => {
    requestAnimationFrame(markProductDataVisible);
  }, [markProductDataVisible]);

  useLayoutEffect(() => {
    ensureProductLoadStarted();
    return () => {
      if (productLoadSessionKeyRef.current !== productLoadSessionKey) return;
      productLoadTimer.cancel();
      productLoadActiveRef.current = false;
      productLoadSessionKeyRef.current = null;
    };
  }, [ensureProductLoadStarted, productLoadSessionKey, productLoadTimer]);

  useLayoutEffect(() => {
    const hasIncompleteSnapshot =
      (totalRevenueQuery.data !== undefined && !totalRevenueQuery.data.isComplete)
      || (supplierQuery.data !== undefined && !supplierQuery.data.isComplete)
      || (productQuery.data !== undefined && !productQuery.data.isComplete)
      || (isChinaKind && chinaBranchTotalsQuery.data !== undefined && !chinaBranchTotalsQuery.data.isComplete)
      || (
        hasChinaSupplierBranches
        && chinaSupplierBranchesQuery.data !== undefined
        && !chinaSupplierBranchesQuery.data.isComplete
      );
    if (hasIncompleteSnapshot) {
      if (!mainReportStatisticsPending) failProductLoad();
      return;
    }
    const hasCompleteData =
      totalRevenueQuery.data?.isComplete === true &&
      supplierQuery.data?.isComplete === true &&
      productQuery.data?.isComplete === true &&
      (!isChinaKind || (chinaBranchTotalsQuery.data?.isComplete === true && !chinaBranchTotalsQuery.isFetching)) &&
      (!hasChinaSupplierBranches
        || (chinaSupplierBranchesQuery.data?.isComplete === true && !chinaSupplierBranchesQuery.isFetching)) &&
      mainReportCacheVersionState === "aligned" &&
      !totalRevenueQuery.isFetching &&
      !supplierQuery.isFetching &&
      !productQuery.isFetching;
    if (!hasCompleteData) return;
    const hasBusinessRows =
      (supplierQuery.data?.data.length ?? 0) > 0
      || (productQuery.data?.data.rows.length ?? 0) > 0;
    if (!hasBusinessRows) {
      // 完整空结果只有空态，没有“首条业务数据”，不得进入 first-data 达标率。
      productLoadTimer.cancel();
      productLoadActiveRef.current = false;
      return;
    }
    productLoadTimer.markDataNormalized();
    markProductDataVisible();
  }, [
    chinaBranchTotalsQuery.data,
    chinaBranchTotalsQuery.isFetching,
    chinaSupplierBranchesQuery.data,
    chinaSupplierBranchesQuery.isFetching,
    failProductLoad,
    hasChinaSupplierBranches,
    isChinaKind,
    mainReportStatisticsPending,
    mainReportCacheVersionState,
    markProductDataVisible,
    productLoadTimer,
    productQuery.data,
    productQuery.isFetching,
    supplierQuery.data,
    supplierQuery.isFetching,
    totalRevenueQuery.data,
    totalRevenueQuery.isFetching,
  ]);
  const productRows = useMemo(
    () => displayedMainReport?.product.data.rows ?? [],
    [displayedMainReport?.product.data.rows],
  );
  const productTotal = displayedMainReport?.product.data.total ?? 0;
  const productPageSummary = useMemo<ProductPageSummary>(() => {
    const currentSales = productRows.reduce((sum, row) => sum + row.salesAmount, 0);
    const compareSales = productRows.reduce((sum, row) => sum + row.compareSalesAmount, 0);
    const currentCostStatus = mergeCostStatuses(productRows.map((row) => row.costStatus));
    const compareCostStatus = mergeCostStatuses(productRows.map((row) => row.compareCostStatus));
    const currentGrossProfit = currentCostStatus === "Missing"
      ? null
      : productRows.reduce((sum, row) => sum + (row.grossProfit ?? 0), 0);
    const compareGrossProfit = compareCostStatus === "Missing"
      ? null
      : productRows.reduce((sum, row) => sum + (row.compareGrossProfit ?? 0), 0);

    return {
      currentSales,
      compareSales,
      currentGrossProfit,
      compareGrossProfit,
      currentGrossMarginRate:
        currentSales > 0 && currentGrossProfit !== null ? currentGrossProfit / currentSales : null,
      compareGrossMarginRate:
        compareSales > 0 && compareGrossProfit !== null ? compareGrossProfit / compareSales : null,
      currentCostStatus,
      compareCostStatus,
    };
  }, [productRows]);
  const chinaBranchTotalRows = useMemo(
    () => displayedMainReport?.chinaBranchTotals?.data ?? [],
    [displayedMainReport?.chinaBranchTotals?.data],
  );
  const reportBranchRevenues = useMemo(
    () => displayedMainReport?.totalRevenue.branches ?? [],
    [displayedMainReport?.totalRevenue.branches],
  );
  // 中国货合计取自分店合计（同期覆盖全部中国供应商）。分店合计固定是全部授权分店：
  // 汇总卡按当前所选分店即时切片（行为与原先顶部单店筛选一致，只跟分店、不跟供应商）；
  // 供应商表的「占中国货」分母按表里数据所属的分店切片。
  const chinaGoodsSummary = useMemo(
    () => summarizeChinaGoods(
      filterRowsByBranch(chinaBranchTotalRows, selectedStoreCode),
      summaryRevenue.revenue,
      summaryRevenue.compareRevenue,
    ),
    [chinaBranchTotalRows, selectedStoreCode, summaryRevenue.compareRevenue, summaryRevenue.revenue],
  );
  const supplierScopeChinaGoods = useMemo(
    () => summarizeChinaGoods(
      filterRowsByBranch(chinaBranchTotalRows, supplierScopeBranchCode),
      supplierScopeRevenue.revenue,
      supplierScopeRevenue.compareRevenue,
    ),
    [chinaBranchTotalRows, supplierScopeBranchCode, supplierScopeRevenue.compareRevenue, supplierScopeRevenue.revenue],
  );
  // 分店表只随供应商变化：选了供应商就改用该供应商在各分店的销售作分子，分母仍是分店营业额。
  const branchTableSupplierCode = isChinaKind ? displayedContext?.supplierCode ?? null : null;
  const chinaSupplierBranchRows = displayedMainReport?.chinaSupplierBranches?.data;
  const chinaBranchShareRows = useMemo(
    () => buildChinaBranchShareRows(
      reportBranchRevenues,
      branchTableSupplierCode && chinaSupplierBranchRows
        ? mergeSupplierBranchSales(chinaSupplierBranchRows)
        : chinaBranchTotalRows,
    ),
    [branchTableSupplierCode, chinaBranchTotalRows, chinaSupplierBranchRows, reportBranchRevenues],
  );
  const chinaProductPageTotals = useMemo(() => summarizeProductPage(productRows), [productRows]);
  // 占比细条以当前页签全量供应商中的最大金额为满格，只表达相对集中度。
  const topSupplierRevenue = useMemo(
    () => supplierRows.reduce((max, row) => Math.max(max, row.revenue), 0),
    [supplierRows],
  );
  const productPageCount = Math.max(1, Math.ceil(productTotal / productPageSize));
  const growthNewLabel = t("productReport.metrics.newGrowth");
  const costPendingLabel = t("productReport.states.costPending");
  const costNoActivityLabel = t("productReport.states.costNoActivity");

  const productBranchQueryKey = useMemo(
    () => [
      "product-report",
      "product-branches",
      accountIdentity,
      cashierStoreScopeVersion,
      drilldown,
      productBranchQueryParams,
    ] as const,
    [accountIdentity, cashierStoreScopeVersion, drilldown, productBranchQueryParams],
  );
  const productBranchSnapshotKey = useMemo(
    () => drilldown?.type === "product" && drilldownSnapshotQueryParams
      ? createReportSnapshotKey({
          accountIdentity,
          tab: "product",
          detail: "product-branches",
          period: range.key,
          startDate: drilldownSnapshotQueryParams.startDate,
          endDate: drilldownSnapshotQueryParams.endDate,
          compareStartDate: drilldownSnapshotQueryParams.compareStartDate,
          compareEndDate: drilldownSnapshotQueryParams.compareEndDate,
          compareMode: drilldownSnapshotQueryParams.compareMode,
          branchCodes: drilldownSnapshotQueryParams.branchCodes ?? [],
          supplierKind: "product",
          supplierCode: drilldown.product.productCode,
        })
      : null,
    [accountIdentity, drilldown, drilldownSnapshotQueryParams, range.key],
  );
  const startDrilldownLoad = useCallback((
    queryKey: readonly unknown[],
    nextKind: "product",
    hasUsableCache: boolean,
  ) => {
    const requestGeneration = drilldownRequestGenerationRef.current + 1;
    drilldownRequestGenerationRef.current = requestGeneration;
    const isSameDrilldownQuery = drilldownLoadQueryKeyRef.current === queryKey;
    if (!isSameDrilldownQuery) {
      drilldownPhysicalRowVisibleRef.current = false;
      drilldownPhysicalPresentationReadyRef.current = false;
    }
    drilldownLoadQueryKeyRef.current = queryKey;
    drilldownLoadKindRef.current = nextKind;
    drilldownLoadGate.start(hasUsableCache ? "warm" : "cold", {
      restorePhysicalState: isSameDrilldownQuery
        ? {
            firstRowVisible: drilldownPhysicalRowVisibleRef.current,
            presentationReady: drilldownPhysicalPresentationReadyRef.current,
          }
        : undefined,
    });
    return requestGeneration;
  }, [drilldownLoadGate]);
  const failDrilldownLoad = useCallback((
    requestGeneration: number,
    queryKey: readonly unknown[],
  ) => {
    if (
      drilldownRequestGenerationRef.current !== requestGeneration
      || drilldownLoadQueryKeyRef.current !== queryKey
    ) return;
    drilldownLoadGate.fail();
  }, [drilldownLoadGate]);

  const productBranchQuery = useQuery({
    queryKey: productBranchQueryKey,
    queryFn: async ({ signal }) => {
      const cachedProductBranches = queryClient.getQueryData<
        ProductReportSnapshot<ProductBranchBreakdownRow[]>
      >(productBranchQueryKey);
      const requestGeneration = startDrilldownLoad(
        productBranchQueryKey,
        "product",
        hasUsableSuccessfulReportCache(
          queryClient.getQueryState(productBranchQueryKey)?.status,
          cachedProductBranches,
          (cachedProductBranches) => cachedProductBranches.isComplete && cachedProductBranches.data.length > 0,
        ),
      );
      try {
        const result = await fetchProductBranchBreakdown(
          productBranchQueryParams!,
          (drilldown as Extract<Drilldown, { type: "product" }>).product.productCode,
          { signal },
        );
        if (
          signal.aborted
          || drilldownRequestGenerationRef.current !== requestGeneration
          || drilldownLoadQueryKeyRef.current !== productBranchQueryKey
        ) {
          const abortError = new Error("Stale product branch report request");
          abortError.name = "AbortError";
          throw abortError;
        }
        return result;
      } catch (error) {
        failDrilldownLoad(requestGeneration, productBranchQueryKey);
        throw error;
      }
    },
    enabled: Boolean(productBranchQueryParams && drilldown?.type === "product"),
    ...REPORT_QUERY_OPTIONS,
  });
  const drilldownKind = drilldown?.type ?? null;
  const activeDrilldownQuery = drilldownKind === "product" ? productBranchQuery : null;
  const activeDrilldownQueryKey = drilldownKind === "product" ? productBranchQueryKey : null;
  const activeDrilldownSnapshotKey = drilldownKind === "product" ? productBranchSnapshotKey : null;
  const activeDrilldownSnapshot = activeDrilldownSnapshotKey
    ? getCompleteReportSnapshot(completeDrilldownSnapshotsRef, activeDrilldownSnapshotKey)
    : undefined;
  const displayedProductBranchRows = reportScopeValid && drilldownKind === "product"
    ? (productBranchQuery.data?.isComplete && !productBranchQuery.isFetching && !productBranchQuery.isError
      ? productBranchQuery.data.data
      : (activeDrilldownSnapshot?.data as ProductBranchBreakdownRow[] | undefined) ?? [])
    : [];
  useLayoutEffect(() => {
    if (!reportScopeValid || !activeDrilldownSnapshotKey || !activeDrilldownQuery?.data?.isComplete
      || activeDrilldownQuery.isFetching || activeDrilldownQuery.isError) return;
    saveCompleteReportSnapshot(
      completeDrilldownSnapshotsRef,
      activeDrilldownSnapshotKey,
      activeDrilldownQuery.data.data,
      {
        statisticUpdatedAt: activeDrilldownQuery.data.statisticUpdatedAt,
        cacheVersion: activeDrilldownQuery.data.cacheVersion,
      },
    );
  }, [
    reportScopeValid,
    activeDrilldownQuery?.data,
    activeDrilldownQuery?.isFetching,
    activeDrilldownQuery?.isError,
    activeDrilldownSnapshotKey,
    completeDrilldownSnapshotsRef,
  ]);
  // 弹窗状态按当前下钻类型取值，避免另一个禁用查询把内容渲染成空白。
  const isDrilldownLoading =
    activeDrilldownQuery?.isLoading && displayedProductBranchRows.length === 0
    || Boolean(
      activeDrilldownQuery?.data !== undefined
      && !activeDrilldownQuery.data.isComplete
      && (!activeDrilldownQuery.data.pollingExhausted || activeDrilldownQuery.isFetching),
    );
  const isDrilldownStatisticsIncomplete = Boolean(
    activeDrilldownQuery?.data !== undefined
    && !activeDrilldownQuery.data.isComplete
    && activeDrilldownQuery.data.pollingExhausted
    && !activeDrilldownQuery.isFetching,
  );
  const isDrilldownError = drilldownKind === "product" ? productBranchQuery.isError : false;
  const drilldownShowingSnapshot = activeDrilldownSnapshot !== undefined
    && (Boolean(activeDrilldownQuery?.isFetching) || Boolean(isDrilldownError) || Boolean(isDrilldownStatisticsIncomplete));
  const recordDrilldownMeasurement = useCallback((measurement: ReportLoadPerformanceMeasurement | null) => {
    if (!measurement || !drilldownLoadKindRef.current) return;
    recordReportLoadPerformance("product-branches", measurement);
  }, []);
  const updateDrilldownFirstDataVisibility = useCallback((visible: boolean) => {
    // 该回调已在 InteractionManager 后测量视口，因此同时证明弹窗展示完成。
    drilldownPhysicalPresentationReadyRef.current = true;
    drilldownPhysicalRowVisibleRef.current = visible;
    recordDrilldownMeasurement(drilldownLoadGate.setPresentationReady(true));
    recordDrilldownMeasurement(drilldownLoadGate.setFirstRowVisible(visible));
  }, [drilldownLoadGate, recordDrilldownMeasurement]);

  useLayoutEffect(() => {
    if (!activeDrilldownQueryKey) {
      drilldownLoadGate.cancel();
      drilldownLoadQueryKeyRef.current = null;
      drilldownLoadKindRef.current = null;
      drilldownPhysicalRowVisibleRef.current = false;
      drilldownPhysicalPresentationReadyRef.current = false;
      return;
    }
    return () => {
      if (drilldownLoadQueryKeyRef.current !== activeDrilldownQueryKey) return;
      drilldownRequestGenerationRef.current += 1;
      drilldownLoadGate.cancel();
      drilldownLoadQueryKeyRef.current = null;
      drilldownLoadKindRef.current = null;
      drilldownPhysicalRowVisibleRef.current = false;
      drilldownPhysicalPresentationReadyRef.current = false;
    };
  }, [activeDrilldownQueryKey, drilldownLoadGate]);

  useLayoutEffect(() => {
    if (
      !activeDrilldownQueryKey
      || activeDrilldownQuery?.data === undefined
      || activeDrilldownQuery.isFetching
      || drilldownLoadQueryKeyRef.current !== activeDrilldownQueryKey
    ) return;
    if (isDrilldownError) {
      // 同条件完整快照仍在时保留旧行；只有没有快照才清空物理可见状态。
      if (displayedProductBranchRows.length > 0) return;
      drilldownPhysicalRowVisibleRef.current = false;
      drilldownLoadGate.setFirstRowVisible(false);
      return;
    }
    if (!activeDrilldownQuery.data.isComplete) {
      if (activeDrilldownQuery.data.pollingExhausted) {
        drilldownPhysicalRowVisibleRef.current = false;
        drilldownLoadGate.fail();
      }
      return;
    }
    if (activeDrilldownQuery.data.data.length === 0) {
      // 空结果没有首条业务行，不能把空态误记成 2 秒达标。
      drilldownPhysicalRowVisibleRef.current = false;
      drilldownLoadGate.cancel();
      return;
    }
    recordDrilldownMeasurement(drilldownLoadGate.markDataNormalized());
  }, [
    activeDrilldownQuery?.data,
    activeDrilldownQuery?.dataUpdatedAt,
    activeDrilldownQuery?.isFetching,
    activeDrilldownQueryKey,
    drilldownLoadGate,
    isDrilldownError,
    displayedProductBranchRows.length,
    recordDrilldownMeasurement,
  ]);
  const retryDrilldown = () => {
    if (drilldownKind === "product" && productBranchQueryParams) {
      // 先重验白名单，避免用上一次成功范围重试刚被停用的分店。
      void storeOptionsQuery.refetch();
    }
  };
  const drilldownEmptyLabel = t("productReport.states.emptyProducts");

  const setQuickRange = (key: ProductReportQuickRangeKey) => {
    const next = getProductReportQuickRange(key);
    setRange(next);
    setDraftStartDate(next.startDate);
    setDraftEndDate(next.endDate);
    setSelectedSupplierCode(null);
    setSupplierPage(1);
    setProductPage(1);
  };

  const applyKind = (nextKind: string) => {
    const next = nextKind as SupplierReportKind;
    setKind(next);
    setSelectedSupplierCode(null);
    setSupplierPage(1);
    setProductPage(1);
    // 两个页签的商品明细默认排序不同，切换页签时回到该页签的默认值，不把数量排序带回澳洲页签。
    setProductSort(getDefaultProductSort(next));
  };

  const applyStore = (storeCode?: string) => {
    setSelectedStoreCode(storeCode);
    setStoreModalVisible(false);
    // 三表联动：换分店保留已选供应商，供应商表与明细按新分店重取（交集为空时由提示条说明）。
    setSupplierPage(1);
    setProductPage(1);
  };

  // 分店表整行点按：选中分店，再点同一行取消。
  const toggleLinkedBranch = (branchCode: string) => {
    applyStore(isSameLinkedCode(selectedStoreCode, branchCode) ? undefined : branchCode);
  };

  // 供应商名称点按：选中供应商，再点同一行取消；明细回到第 1 页。
  const toggleLinkedSupplier = (supplierCode: string) => {
    setSelectedSupplierCode((current) => (isSameLinkedCode(current, supplierCode) ? null : supplierCode));
    setProductPage(1);
  };

  const clearLinkedSupplier = () => {
    setSelectedSupplierCode(null);
    setProductPage(1);
  };

  const clearLinkedSelection = () => {
    applyStore(undefined);
    clearLinkedSupplier();
  };

  const recordSectionOffset = (key: LinkedAnchorKey) => (event: LayoutChangeEvent) => {
    sectionOffsetsRef.current[key] = event.nativeEvent.layout.y;
  };

  // 锚点跳转：让区块标题落在吸顶联动条下方，而不是被它盖住。
  const scrollToLinkedSection = (key: LinkedAnchorKey) => {
    scrollRef.current?.scrollTo({
      y: Math.max(0, sectionOffsetsRef.current[key] - linkedBarHeightRef.current - 8),
      animated: true,
    });
  };

  const updateDraftStartDate = (value: string) => {
    setDraftStartDate(value);
    const nextRange = getCustomProductReportRange(value, draftEndDate);
    if (nextRange) {
      setRange(nextRange);
    }
    setSelectedSupplierCode(null);
    setSupplierPage(1);
    setProductPage(1);
  };

  const updateDraftEndDate = (value: string) => {
    setDraftEndDate(value);
    const nextRange = getCustomProductReportRange(draftStartDate, value);
    if (nextRange) {
      setRange(nextRange);
    }
    setSelectedSupplierCode(null);
    setSupplierPage(1);
    setProductPage(1);
  };

  const applyProductSearch = () => {
    setProductSearch(productSearchDraft.trim());
    setProductPage(1);
  };

  const clearProductSearch = () => {
    setProductSearchDraft("");
    setProductSearch("");
    setProductPage(1);
  };

  const refresh = () => {
    resetMainReportVersionSync();
    if (onRefreshReport) {
      // 嵌入报告中心时统一走控制器，避免下拉与页头刷新并发。
      void onRefreshReport();
      return;
    }
    void onRefreshFreshness?.();
    void storeOptionsQuery.refetch();
  };

  // 明细换页、换筛选时靠上一份数据占位重取，属于局部加载（区块内显示「更新中」），
  // 不能驱动下拉刷新转圈，否则每次联动点选都会把整页往下推。
  const isRefreshing =
    storeOptionsQuery.isRefetching ||
    totalRevenueQuery.isRefetching ||
    supplierQuery.isRefetching ||
    (productQuery.isRefetching && !productQuery.isPlaceholderData) ||
    (isChinaKind && chinaBranchTotalsQuery.isRefetching) ||
    (hasChinaSupplierBranches && chinaSupplierBranchesQuery.isRefetching);
  const selectedBranchName = getStoreLabel(selectedStoreCode);
  // 锚点行数：分店与明细取表里实际显示的数据，正在按新条件重取的区块显示省略号。
  const linkedAnchors: LinkedAnchor[] = [
    ...(isChinaKind
      ? [{ key: "branches" as const, count: chinaBranchShareRows.length, updating: linkedStaleness.branchTable }]
      : []),
    { key: "suppliers", count: supplierRows.length, updating: linkedStaleness.supplierTable },
    { key: "products", count: productTotal, updating: linkedStaleness.productTable },
  ];

  const applySupplierSort = (field: ReportSortField) => {
    setSupplierSort((current) => toggleReportSort(current, field));
    setSupplierPage(1);
  };

  const applyProductSort = (field: ReportSortField) => {
    setProductSort((current) => toggleReportSort(current, field));
    // 商品明细由后端排序后分页，换排序必须从第 1 页重新取。
    setProductPage(1);
  };

  const applyDrilldownSort = (field: ReportSortField) => {
    setDrilldownSort((current) => toggleReportSort(current, field));
  };

  const openDrilldown = (next: Drilldown) => {
    // 每次打开分店弹窗都回到默认金额降序，不沿用上一个弹窗的排序。
    setDrilldownSort(DEFAULT_REPORT_SORT);
    setDrilldown(next);
  };

  // 澳洲与中国供应商共用同一张紧凑表：每家供应商给出一行模型，列顺序即表格顺序——
  // 金额、增长率、数量、占比（首屏），再左滑看 均价、毛利额、毛利率、总占比。
  // 澳洲只显示本期；中国在每格下方多一行灰色同期小字（表头标注「灰字＝同期」）。
  const buildSupplierTableRow = ({
    item,
    rowNumber,
  }: {
    item: SupplierReportRow;
    rowNumber: number;
  }): SupplierTableRow => {
    const isSelected = isSameLinkedCode(item.supplierCode, selectedSupplierCode);
    const showComparison = isChinaKind;
    // 同期值一律经过 compareText：澳洲页签返回 null，表格上不出现任何同期数值。
    const compareText = (value: string) => (showComparison ? value : null);
    // 占比分母跟随数据所属的分店：中国页签用该范围的中国货合计，澳洲页签用本表小计。
    const categoryRevenue = isChinaKind ? supplierScopeChinaGoods.revenue : supplierSubtotal;
    const categoryCompareRevenue = isChinaKind
      ? supplierScopeChinaGoods.compareRevenue
      : supplierCompareSubtotal;
    // 两条占比迷你条用同一标尺：满格 = 当前页签金额最大的供应商；总占比条再按「分类合计 ÷ 总营业额」等比缩短。
    const shareBarPercent = topSupplierRevenue > 0
      ? Math.min(100, Math.max(0, (item.revenue / topSupplierRevenue) * 100))
      : 0;
    const totalShareBarPercent = supplierScopeRevenue.revenue > 0
      ? shareBarPercent * Math.min(1, categoryRevenue / supplierScopeRevenue.revenue)
      : 0;

    return {
      id: item.id,
      rank: formatRowNumber(rowNumber),
      title: getSupplierTitle(item),
      // 中国页签名称下方固定显示编号；后端缺名回退成编号时留空，避免上下重复。
      code: shouldShowSupplierCode(item) ? item.supplierCode : "",
      selected: isSelected,
      accessibilityLabel: t("productReport.sheet.openA11y", { supplier: getSupplierTitle(item) }),
      // 点名称、金额或任一指标都打开同一个选择弹窗：商品明细 / 分店数据（联动筛选在弹窗里）。
      onPress: () => setSheetSupplier(item),
      cells: {
        revenue: { value: formatWholeMoney(item.revenue), compare: compareText(formatWholeMoney(item.compareRevenue)) },
        quantity: { value: formatCount(item.totalQuantity), compare: compareText(item.compareTotalQuantity === null ? "—" : formatCount(item.compareTotalQuantity)) },
        share: { value: formatShare(item.revenue, categoryRevenue), compare: compareText(formatShare(item.compareRevenue, categoryCompareRevenue)), barPercent: shareBarPercent },
        averagePrice: { value: formatNullableMoney(item.averagePrice), compare: compareText(formatNullableMoney(item.compareAveragePrice)) },
        grossProfit: { value: formatNullableWholeMoney(item.grossProfit), compare: compareText(formatNullableWholeMoney(item.compareGrossProfit)) },
        grossMargin: { value: formatGrossMarginRate(item.grossMarginRate, item.costStatus, costPendingLabel, costNoActivityLabel), pending: item.costStatus === "Missing", compare: compareText(formatGrossMarginRate(item.compareGrossMarginRate, item.compareCostStatus, costPendingLabel, costNoActivityLabel)) },
        totalShare: { value: formatShare(item.revenue, supplierScopeRevenue.revenue), compare: compareText(formatShare(item.compareRevenue, supplierScopeRevenue.compareRevenue)), barPercent: totalShareBarPercent },
      },
      // 增长率胶囊由表格放在「数量」和「占比」之间；这里只负责用同一套口径渲染。
      growth: <GrowthPill size="sm" fitText current={item.revenue} compare={item.compareRevenue} newLabel={growthNewLabel} />,
    };
  };

  // 两个供应商页签的商品明细共用同一张商品卡（与供应商弹窗里的商品卡是同一个组件），
  // 都只显示本期值（同期金额只用于算增长率）。
  const renderProductRow = ({
    item,
    rowNumber,
  }: {
    item: ProductReportProductRow;
    rowNumber: number;
  }) => (
    <ProductReportCard
      item={item}
      rowNumber={rowNumber}
      growthNewLabel={growthNewLabel}
      costPendingLabel={costPendingLabel}
      costNoActivityLabel={costNoActivityLabel}
      onPress={() => openDrilldown({ type: "product", product: item })}
    />
  );

  // 中国页签「本页合计」：固定在商品卡片列表上方，字段与商品卡一致，只有本期值。
  const renderChinaProductPageTotalCard = () => {
    const totals = chinaProductPageTotals;
    return (
      <View style={[styles.pageTotalCard, linkedStaleness.productTable ? styles.staleSection : null]}>
        <View style={styles.cardTitleRow}>
          <Text style={styles.pageTotalTitle}>{t("productReport.chinaGoods.pageTotal")}</Text>
          <Text style={styles.caption} numberOfLines={1}>
            {t("productReport.pageSummaryCaption", {
              start: productTotal === 0 ? 0 : (displayedProductPage - 1) * displayedProductPageSize + 1,
              end: Math.min(displayedProductPage * displayedProductPageSize, productTotal),
              total: productTotal,
            })}
          </Text>
        </View>
        <View style={styles.amountLine}>
          <Text style={styles.pageTotalAmount} numberOfLines={1}>{formatWholeMoney(totals.salesAmount)}</Text>
          <GrowthPill current={totals.salesAmount} compare={totals.compareSalesAmount} newLabel={growthNewLabel} />
        </View>
        <MetricGrid
          style={[styles.cardGrid, styles.pageTotalGrid]}
          cells={[
            { key: "quantity", label: t("productReport.columns.quantity"), value: formatCount(totals.quantity) },
            { key: "averagePrice", label: t("productReport.columns.averagePrice"), value: formatNullableMoney(totals.averageUnitPrice) },
            { key: "grossProfit", label: t("productReport.metrics.grossProfit"), value: formatNullableWholeMoney(totals.grossProfit) },
            { key: "grossMargin", label: t("productReport.metrics.grossMarginRate"), value: formatGrossMarginRate(totals.grossMarginRate, totals.costStatus, costPendingLabel, costNoActivityLabel), pending: totals.costStatus === "Missing" },
          ]}
        />
      </View>
    );
  };

  const drilldownSubtitle = drilldown?.type === "product"
    ? [drilldown.product.itemNumber || drilldown.product.productCode, drilldown.product.productName]
        .filter(Boolean)
        .join(" · ")
    : undefined;

  const supplierTableRows = supplierPageRows.map((item, index) => buildSupplierTableRow({
    item,
    rowNumber: (supplierPage - 1) * supplierPageSize + index + 1,
  }));

  return (
    <View style={styles.container}>
      <ScrollView
        ref={scrollRef}
        bounces={false}
        onScroll={markProductDataVisible}
        scrollEventThrottle={16}
        contentContainerStyle={styles.content}
        stickyHeaderIndices={[LINKED_FILTER_BAR_INDEX]}
        refreshControl={<RefreshControl refreshing={isRefreshing} onRefresh={refresh} />}
      >
        {/* 第 1 个直接子元素：（独立页面时的标题 +）澳洲 / 中国供应商分段，始终存在。 */}
        <View style={styles.topBlock}>
          {!embedded ? (
            <Text variant="headlineSmall" style={styles.title}>
              {t("productReport.title")}
            </Text>
          ) : null}
          <SegmentToggle
            value={kind}
            onChange={applyKind}
            options={[
              { value: "australia", label: t("productReport.tabs.australia") },
              { value: "china", label: t("productReport.tabs.china") },
            ]}
          />
        </View>

        {/* 第 2 个直接子元素：日期卡。原「日期区间」文字并入两个日期输入框，格式错误提示写在卡内。 */}
        <View style={styles.dateCard}>
          <View style={styles.dateInputs}>
            <TextInput
              mode="outlined"
              dense
              label={t("productReport.filters.startDate")}
              value={draftStartDate}
              onChangeText={updateDraftStartDate}
              style={styles.dateInput}
              autoCapitalize="none"
            />
            <TextInput
              mode="outlined"
              dense
              label={t("productReport.filters.endDate")}
              value={draftEndDate}
              onChangeText={updateDraftEndDate}
              style={styles.dateInput}
              autoCapitalize="none"
            />
          </View>
          {!dateRangeValid ? (
            <Text variant="labelSmall" style={styles.dateError}>{t("productReport.states.invalidDate")}</Text>
          ) : null}
          <View style={styles.quickBar}>
            {(["today", "yesterday", "thisWeek", "lastWeek", "thisMonth", "lastMonth"] as const).map((key) => (
              <FilterChip
                key={key}
                label={t(`productReport.shortcuts.${key}`)}
                selected={range.key === key}
                onPress={() => setQuickRange(key)}
              />
            ))}
          </View>
        </View>

        {/* 吸顶联动条：ScrollView 第 3 个直接子元素（LINKED_FILTER_BAR_INDEX），前面两个子元素不能增删。 */}
        <LinkedFilterBar
          branchName={selectedBranchName}
          supplierName={selectedSupplierLabel}
          hint={isChinaKind ? t("productReport.linked.hintChina") : t("productReport.linked.hintAustralia")}
          branchPickerDisabled={
            !storeOptionsQuery.isSuccess
            || storeOptionsQuery.isFetching
            || cashierEnabledStoreCodes.length === 0
          }
          anchors={linkedAnchors}
          onOpenBranchPicker={() => setStoreModalVisible(true)}
          onClearBranch={() => applyStore(undefined)}
          onPressSupplier={() => scrollToLinkedSection("suppliers")}
          onClearSupplier={clearLinkedSupplier}
          onClearAll={clearLinkedSelection}
          onPressAnchor={scrollToLinkedSection}
          onLayout={(event) => {
            linkedBarHeightRef.current = event.nativeEvent.layout.height;
          }}
        />

        {!dateRangeValid ? (
          <View style={styles.stateBox}>
            <Text variant="bodyMedium">{t("productReport.states.invalidDate")}</Text>
          </View>
        ) : mainReportRequestError && !mainReportHasDisplay ? (
          <ErrorState
            label={t("productReport.states.error")}
            retryLabel={t("actions.retry")}
            onRetry={() => {
              resetMainReportVersionSync();
              void storeOptionsQuery.refetch();
            }}
          />
        ) : mainReportStatisticsPending && !mainReportHasDisplay ? (
          <LoadingState label={t("reports.states.refreshingStatistics")} />
        ) : mainReportStatisticsIncomplete && !mainReportHasDisplay ? (
          <ErrorState
            label={mainReportStatisticsFailed
              ? t("reports.states.statisticsFailed")
              : t("reports.states.statisticsIncomplete")}
            retryLabel={t("actions.retry")}
            onRetry={() => {
              resetMainReportVersionSync();
              void storeOptionsQuery.refetch();
            }}
          />
        ) : storeOptionsQuery.isSuccess && cashierEnabledStoreCodes.length === 0 ? (
          <EmptyState label={t("reports.states.noCashierEnabledStores")} />
        ) : !displayedMainReport ? (
          // 兜底：切页签或改日期后，其余主数据已到齐、商品明细仍是上一组条件的占位数据时，
          // 既没有同条件快照也不能沿用旧报告过渡；此时停在整页加载，不能渲染零值汇总与空表。
          <LoadingState label={t("reports.states.refreshingStatistics")} />
        ) : (
          <>
            {mainReportHasSnapshot && (mainReportQueriesFetching || mainReportRequestError || mainReportStatisticsPending || mainReportStatisticsIncomplete) ? (
              <Text variant="labelSmall" style={styles.snapshotNotice}>
                {t("reports.states.showingSnapshot", {
                  time: formatReportSnapshotTime(mainReportSnapshot?.statisticUpdatedAt ?? null)
                    ?? t("reports.freshness.noSuccess"),
                })}
              </Text>
            ) : linkedTransition && (mainReportRequestError || mainReportStatisticsIncomplete) ? (
              // 联动点选后新数据失败或统计未完成：保留上一次完整结果，并给出重试入口。
              <View style={styles.linkedNotice}>
                <Text variant="labelSmall" style={styles.linkedNoticeText}>{t("productReport.linked.updateFailed")}</Text>
                <Button
                  compact
                  mode="text"
                  onPress={() => {
                    resetMainReportVersionSync();
                    void storeOptionsQuery.refetch();
                  }}
                >
                  {t("actions.retry")}
                </Button>
              </View>
            ) : null}
            {isChinaKind ? (
              // 中国页签顶部：中国货汇总与分店中国货占比；「本页合计」放在商品明细卡片列表上方。
              <>
                <ChinaGoodsSummaryCard summary={chinaGoodsSummary} />
                <ChinaBranchShareSection
                  rows={chinaBranchShareRows}
                  selectedBranchCode={selectedStoreCode ?? null}
                  onSelectBranch={toggleLinkedBranch}
                  supplierName={getSupplierLabel(branchTableSupplierCode)}
                  updating={linkedStaleness.branchTable}
                  onLayout={recordSectionOffset("branches")}
                />
              </>
            ) : productSectionLoading ? (
              <View style={[styles.card, styles.productSummaryLoading]}>
                <ActivityIndicator size="small" />
                <Text variant="bodySmall" style={styles.muted}>
                  {t("productReport.states.loading")}
                </Text>
              </View>
            ) : (
              <View style={linkedStaleness.productTable ? styles.staleSection : null}>
                <ProductPageSummaryCard
                  summary={productPageSummary}
                  caption={t("productReport.pageSummaryCaption", {
                    start: productTotal === 0 ? 0 : (displayedProductPage - 1) * displayedProductPageSize + 1,
                    end: Math.min(displayedProductPage * displayedProductPageSize, productTotal),
                    total: productTotal,
                  })}
                />
              </View>
            )}
            <View style={styles.reportSection} onLayout={recordSectionOffset("suppliers")}>
              <SupplierSectionHeader
                title={t("productReport.sections.suppliers")}
                badges={displayedContext?.branchCode ? (
                  <ScopePill tone="branch" label={t("productReport.linked.branchScope", { branch: getStoreLabel(displayedContext.branchCode) })} />
                ) : null}
                updating={linkedStaleness.supplierTable}
                page={supplierPage}
                pageCount={supplierPageCount}
                onPrevious={() => setSupplierPage((current) => Math.max(1, current - 1))}
                onNext={() => setSupplierPage((current) => Math.min(supplierPageCount, current + 1))}
                previousLabel={t("productReport.actions.previous")}
                nextLabel={t("productReport.actions.next")}
              />
              {selectedSupplierMissing ? (
                // 已选供应商在所选分店本期无销售：保留选择，不自动清除，由用户决定。
                <View style={styles.supplierMissingNotice}>
                  <View style={styles.supplierMissingDot} />
                  <Text variant="bodySmall" style={styles.supplierMissingText}>
                    {t("productReport.linked.supplierMissing", { supplier: selectedSupplierLabel, branch: selectedBranchName })}
                  </Text>
                  <Button compact mode="outlined" textColor={LINKED_COLORS.supplierText} onPress={clearLinkedSupplier}>
                    {t("productReport.actions.clearSupplier")}
                  </Button>
                </View>
              ) : null}
              {supplierQuery.isLoading && !mainReportHasDisplay ? (
                <LoadingState label={t("productReport.states.loading")} />
              ) : (
                // 供应商紧凑表直接排在页面 ScrollView 里（纵向仍是单一滚动），只有表内右侧指标列横向滑动。
                <View style={linkedStaleness.supplierTable ? styles.staleSection : null}>
                  {supplierPageRows.length === 0 ? (
                    <EmptyState label={t("productReport.states.emptySuppliers")} />
                  ) : (
                    <SupplierReportTable
                      rows={supplierTableRows}
                      isChina={isChinaKind}
                      hint={t("productReport.supplierTable.hint")}
                      sort={supplierSort}
                      onSort={applySupplierSort}
                      firstRowRef={firstSupplierReportRowRef}
                      onFirstRowLayout={scheduleProductDataVisibilityCheck}
                    />
                  )}
                </View>
              )}
              <View style={styles.supplierFooter}>
                <PageSizePicker
                  pageSize={supplierPageSize}
                  options={SUPPLIER_PAGE_SIZE_OPTIONS}
                  onChange={(size) => { setSupplierPageSize(size); setSupplierPage(1); }}
                  testIDPrefix="product-report-supplier-page-size"
                />
                {supplierPageCount > 1 ? (
                  // 列表底部同样可以翻页；翻页后回到供应商区块顶部，从新一页第一行开始看。
                  <CompactPager
                    page={supplierPage}
                    pageCount={supplierPageCount}
                    onPrevious={() => {
                      setSupplierPage((current) => Math.max(1, current - 1));
                      scrollToLinkedSection("suppliers");
                    }}
                    onNext={() => {
                      setSupplierPage((current) => Math.min(supplierPageCount, current + 1));
                      scrollToLinkedSection("suppliers");
                    }}
                    previousLabel={t("productReport.actions.previous")}
                    nextLabel={t("productReport.actions.next")}
                  />
                ) : null}
              </View>
            </View>

            <View style={styles.reportSection} onLayout={recordSectionOffset("products")}>
              <SectionHeader
                title={t("productReport.sections.products")}
                hint={t("productReport.chinaGoods.productHint")}
                badges={displayedContext?.branchCode || displayedContext?.supplierCode ? (
                  <>
                    {displayedContext.branchCode ? (
                      <ScopePill tone="branch" label={getStoreLabel(displayedContext.branchCode) ?? ""} />
                    ) : null}
                    {displayedContext.supplierCode ? (
                      <ScopePill tone="supplier" label={getSupplierLabel(displayedContext.supplierCode) ?? ""} />
                    ) : null}
                  </>
                ) : null}
                updating={linkedStaleness.productTable}
                toolbar={(
                  <View style={styles.productSearchBar}>
                    <TextInput
                      mode="outlined"
                      dense
                      label={t("productReport.filters.productSearch")}
                      value={productSearchDraft}
                      onChangeText={setProductSearchDraft}
                      onSubmitEditing={applyProductSearch}
                      returnKeyType="search"
                      autoCapitalize="none"
                      style={styles.productSearchInput}
                    />
                    <Button compact mode="contained" onPress={applyProductSearch}>
                      {t("productReport.actions.searchProduct")}
                    </Button>
                    {productSearch ? (
                      <Button compact mode="outlined" onPress={clearProductSearch}>
                        {t("productReport.actions.clearProductSearch")}
                      </Button>
                    ) : null}
                  </View>
                )}
                sortFields={PRODUCT_SORT_FIELDS}
                sort={productSort}
                onSort={applyProductSort}
                page={productPage}
                pageCount={productPageCount}
                onPrevious={() => setProductPage((current) => Math.max(1, current - 1))}
                onNext={() => setProductPage((current) => Math.min(productPageCount, current + 1))}
                pageSize={productPageSize}
                pageSizeOptions={PRODUCT_PAGE_SIZE_OPTIONS}
                onPageSizeChange={(size) => { setProductPageSize(size); setProductPage(1); }}
                pageSizeTestID="product-report-product-page-size"
                previousLabel={t("productReport.actions.previous")}
                nextLabel={t("productReport.actions.next")}
              />
              {productSectionLoading ? (
                <LoadingState label={t("productReport.states.loading")} />
              ) : (
                <>
                  {/* 中国页签「本页合计」固定在商品卡片列表上方。 */}
                  {isChinaKind && productRows.length > 0 ? renderChinaProductPageTotalCard() : null}
                  <View style={[styles.cardList, linkedStaleness.productTable ? styles.staleSection : null]}>
                    {productRows.length === 0 ? (
                      displayedContext?.productSearch ? (
                        <EmptyState label={t("productReport.states.emptyProductSearch")} />
                      ) : displayedContext?.branchCode || displayedContext?.supplierCode ? (
                        // 分店与供应商的交集没有销售：说明是筛选组合为空，并给出清除入口。
                        <EmptyState
                          label={t("productReport.linked.emptyFiltered")}
                          actionLabel={t("productReport.linked.clearFilters")}
                          onAction={clearLinkedSelection}
                        />
                      ) : (
                        <EmptyState label={t("productReport.states.emptyProducts")} />
                      )
                    ) : (
                      productRows.map((item, index) => (
                        <View
                          key={item.id}
                          ref={index === 0 ? firstProductReportRowRef : undefined}
                          collapsable={index === 0 ? false : undefined}
                          onLayout={index === 0 ? scheduleProductDataVisibilityCheck : undefined}
                        >
                          {renderProductRow({
                            item,
                            rowNumber: (displayedProductPage - 1) * displayedProductPageSize + index + 1,
                          })}
                        </View>
                      ))
                    )}
                  </View>
                </>
              )}
              {productPageCount > 1 ? (
                <View style={styles.bottomPager}>
                  <CompactPager
                    page={productPage}
                    pageCount={productPageCount}
                    onPrevious={() => {
                      setProductPage((current) => Math.max(1, current - 1));
                      scrollToLinkedSection("products");
                    }}
                    onNext={() => {
                      setProductPage((current) => Math.min(productPageCount, current + 1));
                      scrollToLinkedSection("products");
                    }}
                    previousLabel={t("productReport.actions.previous")}
                    nextLabel={t("productReport.actions.next")}
                  />
                </View>
              ) : null}
            </View>
          </>
        )}
      </ScrollView>

      <StorePickerModal
        visible={
          isStoreModalVisible
          && storeOptionsQuery.isSuccess
          && !storeOptionsQuery.isFetching
        }
        labelAll={t("productReport.filters.allStores")}
        options={
          storeOptionsQuery.isSuccess && !storeOptionsQuery.isFetching
            ? storeOptionsQuery.data ?? []
            : []
        }
        selectedStoreCode={selectedStoreCode}
        onSelect={applyStore}
        onDismiss={() => setStoreModalVisible(false)}
      />
      {sheetSupplier ? (
        <SupplierDetailSheet
          // 换供应商时整体重建，视图、排序、页码都回到初始状态。
          key={sheetSupplier.supplierCode}
          supplier={sheetSupplier}
          kind={kind}
          queryParams={allBranchQueryParams}
          rangeLabel={`${activeRange.startDate} ~ ${activeRange.endDate}`}
          accountIdentity={accountIdentity}
          scopeVersion={cashierStoreScopeVersion}
          summaryShareText={`${isChinaKind ? t("productReport.chinaGoods.compareShareOfChina") : t("productReport.metrics.supplierShare")} ${
            formatShare(sheetSupplier.revenue, isChinaKind ? supplierScopeChinaGoods.revenue : supplierSubtotal)
          }`}
          growthNewLabel={growthNewLabel}
          costPendingLabel={costPendingLabel}
          costNoActivityLabel={costNoActivityLabel}
          onClose={() => setSheetSupplier(null)}
          onLinkFilter={toggleLinkedSupplier}
        />
      ) : null}
      <BranchDrilldownModal
        visible={
          Boolean(drilldown)
          && reportScopeValid
        }
        title={t("productReport.drilldown.product")}
        subtitle={drilldownSubtitle}
        productRows={displayedProductBranchRows}
        isLoading={isDrilldownLoading}
        isError={(isDrilldownError || isDrilldownStatisticsIncomplete)
          && displayedProductBranchRows.length === 0}
        snapshotNotice={drilldownShowingSnapshot
          ? t("reports.states.showingSnapshot", {
              time: formatReportSnapshotTime(activeDrilldownSnapshot?.statisticUpdatedAt ?? null)
                ?? t("reports.freshness.noSuccess"),
            })
          : undefined}
        onRetry={retryDrilldown}
        onDismiss={() => setDrilldown(null)}
        closeLabel={t("actions.close")}
        retryLabel={t("actions.retry")}
        errorLabel={isDrilldownStatisticsIncomplete
          ? t(activeDrilldownQuery?.data?.statisticStatus === "Failed"
            ? "reports.states.statisticsFailed"
            : "reports.states.statisticsIncomplete")
          : t("productReport.states.error")}
        emptyLabel={drilldownEmptyLabel}
        sort={drilldownSort}
        onSortChange={applyDrilldownSort}
        growthNewLabel={growthNewLabel}
        costPendingLabel={costPendingLabel}
        costNoActivityLabel={costNoActivityLabel}
        onFirstDataVisibilityChange={updateDrilldownFirstDataVisibility}
      />
    </View>
  );
}

function useSortChipFields(fields: readonly ReportSortField[]) {
  const { t } = useAppTranslation("common");
  return fields.map((field) => ({ field, label: t(SORT_FIELD_LABEL_KEYS[field]) }));
}

/**
 * 区块头：标题 + 范围标签 + 更新中，提示；可选工具行（商品明细的货号查询）；
 * 排序芯片与「‹ 1/3 ›」翻页同一行，「每页」分段另起一行。
 */
function SectionHeader({
  title,
  hint,
  badges,
  updating = false,
  toolbar,
  sortFields,
  sort,
  onSort,
  page,
  pageCount,
  onPrevious,
  onNext,
  previousLabel,
  nextLabel,
  pageSize,
  pageSizeOptions,
  onPageSizeChange,
  pageSizeTestID,
}: {
  title: string;
  hint?: string;
  // 卡片里数据所属的联动范围（分店、供应商标签），描述的是当前显示的数据而不是刚点选的条件。
  badges?: ReactNode;
  // 正在按新的联动条件重取：旧数据变淡，标题旁显示「更新中」。
  updating?: boolean;
  toolbar?: ReactNode;
  sortFields: readonly ReportSortField[];
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
  page: number;
  pageCount: number;
  onPrevious: () => void;
  onNext: () => void;
  previousLabel: string;
  nextLabel: string;
  pageSize: number;
  pageSizeOptions: readonly number[];
  onPageSizeChange: (pageSize: number) => void;
  pageSizeTestID: string;
}) {
  const { t } = useAppTranslation("common");
  const chipFields = useSortChipFields(sortFields);
  return (
    <View style={styles.sectionHeaderBlock}>
      <View style={styles.sectionTitleBlock}>
        <View style={styles.sectionTitleLine}>
          <Text style={styles.sectionTitle}>{title}</Text>
          {badges}
          {updating ? (
            <View style={styles.updatingIndicator} accessibilityRole="progressbar">
              <ActivityIndicator size={12} />
              <Text variant="labelSmall" style={styles.updatingText}>{t("productReport.linked.updating")}</Text>
            </View>
          ) : null}
        </View>
        {hint ? <Text style={styles.hintText} numberOfLines={2}>{hint}</Text> : null}
      </View>
      {toolbar}
      <View style={styles.sectionControls}>
        <SortChips fields={chipFields} sort={sort} onSort={onSort} />
        <CompactPager
          page={page}
          pageCount={pageCount}
          onPrevious={onPrevious}
          onNext={onNext}
          previousLabel={previousLabel}
          nextLabel={nextLabel}
        />
      </View>
      <PageSizePicker
        pageSize={pageSize}
        options={pageSizeOptions}
        onChange={onPageSizeChange}
        testIDPrefix={pageSizeTestID}
      />
    </View>
  );
}

/** 区块标题旁的联动范围标签：分店蓝、供应商紫，与吸顶联动条和选中卡片同色。 */
/** 「每页 50 / 100」分段：点已选中的档位不触发回调，避免无谓地回到第 1 页。 */
function PageSizePicker({
  pageSize,
  options,
  onChange,
  testIDPrefix,
}: {
  pageSize: number;
  options: readonly number[];
  onChange: (pageSize: number) => void;
  testIDPrefix: string;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={styles.pageSizeRow}>
      <Text style={styles.pageSizeLabel}>{t("pagination.perPage")}</Text>
      <View style={styles.pageSizeSegment} accessibilityRole="radiogroup">
        {options.map((option) => {
          const selected = option === pageSize;
          return (
            <Pressable
              key={option}
              testID={`${testIDPrefix}-${option}`}
              accessibilityRole="radio"
              accessibilityLabel={t("pagination.perPageOption", { count: option })}
              accessibilityState={{ checked: selected }}
              hitSlop={{ top: 8, bottom: 8 }}
              // 点已选中的档位不触发回调，避免无谓地回到第 1 页
              onPress={() => { if (!selected) onChange(option); }}
              style={[styles.pageSizeButton, selected && styles.pageSizeSelected]}
            >
              <Text style={[styles.pageSizeText, selected && styles.pageSizeTextSelected]}>{option}</Text>
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

/**
 * 供应商区块头：标题 + 范围标签 + 更新中，右侧翻页。排序改在表头点按，每页条数挪到列表底部，
 * 把首屏高度让给供应商行。
 */
function SupplierSectionHeader({
  title,
  badges,
  updating,
  page,
  pageCount,
  onPrevious,
  onNext,
  previousLabel,
  nextLabel,
}: {
  title: string;
  badges?: ReactNode;
  updating: boolean;
  page: number;
  pageCount: number;
  onPrevious: () => void;
  onNext: () => void;
  previousLabel: string;
  nextLabel: string;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={styles.supplierHeaderRow}>
      <View style={styles.sectionTitleLine}>
        <Text style={styles.sectionTitle}>{title}</Text>
        {badges}
        {updating ? (
          <View style={styles.updatingIndicator} accessibilityRole="progressbar">
            <ActivityIndicator size={12} />
            <Text variant="labelSmall" style={styles.updatingText}>{t("productReport.linked.updating")}</Text>
          </View>
        ) : null}
      </View>
      <CompactPager
        page={page}
        pageCount={pageCount}
        onPrevious={onPrevious}
        onNext={onNext}
        previousLabel={previousLabel}
        nextLabel={nextLabel}
      />
    </View>
  );
}

function ScopePill({ tone, label }: { tone: "branch" | "supplier"; label: string }) {
  const branch = tone === "branch";
  return (
    <View style={[styles.scopePill, branch ? styles.branchScopePill : styles.supplierScopePill]}>
      <Text
        numberOfLines={1}
        style={[styles.scopePillText, { color: branch ? LINKED_COLORS.branchText : LINKED_COLORS.supplierText }]}
      >
        {label}
      </Text>
    </View>
  );
}

function LoadingState({ label }: { label: string }) {
  return (
    <View style={styles.stateBox}>
      <ActivityIndicator />
      <Text>{label}</Text>
    </View>
  );
}

function ErrorState({ label, retryLabel, onRetry }: { label: string; retryLabel: string; onRetry: () => void }) {
  return (
    <View style={styles.stateBox}>
      <Text variant="bodyMedium">{label}</Text>
      <Button mode="contained" onPress={onRetry}>
        {retryLabel}
      </Button>
    </View>
  );
}

function EmptyState({ label, actionLabel, onAction }: { label: string; actionLabel?: string; onAction?: () => void }) {
  return (
    <View style={styles.stateBox}>
      <Text variant="bodyMedium">{label}</Text>
      {actionLabel && onAction ? (
        <Button compact mode="outlined" onPress={onAction}>
          {actionLabel}
        </Button>
      ) : null}
    </View>
  );
}

function StorePickerModal({
  visible,
  labelAll,
  options,
  selectedStoreCode,
  onSelect,
  onDismiss,
}: {
  visible: boolean;
  labelAll: string;
  options: { label: string; value: string }[];
  selectedStoreCode?: string;
  onSelect: (storeCode?: string) => void;
  onDismiss: () => void;
}) {
  return (
    <Portal>
      <Modal visible={visible} onDismiss={onDismiss} contentContainerStyle={styles.modal}>
        <ScrollView
          bounces={false}
          nestedScrollEnabled
          showsVerticalScrollIndicator
          keyboardShouldPersistTaps="handled"
          style={styles.storeModalList}
          contentContainerStyle={styles.storeModalListContent}
        >
          <Button
            mode={!selectedStoreCode ? "contained" : "outlined"}
            onPress={() => onSelect(undefined)}
            style={styles.modalOption}
          >
            {labelAll}
          </Button>
          {options.map((option) => (
            <Button
              key={option.value}
              mode={selectedStoreCode === option.value ? "contained" : "outlined"}
              onPress={() => onSelect(option.value)}
              style={styles.modalOption}
            >
              {option.label}
            </Button>
          ))}
        </ScrollView>
      </Modal>
    </Portal>
  );
}

/**
 * 商品分店下钻底部抽屉：每个分店一张卡片行（名次 + 分店 + 增长率胶囊，下面一行五项指标，同期小字保留）。
 * 供应商的商品明细 / 分店数据弹窗见 supplier-detail-sheet.tsx。
 */
function BranchDrilldownModal({
  visible,
  title,
  subtitle,
  productRows,
  isLoading,
  isError,
  onRetry,
  onDismiss,
  closeLabel,
  retryLabel,
  errorLabel,
  emptyLabel,
  sort,
  onSortChange,
  growthNewLabel,
  costPendingLabel,
  costNoActivityLabel,
  onFirstDataVisibilityChange,
  snapshotNotice,
}: {
  visible: boolean;
  title: string;
  subtitle?: string;
  productRows: ProductBranchBreakdownRow[];
  isLoading: boolean;
  isError: boolean;
  onRetry: () => void;
  onDismiss: () => void;
  closeLabel: string;
  retryLabel: string;
  errorLabel: string;
  emptyLabel: string;
  sort: ReportSort;
  onSortChange: (field: ReportSortField) => void;
  growthNewLabel: string;
  costPendingLabel: string;
  costNoActivityLabel: string;
  onFirstDataVisibilityChange: (visible: boolean) => void;
  snapshotNotice?: string;
}) {
  const { t } = useAppTranslation("common");
  const { height: windowHeight } = useWindowDimensions();
  const insets = useSafeAreaInsets();
  const sortChipFields = useSortChipFields(PRODUCT_SORT_FIELDS);
  const rows = productRows;
  // 分店弹窗是全量数据，直接在前端排序；序号即当前排序下的名次，同值按分店编码兜底。
  const sortedProductRows = useMemo(
    () => sortReportRows(productRows, sort, PRODUCT_BRANCH_SORT_VALUES, (row) => row.branchCode),
    [productRows, sort],
  );
  const firstRowRef = useRef<View>(null);
  const visibilityTaskRef = useRef<{ cancel: () => void } | null>(null);
  const visibilityGenerationRef = useRef(0);
  const scheduleFirstRowVisibilityCheck = useCallback(() => {
    visibilityTaskRef.current?.cancel();
    const generation = visibilityGenerationRef.current + 1;
    visibilityGenerationRef.current = generation;
    visibilityTaskRef.current = InteractionManager.runAfterInteractions(() => {
      if (
        visibilityGenerationRef.current !== generation
        || !visible
        || isLoading
        || isError
        || rows.length === 0
      ) return;
      firstRowRef.current?.measureInWindow((_x, y, _width, measuredHeight) => {
        if (visibilityGenerationRef.current !== generation) return;
        const isVisible = measuredHeight > 0 && y < windowHeight && y + measuredHeight > 0;
        onFirstDataVisibilityChange(isVisible);
      });
    });
  }, [isError, isLoading, onFirstDataVisibilityChange, rows.length, visible, windowHeight]);
  useLayoutEffect(() => {
    if (!visible || isLoading || isError || rows.length === 0) return;
    scheduleFirstRowVisibilityCheck();
    return () => {
      visibilityGenerationRef.current += 1;
      visibilityTaskRef.current?.cancel();
      visibilityTaskRef.current = null;
    };
  }, [isError, isLoading, rows, scheduleFirstRowVisibilityCheck, visible]);
  return (
    <Portal>
      <Modal
        visible={visible}
        onDismiss={onDismiss}
        style={styles.sheetWrapper}
        contentContainerStyle={[styles.sheet, { paddingBottom: Math.max(insets.bottom, 12) }]}
      >
        <View style={styles.sheetHandle} />
        <View style={styles.sheetHead}>
          <View style={styles.sheetTitleBlock}>
            <Text style={styles.sheetTitle} numberOfLines={1}>{title}</Text>
            {subtitle ? <Text style={styles.sheetSubtitle} numberOfLines={1}>{subtitle}</Text> : null}
          </View>
          <IconButton
            icon="close"
            mode="outlined"
            size={18}
            accessibilityLabel={closeLabel}
            onPress={onDismiss}
            style={styles.sheetClose}
          />
        </View>
        <View style={styles.sheetBody}>
          {snapshotNotice ? <Text variant="labelSmall" style={styles.sheetNotice}>{snapshotNotice}</Text> : null}
          {isLoading ? (
            <LoadingState label={t("productReport.states.loading")} />
          ) : isError ? (
            <ErrorState label={errorLabel} retryLabel={retryLabel} onRetry={onRetry} />
          ) : rows.length === 0 ? (
            <EmptyState label={emptyLabel} />
          ) : (
            <>
              <SortChips fields={sortChipFields} sort={sort} onSort={onSortChange} />
              <ScrollView
                bounces={false}
                onScroll={scheduleFirstRowVisibilityCheck}
                scrollEventThrottle={16}
                style={styles.sheetList}
                contentContainerStyle={styles.sheetListContent}
              >
                {sortedProductRows.map((row, index) => (
                  <ProductBranchRow
                    key={row.id}
                    row={row}
                    rowNumber={index + 1}
                    growthNewLabel={growthNewLabel}
                    costPendingLabel={costPendingLabel}
                    costNoActivityLabel={costNoActivityLabel}
                    rowRef={index === 0 ? firstRowRef : undefined}
                  />
                ))}
              </ScrollView>
            </>
          )}
        </View>
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: REPORT_UI.background,
  },
  content: {
    gap: 10,
    padding: 16,
    paddingBottom: 40,
  },
  topBlock: {
    gap: 10,
  },
  title: {
    color: REPORT_UI.text,
    fontWeight: "700",
  },
  muted: {
    color: REPORT_UI.muted,
  },
  caption: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
    lineHeight: 16,
    fontVariant: ["tabular-nums"],
  },
  snapshotNotice: {
    color: "#B45309",
    marginBottom: 4,
  },
  linkedNotice: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    borderRadius: 8,
    backgroundColor: "#FFFBEB",
    paddingLeft: 10,
  },
  linkedNoticeText: {
    flex: 1,
    color: "#B45309",
  },
  // 局部加载：依赖变化的区块在新数据到齐前变淡，旧数据仍可读、可点。
  staleSection: {
    opacity: LINKED_STALE_OPACITY,
  },
  supplierMissingNotice: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderWidth: 1,
    borderColor: LINKED_COLORS.supplierBorder,
    borderRadius: 10,
    backgroundColor: LINKED_COLORS.supplierBackground,
    paddingLeft: 10,
    paddingRight: 4,
    paddingVertical: 4,
  },
  supplierMissingDot: {
    width: 7,
    height: 7,
    borderRadius: 4,
    backgroundColor: LINKED_COLORS.supplier,
  },
  supplierMissingText: {
    flex: 1,
    minWidth: 0,
    color: LINKED_COLORS.supplierText,
  },
  dateCard: {
    gap: 8,
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
    padding: 10,
  },
  dateInputs: {
    flexDirection: "row",
    gap: 8,
  },
  dateInput: {
    flex: 1,
    minWidth: 0,
    backgroundColor: REPORT_UI.card,
  },
  dateError: {
    color: "#B42318",
  },
  quickBar: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
  },
  productSearchBar: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 6,
  },
  productSearchInput: {
    flex: 1,
    minWidth: 160,
    backgroundColor: REPORT_UI.card,
  },
  reportSection: {
    gap: 10,
    marginTop: 4,
  },
  card: {
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
    padding: 12,
  },
  cardTitleRow: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 8,
  },
  cardTitle: {
    flexShrink: 0,
    color: REPORT_UI.text,
    fontSize: 16,
    fontWeight: "700",
  },
  productSummaryLoading: {
    minHeight: 88,
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  summaryMatrixRow: {
    flexDirection: "row",
    alignItems: "center",
    borderBottomWidth: 1,
    borderBottomColor: REPORT_UI.divider,
    paddingVertical: 5,
  },
  summaryMatrixLabel: {
    width: 62,
    color: REPORT_UI.muted,
    fontSize: 11.5,
  },
  summaryMatrixLabelCurrent: {
    color: REPORT_UI.text,
    fontWeight: "700",
  },
  summaryMatrixCell: {
    flex: 1,
    minWidth: 0,
    paddingLeft: 4,
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  summaryMatrixHead: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
    fontWeight: "700",
  },
  summaryMatrixCurrent: {
    color: REPORT_UI.text,
    fontSize: 15,
    fontWeight: "800",
  },
  summaryMatrixCompare: {
    color: REPORT_UI.muted,
    fontSize: 12.5,
  },
  cardList: {
    gap: 8,
  },
  bottomPager: {
    alignItems: "center",
  },
  supplierHeaderRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  // 供应商表底部：左侧每页条数，右侧翻页。
  supplierFooter: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  pageTotalCard: {
    borderWidth: 1,
    borderColor: "#C7D7FE",
    borderRadius: 12,
    backgroundColor: "#EEF4FF",
    paddingHorizontal: 11,
    paddingVertical: 9,
  },
  pageTotalTitle: {
    flexShrink: 0,
    color: REPORT_UI.text,
    fontSize: 14,
    fontWeight: "700",
  },
  pageTotalAmount: {
    color: REPORT_UI.text,
    fontSize: 18,
    lineHeight: 24,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  pageTotalGrid: {
    backgroundColor: "rgba(255,255,255,0.7)",
  },
  sectionHeaderBlock: {
    gap: 8,
  },
  sectionTitleBlock: {
    minWidth: 0,
    gap: 3,
  },
  sectionTitleLine: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 6,
  },
  sectionTitle: {
    color: REPORT_UI.text,
    fontSize: 17,
    fontWeight: "800",
  },
  hintText: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
    lineHeight: 16,
  },
  updatingIndicator: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  updatingText: {
    color: "#475467",
  },
  scopePill: {
    maxWidth: 168,
    borderWidth: 1,
    borderRadius: 9,
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  branchScopePill: {
    borderColor: LINKED_COLORS.branchBorder,
    backgroundColor: LINKED_COLORS.branchBackground,
  },
  supplierScopePill: {
    borderColor: LINKED_COLORS.supplierBorder,
    backgroundColor: LINKED_COLORS.supplierBackground,
  },
  scopePillText: {
    fontSize: 11,
    lineHeight: 15,
    fontWeight: "700",
  },
  sectionControls: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 6,
  },
  pageSizeRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  pageSizeLabel: {
    color: REPORT_UI.muted,
    fontSize: 11.5,
  },
  pageSizeSegment: {
    flexDirection: "row",
    gap: 2,
    borderRadius: 9,
    backgroundColor: "#F2F4F7",
    padding: 3,
  },
  pageSizeButton: {
    minWidth: 40,
    height: 24,
    paddingHorizontal: 9,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 7,
  },
  pageSizeSelected: {
    backgroundColor: REPORT_UI.accent,
  },
  pageSizeText: {
    color: "#475467",
    fontSize: 12,
    fontWeight: "600",
    fontVariant: ["tabular-nums"],
  },
  pageSizeTextSelected: {
    color: "#FFFFFF",
  },
  amountLine: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    marginTop: 3,
  },
  cardGrid: {
    marginTop: 9,
  },
  stateBox: {
    minHeight: 72,
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
    padding: 12,
  },
  modal: {
    maxHeight: "82%",
    margin: 18,
    gap: 10,
    borderRadius: 8,
    backgroundColor: REPORT_UI.card,
    padding: 16,
  },
  modalOption: {
    alignSelf: "stretch",
  },
  storeModalList: {
    flexShrink: 1,
  },
  storeModalListContent: {
    gap: 10,
  },
  // 底部抽屉：Paper Modal 默认居中，这里改为贴底并去掉底部安全区外边距（由抽屉自己补内边距）。
  sheetWrapper: {
    justifyContent: "flex-end",
    marginBottom: 0,
  },
  sheet: {
    height: "88%",
    borderTopLeftRadius: 20,
    borderTopRightRadius: 20,
    backgroundColor: REPORT_UI.background,
    paddingTop: 8,
  },
  sheetHandle: {
    alignSelf: "center",
    width: 36,
    height: 4,
    borderRadius: 2,
    backgroundColor: "#D0D5DD",
    marginBottom: 6,
  },
  sheetHead: {
    flexDirection: "row",
    alignItems: "flex-start",
    justifyContent: "space-between",
    gap: 8,
    paddingLeft: 14,
    paddingRight: 6,
  },
  sheetTitleBlock: {
    flex: 1,
    minWidth: 0,
    paddingTop: 6,
  },
  sheetTitle: {
    color: REPORT_UI.text,
    fontSize: 19,
    fontWeight: "800",
  },
  sheetSubtitle: {
    color: REPORT_UI.muted,
    fontSize: 13,
  },
  sheetClose: {
    margin: 0,
  },
  sheetBody: {
    flex: 1,
    gap: 10,
    paddingHorizontal: 14,
    paddingTop: 8,
  },
  sheetNotice: {
    borderWidth: 1,
    borderColor: "#FEDF89",
    borderRadius: 10,
    backgroundColor: "#FFFAEB",
    color: "#B54708",
    paddingHorizontal: 10,
    paddingVertical: 6,
  },
  sheetList: {
    flex: 1,
  },
  sheetListContent: {
    overflow: "hidden",
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
  },
});
