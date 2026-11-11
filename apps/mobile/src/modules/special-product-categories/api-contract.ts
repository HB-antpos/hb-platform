// 特殊商品分类接口（api/react/v1/store-special-product-categories）。按「传输层注入」写成工厂，
// 便于在 Node 下用假传输测试请求路径 / 参数；实际调用见 api.ts。
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import {
  chunk,
  MAX_BATCH_PRODUCT_CODES,
  normalizeAssignResult,
  normalizeCandidates,
  normalizeCategories,
  normalizeCategory,
  normalizeOverview,
  normalizeProductsPage,
  normalizeSyncPreview,
  normalizeSyncResults,
  normalizeSyncTargets,
} from "./logic";
import type {
  AssignResult,
  CandidateProduct,
  CategoryOverview,
  CategoryProductsPage,
  SpecialCategory,
  SyncPreviewTarget,
  SyncRequest,
  SyncResultTarget,
  SyncTargetsResponse,
} from "./types";

// apiClient 的 baseURL 已带 /api。读接口需 SpecialProducts.View，写接口需 SpecialProducts.Manage，仅登录账号可用（设备会话 403）。
export const SPECIAL_CATEGORIES_BASE_URL = "/react/v1/store-special-product-categories";

type Params = Record<string, string | number | undefined>;
interface RequestOptions {
  signal?: AbortSignal;
  params?: Params;
}

export interface SpecialCategoriesTransport {
  get(url: string, config?: RequestOptions): Promise<{ data: unknown }>;
  post(url: string, body?: unknown, config?: RequestOptions): Promise<{ data: unknown }>;
  put(url: string, body?: unknown, config?: RequestOptions): Promise<{ data: unknown }>;
  delete(url: string, config?: RequestOptions): Promise<{ data: unknown }>;
}

const enc = encodeURIComponent;

