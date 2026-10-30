import {
  DEFAULT_INVOICE_DATE_FIELD,
  type InvoiceDateField,
  type InvoiceDetailPageSize,
  type InvoiceDetailPriceChangeFilter,
  type InvoiceGridFilters,
  type InvoiceGridSort,
  type InvoiceListPageSize,
} from "./types";

export const LOCAL_SUPPLIER_INVOICES_SOURCE = "local-supplier-invoices";

const LIST_PAGE_SIZES: InvoiceListPageSize[] = [20, 50, 100];
const DETAIL_PAGE_SIZES: InvoiceDetailPageSize[] = [50, 100, 200];
const SORT_COLUMNS: InvoiceGridSort["colId"][] = [
  "CreatedAt",
  "OrderDate",
  "InvoiceNo",
  "StoreName",
  "SupplierName",
];
const SORT_DIRECTIONS: InvoiceGridSort["direction"][] = ["asc", "desc"];

type SearchParamValue = string | string[] | undefined;

export interface LocalSupplierInvoicesReturnState {
  source: typeof LOCAL_SUPPLIER_INVOICES_SOURCE;
  returnInvoiceGuid: string;
  returnDetailGuid?: string;
  returnDetailsPage: number;
  returnDetailsPageSize: InvoiceDetailPageSize;
  returnDetailPriceChangeFilter: InvoiceDetailPriceChangeFilter;
  returnDetailSearch: string;
  returnListPage: number;
  returnListPageSize: InvoiceListPageSize;
  filters: InvoiceGridFilters;
  sort: InvoiceGridSort;
}

export interface BuildLocalSupplierInvoicesReturnStateInput {
  returnInvoiceGuid: string;
  returnDetailGuid?: string;
  returnDetailsPage: number;
  returnDetailsPageSize: InvoiceDetailPageSize;
  returnDetailPriceChangeFilter: InvoiceDetailPriceChangeFilter;
  returnDetailSearch: string;
  returnListPage: number;
  returnListPageSize: InvoiceListPageSize;
  filters: InvoiceGridFilters;
  sort: InvoiceGridSort;
}

export interface LocalSupplierInvoicesRestoreHref {
  pathname: "/(shell)/local-supplier-invoices";
  params: Record<string, string>;
}

function firstParam(value: SearchParamValue) {
  const raw = Array.isArray(value) ? value[0] : value;
  const trimmed = raw?.trim();
  return trimmed || undefined;
}

