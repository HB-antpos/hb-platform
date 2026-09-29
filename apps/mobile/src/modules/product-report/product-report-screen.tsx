import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ReactNode, RefObject } from "react";
import {
  Animated,
  Image,
  InteractionManager,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  useWindowDimensions,
  View,
  type LayoutChangeEvent,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Modal,
  Portal,
  SegmentedButtons,
  Text,
  TextInput,
} from "react-native-paper";
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
import { formatMoney, formatWholeMoney } from "@/modules/reports/format";
import {
  getCashierEnabledStoreCodes,
  getCashierScopedBranchCodes,
} from "@/modules/reports/cashier-enabled-store-scope";
import { GROWTH_COLORS, formatGrowthRate, getGrowthTone } from "@/modules/reports/growth-rate";
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
import { PRODUCT_PAGE_SIZE, SUPPLIER_PAGE_SIZE, getPageRows } from "@/modules/product-report/pagination";
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

type Drilldown =
  | { type: "supplier"; kind: SupplierReportKind; supplier: SupplierReportRow }
  | { type: "product"; product: ProductReportProductRow };

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

// 吸顶联动条在主 ScrollView 直接子元素中的位置：日期行（或标题）→ 页签 → 联动条。
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

// 商品分店均价由后端给出，数量为 0 时是 0，与商品明细的服务端排序口径一致。
const PRODUCT_BRANCH_SORT_VALUES: ReportSortValues<ProductBranchBreakdownRow> = {
  amount: (row) => [row.salesAmount, row.compareSalesAmount],
  quantity: (row) => [row.quantity, row.compareQuantity],
  unitPrice: (row) => [row.averageUnitPrice, row.compareAverageUnitPrice],
};

// 表头只有 38pt 高，上下扩大点击区域接近 44pt 触控下限。
const SORT_HEADER_HIT_SLOP = { top: 10, bottom: 10 };

// 中国供应商页签的商品明细默认按数量降序（用户 2026-09-22 确认）；澳洲页签保持金额降序。
const CHINA_PRODUCT_DEFAULT_SORT: ReportSort = { field: "quantity", order: "desc" };

function getDefaultProductSort(kind: SupplierReportKind): ReportSort {
  return kind === "china" ? CHINA_PRODUCT_DEFAULT_SORT : DEFAULT_REPORT_SORT;
}

// 中国页签供应商名称列固定 102pt（用户 2026-09-22 模拟器验收后要求比自适应宽度再窄 40%），
// 首屏依次露出金额、数量和「占中国货」，其余列左滑查看。
const CHINA_SUPPLIER_NAME_COLUMN_WIDTH = 102;
// 商品表的货号/名称列吃掉剩余宽度，让均价及之后的列正好落在首屏外。
// 视口 = 屏宽 − 内容区左右 16pt − 表格边框 1pt。
const CHINA_TABLE_VIEWPORT_INSET = 34;
// 行内左边距 4 + 图片 52 + 固定列分隔 1 + 数量 60 + 金额 84 + 四段间距 3，再留 4pt。
// 金额列宽必须与 styles.chinaProductAmountColumn 一致，否则金额会被挤出首屏（2026-09-22 模拟器实测）。
const CHINA_PRODUCT_FIRST_SCREEN_FIXED = 217;
const CHINA_PRODUCT_IMAGE_COLUMN_WIDTH = 52;

function getChinaLeadingColumnWidth(windowWidth: number, fixedWidth: number, minWidth: number, maxWidth: number) {
  return Math.round(Math.min(maxWidth, Math.max(minWidth, windowWidth - CHINA_TABLE_VIEWPORT_INSET - fixedWidth)));
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

function formatCount(value: number) {
  return Math.round(value).toLocaleString("en-AU");
}

function formatRowNumber(value: number) {
  return String(value).padStart(2, "0");
}

function formatNullableMoney(value: number | null) {
  return value === null ? "—" : formatMoney(value);
}

function formatNullableWholeMoney(value: number | null) {
  return value === null ? "—" : formatWholeMoney(value);
}

function formatGrossMarginRate(
  value: number | null,
  costStatus: ProductReportCostStatus,
  costPendingLabel: string,
  noActivityLabel: string,
) {
  // 后端统一返回 0-1 比率（例如 0.4 = 40%），展示层再换算为百分比。
  if (costStatus === "NoActivity") return noActivityLabel;
  if (costStatus === "Missing") return costPendingLabel;
  // Complete 但销售额为零时毛利率按数学定义为空值，不代表成本缺失。
  return value === null ? "—" : `${(value * 100).toFixed(1)}%`;
}

function mergeCostStatuses(statuses: readonly ProductReportCostStatus[]): ProductReportCostStatus {
  if (statuses.some((status) => status === "Missing")) return "Missing";
  if (statuses.some((status) => status === "Complete")) return "Complete";
  return "NoActivity";
}

function formatShare(value: number, denominator: number) {
  if (!Number.isFinite(value) || !Number.isFinite(denominator) || denominator <= 0) {
    return "--";
  }
  return `${((value / denominator) * 100).toFixed(1)}%`;
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

function TableCell({
  children,
  style,
  numeric,
}: {
  children: string;
  style?: object;
  numeric?: boolean;
}) {
  return (
    <Text
      variant="bodySmall"
      // 数值不能省略；超出常规列宽或系统放大字体时允许完整换行。
      numberOfLines={numeric ? undefined : 1}
      selectable
      style={[styles.tableCellText, numeric ? styles.numericText : null, style]}
    >
      {children}
    </Text>
  );
}

function SortableHeaderCell({
  label,
  field,
  sort,
  onSort,
  style,
}: {
  label: string;
  field: ReportSortField;
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
  style?: StyleProp<ViewStyle>;
}) {
  const { t } = useAppTranslation("common");
  const active = sort.field === field;
  const descending = sort.order === "desc";
  const state = !active
    ? t("productReport.sort.none")
    : descending
      ? t("productReport.sort.desc")
      : t("productReport.sort.asc");
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: active }}
      accessibilityLabel={t("productReport.sort.accessibilityLabel", { column: label, state })}
      hitSlop={SORT_HEADER_HIT_SLOP}
      onPress={() => onSort(field)}
      style={[styles.sortableHeader, style]}
    >
      <Text
        variant="bodySmall"
        numberOfLines={1}
        style={[styles.tableCellText, styles.headerText, styles.sortableHeaderLabel, active ? styles.sortActiveText : null]}
      >
        {label}
      </Text>
      {/* 箭头单独成段：标签被列宽截断时，排序状态仍然可见。 */}
      <Text variant="bodySmall" style={[styles.sortIndicator, active ? styles.sortActiveText : null]}>
        {active ? (descending ? "▼" : "▲") : "⇅"}
      </Text>
    </Pressable>
  );
}

function FrozenHorizontalTable({
  children,
}: {
  children: (scrollX: Animated.Value) => ReactNode;
}) {
  const scrollX = useRef(new Animated.Value(0)).current;
  return (
    <Animated.ScrollView
      horizontal
      bounces={false}
      scrollEventThrottle={16}
      onScroll={Animated.event(
        [{ nativeEvent: { contentOffset: { x: scrollX } } }],
        { useNativeDriver: true },
      )}
      showsHorizontalScrollIndicator>
      {children(scrollX)}
    </Animated.ScrollView>
  );
}

