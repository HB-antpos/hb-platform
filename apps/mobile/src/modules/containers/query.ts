import type {
  AlignDomesticProductCodePreview,
  AlignDomesticProductCodeRequest,
  AlignDomesticProductCodeResult,
  ContainerDetail,
  ContainerDetailHqPushSelection,
  ContainerDetailQuery,
  ContainerDetailQueryMatchType,
  ContainerDetailQueryResult,
  ContainerDetailQueryTag,
  ContainerDetailTagStats,
  DetectionItem,
  DetectionResult,
  ContainerJob,
  ContainerJobResult,
  ContainerJobStatus,
  ContainerListResponse,
  ContainerMain,
  ContainerQueryRequest,
  SyncResult,
  PushProductsToHqItem,
  PushProductsToHqJob,
  PushProductsToHqResult,
  PushProductsToHqUpdateField,
} from "./types";

export const CONTAINER_LIST_PAGE_SIZE = 20;
export const CONTAINER_DETAIL_PAGE_SIZE = 30;

export const DEFAULT_CONTAINER_DETAIL_EXPORT_COLUMNS = [
  "index",
  "image",
  "itemNumber",
  "barcode",
  "chineseName",
  "englishName",
  "loadingPieces",
  "loadingQuantity",
  "packingQuantity",
  "domesticPrice",
  "importPrice",
  "oemPrice",
  "unitVolume",
  "totalVolume",
  "remarks",
] as const;

export const DEFAULT_CONTAINER_DETAIL_PDF_EXPORT_COLUMNS = [
  "index",
  "image",
  "itemNumber",
  "barcode",
  "englishName",
  "oemPrice",
] as const;

const EMPTY_TAG_STATS: ContainerDetailTagStats = {
  all: 0,
  new: 0,
  existing: 0,
  noOemPrice: 0,
  abnormalImport: 0,
  active: 0,
  inactive: 0,
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function pick<T = unknown>(record: Record<string, unknown>, ...keys: string[]): T | undefined {
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value as T;
    }
  }
  return undefined;
}

export function trimToUndefined(value?: string) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

function asNumber(value: unknown, fallback: number) {
  const parsed = typeof value === "string" ? Number(value) : value;
  return typeof parsed === "number" && Number.isFinite(parsed) ? parsed : fallback;
}

function asString(value: unknown, fallback = "") {
  return typeof value === "string" ? value : fallback;
}

export function unwrapData(value: unknown): unknown {
  if (!isRecord(value)) {
    return value;
  }
  return "data" in value ? value.data : value;
}

export function buildAlignDomesticProductCodePayload(payload: AlignDomesticProductCodeRequest) {
  return {
    DetailHguid: payload.detailHguid,
    ExpectedDomesticProductCode: payload.expectedDomesticProductCode,
    TargetProductCode: payload.targetProductCode,
    SupplierCode: payload.supplierCode,
    // 只有用户在预览里确认合并时才带上，兼容旧接口的请求体
    ...(payload.mergeIntoExistingDomesticProduct ? { MergeIntoExistingDomesticProduct: true } : {}),
  };
}

function asNullableText(value: unknown) {
  const text = asString(value).trim();
  return text ? text : null;
}

export function normalizeAlignDomesticProductCodePreview(raw: unknown): AlignDomesticProductCodePreview {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const rawFields = pick<unknown[]>(record, "fields", "Fields");
  return {
    mode: pick<string>(record, "mode", "Mode") === "Merge" ? "Merge" : "Rename",
    oldProductCode: asString(pick(record, "oldProductCode", "OldProductCode")),
    newProductCode: asString(pick(record, "newProductCode", "NewProductCode")),
    affectedContainerDetails: asNumber(pick(record, "affectedContainerDetails", "AffectedContainerDetails"), 0),
    affectedContainers: asNumber(pick(record, "affectedContainers", "AffectedContainers"), 0),
    fields: (Array.isArray(rawFields) ? rawFields : []).filter(isRecord).map((field) => ({
      field: asString(pick(field, "field", "Field")),
      label: asString(pick(field, "label", "Label")) || asString(pick(field, "field", "Field")),
      existingValue: asNullableText(pick(field, "existingValue", "ExistingValue")),
      oldValue: asNullableText(pick(field, "oldValue", "OldValue")),
      mergedValue: asNullableText(pick(field, "mergedValue", "MergedValue")),
      filledFromOld: Boolean(pick(field, "filledFromOld", "FilledFromOld")),
    })),
  };
}

