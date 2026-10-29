/**
 * 促销页按本店查询商品名称与零售价：只复用商品查询页已有的店级接口，不新增后端接口。
 * - 商品编码 → 名称 / 本店零售价：getProductFastDetail（调价页也在用的轻量详情）
 * - 条码 → 商品：scanProductLabel（一次返回候选 + 当前门店价）
 * - 关键字搜索：lookupProducts
 */
import { getProductFastDetail, lookupProducts, scanProductLabel } from "@/modules/product-maintenance/api";
import type { ProductDetail, ProductLookupItem } from "@/modules/product-maintenance/types";

export interface PromotionProductInfo {
  productCode: string;
  productName: string | null;
  barcode: string | null;
  retailPrice: number | null;
}

function toInfo(detail: ProductDetail): PromotionProductInfo {
  return {
    productCode: detail.productCode,
    productName: detail.productName?.trim() || null,
    barcode: detail.barcode ?? null,
    retailPrice: detail.storePrice?.retailPrice ?? null,
  };
}

export async function fetchPromotionProductInfo(productCode: string, storeCode: string) {
  return toInfo(await getProductFastDetail(productCode, storeCode));
}

export type BarcodeResolveResult =
  | { kind: "single"; product: PromotionProductInfo }
  | { kind: "multiple"; candidates: ProductLookupItem[] }
  | { kind: "notFound" };

/**
 * 扫到的条码解析成商品：唯一命中且带详情时直接返回；多个候选交给页面让用户选。
 */
export async function resolveBarcodeToProduct(barcode: string, storeCode: string): Promise<BarcodeResolveResult> {
  const result = await scanProductLabel({ keyword: barcode, storeCode });
  if (result.detail?.productCode) {
    return { kind: "single", product: toInfo(result.detail) };
  }
  const candidates = result.candidates.filter((item) => item.productCode);
  if (candidates.length === 1) {
    return { kind: "single", product: await fetchPromotionProductInfo(candidates[0].productCode, storeCode) };
  }
  if (candidates.length > 1) {
    return { kind: "multiple", candidates };
  }
  return { kind: "notFound" };
}

export async function searchPromotionProducts(keyword: string, storeCode: string) {
  const items = await lookupProducts({ keyword: keyword.trim(), storeCode });
  return items.filter((item) => item.productCode);
}
