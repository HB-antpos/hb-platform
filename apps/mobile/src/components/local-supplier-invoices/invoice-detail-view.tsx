import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, FlatList, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, Modal, Portal, Snackbar, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { DetailRowCard } from "@/components/local-supplier-invoices/detail-row-card";
import { DetailTag } from "@/components/local-supplier-invoices/detail-tag";
import { EditLineSheet } from "@/components/local-supplier-invoices/edit-line-sheet";
import { FilterChip } from "@/components/local-supplier-invoices/filter-chip";
import { HeaderEditSheet } from "@/components/local-supplier-invoices/header-edit-sheet";
import {
  formatMoney,
  formatOrderDate,
  formatQuantity,
} from "@/components/local-supplier-invoices/invoice-detail-format";
import { InvoiceSalesAnalysisView } from "@/components/local-supplier-invoices/invoice-sales-analysis-view";
import { MoreFiltersSheet } from "@/components/local-supplier-invoices/more-filters-sheet";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  batchUpsertInvoiceDetails,
  checkInvoiceProducts,
  checkInvoiceProductsSync,
  deleteInvoiceDetails,
  fetchCheckProductsJob,
  fetchInvoice,
  fetchInvoiceDetailsAll,
  fetchProductsByBarcode,
  isMissingCheckJobEndpoint,
  startCheckProductsJob,
  updateInvoiceDetailAction,
  updateInvoiceHeader,
} from "@/modules/local-supplier-invoices/api";
import {
  DETAIL_PROGRESS_BUCKETS,
  DETAIL_PROGRESS_BUCKET_COLORS,
  applyDetailFilters,
  buildInvoiceDetailSaveItems,
  buildInvoiceDetailSnapshotIndex,
  countActiveMoreFilterGroups,
  countEditedInvoiceDetailRows,
  createEmptyDetailFilters,
  createEmptyDetailMoreFilters,
  getDetailFilterCounts,
  getDetailProgressStats,
  getEditedInvoiceDetailFields,
  getExecutedPercent,
  getPriceChangeStats,
  hasActiveDetailFilters,
  isProductLinkConfirmed,
  runCheckProducts,
  toggleFilterValue,
  type DetailFilters,
  type DetailMoreFilters,
  type DetailProgressBucket,
  type DetailQuickFilter,
} from "@/modules/local-supplier-invoices/detail";
import type {
  BarcodeMatchedProduct,
  InvoiceDetailPriceChangeFilter,
  LocalSupplierInvoice,
  LocalSupplierInvoiceItem,
  UpdateInvoiceHeaderRequest,
} from "@/modules/local-supplier-invoices/types";
import { getInvoiceInboundStatusLabel } from "@/modules/local-supplier-invoices/types";
import type { Store } from "@/modules/shop/types";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export interface InvoiceDetailInitialView {
  detailGuid?: string;
  priceChange?: InvoiceDetailPriceChangeFilter;
  search?: string;
}

export interface InvoiceDetailViewProps {
  invoice: LocalSupplierInvoice;
  /** 从商品编辑页返回时恢复的视图：筛选、搜索与要回到的那一行。 */
  initialView: InvoiceDetailInitialView | null;
  /** 有 LocalPurchase.Edit：看到处理进度、匹配/操作类型、更多筛选，并可改表头。否则是只读视图（总部处理）。 */
  canEditHeader: boolean;
  /** 管理员：可改明细行、改操作类型、删行、选用匹配商品（与 Web 的 canEditDetailRows 一致）。 */
  canEditRows: boolean;
  stores: Store[];
  onClose: () => void;
  /** 表头或金额变化后通知列表同步。 */
  onInvoiceUpdated: (invoice: LocalSupplierInvoice) => void;
  onOpenProduct: (
    detail: LocalSupplierInvoiceItem,
    view: { priceChange: InvoiceDetailPriceChangeFilter; search: string }
  ) => void;
  onCopy: (label: string, value: string) => void;
  /** 商品检测等会改变列表上「待检测 / 新品」汇总的操作完成后通知列表：关闭详情时重查一次。 */
  onDataChanged?: () => void;
}

function buildInitialFilters(view: InvoiceDetailInitialView | null): DetailFilters {
  const filters = createEmptyDetailFilters();
  if (view?.priceChange === "up") filters.quickFilters = ["priceUp"];
  if (view?.priceChange === "down") filters.quickFilters = ["priceDown"];
  filters.searchText = view?.search?.trim() ?? "";
  return filters;
}

function toPriceChangeFilter(filters: DetailFilters): InvoiceDetailPriceChangeFilter {
  const up = filters.quickFilters.includes("priceUp");
  const down = filters.quickFilters.includes("priceDown");
  if (up && !down) return "up";
  if (down && !up) return "down";
  return "all";
}

/**
 * 进货单明细（与 Web InvoiceEdit 对齐的一期）：
 * 一次加载整张单 → 前端筛选 / 分桶 / 合计；管理员可改行并「保存修改」统一落库。
 */