export function createSpecialCategoriesApi(transport: SpecialCategoriesTransport) {
  const base = SPECIAL_CATEGORIES_BASE_URL;
  // 后端校验失败返回 200 + success:false，unwrapApiEnvelope 会抛出带原因 / 错误码的错误；所有归一化函数内部都会先解包。
  return {
    async getOverview(storeCode: string, signal?: AbortSignal): Promise<CategoryOverview> {
      const response = await transport.get(base, { params: { storeCode }, signal });
      return normalizeOverview(response.data, storeCode);
    },

    async createCategory(input: { storeCode: string; name: string; color: string | null }): Promise<SpecialCategory> {
      const response = await transport.post(base, input);
      const created = normalizeCategory(unwrapApiEnvelope(response.data));
      if (!created) throw Object.assign(new Error("Category response invalid"), { code: "SPECIAL_CATEGORY_INVALID_RESPONSE" });
      return created;
    },

    async updateCategory(id: string, input: { storeCode: string; name: string; color: string | null }): Promise<SpecialCategory> {
      const response = await transport.put(`${base}/${enc(id)}`, input);
      const updated = normalizeCategory(unwrapApiEnvelope(response.data));
      if (!updated) throw Object.assign(new Error("Category response invalid"), { code: "SPECIAL_CATEGORY_INVALID_RESPONSE" });
      return updated;
    },

    /** 删分类 + 其归属行；不改特殊商品标记，商品回到未分类。 */
    async deleteCategory(id: string, storeCode: string): Promise<{ removedItemCount: number }> {
      const response = await transport.delete(`${base}/${enc(id)}`, { params: { storeCode } });
      const data = unwrapApiEnvelope<{ removedItemCount?: unknown }>(response.data);
      return { removedItemCount: Number(data?.removedItemCount) || 0 };
    },

    /** categoryIds 必须是全部分类 id 的一个排列，否则后端返回冲突错误。 */
    async reorderCategories(storeCode: string, categoryIds: string[]): Promise<SpecialCategory[]> {
      const response = await transport.put(`${base}/order`, { storeCode, categoryIds });
      const data = unwrapApiEnvelope<{ categories?: unknown }>(response.data);
      return normalizeCategories(data?.categories);
    },

    /** categoryId：分类 id、"uncategorized" 或省略（全部）。 */
    async getProducts(
      input: { storeCode: string; categoryId?: string; keyword?: string; page: number; pageSize: number },
      signal?: AbortSignal,
    ): Promise<CategoryProductsPage> {
      const response = await transport.get(`${base}/products`, {
        params: {
          storeCode: input.storeCode,
          categoryId: input.categoryId || undefined,
          keyword: input.keyword?.trim() || undefined,
          page: input.page,
          pageSize: input.pageSize,
        },
        signal,
      });
      return normalizeProductsPage(response.data);
    },

    /** 「添加商品」搜索：关键字至少 2 个字符，limit 默认 30、最大 50。 */
    async getCandidates(input: { storeCode: string; keyword: string; limit?: number }, signal?: AbortSignal): Promise<CandidateProduct[]> {
      const response = await transport.get(`${base}/candidates`, {
        params: { storeCode: input.storeCode, keyword: input.keyword.trim(), limit: input.limit ?? 30 },
        signal,
      });
      return normalizeCandidates(response.data);
    },

    /**
     * categoryId = null 表示移到「未分类」；markAsSpecial=true 时把尚不是特殊商品的先标记（添加商品用）。
     * 后端单次最多 500 个商品编码：超出时按顺序分批提交（保持传入顺序），汇总各批结果；某批失败则抛出，已提交的批次不回滚。
     */
    async assignProducts(input: {
      storeCode: string;
      productCodes: string[];
      categoryId: string | null;
      markAsSpecial: boolean;
    }): Promise<AssignResult> {
      const total: AssignResult = { assignedCount: 0, markedCount: 0, skipped: [] };
      for (const productCodes of chunk(input.productCodes, MAX_BATCH_PRODUCT_CODES)) {
        const response = await transport.post(`${base}/assign`, { ...input, productCodes });
        const part = normalizeAssignResult(response.data);
        total.assignedCount += part.assignedCount;
        total.markedCount += part.markedCount;
        total.skipped.push(...part.skipped);
      }
      return total;
    },

    /** 取消特殊商品标记（不删归属行，重新标记后回到原分类）。同样按 500 个一批分批提交。 */
    async unmarkProducts(storeCode: string, productCodes: string[]): Promise<{ unmarkedCount: number }> {
      let unmarkedCount = 0;
      for (const part of chunk(productCodes, MAX_BATCH_PRODUCT_CODES)) {
        const response = await transport.post(`${base}/unmark`, { storeCode, productCodes: part });
        const data = unwrapApiEnvelope<{ unmarkedCount?: unknown }>(response.data);
        unmarkedCount += Number(data?.unmarkedCount) || 0;
      }
      return { unmarkedCount };
    },

    /** 分类内商品顺序；productCodes 必须恰好是该分类当前全部特殊商品的一个排列。手机端暂无入口，留给后续使用。 */
    async reorderCategoryProducts(id: string, storeCode: string, productCodes: string[]): Promise<void> {
      const response = await transport.put(`${base}/${enc(id)}/products/order`, { storeCode, productCodes });
      unwrapApiEnvelope(response.data);
    },

    async getSyncTargets(sourceStoreCode: string, signal?: AbortSignal): Promise<SyncTargetsResponse> {
      const response = await transport.get(`${base}/sync/targets`, { params: { sourceStoreCode }, signal });
      return normalizeSyncTargets(response.data, sourceStoreCode);
    },

    async previewSync(request: SyncRequest): Promise<SyncPreviewTarget[]> {
      const response = await transport.post(`${base}/sync/preview`, request);
      return normalizeSyncPreview(response.data);
    },

    /** mode=replace 必须带 confirmReplace=true，否则后端拒绝。每家目标店一个事务，结果里逐店带 ok / error。 */
    async executeSync(request: SyncRequest & { confirmReplace: boolean }): Promise<SyncResultTarget[]> {
      const response = await transport.post(`${base}/sync/execute`, request);
      return normalizeSyncResults(response.data);
    },
  };
}