function FrozenLeadingColumns({
  scrollX,
  children,
  style,
  tone = "body",
}: {
  scrollX: Animated.Value;
  children: ReactNode;
  style: StyleProp<ViewStyle>;
  tone?: "body" | "header" | "selected";
}) {
  return (
    <Animated.View
      style={[
        styles.frozenLeadingColumns,
        tone === "header"
          ? styles.frozenHeaderColumns
          : tone === "selected"
            ? styles.frozenSelectedColumns
            : styles.frozenBodyColumns,
        style,
        { transform: [{ translateX: scrollX }] },
      ]}
    >
      {children}
    </Animated.View>
  );
}

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

  return (
    <View style={styles.productSummaryCard}>
      <View style={styles.productSummaryHeader}>
        <Text variant="titleMedium" style={styles.sectionTitle}>
          {t("productReport.sections.pageSummary")}
        </Text>
        <Text variant="bodySmall" style={styles.muted} numberOfLines={1}>
          {caption}
        </Text>
      </View>
      <ScrollView horizontal showsHorizontalScrollIndicator contentContainerStyle={styles.productSummaryScroll}>
        <View style={styles.productSummaryGrid}>
          <View style={styles.productSummaryLabelColumn}>
            <TableCell style={styles.headerText}> </TableCell>
            <TableCell style={styles.strongText}>{t("reports.metrics.current")}</TableCell>
            <TableCell style={styles.muted}>{t("productReport.metrics.compare")}</TableCell>
          </View>
          {metrics.map((metric) => (
            <View key={metric.key} style={styles.productSummaryMetric}>
              <TableCell numeric style={styles.headerText}>{metric.label}</TableCell>
              <TableCell numeric style={styles.strongText}>{metric.current}</TableCell>
              <TableCell numeric style={styles.muted}>{metric.compare}</TableCell>
            </View>
          ))}
        </View>
      </ScrollView>
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
  const drilldownLoadKindRef = useRef<"supplier" | "product" | null>(null);
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
  const [productSearchDraft, setProductSearchDraft] = useState("");
  const [productSearch, setProductSearch] = useState("");
  const [drilldown, setDrilldown] = useState<Drilldown | null>(null);
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
    setStoreModalVisible(false);
  }, [cashierEnabledStoreCodes.length, storeOptionsQuery.isFetching, storeOptionsQuery.isSuccess]);

  // 供应商营业额弹窗要看所有分店，不继承顶部单店筛选。
  const supplierBranchQueryParams = useMemo(
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
      productSortKey,
    ] as const,
    [accountIdentity, cashierStoreScopeVersion, kind, productPage, productSearch, productSortKey, queryParams, supplierFilterCodes],
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
          pageSize: PRODUCT_PAGE_SIZE,
          // 商品明细由后端排序后分页，不同排序的完整快照不能互相替代。
          sort: productSortKey,
        })
      : null,
    [accountIdentity, kind, productPage, productSearch, productSortKey, snapshotQueryParams, range.key, selectedSupplierCode],
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
          PRODUCT_PAGE_SIZE,
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
    }
    previousScopeCodesRef.current = scopeFingerprint;
    if (!reportScopeValid) {
      completeMainReportSnapshotsRef.clear();
      completeDrilldownSnapshotsRef.clear();
      lastDisplayedMainReportRef.current = null;
      setDrilldown(null);
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
          productSort: productSortKey,
        }
      : null,
    [kind, linkedScopeKey, productPage, productSearch, productSortKey, selectedStoreCode, selectedSupplierCode, snapshotQueryParams],
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
  const supplierPageCount = Math.max(1, Math.ceil(sortedSupplierRows.length / SUPPLIER_PAGE_SIZE));
  const supplierPageRows = getPageRows(sortedSupplierRows, supplierPage, SUPPLIER_PAGE_SIZE);
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
    const page = findRowPage(sortedSupplierRows, SUPPLIER_PAGE_SIZE, (row) => isSameLinkedCode(row.supplierCode, selectedSupplierCode));
    if (page !== null) setSupplierPage(page);
  }, [selectedSupplierCode, sortedSupplierRows, supplierRows]);
  // 明细的行号与「本页合计」说明按表里实际显示的那一页计算，过渡期间不把旧行标成新页码。
  const displayedProductPage = displayedContext?.productPage ?? productPage;
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
  const supplierNameWidth = CHINA_SUPPLIER_NAME_COLUMN_WIDTH;
  const productInfoWidth = getChinaLeadingColumnWidth(width, CHINA_PRODUCT_FIRST_SCREEN_FIXED, 96, 220);
  // 宽表可横向滚动，空态若按整表宽度居中会落到屏幕外；限制在首屏可见宽度内居中。
  const tableViewportWidth = Math.max(0, width - CHINA_TABLE_VIEWPORT_INSET);
  const productPageCount = Math.max(1, Math.ceil(productTotal / PRODUCT_PAGE_SIZE));
  // 商品报告的两个数据区块各自接近一屏，分页和搜索栏也计入区块高度。
  const sectionScreenHeight = Math.max(560, Math.floor(height * 0.76));
  const supplierTableBodyHeight = Math.max(420, sectionScreenHeight - 112);
  const productTableBodyHeight = Math.max(380, sectionScreenHeight - 184);
  const growthNewLabel = t("productReport.metrics.newGrowth");
  const costPendingLabel = t("productReport.states.costPending");
  const costNoActivityLabel = t("productReport.states.costNoActivity");

  const renderGrowthCell = (current: number, compare: number, columnStyle?: StyleProp<ViewStyle>) => {
    const tone = getGrowthTone(current, compare);
    return (
      <View style={[styles.growthColumn, columnStyle]}>
        <TableCell numeric style={[styles.strongText, { color: GROWTH_COLORS[tone] }]}>
          {formatGrowthRate(current, compare, growthNewLabel)}
        </TableCell>
      </View>
    );
  };

  const supplierBranchQueryKey = useMemo(
    () => [
      "product-report",
      "supplier-branches",
      accountIdentity,
      cashierStoreScopeVersion,
      drilldown,
      supplierBranchQueryParams,
    ] as const,
    [accountIdentity, cashierStoreScopeVersion, drilldown, supplierBranchQueryParams],
  );
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
  const supplierBranchSnapshotKey = useMemo(
    () => drilldown?.type === "supplier" && drilldownSnapshotQueryParams
      ? createReportSnapshotKey({
          accountIdentity,
          tab: "product",
          detail: "supplier-branches",
          period: range.key,
          startDate: drilldownSnapshotQueryParams.startDate,
          endDate: drilldownSnapshotQueryParams.endDate,
          compareStartDate: drilldownSnapshotQueryParams.compareStartDate,
          compareEndDate: drilldownSnapshotQueryParams.compareEndDate,
          compareMode: drilldownSnapshotQueryParams.compareMode,
          branchCodes: drilldownSnapshotQueryParams.branchCodes ?? [],
          supplierKind: drilldown.kind,
          supplierCode: drilldown.supplier.supplierCode,
        })
      : null,
    [accountIdentity, drilldown, range.key, drilldownSnapshotQueryParams],
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
    nextKind: "supplier" | "product",
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

  const supplierBranchQuery = useQuery({
    queryKey: supplierBranchQueryKey,
    queryFn: async ({ signal }) => {
      const cachedSupplierBranches = queryClient.getQueryData<
        ProductReportSnapshot<SupplierBranchBreakdownRow[]>
      >(supplierBranchQueryKey);
      const requestGeneration = startDrilldownLoad(
        supplierBranchQueryKey,
        "supplier",
        hasUsableSuccessfulReportCache(
          queryClient.getQueryState(supplierBranchQueryKey)?.status,
          cachedSupplierBranches,
          (cachedSupplierBranches) => cachedSupplierBranches.isComplete && cachedSupplierBranches.data.length > 0,
        ),
      );
      try {
        const result = await fetchSupplierBranchBreakdown(
          (drilldown as Extract<Drilldown, { type: "supplier" }>).kind,
          supplierBranchQueryParams!,
          (drilldown as Extract<Drilldown, { type: "supplier" }>).supplier.supplierCode,
          { signal },
        );
        if (
          signal.aborted
          || drilldownRequestGenerationRef.current !== requestGeneration
          || drilldownLoadQueryKeyRef.current !== supplierBranchQueryKey
        ) {
          const abortError = new Error("Stale supplier branch report request");
          abortError.name = "AbortError";
          throw abortError;
        }
        return result;
      } catch (error) {
        failDrilldownLoad(requestGeneration, supplierBranchQueryKey);
        throw error;
      }
    },
    enabled: Boolean(supplierBranchQueryParams && drilldown?.type === "supplier"),
    ...REPORT_QUERY_OPTIONS,
  });

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
  const activeDrilldownQuery = drilldownKind === "supplier"
    ? supplierBranchQuery
    : drilldownKind === "product"
      ? productBranchQuery
      : null;
  const activeDrilldownQueryKey = drilldownKind === "supplier"
    ? supplierBranchQueryKey
    : drilldownKind === "product"
      ? productBranchQueryKey
      : null;
  const activeDrilldownSnapshotKey = drilldownKind === "supplier"
    ? supplierBranchSnapshotKey
    : drilldownKind === "product"
      ? productBranchSnapshotKey
      : null;
  const activeDrilldownSnapshot = activeDrilldownSnapshotKey
    ? getCompleteReportSnapshot(completeDrilldownSnapshotsRef, activeDrilldownSnapshotKey)
    : undefined;
  const displayedSupplierBranchRows = reportScopeValid && drilldownKind === "supplier"
    ? (supplierBranchQuery.data?.isComplete && !supplierBranchQuery.isFetching && !supplierBranchQuery.isError
      ? supplierBranchQuery.data.data
      : (activeDrilldownSnapshot?.data as SupplierBranchBreakdownRow[] | undefined) ?? [])
    : [];
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
    activeDrilldownQuery?.isLoading && displayedSupplierBranchRows.length === 0 && displayedProductBranchRows.length === 0
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
  const isDrilldownError =
    drilldownKind === "supplier"
      ? supplierBranchQuery.isError
      : drilldownKind === "product"
        ? productBranchQuery.isError
        : false;
  const drilldownShowingSnapshot = activeDrilldownSnapshot !== undefined
    && (Boolean(activeDrilldownQuery?.isFetching) || Boolean(isDrilldownError) || Boolean(isDrilldownStatisticsIncomplete));
  const recordDrilldownMeasurement = useCallback((measurement: ReportLoadPerformanceMeasurement | null) => {
    if (!measurement || !drilldownLoadKindRef.current) return;
    recordReportLoadPerformance(
      drilldownLoadKindRef.current === "supplier" ? "supplier-branches" : "product-branches",
      measurement,
    );
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
      if (displayedSupplierBranchRows.length > 0 || displayedProductBranchRows.length > 0) return;
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
    displayedSupplierBranchRows.length,
    recordDrilldownMeasurement,
  ]);
  const retryDrilldown = () => {
    if (
      (drilldownKind === "supplier" && supplierBranchQueryParams)
      || (drilldownKind === "product" && productBranchQueryParams)
    ) {
      // 先重验白名单，避免用上一次成功范围重试刚被停用的分店。
      void storeOptionsQuery.refetch();
    }
  };
  const drilldownEmptyLabel =
    drilldownKind === "supplier"
      ? t("productReport.states.emptySupplierBranches")
      : t("productReport.states.emptyProducts");

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

  // 澳洲与中国供应商共用列顺序；澳洲只显示本期，中国把同期列组放在末尾。
  const renderSupplierRow = ({
    item,
    rowNumber,
    scrollX,
  }: {
    item: SupplierReportRow;
    rowNumber: number;
    scrollX: Animated.Value;
  }) => {
    const isSelected = isSameLinkedCode(item.supplierCode, selectedSupplierCode);
    const showComparison = isChinaKind;
    // 占比分母跟随表里数据所属的分店：中国页签用该范围的中国货合计，澳洲页签用本表小计。
    const categoryRevenue = isChinaKind ? supplierScopeChinaGoods.revenue : supplierSubtotal;
    const categoryCompareRevenue = isChinaKind
      ? supplierScopeChinaGoods.compareRevenue
      : supplierCompareSubtotal;
    const shareBarPercent = topSupplierRevenue > 0
      ? Math.min(100, Math.max(0, (item.revenue / topSupplierRevenue) * 100))
      : 0;

    return (
      <View style={[styles.tableRow, styles.supplierTableRow, styles.chinaSupplierRow, isSelected ? styles.selectedRow : null]}>
        <FrozenLeadingColumns
          scrollX={scrollX}
          style={styles.frozenSupplierColumns}
          tone={isSelected ? "selected" : "body"}
        >
          {isSelected ? <View style={styles.selectedSupplierAccent} /> : null}
          <View style={styles.rowNumberColumn}>
            <TableCell numeric style={styles.strongText}>{formatRowNumber(rowNumber)}</TableCell>
          </View>
          <Pressable
            // 供应商列点选联动分店表与商品明细（再点取消），金额列单独查看分店汇总。
            onPress={() => toggleLinkedSupplier(item.supplierCode)}
            accessibilityRole="button"
            accessibilityLabel={isSelected
              ? t("productReport.linked.selectedSupplierA11y", { supplier: getSupplierTitle(item) })
              : t("productReport.linked.selectSupplierA11y", { supplier: getSupplierTitle(item) })}
            accessibilityState={{ selected: isSelected }}
            style={[{ width: supplierNameWidth }, styles.fullHeightCell]}
          >
            <TableCell style={isSelected ? [styles.strongText, styles.selectedSupplierName] : styles.strongText}>
              {getSupplierTitle(item)}
            </TableCell>
            <TableCell style={styles.muted}>{shouldShowSupplierCode(item) ? item.supplierCode : ""}</TableCell>
          </Pressable>
        </FrozenLeadingColumns>
        <Pressable
          onPress={() => openDrilldown({ type: "supplier", kind, supplier: item })}
          accessibilityRole="button"
          accessibilityLabel={[getSupplierTitle(item), t("productReport.drilldown.supplier")].join(" ")}
          style={[styles.chinaSupplierAmountColumn, styles.fullHeightCell]}
        >
          <TableCell numeric style={styles.strongText}>{formatWholeMoney(item.revenue)}</TableCell>
          <View style={styles.chinaChevronWrap} pointerEvents="none">
            <Text style={styles.chinaInlineChevron} accessibilityElementsHidden>›</Text>
          </View>
        </Pressable>
        <View style={styles.chinaQuantityColumn}>
          <TableCell numeric style={styles.strongText}>{formatCount(item.totalQuantity)}</TableCell>
        </View>
        <View style={styles.chinaShareColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatShare(item.revenue, categoryRevenue)}</TableCell>
          <View style={styles.chinaShareTrack}>
            <View style={[styles.chinaShareFill, { width: `${shareBarPercent}%` as `${number}%` }]} />
          </View>
        </View>
        <View style={styles.chinaShareColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatShare(item.revenue, supplierScopeRevenue.revenue)}</TableCell>
        </View>
        <View style={styles.chinaAverageColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatNullableMoney(item.averagePrice)}</TableCell>
        </View>
        <View style={styles.grossProfitColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatNullableWholeMoney(item.grossProfit)}</TableCell>
        </View>
        <View style={styles.grossMarginColumn}>
          <TableCell numeric style={styles.chinaMetricText}>
            {formatGrossMarginRate(item.grossMarginRate, item.costStatus, costPendingLabel, costNoActivityLabel)}
          </TableCell>
        </View>
        {renderGrowthCell(item.revenue, item.compareRevenue, styles.chinaGrowthColumn)}
        {showComparison ? (
          <>
            <View style={styles.chinaCompareAmountColumn}>
              <TableCell numeric style={styles.muted}>{formatWholeMoney(item.compareRevenue)}</TableCell>
            </View>
            <View style={styles.chinaCompareQuantityColumn}>
              <TableCell numeric style={styles.muted}>
                {item.compareTotalQuantity === null ? "—" : formatCount(item.compareTotalQuantity)}
              </TableCell>
            </View>
            <View style={styles.chinaShareColumn}>
              <TableCell numeric style={styles.muted}>{formatShare(item.compareRevenue, categoryCompareRevenue)}</TableCell>
            </View>
            <View style={styles.chinaShareColumn}>
              <TableCell numeric style={styles.muted}>{formatShare(item.compareRevenue, supplierScopeRevenue.compareRevenue)}</TableCell>
            </View>
            <View style={styles.chinaAverageColumn}>
              <TableCell numeric style={styles.muted}>{formatNullableMoney(item.compareAveragePrice)}</TableCell>
            </View>
            <View style={styles.grossProfitColumn}>
              <TableCell numeric style={styles.muted}>{formatNullableWholeMoney(item.compareGrossProfit)}</TableCell>
            </View>
            <View style={styles.grossMarginColumn}>
              <TableCell numeric style={styles.muted}>
                {formatGrossMarginRate(item.compareGrossMarginRate, item.compareCostStatus, costPendingLabel, costNoActivityLabel)}
              </TableCell>
            </View>
          </>
        ) : null}
      </View>
    );
  };

  // 两个供应商页签的商品明细共用中国页签列布局，并且都只显示本期值。
  const renderProductRow = ({
    item,
    rowNumber,
    scrollX,
  }: {
    item: ProductReportProductRow;
    rowNumber: number;
    scrollX: Animated.Value;
  }) => (
    <Pressable
      style={[styles.tableRow, styles.productTableRow, styles.chinaProductRow]}
      onPress={() => openDrilldown({ type: "product", product: item })}
      accessibilityRole="button"
      accessibilityLabel={[item.itemNumber || item.productCode, item.productName || "", t("productReport.drilldown.product")].filter(Boolean).join(" ")}
    >
      <FrozenLeadingColumns scrollX={scrollX} style={styles.frozenProductColumns}>
        <View style={styles.productImageColumn}>
          <View style={styles.chinaImageFrame}>
            {item.productImage ? (
              <Image source={{ uri: item.productImage }} style={styles.productImage} resizeMode="cover" />
            ) : (
              <View style={styles.productImagePlaceholder}>
                <Text variant="labelSmall" style={styles.placeholderText}>
                  {t("productReport.columns.image")}
                </Text>
              </View>
            )}
            <View style={styles.rankBadge} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
              <Text style={styles.rankBadgeText}>{String(rowNumber)}</Text>
            </View>
          </View>
        </View>
        <View style={{ width: productInfoWidth, minWidth: 0 }}>
          <TableCell style={styles.strongText}>{item.itemNumber || "--"}</TableCell>
          <TableCell style={styles.muted}>{item.productName || "--"}</TableCell>
        </View>
      </FrozenLeadingColumns>
      <View style={styles.chinaQuantityColumn}>
        <TableCell numeric style={styles.strongText}>{formatCount(item.quantity)}</TableCell>
      </View>
      <View style={styles.chinaProductAmountColumn}>
        <TableCell numeric style={styles.strongText}>{formatWholeMoney(item.salesAmount)}</TableCell>
      </View>
      <View style={styles.chinaAverageColumn}>
        <TableCell numeric style={styles.chinaMetricText}>{formatMoney(item.averageUnitPrice)}</TableCell>
      </View>
      <View style={styles.grossProfitColumn}>
        <TableCell numeric style={styles.chinaMetricText}>{formatNullableWholeMoney(item.grossProfit)}</TableCell>
      </View>
      <View style={styles.grossMarginColumn}>
        <TableCell numeric style={styles.chinaMetricText}>
          {formatGrossMarginRate(item.grossMarginRate, item.costStatus, costPendingLabel, costNoActivityLabel)}
        </TableCell>
      </View>
      {renderGrowthCell(item.salesAmount, item.compareSalesAmount, styles.chinaGrowthColumn)}
    </Pressable>
  );

  // 表头下固定的「本页合计」：列与商品行对齐，不随商品列表纵向滚动。
  const renderChinaProductPageTotalRow = (scrollX: Animated.Value) => {
    const totals = chinaProductPageTotals;
    return (
      <View
        style={[
          styles.tableRow,
          styles.productTableRow,
          styles.chinaPageTotalRow,
          linkedStaleness.productTable ? styles.staleSection : null,
        ]}
      >
        <FrozenLeadingColumns scrollX={scrollX} style={[styles.frozenProductColumns, styles.chinaPageTotalFrozen]}>
          <View style={{ width: CHINA_PRODUCT_IMAGE_COLUMN_WIDTH + 3 + productInfoWidth, minWidth: 0 }}>
            <TableCell style={styles.strongText}>{t("productReport.chinaGoods.pageTotal")}</TableCell>
            <TableCell style={styles.muted}>
              {t("productReport.pageSummaryCaption", {
                start: productTotal === 0 ? 0 : (displayedProductPage - 1) * PRODUCT_PAGE_SIZE + 1,
                end: Math.min(displayedProductPage * PRODUCT_PAGE_SIZE, productTotal),
                total: productTotal,
              })}
            </TableCell>
          </View>
        </FrozenLeadingColumns>
        <View style={styles.chinaQuantityColumn}>
          <TableCell numeric style={styles.strongText}>{formatCount(totals.quantity)}</TableCell>
        </View>
        <View style={styles.chinaProductAmountColumn}>
          <TableCell numeric style={styles.strongText}>{formatWholeMoney(totals.salesAmount)}</TableCell>
        </View>
        <View style={styles.chinaAverageColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatNullableMoney(totals.averageUnitPrice)}</TableCell>
        </View>
        <View style={styles.grossProfitColumn}>
          <TableCell numeric style={styles.chinaMetricText}>{formatNullableWholeMoney(totals.grossProfit)}</TableCell>
        </View>
        <View style={styles.grossMarginColumn}>
          <TableCell numeric style={styles.chinaMetricText}>
            {formatGrossMarginRate(totals.grossMarginRate, totals.costStatus, costPendingLabel, costNoActivityLabel)}
          </TableCell>
        </View>
        {renderGrowthCell(totals.salesAmount, totals.compareSalesAmount, styles.chinaGrowthColumn)}
      </View>
    );
  };

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
        {!embedded ? (
          <View style={styles.header}>
            <Text variant="headlineSmall" style={styles.title}>
              {t("productReport.title")}
            </Text>
            <Text variant="bodySmall" style={styles.muted}>
              {dateRangeValid ? `${draftStartDate} - ${draftEndDate}` : t("productReport.states.invalidDate")}
            </Text>
          </View>
        ) : (
          <Text variant="bodySmall" style={styles.muted}>
            {dateRangeValid ? `${draftStartDate} - ${draftEndDate}` : t("productReport.states.invalidDate")}
          </Text>
        )}

        <SegmentedButtons
          value={kind}
          onValueChange={applyKind}
          buttons={[
            { value: "australia", label: t("productReport.tabs.australia") },
            { value: "china", label: t("productReport.tabs.china") },
          ]}
        />

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

        <View style={styles.quickBar}>
          {(["today", "yesterday", "thisWeek", "lastWeek", "thisMonth", "lastMonth"] as const).map((key) => (
            <Button key={key} compact mode={range.key === key ? "contained" : "outlined"} onPress={() => setQuickRange(key)}>
              {t(`productReport.shortcuts.${key}`)}
            </Button>
          ))}
        </View>

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
            label={t("reports.states.statisticsIncomplete")}
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
              // 中国页签顶部：中国货汇总与分店中国货占比；「本页合计」移到商品明细表头下方。
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
              <View style={[styles.productSummaryCard, styles.productSummaryLoading]}>
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
                    start: productTotal === 0 ? 0 : (displayedProductPage - 1) * PRODUCT_PAGE_SIZE + 1,
                    end: Math.min(displayedProductPage * PRODUCT_PAGE_SIZE, productTotal),
                    total: productTotal,
                  })}
                />
              </View>
            )}
            <View style={[styles.reportSection, { minHeight: sectionScreenHeight }]} onLayout={recordSectionOffset("suppliers")}>
              <SectionHeader
                title={t("productReport.sections.suppliers")}
                hint={isChinaKind ? t("productReport.chinaGoods.supplierHint") : undefined}
                showComparison={false}
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
                <FrozenHorizontalTable>
                  {(scrollX) => (
                    <View style={styles.table}>
                      <SupplierTableHeader
                        kind={kind}
                        scrollX={scrollX}
                        sort={supplierSort}
                        onSort={applySupplierSort}
                        nameWidth={supplierNameWidth}
                        showComparison={isChinaKind}
                      />
                      <ScrollView
                        bounces={false}
                        nestedScrollEnabled
                        showsVerticalScrollIndicator={false}
                        style={[
                          styles.tableBody,
                          { height: supplierTableBodyHeight },
                          linkedStaleness.supplierTable ? styles.staleSection : null,
                        ]}
                      >
                        {supplierPageRows.length === 0 ? (
                          <View style={{ width: tableViewportWidth }}>
                            <EmptyState label={t("productReport.states.emptySuppliers")} />
                          </View>
                        ) : (
                          supplierPageRows.map((item, index) => (
                            <View
                              key={item.id}
                              ref={index === 0 ? firstSupplierReportRowRef : undefined}
                              collapsable={index === 0 ? false : undefined}
                              onLayout={index === 0 ? scheduleProductDataVisibilityCheck : undefined}
                            >
                              {renderSupplierRow({
                                item,
                                rowNumber: (supplierPage - 1) * SUPPLIER_PAGE_SIZE + index + 1,
                                scrollX,
                              })}
                            </View>
                          ))
                        )}
                      </ScrollView>
                    </View>
                  )}
                </FrozenHorizontalTable>
              )}
            </View>

            <View style={[styles.reportSection, { minHeight: sectionScreenHeight }]} onLayout={recordSectionOffset("products")}>
              <SectionHeader
                title={t("productReport.sections.products")}
                hint={t("productReport.chinaGoods.productHint")}
                showComparison={false}
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
                page={productPage}
                pageCount={productPageCount}
                onPrevious={() => setProductPage((current) => Math.max(1, current - 1))}
                onNext={() => setProductPage((current) => Math.min(productPageCount, current + 1))}
                previousLabel={t("productReport.actions.previous")}
                nextLabel={t("productReport.actions.next")}
              />
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
                <View style={styles.productSearchActions}>
                  <Button compact mode="contained" onPress={applyProductSearch}>
                    {t("productReport.actions.searchProduct")}
                  </Button>
                  {productSearch ? (
                    <Button compact mode="outlined" onPress={clearProductSearch}>
                      {t("productReport.actions.clearProductSearch")}
                    </Button>
                  ) : null}
                </View>
              </View>
              {productSectionLoading ? (
                <LoadingState label={t("productReport.states.loading")} />
              ) : (
                <FrozenHorizontalTable>
                  {(scrollX) => (
                  <View style={styles.table}>
                    <ProductTableHeader
                      scrollX={scrollX}
                      sort={productSort}
                      onSort={applyProductSort}
                      infoWidth={productInfoWidth}
                    />
                    {isChinaKind && productRows.length > 0 ? renderChinaProductPageTotalRow(scrollX) : null}
                    <ScrollView
                      bounces={false}
                      nestedScrollEnabled
                      showsVerticalScrollIndicator={false}
                      style={[
                        styles.tableBody,
                        { height: productTableBodyHeight },
                        linkedStaleness.productTable ? styles.staleSection : null,
                      ]}
                    >
                      {productRows.length === 0 ? (
                        <View style={{ width: tableViewportWidth }}>
                          {displayedContext?.productSearch ? (
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
                          )}
                        </View>
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
                              rowNumber: (displayedProductPage - 1) * PRODUCT_PAGE_SIZE + index + 1,
                              scrollX,
                            })}
                          </View>
                        ))
                      )}
                    </ScrollView>
                  </View>
                  )}
                </FrozenHorizontalTable>
              )}
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
      <BranchDrilldownModal
        visible={
          Boolean(drilldown)
          && reportScopeValid
        }
        title={
          drilldown?.type === "supplier"
            ? t("productReport.drilldown.supplier")
            : t("productReport.drilldown.product")
        }
        supplierRows={displayedSupplierBranchRows}
        productRows={displayedProductBranchRows}
        showComparison={drilldown?.type === "supplier" && drilldown.kind === "china"}
        isLoading={isDrilldownLoading}
        isError={(isDrilldownError || isDrilldownStatisticsIncomplete)
          && displayedSupplierBranchRows.length === 0
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
          ? t("reports.states.statisticsIncomplete")
          : t("productReport.states.error")}
        emptyLabel={drilldownEmptyLabel}
        kind={drilldownKind}
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