export function normalizeAlignDomesticProductCodeResult(raw: unknown): AlignDomesticProductCodeResult {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const oldProductCode = pick<string>(record, "oldProductCode", "OldProductCode");
  const newProductCode = pick<string>(record, "newProductCode", "NewProductCode");

  const filledFields = pick<unknown[]>(record, "filledFields", "FilledFields");
  return {
    mode: pick<string>(record, "mode", "Mode") === "Merge" ? "Merge" : "Rename",
    filledFields: Array.isArray(filledFields) ? filledFields.map((field) => asString(field)).filter(Boolean) : [],
    oldProductCode: asString(oldProductCode),
    OldProductCode: pick<string>(record, "OldProductCode"),
    newProductCode: asString(newProductCode),
    NewProductCode: pick<string>(record, "NewProductCode"),
    updatedDomesticProducts: asNumber(pick(record, "updatedDomesticProducts", "UpdatedDomesticProducts"), 0),
    UpdatedDomesticProducts: pick<number>(record, "UpdatedDomesticProducts"),
    updatedContainerDetails: asNumber(pick(record, "updatedContainerDetails", "UpdatedContainerDetails"), 0),
    UpdatedContainerDetails: pick<number>(record, "UpdatedContainerDetails"),
    updatedDomesticSetProducts: asNumber(pick(record, "updatedDomesticSetProducts", "UpdatedDomesticSetProducts"), 0),
    UpdatedDomesticSetProducts: pick<number>(record, "UpdatedDomesticSetProducts"),
    updatedProductGrades: asNumber(pick(record, "updatedProductGrades", "UpdatedProductGrades"), 0),
    UpdatedProductGrades: pick<number>(record, "UpdatedProductGrades"),
    updatedDomesticProductCreationLogs: asNumber(pick(record, "updatedDomesticProductCreationLogs", "UpdatedDomesticProductCreationLogs"), 0),
    UpdatedDomesticProductCreationLogs: pick<number>(record, "UpdatedDomesticProductCreationLogs"),
  };
}

export function normalizeDetectionResults(raw: unknown): DetectionResult[] {
  const data = unwrapData(raw);
  return Array.isArray(data) ? (data as DetectionResult[]) : [];
}

export function getContainerGuid(container?: ContainerMain | null) {
  return container?.hguid ?? container?.HGUID ?? "";
}

export function getDetailGuid(detail?: ContainerDetail | null) {
  return detail?.hguid ?? detail?.HGUID ?? "";
}

function firstTrimmedValue(...values: Array<string | undefined>) {
  return values.map((value) => value?.trim()).find((value): value is string => Boolean(value));
}

function normalizeMatchKey(value?: string) {
  return value?.trim().toUpperCase();
}

function normalizeDetailMatchType(value?: string): ContainerDetailQueryMatchType | undefined {
  const normalized = value?.trim().toLowerCase();
  if (!normalized) return undefined;
  if (normalized === "productcode" || normalized === "product_code" || normalized === "商品编码" || normalized === "both") {
    return "productCode";
  }
  if (
    normalized === "supplieritem" ||
    normalized === "supplier_item" ||
    normalized === "item_number" ||
    normalized === "itemnumber" ||
    normalized === "供应商编码+货号" ||
    normalized === "供应商货号" ||
    normalized === "货号匹配"
  ) {
    return "supplierItem";
  }
  return "unmatched";
}

export function getDetailProductCode(detail: ContainerDetail) {
  return firstTrimmedValue(detail.商品编码, detail.商品信息?.商品编码);
}

export function getDetailItemNumber(detail: ContainerDetail) {
  return detail.商品信息?.货号 ?? "";
}

export function getDetailProductName(detail: ContainerDetail) {
  return detail.商品信息?.商品名称 ?? detail.商品名称 ?? "";
}

