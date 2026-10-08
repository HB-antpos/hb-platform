import type { AxiosResponse } from "axios";
import { apiClient } from "@/shared/api/client";
import type {
  AlignDomesticProductCodePreview,
  AlignDomesticProductCodeRequest,
  AlignDomesticProductCodeResult,
  ContainerDetailBatchActionResult,
  ContainerDetailBatchPreview,
  ContainerDetailBatchUpdateResult,
  ContainerDetailBatchScope,
  ContainerDetailConcurrentConflict,
  ContainerDetailPresence,
  ContainerDetailSaveValidationError,
  ContainerDetail,
  ContainerDetailHqPushSelection,
  ContainerDetailQuery,
  ContainerDetailQueryResult,
  ContainerExportRequest,
  ContainerExportResult,
  ContainerJob,
  ContainerListResponse,
  ContainerMain,
  ContainerQueryRequest,
  CreateContainerRequest,
  CreateNewProductsHqPushOutcome,
  CreateNewProductsRunResult,
  PushProductsToHqJob,
  PushProductsToHqJobRequest,
  PushProductsToHqUpdateField,
  SyncResult,
  UpdateContainerDetailRequest,
  UpdateContainerRequest,
} from "./types";
import {
  DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS,
  buildAlignDomesticProductCodePayload,
  buildContainerListPayload,
  buildCreatedProductsHqPushPlan,
  buildCreateProductsOperationId,
  buildPushProductsToHqOperationId,
  findContainerDetailsMissingRetailPrice,
  getDetailGuid,
  normalizeCreateContainerResponse,
  normalizeAlignDomesticProductCodePreview,
  normalizeAlignDomesticProductCodeResult,
  normalizeContainerDetailResponse,
  normalizeContainerDetailQueryResult,
  normalizeContainerJob,
  normalizeContainerListResponse,
  normalizePushProductsToHqJob,
  normalizeSyncResult,
  unwrapData,
} from "./query";

const CONTAINERS_PATH = "/react/v1/containers";
const CONTAINER_PRODUCTS_PATH = "/react/v1/container-products";
const PRODUCTS_PATH = "/react/v1/products";

type ExportData = ArrayBuffer | ArrayBufferView | Blob | string;

function ensureSuccess(data: unknown, fallbackMessage: string) {
  if (
    data &&
    typeof data === "object" &&
    ("success" in data || "isSuccess" in data)
  ) {
    const record = data as Record<string, unknown>;
    if (record.success === false || record.isSuccess === false) {
      throw new Error(typeof record.message === "string" ? record.message : fallbackMessage);
    }
  }
}

function pickNumber(data: unknown, key: string, fallback: number) {
  const payload = unwrapData(data);
  if (!payload || typeof payload !== "object") {
    return fallback;
  }
  const value = (payload as Record<string, unknown>)[key];
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? parsed : fallback;
}

function normalizeDetailValidationErrors(data: unknown): ContainerDetailSaveValidationError[] {
  const payload = unwrapData(data);
  if (!payload || typeof payload !== "object") {
    return [];
  }
  const record = payload as Record<string, unknown>;
  const rawErrors = record.validationErrors ?? record.ValidationErrors;
  if (!Array.isArray(rawErrors)) {
    return [];
  }

  return rawErrors.flatMap((item) => {
    if (!item || typeof item !== "object") {
      return [];
    }
    const error = item as Record<string, unknown>;
    const hguid = error.hguid ?? error.HGUID;
    const field = error.field ?? error.Field;
    const code = error.code ?? error.Code;
    const message = error.message ?? error.Message;
    if (
      typeof hguid !== "string" ||
      typeof field !== "string" ||
      typeof code !== "string" ||
      typeof message !== "string" ||
      !hguid.trim() ||
      !field.trim() ||
      !code.trim() ||
      !message.trim()
    ) {
      return [];
    }
    return [{
      hguid: hguid.trim(),
      field: field.trim(),
      code: code.trim(),
      message: message.trim(),
    }];
  });
}