function normalizePositiveInteger(value: SearchParamValue) {
  const raw = firstParam(value);
  if (!raw) {
    return null;
  }

  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function normalizeListPageSize(value: SearchParamValue) {
  const parsed = normalizePositiveInteger(value);
  return parsed != null && LIST_PAGE_SIZES.includes(parsed as InvoiceListPageSize)
    ? parsed as InvoiceListPageSize
    : null;
}

function normalizeDetailPageSize(value: SearchParamValue) {
  const parsed = normalizePositiveInteger(value);
  return parsed != null && DETAIL_PAGE_SIZES.includes(parsed as InvoiceDetailPageSize)
    ? parsed as InvoiceDetailPageSize
    : null;
}

function normalizeDetailPriceChangeFilter(
  value: SearchParamValue
): InvoiceDetailPriceChangeFilter {
  const normalized = firstParam(value)?.toLocaleLowerCase();
  return normalized === "up" || normalized === "down" ? normalized : "all";
}

function normalizeSortColId(value: SearchParamValue) {
  const raw = firstParam(value);
  return raw && SORT_COLUMNS.includes(raw as InvoiceGridSort["colId"])
    ? raw as InvoiceGridSort["colId"]
    : null;
}

function normalizeSortDirection(value: SearchParamValue) {
  const raw = firstParam(value);
  return raw && SORT_DIRECTIONS.includes(raw as InvoiceGridSort["direction"])
    ? raw as InvoiceGridSort["direction"]
    : null;
}

function normalizeDateField(value: unknown): InvoiceDateField {
  return value === "orderDate" ? "orderDate" : DEFAULT_INVOICE_DATE_FIELD;
}

/** 回跳参数里的布尔筛选：只认 "true" / "false"，其余（含空串）当作「不筛选」。 */
function parseOptionalBoolean(value: SearchParamValue) {
  const raw = firstParam(value);
  return raw === "true" ? true : raw === "false" ? false : undefined;
}

function normalizeFilters(filters: InvoiceGridFilters): InvoiceGridFilters {
  const inboundStatus = filters.inboundStatus;
  return {
    storeCode: firstParam(filters.storeCode),
    supplierCode: firstParam(filters.supplierCode),
    invoiceNo: firstParam(filters.invoiceNo),
    inboundStatus:
      inboundStatus === 0 || inboundStatus === 1 || inboundStatus === 2
        ? inboundStatus
        : undefined,
    dateField: normalizeDateField(filters.dateField),
    dateFrom: firstParam(filters.dateFrom),
    dateTo: firstParam(filters.dateTo),
    productChecked: typeof filters.productChecked === "boolean" ? filters.productChecked : undefined,
    // 关闭时统一为 undefined（false 与不筛选等价），避免回跳前后筛选对象不相等。
    hasUncreatedProducts: filters.hasUncreatedProducts ? true : undefined,
    productKeyword: firstParam(filters.productKeyword),
  };
}

export function buildLocalSupplierInvoicesReturnParams(
  input: BuildLocalSupplierInvoicesReturnStateInput
) {
  const filters = normalizeFilters(input.filters);

  return {
    source: LOCAL_SUPPLIER_INVOICES_SOURCE,
    returnInvoiceGuid: input.returnInvoiceGuid,
    returnDetailGuid: input.returnDetailGuid?.trim() ?? "",
    returnDetailsPage: String(input.returnDetailsPage),
    returnDetailsPageSize: String(input.returnDetailsPageSize),
    returnDetailPriceChangeFilter: input.returnDetailPriceChangeFilter,
    returnDetailSearch: input.returnDetailSearch.trim(),
    returnListPage: String(input.returnListPage),
    returnListPageSize: String(input.returnListPageSize),
    returnFilterStoreCode: filters.storeCode ?? "",
    returnFilterSupplierCode: filters.supplierCode ?? "",
    returnFilterInvoiceNo: filters.invoiceNo ?? "",
    returnFilterInboundStatus: filters.inboundStatus == null ? "" : String(filters.inboundStatus),
    returnFilterDateField: filters.dateField ?? DEFAULT_INVOICE_DATE_FIELD,
    returnFilterDateFrom: filters.dateFrom ?? "",
    returnFilterDateTo: filters.dateTo ?? "",
    returnFilterProductChecked: filters.productChecked == null ? "" : String(filters.productChecked),
    returnFilterHasUncreatedProducts: filters.hasUncreatedProducts ? "true" : "",
    returnFilterProductKeyword: filters.productKeyword ?? "",
    returnSortColId: input.sort.colId,
    returnSortDirection: input.sort.direction,
  };
}

export function decodeLocalSupplierInvoicesReturnParams(
  params: Record<string, SearchParamValue>
): LocalSupplierInvoicesReturnState | null {
  const source = firstParam(params.source);
  const returnInvoiceGuid = firstParam(params.returnInvoiceGuid);
  const returnDetailsPage = normalizePositiveInteger(params.returnDetailsPage);
  const returnDetailsPageSize = normalizeDetailPageSize(params.returnDetailsPageSize);
  const returnListPage = normalizePositiveInteger(params.returnListPage);
  const returnListPageSize = normalizeListPageSize(params.returnListPageSize);
  const sortColId = normalizeSortColId(params.returnSortColId);
  const sortDirection = normalizeSortDirection(params.returnSortDirection);
  const inboundStatusRaw = firstParam(params.returnFilterInboundStatus);
  const inboundStatusNumber = inboundStatusRaw == null ? undefined : Number(inboundStatusRaw);
  const inboundStatus =
    inboundStatusNumber === 0 || inboundStatusNumber === 1 || inboundStatusNumber === 2
      ? inboundStatusNumber
      : undefined;

  if (
    source !== LOCAL_SUPPLIER_INVOICES_SOURCE
    || !returnInvoiceGuid
    || returnDetailsPage == null
    || returnDetailsPageSize == null
    || returnListPage == null
    || returnListPageSize == null
    || !sortColId
    || !sortDirection
  ) {
    return null;
  }

  return {
    source,
    returnInvoiceGuid,
    returnDetailGuid: firstParam(params.returnDetailGuid),
    returnDetailsPage,
    returnDetailsPageSize,
    returnDetailPriceChangeFilter: normalizeDetailPriceChangeFilter(
      params.returnDetailPriceChangeFilter
    ),
    returnDetailSearch: firstParam(params.returnDetailSearch) ?? "",
    returnListPage,
    returnListPageSize,
    filters: normalizeFilters({
      storeCode: firstParam(params.returnFilterStoreCode),
      supplierCode: firstParam(params.returnFilterSupplierCode),
      invoiceNo: firstParam(params.returnFilterInvoiceNo),
      inboundStatus,
      dateField: normalizeDateField(firstParam(params.returnFilterDateField)),
      dateFrom: firstParam(params.returnFilterDateFrom),
      dateTo: firstParam(params.returnFilterDateTo),
      productChecked: parseOptionalBoolean(params.returnFilterProductChecked),
      hasUncreatedProducts: parseOptionalBoolean(params.returnFilterHasUncreatedProducts),
      productKeyword: firstParam(params.returnFilterProductKeyword),
    }),
    sort: {
      colId: sortColId,
      direction: sortDirection,
    },
  };
}

export function buildLocalSupplierInvoicesRestoreHref(
  state: LocalSupplierInvoicesReturnState
): LocalSupplierInvoicesRestoreHref {
  return {
    pathname: "/(shell)/local-supplier-invoices",
    params: buildLocalSupplierInvoicesReturnParams(state),
  };
}