export function getDetailEnglishName(detail: ContainerDetail) {
  return detail.商品信息?.英文名称 ?? detail.英文名称 ?? "";
}

export function getDetailBarcode(detail: ContainerDetail) {
  return detail.商品信息?.条形码 ?? "";
}

export function getDetailImageUrl(detail?: ContainerDetail | null) {
  // 图片字段可能来自明细行或商品信息，展示和 HQ 推送保持同一优先级。
  return trimToUndefined(detail?.商品图片) ?? trimToUndefined(detail?.商品信息?.商品图片);
}

export function getDetailLocalProductCode(detail: ContainerDetail) {
  return firstTrimmedValue(detail.localProductCode, detail.LocalProductCode);
}

export function getDetailLocalSupplierCode(detail: ContainerDetail) {
  // 对齐编码接口要求供应商编码；历史 HB 数据缺字段时和 Web 端一致回退 200。
  return firstTrimmedValue(detail.localSupplierCode, detail.商品信息?.localSupplierCode) ?? "200";
}

export function getDetailDomesticProductCode(detail: ContainerDetail) {
  return firstTrimmedValue(
    detail.domesticProductCode,
    detail.DomesticProductCode,
    getDetailProductCode(detail),
  );
}

export function hasDetailProductCodeConflict(detail: ContainerDetail) {
  const explicit = detail.hasProductCodeConflict ?? detail.HasProductCodeConflict;
  if (explicit != null) return Boolean(explicit);

  const localProductCode = normalizeMatchKey(getDetailLocalProductCode(detail));
  const domesticProductCode = normalizeMatchKey(getDetailDomesticProductCode(detail));
  return Boolean(localProductCode && domesticProductCode && localProductCode !== domesticProductCode);
}

export function getDetailMatchType(detail: ContainerDetail): ContainerDetailQueryMatchType {
  if (hasDetailProductCodeConflict(detail)) {
    return "supplierItem";
  }

  return normalizeDetailMatchType(detail.matchType ?? detail.MatchType) ?? "unmatched";
}

export function getDetailReadonlyOemPrice(detail: ContainerDetail) {
  // 只读零售价只展示后端分流结果；缺字段时不回退货柜明细业务价。
  return detail.readonlyOemPrice ?? detail.ReadonlyOemPrice;
}

export function getDetailRealtimeImportPrice(detail: ContainerDetail) {
  // 实时进货价来自仓库商品表；缺字段时不回退 LastImportPrice 历史快照。
  return detail.warehouseImportPrice ?? detail.WarehouseImportPrice;
}

export function getDetailRealtimeRetailPrice(detail: ContainerDetail) {
  // 实时零售价来自仓库商品表；缺字段时不回退 LastOEMPrice 或明细零售价。
  return detail.warehouseOEMPrice ?? detail.WarehouseOEMPrice;
}

export function getDetailVisibleOemPrice(detail: ContainerDetail) {
  // 新商品继续使用明细业务价；已有商品按 Web 端展示仓库实时零售价。
  return detail.是否新商品 ? detail.贴牌价格 : getDetailRealtimeRetailPrice(detail);
}

export function buildDetailDetectionItems(details: ContainerDetail[]): DetectionItem[] {
  return details.map((detail) => ({
    ProductCode: getDetailProductCode(detail),
    ItemNumber: trimToUndefined(getDetailItemNumber(detail)),
    Barcode: trimToUndefined(getDetailBarcode(detail)),
    // 检测接口用供应商+货号找候选；历史 HB 缺供应商时按旧规则回退 200。
    SupplierCode: getDetailLocalSupplierCode(detail),
  }));
}

