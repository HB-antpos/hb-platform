import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, AppState, Image, RefreshControl, ScrollView, StyleSheet, View, type AppStateStatus } from "react-native";
import { useRouter } from "expo-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Card,
  Checkbox,
  Chip,
  Divider,
  HelperText,
  Menu,
  Modal,
  Portal,
  Snackbar,
  Surface,
  Switch,
  Text,
  TextInput,
  useTheme,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAuthStore } from "@/store/auth-store";
import {
  alignDomesticProductCode,
  applyFloatRate,
  applyPrices,
  backfill,
  batchDeleteDetails,
  batchUpdateDetails,
  createProductCreationJob,
  createPushProductsToHqJob,
  createSubmitJob,
  exportContainerDetails,
  getContainerDetail,
  getContainerDetailPresence,
  heartbeatContainerDetailPresence,
  leaveContainerDetailPresence,
  previewContainerDetailBatchAction,
  queryContainerProducts,
  recalculate,
  wait,
  waitPushProductsToHqJob,
  waitSubmitJob,
} from "./api";
import {
  DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMNS,
  DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMNS,
  buildBatchScope,
  buildContainerDetailHqPushSelection,
  buildContainerDetailQuery,
  buildCreateProductsOperationId,
  buildPushProductsToHqOperationId,
  buildSubmitContainerOperationId,
  getCurrentPageDetailGuids,
  getDetailBarcode,
  getDetailEnglishName,
  getDetailGuid,
  getDetailImageUrl,
  getDetailItemNumber,
  getDetailDomesticProductCode,
  getDetailLocalProductCode,
  getDetailLocalSupplierCode,
  getDetailMatchType,
  getDetailProductName,
  getDetailReadonlyOemPrice,
  getDetailRealtimeImportPrice,
  getDetailRealtimeRetailPrice,
  getDetailVisibleOemPrice,
  hasDetailProductCodeConflict,
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
import type {
  ContainerDetail,
  ContainerDetailBatchPreview,
  ContainerDetailConcurrentConflict,
  ContainerDetailPresence,
  ContainerDetailQuery,
  ContainerDetailQueryTag,
  ContainerDetailSaveValidationError,
  ContainerExportFormat,
  UpdateContainerDetailRequest,
} from "./types";

const TAGS: { value: ContainerDetailQueryTag; label: string }[] = [
  { value: "all", label: "全部" },
  { value: "new", label: "新商品" },
  { value: "existing", label: "已有" },
  { value: "noOemPrice", label: "缺零售价" },
  { value: "abnormalImport", label: "进口异常" },
  { value: "active", label: "启用" },
  { value: "inactive", label: "停用" },
];

type BulkModalType = "float" | "prices" | null;

type EditForm = ContainerDetailEditForm;

interface DetailRangeFilterForm {
  containerQuantityMin: string;
  containerQuantityMax: string;
  middlePackQuantityMin: string;
  middlePackQuantityMax: string;
  warehouseImportPriceMin: string;
  warehouseImportPriceMax: string;
  oemPriceMin: string;
  oemPriceMax: string;
}

const EMPTY_DETAIL_RANGE_FILTERS: DetailRangeFilterForm = {
  containerQuantityMin: "",
  containerQuantityMax: "",
  middlePackQuantityMin: "",
  middlePackQuantityMax: "",
  warehouseImportPriceMin: "",
  warehouseImportPriceMax: "",
  oemPriceMin: "",
  oemPriceMax: "",
};

const DETAIL_RANGE_FILTER_KEYS = Object.keys(EMPTY_DETAIL_RANGE_FILTERS) as (keyof DetailRangeFilterForm)[];

function formatDate(value?: string) {
  return value ? value.slice(0, 10) : "--";
}

function formatRecentActivity(value?: string) {
  if (!value) return "刚刚";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "刚刚";
  return date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false });
}

