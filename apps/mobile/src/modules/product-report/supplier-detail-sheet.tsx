import { useMemo, useState, type ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Button, IconButton, Modal, Portal, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import {
  fetchProductBranchBreakdown,
  fetchProductReportProductRows,
  fetchSupplierBranchBreakdown,
  type ProductReportDateQuery,
  type ProductReportProductRow,
  type ProductReportSnapshot,
  type SupplierReportKind,
  type SupplierReportRow,
} from "@/modules/product-report/api";
import { PRODUCT_PAGE_SIZE } from "@/modules/product-report/pagination";
import { CompactPager, SegmentToggle, SortChips } from "@/modules/product-report/product-report-card-parts";
import {
  PRODUCT_BRANCH_SORT_VALUES,
  ProductBranchRow,
  ProductReportCard,
  SUPPLIER_BRANCH_SORT_VALUES,
  SupplierBranchRow,
} from "@/modules/product-report/product-report-rows";
import {
  DEFAULT_REPORT_SORT,
  getReportSortKey,
  sortReportRows,
  toggleReportSort,
  type ReportSort,
  type ReportSortField,
} from "@/modules/product-report/sorting";
import { formatWholeMoney } from "@/modules/reports/format";
import { GrowthPill, REPORT_UI } from "@/modules/reports/report-card-parts";
import { REPORT_QUERY_OPTIONS } from "@/modules/reports/report-config";
import { LINKED_COLORS } from "@/modules/product-report/linked-colors";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 点供应商后的弹窗：先选「商品明细」或「分店数据」，两处都能继续下钻——
 * 商品明细里点商品看该商品各分店的销售；分店数据里点分店看该供应商在该分店的商品。
 * 所有层级都在同一个底部抽屉里切换（左上角 ‹ 返回上一层），不叠加多层弹窗。
 */

type SheetView =
  | { name: "choose" }
  | { name: "products" }
  | { name: "branches" }
  | { name: "productBranches"; product: ProductReportProductRow }
  | { name: "branchProducts"; branchCode: string; branchName: string };

const SORT_FIELDS: readonly { field: ReportSortField; labelKey: string }[] = [
  { field: "amount", labelKey: "productReport.columns.amount" },
  { field: "quantity", labelKey: "productReport.columns.quantity" },
  { field: "unitPrice", labelKey: "productReport.columns.averagePrice" },
];

type SnapshotQueryState = "loading" | "error" | "incomplete" | "failed" | "ready";

// 与主报表一致：统计补算未完成时继续等待，轮询耗尽后提示重试；后端标记「更新失败」要单独说明。
function getSnapshotQueryState(query: {
  data: ProductReportSnapshot<unknown> | undefined;
  isLoading: boolean;
  isError: boolean;
  isFetching: boolean;
}): SnapshotQueryState {
  if (query.isError) return "error";
  if (query.isLoading) return "loading";
  const snapshot = query.data;
  if (snapshot === undefined) return "loading";
  if (snapshot.isComplete) return "ready";
  if (!snapshot.pollingExhausted || query.isFetching) return "loading";
  return snapshot.statisticStatus === "Failed" ? "failed" : "incomplete";
}

function ScopeChip({ tone, label, value }: { tone: "branch" | "supplier"; label: string; value: string }) {
  const branch = tone === "branch";
  const color = branch ? LINKED_COLORS.branch : LINKED_COLORS.supplier;
  return (
    <View
      style={[
        styles.scopeChip,
        {
          borderColor: branch ? LINKED_COLORS.branchBorder : LINKED_COLORS.supplierBorder,
          backgroundColor: branch ? LINKED_COLORS.branchBackground : LINKED_COLORS.supplierBackground,
        },
      ]}
    >
      <View style={[styles.scopeDot, { backgroundColor: color }]} />
      <Text style={[styles.scopeLabel, { color: branch ? LINKED_COLORS.branchText : LINKED_COLORS.supplierText }]}>{label}</Text>
      <Text
        style={[styles.scopeValue, { color: branch ? LINKED_COLORS.branchText : LINKED_COLORS.supplierText }]}
        numberOfLines={1}
      >
        {value}
      </Text>
    </View>
  );
}

function OptionRow({
  icon,
  tone,
  title,
  description,
  count,
  onPress,
}: {
  icon: string;
  tone: "purple" | "blue";
  title: string;
  description: string;
  count: string | null;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={[title, description, count].filter(Boolean).join(" ")}
      onPress={onPress}
      style={styles.option}
    >
      <View style={[styles.optionIcon, tone === "purple" ? styles.optionIconPurple : null]}>
        <Text style={[styles.optionIconText, tone === "purple" ? { color: LINKED_COLORS.supplierText } : null]}>{icon}</Text>
      </View>
      <View style={styles.optionText}>
        <Text style={styles.optionTitle}>{title}</Text>
        <Text style={styles.optionDescription} numberOfLines={1}>{description}</Text>
      </View>
      {count ? (
        <View style={styles.optionCount}>
          <Text style={styles.optionCountText}>{count}</Text>
        </View>
      ) : null}
      <Text style={styles.optionChevron}>›</Text>
    </Pressable>
  );
}

export function SupplierDetailSheet({
  supplier,
  kind,
  queryParams,
  rangeLabel,
  accountIdentity,
  scopeVersion,
  summaryShareText,
  growthNewLabel,
  costPendingLabel,
  costNoActivityLabel,
  onClose,
  onLinkFilter,
}: {
  supplier: SupplierReportRow;
  kind: SupplierReportKind;
  /** 全部收银启用分店的日期查询：弹窗看的是该供应商的全部分店，不继承页面上的单店筛选。 */
  queryParams: ProductReportDateQuery | null;
  rangeLabel: string;
  accountIdentity: string;
  scopeVersion: number;
  summaryShareText: string;
  growthNewLabel: string;
  costPendingLabel: string;
  costNoActivityLabel: string;
  onClose: () => void;
  onLinkFilter: (supplierCode: string) => void;
}) {
  const { t } = useAppTranslation("common");
  const insets = useSafeAreaInsets();
  // 视图栈：下钻 push，左上角返回 pop，所以从「分店 → 商品 → 该商品分店」返回时回到刚才的分店商品列表；
  // 选择页的入口和分段切换是替换栈顶（它们是同一层的并列视图，没有「返回」）。
  const [stack, setStack] = useState<readonly SheetView[]>([{ name: "choose" }]);
  const view = stack[stack.length - 1];
  const pushView = (next: SheetView) => setStack((current) => [...current, next]);
  const replaceView = (next: SheetView) => setStack((current) => [...current.slice(0, -1), next]);
  const [productPage, setProductPage] = useState(1);
  const [branchProductPage, setBranchProductPage] = useState(1);
  const [productSort, setProductSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);
  const [branchSort, setBranchSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);
  const [productBranchSort, setProductBranchSort] = useState<ReportSort>(DEFAULT_REPORT_SORT);

  const supplierCode = supplier.supplierCode;
  const supplierTitle = supplier.supplierName.trim() || supplierCode;
  const supplierCodes = useMemo(() => [supplierCode], [supplierCode]);
  const isChina = kind === "china";
  const productSortKey = getReportSortKey(productSort);
  const activeBranch = view.name === "branchProducts" ? view : null;
  const activeProduct = view.name === "productBranches" ? view.product : null;
  const branchScopedParams = useMemo(
    () => (queryParams && activeBranch ? { ...queryParams, branchCodes: [activeBranch.branchCode] } : null),
    [activeBranch, queryParams],
  );

  const baseKey = ["product-report", "supplier-sheet", accountIdentity, kind, scopeVersion, queryParams, supplierCode] as const;

  // 供应商全部分店的商品明细：弹窗一打开就取第一页，选择页上的「N 款」直接来自它的总数。
  const productsQuery = useQuery({
    queryKey: [...baseKey, "products", productPage, productSortKey],
    queryFn: ({ signal }) => fetchProductReportProductRows(
      kind,
      queryParams!,
      supplierCodes,
      productPage,
      PRODUCT_PAGE_SIZE,
      undefined,
      productSort,
      { signal },
    ),
    enabled: Boolean(queryParams),
    placeholderData: keepPreviousData,
    ...REPORT_QUERY_OPTIONS,
  });
  // 该供应商在各分店的销售：选择页上的「N 家」与分店数据共用。
  const branchesQuery = useQuery({
    queryKey: [...baseKey, "branches"],
    queryFn: ({ signal }) => fetchSupplierBranchBreakdown(kind, queryParams!, supplierCode, { signal }),
    enabled: Boolean(queryParams),
    ...REPORT_QUERY_OPTIONS,
  });
  // 分店的商品明细：同一个商品接口，只把分店范围收窄到所点的那一家。
  const branchProductsQuery = useQuery({
    queryKey: [...baseKey, "branch-products", activeBranch?.branchCode ?? "", branchProductPage, productSortKey],
    queryFn: ({ signal }) => fetchProductReportProductRows(
      kind,
      branchScopedParams!,
      supplierCodes,
      branchProductPage,
      PRODUCT_PAGE_SIZE,
      undefined,
      productSort,
      { signal },
    ),
    enabled: Boolean(branchScopedParams),
    placeholderData: keepPreviousData,
    ...REPORT_QUERY_OPTIONS,
  });
  // 商品的分店数据：该商品在全部分店的销售（与供应商无关，商品本身就是范围）。
  const productBranchesQuery = useQuery({
    queryKey: [...baseKey, "product-branches", activeProduct?.productCode ?? ""],
    queryFn: ({ signal }) => fetchProductBranchBreakdown(queryParams!, activeProduct!.productCode, { signal }),
    enabled: Boolean(queryParams && activeProduct),
    ...REPORT_QUERY_OPTIONS,
  });

  const sortedBranches = useMemo(
    () => sortReportRows(branchesQuery.data?.data ?? [], branchSort, SUPPLIER_BRANCH_SORT_VALUES, (row) => row.branchCode),
    [branchSort, branchesQuery.data],
  );
  const sortedProductBranches = useMemo(
    () => sortReportRows(productBranchesQuery.data?.data ?? [], productBranchSort, PRODUCT_BRANCH_SORT_VALUES, (row) => row.branchCode),
    [productBranchSort, productBranchesQuery.data],
  );

  const sortChipFields = SORT_FIELDS.map(({ field, labelKey }) => ({ field, label: t(labelKey) }));
  const productCount = productsQuery.data?.isComplete ? productsQuery.data.data.total : null;
  const branchCount = branchesQuery.data?.isComplete ? branchesQuery.data.data.length : null;

  const goBack = () => setStack((current) => (current.length > 1 ? current.slice(0, -1) : current));
  const applyProductSort = (field: ReportSortField) => {
    setProductSort((current) => toggleReportSort(current, field));
    setProductPage(1);
    setBranchProductPage(1);
  };

  const isChoose = view.name === "choose";
  const isDrill = view.name === "productBranches" || view.name === "branchProducts";
  const productLabel = (product: ProductReportProductRow) => product.itemNumber || product.productCode;

  let title = supplierTitle;
  let subtitle = [
    supplierCode !== supplierTitle ? supplierCode : "",
    rangeLabel,
    t("productReport.filters.allStores"),
  ].filter(Boolean).join(" · ");
  if (view.name === "productBranches") {
    title = view.product.productName || productLabel(view.product);
    subtitle = [productLabel(view.product), t("productReport.drilldown.product")].join(" · ");
  } else if (view.name === "branchProducts") {
    title = view.branchName || view.branchCode;
    subtitle = [view.branchCode, t("productReport.sheet.branchProductsSubtitle"), rangeLabel].filter(Boolean).join(" · ");
  }

  const retry = (query: { refetch: () => unknown }) => () => { void query.refetch(); };

  // 通用的加载 / 失败 / 空态外壳；ready 且有行才渲染内容。
  const renderBody = (
    query: { data: ProductReportSnapshot<unknown> | undefined; isLoading: boolean; isError: boolean; isFetching: boolean; refetch: () => unknown },
    isEmpty: boolean,
    emptyLabel: string,
    content: () => ReactNode,
  ) => {
    const state = getSnapshotQueryState(query);
    if (state === "loading") {
      return <View style={styles.stateBox}><ActivityIndicator /><Text>{t("productReport.states.loading")}</Text></View>;
    }
    if (state !== "ready") {
      const label = state === "failed"
        ? t("reports.states.statisticsFailed")
        : state === "incomplete"
          ? t("reports.states.statisticsIncomplete")
          : t("productReport.states.error");
      return (
        <View style={styles.stateBox}>
          <Text variant="bodyMedium" style={styles.stateText}>{label}</Text>
          {state === "failed" ? null : (
            <Button mode="contained" onPress={retry(query)}>{t("actions.retry")}</Button>
          )}
        </View>
      );
    }
    if (isEmpty) return <View style={styles.stateBox}><Text variant="bodyMedium">{emptyLabel}</Text></View>;
    return content();
  };

  const renderProductList = (
    query: typeof productsQuery,
    page: number,
    setPage: (page: number) => void,
  ) => {
    const rows = query.data?.data.rows ?? [];
    const total = query.data?.data.total ?? 0;
    const pageCount = Math.max(1, Math.ceil(total / PRODUCT_PAGE_SIZE));
    return (
      <>
        <SortChips fields={sortChipFields} sort={productSort} onSort={applyProductSort} />
        <ScrollView
          bounces={false}
          style={styles.list}
          contentContainerStyle={[styles.listContent, query.isPlaceholderData ? styles.listStale : null]}
        >
          {renderBody(query, rows.length === 0, t("productReport.states.emptyProducts"), () => rows.map((item, index) => (
            <ProductReportCard
              key={item.id}
              item={item}
              rowNumber={(page - 1) * PRODUCT_PAGE_SIZE + index + 1}
              growthNewLabel={growthNewLabel}
              costPendingLabel={costPendingLabel}
              costNoActivityLabel={costNoActivityLabel}
              trailingHint={`${t("productReport.actions.branchDetail")} ›`}
              onPress={() => pushView({ name: "productBranches", product: item })}
            />
          )))}
        </ScrollView>
        <View style={styles.footer}>
          <Text style={styles.footerText} numberOfLines={1}>
            {t("productReport.sheet.totalCount", { count: total, pageSize: PRODUCT_PAGE_SIZE })}
          </Text>
          <CompactPager
            page={page}
            pageCount={pageCount}
            onPrevious={() => setPage(Math.max(1, page - 1))}
            onNext={() => setPage(Math.min(pageCount, page + 1))}
            previousLabel={t("productReport.actions.previous")}
            nextLabel={t("productReport.actions.next")}
          />
        </View>
      </>
    );
  };

  return (
    <Portal>
      <Modal
        visible
        onDismiss={onClose}
        style={styles.sheetWrapper}
        contentContainerStyle={[
          styles.sheet,
          isChoose ? styles.sheetCompact : styles.sheetTall,
          { paddingBottom: Math.max(insets.bottom, 12) },
        ]}
      >
        <View style={styles.handle} />
        <View style={styles.head}>
          {isDrill ? (
            <IconButton
              icon="chevron-left"
              mode="outlined"
              size={18}
              accessibilityLabel={t("actions.back")}
              onPress={goBack}
              style={styles.headButton}
            />
          ) : null}
          <View style={styles.titleBlock}>
            <Text style={styles.title} numberOfLines={1}>{title}</Text>
            <Text style={styles.subtitle} numberOfLines={1}>{subtitle}</Text>
          </View>
          <IconButton
            icon="close"
            mode="outlined"
            size={18}
            accessibilityLabel={t("actions.close")}
            onPress={onClose}
            style={styles.headButton}
          />
        </View>

        {isChoose ? (
          <View style={styles.body}>
            <View style={styles.summary}>
              <Text style={styles.summaryAmount} numberOfLines={1}>{formatWholeMoney(supplier.revenue)}</Text>
              <GrowthPill current={supplier.revenue} compare={supplier.compareRevenue} newLabel={growthNewLabel} />
              <Text style={styles.summaryShare} numberOfLines={1}>{summaryShareText}</Text>
            </View>
            <OptionRow
              icon="▦"
              tone="purple"
              title={t("productReport.sheet.tabProducts")}
              description={t("productReport.sheet.chooseProductsDesc")}
              count={productCount === null ? null : t("productReport.sheet.productCount", { count: productCount })}
              onPress={() => replaceView({ name: "products" })}
            />
            <OptionRow
              icon="▤"
              tone="blue"
              title={t("productReport.sheet.tabBranches")}
              description={t("productReport.sheet.chooseBranchesDesc")}
              count={branchCount === null ? null : t("productReport.sheet.branchCount", { count: branchCount })}
              onPress={() => replaceView({ name: "branches" })}
            />
            <Pressable
              accessibilityRole="button"
              onPress={() => { onLinkFilter(supplierCode); onClose(); }}
              style={styles.linkOnly}
            >
              <Text style={styles.linkOnlyText}>{t("productReport.sheet.linkOnly")}</Text>
            </Pressable>
          </View>
        ) : (
          <View style={styles.body}>
            {view.name === "products" || view.name === "branches" ? (
              <SegmentToggle
                value={view.name}
                options={[
                  {
                    value: "products",
                    label: productCount === null
                      ? t("productReport.sheet.tabProducts")
                      : t("productReport.sheet.tabWithCount", { label: t("productReport.sheet.tabProducts"), count: productCount }),
                  },
                  {
                    value: "branches",
                    label: branchCount === null
                      ? t("productReport.sheet.tabBranches")
                      : t("productReport.sheet.tabWithCount", { label: t("productReport.sheet.tabBranches"), count: branchCount }),
                  },
                ]}
                onChange={(next) => replaceView(next === "products" ? { name: "products" } : { name: "branches" })}
              />
            ) : (
              <View style={styles.scopeRow}>
                {view.name === "branchProducts" ? (
                  <ScopeChip tone="branch" label={t("productReport.linked.branchLabel")} value={view.branchName || view.branchCode} />
                ) : null}
                <ScopeChip tone="supplier" label={t("productReport.linked.supplierLabel")} value={supplierTitle} />
              </View>
            )}

            {view.name === "products" ? renderProductList(productsQuery, productPage, setProductPage) : null}
            {view.name === "branchProducts" ? renderProductList(branchProductsQuery, branchProductPage, setBranchProductPage) : null}

            {view.name === "branches" ? (
              <>
                <SortChips
                  fields={sortChipFields}
                  sort={branchSort}
                  onSort={(field) => setBranchSort((current) => toggleReportSort(current, field))}
                />
                <ScrollView bounces={false} style={styles.list} contentContainerStyle={styles.listFrame}>
                  {renderBody(branchesQuery, sortedBranches.length === 0, t("productReport.sheet.emptyBranches"), () => sortedBranches.map((row, index) => (
                    <SupplierBranchRow
                      key={row.id}
                      row={row}
                      rowNumber={index + 1}
                      growthNewLabel={growthNewLabel}
                      costPendingLabel={costPendingLabel}
                      costNoActivityLabel={costNoActivityLabel}
                      showComparison={isChina}
                      trailingHint={`${t("productReport.sheet.rowDetailHint")} ›`}
                      onPress={() => {
                        setBranchProductPage(1);
                        pushView({ name: "branchProducts", branchCode: row.branchCode, branchName: row.branchName });
                      }}
                    />
                  )))}
                </ScrollView>
              </>
            ) : null}

            {view.name === "productBranches" ? (
              <>
                <SortChips
                  fields={sortChipFields}
                  sort={productBranchSort}
                  onSort={(field) => setProductBranchSort((current) => toggleReportSort(current, field))}
                />
                <ScrollView bounces={false} style={styles.list} contentContainerStyle={styles.listFrame}>
                  {renderBody(productBranchesQuery, sortedProductBranches.length === 0, t("productReport.sheet.emptyBranches"), () => sortedProductBranches.map((row, index) => (
                    <ProductBranchRow
                      key={row.id}
                      row={row}
                      rowNumber={index + 1}
                      growthNewLabel={growthNewLabel}
                      costPendingLabel={costPendingLabel}
                      costNoActivityLabel={costNoActivityLabel}
                    />
                  )))}
                </ScrollView>
              </>
            ) : null}
          </View>
        )}
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  // 底部抽屉：Paper Modal 默认居中，这里改为贴底并去掉底部安全区外边距（由抽屉自己补内边距）。
  sheetWrapper: {
    justifyContent: "flex-end",
    marginBottom: 0,
  },
  sheet: {
    borderTopLeftRadius: 20,
    borderTopRightRadius: 20,
    backgroundColor: REPORT_UI.background,
    paddingTop: 8,
  },
  // 选择页只有两个入口，按内容高度；下钻页是长列表，占 88% 高度。
  sheetCompact: {
    maxHeight: "88%",
  },
  sheetTall: {
    height: "88%",
  },
  handle: {
    alignSelf: "center",
    width: 36,
    height: 4,
    borderRadius: 2,
    backgroundColor: "#D0D5DD",
    marginBottom: 6,
  },
  head: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 4,
    paddingLeft: 8,
    paddingRight: 6,
  },
  headButton: {
    margin: 0,
  },
  titleBlock: {
    flex: 1,
    minWidth: 0,
    paddingTop: 6,
    paddingLeft: 6,
  },
  title: {
    color: REPORT_UI.text,
    fontSize: 18,
    fontWeight: "800",
  },
  subtitle: {
    color: REPORT_UI.muted,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
  },
  body: {
    flexShrink: 1,
    flexGrow: 1,
    gap: 10,
    paddingHorizontal: 14,
    paddingTop: 10,
  },
  summary: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    borderRadius: 12,
    backgroundColor: REPORT_UI.tile,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },
  summaryAmount: {
    color: REPORT_UI.text,
    fontSize: 20,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  summaryShare: {
    flex: 1,
    minWidth: 0,
    color: REPORT_UI.muted,
    fontSize: 11.5,
  },
  option: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    minHeight: 64,
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  optionIcon: {
    width: 42,
    height: 42,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 12,
    backgroundColor: REPORT_UI.accentSoft,
  },
  optionIconPurple: {
    backgroundColor: LINKED_COLORS.supplierBackground,
  },
  optionIconText: {
    color: REPORT_UI.accent,
    fontSize: 20,
  },
  optionText: {
    flex: 1,
    minWidth: 0,
  },
  optionTitle: {
    color: REPORT_UI.text,
    fontSize: 15,
    fontWeight: "800",
  },
  optionDescription: {
    color: REPORT_UI.muted,
    fontSize: 12,
  },
  optionCount: {
    borderRadius: 10,
    backgroundColor: REPORT_UI.accentSoft,
    paddingHorizontal: 8,
    paddingVertical: 2,
  },
  optionCountText: {
    color: REPORT_UI.accent,
    fontSize: 12,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  optionChevron: {
    color: "#98A2B3",
    fontSize: 22,
  },
  linkOnly: {
    alignItems: "center",
    justifyContent: "center",
    minHeight: 44,
    marginTop: 4,
  },
  linkOnlyText: {
    color: "#0958D9",
    fontSize: 13,
    fontWeight: "600",
  },
  scopeRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
  },
  scopeChip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    maxWidth: "100%",
    height: 30,
    borderWidth: 1,
    borderRadius: 15,
    paddingHorizontal: 10,
  },
  scopeDot: {
    width: 7,
    height: 7,
    borderRadius: 4,
  },
  scopeLabel: {
    fontSize: 10.5,
    fontWeight: "600",
    opacity: 0.75,
  },
  scopeValue: {
    flexShrink: 1,
    fontSize: 12.5,
    fontWeight: "700",
  },
  list: {
    flex: 1,
  },
  // 商品卡自带边框，列表只负责间距；分店行是无框行，外面套一层带边框的容器。
  listContent: {
    gap: 8,
  },
  listFrame: {
    overflow: "hidden",
    borderWidth: 1,
    borderColor: REPORT_UI.border,
    borderRadius: 14,
    backgroundColor: REPORT_UI.card,
  },
  listStale: {
    opacity: 0.5,
  },
  footer: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
    paddingTop: 2,
  },
  footerText: {
    flex: 1,
    minWidth: 0,
    color: REPORT_UI.muted,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
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
  stateText: {
    textAlign: "center",
  },
});