function SectionHeader({
  title,
  hint,
  showComparison = true,
  badges,
  updating = false,
  page,
  pageCount,
  onPrevious,
  onNext,
  previousLabel,
  nextLabel,
}: {
  title: string;
  // 单行布局可用提示替代「本期 / 同期」图例；无提示时由 showComparison 控制图例。
  hint?: string;
  showComparison?: boolean;
  // 表里数据所属的联动范围（分店、供应商标签），描述的是当前显示的数据而不是刚点选的条件。
  badges?: ReactNode;
  // 正在按新的联动条件重取：旧数据变淡，标题旁显示「更新中」。
  updating?: boolean;
  page: number;
  pageCount: number;
  onPrevious: () => void;
  onNext: () => void;
  previousLabel: string;
  nextLabel: string;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={styles.sectionHeader}>
      <View style={styles.sectionTitleBlock}>
        <View style={styles.sectionTitleLine}>
          <Text variant="titleMedium" style={styles.sectionTitle}>
            {title}
          </Text>
          {badges}
          {updating ? (
            <View style={styles.updatingIndicator} accessibilityRole="progressbar">
              <ActivityIndicator size={12} />
              <Text variant="labelSmall" style={styles.updatingText}>{t("productReport.linked.updating")}</Text>
            </View>
          ) : null}
        </View>
        {hint ? (
          <Text variant="labelSmall" style={styles.muted} numberOfLines={2}>{hint}</Text>
        ) : showComparison ? (
          <View style={styles.valueLegend}>
            <Text variant="labelSmall" style={styles.strongText}>{t("reports.metrics.current")}</Text>
            <Text variant="labelSmall" style={styles.muted}>/ {t("productReport.metrics.compare")}</Text>
          </View>
        ) : null}
      </View>
      <View style={styles.pager}>
        <Button compact mode="outlined" disabled={page <= 1} onPress={onPrevious}>
          {previousLabel}
        </Button>
        <Text variant="bodySmall" style={styles.pageText}>
          {page}/{pageCount}
        </Text>
        <Button compact mode="outlined" disabled={page >= pageCount} onPress={onNext}>
          {nextLabel}
        </Button>
      </View>
    </View>
  );
}