function normalizeDetailConflicts(data: unknown): ContainerDetailConcurrentConflict[] {
  const payload = unwrapData(data);
  if (!payload || typeof payload !== "object") return [];
  const record = payload as Record<string, unknown>;
  const rawConflicts = record.conflicts ?? record.Conflicts;
  if (!Array.isArray(rawConflicts)) return [];

  return rawConflicts.flatMap((item) => {
    if (!item || typeof item !== "object") return [];
    const conflict = item as Record<string, unknown>;
    const hguid = conflict.hguid ?? conflict.HGUID;
    const field = conflict.field ?? conflict.Field;
    const code = conflict.code ?? conflict.Code;
    const message = conflict.message ?? conflict.Message;
    const currentServerFieldToken = conflict.currentServerFieldToken ?? conflict.CurrentServerFieldToken;
    if (
      typeof hguid !== "string" || !hguid.trim()
      || typeof field !== "string" || !field.trim()
      || code !== "CONCURRENT_FIELD_UPDATE"
      || typeof message !== "string" || !message.trim()
      || typeof currentServerFieldToken !== "string" || !currentServerFieldToken.trim()
    ) return [];
    return [{
      hguid: hguid.trim(),
      field: field.trim(),
      code,
      message: message.trim(),
      serverValue: conflict.serverValue ?? conflict.ServerValue,
      submittedValue: conflict.submittedValue ?? conflict.SubmittedValue,
      currentServerFieldToken: currentServerFieldToken.trim(),
    }];
  });
}

function normalizeDetailPresence(data: unknown): ContainerDetailPresence {
  const payload = unwrapData(data);
  const record = payload && typeof payload === "object" ? payload as Record<string, unknown> : {};
  const normalizeUsers = (value: unknown) => Array.isArray(value) ? value.flatMap((item) => {
    if (!item || typeof item !== "object") return [];
    const user = item as Record<string, unknown>;
    const userGuid = user.userGuid ?? user.UserGuid;
    const userName = user.userName ?? user.UserName ?? user.displayName ?? user.DisplayName ?? user.username ?? user.Username;
    const lastActiveAt = user.lastActiveAt ?? user.LastActiveAt;
    if (typeof userGuid !== "string" || !userGuid.trim() || typeof userName !== "string" || !userName.trim()) return [];
    return [{ userGuid: userGuid.trim(), userName: userName.trim(), ...(typeof lastActiveAt === "string" ? { lastActiveAt } : {}) }];
  }) : [];
  return {
    viewers: normalizeUsers(record.viewers ?? record.Viewers),
    editors: normalizeUsers(record.editors ?? record.Editors),
  };
}

function normalizeBatchPreview(data: unknown): ContainerDetailBatchPreview {
  const payload = unwrapData(data);
  const record = payload && typeof payload === "object" ? payload as Record<string, unknown> : {};
  const previewToken = record.previewToken ?? record.PreviewToken;
  if (typeof previewToken !== "string" || !previewToken.trim()) throw new Error("批量预览未返回有效令牌");
  const affectedRaw = record.affectedCount ?? record.AffectedCount ?? 0;
  const affectedCount = typeof affectedRaw === "number" && Number.isFinite(affectedRaw) ? affectedRaw : Number(affectedRaw) || 0;
  const summary = record.fieldSummary ?? record.FieldSummary;
  const expiresAt = record.expiresAt ?? record.ExpiresAt;
  return {
    previewToken: previewToken.trim(),
    affectedCount,
    fieldSummary: Array.isArray(summary) ? summary.filter((item): item is string => typeof item === "string") : [],
    ...(typeof expiresAt === "string" ? { expiresAt } : {}),
  };
}

