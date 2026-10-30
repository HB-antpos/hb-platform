import * as detail from "./index";
import { assertEqual } from "./test-helpers";

// 统一出口：UI 只从 index 引入，这里确认各子模块的关键导出都在，且 export * 没有产生同名冲突。
const expected = [
  "DetailAction",
  "getDetailProgressBucket",
  "getDetailProgressStats",
  "getExecutedPercent",
  "DETAIL_PROGRESS_BUCKET_COLORS",
  "applyDetailFilters",
  "getDetailFilterCounts",
  "getProductStatusFilter",
  "getPurchasePriceChange",
  "getPurchasePriceChangeRatio",
  "applyInlineEdit",
  "buildInvoiceDetailSaveItems",
  "buildPricingEditorChanges",
  "discountRateToDecimal",
  "formatDiscountRate",
  "getPricingSummary",
  "hasUnsavedDetailChanges",
  "buildHeaderSavePayload",
  "isHeaderDirty",
  "canLinkMatchedProduct",
  "isProductLinkConfirmed",
  "runCheckProducts",
  "CHECK_JOB_POLL_INTERVAL_MS",
] as const;

for (const name of expected) {
  assertEqual(typeof (detail as Record<string, unknown>)[name] !== "undefined", true, `index 应导出 ${name}`);
}