export function mergeDetailDetectionResults(
  details: ContainerDetail[],
  results: DetectionResult[],
): ContainerDetail[] {
  return details.map((detail, index) => {
    const result = results[index];
    if (!result) return detail;

    const localProductCode = firstTrimmedValue(result.localProductCode, result.LocalProductCode);
    const domesticProductCode = firstTrimmedValue(result.domesticProductCode, result.DomesticProductCode);
    const matchType = firstTrimmedValue(result.matchType, result.MatchType);
    const normalizedMatchType = normalizeDetailMatchType(matchType);
    const conflictReason = firstTrimmedValue(result.conflictReason, result.ConflictReason);
    const hasProductCodeConflict = result.hasProductCodeConflict ?? result.HasProductCodeConflict;

    return {
      ...detail,
      matchType: normalizedMatchType ?? detail.matchType,
      MatchType: matchType ?? detail.MatchType,
      localProductCode: localProductCode ?? detail.localProductCode,
      LocalProductCode: localProductCode ?? detail.LocalProductCode,
      domesticProductCode: domesticProductCode ?? detail.domesticProductCode,
      DomesticProductCode: domesticProductCode ?? detail.DomesticProductCode,
      hasProductCodeConflict: hasProductCodeConflict ?? detail.hasProductCodeConflict,
      HasProductCodeConflict: hasProductCodeConflict ?? detail.HasProductCodeConflict,
      conflictReason: conflictReason ?? detail.conflictReason,
      ConflictReason: conflictReason ?? detail.ConflictReason,
    };
  });
}

export function getCurrentPageDetailGuids(details: ContainerDetail[]) {
  return details.map((detail) => getDetailGuid(detail).trim()).filter(Boolean);
}

export function toggleCurrentPageSelection(
  selectedHguids: string[],
  currentPageDetails: ContainerDetail[],
) {
  const pageGuids = getCurrentPageDetailGuids(currentPageDetails);
  if (!pageGuids.length) {
    return selectedHguids.map((item) => item.trim()).filter(Boolean);
  }

  const pageGuidSet = new Set(pageGuids);
  const selectedSet = new Set(selectedHguids.map((item) => item.trim()).filter(Boolean));
  const allPageSelected = pageGuids.every((hguid) => selectedSet.has(hguid));

  if (allPageSelected) {
    // 本页取消只移除当前加载页，保留其他来源的已选项。
    return Array.from(selectedSet).filter((hguid) => !pageGuidSet.has(hguid));
  }

  pageGuids.forEach((hguid) => selectedSet.add(hguid));
  return Array.from(selectedSet);
}

export function buildContainerListPayload(query: ContainerQueryRequest = {}) {
  return {
    DateType: query.dateType || "预计到岸日期",
    StartDate: trimToUndefined(query.startDate),
    EndDate: trimToUndefined(query.endDate),
    LoadingDateStart: trimToUndefined(query.loadingDateStart),
    LoadingDateEnd: trimToUndefined(query.loadingDateEnd),
    EstimatedArrivalDateStart: trimToUndefined(query.estimatedArrivalDateStart),
    EstimatedArrivalDateEnd: trimToUndefined(query.estimatedArrivalDateEnd),
    ActualArrivalDateStart: trimToUndefined(query.actualArrivalDateStart),
    ActualArrivalDateEnd: trimToUndefined(query.actualArrivalDateEnd),
    Page: query.page ?? 1,
    PageSize: query.pageSize ?? CONTAINER_LIST_PAGE_SIZE,
    ItemNumberFilter: trimToUndefined(query.itemNumberFilter),
    ContainerNumberFilter: trimToUndefined(query.containerNumberFilter),
    Statuses: query.statuses?.length ? query.statuses : undefined,
    // 保留 web 端列表的数值区间筛选字段，移动端后续加 UI 时不需要改 API 契约。
    TotalPiecesMin: query.totalPiecesMin,
    TotalPiecesMax: query.totalPiecesMax,
    TotalAmountMin: query.totalAmountMin,
    TotalAmountMax: query.totalAmountMax,
    TotalVolumeMin: query.totalVolumeMin,
    TotalVolumeMax: query.totalVolumeMax,
    SortBy: query.sortBy || query.dateType || "预计到岸日期",
    SortDirection: query.sortDirection || "desc",
  };
}