/** 区块标题旁的联动范围标签：分店蓝、供应商紫，与吸顶联动条和选中行同色。 */
function ScopePill({ tone, label }: { tone: "branch" | "supplier"; label: string }) {
  const branch = tone === "branch";
  return (
    <View style={[styles.scopePill, branch ? styles.branchScopePill : styles.supplierScopePill]}>
      <Text
        variant="labelSmall"
        numberOfLines={1}
        style={[styles.scopePillText, { color: branch ? LINKED_COLORS.branchText : LINKED_COLORS.supplierText }]}
      >
        {label}
      </Text>
    </View>
  );
}

/** 双层表头的分组：上层是组名，下层子列与数据行使用相同列宽和间距。 */
function TableHeaderGroup({
  label,
  muted,
  columnGap = 3,
  children,
}: {
  label: string;
  muted?: boolean;
  columnGap?: number;
  children: ReactNode;
}) {
  return (
    <View style={styles.headerGroup}>
      <View style={styles.headerGroupTitle}>
        <Text variant="labelSmall" numberOfLines={1} style={[styles.headerText, muted ? styles.muted : null]}>
          {label}
        </Text>
      </View>
      <View style={[styles.headerGroupColumns, { gap: columnGap }]}>{children}</View>
    </View>
  );
}

