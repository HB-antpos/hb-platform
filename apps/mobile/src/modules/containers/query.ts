import type {
  AlignDomesticProductCodePreview,
  AlignDomesticProductCodeRequest,
  AlignDomesticProductCodeResult,
  ContainerDetail,
  ContainerDetailFilterState,
  ContainerDetailHqPushSelection,
  ContainerDetailOverview,
  ContainerDetailPageSize,
  ContainerDetailQuery,
  ContainerDetailQueryMatchType,
  ContainerDetailQueryProductType,
  ContainerDetailQueryResult,
  ContainerDetailQuerySortOrder,
  ContainerDetailQueryTag,
  ContainerDetailQueryWarehouseStatus,
  ContainerDetailRangeFilterKey,
  ContainerDetailSearchField,
  ContainerDetailSort,
  ContainerDetailSortField,
  ContainerDetailTagStats,
  CreatedProductsHqPushPlan,
  CreatedProductsHqPushWarning,
  MissingRetailPriceDetail,
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
import { getArrivalInsight, toDateOnly } from "./container-list-logic";

export const CONTAINER_LIST_PAGE_SIZE = 20;

/** 明细每页条数可选项；后端 pageSize 上限 1000，这里取常用档位。 */
export const CONTAINER_DETAIL_PAGE_SIZE_OPTIONS = [50, 100, 200, 500] as const;
export const CONTAINER_DETAIL_DEFAULT_PAGE_SIZE: ContainerDetailPageSize = 50;
/** @deprecated 旧名，等同默认每页条数（已由 30 改为 50）；新代码请用 CONTAINER_DETAIL_DEFAULT_PAGE_SIZE。 */
export const CONTAINER_DETAIL_PAGE_SIZE = CONTAINER_DETAIL_DEFAULT_PAGE_SIZE;

// 本地记住的每页条数可能来自旧版本或被篡改，不在可选项里就回到默认 50
export function normalizeContainerDetailPageSize(value: unknown): ContainerDetailPageSize {
  const parsed = typeof value === "string" ? Number(value) : value;
  return (
    CONTAINER_DETAIL_PAGE_SIZE_OPTIONS.find((option) => option === parsed) ?? CONTAINER_DETAIL_DEFAULT_PAGE_SIZE
  );
}

/** 页数（至少 1 页）。 */
export function getContainerDetailPageCount(total: number, pageSize: number) {
  if (!Number.isFinite(total) || !Number.isFinite(pageSize) || pageSize <= 0) return 1;
  return Math.max(1, Math.ceil(total / pageSize));
}

/** 搜索字段：keyword 会写入后端对应的单列文字筛选，默认按货号。 */
export const CONTAINER_DETAIL_SEARCH_FIELDS: readonly {
  field: ContainerDetailSearchField;
  label: string;
  labelKey: string;
}[] = [
  { field: "itemNumber", label: "货号", labelKey: "containerDetail.searchField.itemNumber" },
  { field: "productName", label: "中文名", labelKey: "containerDetail.searchField.productName" },
  { field: "barcode", label: "条码", labelKey: "containerDetail.searchField.barcode" },
  { field: "englishName", label: "英文名", labelKey: "containerDetail.searchField.englishName" },
];
export const DEFAULT_CONTAINER_DETAIL_SEARCH_FIELD: ContainerDetailSearchField = "itemNumber";

export function normalizeContainerDetailSearchField(value: unknown): ContainerDetailSearchField {
  return CONTAINER_DETAIL_SEARCH_FIELDS.find((item) => item.field === value)?.field ?? DEFAULT_CONTAINER_DETAIL_SEARCH_FIELD;
}

/**
 * 移动端提供的排序项。field 全部取自后端 ApplyContainerDetailSort 白名单；
 * defaultOrder 是首次点选该字段时的方向（文字升序、数值/价格降序，方便先看大的）。
 * 后端没有「最近更新」排序，因此不提供。
 */
export const CONTAINER_DETAIL_SORT_OPTIONS: readonly {
  field: ContainerDetailSortField;
  label: string;
  labelKey: string;
  defaultOrder: ContainerDetailQuerySortOrder;
}[] = [
  { field: "itemNumber", label: "货号", labelKey: "containerDetail.sort.itemNumber", defaultOrder: "ascend" },
  { field: "productName", label: "中文名", labelKey: "containerDetail.sort.productName", defaultOrder: "ascend" },
  { field: "englishName", label: "英文名", labelKey: "containerDetail.sort.englishName", defaultOrder: "ascend" },
  { field: "barcode", label: "条码", labelKey: "containerDetail.sort.barcode", defaultOrder: "ascend" },
  { field: "containerQuantity", label: "装柜数量", labelKey: "containerDetail.sort.containerQuantity", defaultOrder: "descend" },
  { field: "containerPieces", label: "装柜件数", labelKey: "containerDetail.sort.containerPieces", defaultOrder: "descend" },
  { field: "packingQuantity", label: "单件装箱数", labelKey: "containerDetail.sort.packingQuantity", defaultOrder: "descend" },
  { field: "unitVolume", label: "单件体积", labelKey: "containerDetail.sort.unitVolume", defaultOrder: "descend" },
  { field: "domesticPrice", label: "国内价", labelKey: "containerDetail.sort.domesticPrice", defaultOrder: "descend" },
  { field: "importPrice", label: "进口价", labelKey: "containerDetail.sort.importPrice", defaultOrder: "descend" },
  { field: "warehouseImportPrice", label: "实时进货价", labelKey: "containerDetail.sort.warehouseImportPrice", defaultOrder: "descend" },
  { field: "oemPrice", label: "零售价", labelKey: "containerDetail.sort.oemPrice", defaultOrder: "descend" },
  { field: "newProduct", label: "新品优先", labelKey: "containerDetail.sort.newProduct", defaultOrder: "descend" },
  { field: "warehouseStatus", label: "上下架", labelKey: "containerDetail.sort.warehouseStatus", defaultOrder: "descend" },
];

/** 默认按货号升序，和 web 货柜明细当前业务核对顺序保持一致。 */
export const DEFAULT_CONTAINER_DETAIL_SORT: ContainerDetailSort = { field: "itemNumber", order: "ascend" };

export function isContainerDetailSortField(value: unknown): value is ContainerDetailSortField {
  return CONTAINER_DETAIL_SORT_OPTIONS.some((item) => item.field === value);
}

/** 不认识的字段回退默认（后端对未知 key 也会静默回退货号排序，这里提前显式化）。 */
export function normalizeContainerDetailSort(sort?: Partial<ContainerDetailSort> | null): ContainerDetailSort {
  const requestedField = sort?.field;
  const field = isContainerDetailSortField(requestedField) ? requestedField : DEFAULT_CONTAINER_DETAIL_SORT.field;
  const order: ContainerDetailQuerySortOrder = sort?.order === "descend" || sort?.order === "ascend"
    ? sort.order
    : DEFAULT_CONTAINER_DETAIL_SORT.order;
  return { field, order };
}

/** 点选排序项：同一字段再次点选翻转方向，换字段则用该字段的默认方向。 */
export function toggleContainerDetailSort(current: ContainerDetailSort, field: ContainerDetailSortField): ContainerDetailSort {
  if (current.field === field) {
    return { field, order: current.order === "ascend" ? "descend" : "ascend" };
  }
  const option = CONTAINER_DETAIL_SORT_OPTIONS.find((item) => item.field === field);
  return { field, order: option?.defaultOrder ?? "ascend" };
}

export function isDefaultContainerDetailSort(sort: ContainerDetailSort) {
  return sort.field === DEFAULT_CONTAINER_DETAIL_SORT.field && sort.order === DEFAULT_CONTAINER_DETAIL_SORT.order;
}

// ---------------------------------------------------------------------------
// 筛选面板状态：区间 + 商品类型 + 仓库上下架 + 匹配方式
// ---------------------------------------------------------------------------

/** 区间筛选对（顺序即界面展示顺序），min/max 为 ContainerDetailQuery 里的同名字段。 */
export const CONTAINER_DETAIL_RANGE_FILTER_PAIRS: readonly {
  key: "containerQuantity" | "middlePackQuantity" | "warehouseImportPrice" | "oemPrice";
  label: string;
  minKey: ContainerDetailRangeFilterKey;
  maxKey: ContainerDetailRangeFilterKey;
}[] = [
  { key: "containerQuantity", label: "装柜数量", minKey: "containerQuantityMin", maxKey: "containerQuantityMax" },
  { key: "middlePackQuantity", label: "中包数", minKey: "middlePackQuantityMin", maxKey: "middlePackQuantityMax" },
  { key: "warehouseImportPrice", label: "实时进货价", minKey: "warehouseImportPriceMin", maxKey: "warehouseImportPriceMax" },
  { key: "oemPrice", label: "零售价", minKey: "oemPriceMin", maxKey: "oemPriceMax" },
];

export const CONTAINER_DETAIL_PRODUCT_TYPE_OPTIONS: readonly { value: ContainerDetailQueryProductType; label: string }[] = [
  { value: "normal", label: "普通商品" },
  { value: "set", label: "套装商品" },
  { value: "multi", label: "多码商品" },
  { value: "setChild", label: "套装子商品" },
];

export const CONTAINER_DETAIL_WAREHOUSE_STATUS_OPTIONS: readonly { value: ContainerDetailQueryWarehouseStatus; label: string }[] = [
  { value: "active", label: "上架" },
  { value: "inactive", label: "下架" },
];

export const CONTAINER_DETAIL_MATCH_TYPE_OPTIONS: readonly { value: ContainerDetailQueryMatchType; label: string }[] = [
  { value: "productCode", label: "商品编码匹配" },
  { value: "supplierItem", label: "候选需确认" },
  { value: "unmatched", label: "未匹配" },
];

export function createEmptyContainerDetailFilters(): ContainerDetailFilterState {
  return {
    ranges: {
      containerQuantityMin: "",
      containerQuantityMax: "",
      middlePackQuantityMin: "",
      middlePackQuantityMax: "",
      warehouseImportPriceMin: "",
      warehouseImportPriceMax: "",
      oemPriceMin: "",
      oemPriceMax: "",
    },
    productTypes: [],
    warehouseStatus: [],
    matchTypes: [],
  };
}

/** 重置：返回一份全新的空状态（不共享引用，避免被 setState 误改）。 */
export function resetContainerDetailFilters(): ContainerDetailFilterState {
  return createEmptyContainerDetailFilters();
}

/** 空串或空白 -> undefined；非法数字 -> NaN，由校验函数统一报错。 */
function parseFilterNumber(value: string | undefined) {
  const trimmed = value?.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

/**
 * 已生效的筛选条件个数（用于筛选按钮角标）：
 * 每个区间对（只要填了 min 或 max）算 1 个，三组多选各算 1 个；标签 chips 与搜索词不计入。
 */
export function countActiveContainerDetailFilters(state: ContainerDetailFilterState): number {
  const rangeCount = CONTAINER_DETAIL_RANGE_FILTER_PAIRS.filter(
    (pair) => Boolean(state.ranges[pair.minKey]?.trim()) || Boolean(state.ranges[pair.maxKey]?.trim()),
  ).length;
  return (
    rangeCount
    + (state.productTypes.length > 0 ? 1 : 0)
    + (state.warehouseStatus.length > 0 ? 1 : 0)
    + (state.matchTypes.length > 0 ? 1 : 0)
  );
}

/**
 * 校验区间输入：返回有问题的区间对 key（填了非数字，或 min 大于 max）。
 * 为空数组才允许应用筛选。
 */
export function findInvalidContainerDetailRangePairs(state: ContainerDetailFilterState) {
  return CONTAINER_DETAIL_RANGE_FILTER_PAIRS.filter((pair) => {
    const min = parseFilterNumber(state.ranges[pair.minKey]);
    const max = parseFilterNumber(state.ranges[pair.maxKey]);
    if (Number.isNaN(min) || Number.isNaN(max)) return true;
    return min !== undefined && max !== undefined && min > max;
  }).map((pair) => pair.key);
}

/** 把筛选状态转成 ContainerDetailQuery 字段；非法数字一律丢弃（界面应先用校验函数拦截）。 */
export function buildContainerDetailFilterQuery(state: ContainerDetailFilterState): Partial<ContainerDetailQuery> {
  const numberOrUndefined = (key: ContainerDetailRangeFilterKey) => {
    const parsed = parseFilterNumber(state.ranges[key]);
    return parsed === undefined || Number.isNaN(parsed) ? undefined : parsed;
  };
  return {
    containerQuantityMin: numberOrUndefined("containerQuantityMin"),
    containerQuantityMax: numberOrUndefined("containerQuantityMax"),
    middlePackQuantityMin: numberOrUndefined("middlePackQuantityMin"),
    middlePackQuantityMax: numberOrUndefined("middlePackQuantityMax"),
    warehouseImportPriceMin: numberOrUndefined("warehouseImportPriceMin"),
    warehouseImportPriceMax: numberOrUndefined("warehouseImportPriceMax"),
    oemPriceMin: numberOrUndefined("oemPriceMin"),
    oemPriceMax: numberOrUndefined("oemPriceMax"),
    productTypes: state.productTypes.length ? [...state.productTypes] : undefined,
    warehouseStatus: state.warehouseStatus.length ? [...state.warehouseStatus] : undefined,
    matchTypes: state.matchTypes.length ? [...state.matchTypes] : undefined,
  };
}

/** 多选项开关（商品类型 / 上下架 / 匹配方式共用）。 */
export function toggleContainerDetailFilterOption<T extends string>(selected: readonly T[], value: T): T[] {
  return selected.includes(value) ? selected.filter((item) => item !== value) : [...selected, value];
}

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

export type BuildContainerDetailQueryInput = Partial<ContainerDetailQuery> & {
  /** 搜索词，写入 searchField 对应的后端字段（默认货号）；该字段已显式传值时以显式值为准 */
  keyword?: string;
  searchField?: ContainerDetailSearchField;
  /** 排序；优先于 sortBy/sortOrder */
  sort?: Partial<ContainerDetailSort> | null;
  /** 筛选面板状态（区间 + 商品类型 + 上下架 + 匹配方式）；同名显式字段优先 */
  filters?: ContainerDetailFilterState;
};

export function buildContainerDetailQuery(
  containerGuid: string,
  query: BuildContainerDetailQueryInput = {},
): ContainerDetailQuery {
  const keyword = trimToUndefined(query.keyword);
  const searchField = normalizeContainerDetailSearchField(query.searchField);
  // 后端按单列文字筛选，搜索词只落到当前选中的字段，其余字段不受影响。
  const textFilter = (field: ContainerDetailSearchField) =>
    trimToUndefined(query[field]) ?? (searchField === field ? keyword : undefined);
  const filterQuery = query.filters ? buildContainerDetailFilterQuery(query.filters) : {};
  const sort = query.sort
    ? normalizeContainerDetailSort(query.sort)
    : {
        field: query.sortBy || DEFAULT_CONTAINER_DETAIL_SORT.field,
        order: query.sortOrder || DEFAULT_CONTAINER_DETAIL_SORT.order,
      };
  // 显式字段优先于 filters 展开值（只在显式字段不是 undefined 时覆盖）。
  const fromQueryOrFilters = <K extends keyof ContainerDetailQuery>(key: K): ContainerDetailQuery[K] =>
    (query[key] !== undefined ? query[key] : filterQuery[key]) as ContainerDetailQuery[K];
  return {
    containerGuid,
    pageNumber: query.pageNumber ?? 1,
    pageSize: query.pageSize ?? CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
    itemNumber: textFilter("itemNumber"),
    barcode: textFilter("barcode"),
    productName: textFilter("productName"),
    englishName: textFilter("englishName"),
    remark: trimToUndefined(query.remark),
    productTypes: fromQueryOrFilters("productTypes"),
    newProductStates: query.newProductStates,
    matchTypes: fromQueryOrFilters("matchTypes"),
    warehouseStatus: fromQueryOrFilters("warehouseStatus"),
    containerPiecesMin: query.containerPiecesMin,
    containerPiecesMax: query.containerPiecesMax,
    middlePackQuantityMin: fromQueryOrFilters("middlePackQuantityMin"),
    middlePackQuantityMax: fromQueryOrFilters("middlePackQuantityMax"),
    containerQuantityMin: fromQueryOrFilters("containerQuantityMin"),
    containerQuantityMax: fromQueryOrFilters("containerQuantityMax"),
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
    warehouseImportPriceMin: fromQueryOrFilters("warehouseImportPriceMin"),
    warehouseImportPriceMax: fromQueryOrFilters("warehouseImportPriceMax"),
    lastOEMPriceMin: query.lastOEMPriceMin,
    lastOEMPriceMax: query.lastOEMPriceMax,
    importPriceMin: query.importPriceMin,
    importPriceMax: query.importPriceMax,
    oemPriceMin: fromQueryOrFilters("oemPriceMin"),
    oemPriceMax: fromQueryOrFilters("oemPriceMax"),
    selectedTags: query.selectedTags?.filter((item) => item !== "all"),
    sortBy: sort.field,
    sortOrder: sort.order,
    // 分页请求必须带总数与标签统计，页数和概览卡都依赖它们。
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

// ---------------------------------------------------------------------------
// 创建新商品：零售价前置校验 + 「创建后同步 HQ」发送计划
// ---------------------------------------------------------------------------

/** 发送到 HQ 的全部可更新字段（与 Web defaultPushProductsToHqUpdateFields 同序，共 17 项）。 */
export const DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS: readonly PushProductsToHqUpdateField[] = [
  "itemNumber",
  "barcode",
  "productName",
  "englishName",
  "productType",
  "image",
  "purchasePrice",
  "retailPrice",
  "middlePackQuantity",
  "supplierCode",
  "storePurchasePrice",
  "storeRetailPrice",
  "inventoryDomesticPrice",
  "inventoryImportPrice",
  "inventoryOemPrice",
  "productSetCodes",
  "storeMultiCodes",
];

/**
 * 找出「新商品但零售价无效」的明细。
 * 创建仓库新商品会把明细零售价（贴牌价格）写入商品主表、仓库商品和分店零售价，所以必须是有限正数；
 * 已有商品不参与（它们不会被创建）。与 Web findContainerDetailRowsMissingCreateProductRetailPrice 同口径。
 */
export function findContainerDetailsMissingRetailPrice(details: readonly ContainerDetail[]): MissingRetailPriceDetail[] {
  return details
    .filter((detail) => Boolean(detail.是否新商品))
    .map((detail) => ({
      hguid: getDetailGuid(detail),
      label:
        trimToUndefined(getDetailItemNumber(detail))
        ?? trimToUndefined(getDetailProductCode(detail))
        ?? getDetailGuid(detail),
      retailPrice: detail.贴牌价格,
    }))
    .filter((row) => !(typeof row.retailPrice === "number" && Number.isFinite(row.retailPrice) && row.retailPrice > 0));
}

function normalizeLookupKey(value?: string) {
  return value?.trim().toUpperCase() || undefined;
}

function buildDetailLookupIndexes(details: readonly ContainerDetail[]) {
  const byHguid = new Map<string, ContainerDetail>();
  const byProductCode = new Map<string, ContainerDetail>();
  details.forEach((detail) => {
    const hguidKey = normalizeLookupKey(getDetailGuid(detail));
    if (hguidKey && !byHguid.has(hguidKey)) byHguid.set(hguidKey, detail);
    const codeKey = normalizeLookupKey(getDetailProductCode(detail));
    if (codeKey && !byProductCode.has(codeKey)) byProductCode.set(codeKey, detail);
  });
  return { byHguid, byProductCode };
}

/**
 * 「创建新商品」完成后同步 HQ：只从本次结果 created 里挑商品，构造与手动「发送到 HQ」相同的发送选择。
 * - 先按明细 GUID、再按商品编码（忽略大小写与首尾空白）在重载后的最新行里找；
 * - 找不到（分页只加载了部分行、被当前筛选隐藏）时回退到确认创建时的行；
 * - 不因行仍被标成新商品而跳过：后端只信任本地 Product 的实时匹配结果，前端标记可能滞后；
 * - 同一明细或同一商品编码只发送一次（buildContainerDetailHqPushSelection 内按编码去重）。
 * warnings：存在没能进入候选的新建商品，或已有推送任务在途时返回，由界面翻译成提示。
 */
export function buildCreatedProductsHqPushPlan(
  createdItems: readonly { detailHguid?: string; productCode?: string }[],
  latestDetails: readonly ContainerDetail[],
  confirmedDetails: readonly ContainerDetail[] = [],
  options: { pushInFlight?: boolean } = {},
): CreatedProductsHqPushPlan {
  // 最新行优先，确认创建时的行只作兜底。
  const sources = [buildDetailLookupIndexes(latestDetails), buildDetailLookupIndexes(confirmedDetails)];
  const matchedDetails: ContainerDetail[] = [];
  const matchedSet = new Set<ContainerDetail>();
  let unmatchedCount = 0;

  createdItems.forEach((item) => {
    const hguidKey = normalizeLookupKey(item.detailHguid);
    const codeKey = normalizeLookupKey(item.productCode);
    let detail: ContainerDetail | undefined;
    for (const source of sources) {
      detail = (hguidKey ? source.byHguid.get(hguidKey) : undefined)
        ?? (codeKey ? source.byProductCode.get(codeKey) : undefined);
      if (detail) break;
    }
    if (!detail) {
      unmatchedCount += 1;
      return;
    }
    if (!matchedSet.has(detail)) {
      matchedSet.add(detail);
      matchedDetails.push(detail);
    }
  });

  const selection = buildContainerDetailHqPushSelection(matchedDetails);
  // 既没有商品编码、又缺供应商+货号的行，selection 里一条候选都不会产生，要计入「未能发送」。
  const withoutCandidateCount = matchedDetails.filter(
    (detail) => buildContainerDetailHqPushSelection([detail]).items.length === 0,
  ).length;
  const unsentCreatedCount = unmatchedCount + withoutCandidateCount;

  const warnings: CreatedProductsHqPushWarning[] = [];
  if (createdItems.length > 0 && (unsentCreatedCount > 0 || !selection.items.length)) {
    warnings.push({
      code: "UNSENT_CREATED",
      count: selection.items.length ? unsentCreatedCount : createdItems.length,
    });
  }
  const hasCandidates = selection.items.length > 0;
  if (hasCandidates && options.pushInFlight) {
    warnings.push({ code: "PUSH_BUSY" });
  }

  return {
    selection,
    unsentCreatedCount,
    shouldPush: hasCandidates && !options.pushInFlight,
    warnings,
  };
}

// ---------------------------------------------------------------------------
// 货柜头部概览
// ---------------------------------------------------------------------------

/** 标准柜体积（m3），与 Web containersLogic.STANDARD_CONTAINER_VOLUME_CBM 一致。 */
export const STANDARD_CONTAINER_VOLUME_CBM = 68;

function finiteOrUndefined(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/** 装载率（%）：体积缺失/为负返回 undefined；超过 100 照实返回，进度条由界面封顶。 */
export function getContainerLoadRatePercent(volume?: number | null) {
  const value = finiteOrUndefined(volume);
  if (value === undefined || value < 0) return undefined;
  return Math.round((value / STANDARD_CONTAINER_VOLUME_CBM) * 100);
}

/**
 * 概览卡数据：来自 GET /containers/{guid}（金额/体积/日期）和明细 tagStats（新品/已有/行数）。
 * today 为 YYYY-MM-DD，传入才计算到库提示。没有数据的字段保持 undefined，由界面显示 "--"。
 * 注意：后端没有「货柜容量」字段，装载率只能按 68m3 标准柜折算。
 */
export function buildContainerDetailOverview(
  container: ContainerMain | null | undefined,
  tagStats: ContainerDetailTagStats | null | undefined,
  options: { today?: string } = {},
): ContainerDetailOverview {
  const loadingDate = toDateOnly(container?.装柜日期) || undefined;
  const etaDate = toDateOnly(container?.预计到岸日期) || undefined;
  const actualArrivalDate = toDateOnly(container?.实际到货日期) || undefined;
  const totalVolume = finiteOrUndefined(container?.总体积);

  return {
    totalAmount: finiteOrUndefined(container?.合计金额),
    totalVolume,
    loadRatePercent: getContainerLoadRatePercent(totalVolume),
    totalPieces: finiteOrUndefined(container?.合计件数),
    totalQuantity: finiteOrUndefined(container?.合计数量),
    freight: finiteOrUndefined(container?.运费),
    exchangeRate: finiteOrUndefined(container?.汇率),
    loadingDate,
    etaDate,
    actualArrivalDate,
    arrivalInsight: container && options.today ? getArrivalInsight(container, options.today) : null,
    newCount: tagStats ? tagStats.new : undefined,
    existingCount: tagStats ? tagStats.existing : undefined,
    rowCount: tagStats ? tagStats.all : undefined,
    statsMissing: !tagStats,
  };
}

/**
 * 概览卡展示的是整柜构成，不能随搜索词/筛选面板变化：
 * 后端 tagStats 基于当前搜索与筛选范围（不含标签 chips），所以只有「无搜索词且无筛选」时它才等于整柜口径。
 * 其他时刻沿用上一次拿到的整柜统计；cacheable=true 时调用方应把 stats 记为该货柜的整柜统计缓存。
 */
export function resolveContainerDetailOverviewStats({
  remoteStats,
  statsComputed,
  hasScopeFilters,
  cachedStats,
}: {
  remoteStats: ContainerDetailTagStats | null | undefined;
  statsComputed?: boolean;
  hasScopeFilters: boolean;
  cachedStats: ContainerDetailTagStats | null | undefined;
}): { stats: ContainerDetailTagStats | null; cacheable: boolean } {
  if (remoteStats && statsComputed !== false && !hasScopeFilters) {
    return { stats: remoteStats, cacheable: true };
  }
  return { stats: cachedStats ?? null, cacheable: false };
}