export function buildContainerDetailQuery(
  containerGuid: string,
  query: Partial<ContainerDetailQuery> & { keyword?: string } = {},
): ContainerDetailQuery {
  const keyword = trimToUndefined(query.keyword);
  return {
    containerGuid,
    pageNumber: query.pageNumber ?? 1,
    pageSize: query.pageSize ?? CONTAINER_DETAIL_PAGE_SIZE,
    itemNumber: trimToUndefined(query.itemNumber) ?? keyword,
    barcode: trimToUndefined(query.barcode),
    productName: trimToUndefined(query.productName),
    englishName: trimToUndefined(query.englishName),
    remark: trimToUndefined(query.remark),
    productTypes: query.productTypes,
    newProductStates: query.newProductStates,
    matchTypes: query.matchTypes,
    warehouseStatus: query.warehouseStatus,
    containerPiecesMin: query.containerPiecesMin,
    containerPiecesMax: query.containerPiecesMax,
    middlePackQuantityMin: query.middlePackQuantityMin,
    middlePackQuantityMax: query.middlePackQuantityMax,
    containerQuantityMin: query.containerQuantityMin,
    containerQuantityMax: query.containerQuantityMax,
    packingQuantityMin: query.packingQuantityMin,
    packingQuantityMax: query.packingQuantityMax,
    unitVolumeMin: query.unitVolumeMin,
    unitVolumeMax: query.unitVolumeMax,
    domesticPriceMin: query.domesticPriceMin,
    domesticPriceMax: query.domesticPriceMax,
    floatRateMin: query.floatRateMin,
    floatRateMax: query.floatRateMax,
    transportCostMin: query.transportCostMin,
    transportCostMax: query.transportCostMax,
    unitTransportCostMin: query.unitTransportCostMin,
    unitTransportCostMax: query.unitTransportCostMax,
    warehouseImportPriceMin: query.warehouseImportPriceMin,
    warehouseImportPriceMax: query.warehouseImportPriceMax,
    lastOEMPriceMin: query.lastOEMPriceMin,
    lastOEMPriceMax: query.lastOEMPriceMax,
    importPriceMin: query.importPriceMin,
    importPriceMax: query.importPriceMax,
    oemPriceMin: query.oemPriceMin,
    oemPriceMax: query.oemPriceMax,
    selectedTags: query.selectedTags?.filter((item) => item !== "all"),
    // 默认按货号升序，和 web 货柜明细当前业务核对顺序保持一致。
    sortBy: query.sortBy || "itemNumber",
    sortOrder: query.sortOrder || "ascend",
    includeTotal: query.includeTotal ?? true,
    includeStats: query.includeStats ?? true,
  };
}

export function buildBatchScope(
  query: ContainerDetailQuery,
  selectedHguids: string[],
) {
  const normalizedSelected = selectedHguids.map((item) => item.trim()).filter(Boolean);
  if (normalizedSelected.length) {
    return { selectedHguids: normalizedSelected };
  }
  return {
    query: {
      ...query,
      includeStats: false,
      includeTotal: false,
    },
  };
}

export function normalizeCreateContainerResponse(raw: unknown) {
  const data = unwrapData(raw);
  if (typeof data === "string") {
    return data;
  }
  if (!isRecord(data)) {
    return "";
  }

  const containerGuid = pick<string>(data, "containerGuid", "ContainerGuid", "hguid", "HGUID");
  return typeof containerGuid === "string" ? containerGuid : "";
}

export function normalizeContainerListResponse(
  raw: unknown,
  fallbackQuery: ContainerQueryRequest = {},
): ContainerListResponse {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const items = pick<ContainerMain[]>(record, "items", "Items", "containers", "Containers") ?? [];
  const page = asNumber(pick(record, "page", "Page"), fallbackQuery.page ?? 1);
  const pageSize = asNumber(pick(record, "pageSize", "PageSize"), fallbackQuery.pageSize ?? CONTAINER_LIST_PAGE_SIZE);
  const total = asNumber(pick(record, "total", "Total", "totalCount", "TotalCount"), items.length);

  return {
    containers: items,
    totalCount: total,
    page,
    pageSize,
    totalPages: pageSize > 0 ? Math.max(1, Math.ceil(total / pageSize)) : 1,
  };
}

export function normalizeContainerDetailResponse(raw: unknown): ContainerMain {
  const data = unwrapData(raw);
  return isRecord(data) ? (data as ContainerMain) : {};
}