function mapDetailUpdate(item: UpdateContainerDetailRequest) {
  return {
    HGUID: item.hguid,
    调整浮率: item.调整浮率,
    国内价格: item.国内价格,
    进口价格: item.进口价格,
    运输成本: item.运输成本,
    商品名称: item.商品名称,
    英文名称: item.英文名称,
    ClearEnglishName: item.ClearEnglishName,
    贴牌价格: item.贴牌价格,
    单件装箱数: item.单件装箱数,
    中包数: item.中包数,
    单件体积: item.单件体积,
    装柜数量: item.装柜数量,
    合计装柜体积: item.合计装柜体积,
    合计装柜金额: item.合计装柜金额,
    IsActive: item.IsActive,
    SkipRelatedProductSync: item.SkipRelatedProductSync,
    ExpectedServerFieldTokens: item.expectedServerFieldTokens,
    OverrideAcknowledgements: item.overrideAcknowledgements,
  };
}

async function toBase64(data: ExportData) {
  const { fromByteArray } = await import("base64-js");
  if (data instanceof ArrayBuffer) {
    return fromByteArray(new Uint8Array(data));
  }
  if (ArrayBuffer.isView(data)) {
    return fromByteArray(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
  }
  if (typeof Blob !== "undefined" && data instanceof Blob) {
    return fromByteArray(new Uint8Array(await data.arrayBuffer()));
  }
  return typeof data === "string" ? data : "";
}

function getHeader(response: AxiosResponse, name: string) {
  const headers = response.headers as Record<string, unknown>;
  const value = headers[name] ?? headers[name.toLowerCase()];
  return Array.isArray(value) ? String(value[0] ?? "") : String(value ?? "");
}

function getFileNameFromDisposition(disposition: string, fallback: string) {
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (encoded) {
    return decodeURIComponent(encoded);
  }
  return /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? fallback;
}

async function writeAndShareExport(
  response: AxiosResponse<ExportData>,
  fallbackFileName: string,
  fallbackContentType: string,
): Promise<ContainerExportResult> {
  const FileSystem = await import("expo-file-system/legacy");
  const Sharing = await import("expo-sharing");
  const fileName = getFileNameFromDisposition(
    getHeader(response, "content-disposition"),
    fallbackFileName,
  );
  const contentType = getHeader(response, "content-type") || fallbackContentType;
  const base64 = await toBase64(response.data);
  if (!base64) {
    throw new Error("导出文件为空");
  }

  const fileUri = `${FileSystem.documentDirectory ?? ""}${fileName}`;
  // 导出接口返回二进制；本地分享前必须转 Base64 写入 Expo 文件系统。
  await FileSystem.writeAsStringAsync(fileUri, base64, {
    encoding: FileSystem.EncodingType.Base64,
  });

  if (await Sharing.isAvailableAsync()) {
    await Sharing.shareAsync(fileUri, {
      mimeType: contentType,
      dialogTitle: fileName,
    });
  }

  return { fileUri, fileName, contentType };
}

async function postContainerAction(
  containerGuid: string,
  action: string,
  body: object,
) {
  const response = await apiClient.post<ContainerDetailBatchActionResult>(
    `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/actions/${action}`,
    body,
  );
  return {
    totalUpdated: pickNumber(response.data, "totalUpdated", 0),
    totalRequested: pickNumber(response.data, "totalRequested", 0),
  };
}

export async function getContainerList(query: ContainerQueryRequest = {}): Promise<ContainerListResponse> {
  const response = await apiClient.post(`${CONTAINERS_PATH}/list`, buildContainerListPayload(query));
  ensureSuccess(response.data, "获取货柜列表失败");
  return normalizeContainerListResponse(response.data, query);
}

export async function getContainerDetail(containerGuid: string): Promise<ContainerMain> {
  const response = await apiClient.get<unknown>(`${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}`);
  ensureSuccess(response.data, "获取货柜详情失败");
  return normalizeContainerDetailResponse(response.data);
}

export async function queryContainerProducts(
  containerGuid: string,
  query: ContainerDetailQuery,
): Promise<ContainerDetailQueryResult> {
  const response = await apiClient.post(
    `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/products/query`,
    {
      ...query,
      containerGuid,
    },
  );
  ensureSuccess(response.data, "查询货柜明细失败");
  // 后端已在每行直接返回 MatchType / LocalProductCode / DomesticProductCode / HasProductCodeConflict，
  // 不再额外调用 product-warehouse/detect，避免每次翻页多一次请求（且该接口对部分角色 403）。
  return normalizeContainerDetailQueryResult(response.data, query);
}

export async function createContainer(data: CreateContainerRequest): Promise<string> {
  const response = await apiClient.post<unknown>(CONTAINERS_PATH, data);
  ensureSuccess(response.data, "创建货柜失败");
  return normalizeCreateContainerResponse(response.data);
}

export async function updateContainer(containerGuid: string, data: UpdateContainerRequest) {
  const response = await apiClient.put(`${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}`, data);
  ensureSuccess(response.data, "更新货柜失败");
  return true;
}

export async function batchUpdateDetails(
  containerGuid: string,
  updates: UpdateContainerDetailRequest[],
): Promise<ContainerDetailBatchUpdateResult> {
  try {
    const response = await apiClient.post(
      `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/batch-update-details`,
      updates.map(mapDetailUpdate),
    );
    ensureSuccess(response.data, "批量更新货柜明细失败");
    return {
      totalUpdated: pickNumber(response.data, "totalUpdated", updates.length),
      totalRequested: pickNumber(response.data, "totalRequested", updates.length),
      validationErrors: normalizeDetailValidationErrors(response.data),
      conflicts: normalizeDetailConflicts(response.data),
    };
  } catch (error) {
    const response = (error as { response?: { status?: number; data?: unknown } })?.response;
    const payload = response?.data && typeof response.data === "object" ? unwrapData(response.data) as Record<string, unknown> : undefined;
    const code = payload?.code ?? payload?.Code;
    if (response?.status === 428 && code === "CONCURRENCY_TOKEN_REQUIRED") {
      const upgradeError = new Error(typeof payload?.message === "string" ? payload.message : "请升级应用后再编辑货柜明细") as Error & { code?: string };
      upgradeError.code = "CONCURRENCY_TOKEN_REQUIRED";
      throw upgradeError;
    }
    throw error;
  }
}

export async function previewContainerDetailBatchAction(
  containerGuid: string,
  data: { operation: string; scope: ContainerDetailBatchScope; parameters?: Record<string, unknown> },
) {
  const response = await apiClient.post(
    `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/actions/preview`,
    data,
  );
  ensureSuccess(response.data, "批量预览失败");
  return normalizeBatchPreview(response.data);
}

export async function batchDeleteDetails(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  previewToken: string,
) {
  return postContainerAction(containerGuid, "delete-details", { ...scope, previewToken });
}

export async function applyFloatRate(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  floatRate: number,
  previewToken: string,
) {
  return postContainerAction(containerGuid, "apply-float-rate", { ...scope, floatRate, previewToken });
}

export async function applyPrices(
  containerGuid: string,
  scope: ContainerDetailBatchScope,
  prices: { importPrice?: number | null; oemPrice?: number | null },
  previewToken: string,
) {
  return postContainerAction(containerGuid, "apply-prices", { ...scope, ...prices, previewToken });
}

export async function recalculate(containerGuid: string, scope: ContainerDetailBatchScope, previewToken: string) {
  return postContainerAction(containerGuid, "recalculate-costs", { ...scope, previewToken });
}

export async function backfill(containerGuid: string, scope: ContainerDetailBatchScope, previewToken: string) {
  return postContainerAction(containerGuid, "backfill-last-prices", { ...scope, previewToken });
}

export async function getContainerDetailPresence(containerGuid: string): Promise<ContainerDetailPresence> {
  const response = await apiClient.get(`${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/editing-presence`);
  ensureSuccess(response.data, "获取协作状态失败");
  return normalizeDetailPresence(response.data);
}

export async function heartbeatContainerDetailPresence(
  containerGuid: string,
  data: { clientSessionId: string; state: "viewing" | "editing" },
): Promise<ContainerDetailPresence> {
  const response = await apiClient.post(
    `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/editing-presence/heartbeat`,
    data,
  );
  ensureSuccess(response.data, "更新协作状态失败");
  return normalizeDetailPresence(response.data);
}

export async function leaveContainerDetailPresence(containerGuid: string, clientSessionId: string) {
  await apiClient.post(`${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/editing-presence/leave`, { clientSessionId });
}

export async function pushContainersToHbSales(containerGuids: string[]): Promise<SyncResult> {
  const response = await apiClient.post(`${CONTAINERS_PATH}/push-to-hbsales`, { containerGuids });
  ensureSuccess(response.data, "推送 HBSales 失败");
  return normalizeSyncResult(response.data);
}

export async function createProductCreationJob(data: {
  operationId: string;
  containerGuid: string;
  detailHguids: string[];
}): Promise<ContainerJob> {
  const response = await apiClient.post(`${CONTAINER_PRODUCTS_PATH}/create-new-products/jobs`, data);
  ensureSuccess(response.data, "创建新商品任务失败");
  return normalizeContainerJob(response.data);
}

export async function getJob(jobId: string): Promise<ContainerJob> {
  const response = await apiClient.get(`${CONTAINER_PRODUCTS_PATH}/create-new-products/jobs/${encodeURIComponent(jobId)}`);
  ensureSuccess(response.data, "查询货柜任务失败");
  return normalizeContainerJob(response.data, jobId);
}

export async function wait(jobId: string, options: { pollIntervalMs?: number; timeoutMs?: number } = {}) {
  const pollIntervalMs = options.pollIntervalMs ?? 2000;
  const timeoutMs = options.timeoutMs ?? 10 * 60 * 1000;
  const startedAt = Date.now();

  while (Date.now() - startedAt <= timeoutMs) {
    const job = await getJob(jobId);
    if (job.status === "Succeeded" || job.status === "Failed") {
      return job;
    }
    await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
  }
  throw new Error("货柜任务轮询超时");
}

export async function createSubmitJob(data: { operationId: string; containerGuid: string }): Promise<ContainerJob> {
  const response = await apiClient.post(`${CONTAINER_PRODUCTS_PATH}/submit-container/jobs`, {
    ...data,
    detailHguids: [],
    submitContainer: true,
  });
  ensureSuccess(response.data, "提交整柜任务失败");
  return normalizeContainerJob(response.data);
}

export async function waitSubmitJob(jobId: string, options?: { pollIntervalMs?: number; timeoutMs?: number }) {
  return wait(jobId, options);
}

export async function createPushProductsToHqJob(data: PushProductsToHqJobRequest): Promise<PushProductsToHqJob> {
  const response = await apiClient.post(`${PRODUCTS_PATH}/push-to-hq/jobs`, data);
  ensureSuccess(response.data, "创建推送 HQ 任务失败");
  return normalizePushProductsToHqJob(response.data);
}

export async function getPushProductsToHqJob(jobId: string): Promise<PushProductsToHqJob> {
  const response = await apiClient.get(`${PRODUCTS_PATH}/push-to-hq/jobs/${encodeURIComponent(jobId)}`);
  ensureSuccess(response.data, "查询推送 HQ 任务失败");
  return normalizePushProductsToHqJob(response.data, jobId);
}

export async function waitPushProductsToHqJob(
  jobId: string,
  options: { pollIntervalMs?: number; timeoutMs?: number } = {},
) {
  const pollIntervalMs = options.pollIntervalMs ?? 2000;
  const timeoutMs = options.timeoutMs ?? 10 * 60 * 1000;
  const startedAt = Date.now();

  while (Date.now() - startedAt <= timeoutMs) {
    const job = await getPushProductsToHqJob(jobId);
    if (job.status === "Succeeded" || job.status === "Failed") {
      return job;
    }
    await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
  }
  throw new Error("推送 HQ 任务轮询超时");
}

export async function alignDomesticProductCode(
  payload: AlignDomesticProductCodeRequest,
): Promise<AlignDomesticProductCodeResult> {
  const response = await apiClient.post(
    `${CONTAINERS_PATH}/details/align-domestic-product-code`,
    buildAlignDomesticProductCodePayload(payload),
  );
  ensureSuccess(response.data, "对齐国内商品编码失败");
  return normalizeAlignDomesticProductCodeResult(response.data);
}

/** 对齐编码确认前预览：直接改码，或目标编码已存在时合并到已有国内商品（附差异字段） */
export async function previewAlignDomesticProductCode(
  payload: AlignDomesticProductCodeRequest,
): Promise<AlignDomesticProductCodePreview> {
  const response = await apiClient.post(
    `${CONTAINERS_PATH}/details/align-domestic-product-code/preview`,
    buildAlignDomesticProductCodePayload({ ...payload, mergeIntoExistingDomesticProduct: false }),
  );
  ensureSuccess(response.data, "读取对齐预览失败");
  return normalizeAlignDomesticProductCodePreview(response.data);
}

export async function exportContainerDetails(
  containerGuid: string,
  request: ContainerExportRequest,
): Promise<ContainerExportResult> {
  const format = request.format;
  const response = await apiClient.post<ExportData>(
    `${CONTAINERS_PATH}/${encodeURIComponent(containerGuid)}/products/export`,
    {
      format,
      query: request.query,
      selectedHguids: request.selectedHguids ?? [],
      columns: request.columns ?? [],
    },
    { responseType: "arraybuffer" },
  );
  const extension = format === "pdf" ? "pdf" : "xlsx";
  const contentType = format === "pdf"
    ? "application/pdf"
    : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
  return writeAndShareExport(
    response,
    `${request.fileNameHint || "container-details"}.${extension}`,
    contentType,
  );
}

const FINAL_JOB_STATUSES = new Set(["Succeeded", "Failed"]);

/** 创建新商品：提交任务并等到结束（任务提交时已结束则不再轮询）。 */
export async function runCreateNewProductsJob(params: {
  containerGuid: string;
  detailHguids: string[];
  operationId?: string;
  pollIntervalMs?: number;
  timeoutMs?: number;
}): Promise<ContainerJob> {
  const operationId = params.operationId
    ?? buildCreateProductsOperationId(params.containerGuid, params.detailHguids);
  const job = await createProductCreationJob({
    operationId,
    containerGuid: params.containerGuid,
    detailHguids: params.detailHguids,
  });
  if (FINAL_JOB_STATUSES.has(job.status)) return job;
  return wait(job.jobId, { pollIntervalMs: params.pollIntervalMs, timeoutMs: params.timeoutMs });
}

/** 发送到 HQ：按发送选择提交任务并等到结束；updateFields 默认全部 17 项（与 Web 默认一致）。 */
export async function runPushProductsToHqJob(params: {
  containerGuid: string;
  selection: ContainerDetailHqPushSelection;
  updateFields?: readonly PushProductsToHqUpdateField[];
  pollIntervalMs?: number;
  timeoutMs?: number;
}): Promise<PushProductsToHqJob> {
  const updateFields = [...(params.updateFields ?? DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS)];
  const job = await createPushProductsToHqJob({
    productCodes: params.selection.productCodes,
    items: params.selection.items,
    updateFields,
    operationId: buildPushProductsToHqOperationId(
      params.containerGuid,
      params.selection.productCodes,
      params.selection.items.length,
      updateFields,
    ),
  });
  if (FINAL_JOB_STATUSES.has(job.status)) return job;
  return waitPushProductsToHqJob(job.jobId, {
    pollIntervalMs: params.pollIntervalMs,
    timeoutMs: params.timeoutMs,
  });
}

/**
 * 「创建新商品」完整流程（对齐 Web createNewProducts）：
 * 1. 前置校验：新品行零售价必须 > 0，否则返回 blocked，不调用任何接口；
 * 2. 创建任务并等待结束（失败/超时直接抛出，由界面提示，不会继续推送）；
 * 3. syncToHq（默认 true，与 Web 确认框默认勾选一致）时，重载明细后只把本次 created 的商品发送到 HQ。
 *    推送阶段的任何失败都收敛成 push 结果，不影响已完成的创建结果。
 */
export async function createNewProductsAndSyncHq(params: {
  containerGuid: string;
  /** 用户确认创建的明细（提交范围、缺价校验、HQ 候选回退都用它） */
  details: readonly ContainerDetail[];
  syncToHq?: boolean;
  /** 创建结束后重载当前页明细，让已建档状态进入「最新行」；抛错时回退到 details */
  reloadDetails?: () => Promise<readonly ContainerDetail[]> | readonly ContainerDetail[];
  /** 已有发送到 HQ 的任务在提交时返回 true，则本次不并发推送 */
  isPushInFlight?: () => boolean;
  pollIntervalMs?: number;
  timeoutMs?: number;
}): Promise<CreateNewProductsRunResult> {
  const notRun: CreateNewProductsHqPushOutcome = { status: "skipped", reason: "not-run" };
  const detailHguids = Array.from(new Set(params.details.map((detail) => getDetailGuid(detail).trim()).filter(Boolean)));
  if (!detailHguids.length) {
    return { status: "blocked", blockedReason: "NO_DETAILS", missingRetailPrice: [], push: notRun };
  }
  const missingRetailPrice = findContainerDetailsMissingRetailPrice(params.details);
  if (missingRetailPrice.length) {
    return { status: "blocked", blockedReason: "MISSING_RETAIL_PRICE", missingRetailPrice, push: notRun };
  }

  const job = await runCreateNewProductsJob({
    containerGuid: params.containerGuid,
    detailHguids,
    pollIntervalMs: params.pollIntervalMs,
    timeoutMs: params.timeoutMs,
  });

  const created = job.result.created;
  if (params.syncToHq === false) {
    return { status: "completed", missingRetailPrice: [], job, push: { status: "skipped", reason: "sync-disabled" } };
  }
  if (!created.length) {
    return { status: "completed", missingRetailPrice: [], job, push: { status: "skipped", reason: "nothing-created" } };
  }

  let latestDetails: readonly ContainerDetail[] = [];
  try {
    latestDetails = (await params.reloadDetails?.()) ?? [];
  } catch {
    // 重载失败时只用确认创建时的行兜底，创建结果仍然有效。
  }
  const plan = buildCreatedProductsHqPushPlan(created, latestDetails, params.details, {
    pushInFlight: params.isPushInFlight?.() ?? false,
  });
  if (!plan.selection.items.length) {
    return { status: "completed", missingRetailPrice: [], job, plan, push: { status: "skipped", reason: "no-candidates" } };
  }
  if (!plan.shouldPush) {
    return { status: "completed", missingRetailPrice: [], job, plan, push: { status: "skipped", reason: "push-busy" } };
  }

  try {
    const pushJob = await runPushProductsToHqJob({
      containerGuid: params.containerGuid,
      selection: plan.selection,
      pollIntervalMs: params.pollIntervalMs,
      timeoutMs: params.timeoutMs,
    });
    return {
      status: "completed",
      missingRetailPrice: [],
      job,
      plan,
      push: { status: pushJob.status === "Succeeded" ? "succeeded" : "failed", job: pushJob },
    };
  } catch (error) {
    return {
      status: "completed",
      missingRetailPrice: [],
      job,
      plan,
      push: { status: "error", message: error instanceof Error ? error.message : "发送到 HQ 失败" },
    };
  }
}