export function InvoiceDetailView({
  invoice,
  initialView,
  canEditHeader,
  canEditRows,
  stores,
  onClose,
  onInvoiceUpdated,
  onOpenProduct,
  onCopy,
  onDataChanged,
}: InvoiceDetailViewProps) {
  const { t, language } = useAppTranslation(["localSupplierInvoices", "common"]);
  const guid = invoice.invoiceGuid;
  const getErrorMessage = useCallback(
    (error: unknown, fallbackKey: string) => resolveLocalizedErrorMessage(error, { language, t, fallbackKey }),
    [language, t]
  );

  const [details, setDetails] = useState<LocalSupplierInvoiceItem[]>([]);
  const [snapshot, setSnapshot] = useState<LocalSupplierInvoiceItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [filters, setFilters] = useState<DetailFilters>(() => buildInitialFilters(initialView));
  const [searchInput, setSearchInput] = useState(initialView?.search ?? "");
  const [infoExpanded, setInfoExpanded] = useState(false);
  const [moreVisible, setMoreVisible] = useState(false);
  const [headerVisible, setHeaderVisible] = useState(false);
  const [headerSaving, setHeaderSaving] = useState(false);
  const [editingGuid, setEditingGuid] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [salesVisible, setSalesVisible] = useState(false);
  // 提示条画在本弹层内部：详情是全屏 Modal，列表页的 Snackbar 在它下面，用户根本看不到（检测完成等长任务反馈尤其需要）。
  const [toast, setToast] = useState("");
  const notify = useCallback((message: string) => setToast(message), []);
  const [checking, setChecking] = useState(false);
  // 检测在后台任务里跑，页面关闭后要停止轮询；同时避免连点重复提交。
  const checkingRef = useRef(false);
  const unmountedRef = useRef(false);
  const requestIdRef = useRef(0);
  const listRef = useRef<FlatList<LocalSupplierInvoiceItem>>(null);
  const pendingAnchorRef = useRef<string | null>(initialView?.detailGuid ?? null);

  // ---- 加载 ----
  const loadDetails = useCallback(async (spinner = true) => {
    const requestId = ++requestIdRef.current;
    if (spinner) setLoading(true);
    try {
      const rows = await fetchInvoiceDetailsAll(guid);
      if (requestId !== requestIdRef.current) return false;
      setDetails(rows);
      setSnapshot(rows);
      return true;
    } catch (error) {
      if (requestId === requestIdRef.current) notify(getErrorMessage(error, "messages.detailsLoadFailed"));
      return false;
    } finally {
      if (requestId === requestIdRef.current) setLoading(false);
    }
  }, [getErrorMessage, guid, notify]);

  useEffect(() => {
    void loadDetails();
    return () => { requestIdRef.current += 1; };
  }, [loadDetails]);

  /** 明细变化后（保存、删除、选用）重读表头：总金额、收货金额会跟着变。 */
  const refreshHeader = useCallback(async () => {
    try {
      const fresh = await fetchInvoice(guid);
      onInvoiceUpdated({
        ...invoice,
        storeCode: fresh.storeCode || invoice.storeCode,
        storeName: fresh.storeName || invoice.storeName,
        supplierCode: fresh.supplierCode || invoice.supplierCode,
        supplierName: fresh.supplierName || invoice.supplierName,
        orderDate: fresh.orderDate || invoice.orderDate,
        inboundDate: fresh.inboundDate,
        remarks: fresh.remarks,
        totalAmount: fresh.totalAmount,
        receivedTotalAmount: fresh.receivedTotalAmount,
        flowStatus: fresh.flowStatus ?? invoice.flowStatus,
        inboundStatus: fresh.inboundStatus ?? invoice.inboundStatus,
        updatedAt: fresh.updatedAt || invoice.updatedAt,
      });
    } catch {
      // 表头刷新失败不影响明细操作，列表下次刷新会带回最新金额。
    }
  }, [guid, invoice, onInvoiceUpdated]);

  // ---- 搜索防抖 ----
  useEffect(() => {
    const next = searchInput.trim();
    if (next === filters.searchText) return;
    const timeout = setTimeout(() => setFilters((current) => ({ ...current, searchText: next })), 250);
    return () => clearTimeout(timeout);
  }, [filters.searchText, searchInput]);

  // ---- 派生数据 ----
  const showWorkbench = canEditHeader;
  const visible = useMemo(() => applyDetailFilters(details, filters), [details, filters]);
  const counts = useMemo(() => getDetailFilterCounts(details), [details]);
  const stats = useMemo(() => getDetailProgressStats(details), [details]);
  const priceStats = useMemo(() => getPriceChangeStats(details), [details]);
  const snapshotIndex = useMemo(() => buildInvoiceDetailSnapshotIndex(snapshot), [snapshot]);
  const editedCount = useMemo(() => countEditedInvoiceDetailRows(details, snapshotIndex), [details, snapshotIndex]);
  const hasUnsaved = editedCount > 0;
  const totals = useMemo(() => visible.reduce(
    (sum, row) => ({ qty: sum.qty + (row.quantity ?? 0), amount: sum.amount + (row.amount ?? 0) }),
    { qty: 0, amount: 0 }
  ), [visible]);
  const filtersActive = hasActiveDetailFilters(filters);
  const moreGroupCount = countActiveMoreFilterGroups(filters.more);
  const editingDetail = useMemo(
    () => details.find((row) => row.detailGuid === editingGuid) ?? null,
    [details, editingGuid]
  );
  const inboundLabel = getInvoiceInboundStatusLabel(invoice.inboundStatus);
  const executedPercent = getExecutedPercent(stats);

  // ---- 恢复定位：从商品编辑回来时滚回原来那一行（筛选后找不到就算了） ----
  useEffect(() => {
    const anchor = pendingAnchorRef.current;
    if (!anchor || loading || !visible.length) return;
    const index = visible.findIndex((row) => row.detailGuid === anchor);
    pendingAnchorRef.current = null;
    if (index < 0) return;
    const frame = requestAnimationFrame(() => {
      listRef.current?.scrollToIndex({ index, animated: false, viewPosition: 0 });
    });
    return () => cancelAnimationFrame(frame);
  }, [loading, visible]);

  // ---- 未保存守卫 ----
  const save = useCallback(async () => {
    if (saving) return false;
    const items = buildInvoiceDetailSaveItems(details, snapshotIndex);
    if (!items.length) return true;
    setSaving(true);
    try {
      await batchUpsertInvoiceDetails(guid, items);
      notify(t("detail.unsaved.saved"));
      await loadDetails(false);
      await refreshHeader();
      return true;
    } catch (error) {
      notify(getErrorMessage(error, "detail.unsaved.saveFailed"));
      return false;
    } finally {
      setSaving(false);
    }
  }, [details, getErrorMessage, guid, loadDetails, notify, refreshHeader, saving, snapshotIndex, t]);

  const discard = useCallback(() => setDetails(snapshot), [snapshot]);

  /** 会重读明细或离开页面的操作：有未保存修改时先让用户选择保存 / 放弃 / 留下。 */
  const runAfterUnsavedGuard = useCallback((action: () => void) => {
    if (!hasUnsaved) {
      action();
      return;
    }
    Alert.alert(t("detail.unsaved.confirmTitle"), t("detail.unsaved.confirmBody"), [
      { text: t("detail.unsaved.stay"), style: "cancel" },
      { text: t("detail.unsaved.discardAndLeave"), style: "destructive", onPress: () => { discard(); action(); } },
      { text: t("detail.unsaved.saveAndContinue"), onPress: () => { void save().then((ok) => { if (ok) action(); }); } },
    ]);
  }, [discard, hasUnsaved, save, t]);

  const requestClose = useCallback(() => runAfterUnsavedGuard(onClose), [onClose, runAfterUnsavedGuard]);

  // ---- 行操作 ----
  const applyRow = useCallback((next: LocalSupplierInvoiceItem) => {
    setDetails((current) => current.map((row) => (row.detailGuid === next.detailGuid ? next : row)));
    setEditingGuid(null);
  }, []);

  const changeAction = useCallback(async (detail: LocalSupplierInvoiceItem, action: number) => {
    try {
      await updateInvoiceDetailAction(guid, detail.detailGuid, action);
      const patch = (rows: LocalSupplierInvoiceItem[]) => rows.map((row) => (
        row.detailGuid === detail.detailGuid ? { ...row, activityType: action } : row
      ));
      // 操作类型立即落库，同时推进快照，避免它被当成「未保存修改」。
      setDetails(patch);
      setSnapshot(patch);
      notify(t("detail.actionChange.saved"));
      return true;
    } catch (error) {
      notify(getErrorMessage(error, "detail.actionChange.failed"));
      return false;
    }
  }, [getErrorMessage, guid, notify, t]);

  const deleteRow = useCallback((detail: LocalSupplierInvoiceItem) => {
    runAfterUnsavedGuard(() => {
      Alert.alert(t("detail.edit.deleteTitle"), t("detail.edit.deleteBody"), [
        { text: t("detail.edit.cancel"), style: "cancel" },
        {
          text: t("detail.edit.deleteConfirm"),
          style: "destructive",
          onPress: () => {
            void deleteInvoiceDetails(guid, [detail.detailGuid])
              .then(async () => {
                setEditingGuid(null);
                notify(t("detail.deleteDone"));
                await loadDetails(false);
                await refreshHeader();
              })
              .catch((error) => notify(getErrorMessage(error, "detail.deleteFailed")));
          },
        },
      ]);
    });
  }, [getErrorMessage, guid, loadDetails, notify, refreshHeader, runAfterUnsavedGuard, t]);

  const loadMatches = useCallback(
    (detail: LocalSupplierInvoiceItem) => fetchProductsByBarcode(guid, detail.barcode),
    [guid]
  );

  /** 选用匹配商品：先写 productCode，再重新检测该行，确认后端确实沿用了它（条码不属于该商品时检测会清掉关联）。 */
  const linkProduct = useCallback(async (detail: LocalSupplierInvoiceItem, product: BarcodeMatchedProduct) => {
    if (hasUnsaved) {
      notify(t("detail.match.blockedUnsaved"));
      return false;
    }
    try {
      await batchUpsertInvoiceDetails(guid, [{ detailGUID: detail.detailGuid, productCode: product.productCode }]);
      const results = await checkInvoiceProducts(guid, [detail.detailGuid]);
      const confirmed = isProductLinkConfirmed(results.find((r) => r.detailGuid === detail.detailGuid), product.productCode);
      await loadDetails(false);
      notify(confirmed
        ? t("detail.match.linked", { item: product.itemNumber || product.productCode })
        : t("detail.match.notOwner"));
      if (confirmed) setEditingGuid(null);
      return confirmed;
    } catch (error) {
      notify(getErrorMessage(error, "detail.match.loadFailed"));
      return false;
    }
  }, [getErrorMessage, guid, hasUnsaved, loadDetails, notify, t]);

  const saveHeader = useCallback((payload: UpdateInvoiceHeaderRequest) => {
    setHeaderSaving(true);
    void updateInvoiceHeader(guid, payload)
      .then(async (fresh) => {
        onInvoiceUpdated({
          ...invoice,
          storeCode: fresh.storeCode || invoice.storeCode,
          storeName: fresh.storeName || invoice.storeName,
          supplierCode: fresh.supplierCode || invoice.supplierCode,
          supplierName: fresh.supplierName || invoice.supplierName,
          orderDate: fresh.orderDate || invoice.orderDate,
          inboundDate: fresh.inboundDate,
          remarks: fresh.remarks,
          totalAmount: fresh.totalAmount,
          receivedTotalAmount: fresh.receivedTotalAmount,
          flowStatus: fresh.flowStatus ?? invoice.flowStatus,
          inboundStatus: fresh.inboundStatus ?? invoice.inboundStatus,
          updatedAt: fresh.updatedAt || invoice.updatedAt,
        });
        setHeaderVisible(false);
        notify(t("detail.unsaved.headerSaved"));
        // 分店 / 供应商变更会级联到明细，必须重读（此处无未保存修改，入口已拦截）。
        await loadDetails(false);
      })
      .catch((error) => notify(getErrorMessage(error, "detail.unsaved.saveFailed")))
      .finally(() => setHeaderSaving(false));
  }, [getErrorMessage, guid, invoice, loadDetails, notify, onInvoiceUpdated, t]);

  // ---- 商品检测（后台任务；写入类，仅 LocalPurchase.Edit + 管理员，入口只在 canEditRows 时出现） ----
  const hasUnsavedRef = useRef(hasUnsaved);
  hasUnsavedRef.current = hasUnsaved;

  useEffect(() => {
    unmountedRef.current = false;
    return () => { unmountedRef.current = true; };
  }, []);

  /** detailGuids 缺省 = 整张单。检测结果由服务端写回明细，完成后重读明细与表头即可。 */
  const startCheck = useCallback((detailGuids?: string[]) => {
    if (checkingRef.current) return;
    checkingRef.current = true;
    setChecking(true);
    notify(t("detail.check.started"));
    void (async () => {
      try {
        const outcome = await runCheckProducts(
          {
            startJob: startCheckProductsJob,
            getJob: fetchCheckProductsJob,
            checkSync: checkInvoiceProductsSync,
            isMissingEndpoint: isMissingCheckJobEndpoint,
            sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
            now: () => Date.now(),
            isCancelled: () => unmountedRef.current,
          },
          guid,
          detailGuids
        );
        if (outcome.kind === "cancelled") return;
        if (outcome.kind === "failed") {
          notify(outcome.message || t("detail.check.failed"));
          return;
        }
        if (outcome.kind === "timeout") {
          notify(t("detail.check.timeout"));
          return;
        }
        onDataChanged?.();
        // 检测期间用户又改了行（未保存）时不重读明细，免得把他的修改冲掉。
        if (hasUnsavedRef.current) {
          notify(t("detail.check.doneKeepEdits"));
          return;
        }
        await loadDetails(false);
        await refreshHeader();
        const summary = outcome.summary;
        notify(summary
          ? t("detail.check.done", {
            total: summary.total,
            exists: summary.productExists,
            notExists: summary.productNotExists,
            abnormal: summary.barcodeAbnormal,
          })
          : t("detail.check.doneNoSummary"));
      } catch (error) {
        if (!unmountedRef.current) notify(getErrorMessage(error, "detail.check.failed"));
      } finally {
        checkingRef.current = false;
        if (!unmountedRef.current) setChecking(false);
      }
    })();
  }, [getErrorMessage, guid, loadDetails, notify, onDataChanged, refreshHeader, t]);

  const requestCheck = useCallback(() => {
    if (checkingRef.current) return;
    // 检测完成后会重读明细，先让用户处理未保存修改。
    runAfterUnsavedGuard(() => {
      const scoped = filtersActive && visible.length > 0 && visible.length < details.length;
      if (!scoped) {
        startCheck(undefined);
        return;
      }
      const visibleGuids = visible.map((row) => row.detailGuid);
      Alert.alert(t("detail.check.scopeTitle"), t("detail.check.scopeBody"), [
        { text: t("detail.check.scopeAll", { count: details.length }), onPress: () => startCheck(undefined) },
        { text: t("detail.check.scopeVisible", { count: visible.length }), onPress: () => startCheck(visibleGuids) },
        { text: t("detail.edit.cancel"), style: "cancel" },
      ]);
    });
  }, [details.length, filtersActive, runAfterUnsavedGuard, startCheck, t, visible]);

  // ---- 筛选操作 ----
  const toggleQuick = (filter: DetailQuickFilter) => setFilters((current) => ({
    ...current,
    quickFilters: toggleFilterValue(current.quickFilters, filter),
  }));
  const toggleBucket = (bucket: DetailProgressBucket) => setFilters((current) => ({
    ...current,
    progressBucket: current.progressBucket === bucket ? "all" : bucket,
  }));
  const clearFilters = () => {
    setSearchInput("");
    setFilters(createEmptyDetailFilters());
  };
  const countMatches = useCallback(
    (draft: DetailMoreFilters) => applyDetailFilters(details, { ...filters, more: draft }).length,
    [details, filters]
  );

  const handleOpenProduct = useCallback((detail: LocalSupplierInvoiceItem) => {
    runAfterUnsavedGuard(() => onOpenProduct(detail, { priceChange: toPriceChangeFilter(filters), search: filters.searchText }));
  }, [filters, onOpenProduct, runAfterUnsavedGuard]);
  const handleRowPress = useCallback((detail: LocalSupplierInvoiceItem) => {
    // 检测完成后会重读明细，期间改行容易被冲掉：先拦住并提示。
    if (checkingRef.current) {
      notify(t("detail.check.busy"));
      return;
    }
    setEditingGuid(detail.detailGuid);
  }, [notify, t]);

  const renderItem = useCallback(({ item }: { item: LocalSupplierInvoiceItem }) => (
    <View style={styles.rowWrap}>
      <DetailRowCard
        detail={item}
        editedKey={getEditedInvoiceDetailFields(item, snapshotIndex).join("|")}
        showWorkbench={showWorkbench}
        canEditRow={canEditRows}
        onPress={handleRowPress}
        onCopy={onCopy}
        onOpenProduct={handleOpenProduct}
        t={t}
      />
    </View>
  ), [canEditRows, handleOpenProduct, handleRowPress, onCopy, showWorkbench, snapshotIndex, t]);

  // ---- 头部（随列表一起滚动，给明细让出垂直空间） ----
  const header = (
    <View>
      <View style={styles.invoiceCard}>
        <View style={styles.headingRow}>
          <View style={styles.storeIcon}><Icon source="storefront-outline" size={20} color={HB_COLORS.brand} /></View>
          <View style={styles.identity}>
            <Text style={styles.supplier} numberOfLines={1}>{invoice.supplierName || invoice.supplierCode || "--"}</Text>
            <Text style={styles.context} numberOfLines={1}>
              {invoice.invoiceNo || "--"} · {invoice.storeName || invoice.storeCode || "--"}
            </Text>
          </View>
          <View style={styles.statusStack}>
            {showWorkbench && invoice.flowStatus != null && invoice.flowStatus in { 0: 1, 1: 1, 2: 1, 3: 1 } ? (
              <DetailTag label={t(`detail.flowStatus.${invoice.flowStatus}`)} tone={invoice.flowStatus === 2 ? "green" : invoice.flowStatus === 3 ? "purple" : invoice.flowStatus === 1 ? "blue" : "gray"} />
            ) : null}
            <DetailTag
              label={t(`labels.inboundStatusValues.${inboundLabel}`)}
              tone={inboundLabel === "received" ? "green" : inboundLabel === "partial" ? "orange" : "gray"}
            />
          </View>
        </View>

        <View style={styles.amountGrid}>
          <View style={styles.amountCell}>
            <Text style={styles.metricLabel}>{t("labels.invoiceAmount")}</Text>
            <Text style={styles.amountValue} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.72}>{formatMoney(invoice.totalAmount)}</Text>
          </View>
          <View style={styles.amountCell}>
            <Text style={styles.metricLabel}>{t("labels.receivedAmount")}</Text>
            <Text style={styles.amountValue} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.72}>{formatMoney(invoice.receivedTotalAmount)}</Text>
          </View>
          <View style={styles.amountCell}>
            <Text style={styles.metricLabel}>{t("labels.detailsCount", { count: loading ? "--" : details.length })}</Text>
            <Text style={styles.countValue} numberOfLines={1}>
              {t("labels.priceChangeSummary", {
                up: loading ? invoice.priceIncreaseItemCount : priceStats.upCount,
                down: loading ? invoice.priceDecreaseItemCount : priceStats.downCount,
              })}
            </Text>
          </View>
        </View>

        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: infoExpanded }}
          onPress={() => setInfoExpanded((current) => !current)}
          style={styles.infoToggle}
        >
          <Text style={styles.context} numberOfLines={1}>
            {t("labels.orderDate")} {formatOrderDate(invoice.orderDate)} · {t("labels.inboundDate")} {formatOrderDate(invoice.inboundDate)}
          </Text>
          <Text style={styles.infoChevron}>{infoExpanded ? "⌃" : "⌄"}</Text>
        </Pressable>
        {infoExpanded ? (
          <Text style={styles.infoText}>{t("labels.remarks")} {invoice.remarks || "--"}</Text>
        ) : null}
      </View>

      {showWorkbench && !loading && details.length ? (
        <View style={styles.progressCard}>
          <View style={styles.progressTitleRow}>
            <Text style={styles.progressTitle}>{t("detail.progress.title")}</Text>
            <View style={styles.progressTitleActions}>
              <Text style={styles.context}>{t("detail.progress.executedPercent", { percent: executedPercent })}</Text>
              {canEditRows ? (
                <Button
                  compact
                  mode="contained-tonal"
                  icon="text-box-search-outline"
                  loading={checking}
                  disabled={checking}
                  onPress={requestCheck}
                >
                  {checking ? t("detail.check.running") : t("detail.check.action")}
                </Button>
              ) : null}
            </View>
          </View>
          <View style={styles.bar}>
            {DETAIL_PROGRESS_BUCKETS.map((bucket) => (
              stats[bucket] > 0 ? (
                <View key={bucket} style={{ flex: stats[bucket], backgroundColor: DETAIL_PROGRESS_BUCKET_COLORS[bucket] }} />
              ) : null
            ))}
          </View>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.bucketRow}>
            {DETAIL_PROGRESS_BUCKETS.map((bucket) => {
              const selected = filters.progressBucket === bucket;
              return (
                <Pressable
                  key={bucket}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  onPress={() => toggleBucket(bucket)}
                  style={[styles.bucket, selected ? styles.bucketSelected : null]}
                >
                  <View style={[styles.bucketDot, { backgroundColor: DETAIL_PROGRESS_BUCKET_COLORS[bucket] }]} />
                  <Text style={[styles.bucketText, selected ? styles.bucketTextSelected : null]}>
                    {t(`detail.progress.${bucket}`)} {stats[bucket]}
                  </Text>
                </Pressable>
              );
            })}
          </ScrollView>
        </View>
      ) : null}

      {!showWorkbench && !loading && details.length ? (
        <View style={styles.progressCard}>
          <View style={styles.bar}>
            {DETAIL_PROGRESS_BUCKETS.map((bucket) => (
              stats[bucket] > 0 ? (
                <View key={bucket} style={{ flex: stats[bucket], backgroundColor: DETAIL_PROGRESS_BUCKET_COLORS[bucket] }} />
              ) : null
            ))}
          </View>
          <Text style={styles.context}>{t("detail.progress.hqDone", { percent: executedPercent })}</Text>
        </View>
      ) : null}

      <TextInput
        dense
        mode="outlined"
        placeholder={t("filters.detailSearchFull")}
        value={searchInput}
        onChangeText={setSearchInput}
        left={<TextInput.Icon icon="magnify" />}
        style={styles.search}
      />
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow} keyboardShouldPersistTaps="handled">
        <FilterChip label={t("detail.quick.up")} count={counts.price.up} selected={filters.quickFilters.includes("priceUp")} onPress={() => toggleQuick("priceUp")} />
        <FilterChip label={t("detail.quick.down")} count={counts.price.down} selected={filters.quickFilters.includes("priceDown")} onPress={() => toggleQuick("priceDown")} />
        <FilterChip
          label={t(showWorkbench ? "detail.quick.notExists" : "detail.quick.notCreated")}
          count={counts.product.notExists}
          warn
          selected={filters.quickFilters.includes("productNotExists")}
          onPress={() => toggleQuick("productNotExists")}
        />
        {showWorkbench ? (
          <FilterChip label={t("detail.quick.barcodeAbnormal")} count={counts.barcodeAbnormal} warn selected={filters.quickFilters.includes("barcodeAbnormal")} onPress={() => toggleQuick("barcodeAbnormal")} />
        ) : null}
        {showWorkbench ? (
          <FilterChip
            label={t("detail.quick.more")}
            count={moreGroupCount || undefined}
            selected={moreGroupCount > 0}
            onPress={() => setMoreVisible(true)}
          />
        ) : null}
        {filtersActive ? (
          <Pressable accessibilityRole="button" onPress={clearFilters} style={styles.clearAll}>
            <Text style={styles.clearAllText}>{t("detail.quick.clearAll")}</Text>
          </Pressable>
        ) : null}
      </ScrollView>
    </View>
  );

  const summaryText = filtersActive
    ? t("detail.filteredOfTotal", { filtered: visible.length, total: details.length })
    : t("detail.totalRows", { total: details.length });

  return (
    <Portal>
      <Modal visible onDismiss={requestClose} style={styles.overlay} contentContainerStyle={styles.modal}>
        <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
          <View style={styles.navBar}>
            <IconButton accessibilityLabel={t("actions.returnToInvoices")} icon="chevron-left" onPress={requestClose} style={styles.navButton} />
            <Text style={styles.navTitle} numberOfLines={1}>{t("detailTitle")}</Text>
            <Pressable
              accessibilityRole="button"
              onPress={() => setSalesVisible(true)}
              style={styles.navAction}
            >
              <Text style={styles.navActionText}>{t("salesAnalysis.open")}</Text>
            </Pressable>
            {canEditHeader ? (
              <Pressable
                accessibilityRole="button"
                onPress={() => runAfterUnsavedGuard(() => setHeaderVisible(true))}
                style={styles.navAction}
              >
                <Text style={styles.navActionText}>{t("detail.edit.headerAction")}</Text>
              </Pressable>
            ) : null}
          </View>

          <FlatList
            ref={listRef}
            data={loading ? [] : visible}
            keyExtractor={(row) => row.detailGuid}
            renderItem={renderItem}
            extraData={snapshotIndex}
            ListHeaderComponent={header}
            ListEmptyComponent={loading ? (
              <View style={styles.loadingBox}>
                <ActivityIndicator />
                <Text>{t("detail.loadingAll")}</Text>
              </View>
            ) : (
              <EmptyState title={filtersActive ? t("detail.emptyFiltered") : t("messages.detailsEmpty")} />
            )}
            ListFooterComponent={(
              <View style={styles.returnBar}>
                <Button icon="chevron-left" mode="contained-tonal" onPress={requestClose} textColor={HB_COLORS.brand}>
                  {t("actions.returnToInvoices")}
                </Button>
              </View>
            )}
            style={styles.list}
            contentContainerStyle={styles.listContent}
            initialNumToRender={6}
            maxToRenderPerBatch={6}
            windowSize={7}
            keyboardShouldPersistTaps="handled"
            removeClippedSubviews={Platform.OS === "android"}
            onScrollToIndexFailed={({ averageItemLength, index }) => {
              listRef.current?.scrollToOffset({ animated: false, offset: Math.max(0, averageItemLength * index) });
            }}
          />

          {hasUnsaved ? (
            <View style={styles.unsavedBar}>
              <Text style={styles.unsavedText}>{t("detail.unsaved.count", { count: editedCount })}</Text>
              <View style={styles.unsavedActions}>
                <Button compact textColor={HB_COLORS.white} disabled={saving} onPress={discard}>{t("detail.unsaved.discard")}</Button>
                <Button compact mode="contained" loading={saving} disabled={saving} onPress={() => void save()}>
                  {saving ? t("detail.unsaved.saving") : t("detail.unsaved.save")}
                </Button>
              </View>
            </View>
          ) : (
            <View style={styles.summaryBar}>
              <Text style={styles.summaryText}>{summaryText}</Text>
              <Text style={styles.summaryText}>
                {t("detail.totalQty")} <Text style={styles.summaryStrong}>{formatQuantity(totals.qty)}</Text> · {t("detail.totalAmount")} <Text style={styles.summaryStrong}>{formatMoney(totals.amount)}</Text>
              </Text>
            </View>
          )}
        </SafeAreaView>

        <Snackbar visible={Boolean(toast)} onDismiss={() => setToast("")} duration={4000}>
          {toast}
        </Snackbar>
        {salesVisible ? (
          <InvoiceSalesAnalysisView invoice={invoice} onClose={() => setSalesVisible(false)} />
        ) : null}
        {canEditRows ? (
          <EditLineSheet
            detail={editingDetail}
            hasUnsavedEdit={hasUnsaved}
            onApply={applyRow}
            onChangeAction={changeAction}
            onDelete={deleteRow}
            loadMatches={loadMatches}
            onLink={linkProduct}
            onDismiss={() => setEditingGuid(null)}
            t={t}
          />
        ) : null}
        {showWorkbench ? (
          <MoreFiltersSheet
            visible={moreVisible}
            value={filters.more ?? createEmptyDetailMoreFilters()}
            counts={counts}
            countMatches={countMatches}
            onApply={(next) => { setFilters((current) => ({ ...current, more: next })); setMoreVisible(false); }}
            onDismiss={() => setMoreVisible(false)}
            t={t}
          />
        ) : null}
        {canEditHeader ? (
          <HeaderEditSheet
            visible={headerVisible}
            invoice={invoice}
            stores={stores}
            language={language}
            saving={headerSaving}
            onSave={saveHeader}
            onDismiss={() => setHeaderVisible(false)}
            t={t}
          />
        ) : null}
      </Modal>
    </Portal>
  );
}

