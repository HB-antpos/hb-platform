import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, AppState, Pressable, RefreshControl, ScrollView, StyleSheet, View, useWindowDimensions, type AppStateStatus } from "react-native";
import { useRouter } from "expo-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, HelperText, Snackbar, Switch, Text, TextInput } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { PaginationBar } from "@/components/ui/pagination";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import {
  alignDomesticProductCode,
  previewAlignDomesticProductCode,
  applyFloatRate,
  applyPrices,
  backfill,
  batchDeleteDetails,
  batchUpdateDetails,
  createNewProductsAndSyncHq,
  createSubmitJob,
  exportContainerDetails,
  getContainerDetail,
  getContainerDetailPresence,
  heartbeatContainerDetailPresence,
  leaveContainerDetailPresence,
  previewContainerDetailBatchAction,
  queryContainerProducts,
  recalculate,
  runPushProductsToHqJob,
  waitSubmitJob,
} from "./api";
import {
  CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
  CONTAINER_DETAIL_PAGE_SIZE_OPTIONS,
  DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMNS,
  DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMNS,
  DEFAULT_CONTAINER_DETAIL_SEARCH_FIELD,
  DEFAULT_CONTAINER_DETAIL_SORT,
  buildBatchScope,
  buildContainerDetailHqPushSelection,
  buildContainerDetailOverview,
  buildContainerDetailQuery,
  buildSubmitContainerOperationId,
  countActiveContainerDetailFilters,
  createEmptyContainerDetailFilters,
  findContainerDetailsMissingRetailPrice,
  getContainerDetailPageCount,
  getDetailDomesticProductCode,
  getDetailGuid,
  getDetailItemNumber,
  getDetailLocalProductCode,
  getDetailLocalSupplierCode,
  getDetailProductName,
  normalizeContainerDetailPageSize,
  resolveContainerDetailOverviewStats,
  toggleContainerDetailSort,
  toggleCurrentPageSelection,
  toggleSelectedTag,
} from "./query";
import {
  applyContainerDetailServerConflicts,
  buildContainerDetailEditForm,
  buildContainerDetailEditPayload,
  getContainerDetailEditableFieldValue,
  getContainerDetailServerFieldTokens,
  isCurrentContainerDetailEditSession,
  reconcileContainerDetailPartialSave,
  type ContainerDetailEditForm,
} from "./container-detail-edit-state";
import {
  peekRememberedContainerDetailPageSize,
  readRememberedContainerDetailPageSize,
  rememberContainerDetailPageSize,
} from "./container-detail-page-size-storage";
import { ContainerDetailHeader, ContainerDetailQueryBar } from "./container-detail-header";
import { ContainerDetailCreateProductsSheet, ContainerDetailSubmitSheet, type JobSheetPhase } from "./container-detail-confirm-sheets";
import { describeCreateNewProductsResult, describeSubmitContainerResult, type ResultLine } from "./container-detail-job-results";
import {
  LOCATE_HIGHLIGHT_MS,
  getPageSelectionState,
  getSelectedCreatableDetails,
  getSelectedDetails,
  locateDetailRow,
  resolveRowPressAction,
  toSelectedSet,
  toggleRowSelection,
} from "./container-detail-selection";
import {
  ContainerDetailBulkActionsSheet,
  ContainerDetailFilterSheet,
  ContainerDetailInfoBlock,
  ContainerDetailSearchFieldSheet,
  ContainerDetailSortSheet,
  ContainerDetailViewSheet,
  type ContainerDetailBulkActionKey,
} from "./container-detail-sheets";
import {
  CONTAINER_DETAIL_ENGLISH_NAME_FIELD,
  CONTAINER_DETAIL_WHOLE_ROW_FIELD,
  buildContainerHeaderInfo,
  getContainerDetailFieldLabelKey,
} from "./container-detail-table-columns";
import { ContainerDetailTable, type ContainerDetailTableHandle } from "./container-detail-table";
import type {
  AlignDomesticProductCodePreview,
  ContainerDetail,
  ContainerDetailBatchPreview,
  ContainerDetailConcurrentConflict,
  ContainerDetailFilterState,
  ContainerDetailPageSize,
  ContainerDetailPresence,
  ContainerDetailQueryTag,
  ContainerDetailSaveValidationError,
  ContainerDetailSearchField,
  ContainerDetailSort,
  ContainerDetailTagStats,
  ContainerExportFormat,
  CreateNewProductsRunResult,
  UpdateContainerDetailRequest,
} from "./types";

type BulkModalType = "float" | "prices" | null;
type BatchAction = "delete" | "float" | "prices" | "recalculate" | "backfill";

type EditForm = ContainerDetailEditForm;

const EMPTY_DETAILS: ContainerDetail[] = [];

/** 底部操作栏内容高度（不含安全区）。 */
const BOTTOM_BAR_HEIGHT = 64;
/** 表格最小高度：再小就看不到几行了。 */
const MIN_TABLE_HEIGHT = 320;
/** 页面滚动区内边距 / 间距，用于精确计算表格高度。 */
const PAGE_PADDING_TOP = HB_SPACING.xs;
const PAGE_PADDING_BOTTOM = HB_SPACING.sm;
const PAGE_GAP = HB_SPACING.sm;
/** 原生 Modal 刚关闭时立即再弹 Alert / 另一个 Modal，iOS 会丢弃，所以统一延后一点。 */
const AFTER_SHEET_CLOSE_DELAY_MS = 320;

