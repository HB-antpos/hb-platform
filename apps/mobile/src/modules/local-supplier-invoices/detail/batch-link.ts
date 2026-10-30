import type { BarcodeMatchedProduct, LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import { getProductStatusFilter } from "./detail-status";

/**
 * 批量「选用条码匹配商品」（二期 B），口径搬自 Web InvoiceEdit/batchLinkMatchedProducts.ts：
 * 主档不存在、条码却只匹配到一个已有商品的行，把该商品编码回填，之后按已有商品处理。
 */

/** 按条码查询的并发上限，避免一次勾选上百行把接口打满。 */
export const BATCH_LINK_QUERY_CONCURRENCY = 4;

type LinkSource = Pick<
  LocalSupplierInvoiceItem,
  "detailGuid" | "barcode" | "barcodeMatchCount" | "isCreatedByThisInvoice" | "existingProductCount" | "barcodeStatus" | "activityType"
>;

/**
 * 初筛：主档不存在（与单行「选用」同口径）、有条码、检测时条码匹配到过商品。
 * barcodeMatchCount 是匹配记录条数（主条码与多码各算一次），只用来初筛；是否只匹配到一个商品以查询后去重的编码为准。
 */
export function pickBatchLinkCandidates<T extends LinkSource>(selectedGuids: readonly string[], details: readonly T[]) {
  const selected = new Set(selectedGuids);
  const candidates: T[] = [];
  let ineligibleCount = 0;
  for (const detail of details) {
    if (!selected.has(detail.detailGuid)) continue;
    if (getProductStatusFilter(detail) === "notExists" && detail.barcode?.trim() && (detail.barcodeMatchCount ?? 0) > 0) {
      candidates.push(detail);
    } else {
      ineligibleCount += 1;
    }
  }
  return { candidates, ineligibleCount };
}

/** 匹配到的商品按编码去重（不分大小写）后恰好一个时返回其编码；没有或多个返回 null，交给人工逐行处理。 */
export function resolveSingleMatchedProductCode(products: readonly Pick<BarcodeMatchedProduct, "productCode">[] | undefined): string | null {
  const codes = new Map<string, string>();
  for (const product of products ?? []) {
    const code = product.productCode?.trim();
    // 回填用第一次出现的写法（通常是主条码那条）。
    if (code && !codes.has(code.toLowerCase())) codes.set(code.toLowerCase(), code);
  }
  return codes.size === 1 ? [...codes.values()][0] : null;
}

/** 限制并发地逐个执行异步任务，结果顺序与输入一致；单个失败不影响其它任务。 */
export async function mapWithConcurrency<T, R>(
  items: readonly T[],
  limit: number,
  task: (item: T) => Promise<R>
): Promise<PromiseSettledResult<R>[]> {
  const results: PromiseSettledResult<R>[] = new Array(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const index = next;
      next += 1;
      try {
        results[index] = { status: "fulfilled", value: await task(items[index]) };
      } catch (reason) {
        results[index] = { status: "rejected", reason };
      }
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, Math.min(limit, items.length)) }, worker));
  return results;
}