export function normalizeContainerDetailQueryResult(
  raw: unknown,
  query: ContainerDetailQuery,
): ContainerDetailQueryResult {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const items = pick<ContainerDetail[]>(record, "items", "Items") ?? [];
  const pageNumber = asNumber(pick(record, "pageNumber", "PageNumber"), query.pageNumber);
  const pageSize = asNumber(pick(record, "pageSize", "PageSize"), query.pageSize);
  const total = asNumber(pick(record, "itemsTotal", "ItemsTotal", "total", "Total"), items.length);
  const tagStats = {
    ...EMPTY_TAG_STATS,
    ...(pick<Record<string, number>>(record, "tagStats", "TagStats") ?? {}),
  };

  return {
    items,
    itemsTotal: total,
    pageNumber,
    pageSize,
    hasMore: Boolean(pick(record, "hasMore", "HasMore") ?? pageNumber * pageSize < total),
    totalComputed: pick<boolean>(record, "totalComputed", "TotalComputed"),
    statsComputed: pick<boolean>(record, "statsComputed", "StatsComputed"),
    tagStats,
  };
}

export function normalizeSyncResult(raw: unknown): SyncResult {
  const data = unwrapData(raw);
  return isRecord(data) ? (data as SyncResult) : {};
}

function normalizeJobStatus(value: unknown): ContainerJobStatus {
  const status = typeof value === "string" ? value.trim().toLowerCase() : "";
  if (status === "queued" || status === "pending") return "Queued";
  if (status === "running" || status === "processing") return "Running";
  if (status === "succeeded" || status === "success" || status === "completed") return "Succeeded";
  if (status === "failed" || status === "failure" || status === "error") return "Failed";
  return "Queued";
}

function asArray<T>(record: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = record[key];
    if (Array.isArray(value)) {
      return value as T[];
    }
  }
  return [];
}

export function normalizeContainerJob(raw: unknown, fallbackJobId = ""): ContainerJob {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const nested = isRecord(record.result) ? record.result : isRecord(record.Result) ? record.Result : {};
  const merged = { ...record, ...nested };
  const result: ContainerJobResult = {
    createdCount: asNumber(pick(merged, "createdCount", "CreatedCount", "created"), 0),
    updatedCount: asNumber(pick(merged, "updatedCount", "UpdatedCount", "updated"), 0),
    skippedCount: asNumber(pick(merged, "skippedCount", "SkippedCount", "skipped"), 0),
    failedCount: asNumber(pick(merged, "failedCount", "FailedCount", "failed", "errorCount"), 0),
    containerCompleted: Boolean(pick(merged, "containerCompleted", "ContainerCompleted") ?? false),
    created: asArray(merged, "created", "Created"),
    updated: asArray(merged, "updated", "Updated"),
    skipped: asArray(merged, "skipped", "Skipped"),
    errors: asArray(merged, "errors", "Errors"),
  };

  return {
    jobId: String(pick(record, "jobId", "JobId") ?? fallbackJobId),
    status: normalizeJobStatus(pick(record, "status", "Status")),
    operationId: pick<string>(record, "operationId", "OperationId"),
    message: pick<string>(record, "message", "Message"),
    result,
  };
}

export function normalizePushProductsToHqJob(raw: unknown, fallbackJobId = ""): PushProductsToHqJob {
  const data = unwrapData(raw);
  const record = isRecord(data) ? data : {};
  const resultRecord = pick<Record<string, unknown>>(record, "result", "Result");
  const result: PushProductsToHqResult | undefined = resultRecord
    ? {
        successCount: asNumber(pick(resultRecord, "successCount", "SuccessCount", "pushedCount"), 0),
        failedCount: asNumber(pick(resultRecord, "failedCount", "FailedCount", "errorCount"), 0),
        totalCount: asNumber(pick(resultRecord, "totalCount", "TotalCount"), 0),
        affectedRowCount: asNumber(pick(resultRecord, "affectedRowCount", "AffectedRowCount"), 0),
        errors: asArray<string>(resultRecord, "errors", "Errors"),
        message: pick<string>(resultRecord, "message", "Message"),
      }
    : undefined;

  return {
    jobId: String(pick(record, "jobId", "JobId") ?? fallbackJobId),
    status: normalizeJobStatus(pick(record, "status", "Status")),
    operationId: pick<string>(record, "operationId", "OperationId"),
    result,
    message: pick<string>(record, "message", "Message"),
    errors: asArray<string>(record, "errors", "Errors"),
  };
}