function formatNumber(value?: number | null, digits = 2) {
  return value == null || !Number.isFinite(value) ? "--" : value.toFixed(digits);
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

function getMatchTypeLabel(value: ReturnType<typeof getDetailMatchType>) {
  if (value === "productCode") return "商品编码匹配";
  if (value === "supplierItem") return "候选需确认";
  return "未匹配";
}

function hasRangeFilters(filters: DetailRangeFilterForm) {
  return DETAIL_RANGE_FILTER_KEYS.some((key) => Boolean(filters[key].trim()));
}

function hasInvalidRangeFilters(filters: DetailRangeFilterForm) {
  return DETAIL_RANGE_FILTER_KEYS.some((key) => Number.isNaN(parseOptionalNumber(filters[key])));
}

function buildRangeQuery(filters: DetailRangeFilterForm): Partial<ContainerDetailQuery> {
  return {
    containerQuantityMin: parseOptionalNumber(filters.containerQuantityMin),
    containerQuantityMax: parseOptionalNumber(filters.containerQuantityMax),
    middlePackQuantityMin: parseOptionalNumber(filters.middlePackQuantityMin),
    middlePackQuantityMax: parseOptionalNumber(filters.middlePackQuantityMax),
    warehouseImportPriceMin: parseOptionalNumber(filters.warehouseImportPriceMin),
    warehouseImportPriceMax: parseOptionalNumber(filters.warehouseImportPriceMax),
    oemPriceMin: parseOptionalNumber(filters.oemPriceMin),
    oemPriceMax: parseOptionalNumber(filters.oemPriceMax),
  };
}

function createClientSessionId() {
  return globalThis.crypto?.randomUUID?.() ?? `mobile-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function displayConflictValue(value: unknown) {
  if (value == null || value === "") return "--";
  if (typeof value === "boolean") return value ? "启用" : "停用";
  return String(value);
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
      message: "服务器已更新",
      serverValue: getContainerDetailEditableFieldValue(latest, field),
      submittedValue: field === "英文名称" && payload.ClearEnglishName ? "" : submitted[field],
      currentServerFieldToken: latestToken,
    }];
  });
}

function DetailMetric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.metric}>
      <Text variant="labelSmall" style={styles.muted}>{label}</Text>
      <Text variant="bodyMedium">{value}</Text>
    </View>
  );
}

function DetailCard({
  detail,
  selected,
  canEditContainer,
  canAlignDomesticProductCode,
  showReadonlyOemPrice,
  aligning,
  alignDisabled,
  onEdit,
  onAlign,
  onToggle,
}: {
  detail: ContainerDetail;
  selected: boolean;
  canEditContainer: boolean;
  canAlignDomesticProductCode: boolean;
  showReadonlyOemPrice: boolean;
  aligning: boolean;
  alignDisabled: boolean;
  onEdit: () => void;
  onAlign: () => void;
  onToggle: () => void;
}) {
  const theme = useTheme();
  const imageUrl = getDetailImageUrl(detail);
  const [imageFailed, setImageFailed] = useState(false);
  // 按本柜新品展示：本柜建档后 是否新商品 变 false，仍需标为新商品
  const isContainerNewProduct = Boolean(detail.isContainerNewProduct ?? detail.是否新商品);
  const showImage = Boolean(imageUrl && !imageFailed);
  const localProductCode = getDetailLocalProductCode(detail);
  const domesticProductCode = getDetailDomesticProductCode(detail);
  const hasConflict = hasDetailProductCodeConflict(detail);
  const matchType = getDetailMatchType(detail);
  const isSetChild = detail.商品类型 === "套装子商品" || detail.商品信息?.商品类型 === "套装子商品";
  const canAlign = canAlignDomesticProductCode && hasConflict && Boolean(localProductCode && domesticProductCode) && !isSetChild;

  return (
    <Card style={styles.card} mode="outlined">
      <Card.Title
        title={getDetailItemNumber(detail) || detail.商品编码 || "未命名商品"}
        subtitle={getDetailProductName(detail) || getDetailEnglishName(detail) || "--"}
        titleNumberOfLines={1}
        subtitleNumberOfLines={1}
        left={() => (
          <Checkbox.Android
            status={selected ? "checked" : "unchecked"}
            onPress={onToggle}
          />
        )}
        right={() => (
          <View style={styles.detailImageFrame}>
            {showImage ? (
              <Image
                source={{ uri: imageUrl! }}
                style={styles.detailImage}
                resizeMode="contain"
                onError={() => setImageFailed(true)}
              />
            ) : (
              <View style={styles.detailImagePlaceholder}>
                <Text variant="labelSmall" style={styles.imagePlaceholderText}>图片</Text>
              </View>
            )}
          </View>
        )}
      />
      <Card.Content>
        <View style={styles.chipRow}>
          {isContainerNewProduct ? (
            // 新商品用品牌浅蓝底色，与其它默认标签和 Web 蓝色「新」标签区分；已有商品保持默认样式。
            <Chip
              compact
              style={{ backgroundColor: theme.colors.primaryContainer }}
              textStyle={{ color: theme.colors.onPrimaryContainer }}
            >
              新商品
            </Chip>
          ) : (
            <Chip compact>已有商品</Chip>
          )}
          <Chip compact>{detail.warehouseIsActive === false ? "停用" : "启用"}</Chip>
          {detail.matchType || detail.MatchType || hasConflict ? <Chip compact>{getMatchTypeLabel(matchType)}</Chip> : null}
        </View>
        <View style={styles.metricGrid}>
          <DetailMetric label="条码" value={getDetailBarcode(detail) || "--"} />
          <DetailMetric label="数量" value={formatNumber(detail.装柜数量, 0)} />
          <DetailMetric label="中包" value={formatNumber(detail.中包数, 0)} />
          <DetailMetric label="国内价" value={formatNumber(detail.国内价格)} />
          <DetailMetric label="实时进货价" value={formatNumber(getDetailRealtimeImportPrice(detail))} />
          <DetailMetric label="进口价" value={formatNumber(detail.进口价格)} />
          <DetailMetric label="零售价" value={formatNumber(getDetailVisibleOemPrice(detail))} />
          <DetailMetric label="实时零售价" value={formatNumber(getDetailRealtimeRetailPrice(detail))} />
          {showReadonlyOemPrice ? <DetailMetric label="只读零售价" value={formatNumber(getDetailReadonlyOemPrice(detail))} /> : null}
        </View>
        {hasConflict ? (
          <Text style={styles.warningText}>
            候选：本地主档编码 {localProductCode || "--"}，国内编码 {domesticProductCode || "--"}
          </Text>
        ) : null}
        {detail.备注 ? <Text style={styles.remark}>{detail.备注}</Text> : null}
      </Card.Content>
      {canEditContainer || canAlign ? (
        <Card.Actions>
          {canEditContainer ? <Button icon="pencil" onPress={onEdit}>编辑明细</Button> : null}
          {canAlign ? (
            <Button icon="link-variant" loading={aligning} disabled={alignDisabled} onPress={onAlign}>
              对齐编码
            </Button>
          ) : null}
        </Card.Actions>
      ) : null}
    </Card>
  );
}

export function ContainerDetailScreen({ containerGuid }: { containerGuid: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const access = useAuthStore((state) => state.access);
  const userGuid = useAuthStore((state) => state.user?.userGUID ?? "");
  const [keyword, setKeyword] = useState("");
  const [appliedKeyword, setAppliedKeyword] = useState("");
  const [selectedTags, setSelectedTags] = useState<ContainerDetailQueryTag[]>([]);
  const [page, setPage] = useState(1);
  const [selectedHguids, setSelectedHguids] = useState<string[]>([]);
  const [bulkMenuVisible, setBulkMenuVisible] = useState(false);
  const [bulkModalType, setBulkModalType] = useState<BulkModalType>(null);
  const [bulkFloatRate, setBulkFloatRate] = useState("");
  const [bulkImportPrice, setBulkImportPrice] = useState("");
  const [bulkOemPrice, setBulkOemPrice] = useState("");
  const [editingDetail, setEditingDetail] = useState<ContainerDetail | null>(null);
  const [editForm, setEditForm] = useState<EditForm | null>(null);
  const [editEnglishNameError, setEditEnglishNameError] = useState("");
  const [editValidationErrors, setEditValidationErrors] = useState<ContainerDetailSaveValidationError[]>([]);
  const [editConflicts, setEditConflicts] = useState<ContainerDetailConcurrentConflict[]>([]);
  const [presence, setPresence] = useState<ContainerDetailPresence>({ viewers: [], editors: [] });
  const [batchPreview, setBatchPreview] = useState<(ContainerDetailBatchPreview & {
    action: "delete" | "float" | "prices" | "recalculate" | "backfill";
  }) | null>(null);
  const [showReadonlyOemPrice, setShowReadonlyOemPrice] = useState(false);
  const [showRangeFilters, setShowRangeFilters] = useState(false);
  const [rangeFilters, setRangeFilters] = useState<DetailRangeFilterForm>(EMPTY_DETAIL_RANGE_FILTERS);
  const [appliedRangeFilters, setAppliedRangeFilters] = useState<DetailRangeFilterForm>(EMPTY_DETAIL_RANGE_FILTERS);
  const [aligningDetailHguid, setAligningDetailHguid] = useState("");
  const [snackbar, setSnackbar] = useState("");
  const clientSessionIdRef = useRef(createClientSessionId());
  const editSessionIdRef = useRef("");
  const appStateRef = useRef<AppStateStatus>(AppState.currentState);

  const headerQuery = useQuery({
    queryKey: ["containers", "detail", containerGuid],
    queryFn: () => getContainerDetail(containerGuid),
    enabled: Boolean(containerGuid) && access.canViewContainers,
  });

  const detailQueryPayload = useMemo(
    () => buildContainerDetailQuery(containerGuid, {
      keyword: appliedKeyword,
      selectedTags,
      pageNumber: page,
      ...buildRangeQuery(appliedRangeFilters),
    }),
    [appliedKeyword, appliedRangeFilters, containerGuid, page, selectedTags],
  );

  const productsQuery = useQuery({
    queryKey: ["containers", "detail-products", detailQueryPayload],
    queryFn: () => queryContainerProducts(containerGuid, detailQueryPayload),
    enabled: Boolean(containerGuid) && access.canViewContainers,
  });

  const details = useMemo(() => productsQuery.data?.items ?? [], [productsQuery.data?.items]);
  const currentPageDetailGuids = useMemo(() => getCurrentPageDetailGuids(details), [details]);
  const selectedSet = useMemo(
    () => new Set(selectedHguids.map((item) => item.trim()).filter(Boolean)),
    [selectedHguids],
  );
  const currentPageSelectedCount = currentPageDetailGuids.filter((hguid) => selectedSet.has(hguid)).length;
  const isCurrentPageFullySelected = currentPageDetailGuids.length > 0
    && currentPageSelectedCount === currentPageDetailGuids.length;
  const selectedDetails = details.filter((detail) => selectedSet.has(getDetailGuid(detail).trim()));
  const totalPages = Math.max(1, Math.ceil((productsQuery.data?.itemsTotal ?? 0) / detailQueryPayload.pageSize));
  const canEditContainer = access.canEditContainer;
  const canDeleteContainer = access.canDeleteContainer;
  const canRunProductJobs = access.canEditContainer && access.hasPermission("PosProducts.Manage");
  const canAlignDomesticProductCode = canEditContainer && (access.isAdmin || access.hasPermission("Products.Edit"));
  const rangeFilterActive = hasRangeFilters(appliedRangeFilters);
  const presenceState = editingDetail || editForm ? "editing" : "viewing";
  const otherViewers = presence.viewers.filter((item) => item.userGuid !== userGuid);
  const otherEditors = presence.editors.filter((item) => item.userGuid !== userGuid);
  const editingDetailRef = useRef(editingDetail);
  const editFormRef = useRef(editForm);
  const presenceStateRef = useRef<"viewing" | "editing">(presenceState);
  const detailRefetchRef = useRef(productsQuery.refetch);
  const refreshPresenceRef = useRef<(state?: "viewing" | "editing") => Promise<void>>(async () => undefined);
  editingDetailRef.current = editingDetail;
  editFormRef.current = editForm;
  presenceStateRef.current = presenceState;
  detailRefetchRef.current = productsQuery.refetch;

  const invalidateDetail = () => {
    void queryClient.invalidateQueries({ queryKey: ["containers", "detail"] });
    void queryClient.invalidateQueries({ queryKey: ["containers", "detail-products"] });
    void queryClient.invalidateQueries({ queryKey: ["containers", "list"] });
  };

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
          setSnackbar(`服务器已更新 ${conflicts.length} 个正在编辑的字段`);
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
    setEditValidationErrors((current) => current.filter((item) => item.field !== "英文名称"));
    setEditForm((current) => current && { ...current, englishName: value });
  }

  const updateMutation = useMutation({
    mutationFn: async (overrideAcknowledgements?: Record<string, string>) => {
      if (!editingDetail || !editForm) throw new Error("没有可保存的明细");
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
      const englishNameError = validationErrors.find((error) => error.field === "英文名称");
      setEditEnglishNameError(englishNameError?.message ?? "");
      if (validationErrors.length || conflicts.length) {
        setSnackbar(`有 ${validationErrors.length + conflicts.length} 个字段未保存，请处理后重试`);
        return;
      }
      closeEditModal();
      setSnackbar("明细已保存");
    },
    onError: (error) => {
      const code = error instanceof Error ? (error as Error & { code?: string }).code : undefined;
      setSnackbar(code === "CONCURRENCY_TOKEN_REQUIRED" ? "当前应用版本仅可查看，请升级后再编辑货柜明细" : error instanceof Error ? error.message : "保存明细失败");
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
      "确认覆盖服务器值",
      `将覆盖 ${editConflicts.length} 个服务器已更新字段。若服务器再次变化，仍会要求重新确认。`,
      [
        { text: "取消", style: "cancel" },
        {
          text: "确认保留我的值",
          style: "destructive",
          onPress: () => updateMutation.mutate(acknowledgements),
        },
      ],
    );
  }

  const bulkMutation = useMutation({
    mutationFn: async ({
      action,
      previewToken,
    }: {
      action: "delete" | "float" | "prices" | "recalculate" | "backfill";
      previewToken: string;
    }) => {
      const scope = buildBatchScope(detailQueryPayload, selectedHguids);
      if (action === "delete") {
        if (!selectedHguids.length) throw new Error("请先选择要删除的明细");
        return batchDeleteDetails(containerGuid, scope, previewToken);
      }
      if (action === "float") {
        const rate = parseOptionalNumber(bulkFloatRate);
        if (rate === undefined || Number.isNaN(rate)) throw new Error("请输入有效浮率");
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
          throw new Error("请输入有效进口价或零售价");
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
      setSnackbar("批量操作已完成");
    },
    onError: (error, variables) => {
      if (isExpiredBatchPreviewError(error)) {
        // 预览令牌一旦失效不能重放：清除旧令牌，只重新获取预览，绝不自动执行。
        setBatchPreview(null);
        previewBulkMutation.mutate(variables.action, {
          onSuccess: (preview) => setSnackbar(`批量数据已变化，已刷新预览（影响 ${preview.affectedCount} 条），请重新确认执行`),
        });
        return;
      }
      setSnackbar(error instanceof Error ? error.message : "批量操作失败");
    },
  });

  const previewBulkMutation = useMutation({
    mutationFn: async (action: "delete" | "float" | "prices" | "recalculate" | "backfill") => {
      const scope = buildBatchScope(detailQueryPayload, selectedHguids);
      let operation: string;
      let parameters: Record<string, unknown> | undefined;
      if (action === "delete") {
        if (!selectedHguids.length) throw new Error("请先选择要删除的明细");
        operation = "delete-details";
      } else if (action === "float") {
        const floatRate = parseOptionalNumber(bulkFloatRate);
        if (floatRate === undefined || Number.isNaN(floatRate)) throw new Error("请输入有效浮率");
        operation = "apply-float-rate";
        parameters = { floatRate };
      } else if (action === "prices") {
        const importPrice = parseOptionalNumber(bulkImportPrice);
        const oemPrice = parseOptionalNumber(bulkOemPrice);
        if ((importPrice === undefined && oemPrice === undefined) || Number.isNaN(importPrice) || Number.isNaN(oemPrice)) {
          throw new Error("请输入有效进口价或零售价");
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
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "批量预览失败"),
  });

  const createProductsMutation = useMutation({
    mutationFn: async () => {
      if (!selectedHguids.length) throw new Error("请先选择新商品明细");
      const job = await createProductCreationJob({
        containerGuid,
        detailHguids: selectedHguids,
        operationId: buildCreateProductsOperationId(containerGuid, selectedHguids),
      });
      return wait(job.jobId);
    },
    onSuccess: (job) => {
      invalidateDetail();
      setSnackbar(job.message ?? `新商品任务完成：失败 ${job.result.failedCount}`);
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "创建新商品失败"),
  });

  const submitMutation = useMutation({
    mutationFn: async () => {
      const job = await createSubmitJob({
        containerGuid,
        operationId: buildSubmitContainerOperationId(containerGuid),
      });
      return waitSubmitJob(job.jobId);
    },
    onSuccess: (job) => {
      invalidateDetail();
      setSnackbar(job.message ?? `提交整柜完成：失败 ${job.result.failedCount}`);
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "提交整柜失败"),
  });

  const pushHqMutation = useMutation({
    mutationFn: async () => {
      if (!selectedDetails.length) throw new Error("请先选择要推送 HQ 的明细");
      const selection = buildContainerDetailHqPushSelection(selectedDetails);
      if (!selection.items.length) throw new Error("已选明细缺少商品编码或供应商货号候选");
      const job = await createPushProductsToHqJob({
        productCodes: selection.productCodes,
        items: selection.items,
        operationId: buildPushProductsToHqOperationId(containerGuid, selection.productCodes, selection.items.length),
      });
      return waitPushProductsToHqJob(job.jobId);
    },
    onSuccess: (job) => setSnackbar(job.message ?? "推送 HQ 任务已完成"),
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "推送 HQ 失败"),
  });

  const alignDomesticProductCodeMutation = useMutation({
    mutationFn: (detail: ContainerDetail) => {
      const detailHguid = getDetailGuid(detail).trim();
      const localProductCode = getDetailLocalProductCode(detail);
      const domesticProductCode = getDetailDomesticProductCode(detail);
      if (!detailHguid || !localProductCode || !domesticProductCode) {
        throw new Error("缺少可对齐的商品编码");
      }
      return alignDomesticProductCode({
        detailHguid,
        expectedDomesticProductCode: domesticProductCode,
        targetProductCode: localProductCode,
        supplierCode: getDetailLocalSupplierCode(detail),
      });
    },
    onSuccess: (result) => {
      invalidateDetail();
      setSnackbar(`已对齐国内商品编码 ${result.oldProductCode || ""} -> ${result.newProductCode || ""}`);
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "对齐国内商品编码失败"),
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
        fileNameHint: headerQuery.data?.货柜编号 || containerGuid,
      }),
    onSuccess: (result) => setSnackbar(`已导出 ${result.fileName}`),
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "导出失败"),
  });

  const handleAlignDomesticProductCode = (detail: ContainerDetail) => {
    const detailHguid = getDetailGuid(detail).trim();
    const localProductCode = getDetailLocalProductCode(detail);
    const domesticProductCode = getDetailDomesticProductCode(detail);
    if (alignDomesticProductCodeMutation.isPending) {
      return;
    }
    if (!detailHguid || !localProductCode || !domesticProductCode) {
      setSnackbar("缺少可对齐的商品编码");
      return;
    }

    const itemNumber = getDetailItemNumber(detail) || "--";
    const productName = getDetailProductName(detail) || "--";
    Alert.alert(
      "对齐国内商品编码",
      [
        `确认将国内商品编码 ${domesticProductCode} 对齐为 ${localProductCode}？`,
        `货号：${itemNumber}`,
        `商品：${productName}`,
        "如果目标国内编码已存在，后端会拒绝本次对齐，不会自动合并或覆盖。",
      ].join("\n"),
      [
        { text: "取消", style: "cancel" },
        {
          text: "对齐编码",
          onPress: () => {
            setAligningDetailHguid(detailHguid);
            alignDomesticProductCodeMutation.mutate(detail);
          },
        },
      ],
    );
  };

  const requestBatchPreview = (action: "delete" | "float" | "prices" | "recalculate" | "backfill", title: string) => {
    previewBulkMutation.mutate(action, {
      onSuccess: (preview) => {
        if (action === "float" || action === "prices") return;
        Alert.alert(
          title,
          `服务器预览：影响 ${preview.affectedCount} 条。${preview.fieldSummary.join("、")}`,
          [
            { text: "取消", style: "cancel" },
            {
              text: "确认执行",
              style: action === "delete" ? "destructive" : "default",
              onPress: () => bulkMutation.mutate({ action, previewToken: preview.previewToken }),
            },
          ],
        );
      },
    });
  };

  const toggleSelection = (hguid: string) => {
    if (!hguid) return;
    setSelectedHguids((current) => (
      current.includes(hguid)
        ? current.filter((item) => item !== hguid)
        : [...current, hguid]
    ));
  };

  const toggleCurrentPage = () => {
    // 本页全选只作用于当前加载明细，避免跨页批量操作误伤。
    setSelectedHguids((current) => toggleCurrentPageSelection(current, details));
  };

  const updateRangeFilter = (field: keyof DetailRangeFilterForm, value: string) => {
    setRangeFilters((current) => ({ ...current, [field]: value }));
  };

  const applySearch = () => {
    if (hasInvalidRangeFilters(rangeFilters)) {
      setSnackbar("筛选范围存在无效数字");
      return;
    }
    setPage(1);
    setAppliedKeyword(keyword.trim());
    setAppliedRangeFilters({ ...rangeFilters });
    setSelectedHguids([]);
  };

  const clearRangeFilters = () => {
    setRangeFilters(EMPTY_DETAIL_RANGE_FILTERS);
    setAppliedRangeFilters(EMPTY_DETAIL_RANGE_FILTERS);
    setPage(1);
    setSelectedHguids([]);
  };

  const changePage = (nextPage: number) => {
    setSelectedHguids([]);
    setPage(Math.max(1, nextPage));
  };

  if (!access.canViewContainers) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState title="无权访问货柜" description="请联系管理员开通货柜查看权限" />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={headerQuery.isRefetching || productsQuery.isRefetching}
            onRefresh={() => {
              void headerQuery.refetch();
              void productsQuery.refetch();
            }}
          />
        }
      >
        <Surface style={styles.headerPanel} mode="flat">
          <Button icon="arrow-left" onPress={() => router.back()}>返回</Button>
          {headerQuery.isLoading ? (
            <ActivityIndicator />
          ) : (
            <>
              <Text variant="titleLarge">{headerQuery.data?.货柜编号 ?? containerGuid}</Text>
              <View style={styles.metricGrid}>
                <DetailMetric label="预计到库" value={formatDate(headerQuery.data?.预计到岸日期)} />
                <DetailMetric label="实际到库" value={formatDate(headerQuery.data?.实际到货日期)} />
                <DetailMetric label="件数" value={formatNumber(headerQuery.data?.合计件数, 0)} />
                <DetailMetric label="金额" value={formatNumber(headerQuery.data?.合计金额)} />
              </View>
              {otherEditors.length || otherViewers.length ? (
                <View style={styles.presenceRow}>
                  {otherEditors.length ? <Text style={styles.presenceEditing}>正在编辑：{otherEditors.map((item) => `${item.userName} ${formatRecentActivity(item.lastActiveAt)}`).join("、")}</Text> : null}
                  {otherViewers.length ? <Text style={styles.muted}>正在查看：{otherViewers.map((item) => `${item.userName} ${formatRecentActivity(item.lastActiveAt)}`).join("、")}</Text> : null}
                </View>
              ) : null}
            </>
          )}
        </Surface>

        <Surface style={styles.filterPanel} mode="flat">
          <TextInput
            mode="outlined"
            label="搜索货号"
            value={keyword}
            onChangeText={setKeyword}
            right={<TextInput.Icon icon="magnify" onPress={applySearch} />}
            onSubmitEditing={applySearch}
          />
          <View style={styles.chipRow}>
            {TAGS.map((tag) => {
              const selected = tag.value === "all" ? selectedTags.length === 0 : selectedTags.includes(tag.value);
              const count = productsQuery.data?.tagStats[tag.value] ?? 0;
              return (
                <Chip
                  key={tag.value}
                  selected={selected}
                  onPress={() => {
                    setPage(1);
                    setSelectedHguids([]);
                    setSelectedTags((current) => toggleSelectedTag(current, tag.value));
                  }}
                >
                  {tag.label}{tag.value === "all" ? ` ${productsQuery.data?.itemsTotal ?? 0}` : ` ${count}`}
                </Chip>
              );
            })}
          </View>
          <View style={styles.switchRowCompact}>
            <Text style={styles.muted}>只读零售价</Text>
            <Switch value={showReadonlyOemPrice} onValueChange={setShowReadonlyOemPrice} />
          </View>
          <View style={styles.filterToggleRow}>
            <Button
              compact
              icon="filter-variant"
              mode={rangeFilterActive ? "contained-tonal" : "text"}
              onPress={() => setShowRangeFilters((value) => !value)}
            >
              {rangeFilterActive ? "范围筛选已启用" : "范围筛选"}
            </Button>
            {rangeFilterActive ? <Button compact mode="text" onPress={clearRangeFilters}>清空范围</Button> : null}
          </View>
          {showRangeFilters ? (
            <View style={styles.rangeFilterPanel}>
              <View style={styles.inputRow}>
                <TextInput
                  mode="outlined"
                  label="数量下限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.containerQuantityMin}
                  onChangeText={(value) => updateRangeFilter("containerQuantityMin", value)}
                  style={styles.inputHalf}
                />
                <TextInput
                  mode="outlined"
                  label="数量上限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.containerQuantityMax}
                  onChangeText={(value) => updateRangeFilter("containerQuantityMax", value)}
                  style={styles.inputHalf}
                />
              </View>
              <View style={styles.inputRow}>
                <TextInput
                  mode="outlined"
                  label="中包下限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.middlePackQuantityMin}
                  onChangeText={(value) => updateRangeFilter("middlePackQuantityMin", value)}
                  style={styles.inputHalf}
                />
                <TextInput
                  mode="outlined"
                  label="中包上限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.middlePackQuantityMax}
                  onChangeText={(value) => updateRangeFilter("middlePackQuantityMax", value)}
                  style={styles.inputHalf}
                />
              </View>
              <View style={styles.inputRow}>
                <TextInput
                  mode="outlined"
                  label="实时进货价下限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.warehouseImportPriceMin}
                  onChangeText={(value) => updateRangeFilter("warehouseImportPriceMin", value)}
                  style={styles.inputHalf}
                />
                <TextInput
                  mode="outlined"
                  label="实时进货价上限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.warehouseImportPriceMax}
                  onChangeText={(value) => updateRangeFilter("warehouseImportPriceMax", value)}
                  style={styles.inputHalf}
                />
              </View>
              <View style={styles.inputRow}>
                <TextInput
                  mode="outlined"
                  label="零售价下限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.oemPriceMin}
                  onChangeText={(value) => updateRangeFilter("oemPriceMin", value)}
                  style={styles.inputHalf}
                />
                <TextInput
                  mode="outlined"
                  label="零售价上限"
                  keyboardType="decimal-pad"
                  value={rangeFilters.oemPriceMax}
                  onChangeText={(value) => updateRangeFilter("oemPriceMax", value)}
                  style={styles.inputHalf}
                />
              </View>
              <View style={styles.actionRow}>
                <Button mode="contained" icon="filter-check" onPress={applySearch}>应用筛选</Button>
                <Button mode="text" icon="filter-remove" onPress={clearRangeFilters}>清空范围</Button>
              </View>
            </View>
          ) : null}
          <View style={styles.actionRow}>
            <Menu
              visible={bulkMenuVisible}
              onDismiss={() => setBulkMenuVisible(false)}
              anchor={<Button icon="dots-vertical" mode="contained" onPress={() => setBulkMenuVisible(true)}>批量操作</Button>}
            >
              {canEditContainer || canDeleteContainer ? (
                <>
                  {canEditContainer ? (
                    <>
                      <Menu.Item title="批量调浮率" onPress={() => { setBulkMenuVisible(false); setBulkModalType("float"); }} />
                      <Menu.Item title="批量改价" onPress={() => { setBulkMenuVisible(false); setBulkModalType("prices"); }} />
                      <Menu.Item title="重算成本" onPress={() => { setBulkMenuVisible(false); requestBatchPreview("recalculate", "重算成本"); }} />
                      <Menu.Item title="回填上次价格" onPress={() => { setBulkMenuVisible(false); requestBatchPreview("backfill", "回填上次价格"); }} />
                    </>
                  ) : null}
                  {canDeleteContainer ? (
                    <>
                      <Divider />
                      <Menu.Item title="删除所选" onPress={() => { setBulkMenuVisible(false); requestBatchPreview("delete", "删除所选明细"); }} />
                    </>
                  ) : null}
                  <Divider />
                </>
              ) : null}
              <Menu.Item title="导出 Excel" onPress={() => { setBulkMenuVisible(false); exportMutation.mutate("excel"); }} />
              <Menu.Item title="导出 PDF" onPress={() => { setBulkMenuVisible(false); exportMutation.mutate("pdf"); }} />
            </Menu>
            {canRunProductJobs ? (
              <>
                <Button
                  icon="plus-box"
                  mode="outlined"
                  loading={createProductsMutation.isPending}
                  disabled={createProductsMutation.isPending}
                  onPress={() => createProductsMutation.mutate()}
                >
                  创建新商品
                </Button>
                <Button
                  icon="check-decagram"
                  mode="outlined"
                  loading={submitMutation.isPending}
                  disabled={submitMutation.isPending}
                  onPress={() => submitMutation.mutate()}
                >
                  提交整柜
                </Button>
              </>
            ) : null}
            {canRunProductJobs ? (
              <Button
                icon="cloud-upload"
                mode="outlined"
                loading={pushHqMutation.isPending}
                disabled={pushHqMutation.isPending || selectedHguids.length === 0}
                onPress={() => pushHqMutation.mutate()}
              >
                推送已选 HQ
              </Button>
            ) : null}
          </View>
          <View style={styles.selectionSummaryRow}>
            <Text style={styles.muted}>已选 {selectedHguids.length} 条，本页已选 {currentPageSelectedCount} 条</Text>
            <Button
              compact
              mode="text"
              disabled={currentPageDetailGuids.length === 0}
              onPress={toggleCurrentPage}
            >
              {isCurrentPageFullySelected ? "取消本页" : "本页全选"}
            </Button>
          </View>
        </Surface>

        {productsQuery.isLoading ? (
          <ActivityIndicator style={styles.loading} />
        ) : details.length ? (
          details.map((detail) => {
            const hguid = getDetailGuid(detail).trim();
            return (
              <DetailCard
                key={hguid || detail.id || detail.ID}
                detail={detail}
                selected={selectedSet.has(hguid)}
                canEditContainer={canEditContainer}
                canAlignDomesticProductCode={canAlignDomesticProductCode}
                showReadonlyOemPrice={showReadonlyOemPrice}
                aligning={aligningDetailHguid === hguid && alignDomesticProductCodeMutation.isPending}
                alignDisabled={alignDomesticProductCodeMutation.isPending}
                onToggle={() => toggleSelection(hguid)}
                onAlign={() => handleAlignDomesticProductCode(detail)}
                onEdit={() => openEditModal(detail)}
              />
            );
          })
        ) : (
          <EmptyState title="没有明细" description="调整搜索或标签后再试" />
        )}

        <View style={styles.pagination}>
          <Button mode="outlined" disabled={page <= 1} onPress={() => changePage(page - 1)}>
            上一页
          </Button>
          <Text style={styles.pageText}>{page} / {totalPages}</Text>
          <Button
            mode="outlined"
            disabled={!productsQuery.data?.hasMore}
            onPress={() => changePage(page + 1)}
          >
            下一页
          </Button>
        </View>
      </ScrollView>

      <Portal>
        <Modal visible={Boolean(editingDetail && editForm)} onDismiss={updateMutation.isPending ? () => undefined : closeEditModal} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium">编辑明细</Text>
          {editForm ? (
            <>
              <TextInput mode="outlined" label="中文名称" disabled={updateMutation.isPending} value={editForm.productName} onChangeText={(value) => setEditForm((current) => current && { ...current, productName: value })} />
              <TextInput
                mode="outlined"
                label="英文名称"
                value={editForm.englishName}
                error={Boolean(editEnglishNameError)}
                disabled={updateMutation.isPending}
                onChangeText={handleEditEnglishNameChange}
              />
              <HelperText type="error" visible={Boolean(editEnglishNameError)}>
                {editEnglishNameError}
              </HelperText>
              {editValidationErrors.length ? (
                <Surface style={styles.validationPanel} mode="flat">
                  <Text variant="titleSmall" style={styles.validationTitle}>以下字段未保存</Text>
                  {editValidationErrors.map((error) => (
                    <Text key={`${error.field}-${error.code}`} style={styles.muted}>
                      {error.field === "*" ? "本行" : error.field}：{error.message}
                    </Text>
                  ))}
                </Surface>
              ) : null}
              {editConflicts.length ? (
                <Surface style={styles.conflictPanel} mode="flat">
                  <Text variant="titleSmall" style={styles.conflictTitle}>服务器已更新（{editConflicts.length}）</Text>
                  {editConflicts.length > 1 ? (
                    <View style={styles.actionRow}>
                      <Button compact disabled={updateMutation.isPending} onPress={() => applyServerValues(editConflicts)}>全部采用服务器值</Button>
                      <Button compact mode="contained-tonal" disabled={updateMutation.isPending} onPress={keepAllMyValues}>全部保留我的值</Button>
                    </View>
                  ) : null}
                  {editConflicts.map((conflict) => (
                    <View key={conflict.field} style={styles.conflictItem}>
                      <Text variant="labelLarge">{conflict.field}</Text>
                      <Text style={styles.muted}>服务器：{displayConflictValue(conflict.serverValue)}</Text>
                      <Text style={styles.muted}>我的值：{displayConflictValue(conflict.submittedValue)}</Text>
                      <View style={styles.actionRow}>
                        <Button compact disabled={updateMutation.isPending} onPress={() => applyServerValue(conflict)}>采用服务器值</Button>
                        <Button compact mode="contained-tonal" disabled={updateMutation.isPending} onPress={() => keepMyValue(conflict)}>保留我的值</Button>
                      </View>
                    </View>
                  ))}
                </Surface>
              ) : null}
              <View style={styles.inputRow}>
                <TextInput mode="outlined" label="国内价" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.domesticPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, domesticPrice: value })} style={styles.inputHalf} />
                <TextInput mode="outlined" label="进口价" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.importPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, importPrice: value })} style={styles.inputHalf} />
              </View>
              <View style={styles.inputRow}>
                <TextInput mode="outlined" label="零售价" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.oemPrice} onChangeText={(value) => setEditForm((current) => current && { ...current, oemPrice: value })} style={styles.inputHalf} />
                <TextInput mode="outlined" label="浮率" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.floatRate} onChangeText={(value) => setEditForm((current) => current && { ...current, floatRate: value })} style={styles.inputHalf} />
              </View>
              <View style={styles.inputRow}>
                <TextInput mode="outlined" label="装柜数量" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.containerQuantity} onChangeText={(value) => setEditForm((current) => current && { ...current, containerQuantity: value })} style={styles.inputHalf} />
                <TextInput mode="outlined" label="中包数" disabled={updateMutation.isPending} keyboardType="decimal-pad" value={editForm.middlePackQuantity} onChangeText={(value) => setEditForm((current) => current && { ...current, middlePackQuantity: value })} style={styles.inputHalf} />
              </View>
              <View style={styles.switchRow}>
                <Text>启用</Text>
                <Switch value={editForm.isActive} disabled={updateMutation.isPending} onValueChange={(value) => setEditForm((current) => current && { ...current, isActive: value })} />
              </View>
            </>
          ) : null}
          <View style={styles.actionRow}>
            <Button disabled={updateMutation.isPending} onPress={closeEditModal}>取消</Button>
            <Button mode="contained" disabled={updateMutation.isPending} loading={updateMutation.isPending} onPress={() => updateMutation.mutate(undefined)}>保存</Button>
          </View>
        </Modal>

        <Modal visible={Boolean(bulkModalType)} onDismiss={() => setBulkModalType(null)} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium">{bulkModalType === "float" ? "批量调浮率" : "批量改价"}</Text>
          {bulkModalType === "float" ? (
            <TextInput mode="outlined" label="浮率" keyboardType="decimal-pad" value={bulkFloatRate} onChangeText={(value) => { setBatchPreview(null); setBulkFloatRate(value); }} />
          ) : (
            <>
              <TextInput mode="outlined" label="进口价" keyboardType="decimal-pad" value={bulkImportPrice} onChangeText={(value) => { setBatchPreview(null); setBulkImportPrice(value); }} />
              <TextInput mode="outlined" label="零售价" keyboardType="decimal-pad" value={bulkOemPrice} onChangeText={(value) => { setBatchPreview(null); setBulkOemPrice(value); }} />
            </>
          )}
          <Text style={styles.muted}>{selectedHguids.length ? `作用于已选 ${selectedHguids.length} 条` : "未选择时作用于当前筛选结果"}</Text>
          {batchPreview && batchPreview.action === bulkModalType ? (
            <Surface style={styles.previewPanel} mode="flat">
              <Text>服务器预览：影响 {batchPreview.affectedCount} 条</Text>
              {batchPreview.fieldSummary.length ? <Text style={styles.muted}>{batchPreview.fieldSummary.join("、")}</Text> : null}
            </Surface>
          ) : null}
          <View style={styles.actionRow}>
            <Button onPress={() => setBulkModalType(null)}>取消</Button>
            <Button
              mode="contained"
              loading={bulkMutation.isPending || previewBulkMutation.isPending}
              onPress={() => {
                if (!bulkModalType) return;
                const action = bulkModalType === "float" ? "float" : "prices";
                if (batchPreview?.action === action) {
                  bulkMutation.mutate({ action, previewToken: batchPreview.previewToken });
                  return;
                }
                requestBatchPreview(action, action === "float" ? "批量调浮率" : "批量改价");
              }}
            >
              {batchPreview?.action === bulkModalType ? "确认执行" : "预览"}
            </Button>
          </View>
        </Modal>
      </Portal>
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")}>{snackbar}</Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  content: {
    gap: 16,
    padding: 16,
    paddingTop: 12,
    paddingBottom: 24,
  },
  headerPanel: {
    gap: 10,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  filterPanel: {
    gap: 10,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  card: {
    borderRadius: 12,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  chipRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  selectionSummaryRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  filterToggleRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  rangeFilterPanel: {
    gap: 8,
  },
  actionRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    alignItems: "center",
  },
  metricGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  metric: {
    minWidth: 92,
    flexGrow: 1,
  },
  muted: {
    color: "#64748B",
  },
  remark: {
    marginTop: 10,
    color: "#475569",
  },
  warningText: {
    marginTop: 10,
    color: "#B45309",
  },
  presenceRow: {
    gap: 4,
    paddingTop: 2,
  },
  presenceEditing: {
    color: "#B45309",
  },
  detailImageFrame: {
    width: 72,
    height: 72,
    marginRight: 12,
    overflow: "hidden",
    borderRadius: 8,
    backgroundColor: "#EAEFF3",
  },
  detailImage: {
    width: "100%",
    height: "100%",
  },
  detailImagePlaceholder: {
    flex: 1,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EAEFF3",
  },
  imagePlaceholderText: {
    color: "#64748B",
  },
  loading: {
    marginVertical: 28,
  },
  pagination: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 16,
  },
  pageText: {
    minWidth: 70,
    textAlign: "center",
  },
  modal: {
    margin: 18,
    gap: 10,
    padding: 16,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
  },
  conflictPanel: {
    gap: 8,
    padding: 10,
    borderRadius: 8,
    backgroundColor: "#FEF2F2",
    borderWidth: 1,
    borderColor: "#FCA5A5",
  },
  validationPanel: {
    gap: 4,
    padding: 10,
    borderRadius: 8,
    backgroundColor: "#FFF7ED",
    borderWidth: 1,
    borderColor: "#FDBA74",
  },
  validationTitle: {
    color: "#9A3412",
  },
  conflictTitle: {
    color: "#B91C1C",
  },
  conflictItem: {
    gap: 4,
    paddingTop: 6,
    borderTopWidth: 1,
    borderTopColor: "#FECACA",
  },
  previewPanel: {
    gap: 4,
    padding: 10,
    borderRadius: 8,
    backgroundColor: "#EFF6FF",
  },
  inputRow: {
    flexDirection: "row",
    gap: 10,
  },
  inputHalf: {
    flex: 1,
  },
  switchRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  switchRowCompact: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-start",
    gap: 8,
  },
});