function SupplierTableHeader({
  kind,
  scrollX,
  sort,
  onSort,
  nameWidth,
  showComparison,
}: {
  kind: SupplierReportKind;
  scrollX: Animated.Value;
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
  nameWidth: number;
  showComparison: boolean;
}) {
  const { t } = useAppTranslation("common");
  const categoryShareLabel = kind === "china"
    ? t("productReport.chinaGoods.shareOfChina")
    : t("productReport.metrics.supplierShare");
  const totalShareLabel = kind === "china"
    ? t("productReport.chinaGoods.shareOfTotalShort")
    : t("productReport.metrics.totalShare");
  const compareCell = (style: StyleProp<ViewStyle>, label: string) => (
    <View style={style}>
      <TableCell numeric style={styles.chinaCompareHeaderText}>{label}</TableCell>
    </View>
  );

  return (
    <View style={[styles.tableRow, styles.tableHeaderRow, styles.supplierTableRow, styles.chinaHeaderRow]}>
      <FrozenLeadingColumns scrollX={scrollX} style={styles.frozenSupplierColumns} tone="header">
        <View style={styles.rowNumberColumn}>
          <TableCell numeric style={styles.headerText}>{t("productReport.columns.rowNumber")}</TableCell>
        </View>
        <View style={{ width: nameWidth, minWidth: 0 }}>
          <TableCell style={styles.headerText}>{t("productReport.sections.suppliers")}</TableCell>
        </View>
      </FrozenLeadingColumns>
      <View style={styles.chinaSupplierAmountColumn}>
        <SortableHeaderCell
          label={t("productReport.columns.amount")}
          field="amount"
          sort={sort}
          onSort={onSort}
          style={styles.supplierMoneyHeaderCell}
        />
      </View>
      <View style={styles.chinaQuantityColumn}>
        <SortableHeaderCell label={t("productReport.columns.quantity")} field="quantity" sort={sort} onSort={onSort} />
      </View>
      <TableHeaderGroup label={t("productReport.chinaGoods.shareGroup")}>
        <View style={styles.chinaShareColumn}>
          <TableCell numeric style={styles.headerSubText}>{categoryShareLabel}</TableCell>
        </View>
        <View style={styles.chinaShareColumn}>
          <TableCell numeric style={styles.headerSubText}>{totalShareLabel}</TableCell>
        </View>
      </TableHeaderGroup>
      <View style={styles.chinaAverageColumn}>
        <SortableHeaderCell label={t("productReport.columns.averagePrice")} field="unitPrice" sort={sort} onSort={onSort} />
      </View>
      <View style={styles.grossProfitColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossProfit")}</TableCell>
      </View>
      <View style={styles.grossMarginColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossMarginRate")}</TableCell>
      </View>
      <View style={styles.chinaGrowthColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.growthRate")}</TableCell>
      </View>
      {showComparison ? (
        <TableHeaderGroup muted label={t("productReport.metrics.compare")}>
          {compareCell(styles.chinaCompareAmountColumn, t("productReport.columns.amount"))}
          {compareCell(styles.chinaCompareQuantityColumn, t("productReport.columns.quantity"))}
          {compareCell(styles.chinaShareColumn, t("productReport.chinaGoods.compareShareOfChina"))}
          {compareCell(styles.chinaShareColumn, t("productReport.chinaGoods.compareShareOfTotal"))}
          {compareCell(styles.chinaAverageColumn, t("productReport.columns.averagePrice"))}
          {compareCell(styles.grossProfitColumn, t("productReport.metrics.grossProfit"))}
          {compareCell(styles.grossMarginColumn, t("productReport.metrics.grossMarginRate"))}
        </TableHeaderGroup>
      ) : null}
    </View>
  );
}

function ProductTableHeader({
  scrollX,
  sort,
  onSort,
  infoWidth,
}: {
  scrollX: Animated.Value;
  sort: ReportSort;
  onSort: (field: ReportSortField) => void;
  infoWidth: number;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={[styles.tableRow, styles.tableHeaderRow, styles.productTableRow, styles.chinaHeaderRow]}>
      <FrozenLeadingColumns scrollX={scrollX} style={styles.frozenProductColumns} tone="header">
        <View style={styles.productImageColumn}>
          <TableCell style={styles.headerText}>{t("productReport.columns.image")}</TableCell>
        </View>
        <View style={{ width: infoWidth, minWidth: 0 }}>
          <TableCell style={styles.headerText}>{t("productReport.columns.product")}</TableCell>
        </View>
      </FrozenLeadingColumns>
      <View style={styles.chinaQuantityColumn}>
        <SortableHeaderCell label={t("productReport.columns.quantity")} field="quantity" sort={sort} onSort={onSort} />
      </View>
      <View style={styles.chinaProductAmountColumn}>
        <SortableHeaderCell label={t("productReport.columns.amount")} field="amount" sort={sort} onSort={onSort} />
      </View>
      <View style={styles.chinaAverageColumn}>
        <SortableHeaderCell label={t("productReport.columns.averagePrice")} field="unitPrice" sort={sort} onSort={onSort} />
      </View>
      <View style={styles.grossProfitColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossProfit")}</TableCell>
      </View>
      <View style={styles.grossMarginColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossMarginRate")}</TableCell>
      </View>
      <View style={styles.chinaGrowthColumn}>
        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.growthRate")}</TableCell>
      </View>
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

function BranchDrilldownModal({
  visible,
  title,
  supplierRows,
  productRows,
  isLoading,
  isError,
  onRetry,
  onDismiss,
  closeLabel,
  retryLabel,
  errorLabel,
  emptyLabel,
  kind,
  showComparison = false,
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
  supplierRows: SupplierBranchBreakdownRow[];
  productRows: ProductBranchBreakdownRow[];
  isLoading: boolean;
  isError: boolean;
  onRetry: () => void;
  onDismiss: () => void;
  closeLabel: string;
  retryLabel: string;
  errorLabel: string;
  emptyLabel: string;
  kind: "supplier" | "product" | null;
  // 两类供应商下钻共用单行列布局；仅中国供应商在末尾显示同期列组。
  showComparison?: boolean;
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
  const rows = kind === "supplier" ? supplierRows : productRows;
  // 分店弹窗是全量数据，直接在前端排序；序号即当前排序下的名次，同值按分店编码兜底。
  const sortedSupplierRows = useMemo(
    () => sortReportRows(supplierRows, sort, SUPPLIER_METRIC_SORT_VALUES, (row) => row.branchCode),
    [sort, supplierRows],
  );
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
  }, [isError, isLoading, kind, rows, scheduleFirstRowVisibilityCheck, visible]);
  const renderGrowthCell = (current: number, compare: number, columnStyle?: StyleProp<ViewStyle>) => {
    const tone = getGrowthTone(current, compare);
    return (
      <View style={[styles.growthColumn, columnStyle]}>
        <TableCell numeric style={[styles.strongText, { color: GROWTH_COLORS[tone] }]}>
          {formatGrowthRate(current, compare, growthNewLabel)}
        </TableCell>
      </View>
    );
  };
  const renderGrossProfitCell = (current: number | null, compare: number | null) => (
    <View style={styles.grossProfitColumn}>
      <TableCell numeric style={styles.strongText}>{formatNullableWholeMoney(current)}</TableCell>
      <TableCell numeric style={styles.muted}>{formatNullableWholeMoney(compare)}</TableCell>
    </View>
  );
  const renderGrossMarginCell = (
    current: number | null,
    compare: number | null,
    currentStatus: ProductReportCostStatus,
    compareStatus: ProductReportCostStatus,
  ) => (
    <View style={styles.grossMarginColumn}>
      <TableCell numeric style={styles.strongText}>{formatGrossMarginRate(current, currentStatus, costPendingLabel, costNoActivityLabel)}</TableCell>
      <TableCell numeric style={styles.muted}>{formatGrossMarginRate(compare, compareStatus, costPendingLabel, costNoActivityLabel)}</TableCell>
    </View>
  );
  return (
    <Portal>
      <Modal visible={visible} onDismiss={onDismiss} contentContainerStyle={[styles.modal, styles.drilldownModal]}>
        <Text variant="titleMedium" style={styles.modalTitle}>
          {title}
        </Text>
        {snapshotNotice ? <Text variant="labelSmall" style={styles.snapshotNotice}>{snapshotNotice}</Text> : null}
        {isLoading ? (
          <LoadingState label={t("productReport.states.loading")} />
        ) : isError ? (
          <ErrorState label={errorLabel} retryLabel={retryLabel} onRetry={onRetry} />
        ) : rows.length === 0 ? (
          <EmptyState label={emptyLabel} />
        ) : (
          <ScrollView
            bounces={false}
            nestedScrollEnabled
            onScroll={scheduleFirstRowVisibilityCheck}
            scrollEventThrottle={16}
            style={[styles.modalList, styles.drilldownModalList]}
          >
            <FrozenHorizontalTable>
              {(scrollX) => (
              <View style={[styles.table, kind === "product" ? styles.productDrilldownTable : null]}>
                <View style={[styles.tableRow, styles.tableHeaderRow, kind === "product" ? styles.productBranchTableRow : styles.chinaHeaderRow]}>
                  <FrozenLeadingColumns
                    scrollX={scrollX}
                    style={kind === "product" ? styles.frozenProductBranchColumns : styles.frozenBranchColumns}
                    tone="header"
                  >
                    <View style={styles.rowNumberColumn}>
                      <TableCell numeric style={styles.headerText}>{t("productReport.columns.rowNumber")}</TableCell>
                    </View>
                    <View style={kind === "product" ? styles.productBranchNameColumn : styles.branchColumn}>
                      <TableCell style={styles.headerText}>{t("productReport.filters.store")}</TableCell>
                    </View>
                  </FrozenLeadingColumns>
                  {kind === "product" ? (
                    <>
                      <View style={styles.productBranchCountColumn}>
                        <SortableHeaderCell label={t("productReport.columns.quantity")} field="quantity" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.productBranchMoneyColumn}>
                        <SortableHeaderCell label={t("productReport.columns.amount")} field="amount" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.productBranchAverageColumn}>
                        <SortableHeaderCell label={t("productReport.columns.averagePrice")} field="unitPrice" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.productBranchGrowthColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.growthRate")}</TableCell>
                      </View>
                      <View style={styles.grossProfitColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossProfit")}</TableCell>
                      </View>
                      <View style={styles.grossMarginColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossMarginRate")}</TableCell>
                      </View>
                    </>
                  ) : (
                    <>
                      <View style={styles.chinaSupplierAmountColumn}>
                        <SortableHeaderCell label={t("productReport.columns.amount")} field="amount" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.chinaQuantityColumn}>
                        <SortableHeaderCell label={t("productReport.columns.quantity")} field="quantity" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.chinaAverageColumn}>
                        <SortableHeaderCell label={t("productReport.columns.averagePrice")} field="unitPrice" sort={sort} onSort={onSortChange} />
                      </View>
                      <View style={styles.grossProfitColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossProfit")}</TableCell>
                      </View>
                      <View style={styles.grossMarginColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.grossMarginRate")}</TableCell>
                      </View>
                      <View style={styles.chinaGrowthColumn}>
                        <TableCell numeric style={styles.headerText}>{t("productReport.metrics.growthRate")}</TableCell>
                      </View>
                      {showComparison ? (
                        <TableHeaderGroup muted columnGap={8} label={t("productReport.metrics.compare")}>
                          <View style={styles.chinaCompareAmountColumn}>
                            <TableCell numeric style={styles.chinaCompareHeaderText}>{t("productReport.columns.amount")}</TableCell>
                          </View>
                          <View style={styles.chinaCompareQuantityColumn}>
                            <TableCell numeric style={styles.chinaCompareHeaderText}>{t("productReport.columns.quantity")}</TableCell>
                          </View>
                          <View style={styles.chinaAverageColumn}>
                            <TableCell numeric style={styles.chinaCompareHeaderText}>{t("productReport.columns.averagePrice")}</TableCell>
                          </View>
                          <View style={styles.grossProfitColumn}>
                            <TableCell numeric style={styles.chinaCompareHeaderText}>{t("productReport.metrics.grossProfit")}</TableCell>
                          </View>
                          <View style={styles.grossMarginColumn}>
                            <TableCell numeric style={styles.chinaCompareHeaderText}>{t("productReport.metrics.grossMarginRate")}</TableCell>
                          </View>
                        </TableHeaderGroup>
                      ) : null}
                    </>
                  )}
                </View>
                {kind === "supplier"
                  ? sortedSupplierRows.map((row, index) => (
                      <SupplierBranchRow
                        key={row.id}
                        row={row}
                        rowNumber={index + 1}
                        renderGrowthCell={renderGrowthCell}
                        costPendingLabel={costPendingLabel}
                        costNoActivityLabel={costNoActivityLabel}
                        showComparison={showComparison}
                        rowRef={index === 0 ? firstRowRef : undefined}
                        scrollX={scrollX}
                      />
                    ))
                  : sortedProductRows.map((row, index) => (
                      <ProductBranchRow
                        key={row.id}
                        row={row}
                        rowNumber={index + 1}
                        renderGrowthCell={renderGrowthCell}
                        renderGrossProfitCell={renderGrossProfitCell}
                        renderGrossMarginCell={renderGrossMarginCell}
                        rowRef={index === 0 ? firstRowRef : undefined}
                        scrollX={scrollX}
                      />
                    ))}
              </View>
              )}
            </FrozenHorizontalTable>
          </ScrollView>
        )}
        <Button mode="contained" onPress={onDismiss}>
          {closeLabel}
        </Button>
      </Modal>
    </Portal>
  );
}

function SupplierBranchRow({
  row,
  rowNumber,
  renderGrowthCell,
  costPendingLabel,
  costNoActivityLabel,
  showComparison,
  rowRef,
  scrollX,
}: {
  row: SupplierBranchBreakdownRow;
  rowNumber: number;
  renderGrowthCell: (current: number, compare: number, columnStyle?: StyleProp<ViewStyle>) => ReactNode;
  costPendingLabel: string;
  costNoActivityLabel: string;
  showComparison: boolean;
  rowRef?: RefObject<View | null>;
  scrollX: Animated.Value;
}) {
  return (
    <View ref={rowRef} style={[styles.tableRow, styles.chinaSupplierRow]}>
      <FrozenLeadingColumns scrollX={scrollX} style={styles.frozenBranchColumns}>
        <View style={styles.rowNumberColumn}>
          <TableCell numeric style={styles.strongText}>{formatRowNumber(rowNumber)}</TableCell>
        </View>
        <View style={styles.branchColumn}>
          <TableCell style={styles.strongText}>{row.branchName || row.branchCode}</TableCell>
          <TableCell style={styles.muted}>{row.branchCode}</TableCell>
        </View>
      </FrozenLeadingColumns>
      <View style={styles.chinaSupplierAmountColumn}>
        <TableCell numeric style={styles.strongText}>{formatWholeMoney(row.revenue)}</TableCell>
      </View>
      <View style={styles.chinaQuantityColumn}>
        <TableCell numeric style={styles.strongText}>{formatCount(row.totalQuantity)}</TableCell>
      </View>
      <View style={styles.chinaAverageColumn}>
        <TableCell numeric style={styles.chinaMetricText}>{formatNullableMoney(row.averagePrice)}</TableCell>
      </View>
      <View style={styles.grossProfitColumn}>
        <TableCell numeric style={styles.chinaMetricText}>{formatNullableWholeMoney(row.grossProfit)}</TableCell>
      </View>
      <View style={styles.grossMarginColumn}>
        <TableCell numeric style={styles.chinaMetricText}>
          {formatGrossMarginRate(row.grossMarginRate, row.costStatus, costPendingLabel, costNoActivityLabel)}
        </TableCell>
      </View>
      {renderGrowthCell(row.revenue, row.compareRevenue, styles.chinaGrowthColumn)}
      {showComparison ? (
        <>
          <View style={styles.chinaCompareAmountColumn}>
            <TableCell numeric style={styles.muted}>{formatWholeMoney(row.compareRevenue)}</TableCell>
          </View>
          <View style={styles.chinaCompareQuantityColumn}>
            <TableCell numeric style={styles.muted}>
              {row.compareTotalQuantity === null ? "—" : formatCount(row.compareTotalQuantity)}
            </TableCell>
          </View>
          <View style={styles.chinaAverageColumn}>
            <TableCell numeric style={styles.muted}>{formatNullableMoney(row.compareAveragePrice)}</TableCell>
          </View>
          <View style={styles.grossProfitColumn}>
            <TableCell numeric style={styles.muted}>{formatNullableWholeMoney(row.compareGrossProfit)}</TableCell>
          </View>
          <View style={styles.grossMarginColumn}>
            <TableCell numeric style={styles.muted}>
              {formatGrossMarginRate(row.compareGrossMarginRate, row.compareCostStatus, costPendingLabel, costNoActivityLabel)}
            </TableCell>
          </View>
        </>
      ) : null}
    </View>
  );
}

function ProductBranchRow({
  row,
  rowNumber,
  renderGrowthCell,
  renderGrossProfitCell,
  renderGrossMarginCell,
  rowRef,
  scrollX,
}: {
  row: ProductBranchBreakdownRow;
  rowNumber: number;
  renderGrowthCell: (current: number, compare: number, columnStyle?: StyleProp<ViewStyle>) => ReactNode;
  renderGrossProfitCell: (current: number | null, compare: number | null) => ReactNode;
  renderGrossMarginCell: (
    current: number | null,
    compare: number | null,
    currentStatus: ProductReportCostStatus,
    compareStatus: ProductReportCostStatus,
  ) => ReactNode;
  rowRef?: RefObject<View | null>;
  scrollX: Animated.Value;
}) {
  return (
    <View ref={rowRef} style={[styles.tableRow, styles.productBranchTableRow]}>
      <FrozenLeadingColumns scrollX={scrollX} style={styles.frozenProductBranchColumns}>
        <View style={styles.rowNumberColumn}>
          <TableCell numeric style={styles.strongText}>{formatRowNumber(rowNumber)}</TableCell>
        </View>
        <View style={styles.productBranchNameColumn}>
          <TableCell style={styles.strongText}>{row.branchName || row.branchCode}</TableCell>
          <TableCell style={styles.muted}>{row.branchCode}</TableCell>
        </View>
      </FrozenLeadingColumns>
      <View style={styles.productBranchCountColumn}>
        <TableCell numeric style={styles.strongText}>{formatCount(row.quantity)}</TableCell>
        <TableCell numeric style={styles.muted}>{formatCount(row.compareQuantity)}</TableCell>
      </View>
      <View style={styles.productBranchMoneyColumn}>
        <TableCell numeric style={styles.strongText}>{formatWholeMoney(row.salesAmount)}</TableCell>
        <TableCell numeric style={styles.muted}>{formatWholeMoney(row.compareSalesAmount)}</TableCell>
      </View>
      <View style={styles.productBranchAverageColumn}>
        <TableCell numeric style={styles.strongText}>{formatMoney(row.averageUnitPrice)}</TableCell>
        <TableCell numeric style={styles.muted}>{formatMoney(row.compareAverageUnitPrice)}</TableCell>
      </View>
      {renderGrowthCell(row.salesAmount, row.compareSalesAmount, styles.productBranchGrowthColumn)}
      {renderGrossProfitCell(row.grossProfit, row.compareGrossProfit)}
      {renderGrossMarginCell(row.grossMarginRate, row.compareGrossMarginRate, row.costStatus, row.compareCostStatus)}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: "#F7F8FA",
  },
  content: {
    gap: 12,
    padding: 16,
    paddingBottom: 40,
  },
  header: {
    gap: 4,
  },
  title: {
    color: "#111827",
    fontWeight: "700",
  },
  muted: {
    color: "#6B7280",
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
    borderRadius: 8,
    backgroundColor: LINKED_COLORS.supplierBackground,
    paddingLeft: 10,
    paddingRight: 4,
    paddingVertical: 4,
  },
  supplierMissingDot: {
    width: 6,
    height: 6,
    borderRadius: 2,
    backgroundColor: LINKED_COLORS.supplier,
  },
  supplierMissingText: {
    flex: 1,
    minWidth: 0,
    color: "#4C1D95",
  },
  dateInputs: {
    flexDirection: "row",
    gap: 8,
  },
  dateInput: {
    flex: 1,
    minWidth: 132,
    backgroundColor: "#FFFFFF",
  },
  quickBar: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  productSearchBar: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 8,
  },
  productSearchInput: {
    flex: 1,
    minWidth: 180,
    backgroundColor: "#FFFFFF",
  },
  productSearchActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  reportSection: {
    gap: 8,
  },
  productSummaryCard: {
    overflow: "hidden",
    borderWidth: 1,
    borderColor: "#E2E8F0",
    borderRadius: 10,
    paddingHorizontal: 10,
    paddingTop: 8,
    paddingBottom: 6,
    backgroundColor: "#FFFFFF",
  },
  productSummaryLoading: {
    minHeight: 88,
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  productSummaryHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    borderBottomWidth: 1,
    borderBottomColor: "#E2E8F0",
    paddingBottom: 6,
  },
  productSummaryScroll: {
    flexGrow: 1,
  },
  productSummaryGrid: {
    flex: 1,
    flexDirection: "row",
    alignItems: "flex-start",
    paddingTop: 4,
  },
  productSummaryLabelColumn: {
    width: 42,
    flexShrink: 0,
    gap: 2,
  },
  productSummaryMetric: {
    flex: 1,
    minWidth: 112,
    flexShrink: 0,
    gap: 2,
    paddingHorizontal: 3,
  },
  sectionHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    marginTop: 4,
  },
  sectionTitle: {
    color: "#111827",
    fontWeight: "700",
  },
  sectionTitleBlock: {
    flex: 1,
    minWidth: 0,
  },
  sectionTitleLine: {
    flexDirection: "row",
    alignItems: "center",
    flexWrap: "wrap",
    gap: 6,
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
    height: 22,
    justifyContent: "center",
    borderWidth: 1,
    borderRadius: 6,
    paddingHorizontal: 8,
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
    fontWeight: "600",
  },
  valueLegend: {
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
  },
  pager: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  pageText: {
    minWidth: 44,
    textAlign: "center",
    color: "#4B5563",
  },
  listContent: {
    gap: 8,
  },
  table: {
    overflow: "hidden",
    borderWidth: 1,
    borderColor: "#E5E7EB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  supplierTable: {
    minWidth: 832,
  },
  productTable: {
    minWidth: 800,
  },
  drilldownTable: {
    minWidth: 868,
  },
  productDrilldownTable: {
    minWidth: 728,
  },
  tableBody: {
    flexGrow: 0,
  },
  tableRow: {
    minHeight: 54,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderBottomWidth: 1,
    borderBottomColor: "#E5E7EB",
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  tableHeaderRow: {
    minHeight: 38,
    backgroundColor: "#F3F4F6",
  },
  frozenLeadingColumns: {
    position: "relative",
    zIndex: 3,
    elevation: 1,
    alignSelf: "stretch",
    flexDirection: "row",
    alignItems: "center",
    marginVertical: -8,
    paddingVertical: 8,
    borderRightWidth: 1,
    borderRightColor: "#CBD5E1",
  },
  frozenBodyColumns: {
    backgroundColor: "#FFFFFF",
  },
  frozenHeaderColumns: {
    backgroundColor: "#F3F4F6",
  },
  // 供应商选中色用紫色（分店蓝、供应商紫），与吸顶联动条的供应商 chip 同色。
  frozenSelectedColumns: {
    backgroundColor: LINKED_COLORS.supplierBackground,
  },
  selectedSupplierAccent: {
    position: "absolute",
    left: 0,
    top: 0,
    bottom: 0,
    width: 3,
    backgroundColor: LINKED_COLORS.supplier,
  },
  selectedSupplierName: {
    color: LINKED_COLORS.supplierText,
  },
  frozenSupplierColumns: {
    gap: 3,
    marginLeft: -4,
    paddingLeft: 4,
  },
  frozenProductColumns: {
    gap: 3,
    marginLeft: -4,
    paddingLeft: 4,
  },
  frozenBranchColumns: {
    gap: 8,
    marginLeft: -10,
    paddingLeft: 10,
  },
  frozenProductBranchColumns: {
    gap: 4,
    marginLeft: -6,
    paddingLeft: 6,
  },
  productTableRow: {
    gap: 3,
    paddingHorizontal: 4,
  },
  supplierTableRow: {
    gap: 3,
    paddingHorizontal: 4,
  },
  supplierNameColumn: {
    width: 80,
    minWidth: 0,
  },
  supplierMoneyColumn: {
    // 预留整数大额与下钻箭头空间，横向滚动时保持表头、双期金额对齐。
    width: 128,
    flexShrink: 0,
    paddingRight: 10,
    position: "relative",
  },
  fullHeightCell: {
    alignSelf: "stretch",
    justifyContent: "center",
  },
  supplierFilterMeta: {
    flexDirection: "row",
    alignItems: "center",
    gap: 3,
  },
  supplierCodeText: {
    flex: 1,
    minWidth: 0,
  },
  filterProductsHint: {
    color: "#2563EB",
    fontWeight: "700",
  },
  inlineChevron: {
    position: "absolute",
    top: 8,
    right: 1,
    color: "#64748B",
    fontSize: 20,
    lineHeight: 24,
  },
  supplierGrowthColumn: {
    width: 66,
    minWidth: 0,
  },
  supplierShareColumn: {
    width: 66,
    minWidth: 0,
  },
  supplierCountColumn: {
    // 为排序表头和完整商品数量预留空间，横向滚动时列宽保持稳定。
    width: 80,
    flexShrink: 0,
  },
  productNameColumn: {
    width: 190,
    minWidth: 0,
  },
  itemColumn: {
    width: 100,
    minWidth: 0,
  },
  imageColumn: {
    width: 58,
    alignItems: "center",
  },
  productImageColumn: {
    width: 52,
    alignItems: "center",
  },
  productInfoColumn: {
    // 商品明细横向滚动时固定名称列，避免宽屏下把数量列推得太远。
    width: 80,
    minWidth: 0,
  },
  productCountColumn: {
    width: 80,
    flexShrink: 0,
  },
  productMoneyColumn: {
    width: 124,
    flexShrink: 0,
  },
  productAverageColumn: {
    width: 96,
    flexShrink: 0,
  },
  productGrowthColumn: {
    width: 64,
    minWidth: 0,
  },
  branchColumn: {
    width: 130,
    minWidth: 0,
  },
  productBranchTableRow: {
    gap: 4,
    paddingHorizontal: 6,
  },
  productBranchNameColumn: {
    width: 68,
    minWidth: 0,
  },
  productBranchCountColumn: {
    width: 80,
    flexShrink: 0,
  },
  productBranchMoneyColumn: {
    width: 124,
    flexShrink: 0,
  },
  productBranchAverageColumn: {
    width: 96,
    flexShrink: 0,
  },
  productBranchGrowthColumn: {
    width: 64,
    minWidth: 0,
  },
  moneyColumn: {
    width: 124,
    flexShrink: 0,
  },
  countColumn: {
    width: 80,
    flexShrink: 0,
  },
  shareColumn: {
    width: 80,
    minWidth: 0,
  },
  growthColumn: {
    width: 78,
    minWidth: 0,
  },
  rowNumberColumn: {
    width: 24,
    minWidth: 0,
  },
  grossProfitColumn: {
    width: 124,
    flexShrink: 0,
  },
  grossMarginColumn: {
    width: 84,
    flexShrink: 0,
  },
  // 中国页签宽表：每格单行本期值，行高比双行表格更紧凑；同期列组放在最右侧。
  chinaHeaderRow: {
    minHeight: 46,
  },
  chinaSupplierRow: {
    minHeight: 48,
  },
  chinaProductRow: {
    minHeight: 58,
  },
  chinaSupplierAmountColumn: {
    // 右侧 10pt 留给行内 › 下钻箭头；表头排序箭头借用同一段留白（supplierMoneyHeaderCell）。
    width: 84,
    minWidth: 0,
    paddingRight: 10,
    position: "relative",
  },
  chinaChevronWrap: {
    position: "absolute",
    top: 0,
    bottom: 0,
    right: 1,
    justifyContent: "center",
  },
  chinaInlineChevron: {
    color: "#64748B",
    fontSize: 18,
    lineHeight: 20,
  },
  chinaQuantityColumn: {
    width: 60,
    minWidth: 0,
  },
  chinaProductAmountColumn: {
    // 单行本期金额，与 CHINA_PRODUCT_FIRST_SCREEN_FIXED 里的金额宽度保持一致，保证首屏能完整露出金额列。
    width: 84,
    flexShrink: 0,
  },
  chinaShareColumn: {
    width: 60,
    minWidth: 0,
    gap: 3,
  },
  chinaAverageColumn: {
    width: 64,
    minWidth: 0,
  },
  chinaGrowthColumn: {
    // 同期基数很小时增长率会到四位数（如 +1011.5%），64pt 会截断。
    width: 74,
    minWidth: 0,
  },
  chinaCompareAmountColumn: {
    width: 84,
    flexShrink: 0,
  },
  chinaCompareQuantityColumn: {
    width: 56,
    minWidth: 0,
  },
  chinaMetricText: {
    fontWeight: "600",
  },
  chinaShareTrack: {
    height: 3,
    overflow: "hidden",
    borderRadius: 2,
    backgroundColor: "#EEF2F7",
  },
  chinaShareFill: {
    height: 3,
    borderRadius: 2,
    backgroundColor: "#60A5FA",
  },
  headerGroup: {
    gap: 2,
  },
  headerGroupTitle: {
    alignItems: "center",
    borderBottomWidth: 1,
    borderBottomColor: "#D1D5DB",
    paddingBottom: 2,
  },
  headerGroupColumns: {
    // 与 supplierTableRow / productTableRow 的列间距一致，子列才能与数据行对齐。
    flexDirection: "row",
    gap: 3,
  },
  headerSubText: {
    color: "#374151",
    fontWeight: "600",
  },
  chinaCompareHeaderText: {
    color: "#6B7280",
    fontWeight: "600",
  },
  chinaPageTotalRow: {
    minHeight: 48,
    backgroundColor: "#F8FAFC",
  },
  chinaPageTotalFrozen: {
    backgroundColor: "#F8FAFC",
  },
  chinaImageFrame: {
    width: 44,
    height: 44,
    position: "relative",
    overflow: "visible",
  },
  rankBadge: {
    position: "absolute",
    top: -4,
    left: -4,
    minWidth: 18,
    height: 16,
    paddingHorizontal: 3,
    alignItems: "center",
    justifyContent: "center",
    borderWidth: 1,
    borderColor: "#D1D5DB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  rankBadgeText: {
    color: "#374151",
    fontSize: 10,
    lineHeight: 12,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  tableCellText: {
    color: "#111827",
    fontVariant: ["tabular-nums"],
  },
  numericText: {
    textAlign: "right",
  },
  strongText: {
    fontWeight: "700",
  },
  headerText: {
    color: "#374151",
    fontWeight: "700",
  },
  sortableHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
    gap: 2,
    minWidth: 0,
  },
  sortableHeaderLabel: {
    flexShrink: 1,
  },
  sortIndicator: {
    color: "#9CA3AF",
    fontSize: 10,
    lineHeight: 14,
  },
  sortActiveText: {
    color: "#2563EB",
  },
  supplierMoneyHeaderCell: {
    // 供应商金额列右侧 10pt 是行内 › 箭头的留白；表头把排序箭头放进这段留白，
    // 标签与下方数字右对齐，英文 Revenue 不会被箭头挤到截断。
    marginRight: -10,
  },
  row: {
    borderWidth: 1,
    borderColor: "#E5E7EB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
  },
  selectedRow: {
    borderColor: LINKED_COLORS.supplier,
    backgroundColor: LINKED_COLORS.supplierBackground,
  },
  rowPressable: {
    gap: 10,
    padding: 12,
  },
  rowHeader: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  rowTitleWrap: {
    flex: 1,
    minWidth: 0,
  },
  rowTitle: {
    color: "#111827",
    fontWeight: "700",
  },
  metricsGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  metricBox: {
    minWidth: 112,
    flex: 1,
    borderRadius: 6,
    backgroundColor: "#F3F4F6",
    paddingHorizontal: 8,
    paddingVertical: 6,
  },
  metricLabel: {
    color: "#4B5563",
  },
  metricValue: {
    color: "#111827",
    fontWeight: "700",
  },
  metricCompare: {
    color: "#6B7280",
  },
  productRow: {
    minHeight: 92,
    flexDirection: "row",
    gap: 10,
    borderWidth: 1,
    borderColor: "#E5E7EB",
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
    padding: 10,
  },
  productImage: {
    width: 44,
    height: 44,
    borderRadius: 6,
    backgroundColor: "#E5E7EB",
  },
  productImagePlaceholder: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 6,
    backgroundColor: "#E5E7EB",
  },
  placeholderText: {
    color: "#6B7280",
  },
  productMain: {
    flex: 1,
    minWidth: 0,
    gap: 4,
  },
  productMetrics: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  stateBox: {
    minHeight: 72,
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
    padding: 12,
  },
  modal: {
    maxHeight: "82%",
    margin: 18,
    gap: 10,
    borderRadius: 8,
    backgroundColor: "#FFFFFF",
    padding: 16,
  },
  drilldownModal: {
    flex: 1,
    height: "100%",
    maxHeight: "100%",
    margin: 0,
    borderRadius: 0,
    paddingHorizontal: 10,
    paddingTop: 48,
    paddingBottom: 24,
  },
  modalTitle: {
    color: "#111827",
    fontWeight: "700",
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
  modalList: {
    maxHeight: 420,
  },
  drilldownModalList: {
    flex: 1,
    maxHeight: "100%",
  },
  drillRow: {
    gap: 3,
    borderBottomWidth: 1,
    borderBottomColor: "#E5E7EB",
    paddingVertical: 10,
  },
});