export function buildCreateProductsOperationId(containerGuid: string, detailHguids: string[]) {
  const details = detailHguids.map((item) => item.trim()).filter(Boolean).sort().join(",");
  return `container-create-products:${containerGuid}:${details || "empty"}`;
}

export function buildSubmitContainerOperationId(containerGuid: string) {
  return `submit-container:${containerGuid.trim()}`;
}

export function buildPushProductsToHqOperationId(
  containerGuid: string,
  productCodes: string[],
  itemCount: number,
  updateFields: PushProductsToHqUpdateField[] = [],
) {
  const codes = productCodes.map((item) => item.trim()).filter(Boolean).sort().join(",");
  const fields = updateFields.map((item) => item.trim()).filter(Boolean).sort().join(",");
  return `container-push-hq:${containerGuid || "unknown"}:${codes || "items"}:${itemCount}:${fields || "all"}`;
}

export function buildContainerDetailHqPushSelection(details: ContainerDetail[]): ContainerDetailHqPushSelection {
  const productCodes: string[] = [];
  const items: PushProductsToHqItem[] = [];
  const seen = new Set<string>();

  details.forEach((detail) => {
    const hasConflict = hasDetailProductCodeConflict(detail);
    const productCode = hasConflict ? undefined : getDetailProductCode(detail);
    const rowSupplierCode = trimToUndefined(detail.localSupplierCode) ?? trimToUndefined(detail.商品信息?.localSupplierCode);
    const localSupplierCode = productCode ? rowSupplierCode : getDetailLocalSupplierCode(detail);
    const itemNumber = trimToUndefined(getDetailItemNumber(detail));
    if (!productCode && !(localSupplierCode && itemNumber)) {
      return;
    }

    const key = productCode
      ? `code:${productCode.toUpperCase()}`
      : `supplier-item:${localSupplierCode!.toUpperCase()}:${itemNumber!.toUpperCase()}`;
    if (seen.has(key)) return;
    seen.add(key);

    if (productCode) productCodes.push(productCode);

    // 编码冲突未人工对齐前，只把供应商+货号作为候选交给后端实时解析。
    items.push({
      productCode,
      localSupplierCode,
      itemNumber,
      productName: getDetailProductName(detail),
      englishName: getDetailEnglishName(detail),
      barcode: getDetailBarcode(detail),
      imageUrl: getDetailImageUrl(detail),
      domesticPrice: detail.国内价格,
      importPrice: detail.进口价格,
      oemPrice: getDetailVisibleOemPrice(detail),
      isNewProduct: Boolean(detail.是否新商品 ?? detail.warehouseIsActive === false),
      warehouseIsActive: detail.warehouseIsActive,
    });
  });

  return { productCodes, items };
}

export function toPushProductsToHqItems(details: ContainerDetail[]): PushProductsToHqItem[] {
  return details.map((detail) => ({
    productCode: getDetailProductCode(detail),
    localSupplierCode: detail.localSupplierCode ?? detail.商品信息?.localSupplierCode,
    itemNumber: getDetailItemNumber(detail),
    productName: getDetailProductName(detail),
    englishName: getDetailEnglishName(detail),
    barcode: getDetailBarcode(detail),
    imageUrl: getDetailImageUrl(detail),
    domesticPrice: detail.国内价格,
    importPrice: detail.进口价格,
    oemPrice: getDetailVisibleOemPrice(detail),
    isNewProduct: Boolean(detail.是否新商品 ?? detail.warehouseIsActive === false),
    warehouseIsActive: detail.warehouseIsActive,
  }));
}

export function toggleSelectedTag(
  selectedTags: ContainerDetailQueryTag[],
  tag: ContainerDetailQueryTag,
) {
  if (tag === "all") {
    return [];
  }
  return selectedTags.includes(tag)
    ? selectedTags.filter((item) => item !== tag)
    : [...selectedTags, tag];
}