const styles = StyleSheet.create({
  overlay: { justifyContent: "flex-start", marginBottom: 0, marginTop: 0, paddingBottom: 0, paddingTop: 0 },
  modal: { alignSelf: "stretch", backgroundColor: HB_COLORS.white, height: "100%", width: "100%" },
  screen: { flex: 1, backgroundColor: HB_COLORS.background },
  navBar: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  navButton: { margin: 0 },
  navTitle: { flex: 1, fontSize: 18, fontWeight: "700", color: HB_COLORS.textPrimary },
  navAction: { minHeight: 44, minWidth: 44, alignItems: "center", justifyContent: "center", paddingHorizontal: HB_SPACING.xs },
  navActionText: { fontSize: 14, fontWeight: "700", color: HB_COLORS.action },
  list: { flex: 1 },
  listContent: { paddingBottom: HB_SPACING.xs },
  rowWrap: { paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.xs },
  invoiceCard: {
    margin: HB_SPACING.sm,
    marginBottom: 0,
    padding: HB_SPACING.sm,
    gap: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  headingRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  storeIcon: { width: 36, height: 36, borderRadius: 18, alignItems: "center", justifyContent: "center", backgroundColor: "#EAF2FF" },
  identity: { flex: 1, gap: 2 },
  supplier: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  context: { fontSize: 12, color: HB_COLORS.textSecondary },
  statusStack: { alignItems: "flex-end", gap: 3 },
  amountGrid: { flexDirection: "row", gap: 6 },
  amountCell: { flex: 1, backgroundColor: HB_COLORS.surfaceMuted, borderRadius: HB_RADIUS.control, paddingHorizontal: 8, paddingVertical: 6, gap: 1 },
  metricLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  amountValue: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  countValue: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  infoToggle: { minHeight: 32, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted, borderStyle: "dashed", paddingTop: 4 },
  infoChevron: { fontSize: 14, color: HB_COLORS.textSecondary },
  infoText: { fontSize: 12, color: HB_COLORS.textSecondary },
  progressCard: {
    marginHorizontal: HB_SPACING.sm,
    marginTop: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    gap: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  progressTitleRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  progressTitle: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  progressTitleActions: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  bar: { height: 8, borderRadius: 4, overflow: "hidden", flexDirection: "row", backgroundColor: HB_COLORS.surfaceMuted },
  bucketRow: { flexDirection: "row", gap: 5 },
  bucket: { minHeight: 28, flexDirection: "row", alignItems: "center", gap: 4, paddingHorizontal: 8, borderRadius: 14, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, backgroundColor: HB_COLORS.white },
  bucketSelected: { borderColor: HB_COLORS.brand, backgroundColor: "#EAF2FF" },
  bucketDot: { width: 7, height: 7, borderRadius: 4 },
  bucketText: { fontSize: 11, color: HB_COLORS.textSecondary },
  bucketTextSelected: { color: "#073B83", fontWeight: "700" },
  search: { marginHorizontal: HB_SPACING.sm, marginTop: HB_SPACING.xs, backgroundColor: HB_COLORS.white },
  chipRow: { flexDirection: "row", gap: 6, paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.xs },
  clearAll: { minHeight: 32, justifyContent: "center", paddingHorizontal: 6 },
  clearAllText: { fontSize: 12, color: HB_COLORS.action, fontWeight: "700" },
  loadingBox: { alignItems: "center", gap: HB_SPACING.xs, padding: HB_SPACING.lg },
  returnBar: { padding: HB_SPACING.sm },
  summaryBar: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
    backgroundColor: HB_COLORS.surface,
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  summaryText: { fontSize: 12, color: HB_COLORS.textSecondary },
  summaryStrong: { fontWeight: "700", color: HB_COLORS.textPrimary },
  unsavedBar: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    marginHorizontal: HB_SPACING.sm,
    marginBottom: HB_SPACING.xs,
    paddingLeft: HB_SPACING.sm,
    paddingRight: 4,
    paddingVertical: 4,
    backgroundColor: HB_COLORS.textPrimary,
    borderRadius: HB_RADIUS.surface,
  },
  unsavedText: { flex: 1, fontSize: 13, color: HB_COLORS.white },
  unsavedActions: { flexDirection: "row", alignItems: "center", gap: 4 },
});
