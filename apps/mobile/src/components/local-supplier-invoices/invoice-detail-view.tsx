import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, FlatList, Platform, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, IconButton, Modal, Portal, Snackbar, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import {
  BatchActionSheet,
  BatchEditSheet,
  BatchExecuteSheet,
  type BatchExecuteSheetData,
} from "@/components/local-supplier-invoices/batch-sheets";
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
  batchExecuteInvoiceDetails,
  batchSetInvoiceDetailAction,
  batchUpdateInvoiceDetails,
  batchUpsertInvoiceDetails,
  getBatchExecuteFailure,
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
  BATCH_LINK_QUERY_CONCURRENCY,
  DETAIL_PROGRESS_BUCKETS,
  DETAIL_PROGRESS_BUCKET_COLORS,
  PRICE_CHANGE_CONFIRM_REQUIRED_CODE,
  applyDetailFilters,
  buildBatchExecuteRequest,
  buildInvoiceDetailSaveItems,
  buildInvoiceDetailSnapshotIndex,
  constrainSelectionToVisible,
  countActiveMoreFilterGroups,
  countNewProductsWithAdditionalBarcodes,
  countEditedInvoiceDetailRows,
  createEmptyDetailFilters,
  createEmptyDetailMoreFilters,
  excludeFromBatchExecuteRequest,
  findLargePriceChangeRows,
  findSkippableFailedRows,
  getDetailFilterCounts,
  getDetailProgressStats,
  getEditedInvoiceDetailFields,
  getExecutedPercent,
  getPriceChangeStats,
  groupExecuteTargets,
  hasActiveDetailFilters,
  isProductLinkConfirmed,
  mapWithConcurrency,
  pickBatchLinkCandidates,
  pickChosenGuids,
  pickExecuteTargets,
  resolveSingleMatchedProductCode,
  runCheckProducts,
  toggleFilterValue,
  type BatchEditFields,
  type BatchExecuteRequest,
  type ExecuteChoice,
  type NewProductType,
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
  // 关底部弹层（或系统确认框）后紧接着弹提示时，Paper Snackbar 的入场动画会被打断、看不到（模拟器实测）：
  // 先清空，等弹层收起再弹。
  const notifyAfterSheet = useCallback((message: string) => {
    setToast("");
    setTimeout(() => setToast(message), 350);
  }, []);
  const [checking, setChecking] = useState(false);
  const [selectionMode, setSelectionMode] = useState(false);
  const [selectedGuids, setSelectedGuids] = useState<string[]>([]);
  const [batchActionVisible, setBatchActionVisible] = useState(false);
  const [batchEditVisible, setBatchEditVisible] = useState(false);
  const [executeData, setExecuteData] = useState<BatchExecuteSheetData | null>(null);
  const [batchBusy, setBatchBusy] = useState(false);
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

  // ---- 二期 B/C：多选与批量操作（入口只在 canEditRows = Edit + 管理员时出现） ----
  const visibleGuids = useMemo(() => visible.map((row) => row.detailGuid), [visible]);
  // 筛选变化后看不到的勾选自动去掉，批量操作只作用于眼前能看到的行（与 Web 一致）。
  useEffect(() => {
    setSelectedGuids((current) => constrainSelectionToVisible(current, visibleGuids));
  }, [visibleGuids]);
  const selectedSet = useMemo(() => new Set(selectedGuids), [selectedGuids]);
  const allVisibleSelected = visibleGuids.length > 0 && visibleGuids.every((guid) => selectedSet.has(guid));
  const pendingExecutable = useMemo(
    () => pickExecuteTargets(details, { selectedGuids: [], visibleGuids: [] }),
    [details]
  );

  const enterSelection = useCallback(() => {
    if (checkingRef.current) {
      notify(t("detail.check.busy"));
      return;
    }
    // 批量操作完成后都会重读明细：先处理未保存的单行修改。
    runAfterUnsavedGuard(() => {
      setEditingGuid(null);
      setSelectedGuids([]);
      setSelectionMode(true);
    });
  }, [notify, runAfterUnsavedGuard, t]);
  const exitSelection = useCallback(() => {
    setSelectionMode(false);
    setSelectedGuids([]);
  }, []);
  const toggleSelect = useCallback((detail: LocalSupplierInvoiceItem) => {
    setSelectedGuids((current) => (current.includes(detail.detailGuid)
      ? current.filter((guid) => guid !== detail.detailGuid)
      : [...current, detail.detailGuid]));
  }, []);
  const toggleSelectAllVisible = useCallback(() => {
    setSelectedGuids(allVisibleSelected ? [] : visibleGuids);
  }, [allVisibleSelected, visibleGuids]);

  /** 批量写入后：重读明细与表头，并让列表关闭详情时静默刷新。 */
  const reloadAfterBatch = useCallback(async () => {
    onDataChanged?.();
    await loadDetails(false);
    await refreshHeader();
  }, [loadDetails, onDataChanged, refreshHeader]);

  const requireSelection = useCallback(() => {
    if (selectedGuids.length) return true;
    notify(t("detail.batch.selectFirst"));
    return false;
  }, [notify, selectedGuids.length, t]);

  // 批量设置操作类型：立即落库，同时推进快照（不算未保存修改）；勾选保留，方便接着执行。
  const batchSetAction = useCallback(async (action: number) => {
    const targets = [...selectedGuids];
    setBatchBusy(true);
    try {
      await batchSetInvoiceDetailAction(guid, targets, action);
      const chosen = new Set(targets);
      const patch = (rows: LocalSupplierInvoiceItem[]) => rows.map((row) => (
        chosen.has(row.detailGuid) ? { ...row, activityType: action } : row
      ));
      setDetails(patch);
      setSnapshot(patch);
      setBatchActionVisible(false);
      notifyAfterSheet(t("detail.batch.setActionDone", { count: targets.length, action: t(`detail.action.${action}`) }));
    } catch (error) {
      notify(getErrorMessage(error, "detail.batch.setActionFailed"));
    } finally {
      setBatchBusy(false);
    }
  }, [getErrorMessage, guid, notify, notifyAfterSheet, selectedGuids, t]);

  // 批量编辑：等后端写完再重读（Web 是先改本地再后台提交；手机端网络不稳，以服务端结果为准更稳妥）。
  const batchEdit = useCallback(async (fields: BatchEditFields) => {
    const targets = [...selectedGuids];
    setBatchBusy(true);
    try {
      await batchUpdateInvoiceDetails(guid, targets, fields);
      setBatchEditVisible(false);
      setSelectedGuids([]);
      notifyAfterSheet(t("detail.batch.editDone", { count: targets.length }));
      await reloadAfterBatch();
    } catch (error) {
      notify(getErrorMessage(error, "detail.batch.editFailed"));
    } finally {
      setBatchBusy(false);
    }
  }, [getErrorMessage, guid, notify, notifyAfterSheet, reloadAfterBatch, selectedGuids, t]);

  // 批量选用条码匹配商品：只处理条码恰好匹配到一个商品的行，回填后重新检测这些行确认关联生效。
  const runBatchLink = useCallback(async (candidates: LocalSupplierInvoiceItem[]) => {
    setBatchBusy(true);
    try {
      const lookups = await mapWithConcurrency(candidates, BATCH_LINK_QUERY_CONCURRENCY, (detail) => fetchProductsByBarcode(guid, detail.barcode));
      const items: { detailGUID: string; productCode: string }[] = [];
      let multiCount = 0;
      let queryFailed = 0;
      lookups.forEach((result, index) => {
        if (result.status === "rejected") { queryFailed += 1; return; }
        const code = resolveSingleMatchedProductCode(result.value);
        if (code) items.push({ detailGUID: candidates[index].detailGuid, productCode: code });
        else multiCount += 1;
      });
      if (!items.length) {
        notifyAfterSheet(t("detail.batch.linkNone", { multi: multiCount, failed: queryFailed }));
        return;
      }
      await batchUpsertInvoiceDetails(guid, items);
      const results = await checkInvoiceProducts(guid, items.map((item) => item.detailGUID));
      const linked = items.filter((item) => isProductLinkConfirmed(results.find((row) => row.detailGuid === item.detailGUID), item.productCode)).length;
      notifyAfterSheet(t("detail.batch.linkDone", { linked, multi: multiCount, failed: queryFailed, notLinked: items.length - linked }));
      await reloadAfterBatch();
    } catch (error) {
      notify(getErrorMessage(error, "detail.match.loadFailed"));
      await loadDetails(false);
    } finally {
      setBatchBusy(false);
    }
  }, [getErrorMessage, guid, loadDetails, notify, notifyAfterSheet, reloadAfterBatch, t]);

  const requestBatchLink = useCallback(() => {
    if (!requireSelection()) return;
    const { candidates, ineligibleCount } = pickBatchLinkCandidates(selectedGuids, details);
    if (!candidates.length) {
      notify(t("detail.batch.linkNoCandidate"));
      return;
    }
    Alert.alert(
      t("detail.batch.linkTitle"),
      t("detail.batch.linkBody", { count: candidates.length, ineligible: ineligibleCount }),
      [
        { text: t("detail.edit.cancel"), style: "cancel" },
        { text: t("detail.batch.linkConfirm"), onPress: () => { void runBatchLink(candidates); } },
      ]
    );
  }, [details, notify, requireSelection, runBatchLink, selectedGuids, t]);

  // ---- 批量执行（二期 C）：只执行新建商品 / 更新进货价，不同步 HQ、只写本单分店 ----
  const executeTargetsRef = useRef<string[]>([]);

  const openExecute = useCallback((fromSelection: boolean) => {
    if (checkingRef.current) {
      notify(t("detail.check.busy"));
      return;
    }
    if (fromSelection && !requireSelection()) return;
    runAfterUnsavedGuard(() => {
      const targets = pickExecuteTargets(details, {
        selectedGuids: fromSelection ? selectedGuids : [],
        visibleGuids,
      });
      if (!targets.guids.length) {
        notify(targets.webOnlyCount > 0
          ? t("detail.batch.execNothingWebOnly", { count: targets.webOnlyCount })
          : t("detail.batch.execNothing"));
        return;
      }
      executeTargetsRef.current = targets.guids;
      setExecuteData({
        fromSelection,
        groups: groupExecuteTargets(targets.guids, details),
        webOnlyCount: targets.webOnlyCount,
        noActionCount: targets.noActionCount,
        newWithAdditionalCount: countNewProductsWithAdditionalBarcodes(targets.guids, details),
      });
    });
  }, [details, notify, requireSelection, runAfterUnsavedGuard, selectedGuids, t, visibleGuids]);

  /** 进货价涨跌超 40% 的二次确认（#586）：列出最多 5 行，确认后带标志提交。 */
  const confirmLargePriceChange = useCallback((rows: { itemNumber: string; productName: string; lastPurchasePrice: number; purchasePrice: number; ratio: number }[], total: number, onConfirm: () => void, onCancel: () => void) => {
    const lines = rows.slice(0, 5).map((row) => t("detail.batch.largeLine", {
      item: row.itemNumber || "--",
      name: row.productName || "--",
      last: row.lastPurchasePrice.toFixed(2),
      current: row.purchasePrice.toFixed(2),
      percent: `${row.ratio > 0 ? "+" : ""}${(row.ratio * 100).toFixed(1)}%`,
    }));
    if (total > lines.length) lines.push(t("detail.batch.largeMore", { count: total - lines.length }));
    Alert.alert(
      t("detail.batch.largeTitle", { limit: 40 }),
      `${t("detail.batch.largeBody", { count: total, limit: 40 })}\n\n${lines.join("\n")}`,
      [
        { text: t("detail.edit.cancel"), style: "cancel", onPress: onCancel },
        { text: t("detail.batch.largeConfirm"), style: "destructive", onPress: onConfirm },
      ]
    );
  }, [t]);

  const submitExecute = useCallback(async (request: BatchExecuteRequest) => {
    setBatchBusy(true);
    try {
      const result = await batchExecuteInvoiceDetails(guid, request);
      setExecuteData(null);
      setSelectedGuids([]);
      notifyAfterSheet(t("detail.batch.execDone", {
        created: result.createdProducts,
        prices: result.updatedPurchasePrices,
        stores: result.updatedStorePrices,
        skipped: result.skipped,
      }));
      await reloadAfterBatch();
    } catch (error) {
      const failure = getBatchExecuteFailure(error);
      // 前端明细过期：后端发现有超限涨跌而本次没确认过，补一次确认后重试。
      if (failure?.code === PRICE_CHANGE_CONFIRM_REQUIRED_CODE && !request.confirmedLargePriceChange) {
        const rows = findLargePriceChangeRows(request.detailGuids, details);
        confirmLargePriceChange(rows, Math.max(rows.length, failure.errors.length), () => {
          void submitExecute({ ...request, confirmedLargePriceChange: true });
        }, () => undefined);
        return;
      }
      const message = failure?.message || getErrorMessage(error, "detail.batch.execFailed");
      const skippable = failure ? findSkippableFailedRows(request.detailGuids, failure.errors) : null;
      const detailLines = (failure?.errors ?? []).slice(0, 5).join("\n");
      if (skippable) {
        Alert.alert(t("detail.batch.execFailedTitle"), `${message}\n\n${detailLines}`, [
          { text: t("detail.edit.cancel"), style: "cancel" },
          {
            text: t("detail.batch.execSkipFailed", { failed: skippable.failedGuids.length, remaining: skippable.remainingCount }),
            onPress: () => { void submitExecute(excludeFromBatchExecuteRequest(request, skippable.failedGuids)); },
          },
        ]);
      } else {
        Alert.alert(t("detail.batch.execFailedTitle"), detailLines ? `${message}\n\n${detailLines}` : message);
      }
    } finally {
      setBatchBusy(false);
    }
  }, [confirmLargePriceChange, details, getErrorMessage, guid, notifyAfterSheet, reloadAfterBatch, t]);

  const confirmExecute = useCallback((choice: ExecuteChoice, newProductType: NewProductType) => {
    if (!executeData) return;
    const chosen = pickChosenGuids(executeTargetsRef.current, executeData.groups, choice);
    if (!chosen.length) {
      notify(t("detail.batch.execChooseOne"));
      return;
    }
    const build = (confirmedLargePriceChange: boolean) => buildBatchExecuteRequest(chosen, details, {
      newProductType,
      confirmedAt: new Date().toISOString(),
      confirmedLargePriceChange,
    });
    const large = findLargePriceChangeRows(chosen, details);
    if (large.length) {
      confirmLargePriceChange(large, large.length, () => { void submitExecute(build(true)); }, () => undefined);
      return;
    }
    void submitExecute(build(false));
  }, [confirmLargePriceChange, details, executeData, notify, submitExecute, t]);

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
        selectionMode={selectionMode}
        selected={selectionMode && selectedSet.has(item.detailGuid)}
        onToggleSelect={toggleSelect}
        onPress={handleRowPress}
        onCopy={onCopy}
        onOpenProduct={handleOpenProduct}
        t={t}
      />
    </View>
  ), [canEditRows, handleOpenProduct, handleRowPress, onCopy, selectedSet, selectionMode, showWorkbench, snapshotIndex, t, toggleSelect]);

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
          {canEditRows && !selectionMode ? (
            <View style={styles.batchEntryRow}>
              <Button
                compact
                mode="outlined"
                icon="checkbox-multiple-outline"
                disabled={checking || batchBusy}
                onPress={enterSelection}
                style={styles.batchEntryButton}
              >
                {t("detail.batch.enter")}
              </Button>
              <Button
                compact
                mode="contained"
                icon="play-circle-outline"
                disabled={checking || batchBusy || pendingExecutable.guids.length === 0}
                onPress={() => openExecute(false)}
                style={styles.batchEntryButton}
              >
                {t("detail.batch.execPending", { count: pendingExecutable.guids.length })}
              </Button>
            </View>
          ) : null}
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

          {selectionMode ? (
            <View style={styles.selectionBar}>
              <View style={styles.selectionTop}>
                <View style={styles.selectionCountRow}>
                  <Text style={styles.selectionCount}>{t("detail.batch.selectedCount", { count: selectedGuids.length })}</Text>
                  {batchBusy ? <ActivityIndicator size="small" color={HB_COLORS.white} /> : null}
                </View>
                <View style={styles.selectionTopActions}>
                  <Button compact textColor="#8FC2FF" disabled={!visibleGuids.length || batchBusy} onPress={toggleSelectAllVisible}>
                    {allVisibleSelected ? t("detail.batch.clearSelection") : t("detail.batch.selectAllVisible", { count: visibleGuids.length })}
                  </Button>
                  <Button compact textColor={HB_COLORS.white} disabled={batchBusy} onPress={exitSelection}>{t("detail.batch.done")}</Button>
                </View>
              </View>
              <View style={styles.selectionActions}>
                <SelectionAction icon="tag-multiple-outline" label={t("detail.batch.setAction")} disabled={batchBusy} onPress={() => { if (requireSelection()) setBatchActionVisible(true); }} />
                <SelectionAction icon="pencil-outline" label={t("detail.batch.edit")} disabled={batchBusy} onPress={() => { if (requireSelection()) setBatchEditVisible(true); }} />
                <SelectionAction icon="link-variant" label={t("detail.batch.link")} disabled={batchBusy} onPress={requestBatchLink} />
                <SelectionAction icon="play-circle-outline" label={t("detail.batch.execSelected")} primary disabled={batchBusy} onPress={() => openExecute(true)} />
              </View>
            </View>
          ) : hasUnsaved ? (
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
        {canEditRows ? (
          <>
            <BatchActionSheet
              visible={batchActionVisible}
              count={selectedGuids.length}
              busy={batchBusy}
              onPick={(action) => { void batchSetAction(action); }}
              onDismiss={() => setBatchActionVisible(false)}
              t={t}
            />
            <BatchEditSheet
              visible={batchEditVisible}
              count={selectedGuids.length}
              busy={batchBusy}
              onSubmit={(fields) => { void batchEdit(fields); }}
              onDismiss={() => setBatchEditVisible(false)}
              t={t}
            />
            <BatchExecuteSheet
              data={executeData}
              busy={batchBusy}
              onConfirm={confirmExecute}
              onDismiss={() => setExecuteData(null)}
              t={t}
            />
          </>
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

/** 多选底栏的一个操作：图标在上、文字在下，四个等分一行（375pt 宽也放得下）。 */
function SelectionAction({ icon, label, onPress, disabled, primary }: { icon: string; label: string; onPress: () => void; disabled?: boolean; primary?: boolean }) {
  const color = disabled ? "rgba(255,255,255,0.4)" : primary ? "#8FC2FF" : HB_COLORS.white;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: Boolean(disabled) }}
      disabled={disabled}
      onPress={onPress}
      style={styles.selectionAction}
    >
      <Icon source={icon} size={20} color={color} />
      <Text style={[styles.selectionActionText, { color }]} numberOfLines={1}>{label}</Text>
    </Pressable>
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
  batchEntryRow: { flexDirection: "row", gap: HB_SPACING.xs, paddingTop: 2 },
  batchEntryButton: { flex: 1 },
  selectionBar: {
    marginHorizontal: HB_SPACING.sm,
    marginBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 4,
    gap: 2,
    backgroundColor: HB_COLORS.textPrimary,
    borderRadius: HB_RADIUS.surface,
  },
  selectionTop: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingLeft: HB_SPACING.xs },
  selectionCount: { fontSize: 13, fontWeight: "700", color: HB_COLORS.white },
  selectionTopActions: { flexDirection: "row", alignItems: "center" },
  selectionActions: { flexDirection: "row", borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: "rgba(255,255,255,0.2)", paddingTop: 4 },
  selectionAction: { flex: 1, minHeight: 48, alignItems: "center", justifyContent: "center", gap: 2 },
  selectionActionText: { fontSize: 11.5, fontWeight: "600" },
  selectionCountRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  unsavedActions: { flexDirection: "row", alignItems: "center", gap: 4 },
});