function formatRecentActivity(value: string | undefined, justNowLabel: string) {
  if (!value) return justNowLabel;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return justNowLabel;
  return date.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", hour12: false });
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

function createClientSessionId() {
  return globalThis.crypto?.randomUUID?.() ?? `mobile-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function isExpiredBatchPreviewError(error: unknown) {
  const response = (error as { response?: { status?: number; data?: unknown } })?.response;
  const data = response?.data && typeof response.data === "object" ? response.data as Record<string, unknown> : {};
  const code = data.code ?? data.Code;
  return response?.status === 409
    || code === "BATCH_PREVIEW_STALE"
    || code === "BATCH_PREVIEW_TOKEN_INVALID"
    || code === "PREVIEW_TOKEN_EXPIRED";
}

function errorMessageOf(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback;
}

function buildForegroundTokenConflicts(
  baseline: ContainerDetail,
  latest: ContainerDetail,
  form: EditForm,
): ContainerDetailConcurrentConflict[] {
  let payload: UpdateContainerDetailRequest;
  try {
    payload = buildContainerDetailEditPayload(baseline, form);
  } catch {
    // 输入尚未有效或没有本地修改时，不制造并发冲突。
    return [];
  }
  const baselineTokens = getContainerDetailServerFieldTokens(baseline);
  const latestTokens = getContainerDetailServerFieldTokens(latest);
  const submitted = payload as unknown as Record<string, unknown>;
  return Object.keys(payload.expectedServerFieldTokens ?? {}).flatMap((field) => {
    const latestToken = latestTokens[field];
    if (!latestToken || latestToken === baselineTokens[field]) return [];
    return [{
      hguid: getDetailGuid(baseline).trim(),
      field,
      code: "CONCURRENT_FIELD_UPDATE" as const,
      // 仅用于数据结构；界面展示的是服务器值/我的值，不展示此字段
      message: "Server value changed",
      serverValue: getContainerDetailEditableFieldValue(latest, field),
      submittedValue: field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD && payload.ClearEnglishName ? "" : submitted[field],
      currentServerFieldToken: latestToken,
    }];
  });
}

export function ContainerDetailScreen({ containerGuid }: { containerGuid: string }) {
  const { t } = useAppTranslation("containerDetail");
  const listSeparator = t("common.listSeparator");
  // 前台恢复回调里要用最新的 t，但不能把 t 放进心跳 effect 的依赖，否则换语言会重建订阅并重复上报在线状态
  const tRef = useRef(t);
  tRef.current = t;
  const router = useRouter();
  const queryClient = useQueryClient();
  const insets = useSafeAreaInsets();
  const { height: windowHeight } = useWindowDimensions();
  const access = useAuthStore((state) => state.access);
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? "");

  // ---- 查询状态 ----
  const [keyword, setKeyword] = useState("");
  const [appliedKeyword, setAppliedKeyword] = useState("");
  const [searchField, setSearchField] = useState<ContainerDetailSearchField>(DEFAULT_CONTAINER_DETAIL_SEARCH_FIELD);
  const [sort, setSort] = useState<ContainerDetailSort>(DEFAULT_CONTAINER_DETAIL_SORT);
  const [filters, setFilters] = useState<ContainerDetailFilterState>(createEmptyContainerDetailFilters);
  const [selectedTags, setSelectedTags] = useState<ContainerDetailQueryTag[]>([]);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState<ContainerDetailPageSize>(
    () => peekRememberedContainerDetailPageSize() ?? CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
  );
  const [showReadonlyOemPrice, setShowReadonlyOemPrice] = useState(false);

  // ---- 选择与弹层 ----
  const [selectedHguids, setSelectedHguids] = useState<string[]>([]);
  const [highlightedHguid, setHighlightedHguid] = useState("");
  const [searchFieldSheetOpen, setSearchFieldSheetOpen] = useState(false);
  const [sortSheetOpen, setSortSheetOpen] = useState(false);
  const [filterSheetOpen, setFilterSheetOpen] = useState(false);
  const [bulkSheetOpen, setBulkSheetOpen] = useState(false);
  const [bulkModalType, setBulkModalType] = useState<BulkModalType>(null);
  const [bulkFloatRate, setBulkFloatRate] = useState("");
  const [bulkImportPrice, setBulkImportPrice] = useState("");
  const [bulkOemPrice, setBulkOemPrice] = useState("");
  const [batchPreview, setBatchPreview] = useState<(ContainerDetailBatchPreview & { action: BatchAction }) | null>(null);
  const [viewingDetail, setViewingDetail] = useState<ContainerDetail | null>(null);

  // ---- 编辑弹窗（并发保护状态，逻辑保持不变） ----
  const [editingDetail, setEditingDetail] = useState<ContainerDetail | null>(null);
  const [editForm, setEditForm] = useState<EditForm | null>(null);
  const [editEnglishNameError, setEditEnglishNameError] = useState("");
  const [editValidationErrors, setEditValidationErrors] = useState<ContainerDetailSaveValidationError[]>([]);
  const [editConflicts, setEditConflicts] = useState<ContainerDetailConcurrentConflict[]>([]);
  const [presence, setPresence] = useState<ContainerDetailPresence>({ viewers: [], editors: [] });

  // ---- 创建新商品 / 提交整柜 ----
  const [createSheetOpen, setCreateSheetOpen] = useState(false);
  const [createPhase, setCreatePhase] = useState<JobSheetPhase>("confirm");
  // 「创建完成后同时更新 HQ 数据库」每次打开确认框都默认勾选，不持久化
  const [createSyncToHq, setCreateSyncToHq] = useState(true);
  const [createResult, setCreateResult] = useState<CreateNewProductsRunResult | null>(null);
  const [createError, setCreateError] = useState("");
  const [submitSheetOpen, setSubmitSheetOpen] = useState(false);
  const [submitPhase, setSubmitPhase] = useState<JobSheetPhase>("confirm");
  const [submitResultLine, setSubmitResultLine] = useState<ResultLine | null>(null);
  const [submitError, setSubmitError] = useState("");

  const [aligningDetailHguid, setAligningDetailHguid] = useState("");
  // 对齐前读取预览期间的行，用于按钮转圈和防重复点击
  const [previewingAlignDetailHguid, setPreviewingAlignDetailHguid] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const [refreshing, setRefreshing] = useState(false);
  const [viewportHeight, setViewportHeight] = useState(0);
  const [paginationHeight, setPaginationHeight] = useState(0);

  const clientSessionIdRef = useRef(createClientSessionId());
  const editSessionIdRef = useRef("");
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);
  const pageScrollRef = useRef<ScrollView>(null);
  const tableTopRef = useRef(0);
  const tableRef = useRef<ContainerDetailTableHandle>(null);
  const highlightTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const overviewStatsCacheRef = useRef<{ containerGuid: string; stats: ContainerDetailTagStats } | null>(null);
  const lastTotalRef = useRef(0);

  const headerQuery = useQuery({
    queryKey: ["containers", "detail", containerGuid],
    queryFn: () => getContainerDetail(containerGuid),
    enabled: Boolean(containerGuid) && access.canViewContainers,
  });

  const detailQueryPayload = useMemo(
    () => buildContainerDetailQuery(containerGuid, {
      keyword: appliedKeyword,
      searchField,
      sort,
      filters,
      selectedTags,
      pageNumber: page,
      pageSize,
    }),
    [appliedKeyword, containerGuid, filters, page, pageSize, searchField, selectedTags, sort],
  );

  const productsQuery = useQuery({
    queryKey: ["containers", "detail-products", detailQueryPayload],
    queryFn: () => queryContainerProducts(containerGuid, detailQueryPayload),
    enabled: Boolean(containerGuid) && access.canViewContainers,
  });

  const details = productsQuery.data?.items ?? EMPTY_DETAILS;
  const selectedSet = useMemo(() => toSelectedSet(selectedHguids), [selectedHguids]);
  const pageSelection = useMemo(() => getPageSelectionState(details, selectedSet), [details, selectedSet]);
  const selectedDetails = useMemo(() => getSelectedDetails(details, selectedSet), [details, selectedSet]);
  const creatableDetails = useMemo(() => getSelectedCreatableDetails(details, selectedSet), [details, selectedSet]);
  const missingRetailPrice = useMemo(() => findContainerDetailsMissingRetailPrice(creatableDetails), [creatableDetails]);
  const createResultView = useMemo(() => (createResult ? describeCreateNewProductsResult(createResult) : null), [createResult]);

  if (productsQuery.data) lastTotalRef.current = productsQuery.data.itemsTotal;
  const total = productsQuery.data?.itemsTotal ?? lastTotalRef.current;
  const pageCount = getContainerDetailPageCount(total, pageSize);
  const activeFilterCount = countActiveContainerDetailFilters(filters);
  const hasQueryRestrictions = Boolean(appliedKeyword.trim()) || activeFilterCount > 0 || selectedTags.length > 0;

  // 概览卡展示整柜构成，不能随搜索/筛选变化：有搜索或筛选时沿用最近一次整柜统计
  const resolvedOverview = resolveContainerDetailOverviewStats({
    remoteStats: productsQuery.data?.tagStats,
    statsComputed: productsQuery.data?.statsComputed,
    hasScopeFilters: Boolean(appliedKeyword.trim()) || activeFilterCount > 0,
    cachedStats: overviewStatsCacheRef.current?.containerGuid === containerGuid ? overviewStatsCacheRef.current.stats : null,
  });
  if (resolvedOverview.cacheable && resolvedOverview.stats) {
    overviewStatsCacheRef.current = { containerGuid, stats: resolvedOverview.stats };
  }
  const overview = useMemo(
    () => buildContainerDetailOverview(headerQuery.data, resolvedOverview.stats),
    [headerQuery.data, resolvedOverview.stats],
  );
  const headerInfo = useMemo(() => buildContainerHeaderInfo(headerQuery.data), [headerQuery.data]);

  const canEditContainer = access.canEditContainer;
  const canDeleteContainer = access.canDeleteContainer;
  const canRunProductJobs = access.canEditContainer && access.hasPermission("PosProducts.Manage");
  const canAlignDomesticProductCode = canEditContainer && (access.isAdmin || access.hasPermission("Products.Edit"));
  const presenceState = editingDetail || editForm ? "editing" : "viewing";
  const otherViewers = presence.viewers.filter((item) => item.userGuid !== userGuid);
  const otherEditors = presence.editors.filter((item) => item.userGuid !== userGuid);
  const editingDetailRef = useRef(editingDetail);
  const editFormRef = useRef(editForm);
  const presenceStateRef = useRef<"viewing" | "editing">(presenceState);
  const detailRefetchRef = useRef(productsQuery.refetch);
  const refreshPresenceRef = useRef<(state?: "viewing" | "editing") => Promise<void>>(async () => undefined);
  const pushInFlightRef = useRef(false);
  editingDetailRef.current = editingDetail;
  editFormRef.current = editForm;
  presenceStateRef.current = presenceState;
  detailRefetchRef.current = productsQuery.refetch;

  const invalidateDetail = () => {
    void queryClient.invalidateQueries({ queryKey: ["containers", "detail"] });
    void queryClient.invalidateQueries({ queryKey: ["containers", "detail-products"] });
    void queryClient.invalidateQueries({ queryKey: ["containers", "list"] });
  };

  // 读取本机记住的每页条数（冷启动第一次进入时）；用户期间若已改过，读到的是最新值
  useEffect(() => {
    let cancelled = false;
    void readRememberedContainerDetailPageSize().then((value) => {
      if (!cancelled) setPageSize(value);
    });
    return () => { cancelled = true; };
  }, []);

  // 删除/筛选后页数变少时回到最后一页，避免停在空页
  useEffect(() => {
    if (productsQuery.data && page > pageCount) setPage(pageCount);
  }, [page, pageCount, productsQuery.data]);

  useEffect(() => () => {
    if (highlightTimerRef.current) clearTimeout(highlightTimerRef.current);
  }, []);

  useEffect(() => {
    if (!containerGuid || !access.canViewContainers) return;
    let disposed = false;
    const sessionId = clientSessionIdRef.current;
    const refreshPresence = async (state: "viewing" | "editing" = presenceStateRef.current) => {
      try {
        const next = await heartbeatContainerDetailPresence(containerGuid, {
          clientSessionId: sessionId,
          state,
        });
        if (!disposed) setPresence(next);
      } catch {
        // 在线状态只作协作提醒，任何失败都不能阻止编辑或保存。
        if (!disposed) setPresence({ viewers: [], editors: [] });
      }
    };
    refreshPresenceRef.current = refreshPresence;
    void refreshPresence();
    const interval = setInterval(() => {
      if (AppState.currentState === "active") void refreshPresence();
    }, 30_000);
    const subscription = AppState.addEventListener("change", (nextState) => {
      const previousState = appStateRef.current;
      appStateRef.current = nextState;
      if (nextState === "active" && previousState !== "active") {
        // 回到前台先取得新令牌；已打开弹窗的本地输入不能被服务器数据覆盖。
        void detailRefetchRef.current().then((result) => {
          const baseline = editingDetailRef.current;
          const form = editFormRef.current;
          const latest = baseline
            ? result.data?.items.find((item) => getDetailGuid(item).trim() === getDetailGuid(baseline).trim())
            : undefined;
          if (!baseline || !form || !latest || disposed) return;
          const conflicts = buildForegroundTokenConflicts(baseline, latest, form);
          if (!conflicts.length) return;
          setEditConflicts((current) => {
            const byField = new Map(current.map((item) => [item.field, item]));
            conflicts.forEach((item) => byField.set(item.field, item));
            return [...byField.values()];
          });
          setSnackbar(tRef.current("messages.foregroundConflicts", { count: conflicts.length }));
        }).catch(() => undefined);
        void getContainerDetailPresence(containerGuid).then((next) => {
          if (!disposed) setPresence(next);
        }).catch(() => {
          if (!disposed) setPresence({ viewers: [], editors: [] });
        });
        void refreshPresence();
      }
    });
    return () => {
      disposed = true;
      refreshPresenceRef.current = async () => undefined;
      clearInterval(interval);
      subscription.remove();
      void leaveContainerDetailPresence(containerGuid, sessionId).catch(() => undefined);
    };
  }, [access.canViewContainers, containerGuid]);

  useEffect(() => {
    if (AppState.currentState === "active") void refreshPresenceRef.current(presenceState);
  }, [presenceState]);

  function closeEditModal() {
    editSessionIdRef.current = "";
    setEditEnglishNameError("");
    setEditValidationErrors([]);
    setEditConflicts([]);
    setEditingDetail(null);
    setEditForm(null);
  }

  function openEditModal(detail: ContainerDetail) {
    editSessionIdRef.current = createClientSessionId();
    setEditEnglishNameError("");
    setEditValidationErrors([]);
    setEditConflicts([]);
    setEditingDetail(detail);
    setEditForm(buildContainerDetailEditForm(detail));
  }

  function handleEditEnglishNameChange(value: string) {
    setEditEnglishNameError("");
    setEditValidationErrors((current) => current.filter((item) => item.field !== CONTAINER_DETAIL_ENGLISH_NAME_FIELD));
    setEditForm((current) => current && { ...current, englishName: value });
  }

  /** 点击表格行：有编辑权限进入编辑弹窗（与旧版「编辑明细」按钮同一权限），否则只读查看。 */
  function handleRowPress(detail: ContainerDetail) {
    if (resolveRowPressAction(canEditContainer) === "edit") openEditModal(detail);
    else setViewingDetail(detail);
  }

  const updateMutation = useMutation({
    mutationFn: async (overrideAcknowledgements?: Record<string, string>) => {
      if (!editingDetail || !editForm) throw new Error(t("messages.nothingToSave"));
      const editSessionId = editSessionIdRef.current;
      const editingHguid = getDetailGuid(editingDetail).trim();
      const submittedPayload = buildContainerDetailEditPayload(editingDetail, editForm, overrideAcknowledgements);
      const result = await batchUpdateDetails(containerGuid, [submittedPayload]);
      return {
        baseline: editingDetail,
        form: editForm,
        submittedPayload,
        result,
        editSessionId,
        editingHguid,
      };
    },
    onSuccess: async ({ baseline, form, submittedPayload, result, editSessionId, editingHguid }) => {
      invalidateDetail();
      const validationErrors = result.validationErrors.filter((error) => error.hguid === editingHguid);
      const conflicts = result.conflicts.filter((conflict) => conflict.hguid === editingHguid);
      let latest: ContainerDetail | null = null;
      try {
        const refreshed = await detailRefetchRef.current();
        latest = refreshed.data?.items.find((item) => getDetailGuid(item).trim() === editingHguid) ?? null;
      } catch {
        // 保存结果已经成功返回；刷新失败不能丢弃仍打开的用户输入。
      }
      if (!isCurrentContainerDetailEditSession({
        expectedSessionId: editSessionId,
        expectedHguid: editingHguid,
        currentSessionId: editSessionIdRef.current,
        currentHguid: getDetailGuid(editingDetailRef.current).trim(),
      })) {
        // 迟到响应只刷新列表，绝不能覆盖已取消或后来打开的编辑会话。
        return;
      }
      const reconciled = reconcileContainerDetailPartialSave({
        baseline,
        form,
        submittedPayload,
        latest,
        validationErrors,
        conflicts,
      });
      if (reconciled.savedFields.size) {
        setEditingDetail(reconciled.detail);
        setEditForm(reconciled.form);
      }
      setEditConflicts(conflicts);
      setEditValidationErrors(validationErrors);
      const englishNameError = validationErrors.find((error) => error.field === CONTAINER_DETAIL_ENGLISH_NAME_FIELD);
      setEditEnglishNameError(englishNameError?.message ?? "");
      if (validationErrors.length || conflicts.length) {
        setSnackbar(t("messages.unsavedFields", { count: validationErrors.length + conflicts.length }));
        return;
      }
      closeEditModal();
      setSnackbar(t("messages.detailSaved"));
    },
    onError: (error) => {
      const code = error instanceof Error ? (error as Error & { code?: string }).code : undefined;
      setSnackbar(code === "CONCURRENCY_TOKEN_REQUIRED" ? t("messages.upgradeToEdit") : errorMessageOf(error, t("messages.saveFailed")));
    },
  });

  function applyServerValues(conflicts: ContainerDetailConcurrentConflict[]) {
    const resolvedFields = new Set(conflicts.map((item) => item.field));
    const currentDetail = editingDetailRef.current;
    const currentForm = editFormRef.current;
    if (currentDetail && currentForm) {
      const resolved = applyContainerDetailServerConflicts(currentDetail, currentForm, conflicts);
      setEditingDetail(resolved.detail);
      setEditForm(resolved.form);
    }
    setEditConflicts((current) => current.filter((item) => !resolvedFields.has(item.field)));
  }

  function applyServerValue(conflict: ContainerDetailConcurrentConflict) {
    applyServerValues([conflict]);
  }

  function keepMyValue(conflict: ContainerDetailConcurrentConflict) {
    // 覆盖确认只承认用户刚刚看到的版本；中间若再变更，服务器会再次返回冲突。
    updateMutation.mutate({ [conflict.field]: conflict.currentServerFieldToken });
  }

  function keepAllMyValues() {
    const acknowledgements = Object.fromEntries(
      editConflicts.map((item) => [item.field, item.currentServerFieldToken]),
    );
    Alert.alert(
      t("edit.overrideConfirmTitle"),
      t("edit.overrideConfirmMessage", { count: editConflicts.length }),
      [
        { text: t("actions.cancel"), style: "cancel" },
        {
          text: t("edit.overrideConfirmAction"),
          style: "destructive",
          onPress: () => updateMutation.mutate(acknowledgements),
        },
      ],
    );
  }

  function displayConflictValue(value: unknown) {
    if (value == null || value === "") return t("common.none");
    if (typeof value === "boolean") return t(value ? "table.tags.active" : "table.tags.inactive");
    return String(value);
  }

  function describeFieldLabel(field: string) {
    if (field === CONTAINER_DETAIL_WHOLE_ROW_FIELD) return t("edit.wholeRow");
    const key = getContainerDetailFieldLabelKey(field);
    return key ? t(`edit.fields.${key}`) : field;
  }

  const bulkMutation = useMutation({
    mutationFn: async ({
      action,
      previewToken,
    }: {
      action: BatchAction;
      previewToken: string;
    }) => {
      const scope = buildBatchScope(detailQueryPayload, selectedHguids);
      if (action === "delete") {
        if (!selectedHguids.length) throw new Error(t("messages.selectToDelete"));
        return batchDeleteDetails(containerGuid, scope, previewToken);
      }
      if (action === "float") {
        const rate = parseOptionalNumber(bulkFloatRate);
        if (rate === undefined || Number.isNaN(rate)) throw new Error(t("messages.invalidFloatRate"));
        return applyFloatRate(containerGuid, scope, rate, previewToken);
      }
      if (action === "prices") {
        const importPrice = parseOptionalNumber(bulkImportPrice);
        const oemPrice = parseOptionalNumber(bulkOemPrice);
        if (
          (importPrice === undefined && oemPrice === undefined) ||
          Number.isNaN(importPrice) ||
          Number.isNaN(oemPrice)
        ) {
          throw new Error(t("messages.invalidPrices"));
        }
        return applyPrices(containerGuid, scope, { importPrice, oemPrice }, previewToken);
      }
      if (action === "recalculate") return recalculate(containerGuid, scope, previewToken);
      return backfill(containerGuid, scope, previewToken);
    },
    onSuccess: () => {
      setBulkModalType(null);
      setBulkFloatRate("");
      setBulkImportPrice("");
      setBulkOemPrice("");
      setBatchPreview(null);
      setSelectedHguids([]);
      invalidateDetail();
      setSnackbar(t("messages.bulkDone"));
    },
    onError: (error, variables) => {
      if (isExpiredBatchPreviewError(error)) {
        // 预览令牌一旦失效不能重放：清除旧令牌，只重新获取预览，绝不自动执行。
        setBatchPreview(null);
        previewBulkMutation.mutate(variables.action, {
          onSuccess: (preview) => setSnackbar(t("messages.previewRefreshed", { count: preview.affectedCount })),
        });
        return;
      }
      setSnackbar(errorMessageOf(error, t("messages.bulkFailed")));
    },
  });

  const previewBulkMutation = useMutation({
    mutationFn: async (action: BatchAction) => {
      const scope = buildBatchScope(detailQueryPayload, selectedHguids);
      let operation: string;
      let parameters: Record<string, unknown> | undefined;
      if (action === "delete") {
        if (!selectedHguids.length) throw new Error(t("messages.selectToDelete"));
        operation = "delete-details";
      } else if (action === "float") {
        const floatRate = parseOptionalNumber(bulkFloatRate);
        if (floatRate === undefined || Number.isNaN(floatRate)) throw new Error(t("messages.invalidFloatRate"));
        operation = "apply-float-rate";
        parameters = { floatRate };
      } else if (action === "prices") {
        const importPrice = parseOptionalNumber(bulkImportPrice);
        const oemPrice = parseOptionalNumber(bulkOemPrice);
        if ((importPrice === undefined && oemPrice === undefined) || Number.isNaN(importPrice) || Number.isNaN(oemPrice)) {
          throw new Error(t("messages.invalidPrices"));
        }
        operation = "apply-prices";
        parameters = { importPrice, oemPrice };
      } else {
        operation = action === "recalculate" ? "recalculate-costs" : "backfill-last-prices";
      }
      const preview = await previewContainerDetailBatchAction(containerGuid, { operation, scope, parameters });
      return { ...preview, action };
    },
    onSuccess: (preview) => setBatchPreview(preview),
    onError: (error) => setSnackbar(errorMessageOf(error, t("messages.previewFailed"))),
  });

  const pushHqMutation = useMutation({
    mutationFn: async () => {
      if (!selectedDetails.length) throw new Error(t("messages.selectToPushHq"));
      const selection = buildContainerDetailHqPushSelection(selectedDetails);
      if (!selection.items.length) throw new Error(t("messages.pushHqNoCandidates"));
      // 不传 updateFields：默认发送 DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS 全部可更新字段
      return runPushProductsToHqJob({ containerGuid, selection });
    },
    onSuccess: (job) => {
      if (job.status === "Failed") {
        setSnackbar(t("messages.pushHqFailed", { message: job.message ?? job.errors?.[0] ?? "" }));
        return;
      }
      setSnackbar(job.message ?? t("messages.pushHqDone"));
    },
    onError: (error) => setSnackbar(errorMessageOf(error, t("messages.pushHqError"))),
  });
  pushInFlightRef.current = pushHqMutation.isPending;

  const createProductsMutation = useMutation({
    mutationFn: () => createNewProductsAndSyncHq({
      containerGuid,
      // 以确认框里展示的范围为准：已勾选且未建档的新商品
      details: creatableDetails,
      syncToHq: createSyncToHq,
      // 创建结束后重载当前页，让已建档状态进入「最新行」，再据此挑选要发送到 HQ 的商品
      reloadDetails: async () => (await detailRefetchRef.current()).data?.items ?? [],
      isPushInFlight: () => pushInFlightRef.current,
    }),
    onMutate: () => setCreatePhase("running"),
    onSuccess: (result) => {
      invalidateDetail();
      // 被前置校验拦下时没有调用任何接口，保留勾选以便补价后重试
      if (result.status === "completed") setSelectedHguids([]);
      setCreateResult(result);
      setCreatePhase("result");
    },
    onError: (error) => {
      // 创建任务抛错（网络/轮询超时）时结果未知，刷新明细让界面反映真实状态
      invalidateDetail();
      setCreateError(errorMessageOf(error, t("common.none")));
      setCreatePhase("result");
    },
  });

  const submitMutation = useMutation({
    mutationFn: async () => {
      const job = await createSubmitJob({
        containerGuid,
        operationId: buildSubmitContainerOperationId(containerGuid),
      });
      return waitSubmitJob(job.jobId);
    },
    onMutate: () => setSubmitPhase("running"),
    onSuccess: (job) => {
      invalidateDetail();
      setSubmitResultLine(describeSubmitContainerResult(job));
      setSubmitPhase("result");
    },
    onError: (error) => {
      invalidateDetail();
      setSubmitError(errorMessageOf(error, t("common.none")));
      setSubmitPhase("result");
    },
  });

  const alignDomesticProductCodeMutation = useMutation({
    mutationFn: ({ detail, merge }: { detail: ContainerDetail; merge: boolean }) => {
      const detailHguid = getDetailGuid(detail).trim();
      const localProductCode = getDetailLocalProductCode(detail);
      const domesticProductCode = getDetailDomesticProductCode(detail);
      if (!detailHguid || !localProductCode || !domesticProductCode) {
        throw new Error(t("align.missingCodes"));
      }
      return alignDomesticProductCode({
        detailHguid,
        expectedDomesticProductCode: domesticProductCode,
        targetProductCode: localProductCode,
        supplierCode: getDetailLocalSupplierCode(detail),
        mergeIntoExistingDomesticProduct: merge,
      });
    },
    onSuccess: (result) => {
      invalidateDetail();
      if (result.mode === "Merge") {
        const filled = result.filledFields.length ? t("align.filledFields", { fields: result.filledFields.join(listSeparator) }) : "";
        setSnackbar(t("align.merged", { code: result.newProductCode || "", filled }));
        return;
      }
      setSnackbar(t("align.renamed", { from: result.oldProductCode || "", to: result.newProductCode || "" }));
    },
    onError: (error) => setSnackbar(errorMessageOf(error, t("align.failed"))),
    onSettled: () => setAligningDetailHguid(""),
  });

  const exportMutation = useMutation({
    mutationFn: (format: ContainerExportFormat) =>
      exportContainerDetails(containerGuid, {
        format,
        query: detailQueryPayload,
        selectedHguids,
        columns: format === "pdf"
          ? [...DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMNS]
          : [...DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMNS],
        fileNameHint: headerInfo.containerNumber || containerGuid,
      }),
    onSuccess: (result) => setSnackbar(t("messages.exported", { name: result.fileName })),
    onError: (error) => setSnackbar(errorMessageOf(error, t("messages.exportFailed"))),
  });

  const handleAlignDomesticProductCode = (detail: ContainerDetail) => {
    const detailHguid = getDetailGuid(detail).trim();
    const localProductCode = getDetailLocalProductCode(detail);
    const domesticProductCode = getDetailDomesticProductCode(detail);
    if (alignDomesticProductCodeMutation.isPending || previewingAlignDetailHguid) {
      return;
    }
    if (!detailHguid || !localProductCode || !domesticProductCode) {
      setSnackbar(t("align.missingCodes"));
      return;
    }

    const itemNumber = getDetailItemNumber(detail) || "--";
    const productName = getDetailProductName(detail) || "--";
    const confirmAlign = (preview: AlignDomesticProductCodePreview) => {
      // 目标编码在国内商品表已存在 → 合并模式：保留已有记录、空字段用原记录补、原记录软删
      const isMerge = preview.mode === "Merge";
      const lines = isMerge
        ? [
            t("align.mergeIntro", { local: localProductCode, domestic: domesticProductCode }),
            t("align.itemNumber", { value: itemNumber }),
            t("align.product", { value: productName }),
            t("align.mergeImpact", { containers: preview.affectedContainers, rows: preview.affectedContainerDetails }),
            "",
            ...(preview.fields.length
              ? preview.fields.map((field) =>
                  t("align.mergeField", {
                    label: field.label,
                    existing: field.existingValue ?? "--",
                    old: field.oldValue ?? "--",
                    merged: field.mergedValue ?? "--",
                    filled: field.filledFromOld ? t("align.filledFromOld") : "",
                  }),
                )
              : [t("align.mergeNoDiff")]),
            "",
            t("align.mergeNote"),
          ]
        : [
            t("align.renameIntro", { local: localProductCode, domestic: domesticProductCode }),
            t("align.itemNumber", { value: itemNumber }),
            t("align.product", { value: productName }),
          ];
      Alert.alert(
        t(isMerge ? "align.mergeTitle" : "align.renameTitle"),
        lines.join("\n"),
        [
          { text: t("actions.cancel"), style: "cancel" },
          {
            text: t(isMerge ? "align.mergeAction" : "align.renameAction"),
            onPress: () => {
              setAligningDetailHguid(detailHguid);
              alignDomesticProductCodeMutation.mutate({ detail, merge: isMerge });
            },
          },
        ],
      );
    };

    // 先预览：后端判断是直接改码还是合并，并在合并前做完全部校验（不能合并时直接报原因，不弹确认框）
    setPreviewingAlignDetailHguid(detailHguid);
    void previewAlignDomesticProductCode({
      detailHguid,
      expectedDomesticProductCode: domesticProductCode,
      targetProductCode: localProductCode,
      supplierCode: getDetailLocalSupplierCode(detail),
    })
      .then(confirmAlign)
      .catch((error: unknown) => setSnackbar(errorMessageOf(error, t("align.previewFailed"))))
      .finally(() => setPreviewingAlignDetailHguid(""));
  };

  const requestBatchPreview = (action: BatchAction, title: string) => {
    previewBulkMutation.mutate(action, {
      onSuccess: (preview) => {
        if (action === "float" || action === "prices") return;
        Alert.alert(
          title,
          t("bulk.previewMessage", { count: preview.affectedCount, summary: preview.fieldSummary.join(listSeparator) }),
          [
            { text: t("actions.cancel"), style: "cancel" },
            {
              text: t("bulk.confirmExecute"),
              style: action === "delete" ? "destructive" : "default",
              onPress: () => bulkMutation.mutate({ action, previewToken: preview.previewToken }),
            },
          ],
        );
      },
    });
  };

  const confirmPushHq = () => {
    if (!selectedDetails.length) {
      setSnackbar(t("messages.selectToPushHq"));
      return;
    }
    Alert.alert(
      t("bulk.actions.pushHq"),
      t("pushHq.confirmMessage", { count: selectedDetails.length }),
      [
        { text: t("actions.cancel"), style: "cancel" },
        { text: t("pushHq.confirm"), onPress: () => pushHqMutation.mutate() },
      ],
    );
  };

  /** 原生 Modal 刚关闭就再弹 Alert / 另一个 Modal，iOS 会丢弃，统一延后。 */
  const afterSheetClosed = (action: () => void) => {
    setTimeout(action, AFTER_SHEET_CLOSE_DELAY_MS);
  };

  const handleBulkAction = (action: ContainerDetailBulkActionKey) => {
    setBulkSheetOpen(false);
    switch (action) {
      case "float":
      case "prices":
        setBatchPreview(null);
        afterSheetClosed(() => setBulkModalType(action));
        break;
      case "recalculate":
      case "backfill":
        afterSheetClosed(() => requestBatchPreview(action, t(`bulk.actions.${action}`)));
        break;
      case "delete":
        afterSheetClosed(() => requestBatchPreview("delete", t("bulk.deleteTitle")));
        break;
      case "pushHq":
        afterSheetClosed(confirmPushHq);
        break;
      case "submitContainer":
        // 提交整柜必须先经过确认框，不能从菜单直接执行
        setSubmitPhase("confirm");
        setSubmitResultLine(null);
        setSubmitError("");
        afterSheetClosed(() => setSubmitSheetOpen(true));
        break;
      case "exportExcel":
        exportMutation.mutate("excel");
        break;
      case "exportPdf":
        exportMutation.mutate("pdf");
        break;
    }
  };

  const openCreateSheet = () => {
    if (!creatableDetails.length) {
      setSnackbar(t("messages.selectNewProducts"));
      return;
    }
    // 每次打开都默认勾选「同时更新 HQ」，不记忆上一次的选择
    setCreateSyncToHq(true);
    setCreateResult(null);
    setCreateError("");
    setCreatePhase("confirm");
    setCreateSheetOpen(true);
  };

  /** 定位到缺零售价的行：关闭确认框，滚动页面到表格并滚动表格到该行，再短暂高亮。 */
  const locateMissingRow = (hguid: string) => {
    setCreateSheetOpen(false);
    const located = locateDetailRow(details, hguid);
    if (located.kind === "not-on-page") {
      // 行不在当前页（被搜索/筛选隐藏或翻页）：提示用户清除筛选
      afterSheetClosed(() => setSnackbar(t("messages.locateNotOnPage")));
      return;
    }
    afterSheetClosed(() => {
      // 滚到「分页条 + 表格」恰好占满可视区的位置，定位后仍能看到分页条
      pageScrollRef.current?.scrollTo({ y: Math.max(0, tableTopRef.current - paginationHeight - PAGE_GAP), animated: true });
      tableRef.current?.scrollToRow(located.index);
      setHighlightedHguid(hguid.trim());
      if (highlightTimerRef.current) clearTimeout(highlightTimerRef.current);
      highlightTimerRef.current = setTimeout(() => setHighlightedHguid(""), LOCATE_HIGHLIGHT_MS);
    });
  };

  // ---- 查询区交互：任何会改变结果集的操作都回到第 1 页并清空勾选，避免跨页误伤 ----
  const resetPaging = () => {
    setPage(1);
    setSelectedHguids([]);
  };

  const applySearch = () => {
    setAppliedKeyword(keyword.trim());
    resetPaging();
  };

  const clearSearch = () => {
    setKeyword("");
    setAppliedKeyword("");
    resetPaging();
  };

  const selectSearchField = (field: ContainerDetailSearchField) => {
    setSearchFieldSheetOpen(false);
    if (field === searchField) return;
    setSearchField(field);
    if (appliedKeyword) resetPaging();
  };

  const selectSort = (field: ContainerDetailSort["field"]) => {
    setSortSheetOpen(false);
    setSort((current) => toggleContainerDetailSort(current, field));
    resetPaging();
  };

  const applyFilters = (next: ContainerDetailFilterState) => {
    setFilterSheetOpen(false);
    setFilters(next);
    resetPaging();
  };

  const clearAllRestrictions = () => {
    setKeyword("");
    setAppliedKeyword("");
    setFilters(createEmptyContainerDetailFilters());
    setSelectedTags([]);
    resetPaging();
  };

  const toggleTag = (tag: ContainerDetailQueryTag) => {
    resetPaging();
    setSelectedTags((current) => toggleSelectedTag(current, tag));
  };

  const changePage = (nextPage: number) => {
    setSelectedHguids([]);
    setPage(Math.min(Math.max(1, nextPage), pageCount));
  };

  const changePageSize = (value: number) => {
    const next = normalizeContainerDetailPageSize(value);
    if (next === pageSize) return;
    rememberContainerDetailPageSize(next);
    setPageSize(next);
    // 换每页条数后原页码失去意义，回到第 1 页
    resetPaging();
  };

  const toggleRow = useCallback((hguid: string) => {
    setSelectedHguids((current) => toggleRowSelection(current, hguid));
  }, []);

  const toggleCurrentPage = () => {
    // 本页全选只作用于当前加载明细，避免跨页批量操作误伤。
    setSelectedHguids((current) => toggleCurrentPageSelection(current, details as ContainerDetail[]));
  };

  const handleRefresh = async () => {
    setRefreshing(true);
    try {
      await Promise.allSettled([headerQuery.refetch(), productsQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  if (!access.canViewContainers) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState title={t("access.deniedTitle")} description={t("access.deniedDescription")} />
      </SafeAreaView>
    );
  }

  // 表格高度：让「分页条 + 表格」恰好填满页面滚动到底时的可视区
  const effectiveViewport = viewportHeight || (windowHeight - insets.top - insets.bottom - BOTTOM_BAR_HEIGHT);
  const tableHeight = Math.max(
    MIN_TABLE_HEIGHT,
    effectiveViewport - paginationHeight - PAGE_GAP - PAGE_PADDING_BOTTOM,
  );
  const tableStatus = productsQuery.isLoading
    ? "loading"
    : productsQuery.isError && details.length === 0
      ? "error"
      : details.length === 0 ? "empty" : "ready";
  const bulkBusy = previewBulkMutation.isPending || bulkMutation.isPending || exportMutation.isPending || pushHqMutation.isPending;

  const presenceNotice = otherEditors.length || otherViewers.length ? (
    <View style={styles.presenceRow}>
      {otherEditors.length ? (
        <Text style={styles.presenceEditing}>
          {t("presence.editing", { names: otherEditors.map((item) => `${item.userName} ${formatRecentActivity(item.lastActiveAt, t("presence.justNow"))}`).join(listSeparator) })}
        </Text>
      ) : null}
      {otherViewers.length ? (
        <Text style={styles.presenceViewing}>
          {t("presence.viewing", { names: otherViewers.map((item) => `${item.userName} ${formatRecentActivity(item.lastActiveAt, t("presence.justNow"))}`).join(listSeparator) })}
        </Text>
      ) : null}
    </View>
  ) : null;

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right"]}>
      <ScrollView
        ref={pageScrollRef}
        style={styles.pageScroll}
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        onLayout={(event) => setViewportHeight(event.nativeEvent.layout.height)}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void handleRefresh()} />}
      >
        <ContainerDetailHeader
          loading={headerQuery.isLoading}
          info={headerInfo}
          fallbackTitle={containerGuid}
          overview={overview}
          presence={presenceNotice}
          onBack={() => router.back()}
        />
        <ContainerDetailQueryBar
          keyword={keyword}
          searchField={searchField}
          sort={sort}
          activeFilterCount={activeFilterCount}
          selectedTags={selectedTags}
          tagStats={productsQuery.data?.tagStats}
          total={total}
          onKeywordChange={setKeyword}
          onSubmitSearch={applySearch}
          onClearSearch={clearSearch}
          onOpenSearchField={() => setSearchFieldSheetOpen(true)}
          onOpenSort={() => setSortSheetOpen(true)}
          onOpenFilter={() => setFilterSheetOpen(true)}
          onToggleTag={toggleTag}
        />
        {/* 分页条放在表格上方；不再有底部翻页条 */}
        <View onLayout={(event) => setPaginationHeight(event.nativeEvent.layout.height)}>
          <PaginationBar
            testID="container-detail-pagination"
            page={page}
            pageCount={pageCount}
            total={total}
            pageSize={pageSize}
            pageSizeOptions={CONTAINER_DETAIL_PAGE_SIZE_OPTIONS}
            pageSizeHint={t("pageSizeHint")}
            loading={productsQuery.isLoading}
            onPageChange={changePage}
            onPageSizeChange={changePageSize}
          />
        </View>
        <View onLayout={(event) => { tableTopRef.current = event.nativeEvent.layout.y; }}>
          <ContainerDetailTable
            controllerRef={tableRef}
            details={details}
            height={tableHeight}
            status={tableStatus}
            errorMessage={productsQuery.isError ? errorMessageOf(productsQuery.error, "") : undefined}
            selectedSet={selectedSet}
            highlightedHguid={highlightedHguid}
            pageSelection={pageSelection}
            onClearFilters={hasQueryRestrictions ? clearAllRestrictions : undefined}
            onToggleRow={toggleRow}
            onToggleAll={toggleCurrentPage}
            onRowPress={handleRowPress}
            onRetry={() => void productsQuery.refetch()}
          />
        </View>
      </ScrollView>

      <View style={[styles.bottomBar, { paddingBottom: Math.max(insets.bottom, HB_SPACING.xs) }]}>
        <View style={styles.selectionInfo}>
          <Text style={styles.selectionMain} numberOfLines={1}>
            {t("selection.summary", { selected: selectedHguids.length, page: pageSelection.pageCount })}
          </Text>
          {selectedHguids.length ? (
            <Pressable accessibilityRole="button" hitSlop={{ top: 12, bottom: 12, right: 24 }} onPress={() => setSelectedHguids([])}>
              <Text style={styles.selectionClear}>{t("selection.clear")}</Text>
            </Pressable>
          ) : null}
        </View>
        <Button
          mode="outlined"
          icon="menu-down"
          contentStyle={[BUSINESS_UI.buttonContent, styles.bulkButtonContent]}
          style={BUSINESS_UI.button}
          loading={bulkBusy}
          onPress={() => setBulkSheetOpen(true)}
        >
          {t("bulk.button")}
        </Button>
        {canRunProductJobs ? (
          <Button
            mode="contained"
            icon="plus-box"
            contentStyle={BUSINESS_UI.buttonContent}
            style={BUSINESS_UI.button}
            disabled={createProductsMutation.isPending}
            onPress={openCreateSheet}
          >
            {t("createProducts.button")}
          </Button>
        ) : null}
      </View>

      <ContainerDetailSearchFieldSheet
        visible={searchFieldSheetOpen}
        value={searchField}
        onSelect={selectSearchField}
        onDismiss={() => setSearchFieldSheetOpen(false)}
      />
      <ContainerDetailSortSheet
        visible={sortSheetOpen}
        sort={sort}
        onSelect={selectSort}
        onDismiss={() => setSortSheetOpen(false)}
      />
      <ContainerDetailFilterSheet
        visible={filterSheetOpen}
        applied={filters}
        showReadonlyOemPrice={showReadonlyOemPrice}
        onShowReadonlyOemPriceChange={setShowReadonlyOemPrice}
        onApply={applyFilters}
        onDismiss={() => setFilterSheetOpen(false)}
      />
      <ContainerDetailBulkActionsSheet
        visible={bulkSheetOpen}
        selectedCount={selectedHguids.length}
        filteredTotal={total}
        canEditContainer={canEditContainer}
        canDeleteContainer={canDeleteContainer}
        canRunProductJobs={canRunProductJobs}
        busy={bulkBusy}
        onAction={handleBulkAction}
        onDismiss={() => setBulkSheetOpen(false)}
      />
      <ContainerDetailViewSheet
        detail={viewingDetail}
        showReadonlyOemPrice={showReadonlyOemPrice}
        onDismiss={() => setViewingDetail(null)}
      />

      <BusinessSheet visible={Boolean(editingDetail && editForm)}
        title={t("edit.title")}
        dismissable={!updateMutation.isPending}
        onDismiss={updateMutation.isPending ? () => undefined : closeEditModal}
        footer={(
          <View style={styles.footerRow}>
            <Button mode="outlined" style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent} disabled={updateMutation.isPending} onPress={closeEditModal}>{t("actions.cancel")}</Button>
            <Button mode="contained" style={[BUSINESS_UI.button, styles.footerButtonWide]} contentStyle={BUSINESS_UI.buttonContent} disabled={updateMutation.isPending} loading={updateMutation.isPending} onPress={() => updateMutation.mutate(undefined)}>{t("actions.save")}</Button>
          </View>
        )}
      >
        {editingDetail ? (
          <ContainerDetailInfoBlock
            detail={editingDetail}
            showReadonlyOemPrice={showReadonlyOemPrice}
            canAlignDomesticProductCode={canAlignDomesticProductCode}
            aligning={
              previewingAlignDetailHguid === getDetailGuid(editingDetail).trim()
              || (aligningDetailHguid === getDetailGuid(editingDetail).trim() && alignDomesticProductCodeMutation.isPending)
            }
            alignDisabled={alignDomesticProductCodeMutation.isPending || Boolean(previewingAlignDetailHguid) || updateMutation.isPending}
            onAlign={() => handleAlignDomesticProductCode(editingDetail)}
          />
        ) : null}
        {editForm ? (
          <>
            <TextInput mode="outlined" label={t("edit.productName")} disabled={updateMutation.isPending} value={editForm.productName} onChangeText={(value) => setEditForm((current) => current && { ...current, productName: value })} />
            <TextInput
              mode="outlined"
              label={t("edit.englishName")}
              value={editForm.englishName}
              error={Boolean(editEnglishNameError)}
              disabled={updateMutation.isPending}
              onChangeText={handleEditEnglishNameChange}
            />
            <HelperText type="error" visible={Boolean(editEnglishNameError)}>
              {editEnglishNameError}
            </HelperText>
            {editValidationErrors.length ? (
              <View style={styles.validationPanel}>
                <Text style={styles.validationTitle}>{t("edit.validationTitle")}</Text>
                {editValidationErrors.map((error) => (
                  <Text key={`${error.field}-${error.code}`} style={styles.muted}>
                    {t("edit.validationLine", { field: describeFieldLabel(error.field), message: error.message })}
                  </Text>
                ))}
              </View>
            ) : null}
            {editConflicts.length ? (
              <View style={styles.conflictPanel}>
                <Text style={styles.conflictTitle}>{t("edit.conflictTitle", { count: editConflicts.length })}</Text>
                {editConflicts.length > 1 ? (
                  <View style={styles.actionRow}>
                    <Button compact disabled={updateMutation.isPending} onPress={() => applyServerValues(editConflicts)}>{t("edit.useAllServer")}</Button>
                    <Button compact mode="contained-tonal" disabled={updateMutation.isPending} onPress={keepAllMyValues}>{t("edit.keepAllMine")}</Button>
                  </View>
                ) : null}
                {editConflicts.map((conflict) => (
                  <View key={conflict.field} style={styles.conflictItem}>
                    <Text style={styles.conflictField}>{describeFieldLabel(conflict.field)}</Text>
                    <Text style={styles.muted}>{t("edit.serverValue")}{displayConflictValue(conflict.serverValue)}</Text>
                    <Text style={styles.muted}>{t("edit.myValue")}{displayConflictValue(conflict.submittedValue)}</Text>
                    <View style={styles.actionRow}>
                      <Button compact disabled={updateMutation.isPending} onPress={() => applyServerValue(conflict)}>{t("edit.useServer")}</Button>
                      <Button compact mode="contained-tonal" disabled={updateMutation.isPending} onPress={() => keepMyValue(conflict)}>{t("edit.keepMine")}</Button>
                    </View>
                  </View>
                ))}
              </View>
            ) : null}
            <View style={styles.inputRow}>
              <TextInput mode="outlined" label={t("edit.domesticPrice")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.domesticPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, domesticPrice: value })} style={styles.inputHalf} />
              <TextInput mode="outlined" label={t("edit.importPrice")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.importPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, importPrice: value })} style={styles.inputHalf} />
            </View>
            <View style={styles.inputRow}>
              <TextInput mode="outlined" label={t("edit.oemPrice")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.oemPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, oemPrice: value })} style={styles.inputHalf} />
              <TextInput mode="outlined" label={t("edit.floatRate")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.floatRate} onChangeText={(value) => setEditForm((current) => current && { ...current, floatRate: value })} style={styles.inputHalf} />
            </View>
            <View style={styles.inputRow}>
              <TextInput mode="outlined" label={t("edit.containerQuantity")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.containerQuantity} onChangeText={(value) => setEditForm((current) => current && { ...current, containerQuantity: value })} style={styles.inputHalf} />
              <TextInput mode="outlined" label={t("edit.middlePackQuantity")} disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.middlePackQuantity} onChangeText={(value) => setEditForm((current) => current && { ...current, middlePackQuantity: value })} style={styles.inputHalf} />
            </View>
            <View style={styles.switchRow}>
              <Text style={styles.switchLabel}>{t("edit.active")}</Text>
              <Switch value={editForm.isActive} disabled={updateMutation.isPending} onValueChange={(value) => setEditForm((current) => current && { ...current, isActive: value })} />
            </View>
          </>
        ) : null}
      </BusinessSheet>

      <BusinessSheet visible={Boolean(bulkModalType)}
        title={t(bulkModalType === "float" ? "bulk.actions.float" : "bulk.actions.prices")}
        onDismiss={() => setBulkModalType(null)}
        footer={(
          <View style={styles.footerRow}>
            <Button mode="outlined" style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent} onPress={() => setBulkModalType(null)}>{t("actions.cancel")}</Button>
            <Button
              mode="contained"
              style={[BUSINESS_UI.button, styles.footerButtonWide]}
              contentStyle={BUSINESS_UI.buttonContent}
              loading={bulkMutation.isPending || previewBulkMutation.isPending}
              disabled={bulkMutation.isPending || previewBulkMutation.isPending}
              onPress={() => {
                if (!bulkModalType) return;
                const action = bulkModalType === "float" ? "float" : "prices";
                if (batchPreview?.action === action) {
                  bulkMutation.mutate({ action, previewToken: batchPreview.previewToken });
                  return;
                }
                requestBatchPreview(action, t(`bulk.actions.${action}`));
              }}
            >
              {t(batchPreview?.action === bulkModalType ? "bulk.confirmExecute" : "bulk.preview")}
            </Button>
          </View>
        )}
      >
        {bulkModalType === "float" ? (
          <TextInput mode="outlined" label={t("edit.floatRate")} keyboardType="decimal-pad" value={bulkFloatRate} onChangeText={(value) => { setBatchPreview(null); setBulkFloatRate(value); }} />
        ) : (
          <>
            <TextInput mode="outlined" label={t("edit.importPrice")} keyboardType="decimal-pad" value={bulkImportPrice} onChangeText={(value) => { setBatchPreview(null); setBulkImportPrice(value); }} />
            <TextInput mode="outlined" label={t("edit.oemPrice")} keyboardType="decimal-pad" value={bulkOemPrice} onChangeText={(value) => { setBatchPreview(null); setBulkOemPrice(value); }} />
          </>
        )}
        <Text style={styles.muted}>{selectedHguids.length ? t("bulk.scopeSelected", { count: selectedHguids.length }) : t("bulk.scopeFiltered", { count: total })}</Text>
        {batchPreview && batchPreview.action === bulkModalType ? (
          <View style={styles.previewPanel}>
            <Text style={styles.previewTitle}>{t("bulk.previewAffected", { count: batchPreview.affectedCount })}</Text>
            {batchPreview.fieldSummary.length ? <Text style={styles.muted}>{batchPreview.fieldSummary.join(listSeparator)}</Text> : null}
          </View>
        ) : null}
      </BusinessSheet>

      <ContainerDetailCreateProductsSheet
        visible={createSheetOpen}
        phase={createPhase}
        count={creatableDetails.length}
        missingRetailPrice={missingRetailPrice}
        syncToHq={createSyncToHq}
        onSyncToHqChange={setCreateSyncToHq}
        onLocate={locateMissingRow}
        onConfirm={() => createProductsMutation.mutate()}
        onDismiss={() => setCreateSheetOpen(false)}
        resultView={createResultView}
        errorMessage={createError}
      />
      <ContainerDetailSubmitSheet
        visible={submitSheetOpen}
        phase={submitPhase}
        containerNumber={headerInfo.containerNumber}
        rowCount={overview.rowCount}
        onConfirm={() => submitMutation.mutate()}
        onDismiss={() => setSubmitSheetOpen(false)}
        resultLine={submitResultLine}
        errorMessage={submitError}
      />

      <Snackbar
        visible={Boolean(snackbar)}
        onDismiss={() => setSnackbar("")}
        wrapperStyle={{ bottom: BOTTOM_BAR_HEIGHT + insets.bottom }}
      >
        {snackbar}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: HB_COLORS.background,
  },
  pageScroll: { flex: 1 },
  content: {
    gap: PAGE_GAP,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: PAGE_PADDING_TOP,
    paddingBottom: PAGE_PADDING_BOTTOM,
  },
  bottomBar: {
    minHeight: BOTTOM_BAR_HEIGHT,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
  },
  selectionInfo: { flex: 1, minWidth: 0, gap: 2 },
  selectionMain: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "700", fontVariant: ["tabular-nums"] },
  selectionClear: { color: HB_COLORS.action, fontSize: 12, fontWeight: "600" },
  bulkButtonContent: { flexDirection: "row-reverse" },
  presenceRow: { gap: 4 },
  presenceEditing: { color: HB_COLORS.warning, fontSize: 12 },
  presenceViewing: { color: HB_COLORS.textSecondary, fontSize: 12 },
  muted: { color: HB_COLORS.textSecondary },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerButtonWide: { flex: 2 },
  actionRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
    alignItems: "center",
  },
  conflictPanel: {
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.danger,
  },
  conflictTitle: { color: HB_COLORS.danger, fontSize: 14, fontWeight: "700" },
  conflictField: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  conflictItem: {
    gap: 4,
    paddingTop: 6,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
  },
  validationPanel: {
    gap: 4,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.warning,
  },
  validationTitle: { color: HB_COLORS.warning, fontSize: 14, fontWeight: "700" },
  previewPanel: {
    gap: 4,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  previewTitle: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  inputRow: {
    flexDirection: "row",
    gap: HB_SPACING.sm,
  },
  inputHalf: {
    flex: 1,
  },
  switchRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    minHeight: 44,
  },
  switchLabel: { color: HB_COLORS.textPrimary, fontSize: 15 },
});
